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

    /// <summary>
    /// Look inside a folder. Folders under the node root (C:\brainx) are SYSTEM +
    /// Administrators only; the unelevated Explorer answers them with "You don't
    /// currently have permission — Continue", and Continue PERMANENTLY adds the
    /// user to that folder's ACL, undoing the node's hardening. So those are
    /// browsed from this elevated process instead: a file dialog, and the chosen
    /// file opens in Notepad (System32, not whatever the association says).
    /// </summary>
    public static void Browse(IWin32Window owner, string folder, string? protectedRoot, string? selectFile = null)
    {
        if (!IsUnder(folder, protectedRoot))
        {
            if (selectFile != null) Reveal(selectFile); else Open(folder);
            return;
        }
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(owner, $"ไม่พบโฟลเดอร์ {folder}", AppInfo.Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var d = new OpenFileDialog
        {
            InitialDirectory = folder,
            Title = $"ไฟล์ใน {folder} — เปิดด้วยสิทธิ์ Administrator",
            Filter = "Log / ข้อความ (*.log;*.txt;*.json)|*.log;*.txt;*.json|ทุกไฟล์ (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            FileName = selectFile != null ? Path.GetFileName(selectFile) : "",
        };
        if (d.ShowDialog(owner) != DialogResult.OK) return;
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add(d.FileName);
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) { ManagerLog.Warn($"open {Path.GetFileName(d.FileName)} in notepad failed: {ex.GetType().Name}"); }
    }

    private static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
