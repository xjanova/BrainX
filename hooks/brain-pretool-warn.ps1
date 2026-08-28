# PreToolUse hook: warn BEFORE an edit touches a file the vault holds a
# painful lesson about, and let the USER decide (permissionDecision "ask").
#
# This is the push half of brain-first. The pull half (UserPromptSubmit
# nudge) fires before the agent knows WHICH file it will touch; PostToolUse
# fires after the damage. The decision moment is here, in between.
#
# Hot path: this runs on every Edit/Write/MultiEdit. It therefore never
# parses the whole index -- it greps ONE line out of files.tsv
# (basename<TAB>json) and JSON-parses only that line. Whole-file JSON of a
# 1.3 MB index in PS 5.1 costs seconds; Select-String costs ~50 ms.
#
# Precision over recall, calibrated on 180 real sessions (2026-08-28):
#   threshold 4 -> mean 3.3 asks/session (too chatty, trains dismissal)
#   threshold 5 -> median 1, p90 5      <- default
# plus: once per basename per session, hard cap 3 asks per session.
# A warning that fires constantly is a warning nobody reads -- that exact
# failure ran for 3 months in brain-prompt-gate.ps1 (see vault note
# 963a1ef69d65).
#
# KEEP THIS FILE ASCII-ONLY: PS 5.1 mis-decodes BOM-less UTF-8 literals.
# Thai note titles arrive as DATA from files.tsv (read with -Encoding UTF8)
# and leave JSON-escaped, so they survive; string literals here must not.

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'SilentlyContinue'

function Quit { exit 0 }   # every failure path is silence, never a block

try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
} catch { Quit }
if (-not $payload) { Quit }

$tool = $payload.tool_name
if ($tool -notmatch '^(Edit|Write|MultiEdit)$') { Quit }
$fp = $payload.tool_input.file_path
if (-not $fp) { Quit }

