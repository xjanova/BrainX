# Stop hook: inject brain-end reminder ONLY when turn was substantive.
# A turn is "substantive" if it included an Edit/Write/MultiEdit/NotebookEdit/Task call
# since the previous Stop. Trivial Q&A turns get NO reminder (saves ~85t/turn).

# -NoMarker: run the hook diagnostically WITHOUT moving .last-stop-marker.
# Running this hook by hand otherwise advances the very state that defines
# "this turn", so the next real Stop sees an empty window and reports no
# brain write when there was one. That cost a diagnosis on 2026-08-28 and is
# the same self-contamination class as writing test rows into tool-log.ndjson
# or leaving probe peers on the agent bus.
param([switch]$NoMarker)

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'SilentlyContinue'
$root = "$env:USERPROFILE\.claude"

$mode = if (Test-Path "$root\brain-mode.txt") {
    (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower()
} else { 'always' }
if ($mode -eq 'off') { exit 0 }

$toolLog = "$root\tool-log.ndjson"
$markerFile = "$root\.last-stop-marker"

# The Stop payload carries transcript_path — the file Claude Code writes
# itself, ahead of any hook. It is the only race-free record of what this turn
# actually did; see the note beside the brain-write check below.
$transcript = $null
try {
    $stdin = [Console]::In.ReadToEnd()
    if ($stdin) { $transcript = ($stdin | ConvertFrom-Json).transcript_path }
} catch { }

# Cutoff = previous Stop timestamp, or 10 min ago as fallback
$cutoff = (Get-Date).AddMinutes(-10)
if (Test-Path $markerFile) {
    try {
        $stamp = (Get-Content $markerFile -Raw).Trim()
        if ($stamp) { $cutoff = Get-Date $stamp }
    } catch { }
}

# Update marker (best-effort). Skipped for a diagnostic run so the next real
# Stop still sees the turn it is supposed to judge.
if (-not $NoMarker) {
    try { (Get-Date).ToString('o') | Set-Content $markerFile -Encoding utf8 } catch { }
}

# Why this hook logs at all: brain-prompt-gate.ps1 writes every decision to
# brain-decisions.ndjson and is therefore answerable ("why did it nudge?").
# This one wrote nothing, so "it said I never saved, but I did" could only be
# investigated by re-deriving its inputs by hand -- and by then the marker had
# moved and the evidence was gone. One line per decision makes it a lookup.
function Write-StopDecision($verdict, $extra) {
    try {
        ([ordered]@{
            ts        = (Get-Date).ToString('o')
            cutoff    = $cutoff.ToString('o')
            verdict   = $verdict
            edits     = $extra.edits
            brainWrite= $extra.brainWrite
            inWindow  = $extra.inWindow
            auditAge  = $extra.auditAge
            probe     = [bool]$NoMarker
        } | ConvertTo-Json -Compress) |
            Add-Content -Path "$root\brain-stop-log.ndjson" -Encoding utf8
    } catch { }
}

# THE TOOL LOG IS SHARED BY EVERY SESSION ON THIS MACHINE.
#
# ~/.claude/tool-log.ndjson is one global file that every concurrent Claude
# Code session appends to, and the logger records no session id — only
# {ts, tool, mode, resp_size, resp_nonascii}. So "edits in the last window"
# silently counts OTHER sessions' edits as this turn's.
#
# Measured 2026-08-28 19:39: this turn ran read-only git and gh commands and
# nothing else, yet the hook reported edits=2 and nudged about unsaved work.
# The two Edit calls belonged to a session working in D--Code-TPIX-ThaiXTrade,
# whose transcript was being written at the same moment. An ssh_run this
# session never issued was in the same window.
#
# The transcript is per-session and race-free (Claude Code writes it before
# any hook runs), so it answers both problems at once and the tool log is no
# longer consulted for what THIS turn did.
$transcriptLines = @()
if ($transcript -and (Test-Path $transcript)) {
    try { $transcriptLines = Get-Content $transcript -Tail 400 -ErrorAction SilentlyContinue } catch { }
}

function Get-TurnToolUses {
    param([string[]]$Lines, [datetime]$Since)
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($line in $Lines) {
        if ($line -notmatch '"tool_use"') { continue }
        $o = $null
        try { $o = $line | ConvertFrom-Json } catch { continue }
        if (-not $o.timestamp) { continue }
        try { if ((Get-Date $o.timestamp) -le $Since) { continue } } catch { continue }
        foreach ($b in @($o.message.content)) {
            if ($b.type -eq 'tool_use' -and $b.name) { $names.Add([string]$b.name) }
        }
    }
    return $names
}

$turnTools = @()
$substantive = $false
try {
    $turnTools = Get-TurnToolUses -Lines $transcriptLines -Since $cutoff
    foreach ($n in $turnTools) {
        if ($n -match '^(Edit|MultiEdit|Write|NotebookEdit|Task)$') { $substantive = $true; break }
    }
    # Fallback ONLY when there is no transcript to read (an older Claude Code,
    # or a payload without transcript_path). Cross-session contamination is
    # better than no nudge at all, but it must never be the primary path.
    if (-not $transcriptLines -and (Test-Path $toolLog)) {
        $recent = Get-Content $toolLog -Tail 500 -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_ | ConvertFrom-Json } catch { $null }
        } | Where-Object { $_ -and $_.ts -and ((Get-Date $_.ts) -gt $cutoff) }
        foreach ($t in $recent) {
            if ($t.tool -match '^(Edit|MultiEdit|Write|NotebookEdit|Task)$') { $substantive = $true; break }
        }
    }
}
catch { }

