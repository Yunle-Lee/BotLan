using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;

namespace IslandUI.Services;

public enum SparkTunnelState
{
    Connecting,
    Up,
    Reconnecting,
    Failed,
    Stopped,
}

/// <summary>
/// Keeps one local-only SSH forward per saved Spark:
/// 127.0.0.1:LocalPort → Spark 127.0.0.1:GatewayPort, key auth, reconnect with backoff.
/// Replaces Packaging/Connect-Spark.ps1 inside the app.
/// </summary>
public sealed class SparkTunnelService
{
    public static SparkTunnelService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, Tunnel> _tunnels = new();

    /// <summary>Raised from a worker thread: (connection id, state, detail).</summary>
    public event Action<string, SparkTunnelState, string>? StateChanged;

    /// <summary>Local port changed (connection id, new port). Bot BaseUrls are already updated.</summary>
    public event Action<string, int>? LocalPortChanged;

    /// <summary>Starts every saved connection. Safe to call more than once.</summary>
    public void StartAll()
    {
        foreach (var c in SparkConnectionStore.Load()) Start(c);
    }

    /// <summary>Starts (or restarts) the tunnel for a connection.</summary>
    public void Start(SparkConnection connection)
    {
        if (_tunnels.TryRemove(connection.Id, out var old)) old.Stop();
        var t = new Tunnel(this, connection);
        _tunnels[connection.Id] = t;
        t.Start();
    }

    public SparkTunnelState GetState(string id) => _tunnels.TryGetValue(id, out var t) ? t.State : SparkTunnelState.Stopped;

    public int? GetLocalPort(string id) => _tunnels.TryGetValue(id, out var t) && t.State == SparkTunnelState.Up ? t.LocalPort : null;

    /// <summary>Waits until the tunnel is up (true) or fails/times out (false).</summary>
    public async Task<bool> WaitUpAsync(string id, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var s = GetState(id);
            if (s == SparkTunnelState.Up) return true;
            if (s is SparkTunnelState.Failed or SparkTunnelState.Stopped) return false;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }

    public void Stop(string id)
    {
        if (_tunnels.TryRemove(id, out var t)) t.Stop();
    }

    /// <summary>Stops every tunnel; bounded so app exit never hangs on the network.</summary>
    public void StopAll()
    {
        var all = _tunnels.Values.ToArray();
        _tunnels.Clear();
        Task.WaitAll(all.Select(t => Task.Run(t.Stop)).ToArray(), TimeSpan.FromSeconds(3));
    }

    /// <summary>Diagnostics/tests: the live SSH client behind a tunnel.</summary>
    internal SshClient? ActiveClient(string id) => _tunnels.TryGetValue(id, out var t) ? t.Client : null;

    public static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Preferred port if free, else the next free one above it.</summary>
    public static int PickLocalPort(int preferred)
    {
        for (int p = preferred; p < Math.Min(preferred + 200, 65535); p++)
            if (IsPortFree(p)) return p;
        throw new SparkException("本机找不到可用的端口来建立隧道。");
    }

    private void Raise(string id, SparkTunnelState s, string detail) => StateChanged?.Invoke(id, s, detail);

    private void OnPortChanged(SparkConnection c, int oldPort, int newPort)
    {
        SparkConnectionStore.UpdateLocalPort(c.Id, newPort);
        // AgentStore is UI state; change it on the UI thread.
        var url = $"http://127.0.0.1:{newPort}/v1";
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(() => AgentStore.Instance.RetargetSparkBots(c.Id, url));
        else AgentStore.Instance.RetargetSparkBots(c.Id, url);
        if (oldPort != newPort) LocalPortChanged?.Invoke(c.Id, newPort);
    }

    /// <summary>The local port the Bots of a connection currently point at, if any.</summary>
    internal int? BotPort(string connectionId)
    {
        int? found = null;
        void Read()
        {
            var url = AgentStore.Instance.Bots.Select(b => b.Config)
                .FirstOrDefault(c => c.SparkConnectionId == connectionId)?.BaseUrl;
            if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback) found = u.Port;
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(Read); else Read();
        return found;
    }

    private sealed class Tunnel(SparkTunnelService owner, SparkConnection connection)
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private readonly object _gate = new();

