<#
.SYNOPSIS
  Install a BrainX node release by hand, for a node that cannot update itself.

.DESCRIPTION
  The node updates itself every 6 hours. Nodes older than v2.0.435 fetch
  brainx-node-win-x64.zip (about 61 MB) into memory under a 25 s timeout, so
  on a link slower than about 2.5 MB/s the download never completes. Such a
  node downloads the same release again every 6 hours and never installs it.
  This script does that install once, by hand. From v2.0.435 on, the node
  streams the package to disk with a 30 minute deadline and checks its
  signature, so it needs no help after that.

  Nothing on the node changes until the package has been downloaded, matches
  the SHA-256 GitHub publishes for it, and has been unpacked:
    1. find the BrainXNode service and the folder its exe runs from
    2. ask GitHub for the release (the latest by default) and its full node
       package: the server, mcp\ and manager\
    3. download it and check its SHA-256
    4. unpack it beside the app folder and check the files a node needs
    5. back up the app folder (the node keeps running meanwhile)
    6. stop the service, copy the new build over the app folder, start it.
       The vault, token, logs and cloud data are outside the app folder.
    7. wait for /health. If the node does not answer, put the backup back -
       and the service environment, which the new build changes on its first
       start - and start the old build again.

  The package also carries update-manifest.sig. This script does not check it
  (Windows PowerShell 5.1 has no easy ECDSA P-256 import); its check is
  GitHub's SHA-256 over TLS. The node checks the signature of every later
  update itself.

.PARAMETER Tag
  Release to install, e.g. v2.0.435. Default: the latest release.

.PARAMETER DryRun
  Steps 1-4 only, then clean up; the service is not touched. Shows the link
  speed and that the package verifies.

.PARAMETER Force
  Install even when this node already runs that version or a newer one.

.EXAMPLE
  # On the node server, in a PowerShell window started as Administrator:
  powershell -ExecutionPolicy Bypass -File .\Update-Node.ps1 -DryRun
  powershell -ExecutionPolicy Bypass -File .\Update-Node.ps1

.NOTES
  Keep this file pure ASCII: Windows PowerShell 5.1 reads a BOM-less script in
  the ANSI codepage.
#>
[CmdletBinding()]
param(
    [string]$Tag = "latest",
    [string]$Service = "BrainXNode",
    [string]$AppDir = "",          # default: the folder the service's exe runs from
    [string]$Repo = "xjanova/BrainX",
    [int]   $HealthTimeoutSec = 120,
    [switch]$DryRun,
    [switch]$Force
)
$ErrorActionPreference = "Stop"

$FullAsset  = "brainx-node-full-win-x64.zip"
$SmallAsset = "brainx-node-win-x64.zip"
$OldUpdaterSeconds = 25
$UserAgent = "brainx-node-manual-update"
# Left out of the app folder, as the node's own updater leaves them out.
$NotInstalled = @("update-manifest.json", "update-manifest.sig", "selfupdate.cmd")
# What a complete install has. The node's repair check looks for the same files.
$Required = @("BrainX.Server.exe", "mcp\brainx-mcp.exe", "manager\BrainX.ServerManager.exe")
# Service environment values that are safe to print. Every other value
# (tokens, keys, connection strings) is shown as "(set)".
$ShowValues = @("ASPNETCORE_URLS", "BrainX__Urls", "BrainX__EmbeddedMode", "BrainX__RequireAuth",
                "BrainX__VaultPath", "BrainX__AutoUpdate", "BrainX__UpdateServiceName",
                "BrainX__CloudEnabled", "BrainX__McpEnabled", "BrainX__AllowedOrigins")

function Write-Step([string]$text) { Write-Host ""; Write-Host "== $text" }

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# '"C:\brainx\app\BrainX.Server.exe" --x'  ->  'C:\brainx\app\BrainX.Server.exe'
function Get-ExePath([string]$commandLine) {
    $s = $commandLine.Trim()
    if ($s.StartsWith('"')) {
        $end = $s.IndexOf('"', 1)
        if ($end -gt 1) { return $s.Substring(1, $end - 1) }
        return $s.Trim('"')
    }
    $i = $s.IndexOf(".exe", [System.StringComparison]::OrdinalIgnoreCase)
    if ($i -ge 0) { return $s.Substring(0, $i + 4) }
    return $s
}

