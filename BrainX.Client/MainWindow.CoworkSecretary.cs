using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using BrainX.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// Mind as the room's secretary, on the local model.
//
// Owner (2026-10-06): "ทำเป็นเลขาแต่เปิดปิดใช้หรือไม่ใช้ก็ได้" and "เลือกได้ว่า
// จะให้ มายด์ทำอะไรได้บ้าง" — including writing code "ในกรณีที่ การ์ด gpu ใหญ่".
//
// What she can be given, each switched on its own (cowork/secretary.json):
//   answer     the owner asks her in the room (@มาย / @mind) — she answers from
//              the room's own files (CoworkSnapshot), never by guessing.
//   digest     every so often, a few lines on what the room got done, what is
//              stuck and what waits on the owner.
//   summarize  a project's whole story in a few lines, from the history.
//   code       a seat at the table as a coding agent: the broker starts her
//              like any runner, as codex on the local model (--oss). Only on a
//              card with the memory for a coding model.
//
// She costs nobody's quota: Ollama on this machine. Her lines are never
// addressed to anyone, so they wake nobody — the room's floor rules tell the
// agents her lines are summaries, not orders.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private const int SecretaryDigestEvery = 15;              // room lines
    private static readonly TimeSpan SecretaryDigestGap = TimeSpan.FromMinutes(30);
    private const int SecretaryCodeVramMb = 12_000;
    private static readonly Regex SecretaryAliasRx =
        new(@"@(มายด์|มายด|มาย)(?![\p{L}\p{M}\p{Nd}_-])", RegexOptions.CultureInvariant);

    private DispatcherTimer? _secretaryTimer;
    private bool _secretaryBusy;
    private string _secretaryDoing = "";
    private string _secretaryError = "";
    private List<string> _secretaryModels = new();
    private DateTime _secretaryModelsAt;
    private (string Name, int VramMb)? _gpu;

    private string SecretaryPath => Path.Combine(CoworkBusRoot, "cowork", "secretary.json");

    private JObject SecretaryConfig()
    {
        JObject o;
        try { o = File.Exists(SecretaryPath) ? JObject.Parse(File.ReadAllText(SecretaryPath)) : new JObject(); }
        catch { o = new JObject(); }
        o["enabled"] ??= false;
        o["model"] ??= "";
        o["codeModel"] ??= "";
        if (o["can"] is not JObject can) o["can"] = can = new JObject();
        can["answer"] ??= true;
        can["digest"] ??= true;
        can["summarize"] ??= true;
        can["code"] ??= false;
        return o;
    }

    private void SaveSecretaryConfig(JObject o)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SecretaryPath)!);
            var tmp = SecretaryPath + ".tmp-" + Guid.NewGuid().ToString("N")[..6];
            File.WriteAllText(tmp, o.ToString(Formatting.Indented), new UTF8Encoding(false));
            File.Move(tmp, SecretaryPath, overwrite: true);
        }
        catch (Exception ex) { _secretaryError = "บันทึกการตั้งค่าเลขาไม่ได้: " + ex.Message; }
    }

    /// <summary>Her own bookkeeping (cursor, last digest) onto the file as it
    /// is NOW — the owner may have changed a setting while she was thinking,
    /// and saving the copy she started with would undo it.</summary>
    private void UpdateSecretaryState(Action<JObject> change)
    {
        var now = SecretaryConfig();
        change(now);
        SaveSecretaryConfig(now);
    }

    private static bool Can(JObject cfg, string what) => cfg["can"]?[what]?.ToObject<bool?>() == true;
    private bool SecretaryOn => SecretaryConfig()["enabled"]?.ToObject<bool?>() == true;

    /// <summary>Started with the window; ticks whether or not the room is on
    /// screen — a secretary who only listens while you watch is not one.</summary>
    private void StartSecretary()
    {
        if (_secretaryTimer != null) return;
        _secretaryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _secretaryTimer.Tick += async (_, _) => await SecretaryTickAsync();
        _secretaryTimer.Start();
    }

    private async Task SecretaryTickAsync()
    {
        if (_secretaryBusy) return;
        var cfg = SecretaryConfig();
        if (cfg["enabled"]?.ToObject<bool?>() != true) return;
        _secretaryBusy = true;
        try { await Task.Run(() => SecretaryPassAsync(cfg)); }
        catch (Exception ex) { _secretaryError = ex.Message; }
        finally { _secretaryBusy = false; _secretaryDoing = ""; }
    }

    /// <summary>One look at what was said since the last one: answer what was
    /// asked of her, and write the digest when enough has happened.</summary>
    private async Task SecretaryPassAsync(JObject cfg)
    {
        var dir = Path.Combine(CoworkBusRoot, "cowork", "messages");
        if (!Directory.Exists(dir)) return;
        var names = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).OfType<string>()
                             .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (names.Count == 0) return;

        var cursor = cfg["cursor"]?.ToString() ?? "";
        // First time on: start from now, not from the room's whole past.
        if (cursor.Length == 0)
        {
            var latest = names[^1];
            UpdateSecretaryState(c =>
            {
                c["cursor"] = latest;
                c["digestFrom"] = latest;
                c["lastDigestUtc"] = DateTime.UtcNow.ToString("o");
            });
            return;
        }

        var fresh = names.Where(n => string.CompareOrdinal(n, cursor) > 0).ToList();
        var asks = new List<string>();
        foreach (var n in fresh)
        {
            var o = TryReadJson(Path.Combine(dir, n));
            if (o == null || IsSecretaryLine(o)) continue;
            if (Can(cfg, "answer") && IsOwnersLine(o, n)
                && (o["to"]?.ToString() ?? "").Split(',').Any(t => t.Trim().Equals("mind", StringComparison.OrdinalIgnoreCase)))
                asks.Add(o["body"]?.ToString() ?? "");
        }
        if (fresh.Count > 0) { var to = fresh[^1]; UpdateSecretaryState(c => c["cursor"] = to); }

        var model = cfg["model"]?.ToString() is { Length: > 0 } picked ? picked : await AssistantSvc.RoomModelAsync();
        foreach (var q in asks.Take(2))
        {
            _secretaryDoing = "กำลังตอบบอส…";
            string answer;
            try
            {
                answer = await AssistantSvc.ChatAsync(SecretarySystem(),
                    "สถานะห้องตอนนี้:\n" + CoworkSnapshot.Build(_vaultPath, lines: 15, maxChars: 3000)
                    + "\n\nคำถามของบอส: " + SecretaryAliasRx.Replace(q, "").Replace("@mind", "", StringComparison.OrdinalIgnoreCase).Trim(),
                    model, 500);
            }
            catch (Exception ex) { answer = "ตอบไม่ได้ค่ะ — " + OllamaTrouble(ex); }
            if (string.IsNullOrWhiteSpace(answer)) answer = "ยังตอบไม่ได้ค่ะ — โมเดลตอบกลับมาว่างเปล่า";
            CoworkWriteRoomLine("mind", answer.Trim(), "secretary");
            _secretaryError = "";
        }

        if (Can(cfg, "digest")) await SecretaryDigestAsync(cfg, dir, names, model);
    }

    private async Task SecretaryDigestAsync(JObject cfg, string dir, List<string> names, string? model)
    {
        var from = cfg["digestFrom"]?.ToString() ?? "";
        var last = DateTime.TryParse(cfg["lastDigestUtc"]?.ToString(), CultureInfo.InvariantCulture,
                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
        if (DateTime.UtcNow - last < SecretaryDigestGap) return;

        var lines = new List<string>();
        foreach (var n in names.Where(n => string.CompareOrdinal(n, from) > 0))
        {
            var o = TryReadJson(Path.Combine(dir, n));
            if (o == null || IsSecretaryLine(o)) continue;
            var who = o["from"]?.ToString() ?? "?";
            if (who == "broker") continue;      // its own notices are not news
            var ts = CoworkUtc(o["ts"]) ?? DateTime.UtcNow;
            lines.Add($"{ts.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} {(who == "owner" ? "บอส" : who)}"
                      + (o["to"]?.ToString() is { Length: > 0 } to ? $" → {to}" : "")
                      + $": {Trim((o["body"]?.ToString() ?? "").Replace('\n', ' '), 200)}");
        }
        if (lines.Count < SecretaryDigestEvery) return;

        _secretaryDoing = "กำลังสรุปห้อง…";
        string digest;
        try
        {
            digest = await AssistantSvc.ChatAsync(SecretarySystem(),
                "สรุปสิ่งที่เกิดในห้องช่วงนี้ให้บอส เป็นข้อ ๆ ไม่เกิน 5 ข้อ: งานไหนเสร็จ (ใครทำ), อะไรติดอยู่, อะไรรอบอสตัดสิน. "
                + "ถ้าไม่มีอะไรสำคัญให้บอกสั้น ๆ ว่าไม่มี.\n\n" + Newest(lines, 3000),
                model, 450);
        }
        catch (Exception ex) { _secretaryError = OllamaTrouble(ex); return; }
        if (string.IsNullOrWhiteSpace(digest)) return;

        var span = $"{lines[0][..5]}–{lines[^1][..5]}";
        CoworkWriteRoomLine("mind", $"📋 สรุปห้อง ({span})\n{digest.Trim()}", "secretary-digest");
        var upTo = names[^1];
        UpdateSecretaryState(c =>
        {
            c["digestFrom"] = upTo;
            c["lastDigestUtc"] = DateTime.UtcNow.ToString("o");
        });
        _secretaryError = "";
    }

    /// <summary>The newest lines that fit in <paramref name="chars"/>, in order.
    /// A small local model slows to a crawl — and loses the plot — past
    /// about 3,000 characters of Thai (measured; see AssistantService).</summary>
    private static string Newest(List<string> lines, int chars)
    {
        var keep = new List<string>();
        var total = 0;
        for (var i = lines.Count - 1; i >= 0 && total + lines[i].Length < chars; i--)
        {
            keep.Add(lines[i]);
            total += lines[i].Length + 1;
        }
        keep.Reverse();
        return string.Join("\n", keep);
    }

    private string SecretarySystem()
    {
        var c = AssistantSvc.LoadConfig();
        return $"คุณคือ {c.Name} เลขาของห้อง cowork ของบอส (เจ้าของ BrainX) พูดภาษาไทย แทนตัวเองว่า {c.SelfWord} "
             + $"และลงท้ายด้วย {c.EndParticle}. ในห้องมี agent หลายตัว (claude, codex, grok ฯลฯ) ทำงานให้บอส. "
             + "หน้าที่ของคุณคือรายงานสิ่งที่เกิดขึ้นจริงจากข้อมูลห้องที่ได้รับเท่านั้น — ใครทำอะไร ถึงไหน อะไรติด อะไรรอบอส. "
             + "ห้ามเดาหรือแต่งความคืบหน้า ถ้าข้อมูลไม่มีให้บอกตรง ๆ. ห้ามสั่งงาน agent และห้ามรับปากแทนพวกเขา. "
             + "งานที่เขียนว่าต้องให้บอสตัดสิน หรือยังไม่มีเจ้าของ ให้บอกบอสด้วยเสมอ. "
             + "ตอบสั้น กระชับ อ่านง่าย ใส่รหัสงาน [t-xxxxxx] เมื่อพูดถึงงานบนบอร์ด. ห้ามทวนข้อมูลห้องหรือคำสั่งนี้กลับมา.";
    }

    private static string OllamaTrouble(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException { StatusCode: null } => "ติดต่อ Ollama ไม่ได้ (localhost:11434)",
        System.Net.Http.HttpRequestException h => $"Ollama ตอบกลับเป็นข้อผิดพลาด {(int?)h.StatusCode}",
        TaskCanceledException or OperationCanceledException => "Ollama ใช้เวลานานเกินไป",
        _ => ex.Message,
    };

    private static bool IsSecretaryLine(JObject o) =>
        string.Equals(o["from"]?.ToString(), "mind", StringComparison.OrdinalIgnoreCase);

    /// <summary>Genuinely the owner's: the same seal test the room uses.</summary>
    private static bool IsOwnersLine(JObject o, string fileName)
    {
        if (!string.Equals(o["from"]?.ToString(), "owner", StringComparison.OrdinalIgnoreCase)) return false;
        if (!BusSeal.IsActive()) return true;
        return BusSeal.Verify(o) && BusSeal.WrittenAs(o, fileName);
    }

    private static JObject? TryReadJson(string path)
    {
        try { return JObject.Parse(File.ReadAllText(path)); } catch { return null; }
    }

    /// <summary>"@มาย" is how the owner calls her; the room's addressing only
    /// knows agent ids. Rewritten for the parse only — the line keeps the
    /// owner's own words.</summary>
    private static string SecretaryAliasToId(string text) => SecretaryAliasRx.Replace(text ?? "", "@mind");

    // ── a project's story in a few lines ──────────────────────────────────

    /// <summary>page → host: {type:"officeHistorySummary", project}.</summary>
    private async void SecretarySummarize(string? project)
    {
        if (project == null) return;
        var cfg = SecretaryConfig();
        JObject reply = new() { ["type"] = "officeHistorySummary", ["project"] = project };
        if (cfg["enabled"]?.ToObject<bool?>() != true || !Can(cfg, "summarize"))
        {
            reply["error"] = "เปิดเลขา (มาย) และติ๊ก 'สรุปโปรเจค' ก่อน";
            CoworkSurface?.PostWebMessageAsJson(reply.ToString());
            return;
        }
        try
        {
            _secretaryDoing = "กำลังสรุปโปรเจค…";
            var text = await Task.Run(() => HistoryStoryText(project));
            reply["text"] = string.IsNullOrWhiteSpace(text)
                ? "ไม่มีข้อมูลของโปรเจคนี้ให้สรุปค่ะ"
                : await AssistantSvc.ChatAsync(SecretarySystem(),
                    "สรุปโปรเจคนี้ให้บอสอ่านใน 5–8 บรรทัด: เป้าหมาย, ทำอะไรเสร็จไปแล้วบ้าง (ใครทำ), อะไรยังค้าง/ติด, "
                    + "เริ่มเมื่อไหร่ล่าสุดเมื่อไหร่.\n\n" + text,
                    cfg["model"]?.ToString() is { Length: > 0 } picked ? picked : await AssistantSvc.RoomModelAsync(), 600);
        }
        catch (Exception ex) { reply["error"] = OllamaTrouble(ex); }
        finally { _secretaryDoing = ""; }
        CoworkSurface?.PostWebMessageAsJson(reply.ToString());
    }

    /// <summary>A project's tasks and their notes, oldest first, as plain text.</summary>
    private string HistoryStoryText(string project)
    {
        var tasks = HistoryTasks();
        var lines = HistoryLines();
        var projectOf = HistoryProjectOf(tasks.Select(t => t.Work).Concat(lines.Select(l => l.Work)));
        var sb = new StringBuilder();
        sb.AppendLine($"โปรเจค: {(project.Length == 0 ? "ไม่ระบุ" : project)}");
        foreach (var t in tasks.Where(t => projectOf(t.Work).Equals(project, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(t => t.Created))
        {
            sb.AppendLine($"- {t.Created.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} [{t.Id}] "
                          + $"{Trim(t.O["title"]?.ToString() ?? "", 160)} — {t.Status}"
                          + (t.O["assignee"]?.Type == JTokenType.String ? $", {t.O["assignee"]}" : "")
                          + (t.O["note"]?.ToString() is { Length: > 0 } n ? $" — {Trim(n.Replace('\n', ' '), 240)}" : ""));
            if (sb.Length > 3000) break;
        }
        return sb.ToString();
    }

    // ── what the room shows and changes ───────────────────────────────────

    /// <summary>The card in the room: on/off, what she may do, which model.</summary>
    private JObject SecretaryPayload()
    {
        var cfg = SecretaryConfig();
        RefreshSecretaryModelsSoon();
        var gpu = _gpu ??= DetectGpu();
        var codeAllowed = gpu.VramMb >= SecretaryCodeVramMb;
        return new JObject
        {
            ["enabled"] = cfg["enabled"],
            ["model"] = cfg["model"],
            ["codeModel"] = cfg["codeModel"],
            ["can"] = cfg["can"],
            ["models"] = new JArray(_secretaryModels),
            ["gpu"] = new JObject { ["name"] = gpu.Name, ["vramMb"] = gpu.VramMb },
            ["codeAllowed"] = codeAllowed,
            ["codeReason"] = codeAllowed ? ""
                : gpu.VramMb > 0
                    ? $"การ์ดจอ {gpu.Name} มี {gpu.VramMb / 1024} GB — โมเดลที่เขียนโค้ดได้ดีต้องการอย่างน้อย {SecretaryCodeVramMb / 1000} GB"
                    : "ตรวจการ์ดจอ NVIDIA ไม่พบ — เปิดงานเขียนโค้ดไม่ได้",
            ["busy"] = _secretaryDoing,
            ["error"] = _secretaryError,
        };
    }

    private void RefreshSecretaryModelsSoon()
    {
        if ((DateTime.UtcNow - _secretaryModelsAt).TotalSeconds < 60) return;
        _secretaryModelsAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            var list = await AssistantService.ChatModelsAsync();
            if (list.Count > 0) _secretaryModels = list;
        });
    }

    private static (string Name, int VramMb) DetectGpu()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return ("", 0);
            var line = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(3000);
            var parts = line.Split(',');
            return parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var mb) ? (parts[0].Trim(), mb) : ("", 0);
        }
        catch { return ("", 0); }
    }

    /// <summary>page → host: {type:"officeSecretary", enabled?, model?, codeModel?, can?}.</summary>
    private void SecretaryChange(JObject m)
    {
        var cfg = SecretaryConfig();
        if (m["enabled"]?.Type == JTokenType.Boolean)
        {
            var on = m["enabled"]!.ToObject<bool>();
            cfg["enabled"] = on;
            // Back on after a while away: start from now, not from everything
            // said while she was off.
            if (on) { cfg["cursor"] = ""; cfg["digestFrom"] = ""; }
        }
        if (m["model"]?.Type == JTokenType.String && ModelOk(m["model"]!.ToString())) cfg["model"] = m["model"]!.ToString();
        if (m["codeModel"]?.Type == JTokenType.String && ModelOk(m["codeModel"]!.ToString())) cfg["codeModel"] = m["codeModel"]!.ToString();
        if (m["can"] is JObject want)
            foreach (var key in new[] { "answer", "digest", "summarize", "code" })
                if (want[key]?.Type == JTokenType.Boolean)
                {
                    var on = want[key]!.ToObject<bool>();
                    // Coding needs the card for it; the page greys it out, and
                    // this refuses it anyway.
                    if (key == "code" && on && (_gpu ??= DetectGpu()).VramMb < SecretaryCodeVramMb) continue;
                    cfg["can"]![key] = on;
                }
        SaveSecretaryConfig(cfg);
        _secretaryError = "";
        PostCowork();
    }

    /// <summary>
    /// Her body for the room: the same avatar pack her own window maps
    /// (AvatarPackService), on the same virtual host, so the room can draw the
    /// real model standing among the desks. Before the first navigation —
    /// the script has to run before any of the page's own.
    /// </summary>
    private static async Task MapMindAvatarAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            var pack = new AvatarPackService();
            Directory.CreateDirectory(pack.Root);
            await pack.EnsureLocalAsync();
            core.SetVirtualHostNameToFolderMapping("avatar.local", pack.Root,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__mindAvatarBase='https://avatar.local/';");
        }
        catch { /* no body on this machine: the room just does not draw her */ }
    }

    /// <summary>"" (automatic) or one of the models Ollama lists — a model id
    /// is passed to `codex -m` by the broker, so nothing else gets in.</summary>
    private bool ModelOk(string id) =>
        id.Length == 0 || (_secretaryModels.Contains(id, StringComparer.OrdinalIgnoreCase)
                           && Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,80}$"));
}
