# Self-contained single-file publish for the brainx-node toolset.
# Produces exes that run on Windows with NO .NET install required:
#   dist\monitor\BrainX.NodeMonitor.exe   — the tray controller (double-click this)
#   dist\server\BrainX.Server.exe         — the node (the monitor launches it; wwwroot ships beside it)
#
# Usage:  powershell -ExecutionPolicy Bypass -File publish.ps1
param([string]$Rid = "win-x64")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out  = Join-Path $root "dist"

$common = @(
    "-c", "Release", "-r", $Rid,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",   # bundle e_sqlite3 etc. into the exe
    "-p:EnableCompressionInSingleFile=true",
    "--nologo"
)

Write-Host "[publish] brainx-node server ($Rid, self-contained)…" -ForegroundColor Cyan
dotnet publish (Join-Path $root "BrainX.Server\BrainX.Server.csproj") @common -o (Join-Path $out "server")

Write-Host "[publish] BrainX Node Monitor ($Rid, self-contained)…" -ForegroundColor Cyan
dotnet publish (Join-Path $root "BrainX.NodeMonitor\BrainX.NodeMonitor.csproj") @common -o (Join-Path $out "monitor")

Write-Host "[publish] done → $out" -ForegroundColor Green
Get-ChildItem (Join-Path $out "monitor\BrainX.NodeMonitor.exe"), (Join-Path $out "server\BrainX.Server.exe") -ErrorAction SilentlyContinue |
    Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
