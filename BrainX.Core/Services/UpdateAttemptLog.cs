using Newtonsoft.Json;

namespace BrainX.Core.Services;

/// <summary>
/// Remembers, across restarts, that an update was attempted and did not take.
///
/// WHY THIS HAS TO BE ON DISK. The auto-updater's only guard was an in-memory
/// bool (<c>_autoUpdateFiring</c>), and the whole point of applying an update is
/// that the process ends — so the guard died with every attempt. When a package
/// could not be applied (2026-08-01: a handle on the <c>current</c> directory
/// that Velopack could not rename), the app relaunched, checked, downloaded,
/// applied, failed, relaunched: **four PIDs in 75 seconds**, with no counter
/// anywhere that could notice it was the same failure over and over.
///
/// A counter is only a counter if it outlives the thing it is counting.
///
/// Failure is inferred, not reported: nothing survives to write "that didn't
/// work". So an attempt is recorded BEFORE the process gives itself up, and the
/// next launch decides — if we came back still running the old version, the
/// attempt failed; if we came back as the target, it worked and the slate is
/// wiped.
/// </summary>
public sealed class UpdateAttemptLog
{
    /// <summary>Give up after this many consecutive failures at the SAME version
    /// and let the user apply manually. Three is enough to ride out a transient
    /// lock while still turning a permanent one into a message instead of a
    /// machine that will not stay open.</summary>
    public const int MaxConsecutiveFailures = 3;

    public string? TargetVersion { get; set; }
    public int Failures { get; set; }
    public DateTime? FirstAttemptUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }

    [JsonIgnore] private string _path = "";

    public static UpdateAttemptLog Load(string dir)
    {
        var path = Path.Combine(dir, "update-attempts.json");
        try
        {
            if (File.Exists(path))
            {
                var log = JsonConvert.DeserializeObject<UpdateAttemptLog>(File.ReadAllText(path));
                if (log != null) { log._path = path; return log; }
            }
        }
        catch { /* a corrupt log must never stop the app updating */ }
        return new UpdateAttemptLog { _path = path };
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { /* best effort — never block an update on bookkeeping */ }
    }

    /// <summary>
    /// Call once at startup with the version actually running. If it matches the
    /// version we were trying to reach, the update landed and the history is
    /// cleared. Anything still recorded after this is a genuine failure.
    /// </summary>
    public void NoteRunningVersion(string current)
    {
        if (string.IsNullOrEmpty(TargetVersion)) return;
        if (!VersionsMatch(TargetVersion, current)) return;
        TargetVersion = null; Failures = 0; FirstAttemptUtc = null; LastAttemptUtc = null;
        Save();
    }

    /// <summary>True when this exact version has already failed too many times
    /// and should no longer be applied without the user asking for it.</summary>
    public bool ShouldStopTrying(string target) =>
        TargetVersion != null
        && VersionsMatch(TargetVersion, target)
        && Failures >= MaxConsecutiveFailures;

    /// <summary>
    /// Record an attempt. MUST be called — and flushed — before handing control
    /// to the updater, because nothing of ours runs afterwards to record it.
    /// </summary>
    public void RecordAttempt(string target)
    {
        if (TargetVersion == null || !VersionsMatch(TargetVersion, target))
        {
            TargetVersion = target; Failures = 0; FirstAttemptUtc = DateTime.UtcNow;
        }
        Failures++;
        LastAttemptUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>Human-readable state for the update card, or null when there is
    /// nothing worth saying.</summary>
    public string? Describe() =>
        TargetVersion == null || Failures == 0 ? null
        : Failures >= MaxConsecutiveFailures
            ? $"v{TargetVersion} failed to apply {Failures}× — automatic install is paused. Use “Restart & apply” to try manually."
            : $"v{TargetVersion} did not apply on the last attempt ({Failures}/{MaxConsecutiveFailures}).";

    // Velopack reports "2.0.204" while the assembly carries
    // "2.0.204+abbfc64..."; compare on the numeric triple only.
    private static bool VersionsMatch(string a, string b) =>
        Trim(a).Equals(Trim(b), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string v)
    {
        var s = v.TrimStart('v', 'V');
        var cut = s.IndexOfAny(['+', '-', ' ']);
        return cut > 0 ? s[..cut] : s;
    }
}
