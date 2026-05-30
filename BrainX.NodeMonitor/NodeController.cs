using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BrainX.NodeMonitor;

/// <summary>
/// Launches and stops the brainx-node server (BrainX.Server) as a hidden child
/// process. Uses the service-style spawn (UseShellExecute=false + CreateNoWindow
/// + drained stdout/stderr) documented in the brain note "Service-style
/// Process.Start, status-bar polling". Binary is picked newest-by-mtime across
/// Release/Debug (never a hardcoded config order — see "MCP-spawned sibling apps").
/// </summary>
public sealed class NodeController
{
    private Process? _proc;

    /// <summary>Raised for each line the server writes to stdout/stderr.</summary>
    public event Action<string>? Log;

    /// <summary>True while a server WE launched is still alive.</summary>
    public bool OursRunning => _proc is { HasExited: false };

    public int? OurPid => OursRunning ? _proc!.Id : null;

    /// <summary>
    /// Resolve the server binary. Prefers an explicit override, else auto-discovers
    /// the newest BrainX.Server.exe (apphost) or .dll under the repo's build output.
    /// Returns null if nothing is found (server never built).
    /// </summary>
    public static string? ResolveServerPath(string overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var root = FindRepoRoot(AppContext.BaseDirectory);
        if (root == null) return null;

        var serverDir = Path.Combine(root, "BrainX.Server", "bin");
        if (!Directory.Exists(serverDir)) return null;

        // exe (apphost) preferred over dll; among all, newest mtime wins.
        var candidates = new List<string>();
        foreach (var cfg in new[] { "Release", "Debug" })
        foreach (var tfm in new[] { "net10.0", "net10.0-windows" })
        {
            candidates.Add(Path.Combine(serverDir, cfg, tfm, "BrainX.Server.exe"));
            candidates.Add(Path.Combine(serverDir, cfg, tfm, "BrainX.Server.dll"));
        }

        return candidates
            .Where(File.Exists)
            .Select(p => (path: p, mtime: File.GetLastWriteTimeUtc(p), isExe: p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.mtime)
            .ThenByDescending(t => t.isExe)   // tie-break: prefer the native apphost
            .Select(t => t.path)
            .FirstOrDefault();
    }

    private static string? FindRepoRoot(string start)
    {
        var d = new DirectoryInfo(start);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "BrainX.Server"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    public bool Start(string serverPath, int port, string vault, bool embedded,
                      string storageProvider, string mySqlConn, out string error)
    {
        error = "";
        try
        {
            bool isDll = serverPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo
            {
                FileName = isDll ? "dotnet" : serverPath,
                WorkingDirectory = Path.GetDirectoryName(serverPath)!,
                UseShellExecute = false,   // mandatory to suppress the console window
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (isDll) psi.ArgumentList.Add(serverPath);

            // Bind localhost (not 0.0.0.0): a local Windows monitor needs no admin
            // rights and triggers no Windows Firewall prompt, and the dashboard is
            // opened on this machine anyway. (0.0.0.0 is for the Docker/VPS path.)
            // Single URL — no ';' — so the server's app.Run binding stays clean.
            psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
            psi.Environment["BrainX__EmbeddedMode"] = embedded ? "true" : "false";
            if (!string.IsNullOrWhiteSpace(vault))
                psi.Environment["BrainX__VaultPath"] = vault;
            if (!string.IsNullOrWhiteSpace(storageProvider))
                psi.Environment["BrainX__StorageProvider"] = storageProvider;
            if (!string.IsNullOrWhiteSpace(mySqlConn))
                psi.Environment["BrainX__MySqlConnString"] = mySqlConn;

            _proc = Process.Start(psi);
            if (_proc == null) { error = "Process.Start returned null"; return false; }

            // Drain both pipes — a full OS buffer (~4 KB) would freeze the child.
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true); // kills the dotnet host + Kestrel
                _proc.WaitForExit(5000);
            }
        }
        catch { /* already gone */ }
        finally { _proc = null; }
    }
}
