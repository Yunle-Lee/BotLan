using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandUI;
using IslandUI.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

/// <summary>One real turn with the Spark Bot (via the user's tunnel), to see workflow + stats render.
/// Separate data dir; the Bot config is copied in memory only. Needs BOTLAN_LIVE_KEY.</summary>
internal static class LiveChat
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    public static int Run(App app, string output, string prompt)
    {
        Environment.SetEnvironmentVariable("ISLANDUI_TEST_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("TASK_WORKER_ENABLED", "false");
        var key = Environment.GetEnvironmentVariable("BOTLAN_LIVE_KEY") ?? "";
        int failures = 0;
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(async () =>
        {
            ChatWindow? window = null;
            try
            {
                AgentStore.Instance.Bots.Clear();
                AgentStore.Instance.Bots.Add(new AgentBot { Config = new AgentConfig {
                    Id = "live-spark", Name = "Spark · Jev-Step", ColorHex = "#76B900",
                    BaseUrl = "http://127.0.0.1:8091/v1", Model = "jev-step", ApiKey = key } });
                window = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null,
                    [AgentStore.Instance.Bots[0], null], null)!;
                window.Left = 40; window.Top = 30; window.Show();
                var watch = Stopwatch.StartNew();
                WebView2CompositionControl? view = null;
                while (watch.ElapsedMilliseconds < 120000)
                {
                    view = typeof(ChatWindow).GetField("_studioView", Private)!.GetValue(window) as WebView2CompositionControl;
                    if (view?.CoreWebView2 != null && await Js(view, "!!document.querySelector('#island-composer textarea')")) break;
                    await Task.Delay(200);
                }
                await Task.Delay(1500);
                await view!.CoreWebView2.ExecuteScriptAsync("(() => {const t=document.querySelector('#island-composer textarea'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t," + System.Text.Json.JsonSerializer.Serialize(prompt) + "); t.dispatchEvent(new Event('input',{bubbles:true})); t.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));})()");
                // Approve the first Spark command if one is requested (read-only prompt).
                watch.Restart();
                bool approved = false;
                while (watch.ElapsedMilliseconds < 180000)
                {
                    await Task.Delay(700);
                    if (await Js(view, "[...document.querySelectorAll('[role=button]')].some(b=>/批准执行|Approve/.test(b.innerText))"))
                    {
                        if (!approved) await Shot(view, Path.Combine(output, "live-approval.png"));
                        await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[role=button]')].find(b=>/批准执行|Approve/.test(b.innerText)).click()");
                        approved = true;
                    }
                    if (await Js(view, "/tok[/]s|输出 ·|out ·/.test(document.body.innerText)")) break;
                }
                await Task.Delay(800);
                var text = await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#island-main-content').innerText.slice(-1500)");
                Console.WriteLine("PAGE TAIL: " + System.Text.Json.JsonSerializer.Deserialize<string>(text));
                bool stats = await Js(view, "/tok[/]s/.test(document.body.innerText) && /上下文|context/.test(document.body.innerText)");
                Console.WriteLine((stats ? "PASS" : "FAIL") + " speed/context footer rendered"); if (!stats) failures++;
                Console.WriteLine((await Js(view, "/工作流|Workflow/.test(document.body.innerText)") ? "INFO" : "INFO no") + " workflow timeline present");
                await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[role=button]')].find(b=>/工作流|Workflow/.test(b.innerText))?.click()");
                await Task.Delay(400);
                var steps = await view.CoreWebView2.ExecuteScriptAsync("(() => { const b=[...document.querySelectorAll('[role=button]')].find(b=>/工作流|Workflow/.test(b.innerText)); return b ? b.parentElement.innerText : ''; })()");
                Console.WriteLine("TIMELINE: " + System.Text.Json.JsonSerializer.Deserialize<string>(steps));
                var cards = await view.CoreWebView2.ExecuteScriptAsync("(() => { const all=[...document.querySelectorAll('div')].filter(d=>d.innerText && d.innerText.startsWith('DGX Spark · run_spark_command')); const leaves=all.filter(d=>!all.some(o=>o!==d && d.contains(o))); return leaves.map(d=>d.innerText.split(String.fromCharCode(10)).slice(1,3).join(' | ')).join(' || '); })()");
                Console.WriteLine("CARDS: " + System.Text.Json.JsonSerializer.Deserialize<string>(cards));
                await view.CoreWebView2.ExecuteScriptAsync("(() => { const b=[...document.querySelectorAll('[role=button]')].find(b=>/工作流|Workflow/.test(b.innerText)); b?.scrollIntoView({block:'start'}); })()");
                await Task.Delay(400);
                await Shot(view, Path.Combine(output, "live-timeline.png"));
                await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('[role=button]')].find(b=>/回到底部|latest/i.test(b.innerText))?.click()");
                await Task.Delay(600);
                await Shot(view, Path.Combine(output, "live-reply.png"));
            }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + ex); }
            finally
            {
                window?.Close();
                var runtimeType = typeof(ChatWindow).Assembly.GetType("IslandUI.Services.ChatStudioRuntime")!;
                await (Task)runtimeType.GetMethod("StopAsync")!.Invoke(runtimeType.GetProperty("Shared")!.GetValue(null), null)!;
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> Js(WebView2CompositionControl view, string expression)
        => await view.CoreWebView2.ExecuteScriptAsync("Boolean(" + expression + ")") == "true";

    private static async Task Shot(WebView2CompositionControl view, string path)
    {
        using var file = File.Create(path);
        await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }
}
