using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// A card the owner is waiting on, live, in CluadeX.
//
// CluadeX's "BrainX cowork room" model relays the owner's chat into the room
// as the seat `cluadex` — a line and a board card — and the owner then watches
// CluadeX for the answer. The audit of 2026-10-19 found the room a relay with
// nobody on the other end for a quarter of an hour: the card waited the full
// OpenAllocateAfter any open card waits for the room to claim it, and the run
// it finally got started in D:\BrainX, whatever project the chat was in.
//
//  - Handed out at once when nobody else is seated in the room who could
//    claim it, after RelayAllocateGrace when somebody is — to an on-call
//    runner that is free, first. A card the seat named an agent on is called
//    at once instead of after FollowUpIdle of quiet.
//  - The run starts in the request's project folder ("Project folder(s) on
//    this PC — …: E:\Code\…") when runners.json lets that runner work there
//    (RunnerMayWorkIn). When it does not, nobody is started somewhere else
//    instead — a run in the wrong folder does the work in the wrong place, or
//    cannot reach the project at all (claude's --add-dir) — the card is
//    blocked and the seat is told in the room, by name and with the card id
//    (what CluadeX shows the owner), which folder, which are allowed, and
//    what to add.
//  - A card the seat dropped (the owner pressed Stop, or CluadeX stopped
//    waiting) is finished like any dropped card: never handed out, never
//    chased, and whoever held it is told by name (CoworkTaskChange).
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>How long a relayed card is left for somebody seated in the
    /// room to claim before the broker hands it out. Nobody seated: no wait.</summary>
    private static readonly TimeSpan RelayAllocateGrace = TimeSpan.FromSeconds(90);

    /// <summary>How long an open card sits unclaimed before the broker picks
    /// somebody for it.</summary>
    private static TimeSpan CoworkAllocateAfter(JObject card) =>
        !CoworkRelay.IsLiveRelayCard(card) ? OpenAllocateAfter
        : CoworkSomebodyElseSeated(card["createdBy"]?.ToString()) ? RelayAllocateGrace
        : TimeSpan.Zero;

    /// <summary>A live session of an agent who could claim a card itself —
    /// not the owner, the broker, a relay seat or the card's own creator.</summary>
    private static bool CoworkSomebodyElseSeated(string? creator)
    {
        try
        {
            if (!Directory.Exists(CoworkMembersDir)) return false;
            return Directory.GetFiles(CoworkMembersDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(a => a.Length > 0 && !IsReservedIdentity(a) && !CoworkRelay.LiveRelaySeats.Contains(a)
                            && !a.Equals(creator, StringComparison.OrdinalIgnoreCase))
                .Any(CoworkHasLiveSession);
        }
        catch { return false; }
    }

    /// <summary>
    /// The project folders a relayed card names that exist on this PC, the
    /// chat's own project first. The card's detail first; a long request is
    /// cut to fit a card (CluadeX keeps 3,900 characters, the board 4,000) and
    /// the folder line at its end is the first thing to go, so then the line
    /// the seat said in the room with the card — same seat, same work label,
    /// said just before it — is read instead.
    /// </summary>
    private static IReadOnlyList<string> CoworkRelayFolders(JObject card)
    {
        if (!CoworkRelay.IsLiveRelayCard(card)) return Array.Empty<string>();
        var named = CoworkRelay.ProjectFolders(card["detail"]?.ToString());
        if (named.Count == 0) named = CoworkRelayLineFolders(card);
        return named.Where(Directory.Exists).ToList();
    }

    private static IReadOnlyList<string> CoworkRelayLineFolders(JObject card)
    {
        var seat = card["createdBy"]?.ToString() ?? "";
        var work = card["work"]?.ToString();
        if (string.IsNullOrEmpty(work) || CoworkUtc(card["createdUtc"]) is not DateTime created) return Array.Empty<string>();
        var floor = (created - TimeSpan.FromMinutes(5)).Ticks.ToString("D19");
        var ceiling = (created + TimeSpan.FromMinutes(1)).Ticks.ToString("D19");
        foreach (var f in Enumerable.Reverse(CoworkMessageFiles()))
        {
            var name = Path.GetFileName(f);
            if (string.CompareOrdinal(name, ceiling) > 0) continue;
            if (string.CompareOrdinal(name, floor) < 0) break;
            if (!CoworkSpeakerFromName(f).Equals(seat, StringComparison.OrdinalIgnoreCase)) continue;
            var o = ReadJsonOrNull(f);
            if (!string.Equals(o?["work"]?.ToString(), work, StringComparison.OrdinalIgnoreCase)) continue;
            var dirs = CoworkRelay.ProjectFolders(o?["body"]?.ToString());
            if (dirs.Count > 0) return dirs;
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Where runners.json lets this runner work unattended: the roots the
    /// broker looks for project folders under (workRoots), the folders it maps
    /// work labels to (workDirs), the runner's own cwd, and what its arguments
    /// grant (claude's --add-dir, gemini's --include-directories).
    /// </summary>
    private static List<string> RunnerAllowedDirs(BrokerConfig cfg, RunnerSpec runner) =>
        cfg.WorkRoots.Concat(cfg.WorkDirs.Values).Append(runner.Cwd).Concat(CoworkRelay.GrantedDirs(runner.Args))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool RunnerMayWorkIn(BrokerConfig cfg, RunnerSpec runner, string dir) =>
        RunnerAllowedDirs(cfg, runner).Any(root => CoworkRelay.IsWithin(dir, root));

    /// <summary>
    /// The folder a run started for these board cards works in: the project of
    /// the first relayed card among them that this runner may work in. Null
    /// when none names one — the run starts where it always did.
    /// </summary>
    private static string? CoworkCardDirFor(BrokerConfig cfg, RunnerSpec runner, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (!CoworkTaskIdPattern.IsMatch(id)) continue;   // "peer": a teammate's question, no card
            var card = ReadJsonOrNull(CoworkTaskFile(id));
            if (card == null || CoworkTriage.IsFinished(card)) continue;
            if (CoworkRelayFolders(card).FirstOrDefault() is { } dir && RunnerMayWorkIn(cfg, runner, dir)) return dir;
        }
        return null;
    }

    /// <summary>The runners a relayed card could go to: on call and on the
    /// owner's roster — or everyone on the roster when nobody is on call.</summary>
    private static List<string> CoworkRelayTeam(BrokerConfig cfg)
    {
        var roster = cfg.Runners.Keys.Where(a => !IsReservedIdentity(a) && CoworkAgentSkills(a).Known).ToList();
        var onCall = roster.Where(a => cfg.Runners[a].OnCall).ToList();
        return (onCall.Count > 0 ? onCall : roster).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Nobody who could take this relayed card may work in its folder: block
    /// it, and say why to the seat the owner is watching. The note avoids the
    /// words CoworkTriage reads as "needs the owner", so no second notice goes
    /// out to the owner's decision cards for what CluadeX has just shown them.
    /// </summary>
    private static bool CoworkRefuseRelayFolder(BrokerConfig cfg, JObject card, string folder, IReadOnlyList<string> agents,
                                                Func<JObject, bool> onlyIf)
    {
        var id = card["id"]!.ToString();
        var allowed = agents.Where(cfg.Runners.ContainsKey)
            .SelectMany(a => RunnerAllowedDirs(cfg, cfg.Runners[a]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } up ? up : folder;
        var note = $"📁 งานนี้อยู่ที่ {folder} ซึ่ง runners.json ยังไม่ให้ {(agents.Count > 0 ? string.Join(", ", agents) : "runner ไหน")} "
                 + $"ทำงานแบบไม่มีคนดู (อนุญาตแค่ {(allowed.Count > 0 ? string.Join(", ", allowed) : "—")}) — "
                 + "มายไม่เรียกใครไปทำในโฟลเดอร์อื่นแทน เพราะงานจะลงผิดที่. "
                 + $"ถ้าจะให้ทำได้: เพิ่ม \"{folder}\"" + (root != folder ? $" (หรือทั้ง \"{root}\")" : "") + " ใน workRoots ของ runners.json "
                 + "(claude เพิ่มใน --add-dir, gemini ใน --include-directories ด้วย) แล้วส่งใหม่";
        var refused = CoworkBrokerSetTask(id, "blocked", note, onlyIf: onlyIf, to: card["createdBy"]?.ToString());
        if (refused) BrokerLog($"cowork: [{id}] not started — {folder} is outside what runners.json allows ({string.Join(", ", allowed)})");
        return refused;
    }
}