# The service's environment block (a registry MultiString) as a table.
function Get-ServiceEnvironment([string]$name) {
    $map = @{}
    $item = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Environment -ErrorAction SilentlyContinue
    if ($item) {
        foreach ($line in @($item.Environment)) {
            if (-not $line) { continue }
            $eq = $line.IndexOf('=')
            if ($eq -gt 0) { $map[$line.Substring(0, $eq)] = $line.Substring($eq + 1) }
        }
    }
    return $map
}

function Show-ServiceEnvironment($map) {
    if ($map.Count -eq 0) { Write-Host "  service environment: (none)"; return }
    foreach ($k in ($map.Keys | Sort-Object)) {
        if ($ShowValues -contains $k) { Write-Host ("  env {0} = {1}" -f $k, $map[$k]) }
        else { Write-Host ("  env {0} = (set)" -f $k) }
    }
}

# Kestrel's port from BrainX__Urls or ASPNETCORE_URLS (the first URL), else 5142.
function Get-NodePort($map) {
    foreach ($k in @("BrainX__Urls", "ASPNETCORE_URLS")) {
        $v = [string]$map[$k]
        if ($v -match ':(\d{2,5})(/|;|$)') { return [int]$Matches[1] }
    }
    return 5142
}

# "2.0.299+abc1234", "v2.0.435" -> 2.0.299, 2.0.435
function Get-Triple([string]$v) {
    $n = @(0, 0, 0)
    if ($v) {
        $s = ($v.Trim().TrimStart('v', 'V') -split '[+\-]')[0]
        $parts = $s.Split('.')
        for ($i = 0; $i -lt 3 -and $i -lt $parts.Count; $i++) {
            $x = 0
            if ([int]::TryParse($parts[$i], [ref]$x)) { $n[$i] = $x }
        }
    }
    return New-Object System.Version($n[0], $n[1], $n[2])
}

function Assert-AppDir([string]$dir) {
    $full = [System.IO.Path]::GetFullPath($dir).TrimEnd('\')
    $driveRoot = [System.IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full -eq $driveRoot) { throw "refusing to treat the drive root $full as the app folder" }
    if ($full.StartsWith($env:SystemRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw "refusing to use $full, which is inside Windows" }
    if (-not (Test-Path -LiteralPath (Join-Path $full "BrainX.Server.exe"))) { throw "there is no BrainX.Server.exe in $full" }
    return $full
}

function Get-Release([string]$repo, [string]$tag) {
    if ($tag -eq "latest") { $uri = "https://api.github.com/repos/$repo/releases/latest" }
    else {
        if (-not $tag.StartsWith("v")) { $tag = "v" + $tag }
        $uri = "https://api.github.com/repos/$repo/releases/tags/$tag"
    }
    return Invoke-RestMethod -Uri $uri -UserAgent $UserAgent -TimeoutSec 60
}

function Get-Asset($release, [string]$name) {
    foreach ($a in @($release.assets)) { if ($a.name -eq $name) { return $a } }
    return $null
}

function Format-MB([double]$bytes) { return ("{0:N1} MB" -f ($bytes / 1MB)) }

function Get-FreeBytes([string]$path) {
    $driveRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($path))
    return [double](New-Object System.IO.DriveInfo($driveRoot)).AvailableFreeSpace
}

function Get-FolderBytes([string]$path) {
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if ($sum) { return [double]$sum }
    return [double]0
}

# curl.exe (part of Windows since 1803 / Server 2019) has no deadline and
# retries; Invoke-WebRequest is the fallback.
function Save-Url([string]$url, [string]$outFile) {
    $curl = Join-Path $env:SystemRoot "System32\curl.exe"
    if (Test-Path -LiteralPath $curl) {
        & $curl -L --fail --silent --show-error --retry 3 --connect-timeout 30 -A $UserAgent -o $outFile $url
        if ($LASTEXITCODE -ne 0) { throw "the download failed (curl.exe exit $LASTEXITCODE)" }
        return
    }
    $saved = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"   # the progress bar slows Invoke-WebRequest many times over
    try { Invoke-WebRequest -Uri $url -OutFile $outFile -UseBasicParsing -UserAgent $UserAgent }
    finally { $ProgressPreference = $saved }
}

function Assert-Sha256([string]$file, [string]$digest) {
    if (-not $digest -or -not $digest.StartsWith("sha256:", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "GitHub publishes no sha256 digest for this asset - not installing a package that cannot be checked"
    }
    $want = $digest.Substring(7).Trim().ToLowerInvariant()
    $got = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
    if ($got -ne $want) { throw "SHA-256 mismatch: GitHub says $want, the download is $got" }
    return $got
}

function Expand-Package([string]$zip, [string]$dest) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
}

function Get-MissingFiles([string]$dir) {
    return @($Required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dir $_)) })
}

