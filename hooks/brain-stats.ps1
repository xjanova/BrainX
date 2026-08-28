# Measurement script — brain-first cost, hit rates, waste signals.
# Usage: powershell -File ~/.claude/scripts/brain-stats.ps1 [-Days 1]
#
# Chars are converted to tokens with the coefficients fitted in
# BrainX.Core/Services/TokenEstimator.cs, NOT with chars/4. That rule is an
# English-prose approximation and this vault is mostly Thai; measured against
# Claude's own reported output_tokens over 624 all-text messages it was 67.6%
# wrong at the median, always in the direction that made the brain look cheap.

param(
    [int]$Days = 2,
    [switch]$Compact   # one-line summary for SessionStart hook
)

$root = "$env:USERPROFILE\.claude"
$decisionLog = "$root\brain-decisions.ndjson"
$searchLog = "$root\brain-search-log.ndjson"
$toolLog = "$root\tool-log.ndjson"

$cutoff = (Get-Date).AddDays(-$Days)

# Per-fire injected protocol cost (chars in protocol message + recent-search hint)
$ProtocolCharsPerFire = 540   # ~135 tokens (raised after split hit/no-hit hint)
$StopHookCharsPerFire = 480   # ~120 tokens (only fires on substantive turns now)

function Read-Json($path, $cutoff) {
    if (-not (Test-Path $path)) { return @() }
    Get-Content $path -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { $null }
    } | Where-Object { $_ -and $_.ts -and ((Get-Date $_.ts) -gt $cutoff) }
}

$dec = @(Read-Json $decisionLog $cutoff)
$sea = @(Read-Json $searchLog $cutoff)
$tools = @(Read-Json $toolLog $cutoff)

$fired = @($dec | Where-Object { $_.action -eq 'fired' })
$skipped = @($dec | Where-Object { $_.action -eq 'skipped' })

function Sum-Sizes($items, $field) {
    $total = 0
    foreach ($i in $items) {
        $prop = $i.PSObject.Properties[$field]
        if ($prop -and $null -ne $prop.Value) { $total += [int]$prop.Value }
    }
    return $total
}

# Calibrated chars -> tokens. Fitted by ordinary least squares on half the
# ground-truth samples and scored on the other half: median absolute error
# 6.8%, against 67.6% for chars/4 on the same held-out data.
$TokPerAscii    = 0.2056   # ~4.9 chars/token
$TokPerNonAscii = 1.1972   # Thai: ~0.84 chars/token
$TokOverhead    = 58.2     # per-message framing

# Rows logged before resp_nonascii existed have no recorded script mix. There
# is no honest default, so the assumption is named, applied, and counted — the
# summary says how many rows needed it.
$script:AssumedMixRows = 0
$LegacyNonAsciiFraction = 0.45

function ConvertTo-Tokens([int]$chars, [int]$nonAscii, [switch]$NoOverhead) {
    if ($chars -le 0) { return 0 }
    if ($nonAscii -lt 0 -or $nonAscii -gt $chars) {
        $script:AssumedMixRows++
        $nonAscii = [int]($chars * $LegacyNonAsciiFraction)
    }
    $ascii = $chars - $nonAscii
    $t = $ascii * $TokPerAscii + $nonAscii * $TokPerNonAscii
    if (-not $NoOverhead) { $t += $TokOverhead }
    return [int][math]::Round($t)
}

function Sum-Tokens($items) {
    $total = 0
    foreach ($i in $items) {
        $size = 0; $na = -1
        $p = $i.PSObject.Properties['resp_size']
        if ($p -and $null -ne $p.Value) { $size = [int]$p.Value }
        $q = $i.PSObject.Properties['resp_nonascii']
        if ($q -and $null -ne $q.Value) { $na = [int]$q.Value }
        $total += ConvertTo-Tokens $size $na
    }
    return $total
}

# Real cost components (chars -> tokens via /4)
#
# Matched by tool-name SUFFIX. These three lines named the pre-rename server
# ('mcp__obsidianx-brain__...') and so matched nothing at all after 2026-05-25:
# get_note and semantic_search were simply absent from the cost side, which
# made the brain look cheaper than it is. The server prefix is what changes on
# a rebrand; the tool name is what identifies the call.
$protoChars = $fired.Count * $ProtocolCharsPerFire
$protoTokens = $fired.Count * (ConvertTo-Tokens $ProtocolCharsPerFire 0 -NoOverhead)
$noteCalls = @($tools | Where-Object { $_.tool -match '__brain_get_note$' })
$noteChars = Sum-Sizes $noteCalls 'resp_size'
$noteTokens = Sum-Tokens $noteCalls
$semanticCalls = @($tools | Where-Object { $_.tool -match '__brain_semantic_search$' })
$semanticChars = Sum-Sizes $semanticCalls 'resp_size'
$semanticTokens = Sum-Tokens $semanticCalls

