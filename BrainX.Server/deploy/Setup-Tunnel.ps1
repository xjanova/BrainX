<#
.SYNOPSIS
  One-shot Cloudflare Tunnel setup for the brainx-node. Downloads cloudflared,
  authenticates (browser), creates + routes the tunnel, and installs it as a
  Windows service. Run as Administrator AFTER Install-Service.ps1.

.EXAMPLE
  .\Setup-Tunnel.ps1 -Domain serverbrain.example.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Domain,   # e.g. serverbrain.example.com
    [int]   $Port       = 5142,
    [string]$TunnelName = "brainx-node"
)
$ErrorActionPreference = "Stop"
$cfDir = "C:\cloudflared"
New-Item -ItemType Directory -Force $cfDir | Out-Null
$cf = Join-Path $cfDir "cloudflared.exe"
if (-not (Test-Path $cf)) {
    Write-Host "Downloading cloudflared..."
    Invoke-WebRequest "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" -OutFile $cf
}

Write-Host "`n>>> A browser will open - sign in and authorize the zone for $Domain`n"
& $cf tunnel login
& $cf tunnel create $TunnelName
& $cf tunnel route dns $TunnelName $Domain

$cfHome = Join-Path $env:USERPROFILE ".cloudflared"
$tid = (Get-ChildItem $cfHome -Filter *.json | Sort-Object LastWriteTime -Descending | Select-Object -First 1).BaseName
@"
tunnel: $tid
credentials-file: $cfHome\$tid.json
ingress:
  - hostname: $Domain
    service: http://127.0.0.1:$Port
  - service: http_status:404
"@ | Out-File "$cfHome\config.yml" -Encoding ascii

& $cf --config "$cfHome\config.yml" service install
Start-Service cloudflared -ErrorAction SilentlyContinue

Write-Host "`n[done] Tunnel up. Verify:  Invoke-RestMethod https://$Domain/health"
Write-Host "       (Cloudflare DNS now points serverbrain at the tunnel - no origin IP, no 526.)"