# Every process running from the app folder: the node, and any brainx-mcp it started.
function Get-AppProcesses([string]$dir) {
    $prefix = $dir.TrimEnd('\') + '\'
    return @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
    })
}

function Stop-Node([string]$service, [string]$dir) {
    $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Host "  stopping $service..."
        try {
            if ($svc.Status -ne 'StopPending') { $svc.Stop() }
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        } catch {
            Write-Host "  [warn] $service did not report Stopped within 60 s ($($_.Exception.Message))"
        }
    }
    for ($i = 0; $i -lt 30; $i++) {
        if (@(Get-AppProcesses $dir).Count -eq 0) { return }
        Start-Sleep -Seconds 1
    }
    foreach ($p in @(Get-AppProcesses $dir)) {
        Write-Host ("  [warn] ending {0} (pid {1}), still running from the app folder" -f $p.Name, $p.ProcessId)
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

# robocopy's exit code: below 8 = done (1 copied, 2 extras, 4 mismatches), 8 or more = failed.
function Invoke-Robocopy([string]$from, [string]$to, [string[]]$options) {
    $rcArgs = @($from, $to) + $options + @("/R:2", "/W:2", "/NP", "/NFL", "/NDL", "/NJH", "/NJS")
    & robocopy.exe @rcArgs | Out-Host
    return $LASTEXITCODE
}

function Wait-Health([int]$port, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { return (Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 5) }
        catch { Start-Sleep -Seconds 3 }
    }
    return $null
}

function Restore-Backup([string]$backup, [string]$dir, [string]$service, [int]$port, [string[]]$envBefore) {
    Write-Step "Putting the previous build back"
    if (-not (Test-Path -LiteralPath (Join-Path $backup "BrainX.Server.exe"))) {
        Write-Host "  [FATAL] $backup has no BrainX.Server.exe - the app folder was left as it is"
        return $false
    }
    Stop-Node $service $dir
    if ($null -ne $envBefore) {
        # On its first start the new build moves the owner token from the
        # service environment into bearer-token.txt. The old build reads it only
        # from the environment, and without it refuses every owner request.
        New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$service" -Name Environment `
            -PropertyType MultiString -Value $envBefore -Force | Out-Null
        Write-Host "  service environment put back as it was"
    }
    # /MIR: the app folder becomes the backup again - the new mcp\ and manager\ go too.
    $rc = Invoke-Robocopy $backup $dir @("/MIR")
    if ($rc -ge 8) { Write-Host "  [FATAL] restoring failed (robocopy exit $rc)"; return $false }
    try { Start-Service -Name $service }
    catch { Write-Host "  [FATAL] $service would not start: $($_.Exception.Message)"; return $false }
    if (Wait-Health $port 60) { Write-Host "  the previous build is back and answers /health"; return $true }
    Write-Host "  [FATAL] the previous build does not answer /health either"
    return $false
}