        public SparkTunnelState State { get; private set; } = SparkTunnelState.Connecting;
        public int LocalPort { get; private set; } = connection.LocalPort;
        public SshClient? Client { get; private set; }

        public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

        public void Stop()
        {
            _cts.Cancel();
            TearDown();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            Set(SparkTunnelState.Stopped, "已停止");
        }

        private void Set(SparkTunnelState s, string detail)
        {
            State = s;
            owner.Raise(connection.Id, s, detail);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var backoff = TimeSpan.FromSeconds(2);
            bool first = true;
            while (!ct.IsCancellationRequested)
            {
                Set(first ? SparkTunnelState.Connecting : SparkTunnelState.Reconnecting,
                    first ? "正在连接" : "正在重新连接");
                first = false;
                var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    Open(dropped);
                    Set(SparkTunnelState.Up, $"127.0.0.1:{LocalPort} → {connection.Host} 127.0.0.1:{connection.GatewayPort}");
                    backoff = TimeSpan.FromSeconds(2);

                    // Watch the session: ErrorOccurred (keep-alive failure) or IsConnected going false.
                    while (!ct.IsCancellationRequested && !dropped.Task.IsCompleted)
                    {
                        await Task.WhenAny(dropped.Task, Task.Delay(1000, ct)).ConfigureAwait(false);
                        if (Client is not { IsConnected: true }) break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    var target = new SparkTarget(connection.Host, connection.Port, connection.User);
                    var err = SparkConnector.Translate(ex, target, connection.Fingerprint, null);
                    Set(SparkTunnelState.Reconnecting, $"{err.Message}（{backoff.TotalSeconds:0} 秒后重试）");
                    // A changed host key never heals by retrying.
                    if (err.Message.Contains("指纹"))
                    {
                        TearDown();
                        Set(SparkTunnelState.Failed, err.Message);
                        return;
                    }
                }

                TearDown();
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }

        private void Open(TaskCompletionSource dropped)
        {
            var key = SparkConnector.LoadKey(connection.KeyPath);
            SshClient client;
            try
            {
                var info = new ConnectionInfo(connection.Host, connection.Port, connection.User,
                    new PrivateKeyAuthenticationMethod(connection.User, key))
                {
                    Timeout = SparkConnector.ConnectTimeout,
                };
                client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
                string? seen = null;
                client.HostKeyReceived += (_, e) =>
                {
                    seen = "SHA256:" + e.FingerPrintSHA256;
                    e.CanTrust = string.IsNullOrEmpty(connection.Fingerprint) || connection.Fingerprint == seen;
                };
                client.ErrorOccurred += (_, _) => dropped.TrySetResult();
                try
                {
                    client.Connect();
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    if (seen != null && !string.IsNullOrEmpty(connection.Fingerprint) && seen != connection.Fingerprint)
                        throw SparkConnector.Translate(ex, new SparkTarget(connection.Host, connection.Port, connection.User),
                            connection.Fingerprint, seen);
                    throw;
                }
            }
            finally
            {
                key.Dispose();
            }

            lock (_gate) Client = client;

            // Keep the same port across reconnects; pick another only when it is taken. Try the
            // port the Bots already use first, so a restart never leaves them on a dead port.
            int port = LocalPort;
            int botPort = owner.BotPort(connection.Id) ?? port;
            if (botPort != port && IsPortFree(botPort)) port = botPort;
            else if (!IsPortFree(port)) port = PickLocalPort(port + 1);
            var forward = new ForwardedPortLocal("127.0.0.1", (uint)port, "127.0.0.1", (uint)connection.GatewayPort);
            forward.Exception += (_, _) => { };
            client.AddForwardedPort(forward);
            forward.Start();
            int previous = LocalPort;
            LocalPort = port;
            // Also when the port is unchanged: Bots saved with another port must follow the tunnel.
            owner.OnPortChanged(connection, previous, port);
        }

        private void TearDown()
        {
            SshClient? c;
            lock (_gate)
            {
                c = Client;
                Client = null;
            }
            if (c == null) return;
            try
            {
                foreach (var p in c.ForwardedPorts.ToArray())
                {
                    try { p.Stop(); } catch { }
                }
                if (c.IsConnected) c.Disconnect();
            }
            catch
            {
            }
            finally
            {
                c.Dispose();
            }
        }
    }
}
