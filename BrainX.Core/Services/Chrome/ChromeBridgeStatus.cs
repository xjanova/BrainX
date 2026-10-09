using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services.Chrome;

/// <summary>
/// The contract shared with CluadeX (team T4b): bridge_status → "chrome".
/// The names are the wire format; never rename one.
/// </summary>
public enum ChromeBridgeState
{
    NoClaudeCode, ClaudeTooOld, NotSignedIn, ApiKeyOnly, ExtensionMissing, ExtensionDisabled,
    BrowserClosed, Ready, Connecting, Connected, NotReachable, MultiBrowser, Disabled, Failed,
}

/// <summary>What the running bridge has learned from its own process and calls.</summary>
public sealed class ChromeRuntimeSnapshot
{
    public bool Live { get; init; }
    public bool Connecting { get; init; }
    /// <summary>"ok" | "not-reachable" | "multi-browser" | "failed" | null (no call yet).</summary>
    public string? LastOutcome { get; init; }
    public DateTime? LastOutcomeUtc { get; init; }
    /// <summary>A short, already-sanitised reason for "failed" (first stderr line, spawn error).</summary>
    public string? FailureDetail { get; init; }
    public string? ServerName { get; init; }
    public string? ServerVersion { get; init; }
    public int Tools { get; init; }
}

/// <summary>
/// Turns everything the bridge can know into ONE state with ONE action — the
/// card on the other side shows a single working button per state, never a
/// dead one.
///
/// Precedence, and why:
///   1. Off (the owner's switch, or the remote endpoint)  — nothing else matters.
///   2. No usable Claude Code (missing / too old)          — nothing can start.
///   3. Not signed in / API-key only                       — it starts, but never connects.
///   4. What the bridge itself saw (connected, several browsers, connecting)
///      — with Anthropic's cloud bridge the browser can be on ANOTHER computer,
///      so a live, answering bridge outranks this PC's folders.
///   5. Local extension hints (missing / disabled / browser closed).
///   6. The bridge's own failures (not reachable, crashed).
///   7. Ready — everything checks out and the next call will connect.
/// </summary>
public static class ChromeBridgeStatus
{
    public sealed class Inputs
    {
        public bool Enabled { get; init; }
        public bool Remote { get; init; }
        public ClaudeLocateResult? Locate { get; init; }
        public ClaudeAuthResult? Auth { get; init; }
        public ChromeScanResult? Scan { get; init; }
        public ChromeRuntimeSnapshot Runtime { get; init; } = new();
        public int RetentionDays { get; init; } = ChromeBridgeSettings.DefaultRetentionDays;
        public string? PrivateLogPath { get; init; }
        public bool PrivateLogAvailable { get; init; }
    }

    public static ChromeBridgeState Decide(Inputs i)
    {
        if (i.Remote || !i.Enabled) return ChromeBridgeState.Disabled;

        if (i.Locate is { } loc)
        {
            if (loc.Status == ClaudeLocateStatus.TooOld) return ChromeBridgeState.ClaudeTooOld;
            if (loc.Status != ClaudeLocateStatus.Found) return ChromeBridgeState.NoClaudeCode;
        }

        if (i.Auth?.State == ClaudeAuthState.NotSignedIn) return ChromeBridgeState.NotSignedIn;
        if (i.Auth?.State == ClaudeAuthState.ApiKeyOnly) return ChromeBridgeState.ApiKeyOnly;

        var rt = i.Runtime;
        if (rt.Connecting) return ChromeBridgeState.Connecting;
        if (rt.LastOutcome == "multi-browser") return ChromeBridgeState.MultiBrowser;
        if (rt.Live && rt.LastOutcome == "ok") return ChromeBridgeState.Connected;

        if (i.Scan is { } scan)
        {
            if (!scan.AnyInstalled) return ChromeBridgeState.ExtensionMissing;
            if (!scan.AnyEnabled) return ChromeBridgeState.ExtensionDisabled;
            if (!scan.AnyEnabledRunning && rt.LastOutcome != "ok") return ChromeBridgeState.BrowserClosed;
        }

        if (rt.LastOutcome == "not-reachable") return ChromeBridgeState.NotReachable;
        if (rt.LastOutcome == "failed") return ChromeBridgeState.Failed;
        return ChromeBridgeState.Ready;
    }

