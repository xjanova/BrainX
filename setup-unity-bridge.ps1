# setup-unity-bridge.ps1 -- one command to put Unity on the brain's MCP bus.
#
#   powershell -ExecutionPolicy Bypass -File D:\BrainX\setup-unity-bridge.ps1
#
# Idempotent: every step checks before it acts, so re-running is safe and cheap.
# Pass -WhatIf to see what it would do without touching anything.
#
# WHY EACH STEP IS THE WAY IT IS (all learned the hard way on this machine):
#
#  * uv from winget only. The astral.sh install script drops a SECOND uv into
#    ~\.local\bin and prepends it to PATH -- two uv binaries at different
#    versions is its own failure class.
#  * `uv tool install`, never `uvx`. uvx rebuilds an ephemeral venv on EVERY
#    launch; on Windows that races itself and dies with "os error 32 ... being
#    used by another process" (upstream issue #1248). A persistent tool venv has
#    no build step at launch and cannot hit it.
#  * ABSOLUTE paths in the bridge config. ~\.local\bin is NOT on PATH here, and
#    the brain's MCP host inherited its PATH when it launched -- it will not see
#    anything added afterwards. A bare "uv" in the config resolves to nothing.
#  * Stdio transport, not HTTP -- for THIS bridge. Stdio means Unity listens on
#    6400 and the Python server connects IN to it, which is the shape unity-mcp
#    is actually tested in. The package defaults to HTTP.
#    (The hub itself gained an HTTP transport later, for Unreal 5.8's in-editor
#    server -- see the `url` field in mcp-bridges.json. That does not make HTTP
#    the right choice here: unity-mcp still ships a real stdio server, and a
#    spawned child is one less moving part than a port that must already be up.)
#  * Auto-register OFF. Left on, the package rewrites the owner's Claude Code and
#    Claude Desktop configs on every Editor start, wiring Unity DIRECTLY into
#    those clients. That bypasses the brain, which is the one thing this bridge
#    exists to prevent -- the brain would be blind to every Unity action.
#
# ASCII only on purpose: Windows PowerShell 5.1 mis-decodes UTF-8 multibyte
# characters and trips the parser before line one runs.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ProjectPath = "D:\Unity\BrainXBridgeTest",
    [string] $Vault       = "G:\Obsidian",
    [string] $Version     = "10.1.0",
    [switch] $SkipLaunch
)

$ErrorActionPreference = "Stop"
$script:Failed = @()

# Refresh PATH from the registry before anything looks for a tool. A shell that
# was already open when uv was installed still carries the PATH it started with,
# so a freshly installed tool is invisible to it -- the script would report
# "missing" on a machine where the thing it needs is sitting right there.
$env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")

# Run a native .exe and return its output as text.
#
# Windows PowerShell 5.1 turns a native command's stderr into ErrorRecords when
# it is redirected, and under ErrorActionPreference='Stop' that aborts the
# script even though the exe exited 0. uv, winget and Unity all write progress
# to stderr, so every one of them looked like a failure. Relax the preference
# for the duration of the call and judge the result by $LASTEXITCODE, which is
# the only thing that actually says whether it worked.
function Native {
    # NOT named $Args: that is a PowerShell automatic variable, so a parameter
    # by that name never binds. The splat then passed nothing, uv printed its
    # help text, and $binDir became "An extremely fast Python package manager."
    param([string] $Exe, [string[]] $ExeArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try   { $out = & $Exe @ExeArgs 2>&1 | ForEach-Object { "$_" } }
    finally { $ErrorActionPreference = $prev }
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
}

function Step($n, $msg) { Write-Host ""; Write-Host "[$n] $msg" -ForegroundColor Cyan }
function OK($msg)       { Write-Host "    OK   $msg" -ForegroundColor Green }
function Info($msg)     { Write-Host "    ..   $msg" -ForegroundColor Gray }
function Warn($msg)     { Write-Host "    !!   $msg" -ForegroundColor Yellow }
function Fail($msg)     { Write-Host "    FAIL $msg" -ForegroundColor Red; $script:Failed += $msg }

Write-Host "BrainX <-> Unity MCP bridge setup" -ForegroundColor White
Write-Host "project $ProjectPath | vault $Vault | package/server v$Version"

# ---------------------------------------------------------------- 1. uv
Step 1 "uv (Python runner the Unity MCP server needs)"
$uv = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uv) {
    # winget puts real .exe shims in WinGet\Links, which is already on the user
    # PATH. That matters: the bridge spawns with UseShellExecute=false, which
    # searches PATH but does NOT apply PATHEXT and cannot exec a .cmd shim.
    if ($PSCmdlet.ShouldProcess("uv", "winget install astral-sh.uv")) {
        Info "installing via winget"
        $null = Native "winget" @("install","--id","astral-sh.uv","--source","winget","--accept-source-agreements","--accept-package-agreements","--disable-interactivity")
        $env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")
        $uv = Get-Command uv -ErrorAction SilentlyContinue
    }
}
if ($uv) { OK "$($uv.Source)  $((Native $uv.Source @('--version')).Text)" } else { Fail "uv not resolvable" }

