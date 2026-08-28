# Stop hook: inject brain-end reminder ONLY when turn was substantive.
# A turn is "substantive" if it included an Edit/Write/MultiEdit/NotebookEdit/Task call
# since the previous Stop. Trivial Q&A turns get NO reminder (saves ~85t/turn).

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'SilentlyContinue'
$root = "$env:USERPROFILE\.claude"

$mode = if (Test-Path "$root\brain-mode.txt") {
    (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower()
} else { 'always' }
if ($mode -eq 'off') { exit 0 }

$toolLog = "$root\tool-log.ndjson"
$markerFile = "$root\.last-stop-marker"

# Cutoff = previous Stop timestamp, or 10 min ago as fallback
$cutoff = (Get-Date).AddMinutes(-10)
if (Test-Path $markerFile) {
    try {
        $stamp = (Get-Content $markerFile -Raw).Trim()
        if ($stamp) { $cutoff = Get-Date $stamp }
    } catch { }
}

# Update marker (best-effort)
try { (Get-Date).ToString('o') | Set-Content $markerFile -Encoding utf8 } catch { }

if (-not (Test-Path $toolLog)) { exit 0 }

$substantive = $false
try {
    $recent = Get-Content $toolLog -Tail 500 -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { $null }
    } | Where-Object {
        $_ -and $_.ts -and ((Get-Date $_.ts) -gt $cutoff)
    }
    foreach ($t in $recent) {
        if ($t.tool -match '^(Edit|MultiEdit|Write|NotebookEdit|Task)$') {
            $substantive = $true
            break
        }
    }
}
catch { }

if (-not $substantive) { exit 0 }

# ── Brain audit due check ────────────────────────────────────────
# Find any vault under .obsidianx/last-audit.json from the cwd up the tree
# (so this works regardless of which Claude Code project is open).
$auditDue = $false
$auditAge = $null
$brainHealth = $null
try {
    $cwd = (Get-Location).Path
    $probe = $cwd
    while ($probe -and (Test-Path $probe)) {
        $candidate = Join-Path $probe ".obsidianx\last-audit.json"
        if (Test-Path $candidate) {
            $audit = Get-Content $candidate -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
            if ($audit -and $audit.scannedAt) {
                $auditAge = ((Get-Date) - (Get-Date $audit.scannedAt)).TotalDays
                $brainHealth = $audit.brainHealth
            }
            break
        }
        $parent = Split-Path $probe -Parent
        if ($parent -eq $probe) { break }
        $probe = $parent
    }
    if ($null -eq $auditAge) {
        # Never audited — definitely due.
        $auditDue = $true
    }
    elseif ($auditAge -gt 7) {
        $auditDue = $true
    }
    elseif ($null -ne $brainHealth -and $brainHealth -lt 0.6) {
        # Recent audit but brain is unhealthy — nudge to fix.
        $auditDue = $true
    }
}
catch { }

# Did Claude actually save anything to the brain since the last Stop?
# Look for create/append/remember calls in the same window.
$brainWriteHappened = $false
try {
    $brainWriteHappened = ($recent | Where-Object {
        # Suffix match — same rebrand trap as brain-prompt-gate.ps1 and brain-stats.ps1:
        # the server prefix changes, the tool name identifies the call.
        $_.tool -match '__(brain_create_note|brain_append_note|brain_remember)$'
    }).Count -gt 0
} catch { }

# Count how many file-modifying calls happened so we can scale the nudge.
$editCount = 0
try {
    $editCount = ($recent | Where-Object {
        $_.tool -match '^(Edit|MultiEdit|Write|NotebookEdit)$'
    }).Count
} catch { }

# Compose context: gentle reminder if a save happened, firm nudge if not.
$msg =
if (-not $brainWriteHappened -and $editCount -ge 3) {
    # 3+ edits without a single brain write = high probability of unsaved insight.
    "Brain-end (stronger nudge): this turn made $editCount file changes and ZERO writes to the ObsidianX brain. " +
    "If there is ANY non-trivial insight from this work (non-obvious root cause, design decision, architecture lesson, reusable pattern, gotcha) — " +
    "save it NOW via brain_create_note or brain_remember BEFORE this session ends. The next Claude relies on these notes. " +
    "If this is the wrap of a substantive sitting, also write a #session-handoff note in Notes/Claude-Sessions/ with branch / files / what shipped / pending / gotchas."
}
elseif (-not $brainWriteHappened) {
    # 1-2 edits, no save: could be trivial. Soft reminder.
    "Brain-end: this turn modified files but did not save to the ObsidianX brain. If a non-trivial insight emerged, save via brain_create_note or brain_remember."
}
else {
    # Save already happened. Just remind about the handoff at session end.
    "Brain-end: this turn already wrote to the brain ✓. If this is the wrap of the sitting and not just a save mid-flow, also write a #session-handoff note titled 'Session YYYY-MM-DD - <topic>' in Notes/Claude-Sessions/."
}

# Append audit-due nudge if applicable. Phrase scales with severity.
if ($auditDue) {
    $auditMsg =
    if ($null -eq $auditAge) {
        " The brain has never been audited — run brain_audit before the next major decision so we know its health."
    }
    elseif ($null -ne $brainHealth -and $brainHealth -lt 0.6) {
        " brain_audit shows brainHealth=$([math]::Round($brainHealth,2)) (below 0.6). Run brain_apply_audit_fix on the highest-severity actions in .obsidianx/last-audit.json."
    }
    else {
        " Last brain_audit was $([math]::Floor($auditAge)) days ago. Re-run brain_audit (cheap, ~2s) before relying on stale health stats."
    }
    $msg += $auditMsg
}

@{
    hookSpecificOutput = @{
        hookEventName     = 'Stop'
        additionalContext = $msg
    }
} | ConvertTo-Json -Depth 5 -Compress
