<#
.SYNOPSIS
  Register (or re-register) the brainx-node as the Windows service "BrainXNode".
  Called by BrainXNode-Setup.exe; also runnable standalone (as Administrator).

.NOTES
  - Robust against the "service marked for deletion" race that breaks repeat installs.
  - Writes a full transcript to <root>\install-log.txt; if the service won't start
    it captures the node's own startup output to <root>\node-startup.txt.
  - Generates + persists a bearer token; binds Kestrel to localhost only.
#>
[CmdletBinding()]
param(
    [string]$AppDir   = "C:\brainx\app",
    [string]$VaultDir = "C:\brainx\vault",
    [int]   $Port     = 5142,
    [string]$Domain   = ""      # e.g. serverbrain.example.com — restricts CORS when set
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
        Write-Host "[warn] $svc still 'marked for deletion' — close services.msc / Event Viewer if open."
    }
}

# Bearer token — reuse an existing one across re-installs, else generate 24 bytes hex.
$tokenFile = Join-Path $root "bearer-token.txt"
if (Test-Path $tokenFile) { $token = (Get-Content $tokenFile -Raw).Trim() }
else {
    $token = -join ((1..24) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
    $token | Out-File $tokenFile -Encoding ascii -NoNewline
}

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
    Write-Host "[FATAL] could not create $svc — likely lingering 'marked for deletion'. Reboot or close services.msc, then re-run."
    try { Stop-Transcript } catch {}; exit 1
}
# Auto-restart on crash + a description (New-Service can't set these).
& sc.exe description $svc "BrainX brain-matchmaking node (Kestrel on $Port)" *> $null
& sc.exe failure $svc reset= 86400 actions= restart/5000/restart/5000/restart/5000 *> $null

# Service environment (registry MultiString).
$envs = @(
    "ASPNETCORE_URLS=http://127.0.0.1:$Port",
    "BrainX__EmbeddedMode=false",
    "BrainX__RequireAuth=true",
    "BrainX__BearerToken=$token",
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
    Write-Host "[warn] not Running — capturing the node's startup output for diagnosis:"
    $o = Join-Path $root "node-startup.txt"
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"; $env:BrainX__VaultPath = $VaultDir
    $p = Start-Process $exe -PassThru -RedirectStandardOutput $o -RedirectStandardError "$o.err" -WindowStyle Hidden
    Start-Sleep 6; if (-not $p.HasExited) { $p.Kill() }
    Get-Content $o, "$o.err" -ErrorAction SilentlyContinue | Out-Host
}

try {
    $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 8
    Write-Host "[ok] node healthy — embedded=$($h.embedded) authRequired=$($h.authRequired)"
} catch { Write-Host "[warn] health probe failed: $($_.Exception.Message)" }

Write-Host ""
Write-Host "Done. Service '$svc' = $status   |   token: $tokenFile   |   log: $log"
Write-Host "Open the dashboard:  http://localhost:$Port/"
Write-Host "NEXT: 1) copy your vault's .obsidianx into $VaultDir   2) Setup-Tunnel.ps1 -Domain <your-domain>"
try { Stop-Transcript } catch {}