    /// <summary>The "chrome" entry, in the shape the contract fixes.</summary>
    public static JObject Build(Inputs i)
    {
        var state = Decide(i);
        var install = i.Locate?.Install;
        var claudePath = install?.Path ?? i.Locate?.Rejected?.Path;
        var claudeVersion = install?.Version ?? i.Locate?.RejectedVersion;

        var browsers = new JArray();
        foreach (var b in i.Scan?.Installs ?? new List<ChromeExtensionInstall>())
            browsers.Add(new JObject
            {
                ["name"] = b.BrowserName,
                ["profile"] = b.ProfileName,
                ["extensionVersion"] = b.ExtensionVersion,
                ["enabled"] = b.Enabled,
                ["running"] = i.Scan!.RunningBrowsers.Contains(b.Browser),
            });

        var (en, th) = Messages(state, i);
        var action = Action(state, i);

        return new JObject
        {
            ["state"] = state.ToString(),
            ["claudePath"] = claudePath,
            ["claudeVersion"] = claudeVersion,
            ["browsers"] = browsers,
            ["message_en"] = en,
            ["message_th"] = th,
            ["action"] = action,
            // Beyond the fixed fields — useful to an agent, ignored by the card.
            ["enabled"] = i.Enabled && !i.Remote,
            ["authMethod"] = i.Auth?.Method,
            ["server"] = i.Runtime.ServerName == null ? null : $"{i.Runtime.ServerName} {i.Runtime.ServerVersion}".Trim(),
            ["tools"] = i.Runtime.Tools,
            ["toolPrefix"] = ChromeBridgeIds.Prefix,
            ["hiddenTools"] = new JArray(ChromeBridgeIds.NeverExposed.ToArray()),
            ["privateLog"] = new JObject
            {
                ["available"] = i.PrivateLogAvailable,
                ["path"] = i.PrivateLogPath,
                ["retentionDays"] = i.RetentionDays,
                ["searchTool"] = ChromeBridgeIds.PrivateSearchTool,
                ["note"] = "chrome__* arguments and results never enter the vault, the journal, the agent bus or any sync; they are kept here only.",
            },
        };
    }

