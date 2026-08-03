# install-unreal-stack.ps1 -- one command to go from a bare machine to Unreal 5.8
# answering the brain's MCP bridge.
#
#   powershell -ExecutionPolicy Bypass -File D:\BrainX\install-unreal-stack.ps1
#   powershell -ExecutionPolicy Bypass -File D:\BrainX\install-unreal-stack.ps1 -Wait
#
# Idempotent and resumable: every phase checks before it acts. Run it now, do the
# one step that cannot be automated, run it again -- it picks up where it stopped.
# -WhatIf shows the plan without touching anything.
#
# WHAT THIS AUTOMATES THAT YOU WOULD OTHERWISE CLICK
#
# Epic's docs tell you to enable two plugins in Edit > Plugins and tick Auto Start
# Server in Editor Preferences, per project, forever. All three are just files:
#
#   Engine/Plugins/.../ModelContextProtocol.uplugin   "EnabledByDefault": true
#   Engine/Plugins/.../AllToolsets.uplugin            "EnabledByDefault": true
#   Engine/Config/BaseEditorPerProjectUserSettings.ini
#       [/Script/ModelContextProtocolEngine.ModelContextProtocolSettings]
#       bAutoStartServer / ServerPortNumber / ServerUrlPath
#
# Patching them at ENGINE level means every project on this machine gets MCP with
# no per-project setup, and no .uproject is touched -- so nothing leaks into a
# repo that other people share. (Property names confirmed against Epic's
# UModelContextProtocolSettings API reference, not guessed.)
#
# WHAT THIS CANNOT AUTOMATE, AND WILL NOT PRETEND TO
#
#   * Signing in to an Epic account.
#   * Accepting Epic's EULA.
#   * The ~100 GB engine download itself.
#
# Those are your account, your agreement and your bandwidth. The script installs
# the launcher, tells you exactly what to click, and (with -Wait) sits polling
# until the engine appears, then finishes everything else on its own.
#
# ASCII only on purpose: Windows PowerShell 5.1 mis-decodes UTF-8 multibyte
# characters and trips the parser before line one runs.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Vault       = "G:\Obsidian",
    [int]    $Port        = 0,                       # 0 = pick a free one
    [string] $UrlPath     = "/mcp",
    [string] $EngineRoot  = "",                      # blank = discover
    [string] $ProjectPath = "G:\Unreal\BrainXUnreal",
    [switch] $Wait,                                  # poll until the engine appears
    [int]    $WaitMinutes = 240,
    [switch] $SkipLauncher,
    [switch] $NoProject
)

$ErrorActionPreference = "Stop"

# Pre-load the modules we use. Autoloading them later happens INSIDE -WhatIf, and
# PowerShell then narrates every alias the module defines ("What if: Set Alias
# gcim...") as though the script were doing it -- 12 lines of noise between the
# user and the one line that matters.
$script:SavedWhatIf = $WhatIfPreference
$WhatIfPreference = $false
Import-Module CimCmdlets, NetTCPIP -ErrorAction SilentlyContinue
$WhatIfPreference = $script:SavedWhatIf

$script:Failed  = @()
$script:Warned  = @()
$script:Changed = @()

