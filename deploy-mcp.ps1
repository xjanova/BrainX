# Deploy the rebuilt MCP into BOTH the dev Release dir AND the installed app,
# WITHOUT stopping any running agent session (hot-swap).
#
# Why hot-swap works: Claude Code / Claude Desktop / Codex / the BrainX client
# each spawn their own brainx-mcp.exe and hold its DLLs open for the process
# lifetime. Windows blocks OVERWRITE and DELETE of a mapped image, but it
# allows RENAME on the same volume -- the running process keeps executing the
# renamed file through its open handle. So instead of refusing while anything
# runs (the old behavior), this script:
#   1. builds into fresh staging dirs (never locked),
#   2. renames each live target file aside to <name>.stale.<timestamp>,
#   3. copies the new build into place.
# Live sessions keep their old version until they naturally exit; every NEW
# session spawns the new version. Same trick Velopack uses for the client
# (app-{ver} folders + junction flip); this is the file-level equivalent for
# the two MCP deploy targets.
#
# Stale/backup files from earlier runs are cleaned best-effort at the start;
# ones still mapped by a living process are skipped and picked up next run.
#
# TWO deploy targets, because the WPF status-bar version chip reads whichever
# MCP binary ResolveBestMcpExe() picks -- the INSTALLED one for a shipped
# client, the dev Release one for a checkout:
#   1. dev  -> BrainX.Mcp\bin\Release\net9.0      (framework-dependent build)
#   2. app  -> %LOCALAPPDATA%\BrainX\current\mcp  (self-contained install)
# Both end up at the same version (2.9.<git-count>) so every surface agrees.
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

# 1. Live processes are fine now -- just report them so the operator knows
#    which sessions will keep the OLD version until they exit.
$running = Get-Process -Name "brainx-mcp" -ErrorAction SilentlyContinue
if ($running) {
    Write-Output "[hot-swap] $(@($running).Count) live brainx-mcp process(es) keep their current binary until their session ends:"
    $running | Format-Table Id, ProcessName, StartTime | Out-String | Write-Output
} else {
    Write-Output "[hot-swap] no live brainx-mcp process -- plain deploy."
}

# 2. Sweep leftovers from previous hot-swaps. Files still mapped by a living
#    process refuse deletion -- skip them silently, next run retries.
foreach ($dir in @($devRelease, $installedMcp)) {
    if (Test-Path $dir) {
        Get-ChildItem $dir -Filter "*.stale.*" -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item $_.FullName -Force -ErrorAction Stop; Write-Output "[sweep] removed $($_.Name)" } catch {}
        }
        Get-ChildItem $dir -Filter "*.bak.*" -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item $_.FullName -Force -ErrorAction Stop; Write-Output "[sweep] removed $($_.Name)" } catch {}
        }
    }
}

# 3. Build both flavors from the current source. Staging dirs are never
#    executed, so they are never locked.
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

# Hot-swap one file: rename a (possibly mapped) target aside, then copy the
# new build in. The renamed .stale file doubles as the rollback copy.
function Deploy-File($srcDir, $dstDir, $name, $ts) {
    $s = Join-Path $srcDir $name
    $d = Join-Path $dstDir $name
    if (-not (Test-Path $s)) { return "missing-src" }
    if (Test-Path $d) {
        try {
            Move-Item $d "$d.stale.$ts" -Force -ErrorAction Stop
        } catch {
            # Rename refused (unusual even for mapped images). Last resort:
            # in-place overwrite for an unlocked file.
            try { Copy-Item $s $d -Force -ErrorAction Stop; return "overwrote" }
            catch { return "LOCKED" }
        }
    }
    Copy-Item $s $d -Force
    return "swapped"
}

function Deploy-Set($srcDir, $dstDir, $assets, $ts, $label) {
    # Progress goes through Write-Host, NOT Write-Output: a PowerShell
    # function returns EVERY pipeline object, so Write-Output lines here
    # would pollute the returned $failed array (first-run bug: the status
    # lines made the caller report a successful deploy as INCOMPLETE).
    Write-Host ""
    Write-Host "[deploy] ${label}: $dstDir"
    if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Force $dstDir | Out-Null }
    $failed = @()
    foreach ($a in $assets) {
        $r = Deploy-File $srcDir $dstDir $a $ts
        if ($r -eq "LOCKED") { $failed += $a; Write-Warning "  $a still locked -- could not swap" }
        elseif ($r -eq "missing-src") { Write-Warning "  missing in build output: $a" }
        else { Write-Host "  ${r}: $a" }
    }
    return $failed
}

# 4. Deploy to the dev Release dir (framework-dependent code + runtimeconfig).
$devAssets = @(
    "brainx-mcp.dll", "brainx-mcp.exe", "brainx-mcp.pdb",
    "brainx-mcp.deps.json", "brainx-mcp.runtimeconfig.json",
    "BrainX.Core.dll", "BrainX.Core.pdb"
)
$devFailed = Deploy-Set $fwBuild $devRelease $devAssets $ts "dev Release"

# 5. Deploy to the installed app (the folder the shipped client's status bar
#    reads). It is a self-contained folder, so we only swap the managed code +
#    its deps/runtimeconfig -- never the bundled runtime DLLs (coreclr etc.),
#    which are unchanged and identical across builds.
$appFailed = @()
if (Test-Path $installedMcp) {
    $appAssets = @(
        "brainx-mcp.dll", "brainx-mcp.exe",
        "brainx-mcp.deps.json", "brainx-mcp.runtimeconfig.json",
        "BrainX.Core.dll"
    )
    $appFailed = Deploy-Set $scBuild $installedMcp $appAssets $ts "installed app"
} else {
    Write-Output ""
    Write-Output "[skip] no installed app at $installedMcp (dev-only machine)"
}

# 6. Report resulting versions so the operator can confirm the swap.
Write-Output ""
Write-Output "[result] dev Release   -> $(Get-McpVersion $devRelease)"
Write-Output "[result] installed app -> $(Get-McpVersion $installedMcp)"
Write-Output ""
if (($devFailed.Count + $appFailed.Count) -gt 0) {
    Write-Error "Deploy INCOMPLETE -- locked file(s): $((@($devFailed) + @($appFailed)) -join ', '). Close the owning session and re-run."
}
Write-Output "[OK] Hot deploy complete. Live sessions keep their old version until"
Write-Output "     they exit; every NEW agent session now spawns $srcVer."