    private static (string En, string Th) Messages(ChromeBridgeState s, Inputs i)
    {
        const string apiNoteEn = "An API key or API credit cannot drive Chrome — sign in to Claude Code with a claude.ai Pro/Max/Team/Enterprise account. The built-in browser works with any provider.";
        const string apiNoteTh = "API key หรือเครดิต API ใช้คุม Chrome ไม่ได้ — ต้องล็อกอิน Claude Code ด้วยบัญชี claude.ai แบบ Pro/Max/Team/Enterprise ส่วนเบราว์เซอร์ในตัวใช้ได้กับทุกผู้ให้บริการ";
        var v = i.Locate?.RejectedVersion ?? "?";
        var browser = i.Scan?.Installs.FirstOrDefault()?.BrowserName ?? "Chrome";
        var profile = i.Scan?.Installs.FirstOrDefault()?.ProfileName ?? "";

        return s switch
        {
            ChromeBridgeState.Disabled when i.Remote => (
                "Claude in Chrome is local-only: it is never available over the remote endpoint.",
                "Claude in Chrome ใช้ได้เฉพาะในเครื่องนี้ — ไม่เปิดให้ใช้ผ่าน endpoint ระยะไกล"),
            ChromeBridgeState.Disabled => (
                "The Claude in Chrome bridge is off. Turn it on in BrainX ▸ Settings ▸ Claude in Chrome, then restart the agent.",
                "การเชื่อม Claude in Chrome ปิดอยู่ — เปิดได้ที่ BrainX ▸ ตั้งค่า ▸ Claude in Chrome แล้วเริ่มเอเจนต์ใหม่"),
            ChromeBridgeState.NoClaudeCode => (
                "Claude Code isn't installed on this PC. The Chrome bridge runs through Claude Code.",
                "ยังไม่พบ Claude Code ในเครื่องนี้ — การเชื่อม Chrome ต้องใช้ Claude Code"),
            ChromeBridgeState.ClaudeTooOld => (
                $"Claude Code {v} is too old (needs 2.1 or newer).",
                $"Claude Code {v} เก่าเกินไป (ต้องเป็น 2.1 ขึ้นไป)"),
            ChromeBridgeState.NotSignedIn => (
                "Claude Code is not signed in. " + apiNoteEn,
                "Claude Code ยังไม่ได้ล็อกอิน — " + apiNoteTh),
            ChromeBridgeState.ApiKeyOnly => (
                "Claude Code is signed in with an API key. " + apiNoteEn,
                "Claude Code ล็อกอินด้วย API key อยู่ — " + apiNoteTh),
            ChromeBridgeState.ExtensionMissing => (
                "The Claude extension isn't installed in Chrome or Edge on this PC.",
                "ยังไม่ได้ติดตั้งส่วนขยาย Claude ใน Chrome หรือ Edge ในเครื่องนี้"),
            ChromeBridgeState.ExtensionDisabled => (
                $"The Claude extension is turned off in {browser}{(profile.Length > 0 ? $" ({profile})" : "")}. Turn it on at {ExtensionsPage(i)}.",
                $"ส่วนขยาย Claude ถูกปิดอยู่ใน {browser}{(profile.Length > 0 ? $" ({profile})" : "")} — เปิดได้ที่ {ExtensionsPage(i)}"),
            ChromeBridgeState.BrowserClosed => (
                $"Open {browser} first — the extension only answers while the browser is running.",
                $"เปิด {browser} ก่อน — ส่วนขยายจะตอบได้เฉพาะตอนเบราว์เซอร์เปิดอยู่"),
            ChromeBridgeState.Ready => (
                "Ready. The bridge starts on the first chrome__ call.",
                "พร้อมแล้ว — จะเริ่มเชื่อมเมื่อมีการเรียก chrome__ ครั้งแรก"),
            ChromeBridgeState.Connecting => (
                "Connecting to Claude in Chrome…",
                "กำลังเชื่อมต่อ Claude in Chrome…"),
            ChromeBridgeState.Connected => (
                $"Connected · {i.Runtime.Tools} tools.",
                $"เชื่อมแล้ว · {i.Runtime.Tools} เครื่องมือ"),
            ChromeBridgeState.NotReachable => (
                $"Chrome didn't answer. Sign the extension in with the same claude.ai account as Claude Code, then test again. Reconnect: {ChromeBridgeIds.ReconnectUrl}",
                $"Chrome ไม่ตอบ — ให้ส่วนขยายล็อกอินบัญชี claude.ai เดียวกับ Claude Code แล้วกดทดสอบอีกครั้ง เชื่อมใหม่: {ChromeBridgeIds.ReconnectUrl}"),
            ChromeBridgeState.MultiBrowser => (
                "Several browsers are connected to this account and none is chosen — pick one.",
                "มีเบราว์เซอร์เชื่อมกับบัญชีนี้หลายตัวและยังไม่ได้เลือก — เลือกตัวที่จะใช้"),
            ChromeBridgeState.Failed => (
                "The Claude in Chrome server could not start" + (string.IsNullOrWhiteSpace(i.Runtime.FailureDetail) ? "." : $": {i.Runtime.FailureDetail}"),
                "เริ่มเซิร์ฟเวอร์ Claude in Chrome ไม่สำเร็จ" + (string.IsNullOrWhiteSpace(i.Runtime.FailureDetail) ? "" : $": {i.Runtime.FailureDetail}")),
            _ => ("", ""),
        };
    }

