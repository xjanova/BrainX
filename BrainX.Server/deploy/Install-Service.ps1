<#
.SYNOPSIS
  Register (or re-register) the brainx-node as the Windows service "BrainXNode".
  Called by BrainXNode-Setup.exe; also runnable standalone (as Administrator).

.NOTES
  - Robust against the "service marked for deletion" race that breaks repeat installs.
  - Writes a full transcript to <root>\install-log.txt; if the service won't start
    it captures the node's own startup output to <root>\node-startup.txt.
  - Generates + persists a bearer token (OS CSPRNG) in <root>\bearer-token.txt,
    where the node reads it; it is NOT put in the service environment.
  - Locks <root> Program Files style: SYSTEM + Administrators full, Users read &
    execute; the token, logs, vault, cloud, self-update state and the Server
    Manager's backups are SYSTEM + Administrators only (the service runs as
    LocalSystem).
  - Binds Kestrel to localhost only.
  - Keep this file pure ASCII: Windows PowerShell 5.1 reads a BOM-less script
    in the ANSI codepage.
#>
[CmdletBinding()]
param(
    [string]$AppDir   = "C:\brainx\app",
    [string]$VaultDir = "C:\brainx\vault",
    [int]   $Port     = 5142,
    [string]$Domain   = ""      # e.g. serverbrain.example.com - restricts CORS when set
)
$ErrorActionPreference = "Continue"   # one soft error must not abort the whole install
$svc  = "BrainXNode"
$root = Split-Path $AppDir -Parent
$log  = Join-Path $root "install-log.txt"
try { Start-Transcript -Path $log -Force | Out-Null } catch {}

function Test-SvcExists($n) { & sc.exe query $n *> $null; return ($LASTEXITCODE -eq 0) }
function Wait-SvcGone($n, $timeoutSec = 25) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if (-not (Test-SvcExists $n)) { return $true }
        Start-Sleep -Milliseconds 800
    }
    return $false
}

Write-Host "== BrainX Node install =="
$exe = Join-Path $AppDir "BrainX.Server.exe"
if (-not (Test-Path $exe)) { Write-Host "[FATAL] not found: $exe"; try { Stop-Transcript } catch {}; exit 1 }
New-Item -ItemType Directory -Force $VaultDir | Out-Null

# Remove any prior service, handling the async 'marked for deletion' state.
if (Test-SvcExists $svc) {
    Write-Host "Removing existing $svc service..."
    & sc.exe stop $svc *> $null
    Start-Sleep 2
    & sc.exe delete $svc *> $null
    if (-not (Wait-SvcGone $svc)) {
        Write-Host "[warn] $svc still 'marked for deletion' - close services.msc / Event Viewer if open."
    }
}

# Bearer token - reuse an existing one across re-installs, else 32 bytes from the
# OS CSPRNG as hex (Get-Random is not a cryptographic generator). ASCII, no newline.
$tokenFile = Join-Path $root "bearer-token.txt"
$token = ""
if (Test-Path $tokenFile) { $token = ([string](Get-Content $tokenFile -Raw)).Trim() }
if (-not $token) {
    $bytes = New-Object byte[] 32
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = -join ($bytes | ForEach-Object { '{0:x2}' -f $_ })
    [System.IO.File]::WriteAllText($tokenFile, $token, [System.Text.Encoding]::ASCII)
    Write-Host "New owner token written to $tokenFile"
}

# Permissions, Program Files style. Inherited from C:\, the install root was
# readable by every user (bearer-token.txt) and writable by Authenticated Users
# (a DLL planted in app\ runs as SYSTEM).
#  1) the root and everything below it, app\ included: SYSTEM + Administrators
#     full, Users read & execute, inheritance off. Nobody else can write, and an
#     unelevated BrainX client can still see app\manager\BrainX.ServerManager.exe
#     (Windows must read its manifest to raise UAC).
#  2) then the private children: SYSTEM + Administrators only. After step 1,
#     because its /reset would otherwise undo them.
# Well-known SIDs, not names: account names are localized.
$sidSystem = "*S-1-5-18"       # NT AUTHORITY\SYSTEM
$sidAdmins = "*S-1-5-32-544"   # BUILTIN\Administrators
$sidUsers  = "*S-1-5-32-545"   # BUILTIN\Users
& icacls.exe $root /inheritance:r /grant:r "${sidSystem}:(OI)(CI)F" "${sidAdmins}:(OI)(CI)F" "${sidUsers}:(OI)(CI)RX" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "[warn] icacls on $root failed (exit $LASTEXITCODE)" }
& icacls.exe (Join-Path $root "*") /reset /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "[warn] icacls /reset under $root reported errors (exit $LASTEXITCODE)" }

