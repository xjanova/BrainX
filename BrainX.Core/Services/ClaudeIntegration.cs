using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using BrainX.Core.Models;

namespace BrainX.Core.Services;

public class ClaudeIntegration
{
    private readonly string _vaultPath;
    private readonly string _claudeMdPath;

    public ClaudeIntegration(string vaultPath)
    {
        _vaultPath = vaultPath;
        _claudeMdPath = Path.Combine(vaultPath, "CLAUDE.md");
    }

    public bool IsClaudeCodeInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo("claude", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    public void GenerateClaudeMd(KnowledgeGraph graph, BrainIdentity identity)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ObsidianX Brain — Connected to Claude");
        sb.AppendLine();
        sb.AppendLine($"**Brain Address:** `{identity.Address}`");
        sb.AppendLine($"**Display Name:** {identity.DisplayName}");
        sb.AppendLine($"**Total Notes:** {graph.TotalNodes}");
        sb.AppendLine($"**Total Words:** {graph.TotalWords:N0}");
        sb.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("## Vault Structure");
        sb.AppendLine();
        sb.AppendLine("This is an ObsidianX-managed knowledge vault. Notes are Markdown files organized by knowledge domains.");
        sb.AppendLine();
        // The expertise profile used to be rendered here as a static
        // snapshot — but BrainExporter writes a live, marker-managed
        // `## Brain Profile` section below that updates on every export.
        // Two copies of the same chart drifted out of sync (one stale,
        // one fresh) and obscured the per-category percentages. We now
        // defer entirely to the auto-managed section.
        sb.AppendLine("## Instructions for Claude");
        sb.AppendLine();
        sb.AppendLine("- When working with this vault, respect the existing folder structure and linking patterns.");
        sb.AppendLine("- Use [[wiki-links]] for connecting notes. This vault uses ObsidianX knowledge graph.");
        sb.AppendLine("- Always add relevant #tags for categorization.");
        sb.AppendLine("- The brain owner's expertise areas are listed above — tailor your assistance accordingly.");
        sb.AppendLine("- When creating new notes, ensure they connect to existing knowledge nodes.");

        File.WriteAllText(_claudeMdPath, sb.ToString());
    }

    public async Task<string> QueryClaude(string prompt, string context = "", int timeoutSeconds = 120)
    {
        var fullPrompt = string.IsNullOrEmpty(context)
            ? prompt
            : $"Context from my ObsidianX vault:\n{context}\n\nQuestion: {prompt}";

        var psi = new ProcessStartInfo("claude")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _vaultPath
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add(fullPrompt);

        using var proc = Process.Start(psi);
        if (proc == null) return "Error: Could not start Claude Code";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var outputTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var error = await errorTask;
            if (proc.ExitCode != 0 && !string.IsNullOrEmpty(error))
                return $"Error: {error.Trim()}";

            var output = await outputTask;
            return output.Trim();
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return $"Error: Claude query timed out after {timeoutSeconds} seconds. Try a shorter question.";
        }
    }

    public ConnectionStatus CheckConnection()
    {
        var status = new ConnectionStatus
        {
            ClaudeCodeInstalled = IsClaudeCodeInstalled(),
            ClaudeMdExists = File.Exists(_claudeMdPath),
            VaultPath = _vaultPath,
            VaultExists = Directory.Exists(_vaultPath)
        };
        status.IsConnected = status.ClaudeCodeInstalled && status.ClaudeMdExists && status.VaultExists;
        return status;
    }
}

public class ConnectionStatus
{
    public bool IsConnected { get; set; }
    public bool ClaudeCodeInstalled { get; set; }
    public bool ClaudeMdExists { get; set; }
    public bool VaultExists { get; set; }
    public string VaultPath { get; set; } = string.Empty;
    public string StatusMessage => IsConnected
        ? "✓ Brain connected to Claude successfully"
        : !ClaudeCodeInstalled ? "✗ Claude Code not installed — install from claude.ai/code"
        : !VaultExists ? "✗ Vault directory not found"
        : "✗ CLAUDE.md not generated — click 'Connect to Claude' to set up";
}
