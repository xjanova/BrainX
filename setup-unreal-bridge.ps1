# setup-unreal-bridge.ps1 -- one command to put Unreal Engine 5.8 on the brain's MCP bus.
#
#   powershell -ExecutionPolicy Bypass -File D:\BrainX\setup-unreal-bridge.ps1
#
# Idempotent: every step checks before it acts, so re-running is safe and cheap.
# Pass -WhatIf to see what it would do without touching anything.
#
# HOW THIS DIFFERS FROM THE UNITY BRIDGE, AND WHY.
#
# Unity needs a Python server installed and spawned. Unreal 5.8 does not: Epic
# ships a first-party MCP server INSIDE the editor (the ModelContextProtocol
# plugin), so there is nothing to install, nothing to spawn, and no uv. The
# brain talks HTTP straight to the editor at 127.0.0.1:8000/mcp.
#
# That means the steps this script CANNOT do are in the editor's own UI, and it
# says so rather than pretending otherwise:
#
#  * Edit > Plugins > enable "Unreal MCP" AND "All Toolsets", then restart.
#  * Edit > Editor Preferences > General > Model Context Protocol >
#    tick "Auto Start Server". It ships OFF -- a correctly installed plugin will
#    otherwise compile clean, report nothing wrong, and listen on nothing. This
#    is the same trap unity-mcp's AutoStartOnLoad set, and it cost real time
#    there, so it is checked explicitly below rather than assumed.
#
# DO NOT run `ModelContextProtocol.GenerateClientConfig ClaudeCode`. It writes an
# .mcp.json that wires the editor DIRECTLY into each agent, bypassing the brain
# -- which is precisely what this bridge exists to prevent. One config reaches
# every agent, and every call lands in the brain's auto-journal on the way past.
#
# ASCII only on purpose: Windows PowerShell 5.1 mis-decodes UTF-8 multibyte
# characters and trips the parser before line one runs.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Vault = "G:\Obsidian",
    [int]    $Port  = 8000,
    [string] $Path  = "/mcp",
    # Set by install-unreal-stack.ps1, which has already located the engine (and
    # may have been pointed at a non-default root). Without it this script would
    # search again, fail to find that root, and print "Blocked: no
    # UnrealEditor.exe" in the middle of a run that had just configured it.
    [switch] $SkipEngineCheck
)

$ErrorActionPreference = "Stop"
$script:Failed = @()
$script:Warned = @()

# A port that is bound but silent in HTTP is NOT the MCP plugin, and this is the
# likeliest thing to go wrong: 8000 is Epic's default and also one of the most
# contended ports on a dev box. Found on this machine before Unreal was ever
# installed -- an elevated "Manager.exe" already held it. Left undiagnosed, the
# editor fails to bind and the brain dials whatever else answers.
function ReportPortConflict {
    Warn "port $Port is held by something that does not speak HTTP -- not the MCP plugin."
    $owner = $null
    try {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($conn) { $owner = (Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue).Name }
    } catch { }
    if ($owner) { Write-Host "    held by: $owner (pid $($conn.OwningProcess))" -ForegroundColor DarkGray }

    $free = @(8077, 8123, 8181, 8222) | Where-Object {
        -not ((Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue).LocalPort -contains $_)
    } | Select-Object -First 1

    Write-Host ""
    Write-Host "    Move Unreal off $Port. In the editor:" -ForegroundColor DarkGray
    Write-Host "      Edit > Editor Preferences > General > Model Context Protocol" -ForegroundColor DarkGray
    Write-Host "        > Server Port Number = $free" -ForegroundColor DarkGray
    Write-Host "    Then re-run this script with -Port $free so the bridge follows." -ForegroundColor DarkGray
}

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    OK   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    WARN $msg" -ForegroundColor Yellow; $script:Warned += $msg }
function Bad($msg)  { Write-Host "    FAIL $msg" -ForegroundColor Red;   $script:Failed += $msg }