# ------------------------------------------------- 2. the MCP server itself
Step 2 "MCP for Unity server (persistent tool venv)"
# Without uv there is nothing to install into. Say so once, rather than throwing
# CommandNotFoundException out of this step and every later one that uses it.
$uvExe   = if ($uv) { $uv.Source } else { $null }
$binDir  = $null
if ($uvExe) { $binDir = ((Native $uvExe @('tool','dir','--bin')).Text -split "`n" | Select-Object -First 1).Trim() }
if (-not $binDir) { $binDir = Join-Path $env:USERPROFILE ".local\bin" }
$serverExe = Join-Path $binDir "mcp-for-unity.exe"

if (-not $uvExe) {
    Warn "skipped - uv unavailable"
} elseif (Test-Path $serverExe) {
    OK "already installed: $serverExe"
} elseif ($PSCmdlet.ShouldProcess("mcpforunityserver==$Version", "uv tool install")) {
    # A tool env left behind by a crashed install has the directory but no
    # interpreter, and uv then refuses with "Invalid environment ... missing
    # Python executable" forever rather than rebuilding it. Clear it first.
    $toolEnv = Join-Path $env:APPDATA "uv\tools\mcpforunityserver"
    if ((Test-Path $toolEnv) -and -not (Test-Path (Join-Path $toolEnv "Scripts\python.exe"))) {
        Info "clearing a broken tool env at $toolEnv"
        Remove-Item $toolEnv -Recurse -Force -ErrorAction SilentlyContinue
    }
    Info "uv tool install mcpforunityserver==$Version  (first run downloads Python + deps)"
    # Pinned to 3.12 on purpose. Letting uv choose picks the newest managed
    # CPython, and installing that fails on this machine at uv's "minor version
    # link" step -- which leaves a tool env with no python.exe in it.
    $r = Native $uvExe @('tool','install','--python','3.12',"mcpforunityserver==$Version")
    ($r.Text -split "`n" | Select-Object -Last 3) | ForEach-Object { Info $_ }
    if ($r.Code -ne 0) { Warn "uv exited $($r.Code)" }
    if (Test-Path $serverExe) { OK "installed: $serverExe" } else { Fail "mcp-for-unity.exe not found at $serverExe" }
}

# ------------------------------------------------------- 3. Unity editor
Step 3 "Unity Editor"
# The Hub metadata folder is NOT proof of an install: this machine has
# Hub\Editor\6000.2.14f1 containing only metadata.hub.json, while the real
# editor lives on another drive entirely. Find Unity.exe, never infer it.
$editors = @()
$secondary = Join-Path $env:APPDATA "UnityHub\secondaryInstallPath.json"
$roots = @("C:\Program Files\Unity\Hub\Editor")
if (Test-Path $secondary) {
    $alt = (Get-Content $secondary -Raw).Trim('"', ' ', "`n", "`r")
    if ($alt) { $roots += $alt }
}
foreach ($r in $roots) {
    if (Test-Path $r) { $editors += Get-ChildItem $r -Filter "Unity.exe" -Recurse -Depth 3 -ErrorAction SilentlyContinue }
}
$editor = $editors | Sort-Object { $_.VersionInfo.ProductVersion } -Descending | Select-Object -First 1
if ($editor) { OK "$($editor.FullName)  (v$($editor.VersionInfo.ProductVersion))" } else { Fail "no Unity.exe found under: $($roots -join ', ')" }

# ------------------------------------------------------- 4. the project
Step 4 "Unity project + package"
if (-not (Test-Path $ProjectPath)) {
    if ($editor -and $PSCmdlet.ShouldProcess($ProjectPath, "create Unity project")) {
        Info "creating project (about 70s)"
        Start-Process -FilePath $editor.FullName -ArgumentList @('-createProject', $ProjectPath, '-batchmode', '-quit', '-logFile', "$env:TEMP\unity-create.log") -Wait
    }
}
if (Test-Path $ProjectPath) { OK "project present" } else { Fail "project missing: $ProjectPath" }

