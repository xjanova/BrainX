using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// Each agent's quota, as HP.
//
// Owner (2026-10-06): "ให้โชว์ user คงเหลือเป็น หลอด HP ของ อวต้าแต่ละตัว ด้วย
// คือโควต้าคือ HP ใกล้หมด ก็แดง เปลี่ยนสีตามด้วย บอกเป็นเปอร์เซ็นในห้อง มุมขวาบน".
//
// HP is what is LEFT of each agent's WEEKLY limit. Owner (2026-10-06): "หลอด
// hp ใช้ โควต้าลิมิต รายสัปดาห์ทั้งหมดนะ" — the same yardstick for everyone,
// and the one that says how much of the week's work is left; the five-hour
// window refills on its own. Then: "Hp เป็นรายสัปดาห์ SP คือสตามิน่า ราย
// ชั่วโมงรอบ" — that round is the second bar, SP (stamina), under the HP.
// Read from what each vendor's own tools already record:
//
//   claude  claude.ai's usage page, scraped by ClaudeUsageProbe every minute
//           when BrainX is signed in there: weekly (all models) = HP, the
//           5-hour session = SP.
//   codex   the `rate_limits` codex writes into its own session rollouts
//           (~/.codex/sessions/**/rollout-*.jsonl) on every turn: the
//           10080-minute window = HP, the 300-minute one = SP.
//   grok    the credits config Grok Build fetches for its own prompt footer
//           ("Weekly limit left: 7%") and logs to ~/.grok/logs/unified.jsonl
//           as "billing: fetched credits config" — creditUsagePercent of the
//           weekly period = HP. Read from the log, never with Grok's login.
//           Grok publishes no shorter round, so it has no SP bar.
//   mind    มาย is a LOCAL AI (Ollama on this machine): no vendor, no
//           quota — a full bar marked unlimited, never a "?".
//   anyone  resting on the broker's record (quotaResumeUtc in the future)
//           keeps its weekly HP and carries the time it is back as a note.
//
// CluadeX is a tool BrainX drives, not an AI (owner, 2026-10-06: "cluadex
// ไม่ใช่ ai แต่เป็นเครื่องมือ"), so it has no HP frame at all.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private (string Path, DateTime Stamp, JObject? Limits) _codexLimitsCache;
    private DateTime _codexLimitsCheckedUtc;
    private long _grokLogPos;
    private JObject? _grokCredits;
    private DateTime _grokCreditsAt;
    private DateTime _grokCheckedUtc;

    /// <summary>{agent: {left, window, resets, source}} for the room; `left`
    /// null when nothing on this machine says.</summary>
    private JObject CoworkQuota(IEnumerable<string> agents)
    {
        var o = new JObject();
        foreach (var raw in agents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var agent = raw.ToLowerInvariant();
            if (agent == "cluadex") continue;
            JObject? row = null;
            try
            {
                row = agent switch
                {
                    "claude" => ClaudeQuota(),
                    "codex" => CodexQuota(),
                    "grok" => GrokQuota(),
                    "mind" => new JObject { ["left"] = 100, ["unlimited"] = true, ["window"] = "local", ["source"] = "local · Ollama" },
                    _ => null,
                };
            }
            catch { row = null; }

            // The broker stopped it on a usage limit — usually the five-hour
            // one. The weekly HP stays what it is; when it is back is a note.
            if (BrokerRestingUntil(agent) is DateTime until)
            {
                row ??= new JObject { ["left"] = null, ["source"] = "none" };
                row["resting"] = until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            o[agent] = row ?? new JObject { ["left"] = null, ["source"] = "none" };
        }
        return o;
    }

    private JObject? ClaudeQuota()
    {
        var u = _hudUsage;
        if (u == null || !u.Authenticated) return null;
        var row = new JObject { ["left"] = null, ["source"] = "claude.ai" };
        if (u.WeeklyAll is { Percent: >= 0 } week)
        {
            row["left"] = Math.Round(Math.Clamp(100 - week.Percent, 0, 100));
            row["window"] = "สัปดาห์";
            row["resets"] = ResetsOnly(week.ResetLabel);
        }
        if (u.Session is { Percent: >= 0 } session)
            row["sp"] = new JObject
            {
                ["left"] = Math.Round(Math.Clamp(100 - session.Percent, 0, 100)),
                ["window"] = "5 ชม.",
                ["resets"] = ResetsOnly(session.ResetLabel),
            };
        row["asOf"] = u.FetchedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        return row["left"]!.Type == JTokenType.Null && row["sp"] == null ? null : row;
    }

    /// <summary>The dashboard card says "Resets 9 Oct 10:59"; the room
    /// already prints "รีเซ็ต" in front of it.</summary>
    private static string ResetsOnly(string? label) =>
        label is { Length: > 0 } l && l.StartsWith("Resets ", StringComparison.OrdinalIgnoreCase) ? l[7..] : label ?? "";

    /// <summary>The rate limits codex recorded last — read off the newest
    /// rollout that has them, re-read only when that file changes and at most
    /// every 30 s (rollouts run to megabytes).</summary>
    private JObject? CodexQuota()
    {
        if ((DateTime.UtcNow - _codexLimitsCheckedUtc).TotalSeconds >= 30)
        {
            _codexLimitsCheckedUtc = DateTime.UtcNow;
            try { RefreshCodexLimits(); } catch { }
        }
        var lim = _codexLimitsCache.Limits;
        if (lim == null) return null;

        // Each window, whichever slot codex put it in: the 10080-minute one
        // is the week (HP), the shorter one the round (SP).
        JObject? week = null, round = null;
        foreach (var key in new[] { "primary", "secondary" })
            if (lim[key] is JObject w)
            {
                if ((w["window_minutes"]?.ToObject<int?>() ?? 0) >= 10080) week ??= w;
                else round ??= w;
            }

        var row = new JObject { ["left"] = null, ["source"] = "codex" };
        if (CodexWindowLeft(week) is (double weekLeft, string weekResets))
        {
            row["left"] = weekLeft;
            row["window"] = "สัปดาห์";
            row["resets"] = weekResets;
        }
        if (CodexWindowLeft(round) is (double roundLeft, string roundResets))
        {
            var mins = round!["window_minutes"]?.ToObject<int?>() ?? 300;
            row["sp"] = new JObject
            {
                ["left"] = roundLeft,
                ["window"] = mins >= 60 ? $"{mins / 60} ชม." : $"{mins} นาที",
                ["resets"] = roundResets,
            };
        }
        return row["left"]!.Type == JTokenType.Null && row["sp"] == null ? null : row;
    }

    /// <summary>What is left of one codex window. Codex only writes these on
    /// a turn, so a window whose reset time has passed since is full again —
    /// not stuck at whatever it was when codex last ran.</summary>
    private static (double Left, string Resets)? CodexWindowLeft(JObject? w)
    {
        if (w?["used_percent"]?.ToObject<double?>() is not double used) return null;
        var at = w["resets_at"]?.ToObject<long?>();
        if (at is long t && DateTimeOffset.FromUnixTimeSeconds(t) <= DateTimeOffset.UtcNow) return (100, "");
        return (Math.Round(Math.Clamp(100 - used, 0, 100)),
                at is long r ? DateTimeOffset.FromUnixTimeSeconds(r).LocalDateTime.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : "");
    }

    private void RefreshCodexLimits()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var root = Path.Combine(home, "sessions");
        if (!Directory.Exists(root)) return;

        // Newest day folders first (yyyy/MM/dd sorts as text), then newest files.
        var files = Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Take(2)
            .SelectMany(y => Directory.EnumerateDirectories(y).OrderByDescending(d => d, StringComparer.Ordinal).Take(2))
            .SelectMany(m => Directory.EnumerateDirectories(m).OrderByDescending(d => d, StringComparer.Ordinal).Take(3))
            .SelectMany(d => Directory.EnumerateFiles(d, "rollout-*.jsonl"))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(6);

        foreach (var fi in files)
        {
            if (_codexLimitsCache.Path == fi.FullName && _codexLimitsCache.Stamp == fi.LastWriteTimeUtc && _codexLimitsCache.Limits != null)
                return;
            var limits = LastRateLimits(fi.FullName);
            if (limits == null) continue;
            _codexLimitsCache = (fi.FullName, fi.LastWriteTimeUtc, limits);
            return;
        }
    }

    /// <summary>The last `rate_limits` object in a rollout, read from the tail.</summary>
    private static JObject? LastRateLimits(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var take = (int)Math.Min(fs.Length, 512 * 1024);
        fs.Seek(-take, SeekOrigin.End);
        var buf = new byte[take];
        var read = 0;
        while (read < take && fs.Read(buf, read, take - read) is var n && n > 0) read += n;
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);
        foreach (var line in text.Split('\n').Reverse())
        {
            if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
            try
            {
                var o = JObject.Parse(line);
                var rl = o.SelectToken("$..rate_limits") as JObject;
                if (rl?["primary"] is JObject) return rl;
            }
            catch { /* the first line of the tail is cut in half */ }
        }
        return null;
    }

    /// <summary>What Grok Build last said is left of its weekly credits.
    /// Its log only grows, so each look reads just what was added since the
    /// last one; a log that shrank was rotated and is read again from the top.</summary>
    private JObject? GrokQuota()
    {
        if (!_grokCacheLoaded) LoadGrokCreditsCache();
        if ((DateTime.UtcNow - _grokCheckedUtc).TotalSeconds >= 30)
        {
            _grokCheckedUtc = DateTime.UtcNow;
            try { RefreshGrokCredits(); } catch { }
            try { MaybeProbeGrok(); } catch { }
        }
        var c = _grokCredits;
        if (c == null) return null;
        // Grok writes the config as proto3 JSON, which leaves out a field that
        // is zero: an account that has used nothing this week has NO
        // creditUsagePercent at all (the fresh login of 2026-10-06 read "?"
        // all afternoon). A period with no figure is a period with 0% used.
        double used;
        if (c["creditUsagePercent"]?.ToObject<double?>() is double u) used = u;
        else if (c["currentPeriod"] is JObject) used = 0;
        else return null;

        var end = CoworkUtc(c.SelectToken("currentPeriod.end") ?? c["billingPeriodEnd"]);
        // The period it describes is over: the allowance has reset since, and
        // what it is now nobody has asked Grok yet.
        if (end is DateTime e && e <= DateTime.UtcNow) return null;
        var weekly = (c.SelectToken("currentPeriod.type")?.ToString() ?? "").Contains("WEEK", StringComparison.OrdinalIgnoreCase);
        return new JObject
        {
            ["left"] = Math.Round(Math.Clamp(100 - used, 0, 100)),
            ["window"] = weekly ? "สัปดาห์" : "รอบบิล",
            ["resets"] = end is DateTime r ? r.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : "",
            ["asOf"] = _grokCreditsAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
            ["source"] = "grok",
        };
    }

    private void RefreshGrokCredits()
    {
        var home = Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } h
            ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        var log = Path.Combine(home, "logs", "unified.jsonl");
        if (!File.Exists(log)) return;

        using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _grokLogPos) _grokLogPos = 0;
        // A first look at a long log only needs its recent end.
        if (fs.Length - _grokLogPos > 8 * 1024 * 1024) _grokLogPos = fs.Length - 8 * 1024 * 1024;
        if (fs.Length == _grokLogPos) return;

        fs.Seek(_grokLogPos, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _grokLogPos];
        var read = 0;
        while (read < buf.Length && fs.Read(buf, read, buf.Length - read) is var n && n > 0) read += n;
        // Complete lines only: the last one may still be being written, and
        // is read whole next time instead of being skipped half-way through.
        if (read == 0) return;
        var lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
        if (lastNl < 0) return;
        var text = System.Text.Encoding.UTF8.GetString(buf, 0, lastNl);
        _grokLogPos += lastNl + 1;

        var changed = false;
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("billing: fetched credits config", StringComparison.Ordinal)) continue;
            try
            {
                var o = JObject.Parse(line);
                if (o.SelectToken("ctx.config") is JObject cfg)
                {
                    var at = CoworkUtc(o["ts"]) ?? DateTime.UtcNow;
                    // Never let an older line (re-read after a rotation) win
                    // over a newer figure restored from the cache.
                    if (_grokCredits != null && at < _grokCreditsAt) continue;
                    _grokCredits = cfg;
                    _grokCreditsAt = at;
                    changed = true;
                }
            }
            catch { /* the first line after a jump into the middle is cut in half */ }
        }
        if (changed) SaveGrokCreditsCache();
    }

    // ── The figure outlives the log, and BrainX ───────────────────────────
    //
    // The log is Grok's only report, and it forgets. Grok writes the figure
    // when its TUI starts and never on a headless `grok -p` run, and the log
    // rolls: on 2026-10-10 it held four days of the broker's headless runs and
    // no figure at all. The value lived only in this process, so every BrainX
    // restart (and every update is one, several a day) blanked Grok's HP until
    // the owner happened to open Grok ("Brianx Grok ไม่เห็น hp"). On disk, the
    // last figure survives both; GrokQuota still drops it once its period ends,
    // and `asOf` tells the room how old it is.
    private bool _grokCacheLoaded;

    private static string GrokCreditsCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "grok-credits.json");

    private void LoadGrokCreditsCache()
    {
        _grokCacheLoaded = true;
        try
        {
            if (!File.Exists(GrokCreditsCachePath)) return;
            var o = JObject.Parse(File.ReadAllText(GrokCreditsCachePath));
            if (CoworkUtc(o["probedAt"]) is DateTime probed) _grokProbedUtc = probed;
            if (o["config"] is not JObject cfg) return;
            var at = CoworkUtc(o["at"]) ?? File.GetLastWriteTimeUtc(GrokCreditsCachePath);
            if (_grokCredits != null && at < _grokCreditsAt) return;
            _grokCredits = cfg;
            _grokCreditsAt = at;
        }
        catch { /* a damaged cache is the same as no cache */ }
    }

    private void SaveGrokCreditsCache()
    {
        try
        {
            var o = new JObject
            {
                ["at"] = _grokCreditsAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["probedAt"] = _grokProbedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            };
            if (_grokCredits != null) o["config"] = _grokCredits;
            Directory.CreateDirectory(Path.GetDirectoryName(GrokCreditsCachePath)!);
            File.WriteAllText(GrokCreditsCachePath, o.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch { /* HP is a nicety; never let it throw */ }
    }

    // ── Asking Grok, when nobody has for a while ──────────────────────────
    //
    // The cache keeps the last figure; it cannot make it current. The only way
    // to a fresh one without Grok's login token is to let Grok fetch it
    // itself, which its TUI does the moment it starts (measured 2026-10-10:
    // the line was in the log within a second of a hidden start). So when the
    // figure is older than six hours, or its period has ended, start the TUI
    // in a hidden window, wait for the line, and close it. No prompt is ever
    // sent, so it costs no quota. It runs at most once per six hours, a limit
    // that survives restarts via `probedAt` in the cache, and from its own
    // empty folder, so the session folder Grok makes for it stays in one
    // place. Owner approved this on 2026-10-10 ("ทำเลย").
    private static readonly TimeSpan GrokProbeEvery = TimeSpan.FromHours(6);
    private DateTime _grokProbedUtc;
    private static int _grokProbeRunning;

    private void MaybeProbeGrok()
    {
        var now = DateTime.UtcNow;
        var periodOver = CoworkUtc(_grokCredits?.SelectToken("currentPeriod.end")) is DateTime end && end <= now;
        var fresh = _grokCredits != null && now - _grokCreditsAt < GrokProbeEvery && !periodOver;
        if (fresh || now - _grokProbedUtc < GrokProbeEvery) return;

        var home = Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } h
            ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        var exe = Path.Combine(home, "bin", "grok.exe");
        if (!File.Exists(exe)) return;   // Grok is not installed here
        if (Interlocked.Exchange(ref _grokProbeRunning, 1) == 1) return;

        _grokProbedUtc = now;
        SaveGrokCreditsCache();
        var log = Path.Combine(home, "logs", "unified.jsonl");
        _ = Task.Run(() =>
        {
            try { RunGrokProbe(exe, log); }
            catch { /* a probe that fails just leaves the old figure */ }
            finally { Interlocked.Exchange(ref _grokProbeRunning, 0); }
        });
    }

    /// <summary>Start Grok's TUI hidden, wait (up to 25 s) for it to log its
    /// credits config, then close it and everything it started. The next
    /// GrokQuota look reads the new line the normal way.</summary>
    private static void RunGrokProbe(string exe, string log)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "grok-probe");
        Directory.CreateDirectory(dir);
        long startLen = File.Exists(log) ? new FileInfo(log).Length : 0;

        // ShellExecute with a hidden window, not CreateNoWindow: the TUI needs
        // a real console, it just must not be seen.
        using var p = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dir,
        });
        if (p == null) return;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < until && !p.HasExited)
            {
                Thread.Sleep(1000);
                if (LogHasCreditsSince(log, startLen)) break;
            }
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static bool LogHasCreditsSince(string log, long from)
    {
        try
        {
            if (!File.Exists(log)) return false;
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= from) return false;
            fs.Seek(from, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            return sr.ReadToEnd().Contains("billing: fetched credits config", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>When the broker paused this agent on a usage limit, if it is
    /// still resting.</summary>
    private DateTime? BrokerRestingUntil(string agent)
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "broker", agent + ".state.json");
            if (!File.Exists(p)) return null;
            var o = JObject.Parse(File.ReadAllText(p));
            return CoworkUtc(o["quotaResumeUtc"]) is DateTime until && until > DateTime.UtcNow ? until : null;
        }
        catch { return null; }
    }
}