$ts = Get-Date -Format "yyyyMMdd-HHmmss"
$exitCode = 0
$transcript = $false

if (-not (Test-IsAdmin)) {
    Write-Host "[FATAL] Run this in a PowerShell window started as Administrator."
    exit 1
}
# Windows PowerShell 5.1 may still offer only TLS 1.0, which GitHub refuses.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

try {
    Write-Step "1/7 The node on this machine"
    $svcInfo = Get-CimInstance Win32_Service -Filter "Name='$Service'" -ErrorAction SilentlyContinue
    if (-not $svcInfo -and -not $DryRun) { throw "there is no service named $Service on this machine" }
    if (-not $AppDir) {
        if (-not $svcInfo) { throw "there is no service named $Service - pass -AppDir" }
        $AppDir = Split-Path (Get-ExePath $svcInfo.PathName) -Parent
    }
    $AppDir = Assert-AppDir $AppDir
    $root = Split-Path $AppDir -Parent
    try { Start-Transcript -Path (Join-Path $root "manual-update-$ts.log") -Force | Out-Null; $transcript = $true } catch { }
    $current = (Get-Item -LiteralPath (Join-Path $AppDir "BrainX.Server.exe")).VersionInfo.ProductVersion
    $envMap = Get-ServiceEnvironment $Service
    $port = Get-NodePort $envMap
    $svcState = "not installed"
    if ($svcInfo) { $svcState = [string]$svcInfo.State }
    Write-Host "  service   $Service ($svcState)"
    Write-Host "  app       $AppDir"
    Write-Host "  running   $current"
    Write-Host "  port      $port"
    Show-ServiceEnvironment $envMap
    $missingNow = @(Get-MissingFiles $AppDir)
    if ($missingNow.Count -gt 0) { Write-Host ("  missing   " + ($missingNow -join ", ")) }
    # What earlier self-update attempts may have left behind.
    $leftovers = @(Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "node-*.zip" -or ($_.PSIsContainer -and $_.Name -like "staging-*") })
    foreach ($l in $leftovers) { Write-Host "  leftover  $($l.FullName)" }
    $statePath = Join-Path $root "selfupdate-state.json"
    if (Test-Path -LiteralPath $statePath) { Write-Host ("  state     " + (Get-Content -LiteralPath $statePath -Raw).Trim()) }

    Write-Step "2/7 The release on GitHub"
    $release = Get-Release $Repo $Tag
    $target = [string]$release.tag_name
    $asset = Get-Asset $release $FullAsset
    if (-not $asset) { throw "release $target has no $FullAsset" }
    $small = Get-Asset $release $SmallAsset
    Write-Host ("  {0}  {1} ({2}), published {3}" -f $target, $FullAsset, (Format-MB $asset.size), $release.published_at)
    $cmp = (Get-Triple $target).CompareTo((Get-Triple $current))
    if ($cmp -lt 0 -and -not $Force) { throw "this node runs $current, newer than $target (pass -Force to install an older build)" }
    if ($cmp -eq 0 -and $missingNow.Count -eq 0 -and -not $Force) {
        Write-Host "  this node already runs $target with mcp\ and manager\ - nothing to do (-Force reinstalls it)"
        return
    }

    $work = Join-Path $root "manual-update"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $zip = Join-Path $work $FullAsset
    $staging = Join-Path $work ("staging-" + (Get-Triple $target))
    $need = [double]$asset.size * 4 + (Get-FolderBytes $AppDir)
    $free = Get-FreeBytes $root
    Write-Host ("  disk      {0} free, about {1} needed (package, unpacked copy, backup)" -f (Format-MB $free), (Format-MB $need))
    if ($free -lt $need) { throw "not enough free disk space for the update" }

    Write-Step "3/7 Download and check"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Save-Url $asset.browser_download_url $zip
    $sw.Stop()
    $bytes = [double](Get-Item -LiteralPath $zip).Length
    $secs = [Math]::Max($sw.Elapsed.TotalSeconds, 0.1)
    $rate = $bytes / $secs
    Write-Host ("  {0} in {1:N0} s = {2}/s" -f (Format-MB $bytes), $secs, (Format-MB $rate))
    if ($small -and ([double]$small.size / $rate) -gt $OldUpdaterSeconds) {
        Write-Host ("  At this speed {0} ({1}) takes about {2:N0} s. Nodes before v2.0.435 give that download {3} s," -f $SmallAsset, (Format-MB $small.size), ([double]$small.size / $rate), $OldUpdaterSeconds)
        Write-Host "  so on this link they cannot update themselves."
    }
    $sha = Assert-Sha256 $zip $asset.digest
    Write-Host "  sha256 $sha matches GitHub"

    Write-Step "4/7 Unpack"
    Expand-Package $zip $staging
    $missing = @(Get-MissingFiles $staging)
    if ($missing.Count -gt 0) { throw ("the package lacks " + ($missing -join ", ")) }
    $newVersion = (Get-Item -LiteralPath (Join-Path $staging "BrainX.Server.exe")).VersionInfo.ProductVersion
    Write-Host "  BrainX.Server.exe $newVersion, mcp\ and manager\ present"

    if ($DryRun) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ""
        Write-Host "Dry run done: the package downloads, matches GitHub's SHA-256 and unpacks. The node was not touched."
        return
    }

    Write-Step "5/7 Back up the app folder"
    $backup = Join-Path $root ("app-backup-" + (Get-Triple $current) + "-" + $ts)
    $rc = Invoke-Robocopy $AppDir $backup @("/E")
    if ($rc -ge 8) { throw "the backup failed (robocopy exit $rc) - the node was not touched" }
    Write-Host "  $backup"

    Write-Step "6/7 Install"
    $health = $null
    $envItem = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$Service" -Name Environment -ErrorAction SilentlyContinue
    $envBefore = $null
    if ($envItem) { $envBefore = [string[]]@($envItem.Environment) }
    try {
        Stop-Node $Service $AppDir
        $rc = Invoke-Robocopy $staging $AppDir (@("/E", "/XF") + $NotInstalled)
        if ($rc -ge 8) { throw "copying the new build failed (robocopy exit $rc)" }
        Write-Host "  copied, starting $Service..."
        Start-Service -Name $Service
        Write-Step "7/7 Health"
        $health = Wait-Health $port $HealthTimeoutSec
        if (-not $health) { throw "the node did not answer http://127.0.0.1:$port/health within $HealthTimeoutSec s" }
    } catch {
        Write-Host "  [ERROR] $($_.Exception.Message)"
        $exitCode = 1
        if (-not (Restore-Backup $backup $AppDir $Service $port $envBefore)) {
            Write-Host ""
            Write-Host "  RECOVER BY HAND:  robocopy `"$backup`" `"$AppDir`" /MIR   then   Start-Service $Service"
        }
    }

    if ($health) {
        Write-Host ("  /health  " + ($health | ConvertTo-Json -Compress))
        if (-not ($health.PSObject.Properties.Name -contains "cloud")) {
            Write-Host "  [warn] /health has no 'cloud' field - is the old build still the one answering?"
        } elseif (-not $health.cloud) {
            Write-Host "  [warn] BrainX Cloud is off on this node - see the node log in $root\logs"
        }
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host ""
        Write-Host "Done: $Service runs $newVersion."
        Write-Host "  - It updates itself from now on: every 6 h, streamed, signature-checked. Log: $root\logs\selfupdate.log"
        Write-Host "  - The previous build is in $backup - delete it once you are happy."
        Write-Host "  - Server Manager: $AppDir\manager\BrainX.ServerManager.exe (start it from an elevated shell)."
    }
} catch {
    Write-Host ""
    Write-Host "[FATAL] $($_.Exception.Message)"
    $exitCode = 1
} finally {
    if ($transcript) { try { Stop-Transcript | Out-Null } catch { } }
}
exit $exitCode
