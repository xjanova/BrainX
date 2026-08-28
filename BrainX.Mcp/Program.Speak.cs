using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp speak` — turn text into spoken Thai/English audio, for free.
///
/// WHY edge-tts AND NOT WINDOWS. Measured on the owner's machine and recorded
/// in the vault (2026-08-14): SAPI (`System.Speech`) and WinRT
/// (`Windows.Media.SpeechSynthesis`) expose David / Zira / Mark — every one of
/// them en-US. Feed Thai to those and you get silence or mangled phonemes.
/// Adding a Thai voice is a Windows Settings change only the owner can make.
/// That also rules out the browser: `speechSynthesis` in the Universe WebView2
/// reads the same empty Thai voice list, which is why this lives server-side
/// and hands the page an audio file instead.
///
/// edge-tts needs no account, no API key and spends no credit — it drives the
/// read-aloud service Microsoft Edge itself uses. It does need the network.
///
/// CACHE. Synthesis is a network round trip (~1-2 s), and reports repeat
/// themselves. Cache rules are lifted from the Eve TTS incident note, where
/// each was paid for once:
///   - key on NORMALISED text (collapsed whitespace), or a stray double space
///     forks the cache
///   - write `.part` then rename, so a killed run cannot leave a half MP3 that
///     looks complete
///   - prune opportunistically rather than on a timer
/// </summary>
internal static partial class Program
{
    /// <summary>Default voice. Female Thai neural; the male is th-TH-NiwatNeural.</summary>
    private const string DefaultVoice = "th-TH-PremwadeeNeural";

    /// <summary>
    /// Slower than default on purpose. The default rate machine-guns numbers,
    /// and in a status report the numbers are the part that must land.
    /// </summary>
    private const string DefaultRate = "-8%";

    private static readonly Regex WsRx = new(@"\s+", RegexOptions.Compiled);

    internal static async Task<int> SpeakCliAsync(string[] args)
    {
        string? text = null, outPath = null, voice = null, rate = null, vaultArg = null;
        bool play = false, quiet = false, stdin = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--text" when i + 1 < args.Length: text = args[++i]; break;
                case "--file" when i + 1 < args.Length: text = File.ReadAllText(args[++i]); break;
                case "--stdin": stdin = true; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--voice" when i + 1 < args.Length: voice = args[++i]; break;
                case "--rate" when i + 1 < args.Length: rate = args[++i]; break;
                case "--vault" when i + 1 < args.Length: vaultArg = args[++i]; break;
                case "--play": play = true; break;
                case "--quiet": quiet = true; break;
                case "-h" or "--help" or "help":
                    Console.WriteLine("Usage: brainx-mcp speak (--text TEXT | --file PATH | --stdin) [options]");
                    Console.WriteLine();
                    Console.WriteLine("  --out PATH     write the mp3 here (default: cached under <vault>/.obsidianx/voice)");
                    Console.WriteLine($"  --voice NAME   default {DefaultVoice}  (male: th-TH-NiwatNeural)");
                    Console.WriteLine($"  --rate PCT     default {DefaultRate} — slower, so spoken numbers land");
                    Console.WriteLine("  --play         open the result in the default player when done");
                    Console.WriteLine();
                    Console.WriteLine("Free: edge-tts drives Edge's read-aloud service. No key, no credit, needs network.");
                    Console.WriteLine("Windows' own voices are en-US only on this machine, so Thai cannot use SAPI.");
                    return 0;
            }
        }
        if (stdin) text = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("nothing to say — pass --text, --file or --stdin");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);

        void Say(string s) { if (!quiet) Console.WriteLine(s); }

        var (path, cached, err) = await SynthesizeAsync(
            text!, voice ?? DefaultVoice, rate ?? DefaultRate, outPath, _vaultPath).ConfigureAwait(false);
        if (path == null)
        {
            Console.Error.WriteLine($"speak failed: {err}");
            return 1;
        }

        var bytes = new FileInfo(path).Length;
        Say($"brainx-mcp speak · v{ServerVersion}");
        Say($"  voice: {voice ?? DefaultVoice} @ {rate ?? DefaultRate}");
        Say($"  {(cached ? "cache hit" : "synthesised")}: {path} ({bytes:n0} bytes)");
        if (play)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Console.Error.WriteLine($"  (could not open player: {ex.GetType().Name})"); }
        }
        return 0;
    }

    /// <summary>
    /// Text → mp3, cached. Returns (path, wasCached, error). Never throws for
    /// the ordinary failures (no python, no network, edge-tts missing) — those
    /// come back as an error string so a caller can degrade to silence rather
    /// than fall over.
    /// </summary>
    internal static async Task<(string? Path, bool Cached, string? Error)> SynthesizeAsync(
        string text, string voice, string rate, string? outPath, string vaultPath)
    {
        var norm = WsRx.Replace(text, " ").Trim();
        if (norm.Length == 0) return (null, false, "empty text");

        // The voice and rate are part of the identity of the audio, not just of
        // the request — leave them out and switching voice serves the old file.
        var keySrc = $"{voice}|{rate}|{norm}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySrc)))[..16].ToLowerInvariant();

        var cacheDir = Path.Combine(vaultPath, ".obsidianx", "voice");
        var dest = outPath ?? Path.Combine(cacheDir, key + ".mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return (dest, true, null);

        var py = ResolvePython();
        if (py == null) return (null, false, "python not found on PATH (edge-tts needs it)");

        // Text goes through a FILE, never the command line: a status report
        // carries quotes, newlines and Thai, and Windows argument quoting
        // mangles at least one of those every time.
        var tmpTxt = Path.Combine(Path.GetTempPath(), $"brainx-speak-{key}.txt");
        var part = dest + ".part";
        try
        {
            await File.WriteAllTextAsync(tmpTxt, norm, new UTF8Encoding(false)).ConfigureAwait(false);

            var psi = new ProcessStartInfo(py)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // `python -m edge_tts` (UNDERSCORE) — the console script edge-tts.exe
            // installs to a Scripts dir that is not on PATH for a Store Python.
            //
            // `--rate=-8%` must be ONE token. Passed as two, argparse reads the
            // leading '-' of "-8%" as the start of another option and dies with
            // "expected one argument". Same for --volume/--pitch if they are
            // ever added.
            foreach (var a in new[] { "-m", "edge_tts", "--voice", voice, $"--rate={rate}",
                                      "--file", tmpTxt, "--write-media", part })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return (null, false, "could not start python");
            var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);

            if (p.ExitCode != 0 || !File.Exists(part) || new FileInfo(part).Length == 0)
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                var why = stderr.Trim();
                if (why.Contains("No module named", StringComparison.OrdinalIgnoreCase))
                    why = "edge-tts not installed — run: python -m pip install --user edge-tts";
                return (null, false, string.IsNullOrWhiteSpace(why) ? $"edge-tts exited {p.ExitCode}" : why);
            }

            // Atomic: a killed run leaves a .part, never a truncated .mp3 that
            // the cache would then serve forever as a complete file.
            File.Move(part, dest, overwrite: true);
        }
        catch (Exception ex) { return (null, false, $"{ex.GetType().Name}: {ex.Message}"); }
        finally { try { File.Delete(tmpTxt); } catch { } }

        PruneVoiceCache(cacheDir);
        return (dest, false, null);
    }

    private static string? ResolvePython()
    {
        foreach (var exe in new[] { "python", "python3", "py" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                if (p == null) continue;
                p.WaitForExit(5000);
                if (p.ExitCode == 0) return exe;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Opportunistic, cheap, and bounded: only every ~20th synthesis, and it
    /// only deletes. A cache that needs a scheduled job to stay small is a
    /// second thing that can break.
    /// </summary>
    private static void PruneVoiceCache(string dir)
    {
        try
        {
            if (Environment.TickCount % 20 != 0) return;
            var files = new DirectoryInfo(dir).GetFiles("*.mp3");
            if (files.Length <= 400) return;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc).Take(files.Length - 400))
                try { f.Delete(); } catch { }
        }
        catch { }
    }
}
