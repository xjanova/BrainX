// MindPage — her page, and everything between it and AssistantService.
//
// She lives in two places now. Owner (2026-10-06): "ทำให้หน้าต่าง mide มีใน
// เมนูด้วย และแยกได้เช่นกัน" — a view in the dashboard's menu, and her own
// window (this exe) when she is popped out of it. Both are the same page
// talking to the same service, so the hosting lives here once and is compiled
// into both: BrainX.Mind owns the file, BrainX.Client links it. Two copies of
// this is exactly how the old in-dashboard chat drifted from the exe.
//
// What the page asks of its WINDOW (close, minimise, drag, pop out, dock) is
// handed to whoever hosts it through WindowAction — a frameless exe and a view
// inside the dashboard answer those very differently.

using System.IO;
using System.Net.Http;
using BrainX.Core.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BrainX.Mind;

public sealed class MindPage
{
    public const string PageUrl = "https://universe.local/universe/assistant-window.html";

    private readonly WebView2 _web;
    private readonly AssistantService _svc;
    private readonly string _vault;
    private readonly bool _embedded;
    private bool _wired;
    private bool _ready;
    // Bumped on every (re)load: an answer that comes back after the page it
    // was asked from has gone must not land in the next one.
    private int _generation;
    // Bumped on every question: the voice of an answer that was overtaken by
    // a newer question is not played over the newer one.
    private int _askSeq;
    // A status line meant for the page before it was ready to show it — the
    // "no avatar on this machine" notice used to be said into the void.
    private string? _pendingStatus;

    /// <summary>The page asked its window to: close, minimize, drag, popout, dock.</summary>
    public event Action<string>? WindowAction;

    /// <summary>Whether a "back into the dashboard" button makes sense right now.</summary>
    public Func<bool>? CanDock { get; set; }

    public MindPage(WebView2 web, AssistantService svc, string vault, bool embedded)
    {
        _web = web;
        _svc = svc;
        _vault = vault;
        _embedded = embedded;
    }

    /// <summary>Her page is loaded (or loading) — not put down by Unload.</summary>
    public bool IsLive { get; private set; }

    /// <summary>Wire the WebView (once) and load her page (every call).</summary>
    public async Task StartAsync(CoreWebView2Environment? env = null)
    {
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        var pack = new AvatarPackService();

        if (!_wired)
        {
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                "universe.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            var voiceDir = Path.Combine(_vault, ".obsidianx", "voice");
            Directory.CreateDirectory(voiceDir);
            core.SetVirtualHostNameToFolderMapping(
                "voice.local", voiceDir, CoreWebView2HostResourceAccessKind.Allow);

            // Her body is fetched once and kept beside `current`, not shipped
            // inside it — see AvatarPackService for why. The folder is mapped
            // whether or not the pack is there yet: mapping a missing folder is
            // harmless, and doing it here means the download can finish while
            // the page is already up rather than blocking the window on it.
            Directory.CreateDirectory(pack.Root);
            // Fast enough to wait for: the originals are copied off this same
            // machine, so by the time the page asks for her body it is there.
            await pack.EnsureLocalAsync();
            core.SetVirtualHostNameToFolderMapping(
                "avatar.local", pack.Root, CoreWebView2HostResourceAccessKind.Allow);
            // Runs before any page script, so the page never has to guess.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__mindAvatarBase='https://avatar.local/';");

            // The mic is the point of her, and neither host has anywhere
            // sensible to show a permission prompt. Her page only: the
            // dashboard serves other pages from universe.local too.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone &&
                    e.Uri.StartsWith("https://universe.local/universe/assistant-window.html", StringComparison.OrdinalIgnoreCase))
                    e.State = CoreWebView2PermissionState.Allow;
            };

            await AttachLogAsync(core);

