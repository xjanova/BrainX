// ClaudeTranscriptTally — local-only Claude Code usage aggregator.
//
// Reads every assistant message in ~/.claude/projects/**/*.jsonl,
// sums input + output + cache tokens per time window (5 h rolling
// "session", 7 d "weekly", today), groups by model, and pushes a
// TallySnapshot to subscribers every time the on-disk transcripts
// change.
//
// This is the offline half of the dashboard's "CLAUDE USAGE" card.
// The percent bars come from a separate WebView2 probe of
// claude.ai/settings/usage (which alone knows the plan caps);
// these raw counts always work — no auth, no network.
//
// Per-file byte offsets are tracked so a tick that re-fires on a
// 30 MB transcript only reads the newly-appended tail.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BrainX.Client.Services;

public sealed class ClaudeTranscriptTally : IDisposable
{
    public sealed class TallySnapshot
    {
        public long Tokens5h { get; init; }
        public int Messages5h { get; init; }
        public long Tokens24h { get; init; }
        public int Messages24h { get; init; }
        public long Tokens7d { get; init; }
        public int Messages7d { get; init; }
        public long TokensTotal { get; init; }
        public int MessagesTotal { get; init; }
        public Dictionary<string, long> TokensByModel { get; init; } = new();
        public DateTime? LastEventAt { get; init; }
        public DateTime ComputedAt { get; init; } = DateTime.UtcNow;

        public string Tokens5hLabel => FormatTokens(Tokens5h);
        public string Tokens24hLabel => FormatTokens(Tokens24h);
        public string Tokens7dLabel => FormatTokens(Tokens7d);

        private static string FormatTokens(long t) =>
            t switch
            {
                >= 1_000_000_000 => $"{t / 1_000_000_000.0:F1}B",
                >= 1_000_000     => $"{t / 1_000_000.0:F1}M",
                >= 1_000         => $"{t / 1_000.0:F1}k",
                _                => t.ToString("N0"),
            };
    }

    public event EventHandler<TallySnapshot>? Updated;

    private readonly string _projectsRoot;
    private readonly ConcurrentDictionary<string, FileState> _state = new();
    private FileSystemWatcher? _watcher;
    private volatile bool _scanInFlight;

    private sealed class FileState
    {
        public long Offset;
        public long Tokens5h;
        public int Messages5h;
        public long Tokens24h;
        public int Messages24h;
        public long Tokens7d;
        public int Messages7d;
        public long TokensTotal;
        public int MessagesTotal;
        public DateTime? LastEventAt;
        // Per-event timestamps kept so we can age them out of the 5h/24h
        // windows on later ticks without re-reading the whole file.
        public readonly List<(DateTime ts, long tokens)> RecentEvents = new();
        public readonly Dictionary<string, long> TokensByModel = new();
    }

    public ClaudeTranscriptTally(string? projectsRoot = null)
    {
        _projectsRoot = projectsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
    }

    public void Start()
    {
        if (!Directory.Exists(_projectsRoot)) return;

        _ = Task.Run(ScanAllAsync);

        _watcher = new FileSystemWatcher(_projectsRoot)
        {
            IncludeSubdirectories = true,
            Filter = "*.jsonl",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
    }

    private DateTime _lastFsEventAt = DateTime.MinValue;
    private void OnFsEvent(object _, FileSystemEventArgs __)
    {
        // Coalesce rapid bursts — Claude Code appends in chunks, and we
        // don't need sub-second freshness.
        var now = DateTime.UtcNow;
        if (now - _lastFsEventAt < TimeSpan.FromMilliseconds(900)) return;
        _lastFsEventAt = now;
        _ = Task.Run(ScanAllAsync);
    }

    /// <summary>
    /// Public re-scan trigger — useful when the dashboard wakes up after
    /// a long sleep and the file-watcher might have missed updates.
    /// </summary>
    public Task RefreshAsync() => Task.Run(ScanAllAsync);

    private async Task ScanAllAsync()
    {
        if (_scanInFlight) return;
        _scanInFlight = true;
        try
        {
            if (!Directory.Exists(_projectsRoot)) return;
            foreach (var file in Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                try { await ScanFileTailAsync(file).ConfigureAwait(false); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ClaudeTranscriptTally scan {file}: {ex.Message}"); }
            }
            PushSnapshot();
        }
        finally
        {
            _scanInFlight = false;
        }
    }

    private async Task ScanFileTailAsync(string path)
    {
        var fileState = _state.GetOrAdd(path, _ => new FileState());

        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch { return; }
        if (!fi.Exists || fi.Length <= fileState.Offset) return;

        // Reset offset if the file was rotated (became smaller).
        if (fi.Length < fileState.Offset)
        {
            fileState.Offset = 0;
            fileState.RecentEvents.Clear();
            fileState.Tokens5h = fileState.Tokens24h = fileState.Tokens7d = fileState.TokensTotal = 0;
            fileState.Messages5h = fileState.Messages24h = fileState.Messages7d = fileState.MessagesTotal = 0;
            fileState.TokensByModel.Clear();
        }

        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            fs.Position = fileState.Offset;
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                ParseLineInto(line, fileState);
            }
            fileState.Offset = fs.Position;
        }
        catch (IOException) { /* file in use — try again next tick */ }
    }

