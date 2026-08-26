// MainWindow.McpReaper.cs — the Settings face of MCP housekeeping.
//
// The reaping itself lives in BrainX.Mcp (McpInstances.cs) because that is the
// process that knows whether a client has said anything. This file is only the
// switch and the window onto it: how many servers are running, what they cost,
// and a button that shells out to `brainx-mcp reap`.
//
// Deliberately a shell-out rather than a second implementation. The rules that
// keep a sweep safe — pid+start-time identity, launchers only, never a tree
// kill, an age cutoff for servers with no heartbeat — are subtle enough that
// two copies would drift, and the copy in the GUI would be the one nobody
// tested. One implementation, invoked two ways.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>Owner's switch for automatic cleanup. Off means idle servers
    /// stay resident until their client closes them — which, for Claude
    /// Desktop, is never.</summary>
    private bool _mcpReaperEnabled = true;

    /// <summary>Kept in sync with brainx-mcp's own default (McpInstances
    /// .DefaultIdleHours). There is no UI for it — it is here so the card can
    /// state the real number instead of a hardcoded one, and so a hand-edited
    /// value survives the next time this file is rewritten.</summary>
    private double _mcpReaperIdleHours = 24.0;

    private bool _mcpReaperBusy;

    private string McpIdleLabel => _mcpReaperIdleHours >= 1
        ? $"{_mcpReaperIdleHours:0.#} hour{(_mcpReaperIdleHours == 1 ? "" : "s")}"
        : $"{_mcpReaperIdleHours * 60:0} minutes";

    private void RefreshMcpReaperCard()
    {
        if (McpReaperStatus == null) return;
        McpReaperToggle.IsChecked = _mcpReaperEnabled;
        McpReaperSweepBtn.IsEnabled = !_mcpReaperBusy;

        try
        {
            var procs = Process.GetProcessesByName("brainx-mcp");
            var mb = procs.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } }) / (1024 * 1024);
            var oldest = procs
                .Select(p => { try { return (DateTime?)p.StartTime; } catch { return null; } })
                .Where(t => t is not null).OrderBy(t => t).FirstOrDefault();
            foreach (var p in procs) p.Dispose();

            var age = oldest is { } t ? HumanizeAge(t.ToUniversalTime()) : "unknown";
            McpReaperStatus.Text = procs.Length == 0
                ? "No MCP servers running right now."
                : $"{procs.Length} MCP process(es) · {mb} MB · oldest started {age}. "
                + (_mcpReaperEnabled
                    ? $"A server closes itself once its client process is gone. Going quiet for {McpIdleLabel} also closes it, but only for clients that drop connections without closing them and start a fresh server when they need one — Claude Desktop today. Claude Code sessions are never closed for being idle."
                    : "Automatic cleanup is OFF — nothing closes itself, including servers no client is attached to.");
            McpReaperToggle.Content =
                $"Close a brain server once its client is gone, or after {McpIdleLabel} idle (Claude Desktop only)";
        }
        catch (Exception ex) { McpReaperStatus.Text = $"Could not read the process list: {ex.Message}"; }
    }

    private void McpReaperToggle_Click(object sender, RoutedEventArgs e)
    {
        _mcpReaperEnabled = McpReaperToggle.IsChecked == true;
        SaveSettingsToFile();          // machine-settings.json, via SaveMachineSettings
        RefreshMcpReaperCard();
        // Running launchers re-read the file on a 60s TTL, so this lands without
        // restarting anything — say so, or the owner restarts Claude for nothing.
        StatusText.Text = _mcpReaperEnabled
            ? "🧹 Idle MCP servers will close themselves (takes effect within a minute, no restart needed)."
            : "🧹 Automatic MCP cleanup off — idle servers will stay resident.";
    }

    /// <summary>
    /// Manual sweep, including the servers automatic cleanup will never touch:
    /// the ones started by a build from before heartbeats existed, which cannot
    /// prove they are idle and are judged on age alone.
    ///
    /// Always previews first. This closes processes, and a preview is the only
    /// thing standing between "reclaim a gigabyte" and "cut the brain out of a
    /// session the owner is in the middle of".
    /// </summary>
    private async void McpReaperSweep_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpReaperBusy) return;
        var exe = ResolveBestMcpExe();
        if (exe == null || !File.Exists(exe))
        {
            MessageBox.Show(this, "Could not find brainx-mcp.exe to run the sweep.",
                "MCP housekeeping", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _mcpReaperBusy = true;
        RefreshMcpReaperCard();
        try
        {
            var preview = await RunReapAsync(exe, "reap --legacy --dry-run").ConfigureAwait(true);
            // The window can be gone by the time a process enumeration comes
            // back. Showing a modal owned by a closed window throws, and
            // reaping without the owner ever seeing the preview would be worse.
            if (!IsLoaded) return;
            if (preview.Contains("Nothing to reap", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Every MCP server on this machine is either in use or too new to judge.",
                    "Nothing to clean up", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ok = MessageBox.Show(this,
                preview + "\n\nClose these now?",
                "MCP housekeeping — preview", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) { StatusText.Text = "🧹 Cleanup cancelled — nothing was closed."; return; }

            var done = await RunReapAsync(exe, "reap --legacy").ConfigureAwait(true);
            if (!IsLoaded) return;
            StatusText.Text = "🧹 " + (done.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()
                                       ?? "Cleanup finished.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cleanup could not run: {ex.Message}",
                "MCP housekeeping", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _mcpReaperBusy = false;
            RefreshMcpReaperCard();
        }
    }

    private static async Task<string> RunReapAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start brainx-mcp");
        var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }
}