New-Item -ItemType Directory -Force (Join-Path $root "logs") | Out-Null
$private = @($tokenFile, (Join-Path $root "logs"), (Join-Path $root "cloud"), (Join-Path $root "selfupdate-state.json"), (Join-Path $root "manager-backups"))
$private += @(Get-ChildItem -Path $root -Directory -Filter "staging-*" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$vaultFull = [System.IO.Path]::GetFullPath($VaultDir).TrimEnd('\')
$rootFull  = [System.IO.Path]::GetFullPath($root).TrimEnd('\')
if ($vaultFull.StartsWith($rootFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) { $private += $vaultFull }
foreach ($p in $private) {
    if (Test-Path -LiteralPath $p -PathType Container) {
        & icacls.exe $p /inheritance:r /grant:r "${sidSystem}:(OI)(CI)F" "${sidAdmins}:(OI)(CI)F" /Q | Out-Null
    } elseif (Test-Path -LiteralPath $p -PathType Leaf) {
        & icacls.exe $p /inheritance:r /grant:r "${sidSystem}:F" "${sidAdmins}:F" /Q | Out-Null
    } else { continue }
    if ($LASTEXITCODE -ne 0) { Write-Host "[warn] icacls on $p failed (exit $LASTEXITCODE)" }
}
Write-Host "Permissions: $root = SYSTEM + Administrators full, Users read; token, logs, vault, cloud, backups = SYSTEM + Administrators only"

# Create the service, retrying through any lingering deletion.
$created = $false
for ($i = 1; $i -le 5 -and -not $created; $i++) {
    try {
        New-Service -Name $svc -BinaryPathName "`"$exe`"" -DisplayName "BrainX Node" `
            -StartupType Automatic -ErrorAction Stop | Out-Null
        $created = $true
    } catch {
        Write-Host "[create retry $i/5] $($_.Exception.Message)"
        Start-Sleep 3
    }
}
if (-not $created) {
    Write-Host "[FATAL] could not create $svc - likely lingering 'marked for deletion'. Reboot or close services.msc, then re-run."
    try { Stop-Transcript } catch {}; exit 1
}
# Auto-restart on crash + a description (New-Service can't set these).
& sc.exe description $svc "BrainX brain-matchmaking node (Kestrel on $Port)" *> $null
& sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/5000 *> $null

# Service environment (registry MultiString). No token here: the node reads
# <root>\bearer-token.txt, which only SYSTEM + Administrators can open, while
# this registry value is readable far more widely.
$envs = @(
    "ASPNETCORE_URLS=http://127.0.0.1:$Port",
    "BrainX__EmbeddedMode=false",
    "BrainX__RequireAuth=true",
    "BrainX__VaultPath=$VaultDir",
    "BrainX__AutoUpdate=true",
    "BrainX__UpdateServiceName=$svc"
)
if ($Domain) { $envs += "BrainX__AllowedOrigins=https://$Domain" }
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$svc" `
    -Name Environment -PropertyType MultiString -Value $envs -Force | Out-Null

# Start + verify; if it won't run, capture the node's own startup output.
Write-Host "Starting $svc..."
Start-Service $svc -ErrorAction SilentlyContinue
Start-Sleep 4
$status = (Get-Service $svc -ErrorAction SilentlyContinue).Status
Write-Host "Service status: $status"
if ($status -ne 'Running') {
    Write-Host "[warn] not Running - capturing the node's startup output for diagnosis:"
    $o = Join-Path $root "node-startup.txt"
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"; $env:BrainX__VaultPath = $VaultDir
    $p = Start-Process $exe -PassThru -RedirectStandardOutput $o -RedirectStandardError "$o.err" -WindowStyle Hidden
    Start-Sleep 6; if (-not $p.HasExited) { $p.Kill() }
    Get-Content $o, "$o.err" -ErrorAction SilentlyContinue | Out-Host
}

try {
    $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 8
    Write-Host "[ok] node healthy - embedded=$($h.embedded) authRequired=$($h.authRequired)"
} catch { Write-Host "[warn] health probe failed: $($_.Exception.Message)" }

Write-Host ""
Write-Host "Done. Service '$svc' = $status   |   token: $tokenFile   |   log: $log"
Write-Host "Open the dashboard:  http://localhost:$Port/"
Write-Host "NEXT: 1) copy your vault's .obsidianx into $VaultDir   2) Setup-Tunnel.ps1 -Domain <your-domain>"
try { Stop-Transcript } catch {}