# Searches come from tool-log, which is authoritative and alive. The dedicated
# search log only adds query text and hit counts, and it is the file that died.
$searchCalls = @($tools | Where-Object { $_.tool -match '__brain_search$' })
$searchChars = Sum-Sizes $searchCalls 'resp_size'
$searchTokens = Sum-Tokens $searchCalls
$searchCount = $searchCalls.Count + $semanticCalls.Count
# Stop hook fires once per turn (== prompt count). Pre-fix: every turn. Post-fix: only substantive turns.
# Use prompt count as proxy until enough post-fix data accumulates.
$stopFires = $dec.Count
$stopChars = $stopFires * $StopHookCharsPerFire
$stopTokens = $stopFires * (ConvertTo-Tokens $StopHookCharsPerFire 0 -NoOverhead)

$costTokens = $protoTokens + $searchTokens + $noteTokens + $semanticTokens + $stopTokens

# AVOIDED, not "saved". This counts exactly one thing and it is a real
# measurement: prompts where the gate chose not to fire, so the protocol text
# was genuinely never injected. Those characters did not enter the context.
#
# What was removed here: a second term, `skips * 0.5 * 400`, asserting that
# half of the skipped prompts would have run a search costing ~400 tokens.
# Both numbers were invented in 2026-05 and neither was ever measured. It was
# the larger of the two terms, so the headline figure was mostly guess.
#
# What the brain SAVED is still unknown and is not estimated here. Knowing it
# requires sessions that ran with the brain off to compare against; the tool
# log has stamped `mode` on every call for months and every one of them says
# "auto", so no such comparison exists yet.
$avoidedTokens = $skipped.Count * (ConvertTo-Tokens $ProtocolCharsPerFire 0 -NoOverhead)

$net = $avoidedTokens - $costTokens

# Search hit rate. Only the dedicated log carries hit counts, so when it has
# nothing in the window the honest answer is "unknown", NOT 0%. Printing 0%
# beside a real search count is how this went unnoticed for two months: it
# reads as "the brain finds nothing", which is a much more alarming and
# completely different claim than "nobody wrote the number down".
$hits = @($sea | Where-Object { $_.hits -gt 0 }).Count
$misses = @($sea | Where-Object { $_.hits -eq 0 }).Count
$hitKnown = $sea.Count -gt 0
$hitRate = if ($hitKnown) { [math]::Round(($hits / $sea.Count) * 100) } else { $null }
# Show the DENOMINATOR. The hit rate comes from the dedicated log, the search
# count from tool-log, and the two disagree while the dedicated log refills
# after its 67-day outage — "100% hit" beside "56 searches" reads as 56/56 when
# it was 2/2. A rate without its sample size is the same lie in a new costume.
$hitLabel = if ($hitKnown -and $sea.Count -lt $searchCount) { "$hitRate% hit of $($sea.Count) logged" }
            elseif ($hitKnown) { "$hitRate% hit" }
            elseif ($searchCount -gt 0) { 'hit-rate unlogged' }
            else { 'no searches' }

if ($Compact) {
    if (($fired.Count + $skipped.Count + $searchCount) -eq 0) {
        Write-Output "BRAIN STATS (last ${Days}d): no activity yet"
    }
    else {
        $perSearch = if ($searchCalls.Count -gt 0) { [int]($searchTokens / $searchCalls.Count) } else { 0 }
        # No NET SAVE / NET COST verdict any more. That label compared a fully
        # measured cost against a deliberately narrow "avoided" figure and
        # printed the difference as a judgement on the brain — which it never
        # was. Report both measured quantities and say plainly that the third
        # one is not known.
        $mixNote = if ($script:AssumedMixRows -gt 0) { " (~$($script:AssumedMixRows) rows assumed mix)" } else { '' }
        Write-Output "BRAIN STATS (${Days}d): $($fired.Count + $skipped.Count) prompts ($($fired.Count) fired, $($skipped.Count) skipped) | searches $searchCount ($hitLabel, ~${perSearch}t each) | brain cost ~${costTokens}t measured${mixNote} | protocol avoided ~${avoidedTokens}t | savings UNMEASURED - no brain-off runs to compare"
    }
    exit 0
}

Write-Host ""
Write-Host "=== Brain-first stats (last $Days day(s)) ===" -ForegroundColor Cyan
Write-Host ""

Write-Host "PROMPTS:" -ForegroundColor Yellow
Write-Host "  Total seen:            $($dec.Count)"
Write-Host "  Brain-first fired:     $($fired.Count)" -ForegroundColor Green
Write-Host "  Skipped (gate):        $($skipped.Count)" -ForegroundColor DarkGray
if ($skipped.Count -gt 0) {
    Write-Host "  Skip reasons:" -ForegroundColor DarkGray
    $skipped | Group-Object reason | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("    {0,-25} {1}" -f $_.Name, $_.Count)
    }
}
Write-Host ""

