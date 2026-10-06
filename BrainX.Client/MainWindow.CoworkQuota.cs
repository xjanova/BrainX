using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// Each agent's quota, as HP.
//
// Owner (2026-10-06): "ให้โชว์ user คงเหลือเป็น หลอด HP ของ อวต้าแต่ละตัว ด้วย
// คือโควต้าคือ HP ใกล้หมด ก็แดง เปลี่ยนสีตามด้วย บอกเป็นเปอร์เซ็นในห้อง มุมขวาบน".
//
// HP is what is LEFT of the tightest limit the agent is under right now —
// a fresh weekly allowance means nothing while the five-hour window is spent.
// Read from what each vendor's own tools already record, never guessed:
//
//   claude  claude.ai's usage page, scraped by ClaudeUsageProbe (5-hour
//           session + weekly), when BrainX is signed in there.
//   codex   the `rate_limits` codex writes into its own session rollouts
//           (~/.codex/sessions/**/rollout-*.jsonl) on every turn.
//   anyone  out of quota by the broker's own record (quotaResumeUtc in the
//           future) is at 0 until then, whatever else says.
//   grok    keeps nothing on disk to read — shown as unknown, not as full.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private (string Path, DateTime Stamp, JObject? Limits) _codexLimitsCache;
    private DateTime _codexLimitsCheckedUtc;

    /// <summary>{agent: {left, window, resets, source}} for the room; `left`
    /// null when nothing on this machine says.</summary>
    private JObject CoworkQuota(IEnumerable<string> agents)
    {
        var o = new JObject();
        foreach (var raw in agents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var agent = raw.ToLowerInvariant();
            JObject? row = null;
            try
            {
                row = agent switch
                {
                    "claude" => ClaudeQuota(),
                    "codex" => CodexQuota(),
                    _ => null,
                };
            }
            catch { row = null; }

            // The broker's own record beats everything: it stopped the agent
            // on a usage limit and knows when it comes back.
            if (BrokerRestingUntil(agent) is DateTime until)
                row = new JObject
                {
                    ["left"] = 0,
                    ["window"] = "หมดโควตา",
                    ["resets"] = until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["source"] = "broker",
                };

            o[agent] = row ?? new JObject { ["left"] = null, ["source"] = "none" };
        }
        return o;
    }

    private JObject? ClaudeQuota()
    {
        var u = _hudUsage;
        if (u == null || !u.Authenticated) return null;
        var rows = new[] { ("5 ชม.", u.Session), ("สัปดาห์", u.WeeklyAll) }
            .Where(r => r.Item2 is { Percent: >= 0 })
            .ToList();
        if (rows.Count == 0) return null;
        var (name, tight) = rows.OrderByDescending(r => r.Item2!.Percent).First();
        return new JObject
        {
            ["left"] = Math.Round(Math.Clamp(100 - tight!.Percent, 0, 100)),
            ["window"] = name,
            ["resets"] = tight.ResetLabel ?? "",
            ["source"] = "claude.ai",
        };
    }

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

        double? used = null; string window = ""; string resets = "";
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (lim[key] is not JObject w || w["used_percent"]?.ToObject<double?>() is not double p) continue;
            if (used is double u && u >= p) continue;
            used = p;
            var mins = w["window_minutes"]?.ToObject<int?>() ?? 0;
            window = mins >= 10080 ? "สัปดาห์" : mins >= 60 ? $"{mins / 60} ชม." : $"{mins} นาที";
            resets = w["resets_at"]?.ToObject<long?>() is long at
                ? DateTimeOffset.FromUnixTimeSeconds(at).LocalDateTime.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)
                : "";
        }
        if (used is not double usedPct) return null;
        // Hit outright: whatever the percentages say, there is nothing left.
        var reached = lim["rate_limit_reached_type"] is JToken r && r.Type != JTokenType.Null;
        return new JObject
        {
            ["left"] = reached ? 0 : Math.Round(Math.Clamp(100 - usedPct, 0, 100)),
            ["window"] = window,
            ["resets"] = resets,
            ["source"] = "codex",
        };
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
