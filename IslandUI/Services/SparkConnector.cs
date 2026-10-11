using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace IslandUI.Services;

/// <summary>user@host[:port] as typed in the wizard.</summary>
public sealed record SparkTarget(string Host, int Port, string User)
{
    public static SparkTarget Parse(string address, int port)
    {
        var text = (address ?? "").Trim();
        int at = text.LastIndexOf('@');
        if (at <= 0 || at == text.Length - 1)
            throw new SparkException("地址格式应为 用户名@主机，例如 user1@192.168.1.50。");
        var user = text[..at].Trim();
        var host = text[(at + 1)..].Trim();
        if (host.StartsWith('[') && host.Contains(']')) host = host[1..host.IndexOf(']')];
        if (user.Length > 64 || user.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or ';'))
            throw new SparkException("用户名包含无效字符。");
        if (host.Length is 0 or > 253 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\'' or '"'))
            throw new SparkException("主机地址无效。");
        if (port is < 1 or > 65535) throw new SparkException("SSH 端口应在 1–65535 之间。");
        return new SparkTarget(host, port, user);
    }

    public override string ToString() => $"{User}@{Host}:{Port}";
}

public sealed record SparkCommandResult(int ExitCode, string Output, string Error);

/// <summary>An authenticated SSH session to a Spark (used by the wizard, not the tunnel).</summary>
public sealed class SparkSession : IDisposable
{
    internal SparkSession(SshClient client, SparkTarget target, string fingerprint)
    {
        Client = client;
        Target = target;
        Fingerprint = fingerprint;
    }

    public SshClient Client { get; }
    public SparkTarget Target { get; }
    public string Fingerprint { get; }

    /// <summary>Runs a non-interactive sh command, streaming each stdout/stderr line to <paramref name="onLine"/>.</summary>
    public async Task<SparkCommandResult> RunAsync(string command, TimeSpan timeout,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var cmd = Client.CreateCommand(command);
        var output = new StringBuilder();
        var error = new StringBuilder();
        try
        {
            var exec = cmd.ExecuteAsync(cts.Token);
            var readOut = Task.Run(() => Pump(cmd.OutputStream, output, onLine), CancellationToken.None);
            var readErr = Task.Run(() => Pump(cmd.ExtendedOutputStream, error, onLine), CancellationToken.None);
            await exec.ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SparkException($"Spark 上的命令超时（{timeout.TotalSeconds:0} 秒）。");
        }
        catch (TimeoutException)
        {
            // Streams did not close after exit; keep what was read.
        }
        catch (SshConnectionException ex)
        {
            throw new SparkException("与 Spark 的 SSH 连接中断。", ex);
        }
        return new SparkCommandResult(cmd.ExitStatus ?? -1, output.ToString(), error.ToString());
    }

    private static void Pump(Stream stream, StringBuilder sink, Action<string>? onLine)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lock (sink)
            {
                // Cap what we keep; installer output can be long.
                if (sink.Length < SparkPairing.MaxPayloadChars * 2) sink.AppendLine(line);
            }
            onLine?.Invoke(line);
        }
    }

    public void Dispose()
    {
        try { if (Client.IsConnected) Client.Disconnect(); } catch { }
        Client.Dispose();
    }
}