if (-not $substantive) {
    Write-StopDecision 'quiet-turn' @{ edits = 0; brainWrite = $null; inWindow = @($turnTools).Count; auditAge = $null }
    exit 0
}

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
    # Fallback, and it is the case that actually happens: a session started in a
    # CODE repo (D:\BrainX) is not inside the vault (G:\Obsidian), so the walk-up
    # above finds nothing and $auditAge stays null — which this hook then reports
    # as "the brain has never been audited", every single session, forever.
    # Measured 2026-08-28: last-audit.json existed with brainHealth 0.988 written
    # hours earlier while the nudge insisted no audit had ever run.
    # session-start.ps1 already carries this exact fallback; stop-hook.ps1 was
    # written from the same walk-up and never got it.
    if ($null -eq $auditAge) {
        foreach ($cand in @($env:BRAINX_VAULT, '__BRAINX_VAULT__')) {
            if (-not $cand) { continue }
            $candidate = Join-Path $cand ".obsidianx\last-audit.json"
            if (Test-Path $candidate) {
                $audit = Get-Content $candidate -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
                if ($audit -and $audit.scannedAt) {
                    $auditAge = ((Get-Date) - (Get-Date $audit.scannedAt)).TotalDays
                    $brainHealth = $audit.brainHealth
                }
                break
            }
        }
    }
    if ($null -eq $auditAge) {
        # Genuinely never audited — both the walk-up and the vault fallback
        # came up empty.
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
    # Suffix match — same rebrand trap as brain-prompt-gate.ps1 and brain-stats.ps1:
    # the server prefix changes, the tool name identifies the call.
    $brainRx = '__(brain_create_note|brain_append_note|brain_remember)$'
    foreach ($n in $turnTools) { if ($n -match $brainRx) { $brainWriteHappened = $true; break } }

    # THE TOOL LOG RACES AND THE TRANSCRIPT DOES NOT.
    #
    # Measured 2026-08-28: the hook logged inWindow=43 / brainWrite=false at
    # 18:51:06 while a brain_append_note had run at 18:50:29. Eight seconds
    # after a later write the line was STILL missing. The cause is structural,
    # not timing luck: tool-log.ndjson is written by a PostToolUse hook, every
    # tool call now spawns up to three PowerShell processes for the three
    # PostToolUse matchers, and Stop fires while that queue is still draining.
    # The turn's LAST tool call is the one most likely to be missing — and it
    # is a brain write more often than anything else, because saving is the
    # last thing a turn does. So the nudge fired "you saved nothing" at the
    # exact moment saving had just happened.
    #
    # A longer sleep would only widen a window that has no correct width. The
    # transcript is written by Claude Code itself, before any hook runs, so it
    # is both authoritative and already complete when Stop reads it.
    # tool-log stays the source for the EDIT count, which is not time-critical
    # in the same way and would cost a second parse of a large file.
    if (-not $brainWriteHappened -and $transcript -and (Test-Path $transcript)) {
        try {
            foreach ($line in (Get-Content $transcript -Tail 400 -ErrorAction SilentlyContinue)) {
                if ($line -notmatch 'brain_create_note|brain_append_note|brain_remember') { continue }
                $o = $null
                try { $o = $line | ConvertFrom-Json } catch { continue }
                if (-not $o.timestamp) { continue }
                try { if ((Get-Date $o.timestamp) -le $cutoff) { continue } } catch { continue }
                foreach ($b in @($o.message.content)) {
                    if ($b.type -eq 'tool_use' -and
                        $b.name -match '__(brain_create_note|brain_append_note|brain_remember)$') {
                        $brainWriteHappened = $true; break
                    }
                }
                if ($brainWriteHappened) { break }
            }
        } catch { }
    }
} catch { }

# Count how many file-modifying calls happened so we can scale the nudge.
# From the TRANSCRIPT, for the same reason as everything above: the shared
# tool log would count another session's edits as this turn's, and did.
$editCount = 0
try {
    foreach ($n in $turnTools) {
        if ($n -match '^(Edit|MultiEdit|Write|NotebookEdit)$') { $editCount++ }
    }
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

$verdict = if ($brainWriteHappened) { 'saved' } else { 'no-save' }
Write-StopDecision $verdict @{
    edits = $editCount; brainWrite = $brainWriteHappened
    inWindow = @($turnTools).Count; auditAge = $auditAge
}

@{
    hookSpecificOutput = @{
        hookEventName     = 'Stop'
        additionalContext = $msg
    }
} | ConvertTo-Json -Depth 5 -Compress