# ---------------------------------------------------------------- 1. the engine
if ($SkipEngineCheck) {
    Step "Locating Unreal Engine 5.8+ (skipped -- already located by the installer)"
    $editor = "skipped"
} else {

Step "Locating Unreal Engine 5.8+"

$editor = $null
$roots = @(
    "C:\Program Files\Epic Games", "D:\Epic Games", "E:\Epic Games", "F:\Epic Games",
    "G:\Epic Games", "D:\UnrealEngine", "D:\Unreal"
)
foreach ($r in $roots) {
    if (-not (Test-Path $r)) { continue }
    $hit = Get-ChildItem $r -Directory -ErrorAction SilentlyContinue |
           ForEach-Object { Join-Path $_.FullName "Engine\Binaries\Win64\UnrealEditor.exe" } |
           Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($hit) { $editor = $hit; break }
}

# The launcher's own manifest is authoritative when it exists -- a folder named
# "UE_5.8" proves nothing (a Unity Hub folder on this machine held only
# metadata and no editor at all, and it read as an install to Get-ChildItem).
$dat = "C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat"
if (-not $editor -and (Test-Path $dat)) {
    try {
        $manifest = [System.IO.File]::ReadAllText($dat) | ConvertFrom-Json
        foreach ($i in $manifest.InstallationList) {
            $cand = Join-Path $i.InstallLocation "Engine\Binaries\Win64\UnrealEditor.exe"
            if (Test-Path $cand) { $editor = $cand; break }
        }
    } catch { }
}

if ($editor) {
    $ver = (Get-Item $editor).VersionInfo.ProductVersion
    Ok "UnrealEditor.exe -> $editor"
    Ok "version $ver"
    if ($ver -and $ver -match '^(\d+)\.(\d+)') {
        $maj = [int]$Matches[1]; $min = [int]$Matches[2]
        if ($maj -lt 5 -or ($maj -eq 5 -and $min -lt 8)) {
            Bad "the built-in MCP plugin is 5.8+. This is $ver -- upgrade, or this bridge has no server to talk to."
        }
    }
} else {
    Bad "no UnrealEditor.exe found. Install Unreal Engine 5.8+ via the Epic Games Launcher first."
    Write-Host ""
    Write-Host "    Epic Games Launcher: https://store.epicgames.com/en-US/download" -ForegroundColor DarkGray
    Write-Host "    Then Unreal Engine tab > Library > + > 5.8. Budget ~100 GB." -ForegroundColor DarkGray
    Write-Host "    C: has little room on this machine -- install to D: or G:." -ForegroundColor DarkGray
}

}   # end -SkipEngineCheck

# ------------------------------------------------------------ 2. bridge config
Step "Wiring the bridge in mcp-bridges.json"

$cfgPath = Join-Path $Vault ".obsidianx\mcp-bridges.json"
if (-not (Test-Path $cfgPath)) {
    Bad "no config at $cfgPath -- start any agent once so the brain seeds it, then re-run."
} else {
    # [System.IO.File] not Get-Content: PS 5.1 reads UTF-8 as ANSI and would
    # corrupt any non-ASCII path already in this file.
    $raw = [System.IO.File]::ReadAllText($cfgPath)
    $cfg = $raw | ConvertFrom-Json
    $entry = $cfg.bridges | Where-Object { $_.id -eq 'unreal' } | Select-Object -First 1

    $url = "http://127.0.0.1:$Port$Path"
    if (-not $entry) {
        Bad "no 'unreal' entry in the config. Delete the file and start an agent to re-seed it."
    } else {
        $needs = ($entry.enabled -ne $true) -or ($entry.url -ne $url) -or ($entry.command)
        if (-not $needs) {
            Ok "already enabled and pointed at $url"
        } elseif ($PSCmdlet.ShouldProcess($cfgPath, "enable the unreal bridge at $url")) {
            # A stale command+args from the pre-5.8 seed would make Validate()
            # refuse the entry outright ("set either command or url, not both").
            if ($entry.PSObject.Properties.Name -contains 'command') { $entry.PSObject.Properties.Remove('command') }
            if ($entry.PSObject.Properties.Name -contains 'args')    { $entry.PSObject.Properties.Remove('args') }

            $entry | Add-Member -NotePropertyName url     -NotePropertyValue $url  -Force
            $entry | Add-Member -NotePropertyName enabled -NotePropertyValue $true -Force
            $entry | Add-Member -NotePropertyName probe -NotePropertyValue ([pscustomobject]@{
                kind = 'http'; host = '127.0.0.1'; port = $Port; path = $Path
            }) -Force

            Copy-Item $cfgPath "$cfgPath.bak" -Force
            # NO BOM: Node and Python tooling read this directory, and a BOM is
            # the classic silent JSON.parse killer on Windows.
            [System.IO.File]::WriteAllText($cfgPath,
                ($cfg | ConvertTo-Json -Depth 20),
                (New-Object System.Text.UTF8Encoding $false))
            Ok "enabled at $url (backup: $cfgPath.bak)"
        }
    }
}