$root = "$env:USERPROFILE\.claude"
$mode = if (Test-Path "$root\brain-mode.txt") { (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower() } else { 'always' }
if ($mode -eq 'off') { Quit }

# Vault notes and scratch files are not what the warn layer exists for.
$vault = if ($env:BRAINX_VAULT) { $env:BRAINX_VAULT } else { '__BRAINX_VAULT__' }
$fpn = $fp.ToLower().Replace('/', '\')
if ($fpn.StartsWith($vault.ToLower())) { Quit }
if ($fpn -match '\\(scratchpad|temp|tmp)\\' -or $fpn.EndsWith('.md')) { Quit }

$tsv = Join-Path $vault '.obsidianx\push-pack\files.tsv'
if (-not (Test-Path $tsv)) { Quit }

$base = [System.IO.Path]::GetFileName($fp).ToLower()
if (-not $base) { Quit }

# --- cooldown: once per file, max 3 asks per session ---------------------
$sid = if ($payload.session_id) { $payload.session_id } else { 'nosession' }
$cacheDir = "$root\cache"
if (-not (Test-Path $cacheDir)) { New-Item -ItemType Directory -Force $cacheDir | Out-Null }
$marker = Join-Path $cacheDir "brainx-warned-$sid.txt"
$warned = @()
if (Test-Path $marker) { $warned = @(Get-Content $marker -ErrorAction SilentlyContinue) }
if ($warned -contains $base) { Quit }
if ($warned.Count -ge 3) { Quit }

# --- the one-line lookup -------------------------------------------------
$esc = [regex]::Escape($base)
$hit = Select-String -Path $tsv -Pattern "^$esc`t" -Encoding UTF8 | Select-Object -First 1
if (-not $hit) { Quit }
$entries = $null
try { $entries = ($hit.Line -split "`t", 2)[1] | ConvertFrom-Json } catch { Quit }
$entries = @($entries | Where-Object { $_.w }) | Select-Object -First 6
if ($entries.Count -eq 0) { Quit }

# --- scoring (mirrors calibrate.py exactly) ------------------------------
$cwdLeaf = ''
if ($payload.cwd) {
    $cwdLeaf = ([System.IO.Path]::GetFileName($payload.cwd.TrimEnd('\', '/'))).ToLower()
    $cwdLeaf = $cwdLeaf -replace '^(code|d|e)[-]+', '' -replace '[^a-z0-9]', ''
}
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$today = (Get-Date).Date
$apSegs = $fpn.Trim('\').Split('\')

$highSeverity = @('gotcha','bug','bug-fix','bugfix','regression','deadlock','security','incident','data-loss')
$best = $null; $bestScore = -99
foreach ($e in $entries) {
    # Sharp lessons outrank broad ones: a 'gotcha' is a recorded burn on this
    # exact file; 'coding-lesson' is the vault's broadest tag.
    $s = if ($highSeverity -contains $e.t) { 3 } else { 2 }
    $epSegs = $e.p.ToLower().Replace('\', '/').Trim('/').Split('/')
    $seg = 0
    while (($seg -lt ([Math]::Min($epSegs.Count, $apSegs.Count) - 1)) -and
           ($epSegs[$epSegs.Count - 2 - $seg] -eq $apSegs[$apSegs.Count - 2 - $seg])) { $seg++ }
    $s += [Math]::Min($seg, 3)
    try {
        $d = ($today - [datetime]::ParseExact($e.m, 'yyyy-MM-dd', $inv)).Days
        if ($d -le 60) { $s += 2 } elseif ($d -le 180) { $s += 1 }
    } catch { }
    if ($e.s) {
        # Scope is judged against the FILE first, cwd second: a note scoped
        # 'hooks' about a script in ~\.claude\scripts must warn regardless of
        # which repo the session happens to sit in. Basename-only matches with
        # no scope evidence pay a penalty; a matching path segment is already
        # evidence enough, so seg>0 skips the penalty.
        $sc = $e.s.ToLower()
        $scNorm = $sc -replace '[^a-z0-9]', ''
        $flSlash = $fpn.Replace('\', '/')
        $isDotClaude = $flSlash.Contains('/.claude/')
        $pos = ($cwdLeaf -and $scNorm -and ($cwdLeaf.Contains($scNorm) -or $scNorm.Contains($cwdLeaf))) -or
               ($sc -and $flSlash.Contains($sc)) -or
               ($isDotClaude -and ($sc -eq 'hooks' -or $sc -eq 'claude-code'))
        if ($pos) { $s += 1 } elseif ($seg -eq 0) { $s -= 2 }
    }
    if ($s -gt $bestScore) { $bestScore = $s; $best = $e }
}

# Two tiers, calibrated 2026-08-28 on 180 historical sessions
# (scratchpad/calibrate.py). ASK interrupts the user, so it is reserved for
# a sharp, fresh, path-corroborated lesson (mean ~2/session historically);
# the 5-6 band goes to the AGENT as additionalContext -- read the note,
# self-correct, no interruption. Below 5 is silence.
$askThr = 7
$ctxThr = 5
if ($env:BRAINX_WARN_ASK -match '^\d+$') { $askThr = [int]$env:BRAINX_WARN_ASK }
if ($env:BRAINX_WARN_CTX -match '^\d+$') { $ctxThr = [int]$env:BRAINX_WARN_CTX }
if ($bestScore -lt $ctxThr) { Quit }

Add-Content -Path $marker -Value $base
# Opportunistic prune of markers from long-dead sessions.
Get-ChildItem $cacheDir -Filter 'brainx-warned-*.txt' |
    Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } |
    Remove-Item -Force -ErrorAction SilentlyContinue

$second = @($entries | Where-Object { $_.id -ne $best.id }) | Select-Object -First 1
$also = if ($second) { " (also: brain_get_note $($second.id))" } else { '' }

if ($bestScore -ge $askThr) {
    $reason = "BrainX: '$base' has a recorded lesson [$($best.t)] -- $($best.n) ($($best.m)). Read first: brain_get_note $($best.id)$also"
    @{
        hookSpecificOutput = @{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'ask'
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 5 -Compress
} else {
    $ctx = "BrainX warn: you are about to edit '$base', which carries a recorded [$($best.t)] lesson -- '$($best.n)' ($($best.m)). Call brain_get_note $($best.id) BEFORE editing if you have not read it this session$also."
    @{
        hookSpecificOutput = @{
            hookEventName     = 'PreToolUse'
            additionalContext = $ctx
        }
    } | ConvertTo-Json -Depth 5 -Compress
}
