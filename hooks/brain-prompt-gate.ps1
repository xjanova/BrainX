# UserPromptSubmit gate for brain-first protocol
# Decides whether to inject brain-first protocol message based on prompt characteristics
# Logs all decisions to brain-decisions.ndjson for measurement

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'SilentlyContinue'
$root = "$env:USERPROFILE\.claude"
$decisionLog = "$root\brain-decisions.ndjson"
$searchLog = "$root\brain-search-log.ndjson"
$aliasFile = "$root\scripts\brain-aliases.json"

function Write-Decision($action, $reason, $promptLen) {
    $entry = @{
        ts         = (Get-Date).ToString('o')
        action     = $action
        reason     = $reason
        prompt_len = $promptLen
    } | ConvertTo-Json -Compress
    Add-Content -Path $decisionLog -Value $entry -Encoding utf8
}

# Mode check
$mode = if (Test-Path "$root\brain-mode.txt") {
    (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower()
} else { 'always' }

if ($mode -eq 'off') {
    Write-Decision 'skipped' 'mode-off' 0
    exit 0
}

# Read prompt from stdin
try {
    $payload = ([Console]::In.ReadToEnd() | ConvertFrom-Json)
    $p = $payload.prompt
    if (-not $p) { $p = $payload.user_prompt }
}
catch {
    Write-Decision 'skipped' 'parse-error' 0
    exit 0
}

if (-not $p) {
    Write-Decision 'skipped' 'empty-prompt' 0
    exit 0
}

$len = $p.Length

# === Skip conditions (cheap to evaluate first) ===

# 1. auto mode: skip short prompts (raised 60 -> 80 after net-cost analysis)
if ($mode -eq 'auto' -and $len -lt 80) {
    Write-Decision 'skipped' 'auto-short' $len
    exit 0
}

# 2. very long prompts have their own context
if ($len -gt 1500) {
    Write-Decision 'skipped' 'long-prompt' $len
    exit 0
}

# 3. prompts with code blocks have context user pasted
if ($p -match '```') {
    Write-Decision 'skipped' 'has-code-block' $len
    exit 0
}

# 4. prompts with explicit file paths -- user already gave us location to look
if ($p -match '[A-Za-z]:\\[\w\.\-\\]+' -or $p -match '(^|[\s\(])(/[\w\.\-/]+|\.\\?[\w\.\-/]+)\.(cs|ps1|ts|tsx|js|jsx|py|md|json|yml|yaml|xaml|sql|go|rs|rb|php|java|kt)\b') {
    Write-Decision 'skipped' 'has-file-path' $len
    exit 0
}

# 5. continuation/follow-up words at start of SHORT prompts only
# (100-char ceiling so "ลองดูใหม่ ตอนนี้ build แล้ว crash ที่ startup" still fires)
$startTrim = $p.TrimStart()
$continuations = @(
    'ลองสิ', 'ลองดู', 'ทำต่อ', 'ทำเลย', 'ดีแล้ว', 'ใช่เลย', 'ใช่แล้ว',
    'ไม่ใช่', 'ไม่', 'งั้น', 'เออ', 'อืม', 'แก้', 'ลุย', 'ต่อ',
    'OK', 'ok', 'Ok', 'yes', 'Yes', 'no', 'No', 'next', 'Next',
    'continue', 'Continue', 'go', 'Go', 'do it', 'Do it'
)
foreach ($c in $continuations) {
    if ($startTrim.StartsWith($c) -and $len -lt 100) {
        Write-Decision 'skipped' "continuation:$c" $len
        exit 0
    }
}

# === Build protocol message ===
$protocolParts = @(
    'Brain-first protocol: before responding to non-trivial prompts, run ONE brain_search with 2-4 keywords. If 0 hits, retry with brain_semantic_search OR read .obsidianx/brain-export.json directly. brain_get_note ONLY when preview is insufficient (notes can be 5k-20k tokens). Skip search entirely for: trivial Q, generic coding/framework knowledge, prompts with explicit file paths or code blocks. Cite note titles you actually read.'
)

# Inject recent searches (last 60 min) to prevent duplicate queries
if (Test-Path $searchLog) {
    try {
        $cutoff = (Get-Date).AddMinutes(-60)
        $recent = Get-Content $searchLog -Tail 50 -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } | Where-Object {
            $_ -and $_.query -and ((Get-Date $_.ts) -gt $cutoff)
        }
        if ($recent -and $recent.Count -gt 0) {
            # Split hit vs no-hit so Claude knows which to retry with new keywords
            $hitQ = @($recent | Where-Object { $_.hits -gt 0 } | Select-Object -ExpandProperty query -Unique | Select-Object -Last 8)
            $missQ = @($recent | Where-Object { $_.hits -eq 0 } | Select-Object -ExpandProperty query -Unique | Select-Object -Last 6)
            $bits = @()
            if ($hitQ.Count -gt 0) { $bits += "HIT (results in context, do NOT repeat verbatim): " + ($hitQ -join ' | ') }
            if ($missQ.Count -gt 0) { $bits += "NO-HIT (try different keywords or brain_semantic_search): " + ($missQ -join ' | ') }
            if ($bits.Count -gt 0) {
                $protocolParts += "RECENT SEARCHES (last 60min) -- " + ($bits -join '  ||  ')
            }
        }
    }
    catch { }
}