Write-Host "BRAIN SEARCHES:" -ForegroundColor Yellow
Write-Host "  brain_search calls:    $($searchCalls.Count)  (from tool-log)"
Write-Host "  semantic_search calls: $($semanticCalls.Count)"
if ($hitKnown) {
    Write-Host "  With hits:             $hits ($hitRate%)" -ForegroundColor Green
    Write-Host "  Zero hits (waste):     $misses" -ForegroundColor Red
}
else {
    Write-Host "  Hit rate:              UNKNOWN - no entries in brain-search-log.ndjson for this window" -ForegroundColor Yellow
    if (Test-Path $searchLog) {
        Write-Host ("                         (log last written {0})" -f (Get-Item $searchLog).LastWriteTime) -ForegroundColor DarkGray
    }
}
$avgSize = if ($searchCalls.Count -gt 0) { [math]::Round(($searchChars / $searchCalls.Count)) } else { 0 }
$avgTok = if ($searchCalls.Count -gt 0) { [int]($searchTokens / $searchCalls.Count) } else { 0 }
Write-Host "  Avg response size:     $avgSize chars (~${avgTok}t calibrated)"

$dupes = $sea | Group-Object query | Where-Object { $_.Count -gt 1 } | Sort-Object Count -Descending | Select-Object -First 5
if ($dupes) {
    Write-Host ""
    Write-Host "  Top duplicate queries:" -ForegroundColor Yellow
    $dupes | ForEach-Object {
        Write-Host ("    {0}x '{1}'" -f $_.Count, $_.Name)
    }
}

$recentZero = @($sea | Where-Object { $_.hits -eq 0 } | Select-Object -Last 10)
if ($recentZero.Count -gt 0) {
    Write-Host ""
    Write-Host "  Recent zero-hit queries (consider aliases or semantic_search):" -ForegroundColor Yellow
    $recentZero | ForEach-Object { Write-Host "    '$($_.query)'" }
}
Write-Host ""

Write-Host "TOKEN COST (measured; calibrated chars->tokens):" -ForegroundColor Yellow
Write-Host ("  Protocol injects:    {0,8} t  ({1} fires x {2} chars)" -f $protoTokens, $fired.Count, $ProtocolCharsPerFire)
Write-Host ("  Stop hook injects:   {0,8} t  ({1} turns x {2} chars; pre-fix fired every turn)" -f $stopTokens, $stopFires, $StopHookCharsPerFire)
Write-Host ("  brain_get_note hits: {0,8}    (calls with measurable size)" -f $noteCalls.Count) -ForegroundColor DarkGray
Write-Host ("  brain_search bodies: {0,8} t  ({1} chars total)" -f $searchTokens, $searchChars)
Write-Host ("  brain_get_note:      {0,8} t  ({1} chars total)" -f $noteTokens, $noteChars)
Write-Host ("  brain_semantic:      {0,8} t  ({1} chars total)" -f $semanticTokens, $semanticChars)
Write-Host ("  TOTAL COST:          {0,8} t" -f $costTokens) -ForegroundColor Red
if ($script:AssumedMixRows -gt 0) {
    Write-Host ("  note: {0} row(s) predate resp_nonascii logging - script mix assumed, not measured" -f $script:AssumedMixRows) -ForegroundColor DarkGray
}
Write-Host ""

Write-Host "TOKEN AVOIDED (measured):" -ForegroundColor Yellow
Write-Host ("  Protocol not injected:{0,7} t  ({1} gate skips x {2} t)" -f $avoidedTokens, $skipped.Count, (ConvertTo-Tokens $ProtocolCharsPerFire 0 -NoOverhead))
Write-Host ""

Write-Host "TOKEN SAVED: not measured." -ForegroundColor Yellow
Write-Host "  What the brain replaced cannot be read off a log of what it did." -ForegroundColor DarkGray
Write-Host "  It needs sessions run with brain-mode off to compare against." -ForegroundColor DarkGray
$modes = @($tools | ForEach-Object { $_.mode } | Where-Object { $_ } | Sort-Object -Unique)
Write-Host ("  Modes seen in this window: {0}" -f (($modes -join ', '))) -ForegroundColor DarkGray
if ($modes.Count -lt 2) {
    Write-Host "  Only one mode recorded - there is nothing to compare yet." -ForegroundColor DarkGray
}
Write-Host ""
Write-Host ("BALANCE (measured only): cost {0} t, avoided {1} t, difference {2} t" -f $costTokens, $avoidedTokens, $net) -ForegroundColor Cyan
Write-Host "  This is NOT a verdict on the brain - it compares a full cost" -ForegroundColor DarkGray
Write-Host "  against one narrow avoided item, and omits the unmeasured side." -ForegroundColor DarkGray
Write-Host ""
