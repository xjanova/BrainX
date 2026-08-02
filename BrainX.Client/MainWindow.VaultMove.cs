// MainWindow.VaultMove.cs — moving the vault to a new location, for real.
//
// ChangeVaultPath_Click used to be a MessageBox telling the owner to restart
// the app with a command-line argument — instructions, not a feature. This is
// the feature: pick a folder, migrate the data, remember the choice, relaunch.
//
// The migration is gated on brain.db being CLOSED. Four brainx-mcp processes
// typically hold it (one per Claude window), and moving a live WAL database
// produces a torn copy that reads as corruption later. Rather than migrate
// "best effort" and hope, the move refuses to start until the writers are
// gone: an owner who has to close Claude first is inconvenienced; an owner
// with a silently torn brain.db is robbed.
//
// The chosen path is remembered OUTSIDE the vault (%LOCALAPPDATA%\BrainX\
// vault-path.txt) — a pointer stored inside the thing it points at would move
// along with it and never be findable on the next launch. On restart,
// AutoOnboard sees the new _vaultPath differs from BRAINX_VAULT in the Claude
// configs and rewrites them itself; that self-heal has existed for versions.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace BrainX.Client;

public partial class MainWindow
{
    /// <summary>Where the app remembers which vault to open. Read at startup
    /// (CLI argument still wins), written on every successful launch and on
    /// every successful move — so it self-heals if hand-edited wrong.</summary>
    private static string VaultPointerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "vault-path.txt");

    private static string? ReadVaultPointer()
    {
        try
        {
            if (!File.Exists(VaultPointerPath)) return null;
            var p = File.ReadAllText(VaultPointerPath).Trim();
            return p.Length > 0 && Directory.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    private static void WriteVaultPointer(string vaultPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VaultPointerPath)!);
            File.WriteAllText(VaultPointerPath, vaultPath);
        }
        catch { /* pointer is a convenience, not a dependency */ }
    }

    private void MoveVault_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "เลือกโฟลเดอร์ปลายทางสำหรับ vault (ต้องว่าง)"
        };
        if (dlg.ShowDialog() != true) return;
        var target = dlg.FolderName;

        // Refuse the classic foot-guns before touching anything.
        if (string.IsNullOrWhiteSpace(target)) return;
        var fullTarget = Path.GetFullPath(target);
        var fullSource = Path.GetFullPath(_vaultPath);
        if (string.Equals(fullTarget, fullSource, StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show("ปลายทางเป็นที่เดิม", "Move vault"); return; }
        if (fullTarget.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show("ปลายทางอยู่ข้างใน vault เดิม — ย้ายเข้าไปในตัวเองไม่ได้", "Move vault"); return; }
        if (Directory.Exists(fullTarget) && Directory.EnumerateFileSystemEntries(fullTarget).Any())
        { MessageBox.Show("โฟลเดอร์ปลายทางต้องว่าง", "Move vault"); return; }

        var sameVolume = string.Equals(Path.GetPathRoot(fullSource), Path.GetPathRoot(fullTarget),
                                       StringComparison.OrdinalIgnoreCase);
        var confirm = MessageBox.Show(
            $"ย้าย vault\n\nจาก:  {fullSource}\nไป:    {fullTarget}\n\n" +
            (sameVolume
                ? "ไดรฟ์เดียวกัน — ย้ายทันที (rename)\n"
                : "คนละไดรฟ์ — คัดลอกทั้งหมด แล้วเก็บต้นทางไว้เป็น backup (ไม่ลบให้อัตโนมัติ)\n") +
            "\nก่อนย้าย: ปิดหน้าต่าง Claude / Claude Desktop ทุกบานที่ต่อสมองนี้อยู่ " +
            "(brainx-mcp ถือไฟล์ brain.db ไว้ — ถ้ายังเปิดอยู่การย้ายจะถูกปฏิเสธ)\n\n" +
            "แอปจะรีสตาร์ทเองหลังย้ายเสร็จ ดำเนินการต่อ?",
            "Move vault", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        // Release everything THIS process holds open in the vault.
        try { _vaultWatcher?.Dispose(); } catch { }
        _vaultWatcher = null;
        _accessLogTimer?.Stop();
        try { _storage?.Dispose(); } catch { }
        _storage = null;

        // The writers gate. Exclusive open succeeds only when no MCP session
        // (or anything else) has brain.db open. WAL sidecars linger after a
        // clean close and are part of the database — they move with it.
        var db = Path.Combine(fullSource, ".obsidianx", "brain.db");
        if (File.Exists(db))
        {
            try
            {
                using var probe = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                MessageBox.Show(
                    "brain.db ยังถูกเปิดอยู่โดยโปรเซสอื่น (มักเป็น Claude ที่ยังเปิดค้าง)\n" +
                    "ปิดหน้าต่าง Claude ทุกบานแล้วลองใหม่ — ยังไม่มีอะไรถูกย้าย",
                    "Move vault", MessageBoxButton.OK, MessageBoxImage.Warning);
                RestartVaultMachinery();
                return;
            }
        }

        try
        {
            if (sameVolume)
            {
                // A rename: atomic at the directory level, no data traversal.
                // If the target's parent doesn't exist, create it first.
                Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
                if (Directory.Exists(fullTarget)) Directory.Delete(fullTarget);   // verified empty above
                Directory.Move(fullSource, fullTarget);
            }
            else
            {
                StatusText.Text = "กำลังคัดลอก vault …";
                CopyDirectory(fullSource, fullTarget);
                var (srcFiles, dstFiles) = (CountFiles(fullSource), CountFiles(fullTarget));
                if (dstFiles < srcFiles)
                    throw new IOException($"copy incomplete: {dstFiles}/{srcFiles} files");
                // Source is deliberately KEPT. Deleting the only other copy of
                // a knowledge base in the same breath as writing its first
                // copy is how backups die; the owner deletes it when ready.
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ย้ายไม่สำเร็จ: {ex.Message}\n\nต้นทางยังอยู่ครบ", "Move vault",
                MessageBoxButton.OK, MessageBoxImage.Error);
            RestartVaultMachinery();
            return;
        }

        WriteVaultPointer(fullTarget);

        // Relaunch through a delay shim: the single-instance mutex is held
        // until this process exits, and a child started directly would hit
        // the gate and bail before we're gone.
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping -n 3 127.0.0.1 >nul && start \"\" \"{exe}\" \"{fullTarget}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        });
        Application.Current.Shutdown();
    }

    /// <summary>Bring the watchers back after an aborted move — the owner is
    /// staying on the old vault and the app must keep working.</summary>
    private void RestartVaultMachinery()
    {
        try
        {
            // Same factory as the settings page — the owner may be on File or
            // MySql, and an aborted move must not silently switch providers.
            _storage = BrainX.Core.Services.BrainStorageFactory.Create(
                _storageProvider, _vaultPath, _mySqlConnString);
        }
        catch { _storage = null; }
        _accessLogTimer?.Start();
        StartVaultWatcher();
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: false);
    }

    private static int CountFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
}
