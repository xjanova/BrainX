# Deploy the rebuilt MCP DLL after restarting Claude Code.
#
# Why this script: Claude Code spawns one brainx-mcp.exe per session and
# holds the DLL open for the lifetime of that session. Building over a live
# DLL fails with MSB3027. The new build lives at bin/TestBuild/ -- this
# script swaps it into bin/Release/ once no MCP process is alive.
#
# IMPORTANT: this file MUST be saved as UTF-8 WITHOUT BOM and ASCII-only,
# because Windows PowerShell 5.1 silently mis-decodes UTF-8 multibyte
# characters (em-dash, checkmark, etc.) and trips the parser before
# any line of the script runs. Keep punctuation in this file ASCII.

$ErrorActionPreference = "Stop"
$mcpProj = "$PSScriptRoot\BrainX.Mcp"
$src     = "$mcpProj\bin\TestBuild"
$dst     = "$mcpProj\bin\Release\net9.0"

# 1. Sanity check
if (-not (Test-Path $src)) {
    Write-Error "Source build not found at $src -- run: dotnet build BrainX.Mcp -c Release -p:OutputPath=bin\TestBuild\"
}

# 2. Confirm no MCP process is holding files open
$running = Get-Process -Name "brainx-mcp" -ErrorAction SilentlyContinue
if ($running) {
    Write-Output "Found $(($running).Count) running brainx-mcp process(es):"
    $running | Format-Table Id, ProcessName, StartTime
    Write-Error "Quit ALL Claude Code windows first. The MCP processes will exit with their parent."
}

# 3. Backup current Release dll (rolling backup)
$ts = Get-Date -Format "yyyyMMddHHmmss"
$dstDll = "$dst\brainx-mcp.dll"
if (Test-Path $dstDll) {
    Copy-Item $dstDll "$dstDll.bak.$ts" -Force
    Write-Output "Backed up existing dll to brainx-mcp.dll.bak.$ts"
}

# 4. Copy new build into place (only the things that actually changed)
$assets = @(
    "brainx-mcp.dll",
    "brainx-mcp.pdb",
    "brainx-mcp.deps.json",
    "brainx-mcp.runtimeconfig.json",
    "BrainX.Core.dll",
    "BrainX.Core.pdb"
)
foreach ($a in $assets) {
    $s = Join-Path $src $a
    $d = Join-Path $dst $a
    if (Test-Path $s) {
        Copy-Item $s $d -Force
        Write-Output "Deployed: $a"
    } else {
        Write-Warning "Skipped (not in TestBuild): $a"
    }
}

Write-Output ""
Write-Output "[OK] Deploy complete. Reopen Claude Code -- the next brainx-mcp spawn will use the new build."
Write-Output "Verify by calling brain_find_contradictions and looking for mode='llm-verified'."