/// <summary>
/// The SSH half of "连接 DGX Spark": connect (password or key, TOFU host key),
/// preflight, optional install, pairing, and installing the laptop's public key.
/// </summary>
public static class SparkConnector
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Connects with a password (first time) or the DPAPI key. The host key is pinned on
    /// the first successful login and must match afterwards.
    /// </summary>
    public static Task<SparkSession> ConnectAsync(SparkTarget target, string? password, string? keyPath,
        CancellationToken ct = default)
        => Task.Run(() => Connect(target, password, keyPath, ct), ct);

    private static SparkSession Connect(SparkTarget target, string? password, string? keyPath, CancellationToken ct)
    {
        var methods = new List<AuthenticationMethod>();
        PrivateKeyFile? keyFile = null;
        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            keyFile = LoadKey(keyPath);
            methods.Add(new PrivateKeyAuthenticationMethod(target.User, keyFile));
        }
        if (!string.IsNullOrEmpty(password))
        {
            methods.Add(new PasswordAuthenticationMethod(target.User, password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(target.User);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            methods.Add(kbd);
        }
        if (methods.Count == 0)
            throw new SparkException("首次连接需要输入密码。");

        var info = new ConnectionInfo(target.Host, target.Port, target.User, [.. methods])
        {
            Timeout = ConnectTimeout,
        };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
        var pinned = SparkHostKeys.Get(target.Host, target.Port);
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = pinned == null || pinned == seen;
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw Translate(ex, target, pinned, seen);
        }
        finally
        {
            keyFile?.Dispose();
        }

        if (pinned == null && seen != null) SparkHostKeys.Pin(target.Host, target.Port, seen);
        return new SparkSession(client, target, seen ?? pinned ?? "");
    }

    internal static PrivateKeyFile LoadKey(string keyPath)
    {
        var pem = SparkKeyStore.LoadPem(keyPath);
        try
        {
            using var ms = new MemoryStream(pem, writable: false);
            return new PrivateKeyFile(ms);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(pem);
        }
    }

    internal static SparkException Translate(Exception ex, SparkTarget target, string? pinned, string? seen)
    {
        if (ex is SparkException se) return se;
        if (pinned != null && seen != null && pinned != seen)
            return new SparkException(
                $"Spark 的主机指纹变了！已拒绝连接。\n之前：{pinned}\n现在：{seen}\n如果你重装过 Spark 的系统，请删除 spark-hosts.json 中的对应条目后再试；否则可能有人在冒充它。", ex);
        return ex switch
        {
            SshAuthenticationException => new SparkException("用户名或密码不对（或 Spark 上的 BotLan 密钥已失效）。", ex),
            SshOperationTimeoutException or TimeoutException => new SparkException($"连接 {target.Host}:{target.Port} 超时，请检查地址、端口和网络。", ex),
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }
                => new SparkException($"找不到主机 {target.Host}。", ex),
            SocketException { SocketErrorCode: SocketError.TimedOut }
                => new SparkException($"连接 {target.Host}:{target.Port} 超时，请检查地址、端口和网络。", ex),
            SocketException => new SparkException($"无法连接到 {target.Host}:{target.Port}（{((SocketException)ex).SocketErrorCode}）。Spark 开机了吗？SSH 端口对吗？", ex),
            SshConnectionException => new SparkException($"SSH 连接被 Spark 断开：{ex.Message}", ex),
            OperationCanceledException => new SparkException("已取消。", ex),
            _ => new SparkException($"SSH 连接失败：{ex.Message}", ex),
        };
    }

    // ---- remote steps -------------------------------------------------------------------

    private const string FindSetup =
        "S=''; for d in \"$HOME/spark-duo\" \"$HOME/BotLan-CLI\"; do " +
        "if [ -f \"$d/botlan_setup.py\" ]; then S=\"$d/botlan_setup.py\"; break; fi; done; ";

    /// <summary>
    /// botlan_setup.py status --json, or (before it is deployed) the master-key fallback:
    /// gateway script + ~/.spark-duo/botlan.key exist and the service / health endpoint answers.
    /// </summary>
    public static async Task<SparkStatus> PreflightAsync(SparkSession session, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var r = await session.RunAsync(FindSetup +
            "if [ -n \"$S\" ]; then echo \"SETUP=$S\"; python3 \"$S\" status --json; exit $?; fi; " +
            "echo FALLBACK; " +
            "if [ -f \"$HOME/spark-duo/botlan_gateway.py\" ] && [ -s \"$HOME/.spark-duo/botlan.key\" ]; then echo HAS_KEY=1; else echo HAS_KEY=0; fi; " +
            "echo ACTIVE=$(systemctl --user is-active botlan-gateway 2>/dev/null); " +
            "echo HEALTH=$(curl -s -o /dev/null -m 5 -w '%{http_code}' http://127.0.0.1:8091/health 2>/dev/null)",
            TimeSpan.FromSeconds(40), null, ct).ConfigureAwait(false);

        var lines = r.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.FirstOrDefault()?.StartsWith("SETUP=") == true)
        {
            log?.Invoke($"使用 {lines[0][6..]}");
            try
            {
                return SparkPairing.ParseStatus(r.Output);
            }
            catch (SparkException) when (r.ExitCode != 0)
            {
                throw new SparkException($"botlan_setup.py status 失败（退出码 {r.ExitCode}）：{Tail(r.Error)}");
            }
        }

        string Value(string k) => lines.FirstOrDefault(l => l.StartsWith(k + "="))?[(k.Length + 1)..] ?? "";
        bool hasKey = Value("HAS_KEY") == "1";
        var health = Value("HEALTH");
        bool gatewayOk = Value("ACTIVE") == "active" || health is "200" or "401";
        log?.Invoke($"未找到 botlan_setup.py，回退检查：主密钥={(hasKey ? "有" : "无")} 服务={Value("ACTIVE")} health={health}");
        return new SparkStatus(hasKey, hasKey && gatewayOk, hasKey && gatewayOk);
    }

    /// <summary>Clones BotLan-CLI and runs 08_botlan.sh --install, streaming its output.</summary>
    public static async Task InstallAsync(SparkSession session, Action<string> log, CancellationToken ct = default)
    {
        log("开始在 Spark 上安装 BotLan（构建/下载模型可能需要 15 分钟以上，请保持窗口打开）…");
        var r = await session.RunAsync(
            "set -e; cd \"$HOME\"; " +
            "if [ -d BotLan-CLI/.git ]; then echo '更新 ~/BotLan-CLI'; git -C BotLan-CLI pull --ff-only || true; " +
            "else git clone --depth 1 -b botlan-gateway https://github.com/Yunle-Lee/BotLan-CLI BotLan-CLI " +
            "|| { echo '分支 botlan-gateway 不可用，改用默认分支'; git clone --depth 1 https://github.com/Yunle-Lee/BotLan-CLI BotLan-CLI; }; fi; " +
            "cd BotLan-CLI && sh scripts/08_botlan.sh --install 2>&1",
            TimeSpan.FromMinutes(90), log, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new SparkException($"安装失败（退出码 {r.ExitCode}）。请查看日志；也可以在 Spark 上手动运行 cd ~/BotLan-CLI && sh scripts/08_botlan.sh --install。");
        log("安装完成。可以在 Spark 上运行 sh scripts/09_setup.sh 定义 Bot、目录范围和后端。");
    }

    /// <summary>botlan_setup.py pair --json, or a single master-key Bot when setup is not deployed.</summary>
    public static async Task<SparkPairingResult> PairAsync(SparkSession session, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var r = await session.RunAsync(FindSetup +
            "if [ -n \"$S\" ]; then python3 \"$S\" pair --json; exit $?; fi; " +
            "echo FALLBACK; hostname; head -c 512 \"$HOME/.spark-duo/botlan.key\"",
            TimeSpan.FromSeconds(60), null, ct).ConfigureAwait(false);

        var output = r.Output;
        if (output.StartsWith("FALLBACK"))
        {
            var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            log?.Invoke("botlan_setup.py 尚未部署，使用主密钥创建一个 Bot。");
            // Re-shape into the pairing contract so the same validation applies.
            output = JsonSerializer.Serialize(new
            {
                v = 1,
                spark = new { hostname = lines.ElementAtOrDefault(1) ?? "spark", gateway_port = 8091, gateway_ok = true },
                bots = Array.Empty<object>(),
                master = new { name = "DGX Spark", model = "jev-step", api_key = lines.ElementAtOrDefault(2) },
            });
        }
        else if (r.ExitCode != 0)
        {
            throw new SparkException($"配对失败（退出码 {r.ExitCode}）：{Tail(r.Error)}");
        }
        return SparkPairing.ParsePair(output);
    }

    /// <summary>Appends the public key line to ~/.ssh/authorized_keys (700/600, no duplicates).</summary>
    public static async Task InstallPublicKeyAsync(SparkSession session, string publicKeyLine, CancellationToken ct = default)
    {
        var parts = publicKeyLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "ssh-rsa" || !IsSafe(parts[1]) || !IsSafe(parts[2]))
            throw new ArgumentException("Unexpected public key format.", nameof(publicKeyLine));
        var r = await session.RunAsync(
            "umask 077; F=\"$HOME/.ssh/authorized_keys\"; mkdir -p \"$HOME/.ssh\" && chmod 700 \"$HOME/.ssh\" && touch \"$F\" && chmod 600 \"$F\" && " +
            $"if grep -qF '{parts[1]}' \"$F\"; then echo EXISTS; else " +
            "if [ -s \"$F\" ] && [ -n \"$(tail -c1 \"$F\")\" ]; then echo >> \"$F\"; fi; " +
            $"printf '%s\\n' '{publicKeyLine}' >> \"$F\" && echo ADDED; fi",
            TimeSpan.FromSeconds(20), null, ct).ConfigureAwait(false);
        if (r.ExitCode != 0 || !(r.Output.Contains("ADDED") || r.Output.Contains("EXISTS")))
            throw new SparkException($"无法把密钥写入 Spark 的 ~/.ssh/authorized_keys：{Tail(r.Error)}");
    }

    /// <summary>Removes authorized_keys lines whose comment is exactly <paramref name="comment"/>. Returns lines removed.</summary>
    public static async Task<int> RemovePublicKeyAsync(SparkSession session, string comment, CancellationToken ct = default)
    {
        if (!IsSafe(comment)) throw new ArgumentException("Unsafe comment.", nameof(comment));
        var r = await session.RunAsync(
            "F=\"$HOME/.ssh/authorized_keys\"; [ -f \"$F\" ] || { echo 0; exit 0; }; " +
            $"N=$(grep -c ' {comment}$' \"$F\" || true); " +
            $"if [ \"$N\" -gt 0 ]; then grep -v ' {comment}$' \"$F\" > \"$F.botlan.tmp\"; cat \"$F.botlan.tmp\" > \"$F\"; rm -f \"$F.botlan.tmp\"; fi; echo $N",
            TimeSpan.FromSeconds(20), null, ct).ConfigureAwait(false);
        return int.TryParse(r.Output.Trim().Split('\n').LastOrDefault(), out var n) ? n : 0;
    }

    private static bool IsSafe(string s)
        => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '@' or '.' or '_' or '-');

    private static string Tail(string text)
    {
        var t = text.Trim();
        return t.Length > 300 ? "…" + t[^300..] : (t.Length == 0 ? "无错误输出" : t);
    }
}
