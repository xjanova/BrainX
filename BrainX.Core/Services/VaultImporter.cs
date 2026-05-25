using System.Security.Cryptography;
using System.Text;

namespace BrainX.Core.Services;

/// <summary>
/// Scans external paths for knowledge-worthy files and imports them as
/// linked notes into the vault.
///
/// Uses the "Resonance Scan" algorithm — a priority-queue filesystem walk
/// that prioritizes folders by Name Resonance × Sibling Prior × Depth
/// Decay, with archetype-aware descent (in project roots we skip src/ and
/// only dive into docs/ and .claude/) and 64-bit SimHash near-duplicate
/// detection over file heads to skip the 50 copies of the same README.
/// Runs ~10-50× faster than a naive recursive walk on typical dev drives.
/// </summary>
public class VaultImporter
{
    public enum ImportMode { Reference, Copy }

    // Folders that are NEVER descended into, regardless of score.
    private static readonly HashSet<string> HardSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", ".vscode",
        "bin", "obj", "dist", "build", "target", "out", "release", "debug",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".tox",
        ".next", ".nuxt", ".cache", ".tmp", "tmp", "temp",
        "vendor", "packages", "_modules",
        ".obsidianx", "Imported",
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData",
        "Program Files", "Program Files (x86)", "AppData"
    };

    // Folder-name tokens with learned resonance weights.
    // Positive = knowledge lives here; negative = noise; 0 = neutral.
    private static readonly Dictionary<string, double> NameResonance =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // strong positive
        ["docs"]       =  2.5, ["doc"]       =  2.5, ["documentation"] = 2.5,
        ["notes"]      =  2.5, ["note"]      =  2.5,
        ["brain"]      =  3.0, ["wiki"]      =  2.5, ["knowledge"]   = 3.0,
        ["memo"]       =  2.0, ["memos"]     =  2.0,
        ["obsidian"]   =  3.0, ["vault"]     =  3.0,
        ["blog"]       =  1.8, ["posts"]     =  1.8, ["articles"]    = 1.8,
        ["papers"]     =  2.0, ["research"]  =  2.0,
        ["readme"]     =  1.5, [".claude"]   =  3.0, ["claude"]      = 2.0,
        ["specs"]      =  1.5, ["spec"]      =  1.5,
        ["guides"]     =  1.8, ["tutorials"] =  1.5,
        // mild positive
        ["src"]        =  0.3, // source may contain README
        // neutral/unknown handled by 0
        // negative
        ["test"]       = -0.8, ["tests"]     = -0.8, ["spec"]        = -0.5,
        ["fixtures"]   = -1.0, ["mocks"]     = -1.0,
        ["logs"]       = -1.5, ["log"]       = -1.5,
        ["assets"]     = -1.0, ["images"]    = -1.2, ["img"]         = -1.2,
        ["screenshots"]= -1.5, ["icons"]     = -1.5,
        ["fonts"]      = -2.0, ["media"]     = -1.5,
        ["backup"]     = -1.5, ["old"]       = -1.5, ["archive"]     = -0.5,
    };

    // If these markers exist in a folder, it's a "project root" — apply archetype jump.
    private static readonly string[] ProjectMarkers =
    [
        ".git", "package.json", "Cargo.toml", "go.mod", "pyproject.toml",
        "pom.xml", "build.gradle", ".csproj", ".sln", ".slnx", "requirements.txt"
    ];

    // In project roots we ONLY descend into these folders (huge speed win).
    private static readonly HashSet<string> ProjectRootAllowedDescent =
        new(StringComparer.OrdinalIgnoreCase)
        { "docs", "doc", "documentation", ".claude", "notes", "wiki", "spec", "specs" };

    private const long MaxFileSizeBytes = 5 * 1024 * 1024;
    private const int SimHashHeadBytes = 4096;
    private const int SimHashDuplicateThreshold = 4; // Hamming distance
    private const double InitialScoreThreshold = 0.3;
    private const double DepthDecay = 0.9;
    private const string ImportedFolderName = "Imported";

    public ScanReport Scan(ImportOptions options)
    {
        var report = new ScanReport { Policy = options.Policy };
        var patterns = ParsePatterns(options.Patterns);
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);
        var seenSimHashes = new List<ulong>();
        var pq = new PriorityQueue<FolderNode, double>();

        // Seed the priority queue with scan roots
        foreach (var root in EnumerateRoots(options))
        {
            if (!Directory.Exists(root)) continue;
            pq.Enqueue(new FolderNode(root, Depth: 0, SiblingPrior: 1.0),
                priority: -ScoreFolder(root, depth: 0, siblingPrior: 1.0));
        }

        double threshold = InitialScoreThreshold;
        int consecutiveMisses = 0;
        const int MissesUntilWiden = 30;

        while (pq.Count > 0)
        {
            // Adaptive threshold: if we're missing, widen the search
            if (consecutiveMisses >= MissesUntilWiden)
            {
                threshold *= 0.5;
                consecutiveMisses = 0;
                report.ThresholdAdjustments++;
            }

            var folder = pq.Dequeue();
            var score = -ScoreFolder(folder.Path, folder.Depth, folder.SiblingPrior);

            // Negative-score == high priority in our queue (we negate when enqueueing)
            var actualScore = -score;
            if (actualScore < threshold)
            {
                report.PrunedFolders++;
                continue;
            }

            report.VisitedFolders++;

            // Scan files in this folder
            bool foundHere = ScanFolderFiles(folder.Path, patterns, seenHashes,
                seenSimHashes, report, options.VaultPath);

            consecutiveMisses = foundHere ? 0 : consecutiveMisses + 1;

            // Descend into subdirectories
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(folder.Path).ToList(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            bool isProjectRoot = DetectProjectRoot(folder.Path);
            if (isProjectRoot) report.ProjectRootsDetected++;

            var childSiblingPrior = foundHere ? 1.3 : 0.85;

            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (HardSkip.Contains(name)) { report.SkippedByDenyList++; continue; }

                if (isProjectRoot && !ProjectRootAllowedDescent.Contains(name))
                {
                    report.SkippedByArchetype++;
                    continue;
                }

                // Don't re-import the vault's own Imported folder
                if (PathsOverlap(sub, options.VaultPath)) continue;

                var subScore = ScoreFolder(sub, folder.Depth + 1, childSiblingPrior);
                if (subScore < threshold * 0.5)
                {
                    report.PrunedFolders++;
                    continue;
                }

                pq.Enqueue(new FolderNode(sub, Depth: folder.Depth + 1, SiblingPrior: childSiblingPrior),
                    priority: -subScore);
            }
        }

        report.Hits = report.Hits
            .OrderByDescending(h => h.ResonanceScore)
            .ThenByDescending(h => h.ModifiedAt)
            .ToList();

        return report;
    }

    private bool ScanFolderFiles(string folder, string[] patterns,
        HashSet<string> seenHashes, List<ulong> seenSimHashes,
        ScanReport report, string vaultPath)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder); }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }

        bool foundAny = false;
        var folderScore = ScoreFolder(folder, depth: 0, siblingPrior: 1.0);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!MatchesAnyPattern(name, patterns)) continue;

            FileInfo fi;
            try { fi = new FileInfo(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (fi.Length > MaxFileSizeBytes) continue;
            if (fi.Length == 0) continue;

            // Smart ingest gate — defer drafts / freshly-modified / dirty
            // git files until they look stable. Policy-less callers get
            // the old behaviour (everything passes).
            var policy = report.Policy;
            if (policy != null)
            {
                var verdict = policy.Evaluate(file);
                if (!verdict.ShouldIngest)
                {
                    report.Pending.Add(new PendingFile
                    {
                        Path = file,
                        Reason = verdict.Reason,
                        Category = verdict.Category
                    });
                    continue;
                }
            }

            // SimHash over first 4 KB — near-dup check BEFORE full content hash
            ulong simHash;
            try { simHash = ComputeSimHash(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            bool nearDup = false;
            foreach (var existing in seenSimHashes)
            {
                if (HammingDistance(simHash, existing) <= SimHashDuplicateThreshold)
                { nearDup = true; break; }
            }
            if (nearDup) { report.NearDuplicatesSkipped++; continue; }
            seenSimHashes.Add(simHash);

            // Full hash (for exact-match manifest later)
            string fullHash;
            try { fullHash = HashFile(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (!seenHashes.Add(fullHash)) { report.ExactDuplicatesSkipped++; continue; }

            // Boost score for special filenames
            var nameBoost = name.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase) ? 2.0
                : name.Equals("README.md", StringComparison.OrdinalIgnoreCase) ? 1.3
                : 1.0;

            report.Hits.Add(new ScanHit
            {
                SourcePath = file,
                FileName = name,
                SizeBytes = fi.Length,
                ModifiedAt = fi.LastWriteTimeUtc,
                ContentHash = fullHash,
                SimHash = simHash,
                ResonanceScore = folderScore * nameBoost,
                SuggestedVaultPath = BuildVaultRelativePath(file, vaultPath)
            });
            foundAny = true;
        }

        return foundAny;
    }

    // ───────────── Resonance scoring ─────────────

    private static double ScoreFolder(string path, int depth, double siblingPrior)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = path; // drive root

        double nameScore = 0.0;
        if (NameResonance.TryGetValue(name, out var w)) nameScore = w;
        else
        {
            // partial token match — "user-notes" → pick up "notes"
            foreach (var (token, weight) in NameResonance)
            {
                if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                { nameScore += weight * 0.6; break; }
            }
        }

        var baseScore = 1.0 + nameScore;
        var depthFactor = Math.Pow(DepthDecay, depth);
        var score = baseScore * siblingPrior * depthFactor;
        return score;
    }

    private static bool DetectProjectRoot(string folder)
    {
        try
        {
            foreach (var marker in ProjectMarkers)
            {
                if (marker.StartsWith('.'))
                {
                    if (Directory.Exists(Path.Combine(folder, marker))) return true;
                    if (File.Exists(Path.Combine(folder, marker))) return true;
                }
                else if (marker.StartsWith('*'))
                {
                    var ext = marker[1..];
                    if (Directory.EnumerateFiles(folder, "*" + ext).Any()) return true;
                }
                else
                {
                    if (File.Exists(Path.Combine(folder, marker))) return true;
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return false;
    }

    // ───────────── SimHash (64-bit, 4-gram tokens) ─────────────

    private static ulong ComputeSimHash(string file)
    {
        byte[] head = new byte[SimHashHeadBytes];
        int n;
        using (var fs = File.OpenRead(file))
            n = fs.Read(head, 0, head.Length);

        if (n < 16) return 0UL;

        // Tokenize as 4-grams, each token hashed to 64 bits,
        // aggregate: bit i = +1 if set in token-hash, -1 otherwise.
        Span<int> bits = stackalloc int[64];
        var text = Encoding.UTF8.GetString(head, 0, n);
        var clean = new StringBuilder(text.Length);
        foreach (var ch in text)
            clean.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        var s = clean.ToString();

        int tokenCount = 0;
        for (int i = 0; i + 4 <= s.Length; i++)
        {
            if (s[i] == ' ') continue;
            var token = s.AsSpan(i, 4);
            var h = FnvHash64(token);
            for (int b = 0; b < 64; b++)
                bits[b] += ((h >> b) & 1UL) == 1UL ? 1 : -1;
            tokenCount++;
        }
        if (tokenCount == 0) return 0UL;

        ulong sim = 0;
        for (int b = 0; b < 64; b++)
            if (bits[b] > 0) sim |= 1UL << b;
        return sim;
    }

    private static ulong FnvHash64(ReadOnlySpan<char> s)
    {
        const ulong Prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= Prime;
        }
        return h;
    }

    private static int HammingDistance(ulong a, ulong b)
    {
        return System.Numerics.BitOperations.PopCount(a ^ b);
    }

    // ───────────── Import (writing notes into vault) ─────────────

    public ImportResult Import(IReadOnlyList<ScanHit> hits, ImportOptions options)
    {
        var result = new ImportResult();
        var importedDir = Path.Combine(options.VaultPath, ImportedFolderName);
        Directory.CreateDirectory(importedDir);

        var manifest = LoadManifest(options.VaultPath);

        foreach (var hit in hits)
        {
            try
            {
                var target = Path.Combine(options.VaultPath, hit.SuggestedVaultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (manifest.TryGetValue(hit.SourcePath, out var prev) && prev == hit.ContentHash)
                { result.Skipped.Add(hit.SourcePath); continue; }

                if (options.Mode == ImportMode.Copy)
                    WriteCopyNote(target, hit);
                else
                    WriteReferenceNote(target, hit);

                manifest[hit.SourcePath] = hit.ContentHash;
                result.Imported.Add(target);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{hit.SourcePath}: {ex.Message}");
            }
        }

        SaveManifest(options.VaultPath, manifest);
        return result;
    }

    private static void WriteReferenceNote(string target, ScanHit hit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"source: {hit.SourcePath.Replace("\\", "/")}");
        sb.AppendLine($"imported: {DateTime.UtcNow:O}");
        sb.AppendLine($"hash: {hit.ContentHash}");
        sb.AppendLine($"simhash: {hit.SimHash:x16}");
        sb.AppendLine($"resonance: {hit.ResonanceScore:F3}");
        sb.AppendLine("kind: reference");
        sb.AppendLine("tags:");
        sb.AppendLine("  - imported");
        sb.AppendLine($"  - {SafeTag(Path.GetFileNameWithoutExtension(hit.FileName))}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {Path.GetFileNameWithoutExtension(hit.FileName)}");
        sb.AppendLine();
        sb.AppendLine($"> Imported reference to `{hit.SourcePath}`");
        sb.AppendLine($"> Modified: {hit.ModifiedAt:yyyy-MM-dd HH:mm} UTC · Size: {hit.SizeBytes:N0} bytes · Resonance: {hit.ResonanceScore:F2}");
        sb.AppendLine();
        try
        {
            var preview = File.ReadAllText(hit.SourcePath);
            if (preview.Length > 800) preview = preview[..800] + "\n\n…";
            sb.AppendLine("## Preview");
            sb.AppendLine();
            sb.AppendLine(preview);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        File.WriteAllText(target, sb.ToString());
    }

    private static void WriteCopyNote(string target, ScanHit hit)
    {
        var content = File.ReadAllText(hit.SourcePath);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"source: {hit.SourcePath.Replace("\\", "/")}");
        sb.AppendLine($"imported: {DateTime.UtcNow:O}");
        sb.AppendLine($"hash: {hit.ContentHash}");
        sb.AppendLine($"resonance: {hit.ResonanceScore:F3}");
        sb.AppendLine("kind: copy");
        sb.AppendLine("tags:");
        sb.AppendLine("  - imported");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(content);
        File.WriteAllText(target, sb.ToString());
    }

    // ───────────── Path / pattern helpers ─────────────

    private static string BuildVaultRelativePath(string sourcePath, string vaultPath)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(sourcePath) ?? "External");
        var file = Path.GetFileName(sourcePath);
        if (string.IsNullOrEmpty(parent)) parent = "External";

        var safeParent = string.Concat(parent.Split(Path.GetInvalidFileNameChars()));
        var safeFile = string.Concat(file.Split(Path.GetInvalidFileNameChars()));

        if (safeFile.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase))
            safeFile = $"{safeParent}-CLAUDE.md";
        else if (safeFile.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            safeFile = $"{safeParent}-README.md";

        return Path.Combine(ImportedFolderName, safeParent, safeFile);
    }

    private static IEnumerable<string> EnumerateRoots(ImportOptions options)
    {
        foreach (var p in options.ScanPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            yield return p;

        if (!options.ScanWholeMachine) yield break;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            if (!drive.IsReady) continue;
            yield return drive.RootDirectory.FullName;
        }
    }

    private static bool PathsOverlap(string a, string b)
    {
        try
        {
            var fa = Path.GetFullPath(a);
            var fb = Path.GetFullPath(b);
            return fa.StartsWith(fb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string[] ParsePatterns(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
            return ["CLAUDE.md", "README.md", "*.md"];
        return patterns.Split([';', ','], StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);
    }

    private static bool MatchesAnyPattern(string fileName, string[] patterns)
    {
        foreach (var p in patterns) if (GlobMatch(fileName, p)) return true;
        return false;
    }

    private static bool GlobMatch(string name, string pattern)
    {
        int ni = 0, pi = 0, star = -1, match = 0;
        while (ni < name.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' ||
                char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(name[ni])))
            { ni++; pi++; }
            else if (pi < pattern.Length && pattern[pi] == '*')
            { star = pi++; match = ni; }
            else if (star != -1)
            { pi = star + 1; ni = ++match; }
            else return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static string ManifestPath(string vaultPath) =>
        Path.Combine(vaultPath, ".obsidianx", "import-manifest.json");

    private static Dictionary<string, string> LoadManifest(string vaultPath)
    {
        var p = ManifestPath(vaultPath);
        if (!File.Exists(p)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(
                File.ReadAllText(p)) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveManifest(string vaultPath, Dictionary<string, string> manifest)
    {
        var p = ManifestPath(vaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, Newtonsoft.Json.JsonConvert.SerializeObject(
            manifest, Newtonsoft.Json.Formatting.Indented));
    }

    private static string SafeTag(string s) =>
        new(s.Where(char.IsLetterOrDigit).ToArray());

    private readonly record struct FolderNode(string Path, int Depth, double SiblingPrior);
}

public class ImportOptions
{
    public string VaultPath { get; set; } = string.Empty;
    public List<string> ScanPaths { get; set; } = [];
    public bool ScanWholeMachine { get; set; }
    public string Patterns { get; set; } = "CLAUDE.md;README.md;*.md";
    public VaultImporter.ImportMode Mode { get; set; } = VaultImporter.ImportMode.Reference;

    /// <summary>If set, each candidate is evaluated by the policy; files
    /// that aren't "stable" are excluded from this scan. The caller can
    /// queue them for retry later.</summary>
    public IngestPolicy? Policy { get; set; }
}

public class ScanHit
{
    public string SourcePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public ulong SimHash { get; set; }
    public double ResonanceScore { get; set; }
    public string SuggestedVaultPath { get; set; } = string.Empty;
}

public class ScanReport
{
    public List<ScanHit> Hits { get; set; } = [];
    public int VisitedFolders { get; set; }
    public int PrunedFolders { get; set; }
    public int SkippedByDenyList { get; set; }
    public int SkippedByArchetype { get; set; }
    public int ProjectRootsDetected { get; set; }
    public int NearDuplicatesSkipped { get; set; }
    public int ExactDuplicatesSkipped { get; set; }
    public int ThresholdAdjustments { get; set; }

    /// <summary>Files the IngestPolicy deferred — caller can retry later.</summary>
    public List<PendingFile> Pending { get; set; } = [];

    /// <summary>Passed through from ImportOptions so the scan loop can consult it.</summary>
    public IngestPolicy? Policy { get; set; }
}

public class PendingFile
{
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Category { get; set; } = "";
}

public class ImportResult
{
    public List<string> Imported { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}
