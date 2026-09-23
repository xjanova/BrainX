using System.Globalization;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// The findability canary: every note written in the last three days must come
/// back in brain_recall's top three when asked for by its own title.
///
/// From 2026-09-16 to 09-23 every health signal the brain had said "fine" —
/// garden "0 embeddings written", health 0.984, audit "nothing to do" — while no
/// new note got a vector and brain_recall ranked a month-old incident over a
/// two-day-old note that matched the question almost word for word. The counts
/// were all true and none of them was the question the owner relies on: can I
/// find what was just written? This asks exactly that, end to end through the
/// ranking an agent gets, and says so at SessionStart and in brain_stats when
/// the answer is no — on the first day, not the seventh.
/// </summary>
internal static partial class Program
{
    private const int CanaryNotes = 8;
    private const int CanaryTop = 3;
    private static readonly TimeSpan CanaryWindow = TimeSpan.FromHours(72);
    private static readonly TimeSpan CanaryEvery = TimeSpan.FromHours(12);

    private static string CanaryPath(string vault) => Path.Combine(vault, ".obsidianx", "findability.json");

    /// <summary>
    /// Run the canary in the background when the last result is older than
    /// <see cref="CanaryEvery"/>. One process per vault: six sessions starting
    /// in the same minute must not all embed the same eight titles.
    /// </summary>
    private static void ScheduleCanary()
    {
        if (Environment.GetEnvironmentVariable("BRAINX_CANARY") == "0") return;
        _ = Task.Run(async () =>
        {
            // After the handshake and the embed warm-up, not in their way.
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            try
            {
                if (ReadCanary(_vaultPath)?["checkedAt"]?.Value<DateTime?>() is DateTime at
                    && DateTime.UtcNow - at.ToUniversalTime() < CanaryEvery) return;
                // Taken and released on this thread: RunCanary is synchronous,
                // and a Mutex belongs to the thread that took it.
                var gate = AcquireVaultLock(0, "canary");
                if (gate == null) return;
                try { WriteCanary(_vaultPath, RunCanary()); }
                finally { ReleaseVaultLock(gate); }
            }
            catch (Exception ex) { Log($"canary: {ex.GetType().Name}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// The check itself. Picks the most recently written notes whose title can
    /// stand as a query — unique in the vault and long enough to mean
    /// something; "README" asks for one of forty — and ranks each title with the
    /// same <see cref="HybridRank"/> brain_recall and brain_semantic_search use.
    /// </summary>
    internal static JObject RunCanary()
    {
        BrainExport? loaded;
        lock (_requestGate) loaded = LoadExport();
        var export = loaded ?? throw new InvalidOperationException("no brain-export");
        var titleCount = export.Nodes
            .GroupBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var since = DateTime.UtcNow - CanaryWindow;
        var picks = export.Nodes
            .Where(n => n.ModifiedAt.ToUniversalTime() >= since
                        && n.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        && n.Kind != "instructions"
                        && n.Title.Length >= 12
                        && titleCount[n.Title] == 1
                        // Reports BrainX rewrites itself (source: brainx-eval,
                        // brainx-garden): a table of numbers is looked up by its
                        // name — the exact-title guard's job — not found by what
                        // it says, and every eval run makes one "newest".
                        && !FrontmatterRefs(n, "source").Any(s => s.StartsWith("brainx-", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(n => n.ModifiedAt)
            .Take(CanaryNotes)
            .ToList();

        var all = export.Nodes.ToList();
        var embeddings = Path.Combine(export.VaultPath, ".obsidianx", "embeddings");
        var rows = new JArray();
        int found = 0, semantic = 0;
        foreach (var n in picks)
        {
            // The embed is a network call and needs no lock; the ranking reads
            // the caches a live request may be writing, so it takes the gate.
            var vec = EmbedQuery(n.Title);
            if (vec != null) semantic++;
            // Without the exact-title guard: with it, every title lookup wins by
            // construction and this would stop measuring the ranking at all.
            List<(NodeSummary Node, double Score)> ranked;
            lock (_requestGate) ranked = HybridRank(export, all, n.Title.ToLowerInvariant(), 10, vec, exactTitle: false).Ranked;
            var rank = ranked.FindIndex(r => r.Node.Id == n.Id) + 1;
            var ok = rank is >= 1 and <= CanaryTop;
            if (ok) found++;
            rows.Add(new JObject
            {
                ["id"] = n.Id,
                ["title"] = n.Title,
                ["rank"] = rank == 0 ? null : rank,
                ["hasVector"] = File.Exists(Path.Combine(embeddings, n.Id + ".bin")),
                ["ok"] = ok
            });
        }

        string? problem = null;
        var lost = picks.Count - found;
        if (lost > 0)
            problem = $"{lost} of {picks.Count} notes written in the last {CanaryWindow.TotalHours:0} h do not come back "
                    + $"in brain_recall's top {CanaryTop} when asked for by their own title";
        // Found by keyword alone is not the pipeline working: with no query
        // vector the ranking IS the keyword ranking, and a stalled semantic side
        // would pass unnoticed — the exact shape of the week this exists for.
        if (picks.Count > 0 && semantic == 0)
            problem = (problem == null ? "" : problem + "; ")
                    + "no query could be embedded, so semantic search is down and this was keyword-only";
        var missingVectors = rows.Count(r => r["hasVector"]?.Value<bool>() == false);
        if (problem != null && missingVectors > 0)
            problem += $" — {missingVectors} of them have no vector (see brain_stats.embeddings)";
        // A note an hour old with no vector means nothing is embedding — a
        // write embeds its own note within seconds, the Garden the rest. On
        // its own this is the stall of 2026-09-16, even while titles still rank.
        var stranded = picks.Count(n => DateTime.UtcNow - n.ModifiedAt.ToUniversalTime() > TimeSpan.FromHours(1)
                                        && Directory.Exists(embeddings)
                                        && !File.Exists(Path.Combine(embeddings, n.Id + ".bin")));
        if (problem == null && stranded > 0)
            problem = $"{stranded} of the {picks.Count} newest notes are over an hour old and still have no vector — "
                    + "nothing is embedding (see brain_stats.embeddings)";

        return new JObject
        {
            ["checkedAt"] = DateTime.UtcNow,
            ["windowHours"] = (int)CanaryWindow.TotalHours,
            ["checked"] = picks.Count,
            ["found"] = found,
            ["queriesEmbedded"] = semantic,
            // JValue.CreateNull, not the string: a null string assigned here is a
            // String-typed JValue holding null, which reads as "has a problem".
            ["problem"] = problem == null ? JValue.CreateNull() : problem,
            ["notes"] = rows
        };
    }

    private static JObject? ReadCanary(string vault)
    {
        try
        {
            var p = CanaryPath(vault);
            return File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    private static void WriteCanary(string vault, JObject result)
    {
        var p = CanaryPath(vault);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        AtomicWrite(p, result.ToString());
    }

    /// <summary>brain_stats' view of the last canary run: the result, and whether it is too old to trust.</summary>
    private static JObject? CanaryForStats()
    {
        var last = ReadCanary(_vaultPath);
        if (last == null) return null;
        var at = last["checkedAt"]?.Value<DateTime?>()?.ToUniversalTime();
        var summary = new JObject
        {
            ["checkedAt"] = last["checkedAt"],
            ["checked"] = last["checked"],
            ["found"] = last["found"],
            ["problem"] = last["problem"],
        };
        if (at is DateTime t && DateTime.UtcNow - t > CanaryEvery * 3)
            summary["stale"] = $"last run {(DateTime.UtcNow - t).TotalHours.ToString("0", CultureInfo.InvariantCulture)} h ago — no MCP session has run it since";
        if (last.Value<string?>("problem") is { Length: > 0 })
            summary["notes"] = last["notes"];
        return summary;
    }

    /// <summary>`brainx-mcp canary [--vault PATH]` — run it now, print it, record it.</summary>
    internal static int CanaryCli(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) _vaultPath = Path.GetFullPath(args[++i]);
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp canary [--vault PATH]");
                Console.WriteLine();
                Console.WriteLine($"Asks for each of the {CanaryNotes} most recently written notes by its own title, through");
                Console.WriteLine($"the ranking brain_recall uses, and checks it comes back in the top {CanaryTop}. Writes");
                Console.WriteLine(".obsidianx/findability.json, which brain_stats and the SessionStart hook read.");
                Console.WriteLine("MCP sessions run this on their own every 12 hours; this runs it now.");
                return 0;
            }
        }
        var result = RunCanary();
        WriteCanary(_vaultPath, result);
        foreach (var n in result["notes"] as JArray ?? new JArray())
            Console.WriteLine($"  {(n["ok"]?.Value<bool>() == true ? "ok  " : "LOST")}  rank {n["rank"]?.ToString() ?? "—",-3} "
                            + $"{(n["hasVector"]?.Value<bool>() == true ? "  " : "no vector  ")}{n["title"]}");
        Console.WriteLine();
        Console.WriteLine($"found {result["found"]} of {result["checked"]} by their own title "
                        + $"({result["queriesEmbedded"]} queries embedded)");
        var problem = result.Value<string?>("problem");
        if (!string.IsNullOrEmpty(problem)) Console.WriteLine("PROBLEM: " + problem);
        return string.IsNullOrEmpty(problem) ? 0 : 1;
    }
}