function Step($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    OK   $m" -ForegroundColor Green }
function Info($m) { Write-Host "    ..   $m" -ForegroundColor DarkGray }
function Warn($m) { Write-Host "    WARN $m" -ForegroundColor Yellow; $script:Warned += $m }
function Bad($m)  { Write-Host "    FAIL $m" -ForegroundColor Red;   $script:Failed += $m }
function Did($m)  { Write-Host "    SET  $m" -ForegroundColor Magenta; $script:Changed += $m }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Read/write JSON without letting PS 5.1 guess an encoding. .uplugin files are
# parsed by UE's own C++ JSON reader; a BOM is the classic silent killer, so
# every write here is UTF-8 no-BOM.
function Read-JsonFile($path) { [System.IO.File]::ReadAllText($path) | ConvertFrom-Json }
function Write-JsonFile($path, $obj) {
    [System.IO.File]::WriteAllText($path, ($obj | ConvertTo-Json -Depth 40),
        (New-Object System.Text.UTF8Encoding $false))
}
function Backup-Once($path) {
    $bak = "$path.brainx.bak"
    if (-not (Test-Path $bak)) { Copy-Item $path $bak -Force }   # never overwrite the pristine copy
    return $bak
}

function Find-UnrealEditor($root) {
    if ($root) {
        $c = Join-Path $root "Engine\Binaries\Win64\UnrealEditor.exe"
        if (Test-Path $c) { return $c }
    }
    # The launcher's manifest is authoritative. A folder called UE_5.8 proves
    # nothing -- a Unity Hub folder on this machine held only metadata and no
    # editor at all, and it read as an install to Get-ChildItem.
    $dat = "C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat"
    if (Test-Path $dat) {
        try {
            foreach ($i in (Read-JsonFile $dat).InstallationList) {
                $c = Join-Path $i.InstallLocation "Engine\Binaries\Win64\UnrealEditor.exe"
                if (Test-Path $c) { return $c }
            }
        } catch { }
    }
    foreach ($r in @("C:\Program Files\Epic Games", "D:\Epic Games", "E:\Epic Games",
                     "F:\Epic Games", "G:\Epic Games", "D:\UnrealEngine", "G:\UnrealEngine")) {
        if (-not (Test-Path $r)) { continue }
        $hit = Get-ChildItem $r -Directory -ErrorAction SilentlyContinue |
               ForEach-Object { Join-Path $_.FullName "Engine\Binaries\Win64\UnrealEditor.exe" } |
               Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $null
}

function Get-FreePort($preferred) {
    $busy = (Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue).LocalPort
    if ($preferred -gt 0) {
        if ($busy -notcontains $preferred) { return $preferred }
        Warn "requested port $preferred is already in use"
    }
    # 8000 is Epic's default AND one of the most contended ports on a dev box --
    # on this machine an elevated Manager.exe already held it before Unreal was
    # ever installed. Try it, but do not insist.
    foreach ($p in @(8000, 8077, 8123, 8181, 8222, 8321)) {
        if ($busy -notcontains $p) { return $p }
    }
    return 0
}

# ------------------------------------------------------------- 0. preflight
Step "Preflight"
Info "admin: $(if (Test-Admin) { 'yes' } else { 'no' })"
Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
    Where-Object { $_.Free -ne $null -and $_.Used -ne $null } |
    ForEach-Object { Info ("{0}: {1} GB free" -f $_.Name, [math]::Round($_.Free / 1GB, 1)) }
Info "Unreal 5.8 needs roughly 100 GB installed."

# ------------------------------------------------- 1. Epic Games Launcher
Step "Epic Games Launcher"
$launcherExe = @(
    "C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
    "C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($launcherExe) {
    Ok "already installed -> $launcherExe"
} elseif ($SkipLauncher) {
    Info "skipped (-SkipLauncher)"
} elseif ($PSCmdlet.ShouldProcess("EpicGames.EpicGamesLauncher", "winget install")) {
    Info "installing via winget (this is the only piece winget can provide -- the"
    Info "engine itself is not a winget package, it comes from the launcher)"
    # No 2>&1 on a native exe: under PS 5.1 that wraps stderr in ErrorRecords and
    # trips ErrorActionPreference='Stop' even when the exit code is 0, and winget
    # writes progress to stderr.
    winget install --id EpicGames.EpicGamesLauncher --silent `
        --accept-package-agreements --accept-source-agreements | Out-Host
    if ($LASTEXITCODE -eq 0) { Did "Epic Games Launcher installed" }
    else { Bad "winget install failed (exit $LASTEXITCODE)" }
}

# ------------------------------------------------------------- 2. the engine
Step "Unreal Engine 5.8+"
$editor = Find-UnrealEditor $EngineRoot

if (-not $editor -and $Wait) {
    Write-Host ""
    Write-Host "    Waiting for the engine. In the Epic Games Launcher:" -ForegroundColor Yellow
    Write-Host "      1. Sign in to your Epic account" -ForegroundColor Yellow
    Write-Host "      2. Unreal Engine tab > Library > '+' > choose 5.8" -ForegroundColor Yellow
    Write-Host "      3. Options > install location: pick G: (most free space here)" -ForegroundColor Yellow
    Write-Host "      4. Install, then leave this running -- it will continue by itself." -ForegroundColor Yellow
    Write-Host ""
    if ($launcherExe -and $PSCmdlet.ShouldProcess("Epic Games Launcher", "launch")) {
        Start-Process $launcherExe | Out-Null
    }
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 30
        $editor = Find-UnrealEditor $EngineRoot
        if ($editor) { break }
        Write-Host "." -NoNewline -ForegroundColor DarkGray
    }
    Write-Host ""
}

if (-not $editor) {
    Bad "Unreal Engine not found."
    Write-Host ""
    Write-Host "    This is the one step that cannot be automated: it needs your Epic" -ForegroundColor DarkGray
    Write-Host "    login and your acceptance of Epic's EULA." -ForegroundColor DarkGray
    Write-Host "      Launcher > Unreal Engine > Library > '+' > 5.8 > install to G:" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "    Then re-run this script -- or re-run it now with -Wait and it will" -ForegroundColor DarkGray
    Write-Host "    finish the rest by itself the moment the engine appears." -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Blocked:" -ForegroundColor Red
    $script:Failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

$engineDir = (Get-Item $editor).Directory.Parent.Parent.Parent.FullName   # ...\Engine\Binaries\Win64 -> root
$ueVersion = (Get-Item $editor).VersionInfo.ProductVersion
Ok "UnrealEditor.exe -> $editor"
Ok "version $ueVersion"

if ($ueVersion -match '^(\d+)\.(\d+)') {
    $maj = [int]$Matches[1]; $min = [int]$Matches[2]
    if ($maj -lt 5 -or ($maj -eq 5 -and $min -lt 8)) {
        Bad "the built-in MCP plugin is 5.8+. This is $ueVersion -- nothing to bridge to."
    }
}

# ------------------------------------------------- 3. engine-level plugin patch
Step "Enabling the MCP plugins engine-wide"

$pluginRoot = Join-Path $engineDir "Engine\Plugins"
if (-not (Test-Path $pluginRoot)) {
    Bad "no Engine\Plugins under $engineDir"
} else {
    # SEARCH, never hardcode: Epic moves plugins between Experimental/ and
    # Runtime/ across releases, and a hardcoded path would fail silently on the
    # first version that reorganised.
    foreach ($want in @("ModelContextProtocol", "AllToolsets")) {
        $up = Get-ChildItem $pluginRoot -Recurse -Filter "$want.uplugin" -ErrorAction SilentlyContinue |
              Select-Object -First 1
        if (-not $up) { Bad "$want.uplugin not found under $pluginRoot"; continue }

        try {
            $j = Read-JsonFile $up.FullName
            if ($j.EnabledByDefault -eq $true) {
                Ok "$want already EnabledByDefault"
            } elseif ($PSCmdlet.ShouldProcess($up.FullName, "set EnabledByDefault = true")) {
                if (-not (Test-Admin) -and $up.FullName -like "C:\Program Files*") {
                    Bad "$want needs admin to edit under Program Files -- re-run this script as Administrator"
                    continue
                }
                $bak = Backup-Once $up.FullName
                $j | Add-Member -NotePropertyName EnabledByDefault -NotePropertyValue $true -Force
                Write-JsonFile $up.FullName $j
                Did "$want EnabledByDefault = true  (backup: $bak)"
            }
        } catch { Bad "could not patch $($up.FullName): $($_.Exception.Message)" }
    }
}

# ----------------------------------------------- 4. engine-level MCP settings
Step "Auto-starting the MCP server"

$Port = Get-FreePort $Port
if ($Port -eq 0) { Bad "no free port found in 8000-8321" } else { Ok "port $Port" }

$baseIni = Join-Path $engineDir "Engine\Config\BaseEditorPerProjectUserSettings.ini"
$section = "[/Script/ModelContextProtocolEngine.ModelContextProtocolSettings]"

if (-not (Test-Path $baseIni)) {
    Bad "no $baseIni"
} elseif ($PSCmdlet.ShouldProcess($baseIni, "set bAutoStartServer / ServerPortNumber / ServerUrlPath")) {
    if (-not (Test-Admin) -and $baseIni -like "C:\Program Files*") {
        Bad "editing $baseIni needs admin -- re-run this script as Administrator"
    } else {
        try {
            $bak  = Backup-Once $baseIni
            $text = [System.IO.File]::ReadAllText($baseIni)

            $block = @(
                $section,
                "bAutoStartServer=True",
                "ServerPortNumber=$Port",
                "ServerUrlPath=$UrlPath",
                # PINNED, not left to the default. Tool Search makes the list
                # endpoint return just three meta-tools (list_toolsets,
                # describe_toolset, call_tool) and dispatch the rest. Unreal 5.8
                # exposes on the order of 830 tools across 52 toolsets; with this
                # off, ALL of them arrive as unreal__* and are merged into the
                # brain's own tool list, drowning it in every agent session.
                # It defaults to true today -- that is exactly why a silent flip
                # would be so expensive, so state it.
                "bEnableToolSearch=True"
            ) -join "`r`n"

            if ($text -match [regex]::Escape($section)) {
                # Replace our whole section rather than appending a second copy:
                # UE reads the LAST value, so duplicates work by luck, not design.
                $pattern = [regex]::Escape($section) + "(\r?\n(?!\[)[^\r\n]*)*"
                $text = [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $block })
            } else {
                $text = $text.TrimEnd() + "`r`n`r`n" + $block
            }

            # Normalise the ending in BOTH paths. The replace regex is greedy and
            # also consumes the file's trailing newline, so without this the
            # rebuilt text differed from the file by exactly one CRLF on every
            # re-run: the comparison below always saw a change, and each run
            # quietly shaved a newline off the file.
            $text = $text.TrimEnd() + "`r`n"

            # Only claim a change when there IS one. Re-running used to report
            # "SET bAutoStartServer..." every time and list it under Changed:,
            # so a no-op run looked identical to a real one -- a summary that
            # cries wolf is worse than no summary.
            if ($text -ceq [System.IO.File]::ReadAllText($baseIni)) {
                Ok "already set: bAutoStartServer=True, ServerPortNumber=$Port, ServerUrlPath=$UrlPath"
            } else {
                # Engine ini files are ASCII/UTF-8 no-BOM.
                [System.IO.File]::WriteAllText($baseIni, $text, (New-Object System.Text.UTF8Encoding $false))
                Did "bAutoStartServer=True, ServerPortNumber=$Port, ServerUrlPath=$UrlPath  (backup: $bak)"
            }
            Info "engine-level: applies to every project on this machine, and no .uproject is touched"
        } catch { Bad "could not patch $baseIni : $($_.Exception.Message)" }
    }
}

Warn "an engine UPDATE replaces these files -- re-run this script after upgrading Unreal"

# --------------------------------------------------------------- 5. a project
if (-not $NoProject) {
    Step "Project"
    $uproject = Join-Path $ProjectPath ((Split-Path $ProjectPath -Leaf) + ".uproject")
    if (Test-Path $uproject) {
        Ok "already exists -> $uproject"
    } elseif ($PSCmdlet.ShouldProcess($uproject, "create a blank Blueprint project")) {
        # A blank BP project really is just this file plus a Content dir; the
        # editor generates everything else on first open. No template copy, no
        # engine invocation, nothing to go stale.
        New-Item -ItemType Directory -Force -Path (Join-Path $ProjectPath "Content") | Out-Null
        $assoc = if ($ueVersion -match '^(\d+\.\d+)') { $Matches[1] } else { "5.8" }
        Write-JsonFile $uproject ([pscustomobject]@{
            FileVersion       = 3
            EngineAssociation = $assoc
            Category          = ""
            Description       = "Created by install-unreal-stack.ps1 for the BrainX MCP bridge"
        })
        Did "created $uproject (engine $assoc)"
    }
}

# ---------------------------------------------------------------- 6. the bridge
Step "Wiring the brain's bridge"
$setup = Join-Path $PSScriptRoot "setup-unreal-bridge.ps1"
if (-not (Test-Path $setup)) {
    Bad "setup-unreal-bridge.ps1 not found next to this script"
} else {
    # Reuse rather than duplicate: that script owns config wiring and verification.
    # -SkipEngineCheck because we have already found the engine (possibly at a
    # non-default -EngineRoot it would never look in).
    & $setup -Vault $Vault -Port $Port -Path $UrlPath -SkipEngineCheck -WhatIf:$WhatIfPreference
}

# ------------------------------------------------------------------- verdict
Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
if ($script:Changed.Count -gt 0) {
    Write-Host "Changed:" -ForegroundColor Magenta
    $script:Changed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Magenta }
}
if ($script:Failed.Count -gt 0) {
    Write-Host "Blocked:" -ForegroundColor Red
    $script:Failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host ""
Write-Host "Engine side is configured. Last step:" -ForegroundColor Green
Write-Host "  1. Open the project in Unreal (the MCP server auto-starts on port $Port)" -ForegroundColor Green
Write-Host "  2. Re-run this script, or setup-unreal-bridge.ps1 -Port $Port, to confirm" -ForegroundColor Green
Write-Host "  3. Restart your agent, then call bridge_status" -ForegroundColor Green
if ($script:Warned.Count -gt 0) {
    Write-Host ""
    Write-Host "Warnings:" -ForegroundColor Yellow
    $script:Warned | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}