            core.WebMessageReceived += OnMessage;
            core.Settings.AreDefaultContextMenusEnabled = false;
            _wired = true;
        }

        _ready = false;
        _generation++;
        core.Navigate(PageUrl + (_embedded ? "?embedded=1" : ""));
        IsLive = true;

        // Only the download half runs in the background. The copy half was
        // awaited above, BEFORE navigation — measured at 87ms for the
        // whole 33MB, against a reload() that has to arrive after the page
        // has attached its bridge and would silently do nothing if it beat
        // it there.
        if (!pack.IsInstalled) _ = EnsureAvatarAsync(pack);
    }

    /// <summary>Put her down: no voice, no mic, no render loop while she is
    /// somewhere else. StartAsync brings her back.</summary>
    public void Unload()
    {
        IsLive = false;
        _ready = false;
        _generation++;
        try { _web.CoreWebView2?.Navigate("about:blank"); } catch { }
    }

    /// <summary>Her console, on request.
    ///
    /// She has no address bar, no F12 and no status line, so a page that
    /// fails to load is a blank blue window and nothing else — which is
    /// exactly how a build shipped with the vendor scripts landing at the
    /// wrong paths: 404, 404, 404, and no way to see it from outside. Set
    /// BRAINX_MIND_LOG to a file path and every console message, exception
    /// and failed request goes there.</summary>
    private static async Task AttachLogAsync(CoreWebView2 core)
    {
        if (Environment.GetEnvironmentVariable("BRAINX_MIND_LOG") is not { Length: > 0 } logPath) return;
        try { File.Delete(logPath); } catch { }
        void W(string t) { try { File.AppendAllText(logPath, t + "\n"); } catch { } }
        var rt = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
        rt.DevToolsProtocolEventReceived += (_, ev) => W("CONSOLE " + ev.ParameterObjectAsJson);
        var ex2 = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
        ex2.DevToolsProtocolEventReceived += (_, ev) => W("THROW " + ev.ParameterObjectAsJson);
        var lg = core.GetDevToolsProtocolEventReceiver("Log.entryAdded");
        lg.DevToolsProtocolEventReceived += (_, ev) => W("LOG " + ev.ParameterObjectAsJson);
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        core.WebResourceResponseReceived += (_, e) =>
        {
            try
            {
                if (e.Response.StatusCode >= 400)
                    W($"HTTP {e.Response.StatusCode} {e.Request.Uri}");
            }
            catch { }
        };
    }

    /// <summary>
    /// Fetch her body if it is not already here, telling the page how far along
    /// it is. Silent when it is already installed — which is every run after
    /// the first, and the entire point of the exercise.
    /// </summary>
    private async Task EnsureAvatarAsync(AvatarPackService pack)
    {
        if (pack.IsInstalled) return;
        var progress = new Progress<(string stage, double fraction)>(p => _ = Status(Describe(p)));
        var dir = await pack.EnsureRemoteAsync(progress);
        if (dir != null) { await Eval("window.brainxAssistant?.reload?.()"); return; }

        // Nothing to reopen and nothing to retry: this machine simply does not
        // have her model on it. The clips are Mixamo's and the model is the
        // owner's, so neither is in the repository or in the installer — say
        // where to put them rather than offering a retry that cannot help.
        var here = AvatarPackService.LocalSources().First();
        await Status($"ยังไม่มีไฟล์ตัวมายในเครื่องนี้ค่ะ — วางไว้ที่ {here} แล้วเปิดใหม่นะคะ");
    }

    /// <summary>Her strip's status line — kept until the page is ready if it
    /// is not yet.</summary>
    public async Task Status(string text)
    {
        if (!_ready) { _pendingStatus = text; return; }
        await Eval($"window.brainxChat?.status?.({Json(text)})");
    }

    private static string Describe((string stage, double fraction) p) => p.stage switch
    {
        "missing" => "",
        "connecting" => "กำลังเชื่อมต่อ…",
        "downloading" => $"กำลังโหลดตัวมาย {p.fraction * 100:0}%",
        "unpacking" => "กำลังแตกไฟล์…",
        "ready" => "",
        _ => p.stage,
    };

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var m = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                e.WebMessageAsJson, new { type = "", text = "", action = "" });
            switch (m?.type)
            {
                case "mind.ready":
                    _ready = true;
                    // Read now, not when the host started: the voice — and with
                    // it the pronoun and particle — may have been changed in the
                    // dashboard's settings since.
                    var cfg = _svc.LoadConfig();
                    var dock = !_embedded && (CanDock?.Invoke() ?? false);
                    // The page writes Thai of its own (greeting, mic errors),
                    // so it needs the particle too — not just the face gender.
                    await Eval($"window.brainxAssistant.configure({{" +
                               $"name:{Json(cfg.Name)}," +
                               $"female:{(cfg.Female ? "true" : "false")}," +
                               $"self:{Json(cfg.SelfWord)}," +
                               $"particle:{Json(cfg.EndParticle)}," +
                               $"dockable:{(dock ? "true" : "false")}}})");
                    if (_pendingStatus is { } pending) { _pendingStatus = null; await Status(pending); }
                    _ = _svc.WarmAsync();      // see AssistantService.WarmAsync
                    break;

                case "mind.ask":
                    if (!string.IsNullOrWhiteSpace(m.text)) _ = AskAsync(m.text);
                    break;

                case "mind.window":
                    if (!string.IsNullOrEmpty(m.action)) WindowAction?.Invoke(m.action);
                    break;

                case "mind.drag":
                    WindowAction?.Invoke("drag");
                    break;
            }
        }
        catch { }
    }

    private async Task AskAsync(string question)
    {
        var gen = _generation;
        var seq = ++_askSeq;
        var particle = _svc.LoadConfig().EndParticle;
        try
        {
            var answer = await _svc.AskAsync(question);
            if (gen != _generation) return;
            if (string.IsNullOrWhiteSpace(answer))
            {
                // Her own words, so they carry her own particle. A male voice
                // apologising with ค่ะ is the wrong person talking.
                await Eval($"window.brainxChat.reply({Json($"ยังตอบไม่ได้{particle} — โมเดลตอบกลับมาว่างเปล่า ลองถามใหม่อีกครั้ง")},false)");
                return;
            }
            // Text first, voice second: reading is faster than listening, and
            // an answer that exists only as audio cannot be re-read or copied.
            await Eval($"window.brainxChat.reply({Json(answer)},true)");

            var mp3 = await _svc.SpeakAsync(answer);
            if (mp3 != null && gen == _generation && seq == _askSeq)
                await Eval($"window.brainxAssistant.say('https://voice.local/{Uri.EscapeDataString(mp3)}')");
        }
        catch (Exception ex)
        {
            if (gen == _generation)
                await Eval($"window.brainxChat.reply({Json(Explain(ex, particle))},false)");
        }
    }

    /// <summary>What went wrong, in her words — not HttpClient's English.</summary>
    private static string Explain(Exception ex, string particle) => ex switch
    {
        HttpRequestException { StatusCode: null } =>
            $"ติดต่อ Ollama ไม่ได้{particle} — ตรวจว่าเปิดอยู่ที่ 11434",
        HttpRequestException h =>
            $"Ollama ตอบกลับเป็นข้อผิดพลาด ({(int?)h.StatusCode}){particle} — ลองใหม่อีกครั้งนะ{(particle == "ค่ะ" ? "คะ" : particle)}",
        TaskCanceledException or OperationCanceledException =>
            $"คิดนานเกินไปจนหมดเวลา{particle} — ลองถามให้สั้นลง หรือรอให้ Ollama โหลดโมเดลเสร็จก่อน",
        _ => $"ผิดพลาด{particle}: {ex.Message}",
    };

    /// <summary>Run script in her page — only once it has said it is ready.</summary>
    public async Task Eval(string js)
    {
        if (!_ready || _web?.CoreWebView2 == null) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync(js); } catch { }
    }

    /// <summary>JSON-encode for a script string — a name or an answer with a
    /// quote in it would otherwise be a syntax error in the page.</summary>
    public static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}

/// <summary>
/// The note her own window leaves when the owner asks to put her back into
/// the dashboard. The two are separate processes; a file the dashboard checks
/// when it sees her exit is all the conversation they need.
/// </summary>
public static class MindDock
{
    public static string RequestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "mind-dock.request");

    /// <summary>Take the note if it is there and recent — one left behind by a
    /// crash an hour ago is not a request.</summary>
    public static bool TakeRequest()
    {
        try
        {
            var p = RequestPath;
            if (!File.Exists(p)) return false;
            var fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(p) < TimeSpan.FromSeconds(60);
            File.Delete(p);
            return fresh;
        }
        catch { return false; }
    }
}