# Inject alias hints if prompt mentions a known variant
if (Test-Path $aliasFile) {
    try {
        $aliases = Get-Content $aliasFile -Raw -Encoding utf8 | ConvertFrom-Json
        $hints = @()
        foreach ($prop in $aliases.PSObject.Properties) {
            $canonical = $prop.Name
            $variants = $prop.Value
            foreach ($v in $variants) {
                if ($p -match [regex]::Escape($v)) {
                    $hints += "'$v' -> canonical brain term: '$canonical'"
                    break
                }
            }
        }
        if ($hints.Count -gt 0) {
            $protocolParts += "ALIAS HINTS -- use canonical name in brain_search: " + ($hints -join '; ')
        }
    }
    catch { }
}

# === Did the previous turn end without a brain search?  ===
# If so, escalate the protocol message — the user is paying tokens for the
# protocol injection AND not getting brain value out of it.
$toolLog = "$root\tool-log.ndjson"
if (Test-Path $toolLog) {
    try {
        $markerFile = "$root\.last-stop-marker"
        $cutoff = (Get-Date).AddMinutes(-15)
        if (Test-Path $markerFile) {
            try {
                $stamp = (Get-Content $markerFile -Raw).Trim()
                if ($stamp) { $cutoff = Get-Date $stamp }
            } catch { }
        }
        $prevTurn = Get-Content $toolLog -Tail 200 -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } | Where-Object { $_ -and $_.ts -and ((Get-Date $_.ts) -gt $cutoff) }

        $hadEdit = ($prevTurn | Where-Object { $_.tool -match '^(Edit|MultiEdit|Write|NotebookEdit)$' }).Count -gt 0
        $hadBrainRead = ($prevTurn | Where-Object {
            # Match by tool-name SUFFIX. The server prefix changes on a rebrand
            # (obsidianx-brain -> brainx-brain, 2026-05-25); the tool name does not.
            # Anchored to the old prefix this matched NOTHING for ~3 months, so the
            # stronger nudge fired on every editing turn regardless of what was read.
            $_.tool -match '__(brain_search|brain_semantic_search|brain_get_note|brain_synthesize|brain_get_backlinks)$'
        }).Count -gt 0

        if ($hadEdit -and -not $hadBrainRead) {
            $protocolParts = ,(
                "STRONGER NUDGE - the previous turn made code changes WITHOUT consulting the brain first. " +
                "This vault has 600+ notes documenting past decisions and bugs. " +
                "BEFORE answering this prompt, run brain_search (or brain_semantic_search if 0 hits) " +
                "and CITE the note titles you read in your reply. " +
                "Memory rule: feedback_consult_brain_proactively.md"
            ) + $protocolParts
        }
    }
    catch { }
}

$ctx = $protocolParts -join "`n"

Write-Decision 'fired' 'normal' $len

@{
    hookSpecificOutput = @{
        hookEventName     = 'UserPromptSubmit'
        additionalContext = $ctx
    }
} | ConvertTo-Json -Depth 5 -Compress
