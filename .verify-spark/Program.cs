using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IslandUI;
using IslandUI.Services;
using Renci.SshNet;

// Verifies the "连接 DGX Spark" services against a real Spark and renders the UI.
//   SparkVerify --unit                      pairing parser checks only
//   SparkVerify --render <outdir>           wizard + empty Long Island CTA PNGs
//   SparkVerify --live user@host port       full live run (password from env SPARK_PW)
// Always uses ISLANDUI_SPARK_DATA_DIR (set to a temp dir if missing); never saves AgentStore.
internal static class Program
{
    private const string TestComment = "botlan-verify";
    private const int TestLocalPort = 18091;
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ISLANDUI_SPARK_DATA_DIR")))
            Environment.SetEnvironmentVariable("ISLANDUI_SPARK_DATA_DIR",
                Path.Combine(Path.GetTempPath(), "botlan-spark-verify-" + Guid.NewGuid().ToString("N")[..6]));
        Console.WriteLine($"data dir: {SparkPaths.DataDir}");

        if (args.Contains("--unit") || args.Length == 0) UnitChecks();
        if (args.Contains("--render")) Render(args.SkipWhile(a => a != "--render").Skip(1).FirstOrDefault() ?? ".verify-spark/renders");
        int live = Array.IndexOf(args, "--live");
        if (live >= 0) LiveAsync(args[live + 1], int.Parse(args[live + 2])).GetAwaiter().GetResult();
        if (args.Contains("--cleanup-only"))
        {
            int i = Array.IndexOf(args, "--cleanup-only");
            CleanupAsync(SparkTarget.Parse(args[i + 1], int.Parse(args[i + 2]))).GetAwaiter().GetResult();
        }

        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}");
    }

    private static void Throws(string? input, string label)
    {
        try
        {
            SparkPairing.ParsePair(input);
            Check(false, label + " (no exception)");
        }
        catch (SparkException ex)
        {
            Check(true, $"{label} → {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(false, $"{label} (wrong exception {ex.GetType().Name})");
        }
    }

    // ---- unit ---------------------------------------------------------------------------

    private static void UnitChecks()
    {
        const string good = """
            {"v":1,
             "spark":{"hostname":"spark-8691","gateway_port":8091,"gateway_ok":true,"mem_total_gb":121.6,"gpu":"NVIDIA GB10"},
             "bots":[{"id":"z1","name":"Vision","color":"#54A8FF","model":"jev-step","api_key":"blz_abcdef123456","backend":"jev-step","scope":["/home/user1/work"]},
                     {"id":"z2","name":"NoKey","color":"#FF0000","model":"jev-step","api_key":null}],
             "master":{"name":"DGX Spark","model":"jev-step","api_key":"bl_master123456"}}
            """;
        var r = SparkPairing.ParsePair(good);
        Check(r.Spark.Hostname == "spark-8691" && r.Spark.GatewayPort == 8091 && r.Spark.Gpu == "NVIDIA GB10", "good: spark info");
        Check(r.Bots.Count == 1 && r.Bots[0].Name == "Vision" && r.Bots[0].ApiKey == "blz_abcdef123456", "good: null api_key skipped, master unused");

        var m = SparkPairing.ParsePair("""{"v":1,"spark":{"hostname":"s","gateway_port":8091},"bots":[],"master":{"name":"DGX Spark","model":"jev-step","api_key":"bl_master123456"}}""");
        Check(m.Bots.Count == 1 && m.Bots[0].Name == "DGX Spark" && m.Bots[0].ApiKey == "bl_master123456", "empty bots → one Bot from master");

        var banner = SparkPairing.ParsePair("Welcome to Ubuntu\nlast login...\n" + good.Replace("\n", " "));
        Check(banner.Bots.Count == 1, "banner noise before JSON tolerated");

        var hostile = SparkPairing.ParsePair(
            """{"spark":{"hostname":"x; rm -rf /","gateway_port":8091},"bots":[{"name":"  """ + new string('A', 300) +
            """  ","color":"red","model":"$(reboot)","api_key":"blz_validkey123"},{"name":"\u0007Bell\u0000","color":"#12abEF","api_key":"has space key"}]}""");
        Check(hostile.Spark.Hostname == "spark", "hostile hostname replaced");
        Check(hostile.Bots.Count == 1, "api_key with whitespace rejected");
        Check(hostile.Bots[0].Name.Length <= 40 && hostile.Bots[0].ColorHex == SparkPairing.DefaultColor && hostile.Bots[0].Model == "jev-step",
            $"name trimmed to {hostile.Bots[0].Name.Length}, bad color/model defaulted");

        var ctl = SparkPairing.ParsePair("""{"spark":{"hostname":"s"},"bots":[{"name":"\u0007Be\u0000ll ","color":"#12abEF","api_key":"blz_validkey123"}]}""");
        Check(ctl.Bots[0].Name == "Bell" && ctl.Bots[0].ColorHex == "#12ABEF", "control chars stripped, color normalized");

        Throws(null, "null input");
        Throws("", "empty input");
        Throws("not json at all", "garbage");
        Throws("[1,2,3]", "array root");
        Throws("""{"v":1}""", "missing spark");
        Throws("""{"spark":"x"}""", "spark not an object");
        Throws("""{"spark":{"gateway_port":70000},"bots":[]}""", "port out of range");
        Throws("""{"spark":{},"bots":[{"name":"a","api_key":null}]}""", "only null keys, no master");
        Throws("""{"v":9,"spark":{}}""", "newer version");
        Throws("{\"spark\":{},\"x\":\"" + new string('x', SparkPairing.MaxPayloadChars) + "\"}", "oversized payload");
        Throws(new string('[', 5000), "deep nesting");
        Throws("""{"spark":{},"bots":"nope","master":5}""", "wrong field types");

        var many = "{\"spark\":{},\"bots\":[" + string.Join(",", Enumerable.Range(0, 100)
            .Select(i => $"{{\"name\":\"b{i}\",\"api_key\":\"blz_key{i:D8}\"}}")) + "]}";
        Check(SparkPairing.ParsePair(many).Bots.Count == SparkPairing.MaxBots, $"bot count capped at {SparkPairing.MaxBots}");

        var st = SparkPairing.ParseStatus("""{"installed":true,"gateway_ok":true,"models_ok":false}""");
        Check(st is { Installed: true, GatewayOk: true, ModelsOk: false }, "status parse");

        // Duplicate planning: same base URL + key is skipped.
        var conn = new SparkConnection { Id = "c1", LocalPort = 18091 };
        var (fresh, dupes) = SparkConnectFlow.PlanBots(r, conn,
            [new AgentConfig { BaseUrl = "http://127.0.0.1:18091/v1/", ApiKey = "blz_abcdef123456" }]);
        Check(fresh.Count == 0 && dupes.Count == 1, "duplicate (same base URL + key) skipped");
        (fresh, _) = SparkConnectFlow.PlanBots(r, conn, []);
        Check(fresh.Count == 1 && fresh[0].BaseUrl == "http://127.0.0.1:18091/v1" && fresh[0].SparkConnectionId == "c1", "new Bot gets tunnel URL + connection id");

        foreach (var (addr, ok) in new[] { ("user1@spark.example.lan", true), ("spark.example.lan", false), ("@host", false), ("u@", false), ("a b@host", false), ("u@[::1]", true) })
        {
            bool parsed;
            try { SparkTarget.Parse(addr, 22); parsed = true; } catch (SparkException) { parsed = false; }
            Check(parsed == ok, $"address '{addr}' parse={parsed}");
        }

        // Key store round trip (temp data dir).
        var keyPath = Path.Combine(SparkPaths.DataDir, "unit-key.dpapi");
        var pub = SparkKeyStore.EnsureKey(keyPath, TestComment);
        var raw = File.ReadAllBytes(keyPath);
        Check(pub.StartsWith("ssh-rsa AAAAB3NzaC1yc2E") && pub.EndsWith(" " + TestComment), "public key line format");
        Check(!System.Text.Encoding.ASCII.GetString(raw).Contains("PRIVATE KEY"), "private key not plaintext on disk");
        using (var k = new PrivateKeyFile(new MemoryStream(SparkKeyStore.LoadPem(keyPath))))
            Check(k.Key != null, "DPAPI key loads into SSH.NET");
        Check(SparkKeyStore.EnsureKey(keyPath, TestComment) == pub, "EnsureKey is stable");
        File.Delete(keyPath);
    }

    // ---- live ---------------------------------------------------------------------------

    private static async Task LiveAsync(string address, int port)
    {
        var password = Environment.GetEnvironmentVariable("SPARK_PW");
        if (string.IsNullOrEmpty(password)) { Check(false, "SPARK_PW not set"); return; }
        var target = SparkTarget.Parse(address, port);
        var keyPath = Path.Combine(SparkPaths.DataDir, "spark-key.dpapi");
        var options = new SparkFlowOptions { PreferredLocalPort = TestLocalPort, KeyComment = TestComment, KeyPath = keyPath };

        try
        {
            // Wrong password → clear error, nothing pinned yet is fine.
            try
            {
                using var _ = await SparkConnector.ConnectAsync(target, "definitely-wrong-pw", null);
                Check(false, "wrong password rejected");
            }
            catch (SparkException ex) { Check(ex.Message.Contains("密码"), $"wrong password → {ex.Message}"); }

            // Unreachable port → clear error.
            try
            {
                using var _ = await SparkConnector.ConnectAsync(new SparkTarget("127.0.0.1", 1, "x"), "x", null);
                Check(false, "unreachable rejected");
            }
            catch (SparkException ex) { Check(ex.Message.Contains("无法连接") || ex.Message.Contains("超时"), $"unreachable → {ex.Message}"); }

            // 1. First connect: password, TOFU pin, preflight, pair, key install, tunnel.
            Check(SparkHostKeys.Get(target.Host, target.Port) == null, "no pinned host key before first connect");
            var flow = new SparkConnectFlow(options);
            flow.StepChanged += (s, st, d) => Console.WriteLine($"  [{s}] {st} {d}");
            flow.Log += l => Console.WriteLine($"  log: {l}");
            var r1 = await flow.RunAsync(target, password, () => []);
            var pinned = SparkHostKeys.Get(target.Host, target.Port);
            Check(pinned != null && pinned.StartsWith("SHA256:"), $"TOFU pinned {pinned}");
            Check(!r1.UsedKeyAuth, "first connect used password");
            Check(r1.NewBots.Count >= 1, $"pairing produced {r1.NewBots.Count} Bot(s): {string.Join(", ", r1.NewBots.Select(b => $"{b.Name}/{b.Model}/{b.ColorHex}"))}");
            Check(r1.Connection.LocalPort == TestLocalPort, $"tunnel on local port {r1.Connection.LocalPort}");
            Check(r1.NewBots.All(b => b.BaseUrl == $"http://127.0.0.1:{TestLocalPort}/v1"), "Bot BaseUrl uses tunnel port");

            var saved = File.ReadAllText(SparkPaths.ConnectionsFile);
            Check(!saved.Contains(password) && !r1.NewBots.Any(b => saved.Contains(b.ApiKey)), "sparks.json holds no password/API key");
            Console.WriteLine("  sparks.json: " + saved.Replace("\n", " ").Replace("  ", ""));

            var key = r1.NewBots[0].ApiKey;
            var noKey = await SparkConnectFlow.HealthAsync(TestLocalPort, null);
            var withKey = await SparkConnectFlow.HealthAsync(TestLocalPort, key);
            Check(noKey == HttpStatusCode.Unauthorized, $"GET /health without key → {(int)noKey}");
            Check(withKey == HttpStatusCode.OK, $"GET /health with key → {(int)withKey}");

            // 2. Drop the session; the tunnel must reconnect on its own.
            var id = r1.Connection.Id;
            var states = new List<SparkTunnelState>();
            SparkTunnelService.Instance.StateChanged += (cid, s, d) => { if (cid == id) lock (states) states.Add(s); Console.WriteLine($"  tunnel {s}: {d}"); };
            var active = (SshClient?)typeof(SparkTunnelService).GetMethod("ActiveClient", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(SparkTunnelService.Instance, [id]);
            Check(active is { IsConnected: true }, "tunnel session live");
            // Close the transport underneath SSH.NET (as a network drop would), not a polite disconnect.
            var socketField = typeof(BaseClient).GetField("<Session>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            var session = socketField?.GetValue(active!);
            var sock = session?.GetType().GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(session) as System.Net.Sockets.Socket;
            if (sock != null) { sock.Close(); Console.WriteLine("  closed SSH socket underneath the tunnel"); }
            else { active.Disconnect(); Console.WriteLine("  (socket not reachable by reflection; disconnected client)"); }
            var t0 = DateTime.UtcNow;
            bool sawDown = false;
            while (DateTime.UtcNow - t0 < TimeSpan.FromSeconds(20))
            {
                lock (states) sawDown = states.Contains(SparkTunnelState.Reconnecting);
                if (sawDown) break;
                await Task.Delay(200);
            }
            Check(sawDown, "tunnel noticed the drop");
            var up = await SparkTunnelService.Instance.WaitUpAsync(id, TimeSpan.FromSeconds(40));
            var after = await SparkConnectFlow.HealthAsync(TestLocalPort, key);
            Check(up && after == HttpStatusCode.OK, $"reconnected in {(DateTime.UtcNow - t0).TotalSeconds:0.0}s, /health → {(int)after}");

            // 3. Second connect: key only (no password), duplicates skipped.
            var flow2 = new SparkConnectFlow(options);
            flow2.StepChanged += (s, st, d) => Console.WriteLine($"  [2:{s}] {st} {d}");
            var r2 = await flow2.RunAsync(target, null, () => r1.NewBots);
            Check(r2.UsedKeyAuth, "second connect authenticated with the DPAPI key only");
            Check(r2.NewBots.Count == 0 && r2.SkippedDuplicates.Count == r1.NewBots.Count, $"second pairing skipped {r2.SkippedDuplicates.Count} duplicate(s)");
            Check(r2.Connection.Id == id && r2.Connection.LocalPort == TestLocalPort, "same saved connection and port reused");

            // authorized_keys has exactly one test key line.
            using (var s = await SparkConnector.ConnectAsync(target, null, keyPath))
            {
                var c = await s.RunAsync($"grep -c ' {TestComment}$' ~/.ssh/authorized_keys; stat -c '%a' ~/.ssh ~/.ssh/authorized_keys", TimeSpan.FromSeconds(10));
                var lines = c.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Check(lines.FirstOrDefault() == "1", $"authorized_keys has {lines.FirstOrDefault()} '{TestComment}' line (no duplicates)");
                Check(lines.ElementAtOrDefault(1) == "700" && lines.ElementAtOrDefault(2) == "600", $"perms ~/.ssh={lines.ElementAtOrDefault(1)} authorized_keys={lines.ElementAtOrDefault(2)}");
            }

            // 4. Changed fingerprint is refused.
            SparkHostKeys.Pin(target.Host, target.Port, "SHA256:not-the-real-key");
            try
            {
                using var _ = await SparkConnector.ConnectAsync(target, null, keyPath);
                Check(false, "fingerprint change refused");
            }
            catch (SparkException ex) { Check(ex.Message.Contains("指纹"), "fingerprint change → " + ex.Message.Split('\n')[0]); }
            SparkHostKeys.Pin(target.Host, target.Port, pinned!);

            // 5. Clean shutdown.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SparkTunnelService.Instance.StopAll();
            Check(sw.Elapsed < TimeSpan.FromSeconds(4), $"StopAll took {sw.ElapsedMilliseconds} ms");
            await Task.Delay(300);
            Check(SparkTunnelService.IsPortFree(TestLocalPort), $"local port {TestLocalPort} released");
            Check(await SparkConnectFlow.HealthAsync(TestLocalPort, key) == 0, "nothing answers after shutdown");
        }
        catch (Exception ex)
        {
            Check(false, $"live run threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SparkTunnelService.Instance.StopAll();
            await CleanupAsync(target, keyPath, password);
        }
    }

    private static async Task CleanupAsync(SparkTarget target, string? keyPath = null, string? password = null)
    {
        password ??= Environment.GetEnvironmentVariable("SPARK_PW");
        try
        {
            // Password login so cleanup works even if the key step failed.
            using var s = await SparkConnector.ConnectAsync(target, password, null);
            int removed = await SparkConnector.RemovePublicKeyAsync(s, TestComment);
            var left = await s.RunAsync($"grep -c ' {TestComment}$' ~/.ssh/authorized_keys || true", TimeSpan.FromSeconds(10));
            Check(left.Output.Trim() == "0", $"cleanup: removed {removed} '{TestComment}' key line(s) from Spark, {left.Output.Trim()} left");
        }
        catch (Exception ex)
        {
            Check(false, "cleanup failed: " + ex.Message);
        }
        if (keyPath != null && File.Exists(keyPath)) File.Delete(keyPath);
    }

    // ---- render -------------------------------------------------------------------------

    private static void Render(string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Wizard, idle and mid-flow.
        var w = new SparkConnectWindow();
        Save(w, Path.Combine(outDir, "wizard-idle.png"));
        var set = typeof(SparkConnectWindow).GetMethod("SetStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var log = typeof(SparkConnectWindow).GetMethod("AppendLog", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var msg = typeof(SparkConnectWindow).GetMethod("ShowMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((TextBox)w.FindName("AddressBox")).Text = "user1@spark.example.lan";
        ((TextBox)w.FindName("PortBox")).Text = "222";
        set.Invoke(w, [SparkStep.Ssh, SparkStepState.Done, "密码登录"]);
        set.Invoke(w, [SparkStep.Check, SparkStepState.Done, "网关正常"]);
        set.Invoke(w, [SparkStep.Install, SparkStepState.Skipped, null]);
        set.Invoke(w, [SparkStep.Pair, SparkStepState.Done, "2 个 Bot"]);
        set.Invoke(w, [SparkStep.Tunnel, SparkStepState.Running, null]);
        foreach (var l in new[] { "连接 user1@spark.example.lan:222 …", "主机指纹 SHA256:abc…", "installed=True gateway_ok=True models_ok=True", "Spark spark-8691：2 个 Bot，网关端口 8091，NVIDIA GB10，121.6 GB", "已在 Spark 上授权本机密钥（botlan@LAPTOP）。" })
            log.Invoke(w, [l]);
        Save(w, Path.Combine(outDir, "wizard-running.png"));
        set.Invoke(w, [SparkStep.Tunnel, SparkStepState.Failed, "x"]);
        msg.Invoke(w, ["用户名或密码不对（或 Spark 上的 BotLan 密钥已失效）。", true]);
        Save(w, Path.Combine(outDir, "wizard-error.png"));
        set.Invoke(w, [SparkStep.Tunnel, SparkStepState.Done, "127.0.0.1:8091"]);
        set.Invoke(w, [SparkStep.AddBots, SparkStepState.Done, "Vision、Coder"]);
        msg.Invoke(w, ["已添加 2 个 Bot：Vision、Coder。\n隧道 127.0.0.1:8091 会在 BotLan 启动时自动连接。", false]);
        ((FrameworkElement)w.FindName("ActionRow")).Visibility = Visibility.Visible;
        ((FrameworkElement)w.FindName("DoneButton")).Visibility = Visibility.Visible;
        Save(w, Path.Combine(outDir, "wizard-done.png"));
        w.Close();

        // Empty Long Island: in-memory only, never saved.
        AgentStore.Instance.Bots.Clear();
        var island = new LongIslandWindow();
        var cta = (FrameworkElement)island.FindName("SparkCta");
        Save(island, Path.Combine(outDir, "long-island-empty.png"));
        Check(cta.Visibility == Visibility.Visible && cta.ActualWidth <= 60, $"CTA visible with zero Bots, width {cta.ActualWidth}");
        AgentStore.Instance.Bots.Add(new AgentBot { Config = new AgentConfig { Name = "Vision", ColorHex = "#54A8FF", Id = "preview" } });
        Save(island, Path.Combine(outDir, "long-island-one-bot.png"));
        Check(cta.Visibility == Visibility.Collapsed, "CTA hidden once a Bot exists");
        island.Close();

        var cfg = new AgentConfigWindow();
        Save(cfg, Path.Combine(outDir, "agent-config.png"));
        cfg.Close();
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        int w = (int)window.Width, h = (int)window.Height;
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(0, 0, w, h));
        root.UpdateLayout();
        var raw = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        raw.Render(root);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(232, 234, 239)), null, new Rect(0, 0, w, h));
            dc.DrawImage(raw, new Rect(0, 0, w, h));
        }
        var result = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        result.Render(backdrop);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"rendered {path}");
    }
}
