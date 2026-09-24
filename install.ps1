<#
  BrainX Node - one-shot Windows installer / bootstrapper for running a node on
  THIS PC as a normal process (dev / personal use). Production servers use the
  BrainXNode-Setup.exe installer instead, which registers a Windows Service.

  Installs the brainx-node server + BrainX Server Manager into
  %LOCALAPPDATA%\BrainX\node, prepares a vault folder, (optionally) installs
  Ollama, and launches the Manager (in Dev mode it starts/stops the node as a
  child process).

  Pure ASCII on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI.

  Dependency handling:
    - If a self-contained build exists in .\dist (run publish.ps1 first), NOTHING
      else is needed: the .NET runtime is bundled in the exes.
    - Otherwise the script ensures the .NET 10 Desktop Runtime is present (winget,
      with a fallback to the official dotnet-install script) and uses a
      framework-dependent build.

  Start with Windows: the Manager runs elevated (it controls services), and
  Windows never auto-starts an elevated app from the Run key. The Manager offers
  a logon Scheduled Task on its first run instead.

  Usage:
    powershell -ExecutionPolicy Bypass -File install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1 -WithOllama -VaultPath "D:\MyVault" -Port 5142
#>
[CmdletBinding()]
param(
    [string] $VaultPath = (Join-Path $env:USERPROFILE "BrainX-Vault"),
    [int]    $Port = 5142,
    [string] $StorageProvider = "sqlite",
    [switch] $WithOllama,
    [switch] $NoAutostart,   # kept so old command lines still run; autostart is now the Manager's own offer
    [switch] $NoLaunch
)

$ErrorActionPreference = "Stop"
$root       = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA "BrainX\node"
$distMon    = Join-Path $root "dist\monitor"
$distSrv    = Join-Path $root "dist\server"
$managerExeName = "BrainX.ServerManager.exe"

function Info($m){ Write-Host "[install] $m" -ForegroundColor Cyan }
function Ok($m)  { Write-Host "[install] $m" -ForegroundColor Green }
function Warn($m){ Write-Host "[install] $m" -ForegroundColor Yellow }

# -- 1. Ensure we have something to install -----------------------------------
$selfContained = (Test-Path (Join-Path $distMon $managerExeName)) -and `
                 (Test-Path (Join-Path $distSrv "BrainX.Server.exe"))

if (-not $selfContained) {
    Warn "No self-contained build in .\dist - falling back to a framework-dependent build."
    Info "Checking for the .NET runtime..."
    $hasDotnet = $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)
    if (-not $hasDotnet) {
        Info "Installing the .NET 10 Desktop Runtime..."
        $wg = Get-Command winget -ErrorAction SilentlyContinue
        if ($wg) {
            winget install --id Microsoft.DotNet.DesktopRuntime.10 --silent --accept-source-agreements --accept-package-agreements
        } else {
            # Fallback: official dotnet-install script (per-user, no admin).
            $dl = Join-Path $env:TEMP "dotnet-install.ps1"
            Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $dl
            & $dl -Channel 10.0 -Runtime windowsdesktop -InstallDir (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet")
            $env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:Path"
        }
    } else { Ok ".NET runtime present." }

    Info "Building (framework-dependent)..."
    dotnet publish (Join-Path $root "BrainX.Server\BrainX.Server.csproj") -c Release -o (Join-Path $root "dist\server") --nologo
    dotnet publish (Join-Path $root "BrainX.NodeMonitor\BrainX.NodeMonitor.csproj") -c Release -o (Join-Path $root "dist\monitor") --nologo
}

# -- 2. Copy into the install dir ---------------------------------------------
Info "Installing to $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $distSrv "*") (Join-Path $installDir "server") -Recurse -Force
Copy-Item (Join-Path $distMon "*") (Join-Path $installDir "monitor") -Recurse -Force
$managerExe = Join-Path $installDir "monitor\$managerExeName"

# -- 3. Vault folder ----------------------------------------------------------
if (-not (Test-Path $VaultPath)) { New-Item -ItemType Directory -Force -Path $VaultPath | Out-Null }
New-Item -ItemType Directory -Force -Path (Join-Path $VaultPath ".obsidianx") | Out-Null
Ok "Vault: $VaultPath"

# -- 4. Seed the Manager's Dev-mode settings (vault / port / server exe) -------
$cfgDir = Join-Path $env:APPDATA "BrainX\NodeMonitor"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
@{
    Port = $Port
    VaultPath = $VaultPath
    StorageProvider = $StorageProvider
    MySqlConnString = ""
    ServerPathOverride = (Join-Path $installDir "server\BrainX.Server.exe")
} | ConvertTo-Json | Set-Content -Path (Join-Path $cfgDir "settings.json") -Encoding utf8
Ok "Manager settings seeded (port $Port, storage $StorageProvider)."

# -- 5. Optional: Ollama (local AI backend) ------------------------------------
if ($WithOllama) {
    if (Get-Command ollama -ErrorAction SilentlyContinue) {
        Ok "Ollama already installed."
    } elseif (Get-Command winget -ErrorAction SilentlyContinue) {
        Info "Installing Ollama..."
        winget install --id Ollama.Ollama --silent --accept-source-agreements --accept-package-agreements
    } else {
        Warn "winget not found - install Ollama manually from https://ollama.com if you want local AI."
    }
}

# -- 6. Old autostart entry ----------------------------------------------------
# Earlier versions registered BrainX.NodeMonitor.exe under the Run key; that exe
# no longer exists. Remove the stale entry (the Manager offers a logon task).
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Get-ItemProperty -Path $run -Name "BrainXNodeMonitor" -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $run -Name "BrainXNodeMonitor" -ErrorAction SilentlyContinue
    Ok "Removed the old BrainXNodeMonitor autostart entry."
}

# -- 7. Launch -----------------------------------------------------------------
if (-not $NoLaunch) {
    Info "Launching BrainX Server Manager (Windows will ask for administrator rights)..."
    Start-Process $managerExe
}
Ok "Done. BrainX Server Manager is in your tray: press Start, then Open Dashboard."
