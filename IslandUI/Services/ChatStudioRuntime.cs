using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace IslandUI.Services;

/// <summary>One owned, loopback-only AI Studio backend, shared by all Bot sessions.</summary>
internal sealed class ChatStudioRuntime
{
    public static ChatStudioRuntime Shared { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private Process? _process;
    private string? _address;
    private string? _hostKey;
    private readonly Dictionary<string, (string Fingerprint, StudioSession Session)> _sessions = [];
    private CancellationTokenSource? _idleStop;
    private readonly object _idleSync = new();
    private Task<CoreWebView2Environment>? _environment;
    private bool _exiting;

    private ChatStudioRuntime()
    {
        if (Application.Current != null)
            Application.Current.Exit += (_, _) => { _exiting = true; _ = StopAsync(); };
    }

    public Task<CoreWebView2Environment> BrowserEnvironmentAsync()
    {
        if (_environment is null || _environment.IsFaulted || _environment.IsCanceled)
            _environment = CoreWebView2Environment.CreateAsync(null, Path.Combine(DataRoot, "webview"));
        return _environment;
    }

    // One bounded warm backend, never a hidden web page or an instance per Bot.
    // Close still disposes WebView/animations immediately; reopening cancels this idle release.
    public void ReleaseWhenIdle()
    {
        CancelIdleStop();
        if (_exiting) return;
        var idle = new CancellationTokenSource();
        lock (_idleSync) _idleStop = idle;
        _ = StopAfterIdleAsync(idle);
    }

    private void CancelIdleStop()
    {
        lock (_idleSync)
        {
            _idleStop?.Cancel();
            _idleStop = null;
        }
    }

    private async Task StopAfterIdleAsync(CancellationTokenSource idle)
    {
        var token = idle.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!token.IsCancellationRequested) await StopOwnedProcessAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_idleSync)
            {
                if (ReferenceEquals(_idleStop, idle)) _idleStop = null;
                idle.Dispose();
            }
        }
    }

    public string ChatRoot { get; } = LocateChatRoot();
    public string WebRoot => Path.Combine(ChatRoot, "dist", "island");
    public string DataRoot
    {
        get
        {
#if DEBUG
            var test = Environment.GetEnvironmentVariable("ISLANDUI_TEST_DATA_DIR");
            if (test != null)
            {
                var full = Path.GetFullPath(test);
                var workspace = Path.GetDirectoryName(ChatRoot)! + Path.DirectorySeparatorChar;
                if (!full.StartsWith(workspace, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Test data must remain inside the working copy.");
                return full;
            }
#endif
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "installed.marker")))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandUI", "ChatData");
            return Path.Combine(ChatRoot, ".island-data");
        }
    }

    public async Task<StudioSession> SessionAsync(AgentBot selected, CancellationToken cancellation)
    {
        CancelIdleStop();
        // Snapshot on the UI thread; no user configuration is rewritten.
        var configs = AgentStore.Instance.Bots.Select(bot => new
        {
            id = bot.Config.Id, name = bot.Config.Name, model = bot.Config.Model,
            baseUrl = bot.Config.BaseUrl, apiKey = bot.Config.ApiKey,
        }).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configs)));
        await _gate.WaitAsync(cancellation);
        try
        {
            await EnsureStartedAsync(cancellation);
            if (_sessions.TryGetValue(selected.Config.Id, out var cached) && cached.Fingerprint == fingerprint)
                return cached.Session with { Name = selected.Config.Name, Color = selected.Config.ColorHex };
            using var request = new HttpRequestMessage(HttpMethod.Post, _address + "/island/session");
            request.Headers.Add("X-Island-Host-Key", _hostKey);
            request.Content = JsonContent.Create(new { selectedId = selected.Config.Id, bots = configs });
            using var response = await _http.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("AI Studio 无法读取当前 Bot 配置，请检查模型名称和 API 地址。");
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var session = new StudioSession(selected.Config.Id, selected.Config.Name, selected.Config.ColorHex,
                payload.RootElement.GetProperty("token").GetString()!, _address!);
            _sessions[selected.Config.Id] = (fingerprint, session);
            return session;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellation)
    {
        if (_process is { HasExited: false } && _address != null) return;
        if (!File.Exists(Path.Combine(WebRoot, "index.html")))
            throw new FileNotFoundException("找不到 Chat/dist/island/index.html，请先构建嵌入版页面。");
        var backend = Path.Combine(ChatRoot, "dist", "island-server", "island-server.mjs");
        if (!File.Exists(backend))
            backend = Path.Combine(ChatRoot, "dist", "island-server", "apps", "server", "src", "island-entry.js");
        if (!File.Exists(backend)) throw new FileNotFoundException("找不到嵌入版 Chat 后台，请先运行 Chat 的 build:island。");
        _sessions.Clear();
        _process?.Dispose();
        _address = null;
        _hostKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(FindNode())
        {
            WorkingDirectory = ChatRoot,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        start.ArgumentList.Add(backend);
        start.Environment["ISLAND_HOST_KEY"] = _hostKey;
        start.Environment["ISLAND_PARENT_PID"] = Environment.ProcessId.ToString();
        start.Environment["ISLAND_DATA_DIR"] = DataRoot;
        start.Environment["NODE_ENV"] = "production";
        start.Environment["DO_NOT_TRACK"] = "1";
        start.Environment["COPILOTKIT_TELEMETRY_DISABLED"] = "true";
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data?.StartsWith("ISLAND_READY ", StringComparison.Ordinal) == true
                && Uri.TryCreate(e.Data[13..], UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == "http" && endpoint.Host == "127.0.0.1")
                ready.TrySetResult(endpoint.GetLeftPart(UriPartial.Authority));
        };
        // Drain output without leaking credentials, document contents or tokens into logs.
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) => ready.TrySetException(
            new InvalidOperationException("AI Studio 后台启动失败，请检查 Chat 的依赖和配置。"));
        _process = process;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { _address = await ready.Task.WaitAsync(TimeSpan.FromSeconds(75), cancellation); }
        catch
        {
            await StopOwnedProcessAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        CancelIdleStop();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopOwnedProcessAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopOwnedProcessAsync()
    {
        var process = _process;
        _process = null;
        _address = null;
        _hostKey = null;
        _sessions.Clear();
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("shutdown").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            // Only this exact Process object was launched by us. Never kill by name/port.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        finally { process.Dispose(); }
    }

    private static string LocateChatRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "Chat");
            if (File.Exists(Path.Combine(candidate, "dist", "island-server", "island-server.mjs"))
                || File.Exists(Path.Combine(candidate, "apps", "server", "src", "island-entry.ts"))) return candidate;
        }
        throw new DirectoryNotFoundException("请把 Chat 文件夹保留在当前 IslandUI 项目旁边。");
    }

    private static string FindNode()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "runtime", "node.exe");
        if (File.Exists(packaged)) return packaged;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), "node.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("运行 Chat 需要 Node.js 22 或更新版本。");
    }
}

internal sealed record StudioSession(string BotId, string Name, string Color, string Token, string ApiUrl);