$manifest = Join-Path $ProjectPath "Packages\manifest.json"
if (Test-Path $manifest) {
    $j = Get-Content $manifest -Raw | ConvertFrom-Json
    $has = $j.dependencies.PSObject.Properties.Name -contains "com.coplaydev.unity-mcp"
    if ($has) {
        OK "package already in manifest"
    } elseif ($PSCmdlet.ShouldProcess($manifest, "add com.coplaydev.unity-mcp")) {
        Copy-Item $manifest "$manifest.bak" -Force
        # Pinned to the same version as the server above. #main drifts the moment
        # upstream merges, and a bridge whose two ends disagree fails in ways
        # that look like "Unity is broken".
        $j.dependencies | Add-Member -NotePropertyName "com.coplaydev.unity-mcp" `
            -NotePropertyValue "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v$Version"
        ($j | ConvertTo-Json -Depth 20) | Set-Content $manifest -Encoding utf8
        OK "added (backup at manifest.json.bak)"
    }
} else { Fail "no manifest.json in $ProjectPath\Packages" }

# --------------------------------------------- 5. editor prefs bootstrap
Step 5 "Editor prefs (auto-start ON, stdio transport, auto-register OFF)"
$bootDir = Join-Path $ProjectPath "Assets\Editor"
$bootCs  = Join-Path $bootDir "BrainXBridgeBoot.cs"
$csharp = @'
using UnityEditor;
using UnityEngine;

/// <summary>
/// Configures MCP for Unity for use through the BrainX bridge. Written by
/// setup-unity-bridge.ps1; safe to delete once the prefs are set (they persist
/// in EditorPrefs, not in the project).
/// </summary>
public static class BrainXBridgeBoot
{
    public static void Configure()
    {
        // Ships false, so a freshly imported package compiles clean, reports no
        // errors, and never listens on anything. Nothing says why.
        EditorPrefs.SetBool("MCPForUnity.AutoStartOnLoad", true);

        // Ships true (HTTP). The brain spawns a child process over stdio and has
        // no way to talk to an HTTP endpoint, so HTTP mode is invisible to it.
        EditorPrefs.SetBool("MCPForUnity.UseHttpTransport", false);

        // Ships true, and on every Editor start it rewrites the user's Claude
        // Code / Claude Desktop configs to point at Unity DIRECTLY. That is a
        // registrar, and the whole point of the bridge is that Unity traffic
        // goes through the brain so the brain can journal it.
        EditorPrefs.SetBool("MCPForUnity.AutoRegisterEnabled", false);

        Debug.Log(string.Format(
            "[BrainXBridgeBoot] autoStart={0} http={1} autoRegister={2}",
            EditorPrefs.GetBool("MCPForUnity.AutoStartOnLoad", false),
            EditorPrefs.GetBool("MCPForUnity.UseHttpTransport", true),
            EditorPrefs.GetBool("MCPForUnity.AutoRegisterEnabled", true)));
    }

    [MenuItem("BrainX/Configure MCP bridge")]
    private static void FromMenu() { Configure(); }
}
'@
if ($PSCmdlet.ShouldProcess($bootCs, "write editor bootstrap")) {
    New-Item -ItemType Directory -Force -Path $bootDir | Out-Null
    [System.IO.File]::WriteAllText($bootCs, $csharp, (New-Object System.Text.UTF8Encoding($false)))
    OK "wrote $bootCs"
}

if ($editor -and $PSCmdlet.ShouldProcess("EditorPrefs", "apply via -executeMethod")) {
    $running = Get-Process Unity -ErrorAction SilentlyContinue
    if ($running) {
        Info "closing the editor gracefully (executeMethod needs the project free)"
        $running | ForEach-Object { $null = $_.CloseMainWindow() }
        $null = $running | ForEach-Object { $_.WaitForExit(60000) }
    }
    Info "applying prefs"
    Start-Process -FilePath $editor.FullName -Wait -ArgumentList @(
        '-projectPath', $ProjectPath, '-batchmode', '-quit',
        '-executeMethod', 'BrainXBridgeBoot.Configure', '-logFile', "$env:TEMP\unity-prefs.log")
    $line = Select-String -Path "$env:TEMP\unity-prefs.log" -Pattern "\[BrainXBridgeBoot\]" -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($line) { OK $line.Line.Trim() } else { Fail "prefs not confirmed - see $env:TEMP\unity-prefs.log" }
}

# ------------------------------- 6. undo the package's client registrations
Step 6 "Remove direct client registrations (traffic must go through the brain)"
$clients = @(
    (Join-Path $env:USERPROFILE ".claude.json"),
    (Join-Path $env:APPDATA "Claude\claude_desktop_config.json")
)
foreach ($c in $clients) {
    if (-not (Test-Path $c)) { continue }
    $raw = [System.IO.File]::ReadAllText($c)
    $obj = $raw | ConvertFrom-Json
    $removed = @()
    foreach ($holder in @($obj) + @($obj.mcpServers) + @($obj.projects.PSObject.Properties.Value)) {
        if (-not $holder) { continue }
        $servers = if ($holder.mcpServers) { $holder.mcpServers } else { $holder }
        if (-not $servers -or -not $servers.PSObject) { continue }
        foreach ($name in @($servers.PSObject.Properties.Name)) {
            if ($name -match '^(unity|Unity)MCP$') {
                $servers.PSObject.Properties.Remove($name); $removed += $name
            }
        }
    }
    if ($removed.Count -gt 0) {
        if ($PSCmdlet.ShouldProcess($c, "remove $($removed -join ', ')")) {
            Copy-Item $c "$c.bak" -Force
            # No BOM: a BOM here breaks Node/Claude JSON.parse on this machine.
            [System.IO.File]::WriteAllText($c, ($obj | ConvertTo-Json -Depth 40), (New-Object System.Text.UTF8Encoding($false)))
            OK "$(Split-Path $c -Leaf): removed $($removed -join ', ') (backup .bak)"
        }
    } else { OK "$(Split-Path $c -Leaf): clean" }
}

# ------------------------------------------------- 7. the brain's bridge
Step 7 "Point the brain's bridge at the server"
$bridgeCfg = Join-Path $Vault ".obsidianx\mcp-bridges.json"
if (Test-Path $bridgeCfg) {
    $b = [System.IO.File]::ReadAllText($bridgeCfg) | ConvertFrom-Json
    $unity = $b.bridges | Where-Object { $_.id -eq "unity" }
    if ($unity) {
        if ($PSCmdlet.ShouldProcess($bridgeCfg, "enable unity bridge")) {
            Copy-Item $bridgeCfg "$bridgeCfg.bak" -Force
            $unity.enabled = $true
            $unity.command = $serverExe          # absolute: .local\bin is not on PATH
            $unity.args    = @('--transport', 'stdio')
            [System.IO.File]::WriteAllText($bridgeCfg, ($b | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding($false)))
            OK "enabled -> $serverExe --transport stdio"
        }
        # The hub caches tools/list for 24h (10min negative). A stale "no tools"
        # entry outlives the fix that made tools appear.
        $cache = Join-Path $Vault ".obsidianx\mcp-bridges.cache.json"
        if (Test-Path $cache) {
            if ($PSCmdlet.ShouldProcess($cache, "invalidate schema cache")) {
                Remove-Item $cache -Force; OK "schema cache cleared"
            }
        } else { OK "no stale schema cache" }
    } else { Fail "no 'unity' bridge entry in $bridgeCfg" }
} else { Fail "bridge config not found: $bridgeCfg" }

# ------------------------------------------------------- 8. launch + verify
if (-not $SkipLaunch -and $editor -and $PSCmdlet.ShouldProcess("Unity", "launch and verify")) {
    Step 8 "Launch the Editor and wait for the bridge"
    Start-Process -FilePath $editor.FullName -ArgumentList @('-projectPath', $ProjectPath, '-logFile', "$env:TEMP\unity-bridge.log")
    Info "waiting for a listener on 6400 (up to 240s)"
    $up = $false
    for ($i = 0; $i -lt 48; $i++) {
        Start-Sleep -Seconds 5
        if (Get-NetTCPConnection -State Listen -LocalPort 6400 -ErrorAction SilentlyContinue) { $up = $true; break }
    }
    if ($up) { OK "Unity is listening on 6400 after $((($i+1)*5))s" }
    else {
        Fail "6400 never came up"
        Select-String -Path "$env:TEMP\unity-bridge.log" -Pattern "MCP-FOR-UNITY" -ErrorAction SilentlyContinue |
            Select-Object -Last 6 | ForEach-Object { Warn $_.Line.Trim() }
    }
}

# ---------------------------------------------------------------- summary
Write-Host ""
if ($script:Failed.Count -eq 0) {
    Write-Host "DONE - every step verified." -ForegroundColor Green
    Write-Host "Start a NEW agent session: the hub reads tools/list once per session," -ForegroundColor Gray
    Write-Host "so unity__* tools appear only in a session opened after this point." -ForegroundColor Gray
} else {
    Write-Host "INCOMPLETE - $($script:Failed.Count) step(s) failed:" -ForegroundColor Red
    $script:Failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
