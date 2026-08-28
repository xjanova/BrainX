# Retrieval regression gate. Run BEFORE deploying a ranking-adjacent change:
#
#   .\eval-gate.ps1                       # paraphrase-46 (fast, ~3 min)
#   .\eval-gate.ps1 -Journal              # + journal-651 (slow; big changes only)
#   .\eval-gate.ps1 -Exe <path>           # gate a specific build (default: TestBuild, then dev Release)
#
# It runs `brainx-mcp eval`, then compares the SHIPPED arm's hit@5 / mrr@10
# against the most recent results file from an EARLIER date. A drop bigger
# than the epsilon fails the gate (exit 1). Numbers that only ever get looked
# at after a regression ships are numbers nobody looked at -- this makes the
# look mandatory and mechanical.
#
# Epsilon exists because n=46: one query flipping is ~2.2 points of hit@5 and
# the small-sample playbook (vault note, 2026-08-12) says a 1-query wobble is
# weather, not climate. Default 3.0 points fails only on >1 query regressing.
#
# ASCII-only on purpose -- see the header of deploy-mcp.ps1.

param(
    [string]$Vault = "G:\Obsidian",
    [string]$Exe = "",
    [switch]$Journal,
    [double]$EpsilonPoints = 3.0
)
$ErrorActionPreference = "Stop"

if (-not $Exe) {
    foreach ($cand in @("$PSScriptRoot\BrainX.Mcp\bin\TestBuild\brainx-mcp.exe",
                        "$PSScriptRoot\BrainX.Mcp\bin\Release\net9.0\brainx-mcp.exe")) {
        if (Test-Path $cand) { $Exe = $cand; break }
    }
}
if (-not $Exe -or -not (Test-Path $Exe)) { Write-Error "no brainx-mcp.exe found; pass -Exe" }

$evalDir = Join-Path $Vault ".obsidianx\eval"
$today = Get-Date -Format "yyyy-MM-dd"

function Gate([string]$goldPath, [string]$suffix) {
    Write-Host ""
    Write-Host "[gate] eval on $suffix ..." -ForegroundColor Cyan

    # Baseline = newest results file for this gold set dated BEFORE today.
    # Resolve it BEFORE running eval, which overwrites today's file.
    $baseline = Get-ChildItem $evalDir -Filter "results-*-$suffix.json" |
        Where-Object { $_.Name -notmatch [regex]::Escape($today) } |
        Sort-Object Name -Descending | Select-Object -First 1

    # cmd /c owns the redirection: PowerShell 5.1 redirecting a native exe's
    # stderr wraps each line in a NativeCommandError record, and under
    # ErrorActionPreference=Stop the first embed-timing line kills the gate.
    cmd /c "`"$Exe`" eval --vault `"$Vault`" --gold `"$goldPath`" --quiet >nul 2>&1"
    if ($LASTEXITCODE -ne 0) { Write-Error "eval run failed (exit $LASTEXITCODE)" }

    $currentPath = Join-Path $evalDir "results-$today-$suffix.json"
    if (-not (Test-Path $currentPath)) { Write-Error "eval wrote no $currentPath" }
    $cur = Get-Content $currentPath -Raw -Encoding utf8 | ConvertFrom-Json

    function ArmOf($doc) {
        $a = $doc.overall | Where-Object { $_.mode -eq "shipped" } | Select-Object -First 1
        if (-not $a) { $a = $doc.overall | Where-Object { $_.mode -eq "hybrid" } | Select-Object -First 1 }
        return $a
    }
    $curArm = ArmOf $cur
    Write-Host ("  today    ({0}):  hit@5 {1:P1}  mrr@10 {2:N3}" -f $curArm.mode, $curArm.'hit@5', $curArm.'mrr@10')

    if (-not $baseline) {
        Write-Host "  no earlier baseline for $suffix -- recording today as the first one." -ForegroundColor Yellow
        return $true
    }
    $base = Get-Content $baseline.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    $baseArm = ArmOf $base
    Write-Host ("  baseline ({0}):  hit@5 {1:P1}  mrr@10 {2:N3}   [{3}]" -f $baseArm.mode, $baseArm.'hit@5', $baseArm.'mrr@10', $baseline.Name)

    $dHit = ($curArm.'hit@5' - $baseArm.'hit@5') * 100.0
    $dMrr = ($curArm.'mrr@10' - $baseArm.'mrr@10') * 100.0
    Write-Host ("  delta:  hit@5 {0:+0.0;-0.0} pts   mrr@10 {1:+0.0;-0.0} pts" -f $dHit, $dMrr)

    if ($dHit -lt -$EpsilonPoints -or $dMrr -lt -$EpsilonPoints) {
        Write-Host "  REGRESSION beyond $EpsilonPoints pts -- do not deploy this ranking change." -ForegroundColor Red
        return $false
    }
    Write-Host "  OK" -ForegroundColor Green
    return $true
}

$ok = Gate (Join-Path $evalDir "gold-paraphrase.json") "gold-paraphrase"
if ($Journal) {
    $ok = (Gate (Join-Path $evalDir "gold.json") "gold") -and $ok
}

Write-Host ""
if (-not $ok) { Write-Error "eval gate FAILED" }
Write-Host "[gate] PASS" -ForegroundColor Green