# ------------------------------------------------------------- 3. is it up yet
Step "Checking the editor's MCP server"

$listening = $false
try {
    $c = New-Object Net.Sockets.TcpClient
    $c.Connect("127.0.0.1", $Port)
    $listening = $c.Connected
    $c.Close()
} catch { }

if ($listening) {
    Ok "something is listening on 127.0.0.1:$Port"
    # A bound port is not an answer -- ask it to speak HTTP, which is exactly
    # what EngineProbe's http kind does.
    try {
        $req = [Net.HttpWebRequest]::Create("http://127.0.0.1:$Port$Path")
        $req.Method = "HEAD"; $req.Timeout = 3000
        try { $resp = $req.GetResponse(); $code = [int]$resp.StatusCode; $resp.Close() }
        catch [Net.WebException] { $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 } }
        if ($code -gt 0) { Ok "HTTP server answered (status $code) -- the editor is serving MCP" }
        else { ReportPortConflict }
    } catch { ReportPortConflict }
} else {
    Warn "nothing on 127.0.0.1:$Port yet."
    Write-Host ""
    if ($SkipEngineCheck) {
        # install-unreal-stack.ps1 has already flipped both plugins and set
        # bAutoStartServer at engine level. Repeating the manual steps here would
        # tell the owner to go and do work that was just done for them.
        Write-Host "    The engine side is already configured. Just open the project:" -ForegroundColor DarkGray
        Write-Host "      the MCP server auto-starts on port $Port when the editor loads." -ForegroundColor DarkGray
    } else {
        Write-Host "    In the Unreal Editor, once per project:" -ForegroundColor DarkGray
        Write-Host "      1. Edit > Plugins > enable 'Unreal MCP' AND 'All Toolsets' > restart" -ForegroundColor DarkGray
        Write-Host "      2. Edit > Editor Preferences > General > Model Context Protocol" -ForegroundColor DarkGray
        Write-Host "         > tick 'Auto Start Server'  (it ships OFF)" -ForegroundColor DarkGray
        Write-Host "      3. Confirm Server Port Number = $Port and Server URL Path = $Path" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "    Or let install-unreal-stack.ps1 do all three for you." -ForegroundColor DarkGray
    }
    Write-Host ""
    Write-Host "    The editor must be OPEN whenever an agent uses unreal__ tools." -ForegroundColor DarkGray
}

# ------------------------------------------------------------------- 4. verdict
Write-Host ""
if ($script:Failed.Count -eq 0 -and $script:Warned.Count -eq 0) {
    Write-Host "Unreal bridge ready. Restart your agent, then call bridge_status." -ForegroundColor Green
} elseif ($script:Failed.Count -eq 0) {
    Write-Host "Config is done; the editor side is not up yet (see WARN above)." -ForegroundColor Yellow
    Write-Host "Re-run this script after the editor is open to confirm." -ForegroundColor Yellow
} else {
    Write-Host "Blocked:" -ForegroundColor Red
    $script:Failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
