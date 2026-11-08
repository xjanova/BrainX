// SetupGirls.cs - which girl stands on which page of the setup wizard
// (installer contract §2.6). Per run: the eight names are shuffled and page i
// shows name[i % 8] in a random pose, so no girl repeats inside one run. The
// run's list is remembered in <data>\setup-girls.txt, and a run whose first
// page would show the same girl as the last run's first page is reshuffled.

namespace BrainX.Core.Services.Setup;

public sealed record SetupGirl(string Name, int Pose)
{
    /// <summary>"claude.png" for pose 0, "claude-2.png" for pose 1.</summary>
    public string FileName => Pose == 0 ? Name + ".png" : Name + "-2.png";
}

public sealed class SetupGirls
{
    public static readonly IReadOnlyList<string> All =
        ["antigravity", "brainx", "claude", "cluadex", "codex", "gemini", "grok", "mai"];

    public IReadOnlyList<SetupGirl> Order { get; }

    private SetupGirls(IReadOnlyList<SetupGirl> order) => Order = order;

    /// <summary>The girl for page <paramref name="page"/>.</summary>
    public SetupGirl ForPage(int page) => Order[((page % Order.Count) + Order.Count) % Order.Count];

    /// <summary>
    /// A new run's order. <paramref name="lastFirst"/> is the first girl of
    /// the previous run; this run never starts with her (when there is more
    /// than one girl to choose from).
    /// </summary>
    public static SetupGirls Plan(IReadOnlyList<string> names, Random rng, string? lastFirst)
    {
        var pool = names.Count > 0 ? names.ToArray() : All.ToArray();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            Shuffle(pool, rng);
            if (pool.Length < 2 || !string.Equals(pool[0], lastFirst, StringComparison.OrdinalIgnoreCase)) break;
        }
        // Eight tries of a uniform shuffle all landing on her again is ~1e-7;
        // still, guarantee it rather than hope.
        if (pool.Length >= 2 && string.Equals(pool[0], lastFirst, StringComparison.OrdinalIgnoreCase))
            (pool[0], pool[1]) = (pool[1], pool[0]);
        return new SetupGirls(pool.Select(n => new SetupGirl(n, rng.Next(2))).ToList());
    }

    private static void Shuffle(string[] a, Random rng)
    {
        for (var i = a.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }

    public static string DefaultMemoryPath => Path.Combine(VaultLocations.DataRoot, "setup-girls.txt");

    /// <summary>
    /// Plan a run from the girls whose pictures exist in <paramref name="girlsDir"/>,
    /// avoiding the last run's first girl, and remember this run.
    /// </summary>
    public static SetupGirls ForNewRun(string girlsDir, string? memoryPath = null, Random? rng = null)
    {
        memoryPath ??= DefaultMemoryPath;
        var present = All.Where(n => File.Exists(Path.Combine(girlsDir, n + ".png"))).ToList();
        var plan = Plan(present.Count > 0 ? present : All, rng ?? Random.Shared, ReadFirst(memoryPath));
        // A pose whose picture is missing falls back to the other.
        plan = new SetupGirls(plan.Order
            .Select(g => g.Pose == 1 && !File.Exists(Path.Combine(girlsDir, g.FileName)) ? g with { Pose = 0 } : g)
            .ToList());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(memoryPath)!);
            File.WriteAllText(memoryPath, string.Join(",", plan.Order.Select(g => $"{g.Name}:{g.Pose}")));
        }
        catch { /* memory is a nicety */ }
        return plan;
    }

    /// <summary>The first girl of the remembered run, or null.</summary>
    public static string? ReadFirst(string memoryPath)
    {
        try
        {
            if (!File.Exists(memoryPath)) return null;
            var first = File.ReadAllText(memoryPath).Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first?.Split(':')[0].Trim() is { Length: > 0 } n ? n : null;
        }
        catch { return null; }
    }
}
