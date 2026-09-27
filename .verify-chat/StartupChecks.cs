using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IslandUI;
using IslandUI.Services;
using Microsoft.Web.WebView2.Wpf;

internal static class StartupChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    public static int Run(App app, string output, bool checkIdle = false)
    {
        Environment.SetEnvironmentVariable("ISLANDUI_TEST_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("TASK_WORKER_ENABLED", "false");
        var frame = new DispatcherFrame();
        int failures = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            ChatWindow? window = null;
            var timings = new List<object>();
            var runtimeType = typeof(ChatWindow).Assembly.GetType("IslandUI.Services.ChatStudioRuntime")!;
            object? runtime = null;
            try
            {
                AgentStore.Instance.Bots.Clear();
                var bot = new AgentBot { Config = new AgentConfig { Id = "startup-fixture", Name = "启动测试 Bot",
                    ApiKey = "unused-test-key", BaseUrl = "http://127.0.0.1:1/v1" } };
                AgentStore.Instance.Bots.Add(bot);
                runtime = runtimeType.GetProperty("Shared")!.GetValue(null)!;
                int? firstBackend = null;
                for (int pass = 0; pass < (checkIdle ? 4 : 3); pass++)
                {
                    var clock = Stopwatch.StartNew();
                    window = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null, [bot, null], null)!;
                    window.Show();
                    WebView2CompositionControl? view = null;
                    bool ready = false;
                    var phases = new Dictionary<string, long>();
                    while (clock.Elapsed < TimeSpan.FromSeconds(110))
                    {
                        view = typeof(ChatWindow).GetField("_studioView", Private)!.GetValue(window) as WebView2CompositionControl;
                        if (runtimeType.GetField("_process", Private)!.GetValue(runtime) != null)
                            phases.TryAdd("backendSpawn", clock.ElapsedMilliseconds);
                        if (runtimeType.GetField("_address", Private)!.GetValue(runtime) != null)
                            phases.TryAdd("backendReady", clock.ElapsedMilliseconds);
                        if (view?.CoreWebView2 != null)
                            phases.TryAdd("browserReady", clock.ElapsedMilliseconds);
                        if (((Button)window.FindName("StudioRetry")).IsVisible)
                            throw new Exception(((TextBlock)window.FindName("StudioLoadMessage")).Text);
                        if (view?.CoreWebView2 != null && await view.CoreWebView2.ExecuteScriptAsync(
                            "Boolean(document.querySelector('textarea[placeholder*=\"输入消息\"],textarea[placeholder*=\"Message or instruction\"]'))") == "true")
                        { ready = true; break; }
                        await Task.Delay(50);
                    }
                    if (!ready) throw new TimeoutException("Composer never became ready to send.");
                    var backend = (Process?)runtimeType.GetField("_process", Private)!.GetValue(runtime);
                    if (pass == 0) firstBackend = backend?.Id;
                    else if (pass < 3 && checkIdle && backend?.Id != firstBackend)
                        throw new Exception("Rapid reopen did not reuse the same owned backend.");
                    timings.Add(new { pass, readyMs = clock.ElapsedMilliseconds, backendId = backend?.Id, phases });
                    Console.WriteLine($"READY pass={pass} ms={clock.ElapsedMilliseconds} backend={backend?.Id}");
                    Console.WriteLine("PHASES " + JsonSerializer.Serialize(phases));
                    Console.WriteLine("PAGE_TIMING " + await view!.CoreWebView2.ExecuteScriptAsync(
                        "JSON.stringify({navigation:performance.getEntriesByType('navigation').map(e=>({dom:e.domContentLoadedEventEnd,load:e.loadEventEnd})),resources:performance.getEntriesByType('resource').map(e=>({path:new URL(e.name).pathname,start:Math.round(e.startTime),duration:Math.round(e.duration)}))})"));
                    window.Close(); window = null;
                    var idle = runtimeType.GetField("_idleStop", Private)!.GetValue(runtime) as CancellationTokenSource;
                    Console.WriteLine($"CLOSED idle={idle != null} cancelled={idle?.IsCancellationRequested} exiting={runtimeType.GetField("_exiting", Private)!.GetValue(runtime)} gate={((SemaphoreSlim)runtimeType.GetField("_gate", Private)!.GetValue(runtime)!).CurrentCount}");
                    if (checkIdle && pass == 2)
                    {
                        Console.WriteLine("Waiting for the real 90-second idle release...");
                        var idleWatch = Stopwatch.StartNew();
                        int ownedId = backend!.Id;
                        await Task.Delay(90000);
                        // Timer dispatch and the existing graceful database shutdown need
                        // scheduling allowance on a machine also rendering/building tests.
                        while (idleWatch.Elapsed < TimeSpan.FromSeconds(120)
                            && (runtimeType.GetField("_process", Private)!.GetValue(runtime) != null || ProcessAlive(ownedId)))
                            await Task.Delay(250);
                        Console.WriteLine($"IDLE_STATE timer={runtimeType.GetField("_idleStop", Private)!.GetValue(runtime) != null} gate={((SemaphoreSlim)runtimeType.GetField("_gate", Private)!.GetValue(runtime)!).CurrentCount}");
                        if (runtimeType.GetField("_process", Private)!.GetValue(runtime) != null || ProcessAlive(ownedId))
                            throw new Exception("Idle backend was not released.");
                        timings.Add(new { idleReleaseMs = idleWatch.ElapsedMilliseconds });
                        Console.WriteLine($"PASS idle backend and OS process released at {idleWatch.ElapsedMilliseconds}ms; testing reopen after expiry");
                    }
                    else await Task.Delay(350);
                }
                File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + ex); }
            finally
            {
                window?.Close();
                if (runtime != null) await (Task)runtimeType.GetMethod("StopAsync")!.Invoke(runtime, null)!;
                Console.WriteLine($"Startup failures: {failures}");
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return failures == 0 ? 0 : 1;
    }

    private static bool ProcessAlive(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