    private static void ParseLineInto(string line, FileState s)
    {
        // Cheap pre-filter: only assistant lines carry usage; skip the rest
        // without paying the JsonDocument parse cost (these files are big).
        if (line.Length < 20 || line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) return;
        if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("usage", out var usage)) return;

            long Get(JsonElement el, string name) =>
                el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt64() : 0L;

            var totalTokens =
                Get(usage, "input_tokens") +
                Get(usage, "output_tokens") +
                Get(usage, "cache_creation_input_tokens") +
                Get(usage, "cache_read_input_tokens");
            if (totalTokens <= 0) return;

            DateTime ts = DateTime.UtcNow;
            if (root.TryGetProperty("timestamp", out var tsEl) &&
                tsEl.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(tsEl.GetString(), null,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                ts = parsed;
            }

            string model = msg.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString() ?? "unknown" : "unknown";

            s.TokensTotal += totalTokens;
            s.MessagesTotal += 1;
            s.RecentEvents.Add((ts, totalTokens));
            if (s.TokensByModel.TryGetValue(model, out var prev))
                s.TokensByModel[model] = prev + totalTokens;
            else
                s.TokensByModel[model] = totalTokens;
            s.LastEventAt = (s.LastEventAt is null || ts > s.LastEventAt) ? ts : s.LastEventAt;
        }
        catch { /* malformed line — skip */ }
    }

    private void PushSnapshot()
    {
        var now = DateTime.UtcNow;
        var cutoff5h = now - TimeSpan.FromHours(5);
        var cutoff24h = now - TimeSpan.FromHours(24);
        var cutoff7d = now - TimeSpan.FromDays(7);

        long t5 = 0, t24 = 0, t7d = 0, ttot = 0;
        int m5 = 0, m24 = 0, m7d = 0, mtot = 0;
        DateTime? last = null;
        var byModel = new Dictionary<string, long>();

        foreach (var (_, s) in _state)
        {
            // Prune events older than 7d to keep RecentEvents bounded.
            s.RecentEvents.RemoveAll(ev => ev.ts < cutoff7d);

            foreach (var ev in s.RecentEvents)
            {
                if (ev.ts >= cutoff5h) { t5 += ev.tokens; m5++; }
                if (ev.ts >= cutoff24h) { t24 += ev.tokens; m24++; }
                if (ev.ts >= cutoff7d) { t7d += ev.tokens; m7d++; }
            }
            ttot += s.TokensTotal;
            mtot += s.MessagesTotal;
            if (s.LastEventAt is DateTime le && (last is null || le > last)) last = le;

            foreach (var (model, tokens) in s.TokensByModel)
            {
                byModel.TryGetValue(model, out var prev);
                byModel[model] = prev + tokens;
            }
        }

        var snap = new TallySnapshot
        {
            Tokens5h = t5, Messages5h = m5,
            Tokens24h = t24, Messages24h = m24,
            Tokens7d = t7d, Messages7d = m7d,
            TokensTotal = ttot, MessagesTotal = mtot,
            TokensByModel = byModel,
            LastEventAt = last
        };

        try { Updated?.Invoke(this, snap); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ClaudeTranscriptTally event: {ex.Message}"); }
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
    }
}
