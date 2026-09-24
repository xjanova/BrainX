// MainWindow.ServerHost.cs — the door to the Server Manager, on the one PC
// that runs the BrainX server.
//
// Owner decision (2026-09-24): server administration is NOT part of this app.
// It lives in BrainX Server Manager, a separate elevated program that ships
// inside the node package (<node app dir>\manager\) and updates with the node,
// so its admin API and its UI never drift apart. Hiding admin screens behind a
// license in a publicly shipped client would protect nothing (the real gate is
// the node's local-only, owner-token admin API) — this card only appears when
// the BrainXNode service is installed on this machine, and only launches.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace BrainX.Client;

public partial class MainWindow
{
    private const string NodeServiceName = "BrainXNode";
    private const string ServerManagerExeName = "BrainX.ServerManager.exe";

    /// <summary>
    /// The executable the BrainXNode service runs, or null when this PC has no
    /// such service. Reads the service's ImagePath — readable without admin.
    /// </summary>
    internal static string? FindNodeServiceExe()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{NodeServiceName}");
            if (key?.GetValue("ImagePath") is not string image || string.IsNullOrWhiteSpace(image)) return null;
            image = Environment.ExpandEnvironmentVariables(image.Trim());

            // ImagePath is either "C:\path with spaces\x.exe" args… or C:\path\x.exe args…
            if (image.StartsWith('"'))
            {
                var end = image.IndexOf('"', 1);
                return end > 1 ? image[1..end] : null;
            }
            var exeEnd = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd > 0 ? image[..(exeEnd + 4)] : null;
        }
        catch
        {
            // No registry access, or a malformed value: behave as "no server here".
            return null;
        }
    }

    /// <summary>The Server Manager shipped next to that node exe, if the node package has it yet.</summary>
    internal static string? FindServerManagerExe(string nodeExe)
    {
        var dir = Path.GetDirectoryName(nodeExe);
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir, "manager", ServerManagerExeName);
        return File.Exists(path) ? path : null;
    }

    private void RefreshServerHostPanel()
    {
        if (ServerHostPanel == null) return;
        var nodeExe = FindNodeServiceExe();
        if (nodeExe == null)
        {
            ServerHostPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ServerHostPanel.Visibility = Visibility.Visible;
        var manager = FindServerManagerExe(nodeExe);
        OpenServerManagerBtn.IsEnabled = manager != null;
        ServerHostStatus.Text = manager != null
            ? "Start, stop and update the server, see BrainX Cloud customers and read the server log. Opens as administrator."
            : "The Server Manager arrives with the next server update — it installs itself next to the server.";
    }

    private void OpenServerManager_Click(object sender, RoutedEventArgs e)
    {
        var nodeExe = FindNodeServiceExe();
        var manager = nodeExe == null ? null : FindServerManagerExe(nodeExe);
        if (manager == null)
        {
            RefreshServerHostPanel();
            return;
        }

        try
        {
            // "runas" asks for elevation BEFORE the exe is opened: the node
            // hardens its install folder, and an unelevated shell may not be
            // allowed to read the manifest that would otherwise trigger UAC.
            // This app itself stays unelevated.
            Process.Start(new ProcessStartInfo(manager)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(manager)!,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The owner said no to the UAC prompt — nothing went wrong.
        }
        catch (Exception)
        {
            ServerHostStatus.Text = "The Server Manager couldn't be opened from here. Open it from the Start menu instead.";
        }
    }
}
