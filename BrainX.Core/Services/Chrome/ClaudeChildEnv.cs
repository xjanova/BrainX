using System.Collections;
using System.Diagnostics;

namespace BrainX.Core.Services.Chrome;

/// <summary>
/// The environment a claude.exe child of BrainX runs with.
///
/// brainx-mcp is itself usually a child of Claude Code or Claude Desktop, and
/// those hosts put their own session into the environment: Desktop's proxy
/// (ANTHROPIC_BASE_URL), the account (CLAUDE_CODE_ACCOUNT_UUID), sometimes a
/// token (CLAUDE_CODE_OAUTH_TOKEN, ANTHROPIC_API_KEY), and CLAUDECODE, which
/// makes a nested claude.exe refuse to start. None of it belongs to the
/// claude.exe BrainX starts: that one must authenticate as the owner's OWN
/// Claude Code sign-in, or not at all — borrowing a host's token would make
/// the browser act under whichever app happened to spawn the brain.
///
/// Values are never read here, only names. Nothing in this class logs.
/// </summary>
public static class ClaudeChildEnv
{
    /// <summary>Does this variable belong to a parent Claude session?</summary>
    public static bool IsStripped(string name) =>
        name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Remove every stripped variable from a ProcessStartInfo environment.</summary>
    public static void Sanitize(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.Where(IsStripped).ToList())
            environment.Remove(key);
    }

    /// <summary>The names that WOULD be stripped from this process's own
    /// environment — for a test that has to prove they never reach the child.</summary>
    public static IReadOnlyList<string> StrippedNamesInCurrentProcess()
    {
        var list = new List<string>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && IsStripped(k)) list.Add(k);
        return list;
    }

    /// <summary>
    /// A ProcessStartInfo for a short-lived claude.exe probe (--version, auth
    /// status): no window, no shell, argument list rather than a command line,
    /// UTF-8 pipes and the stripped environment.
    /// </summary>
    public static ProcessStartInfo ProbeStartInfo(string command, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Sanitize(psi.Environment);
        return psi;
    }

    /// <summary>
    /// Run a probe and return (exit code, stdout) or null on timeout / failure
    /// to start. stdin is closed at once so a binary that wrongly fell into the
    /// interactive CLI sees EOF instead of waiting for a prompt. stderr is
    /// drained and dropped.
    /// </summary>
    public static (int ExitCode, string Stdout)? RunProbe(string command, IEnumerable<string> args, TimeSpan timeout)
    {
        Process? p = null;
        try
        {
            p = Process.Start(ProbeStartInfo(command, args));
            if (p == null) return null;
            try { p.StandardInput.Close(); } catch { }
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            // Bounded: the pipe closes with the process, but a grandchild that
            // inherited the handle could keep it open.
            if (!stdout.Wait(TimeSpan.FromSeconds(2))) return null;
            return (p.ExitCode, stdout.Result);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { p?.Dispose(); } catch { }
        }
    }
}
