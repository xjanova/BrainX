# Deploy the rebuilt MCP into BOTH the dev Release dir AND the installed app.
#
# Why this script: Claude Code / Claude Desktop / the BrainX client each spawn
# their own brainx-mcp.exe and hold its DLLs open for the process lifetime.
# Building or copying over a live DLL fails with MSB3027 / access-denied. This
# script refuses to run while any brainx-mcp is alive, then rebuilds and swaps
# the binaries into place.
#
# TWO deploy targets, because the WPF status-bar version chip reads whichever
# MCP binary ResolveBestMcpExe() picks -- the INSTALLED one for a shipped
# client, the dev Release one for a checkout:
#   1. dev  -> BrainX.Mcp\bin\Release\net9.0      (framework-dependent build)
#   2. app  -> %LOCALAPPDATA%\BrainX\current\mcp  (self-contained install)
# Both end up at the same version (2.8.<git-count>) so every surface agrees.
#
# IMPORTANT: this file MUST be saved as UTF-8 WITHOUT BOM and ASCII-only,
# because Windows PowerShell 5.1 silently mis-decodes UTF-8 multibyte
# characters (em-dash, checkmark, etc.) and trips the parser before any line
# of the script runs. Keep punctuation in this file ASCII.

$ErrorActionPreference = "Stop"
$mcpProj     = "$PSScriptRoot\BrainX.Mcp"
$fwBuild     = "$mcpProj\bin\TestBuild"        # framework-dependent -> dev Release
$scBuild     = "$mcpProj\bin\SelfContained"    # self-contained     -> installed app
$devRelease  = "$mcpProj\bin\Release\net9.0"
$installedMcp = Join-Path $env:LOCALAPPDATA "BrainX\current\mcp"

# 1. Refuse to run while any MCP process holds files open.
$running = Get-Process -Name "brainx-mcp" -ErrorAction SilentlyContinue
if ($running) {
    Write-Output "Found $(($running).Count) running brainx-mcp process(es):"
    $running | Format-Table Id, ProcessName, StartTime
    Write-Error "Close ALL Claude Code windows, Claude Desktop, AND the BrainX client first. Every brainx-mcp exits with its parent."
}

# 2. Build both flavors from the current source.
Write-Output "[build] framework-dependent -> $fwBuild"
dotnet build $mcpProj -c Release -p:OutputPath=bin\TestBuild\ --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Error "framework-dependent build failed" }

Write-Output "[build] self-contained win-x64 -> $scBuild"
dotnet publish "$mcpProj\BrainX.Mcp.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false --nologo -v q -o $scBuild
if ($LASTEXITCODE -ne 0) { Write-Error "self-contained publish failed" }

function Get-McpVersion($dir) {
    $dll = Join-Path $dir "brainx-mcp.dll"
    if (Test-Path $dll) { return ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll)).ProductVersion }
    return "(missing)"
}
$srcVer = Get-McpVersion $scBuild
Write-Output "[version] built brainx-mcp -> $srcVer"

$ts = Get-Date -Format "yyyyMMddHHmmss"

# 3. Deploy to the dev Release dir (framework-dependent code + runtimeconfig).
Write-Output ""
Write-Output "[deploy] dev Release: $devRelease"
$devAssets = @(
    "brainx-mcp.dll", "brainx-mcp.exe", "brainx-mcp.pdb",
    "brainx-mcp.deps.json", "brainx-mcp.runtimeconfig.json",
    "BrainX.Core.dll", "BrainX.Core.pdb"
)
foreach ($a in $devAssets) {
    $s = Join-Path $fwBuild $a
    $d = Join-Path $devRelease $a
    if (Test-Path $s) {
        if (Test-Path $d) { Copy-Item $d "$d.bak.$ts" -Force }
        Copy-Item $s $d -Force
        Write-Output "  deployed: $a"
    }
}

# 4. Deploy to the installed app (the folder the shipped client's status bar
#    reads). It is a self-contained folder, so we only swap the managed code +
#    its deps/runtimeconfig -- never the bundled runtime DLLs (coreclr etc.),
#    which are unchanged and identical across builds.
if (Test-Path $installedMcp) {
    Write-Output ""
    Write-Output "[deploy] installed app: $installedMcp"
    $appAssets = @(
        "brainx-mcp.dll", "brainx-mcp.exe",
        "brainx-mcp.deps.json", "brainx-mcp.runtimeconfig.json",
        "BrainX.Core.dll"
    )
    foreach ($a in $appAssets) {
        $s = Join-Path $scBuild $a
        $d = Join-Path $installedMcp $a
        if (Test-Path $s) {
            if (Test-Path $d) { Copy-Item $d "$d.bak.$ts" -Force }
            Copy-Item $s $d -Force
            Write-Output "  deployed: $a"
        } else {
            Write-Warning "  missing in self-contained build: $a"
        }
    }
} else {
    Write-Output ""
    Write-Output "[skip] no installed app at $installedMcp (dev-only machine)"
}

# 5. Report resulting versions so the operator can confirm the swap.
Write-Output ""
Write-Output "[result] dev Release   -> $(Get-McpVersion $devRelease)"
Write-Output "[result] installed app -> $(Get-McpVersion $installedMcp)"
Write-Output ""
Write-Output "[OK] Deploy complete. Reopen the BrainX client / Claude -- the status-bar"
Write-Output "     chip and every version surface will now read $srcVer."
