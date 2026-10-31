using System.Reflection;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// A move over a file somebody has open is refused, however they opened it: 311
/// of 3,000 with a reader there every millisecond, all through within 115 ms of
/// trying again (2026-10-31). The writers here keep notes, tasks and heartbeats
/// the app and Obsidian read all day.
/// </summary>
internal static partial class Program
{
    private static void RegisterBusyFileChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("busy file: brain_mark_verified gets through with the note open", BusyFileMarkVerified));
        checks.Add(("busy file: task_update gets through with the task open", BusyFileTaskUpdate));
        checks.Add(("busy file: the activity feed is trimmed with a reader on it", BusyFileActivityTrim));
        checks.Add(("busy file: a launcher's heartbeat gets through with its record being read", BusyFileInstanceRecord));
        checks.Add(("busy file: every replacing move in BrainX.Mcp retries, bar the ones that must not", BusyFileNoBareMoves));
        checks.Add(("busy file: a file held past the wait fails cleanly, and a missing folder fails at once", BusyFileGivesUpCleanly));
    }

    /// <summary>What a write that does not get through leaves behind: the old file
    /// whole and no scratch copy, because several callers swallow this.</summary>
    private static Task BusyFileGivesUpCleanly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "brainx-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "state.json");
            var tmp = target + ".tmp";
            File.WriteAllText(target, "old");
            File.WriteAllText(tmp, "new");

            Exception? thrown = null;
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                try { AtomicFile.MoveOver(tmp, target); }
                catch (Exception ex) { thrown = ex; }
            }
            Check("the refusal comes out as it would from File.Move", thrown is UnauthorizedAccessException or IOException, thrown?.GetType().Name);
            Check("…the old file is whole", File.ReadAllText(target) == "old", File.ReadAllText(target));
            Check("…and the scratch copy is gone", !File.Exists(tmp));

            File.WriteAllText(tmp, "new");
            AtomicFile.MoveOver(tmp, target);
            Check("with nobody on the file it lands", File.ReadAllText(target) == "new" && !File.Exists(tmp));

            File.WriteAllText(tmp, "new");
            var took = System.Diagnostics.Stopwatch.StartNew();
            try { AtomicFile.MoveOver(tmp, Path.Combine(dir, "no-such-folder", "state.json")); thrown = null; }
            catch (Exception ex) { thrown = ex; }
            Check($"nowhere to put it is not waited out (took {took.ElapsedMilliseconds} ms)",
                  thrown is DirectoryNotFoundException && took.ElapsedMilliseconds < 200, thrown?.GetType().Name);
            Check("…and leaves no scratch copy either", !File.Exists(tmp));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    /// <summary>
    /// <paramref name="target"/> open as a reader has it, until the writer has
    /// reached its move: its temp file is beside the target by then, or the
    /// write has come back already. A move that takes no for an answer has been
    /// refused long before the hold lets go, so nothing here is left to who wins
    /// a race, and a writer that tries again has time to spare.
    /// </summary>
    private static async Task<T> WhileOpen<T>(string target, Func<Task<T>> write)
    {
        var dir = Path.GetDirectoryName(target)!;
        var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var writing = write();
            var end = DateTime.UtcNow.AddSeconds(30);
            while (!writing.IsCompleted && DateTime.UtcNow < end && !Directory.EnumerateFiles(dir, "*.tmp").Any())
                await Task.Delay(1);
            await Task.Delay(60);
            held.Dispose();
            return await writing;
        }
        finally { held.Dispose(); }
    }

    private static async Task BusyFileMarkVerified()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(_ => { });
        var note = Path.Combine(rig.Vault, "Notes", "Deploy port.md");
        File.WriteAllText(note, "---\ntags: [deploy]\n---\n# Deploy port\n\nThe app listens on 8080.\n");
        var graph = new KnowledgeIndexer().IndexVault(rig.Vault);
        File.WriteAllText(Path.Combine(rig.Vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            BrainExporter.BuildExport(new BrainX.Core.Models.BrainIdentity { Address = "t", DisplayName = "t" }, graph, rig.Vault)));

        var server = await StartServer(exe, rig.Vault);
        try
        {
            await WhileOpen(note, () => Rpc(server, 10, "tools/call", new JObject
            {
                ["name"] = "brain_mark_verified",
                ["arguments"] = new JObject { ["id"] = graph.Nodes.Single().Id, ["ok"] = true },
            }));
            Check("the verdict is on the note", File.ReadAllText(note).Contains("verifyStatus: ok"), File.ReadAllText(note));
            Check("…and no scratch copy beside it", !Directory.EnumerateFiles(Path.GetDirectoryName(note)!, "*.tmp").Any());
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); } catch { }
            server.Dispose();
        }
    }

    private static async Task BusyFileTaskUpdate()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(_ => { });
        await using var session = await StartBusSession(exe, rig.Vault, rig.Key, "codex");
        var task = await session.Call("task_handoff", new JObject { ["title"] = "Busy task", ["goal"] = "It lands", ["assignee"] = "claude" });
        var file = Path.Combine(rig.Vault, task["path"]!.ToString());

        await WhileOpen(file, () => session.Raw("task_update", new JObject { ["task_id"] = task["task_id"], ["status"] = "claimed" }));
        Check("the status is on the task", File.ReadAllText(file).Contains("status: claimed"), File.ReadAllText(file));
        Check("…and no scratch copy beside it", !Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.tmp").Any());
    }

    private static async Task BusyFileActivityTrim()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        await using var rig = NewOpenRunRig(_ => { });
        await using var session = await StartBusSession(exe, rig.Vault, rig.Key, "codex");
        await session.Raw("brain_stats", new JObject());
        var dir = Path.Combine(rig.Bus, "activity");
        var feed = Directory.GetFiles(dir, "*.ndjson").Single();
        // Past the 256 KB a feed may reach and the 500 lines a trim keeps.
        File.AppendAllLines(feed, Enumerable.Range(0, 3000).Select(i =>
            new JObject { ["ts"] = DateTime.UtcNow.ToString("o"), ["tool"] = "pad", ["summary"] = new string('x', 120) }
                .ToString(Newtonsoft.Json.Formatting.None)));

        await WhileOpen(feed, () => session.Raw("brain_stats", new JObject()));
        Check("the feed is back under its cap", new FileInfo(feed).Length < 256 * 1024, new FileInfo(feed).Length.ToString());
        Check("…and no scratch copy beside it", !Directory.EnumerateFiles(dir, "*.tmp").Any());
    }

    /// <summary>The launcher's record lives in the machine's AppData, which a child
    /// process cannot be pointed away from, so this runs the real Flush in here,
    /// on a file in a temp folder.</summary>
    private static async Task BusyFileInstanceRecord()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var instances = Assembly.LoadFrom(Path.ChangeExtension(exe, ".dll")).GetType("BrainX.Mcp.McpInstances")!;
        const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Static;
        var dir = Path.Combine(Path.GetTempPath(), "brainx-instances-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var record = Path.Combine(dir, "4242.json");
        File.WriteAllText(record, "{}");
        instances.GetField("_file", priv)!.SetValue(null, record);
        instances.GetField("_rec", priv)!.SetValue(null, new JObject { ["pid"] = 4242 });
        instances.GetField("_registered", priv)!.SetValue(null, true);
        try
        {
            await WhileOpen(record, () => Task.Run(() => instances.GetMethod("Flush", priv)!.Invoke(null, new object[] { true })));
            Check("the heartbeat is in the record", File.ReadAllText(record).Contains("lastActivityUtc"), File.ReadAllText(record));
            Check("…and no scratch copy beside it", !Directory.EnumerateFiles(dir, "*.tmp").Any());
        }
        finally
        {
            instances.GetField("_registered", priv)!.SetValue(null, false);
            instances.GetField("_file", priv)!.SetValue(null, "");
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The writers no check above reaches are held to the same rule by reading
    /// them. Three moves stay bare on purpose: taking a message out of an inbox,
    /// where the move decides which session got it and a refusal means try on
    /// the next read, and retiring a bundle, which says so and is retried by the
    /// next bake.
    /// </summary>
    private static Task BusyFileNoBareMoves()
    {
        var project = FindRepoFile("BrainX.Mcp", "BrainX.Mcp.csproj");
        var ndjson = FindRepoFile("BrainX.Core", "Services", "NdjsonLog.cs");
        if (project == null || ndjson == null) { Check("the checkout is there to read", false); return Task.CompletedTask; }

        string[] onPurpose = { "File.Move(f, claimed, overwrite: true)", "Path.Combine(readDir, Path.GetFileName(f)), overwrite: true)", "Path.Combine(retiredDir, Path.GetFileName(file)), overwrite: true)" };
        var sep = Path.DirectorySeparatorChar;
        var sources = Directory.EnumerateFiles(Path.GetDirectoryName(project)!, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .Append(ndjson);
        var bare = sources
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Number: i + 1, Line: line.Trim())))
            .Where(l => l.Line.Contains("File.Move(") && l.Line.Contains("overwrite: true") && !onPurpose.Any(l.Line.Contains))
            .Select(l => $"{l.File}:{l.Number}")
            .ToList();
        Check("no replacing move is left on its own", bare.Count == 0, string.Join(", ", bare));
        return Task.CompletedTask;
    }
}
