using BrainX.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace BrainX.Client;

public partial class MainWindow
{
    private FileSystemWatcher? _codeGraphWatcher;
    private DispatcherTimer? _codeGraphRefresh;
    private string? _codeGraphWatchedVault;
    private bool _codeHooked, _codeClosed;
    /// <summary>The latest push wins: a slow load that finishes behind a newer one is dropped.</summary>
    private int _codePush;
    /// <summary>Hash of the last message posted. The page reports every change; a rewrite that changed nothing is not one.</summary>
    private string? _codeSent;
    /// <summary>One entry per stored graph, so a build of one repo does not re-read the others.</summary>
    private readonly ConcurrentDictionary<string, CodeRepoView> _codeViews = new(StringComparer.Ordinal);

    private sealed record CodeRepoView(string Vault, DateTime Written, long Length, JObject Layer, JObject Card);

    private void HookCodeUniverse()
    {
        if (!_codeHooked)
        {
            _codeHooked = true;
            Closed += (_, _) =>
            {
                _codeClosed = true;
                _codeGraphRefresh?.Stop();
                _codeGraphWatcher?.Dispose();
                StopCodeBuild();
            };
        }
        if (_codeGraphWatcher != null && _codeGraphWatchedVault == _vaultPath) return;
        _codeGraphWatcher?.Dispose();
        _codeGraphWatcher = null;
        var parent = Path.Combine(_vaultPath, ".obsidianx");
        if (!Directory.Exists(parent)) return;
        var graphDir = CodeGraphStore.Directory(_vaultPath);
        if (_codeGraphRefresh == null)
        {
            _codeGraphRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _codeGraphRefresh.Tick += (_, _) =>
            {
                _codeGraphRefresh.Stop();
                _ = PushCodeGraphAsync();
            };
        }
        var watcher = new FileSystemWatcher(parent, "*.json") { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        void Changed(object? _, FileSystemEventArgs e)
        {
            if (!string.Equals(Path.GetDirectoryName(e.FullPath), graphDir, StringComparison.OrdinalIgnoreCase)) return;
            _ = Dispatcher.BeginInvoke(new Action(() => { _codeGraphRefresh?.Stop(); _codeGraphRefresh?.Start(); }));
        }
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += Changed;
        watcher.EnableRaisingEvents = true;
        _codeGraphWatcher = watcher;
        _codeGraphWatchedVault = _vaultPath;
    }

    /// <summary>
    /// What `ready` does: the page starts empty. Code first, so the sky mounts once
    /// with both, but a slow graph never holds the notes back: past the wait the
    /// notes mount alone and the code layer joins them when it lands.
    /// </summary>
    private async Task PushUniverseOnReadyAsync()
    {
        _codeSent = null;
        _codeDashOpen = false;
        ForgetOpenCodeGraphs();
        await Task.WhenAny(PushCodeGraphAsync(), Task.Delay(1200));
        try { PushBrainSnapshotToUniverse(); }
        catch (Exception ex) { Debug.WriteLine($"Universe snapshot: {ex.Message}"); }
        PostCodeBuildState(null);
    }

    /// <summary>
    /// Send the code layer and the repo cards. Reads off the UI thread: a graph
    /// is tens of megabytes of JSON. Never throws: the notes do not depend on it.
    /// </summary>
    private async Task PushCodeGraphAsync()
    {
        try
        {
            if (UniverseWebView?.CoreWebView2 == null || _codeClosed) return;
            HookCodeUniverse();
            var push = ++_codePush;
            var vault = _vaultPath;
            var json = await Task.Run(() => JsonConvert.SerializeObject(new JObject { ["type"] = "codeGraph", ["payload"] = LoadCodeUniverse(vault) }));
            if (push != _codePush || _codeClosed) return;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            if (hash == _codeSent) return;
            _codeSent = hash;
            UniverseWebView?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Code universe: {ex.Message}");
            PostCode(new JObject { ["type"] = "codeGraphFailed", ["message"] = "The code graphs could not be read. What is on screen may be out of date." });
        }
    }

    private JObject LoadCodeUniverse(string vault)
    {
        var drawn = new JObject { ["nodes"] = new JArray(), ["edges"] = new JArray(), ["mentions"] = new JArray() };
        var cards = new JArray();
        foreach (var repo in CodeGraphStore.List(vault))
        {
            var view = ViewOf(vault, repo);
            foreach (var part in new[] { "nodes", "edges", "mentions" })
                foreach (var row in (JArray)view.Layer[part]!) ((JArray)drawn[part]!).Add(row);
            var card = (JObject)view.Card.DeepClone();
            if (card["error"] == null) CodeGraphView.MarkState(card);
            cards.Add(card);
        }
        drawn["totals"] = cards;
        drawn["drawKey"] = CodeGraphView.DrawKey(new JObject { ["nodes"] = drawn["nodes"], ["edges"] = drawn["edges"], ["mentions"] = drawn["mentions"] });
        return drawn;
    }

    private CodeRepoView ViewOf(string vault, string repo)
    {
        var file = new FileInfo(CodeGraphStore.PathFor(vault, repo));
        var written = file.Exists ? file.LastWriteTimeUtc : default;
        var length = file.Exists ? file.Length : 0;
        if (_codeViews.TryGetValue(repo, out var known) && known.Vault == vault && known.Written == written && known.Length == length)
            return known;
        JObject layer, card;
        try
        {
            var graph = CodeGraphStore.Load(vault, repo) ?? throw new InvalidDataException("gone");
            layer = CodeGraphView.Layer(graph, repo);
            card = CodeGraphView.Card(graph, repo, ((JArray)layer["nodes"]!).Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Code universe {repo}: {ex.Message}");
            layer = new JObject { ["nodes"] = new JArray(), ["edges"] = new JArray(), ["mentions"] = new JArray() };
            card = CodeGraphView.UnreadableCard(repo);
        }
        return _codeViews[repo] = new CodeRepoView(vault, written, length, layer, card);
    }

    private void PostCode(JObject message)
    {
        try { UniverseWebView?.CoreWebView2?.PostWebMessageAsJson(message.ToString(Formatting.None)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"Code universe post: {ex.Message}");
        }
    }

    // ── The dashboard's requests ──

    private void HandleCodeMessage(string type, string json)
    {
        var o = JObject.Parse(json);
        switch (type)
        {
            case "codeDashOpen":
                _codeDashOpen = true;
                _ = PushCodeGraphAsync();     // cards go stale without the file changing: HEAD moves
                PostCodeBuildState(null);
                break;
            case "codeDashClose":
                _codeDashOpen = false;
                ForgetOpenCodeGraphs();
                break;
            case "codeQuery": _ = AnswerCodeQueryAsync(o); break;
            case "codeBuild": _ = BuildCardRepoAsync(o["repo"]?.ToString(), o["full"]?.Value<bool>() ?? false); break;
            case "codeBuildStop": StopCodeBuild(); break;
            case "codeAddRepo": _ = Dispatcher.BeginInvoke(new Action(() => _ = AddCodeRepoAsync())); break;
            case "codeReveal": RevealCodeFile(o["repo"]?.ToString(), o["file"]?.ToString()); break;
        }
    }

    // Whole graphs are held only while the panel is open, and queries take turns:
    // the page asks one thing at a time, and two first reads of one file are a waste.
    private bool _codeDashOpen;
    private readonly Dictionary<string, (DateTime Written, long Length, CodeGraph Graph)> _codeOpenGraphs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _codeQueryGate = new(1, 1);

    private void ForgetOpenCodeGraphs()
    {
        if (!_codeQueryGate.Wait(0)) return;     // the query in flight clears them on its way out
        try { _codeOpenGraphs.Clear(); }
        finally { _codeQueryGate.Release(); }
    }

    private CodeGraph? OpenCodeGraph(string vault, string repo)
    {
        var file = new FileInfo(CodeGraphStore.PathFor(vault, repo));
        if (!file.Exists) return null;
        if (_codeOpenGraphs.TryGetValue(repo, out var open) && open.Written == file.LastWriteTimeUtc && open.Length == file.Length)
            return open.Graph;
        var graph = CodeGraphStore.Load(vault, repo);
        if (graph != null) _codeOpenGraphs[repo] = (file.LastWriteTimeUtc, file.Length, graph);
        return graph;
    }

    private async Task AnswerCodeQueryAsync(JObject q)
    {
        var vault = _vaultPath;
        JObject answer;
        try
        {
            await _codeQueryGate.WaitAsync();
            try
            {
                answer = await Task.Run(() =>
                {
                    var graph = OpenCodeGraph(vault, CodeGraphStore.NormaliseRepo(q["repo"]?.ToString() ?? ""));
                    if (graph == null) return new JObject { ["error"] = "This graph is gone. It may have been removed." };
                    return q["op"]?.ToString() switch
                    {
                        "overview" => CodeGraphView.Overview(graph),
                        "search" => CodeGraphView.Search(graph, q["q"]?.ToString()),
                        "symbol" => CodeGraphView.Detail(graph, q["symbol"]?.ToString())
                            ?? new JObject { ["error"] = "This symbol is no longer in the graph." },
                        _ => new JObject { ["error"] = "Unknown request." }
                    };
                });
            }
            finally
            {
                if (!_codeDashOpen) _codeOpenGraphs.Clear();
                _codeQueryGate.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Code query: {ex.Message}");
            answer = new JObject { ["error"] = "This graph could not be read. Rebuild it." };
        }
        answer["type"] = "codeAnswer";
        answer["id"] = q["id"];
        PostCode(answer);
    }

    private void RevealCodeFile(string? repo, string? relative)
    {
        var root = repo != null && _codeViews.TryGetValue(repo, out var view) ? view.Card["root"]?.ToString() : null;
        var full = CodeFiles.Inside(root, relative);
        if (full == null || !File.Exists(full))
        {
            PostCode(new JObject { ["type"] = "codeNotice", ["message"] = "That file is not on this machine any more." });
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Code reveal: {ex.Message}");
        }
    }

    // ── Builds ──
    //
    // The client carries no Roslyn: a build is brainx-mcp's code-build verb, one
    // at a time like the MCP's own builds. Roots come from the stored card or the
    // folder dialog, never from the page. Killing a run is safe for the same reason
    // the gardener's is: the graph and its cache are replaced whole.

    private const int CodeBuildMinutes = 30;

    private sealed class CodeBuildRun
    {
        public required string Repo { get; init; }
        public Process? Process { get; set; }
        public bool Stopped { get; set; }
    }

    private CodeBuildRun? _codeBuild;

    private sealed record CodeBuildOutcome(string State, string Message);

    private void PostCodeBuildState(CodeBuildOutcome? outcome, string? repo = null)
    {
        var run = _codeBuild;
        PostCode(new JObject
        {
            ["type"] = "codeBuildState", ["running"] = run != null, ["repo"] = run?.Repo ?? repo,
            ["state"] = outcome?.State, ["message"] = outcome?.Message
        });
    }

    private void StopCodeBuild()
    {
        var run = _codeBuild;
        if (run == null) return;
        run.Stopped = true;
        try { run.Process?.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"Code build stop: {ex.Message}");
        }
    }

    private Task BuildCardRepoAsync(string? repo, bool full)
    {
        var root = repo != null && _codeViews.TryGetValue(repo, out var view) ? view.Card["root"]?.ToString() : null;
        if (string.IsNullOrEmpty(root) || repo == null)
        {
            PostCodeBuildState(new CodeBuildOutcome("failed", "This graph does not say which folder it came from. Add the folder again."), repo);
            return Task.CompletedTask;
        }
        return RunCodeBuildAsync(root, repo, full);
    }

    private async Task AddCodeRepoAsync()
    {
        if (_codeBuild != null)
        {
            PostCodeBuildState(new CodeBuildOutcome("busy", "Finish or stop the build that is running first."));
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a code folder to map" };
        if (dialog.ShowDialog(this) != true) return;
        var root = Path.GetFullPath(dialog.FolderName);
        if (string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
        {
            PostCodeBuildState(new CodeBuildOutcome("failed", "Pick a project folder, not a whole drive."));
            return;
        }
        var vault = _vaultPath;
        string repo;
        try { repo = await Task.Run(() => CodeGraphStore.SlugFor(vault, root)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"Code add: {ex.Message}");
            PostCodeBuildState(new CodeBuildOutcome("failed", "That folder could not be read."));
            return;
        }
        await RunCodeBuildAsync(root, repo, full: false);
    }

    private async Task RunCodeBuildAsync(string root, string repo, bool full)
    {
        if (_codeBuild != null)
        {
            PostCodeBuildState(new CodeBuildOutcome("busy", "Finish or stop the build that is running first."));
            return;
        }
        if (!Directory.Exists(root))
        {
            PostCodeBuildState(new CodeBuildOutcome("failed", $"The folder is not there any more: {root}"), repo);
            return;
        }
        var exe = ResolveBestMcpExe();
        if (exe == null)
        {
            PostCodeBuildState(new CodeBuildOutcome("failed", "brainx-mcp.exe was not found. Reinstall or rebuild BrainX."), repo);
            return;
        }
        var run = new CodeBuildRun { Repo = repo };
        _codeBuild = run;
        PostCodeBuildState(null);
        var vault = _vaultPath;
        CodeBuildOutcome outcome;
        try { outcome = await Task.Run(() => SpawnCodeBuild(exe, root, repo, full, vault, run)); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Code build: {ex.Message}");
            outcome = new CodeBuildOutcome("failed", "The build could not be started.");
        }
        _codeBuild = null;
        PostCodeBuildState(outcome, repo);
        await PushCodeGraphAsync();
    }

    private static CodeBuildOutcome SpawnCodeBuild(string exe, string root, string repo, bool full, string vault, CodeBuildRun run)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "code-build", root, "--repo", repo, "--vault", vault }) psi.ArgumentList.Add(arg);
        if (full) psi.ArgumentList.Add("--full");

        using var p = Process.Start(psi);
        if (p == null) return new CodeBuildOutcome("failed", "The build could not be started.");
        run.Process = p;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        // Stdin is closed at once: an older brainx-mcp without this verb starts its
        // server instead, and the server leaves on end-of-input. It says nothing on
        // stdout, which is how that case is told from a build (below).
        p.StandardInput.Close();
        // Both pipes drain before the wait, the gardener's lesson: a pipe nobody
        // reads fills and the child never exits.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(CodeBuildMinutes)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new CodeBuildOutcome("failed", $"The build ran past {CodeBuildMinutes} minutes and was stopped.");
        }
        Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5));
        if (run.Stopped) return new CodeBuildOutcome("stopped", "Build stopped. The last saved graph is unchanged.");

        var output = stdout.IsCompletedSuccessfully ? stdout.Result.Trim() : "";
        var line = output.Split('\n').LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line))
        {
            Debug.WriteLine($"Code build {repo}: no output, exit {p.ExitCode}. {(stderr.IsCompletedSuccessfully ? stderr.Result : "")}");
            return new CodeBuildOutcome("failed", "This brainx-mcp cannot build graphs. Update BrainX.");
        }
        JObject report;
        try { report = JObject.Parse(line); }
        catch (JsonException) { return new CodeBuildOutcome("failed", "The build gave an answer BrainX could not read."); }
        return report["status"]?.ToString() switch
        {
            "unchanged" => new CodeBuildOutcome("done", "Already up to date."),
            "locked" => new CodeBuildOutcome("failed", "Another build of this repo is running."),
            "built" or "refreshed" => new CodeBuildOutcome("done",
                $"{report["symbols"]?.Value<int>():N0} symbols, {report["edges"]?.Value<int>():N0} edges in {(report["milliseconds"]?.Value<double>() ?? 0) / 1000:0.#} s."),
            _ => new CodeBuildOutcome("failed", report["error"]?.ToString() is { Length: > 0 } why ? why : "The build did not finish.")
        };
    }
}
