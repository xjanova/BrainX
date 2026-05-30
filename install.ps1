<#
  BrainX Node — one-shot Windows installer / bootstrapper.

  Installs the brainx-node server + the tray Monitor into %LOCALAPPDATA%\BrainX\node,
  prepares a vault folder, (optionally) installs Ollama, registers the Monitor to start
  with Windows, and launches it.

  Dependency handling:
    • If a self-contained build exists in .\dist (run publish.ps1 first), NOTHING else is
      needed — the .NET runtime is bundled in the exes.
    • Otherwise the script ensures the .NET 10 Desktop Runtime is present (winget, with a
      fallback to the official dotnet-install script) and uses a framework-dependent build.

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
    [switch] $NoAutostart,
    [switch] $NoLaunch
)

$ErrorActionPreference = "Stop"
$root      = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA "BrainX\node"
$distMon   = Join-Path $root "dist\monitor"
$distSrv   = Join-Path $root "dist\server"

function Info($m){ Write-Host "[install] $m" -ForegroundColor Cyan }
function Ok($m)  { Write-Host "[install] $m" -ForegroundColor Green }
function Warn($m){ Write-Host "[install] $m" -ForegroundColor Yellow }

# ── 1. Ensure we have something to install ───────────────────────────────────
$selfContained = (Test-Path (Join-Path $distMon "BrainX.NodeMonitor.exe")) -and `
                 (Test-Path (Join-Path $distSrv "BrainX.Server.exe"))

if (-not $selfContained) {
    Warn "No self-contained build in .\dist — falling back to a framework-dependent build."
    Info "Checking for the .NET runtime…"
    $hasDotnet = $null -ne (Get-Command dotnet -ErrorAction SilentlyContinue)
    if (-not $hasDotnet) {
        Info "Installing the .NET 10 Desktop Runtime…"
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

    Info "Building (framework-dependent)…"
    dotnet publish (Join-Path $root "BrainX.Server\BrainX.Server.csproj") -c Release -o (Join-Path $root "dist\server") --nologo
    dotnet publish (Join-Path $root "BrainX.NodeMonitor\BrainX.NodeMonitor.csproj") -c Release -o (Join-Path $root "dist\monitor") --nologo
}

# ── 2. Copy into the install dir ─────────────────────────────────────────────
Info "Installing to $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $distSrv "*") (Join-Path $installDir "server") -Recurse -Force
Copy-Item (Join-Path $distMon "*") (Join-Path $installDir "monitor") -Recurse -Force
$monitorExe = Join-Path $installDir "monitor\BrainX.NodeMonitor.exe"

# ── 3. Vault folder ──────────────────────────────────────────────────────────
if (-not (Test-Path $VaultPath)) { New-Item -ItemType Directory -Force -Path $VaultPath | Out-Null }
New-Item -ItemType Directory -Force -Path (Join-Path $VaultPath ".obsidianx") | Out-Null
Ok "Vault: $VaultPath"

# ── 4. Seed the Monitor's settings so it points at the right vault/port ──────
$cfgDir = Join-Path $env:APPDATA "BrainX\NodeMonitor"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
@{
    Port = $Port
    VaultPath = $VaultPath
    StorageProvider = $StorageProvider
    MySqlConnString = ""
    ServerPathOverride = (Join-Path $installDir "server\BrainX.Server.exe")
} | ConvertTo-Json | Set-Content -Path (Join-Path $cfgDir "settings.json") -Encoding utf8
Ok "Monitor settings seeded (port $Port, storage $StorageProvider)."

# ── 5. Optional: Ollama (local AI backend) ───────────────────────────────────
if ($WithOllama) {
    if (Get-Command ollama -ErrorAction SilentlyContinue) {
        Ok "Ollama already installed."
    } elseif (Get-Command winget -ErrorAction SilentlyContinue) {
        Info "Installing Ollama…"
        winget install --id Ollama.Ollama --silent --accept-source-agreements --accept-package-agreements
    } else {
        Warn "winget not found — install Ollama manually from https://ollama.com if you want local AI."
    }
}

# ── 6. Start with Windows ────────────────────────────────────────────────────
if (-not $NoAutostart) {
    $run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    New-ItemProperty -Path $run -Name "BrainXNodeMonitor" -Value "`"$monitorExe`"" -PropertyType String -Force | Out-Null
    Ok "Registered to start with Windows."
}

# ── 7. Launch ────────────────────────────────────────────────────────────────
if (-not $NoLaunch) {
    Info "Launching the Monitor…"
    Start-Process $monitorExe
}
Ok "Done. The BrainX Node Monitor is in your tray — press ▶ Start, then 🖥 Open Dashboard."
