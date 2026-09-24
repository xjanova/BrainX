using System.Diagnostics;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.UI;

/// <summary>
/// Open URLs and folders WITHOUT passing on this app's admin token. A plain
/// ShellExecute from an elevated process starts the browser elevated — browsing
/// the web as administrator. explorer.exe hands the request to the already
/// running, unelevated shell and exits, so the browser/folder opens as the user.
/// </summary>
internal static class Shell
{
    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public static void Open(string target)
    {
        try
        {
            var psi = new ProcessStartInfo(Explorer) { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) { ManagerLog.Warn($"open {target} failed: {ex.GetType().Name}"); }
    }

    /// <summary>Open the folder with the file selected.</summary>
    public static void Reveal(string file)
    {
        try
        {
            if (!File.Exists(file)) { Open(Path.GetDirectoryName(file) ?? file); return; }
            // One token, /select,"path" — a Windows path cannot contain a quote, so this cannot break out.
            var psi = new ProcessStartInfo(Explorer, $"/select,\"{file}\"") { UseShellExecute = false };
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) { ManagerLog.Warn($"reveal {file} failed: {ex.GetType().Name}"); }
    }

    /// <summary>The web dashboard on localhost (same origin the installer's shortcut uses, so a saved login still applies).</summary>
    public static string DashboardUrl(Uri nodeBase)
    {
        var b = new UriBuilder(nodeBase);
        if (b.Host is "127.0.0.1" or "[::1]") b.Host = "localhost";
        b.Path = "/";
        return b.Uri.ToString();
    }
}
