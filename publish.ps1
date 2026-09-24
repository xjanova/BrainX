# Self-contained publish for the brainx-node toolset. Runs with NO .NET install
# on the target machine.
#
# Pure ASCII on purpose: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI,
# and a UTF-8 dash decodes to a curly quote that can end a string early.
#
#   publish.ps1          dist\monitor\BrainX.ServerManager.exe  the control app
#                        dist\server\BrainX.Server.exe          the node, single file (dev use)
#
#   publish.ps1 -Node    publish\node = the layout of the release's FULL node zip,
#                        which BrainXNode.iss packages into the installer:
#                          BrainX.Server.exe (+ wwwroot)        the node
#                          mcp\brainx-mcp.exe                   BrainX Cloud /mcp + re-index
#                          manager\BrainX.ServerManager.exe     the control app
#
#   -Version 2.0.450     stamps BUILD_VERSION the way CI does; the node's
#                        self-update compares it with release tags.
#
# Usage:  powershell -ExecutionPolicy Bypass -File publish.ps1 [-Node] [-Version x.y.z]
param([string]$Rid = "win-x64", [switch]$Node, [string]$Version = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if ($Version) { $env:BUILD_VERSION = $Version }

$single = @(
    "-c", "Release", "-r", $Rid,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",   # bundle e_sqlite3 etc. into the exe
    "-p:EnableCompressionInSingleFile=true",
    "--nologo"
)
$folder = @(
    "-c", "Release", "-r", $Rid,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "--nologo"
)

$server  = Join-Path $root "BrainX.Server\BrainX.Server.csproj"
$mcp     = Join-Path $root "BrainX.Mcp\BrainX.Mcp.csproj"
$manager = Join-Path $root "BrainX.NodeMonitor\BrainX.NodeMonitor.csproj"

if ($Node) {
    $out = Join-Path $root "publish\node"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Write-Host "[publish] node server ($Rid) -> $out" -ForegroundColor Cyan
    dotnet publish $server @folder -o $out
    if ($LASTEXITCODE -ne 0) { throw "server publish failed" }

    Write-Host "[publish] brainx-mcp -> $out\mcp" -ForegroundColor Cyan
    dotnet publish $mcp @folder -o (Join-Path $out "mcp")
    if ($LASTEXITCODE -ne 0) { throw "brainx-mcp publish failed" }

    Write-Host "[publish] Server Manager -> $out\manager" -ForegroundColor Cyan
    dotnet publish $manager @single -o (Join-Path $out "manager")
    if ($LASTEXITCODE -ne 0) { throw "Server Manager publish failed" }

    Write-Host "[publish] done. Next: ISCC.exe /DAppVer=<ver> BrainXNode.iss" -ForegroundColor Green
    Get-ChildItem (Join-Path $out "BrainX.Server.exe"), (Join-Path $out "mcp\brainx-mcp.exe"), (Join-Path $out "manager\BrainX.ServerManager.exe") -ErrorAction SilentlyContinue |
        Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
    return
}

$out = Join-Path $root "dist"

Write-Host "[publish] brainx-node server ($Rid, single file)" -ForegroundColor Cyan
dotnet publish $server @single -o (Join-Path $out "server")
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }

Write-Host "[publish] BrainX Server Manager ($Rid, single file)" -ForegroundColor Cyan
dotnet publish $manager @single -o (Join-Path $out "monitor")
if ($LASTEXITCODE -ne 0) { throw "Server Manager publish failed" }

Write-Host "[publish] done -> $out" -ForegroundColor Green
Get-ChildItem (Join-Path $out "monitor\BrainX.ServerManager.exe"), (Join-Path $out "server\BrainX.Server.exe") -ErrorAction SilentlyContinue |
    Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