    private static string ExtensionsPage(Inputs i) =>
        i.Scan?.Installs.FirstOrDefault(x => !x.Enabled)?.Browser switch
        {
            "edge" => "edge://extensions",
            "brave" => "brave://extensions",
            "opera" => "opera://extensions",
            "vivaldi" => "vivaldi://extensions",
            _ => "chrome://extensions",
        };

    private static JObject Action(ChromeBridgeState s, Inputs i)
    {
        JObject A(string kind, string en, string th, string? target) => new()
        {
            ["kind"] = kind, ["label_en"] = en, ["label_th"] = th, ["target"] = target,
        };
        var install = i.Locate?.Install;
        var test = ChromeBridgeIds.Prefix + "list_connected_browsers";

        switch (s)
        {
            case ChromeBridgeState.Disabled when i.Remote:
                return A("none", "", "", null);
            case ChromeBridgeState.Disabled:
                return A("open_brainx_settings", "Turn on in BrainX", "เปิดใน BrainX", ChromeBridgeIds.BrainXSettingsTarget);
            case ChromeBridgeState.NoClaudeCode:
                return A("open_url", "How to install", "วิธีติดตั้ง", ChromeBridgeIds.InstallClaudeCodeUrl);
            case ChromeBridgeState.ClaudeTooOld:
            {
                var rej = i.Locate?.Rejected;
                if (rej?.Kind is ClaudeInstallKind.Desktop or ClaudeInstallKind.DesktopMsix)
                    return A("open_url", "Open the Claude app to update", "เปิดแอป Claude เพื่ออัปเดต", ChromeBridgeIds.DesktopDownloadUrl);
                if (rej?.Kind == ClaudeInstallKind.Npm)
                    return A("run_console", "Update Claude Code", "อัปเดต Claude Code", "npm install -g @anthropic-ai/claude-code@latest");
                var cmd = rej == null ? "claude update"
                    : new ClaudeCodeInstall(rej.Path, rej.Kind, rej.Command, rej.PrefixArgs, "0.0.0").DisplayCommand("update");
                return A("run_console", "Update Claude Code", "อัปเดต Claude Code", cmd);
            }
            case ChromeBridgeState.NotSignedIn:
            case ChromeBridgeState.ApiKeyOnly:
                return A("run_console", "Sign in", "เข้าสู่ระบบ",
                    install?.DisplayCommand("auth", "login", "--claudeai") ?? "claude auth login --claudeai");
            case ChromeBridgeState.ExtensionMissing:
                return A("open_url", "Install extension", "ติดตั้งส่วนขยาย", ChromeBridgeIds.InstallExtensionUrl);
            case ChromeBridgeState.ExtensionDisabled:
            {
                var page = ExtensionsPage(i);
                return A("copy_text", $"Copy {page}", $"คัดลอก {page}", page);
            }
            case ChromeBridgeState.BrowserClosed:
            {
                var b = i.Scan?.Installs.FirstOrDefault(x => x.Enabled);
                var name = b?.BrowserName ?? "Chrome";
                return A("launch_browser", $"Open {name}", $"เปิด {name}", b?.ProcessName ?? "chrome");
            }
            case ChromeBridgeState.Ready:
                return A("call_tool", "Connect", "เชื่อมต่อ", test);
            case ChromeBridgeState.Connecting:
                return A("none", "", "", null);
            case ChromeBridgeState.Connected:
                return A("call_tool", "Test", "ทดสอบ", test);
            case ChromeBridgeState.NotReachable:
                return A("call_tool", "Test again", "ทดสอบอีกครั้ง", test);
            case ChromeBridgeState.MultiBrowser:
                return A("call_tool", "Choose a browser", "เลือกเบราว์เซอร์", test);
            case ChromeBridgeState.Failed:
                return A("check_again", "Check again", "ตรวจอีกครั้ง", "bridge_status");
            default:
                return A("none", "", "", null);
        }
    }
}
