# SessionStart hook: inject brain handoffs + brain-first stats
# Called by Claude Code on every session start/resume.

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'SilentlyContinue'
$root = "$env:USERPROFILE\.claude"

# Mode check
$mode = if (Test-Path "$root\brain-mode.txt") {
    (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower()
} else { 'always' }
if ($mode -eq 'off') { exit 0 }

# Walk up from cwd to find a vault with brain-export.json
$d = (Get-Location).Path
$vaultRoot = $null
while ($d) {
    if (Test-Path "$d\.obsidianx\brain-export.json") { $vaultRoot = $d; break }
    $p = Split-Path $d -Parent
    if (-not $p -or $p -eq $d) { break }
    $d = $p
}

# Fallback: sessions started in a CODE repo (D:\BrainX) are not inside the
# vault (G:\Obsidian), so the walk-up above finds nothing and the whole handoff
# injection silently does nothing. Resolve the same way brainx-mcp does.
if (-not $vaultRoot) {
    foreach ($cand in @($env:BRAINX_VAULT, '__BRAINX_VAULT__')) {
        if ($cand -and (Test-Path "$cand\.obsidianx\brain-export.json")) { $vaultRoot = $cand; break }
    }
}

# === Build handoff list (only when in a vault) ===
$handoffSection = $null
if ($vaultRoot) {
    try {
        $exp = Get-Content "$vaultRoot\.obsidianx\brain-export.json" -Raw -Encoding utf8 | ConvertFrom-Json
        $cutoff = (Get-Date).AddDays(-21)
        $recent = @($exp.Nodes |
            Where-Object { $_.Tags -contains 'session-handoff' } |
            Where-Object { try { (Get-Date $_.ModifiedAt) -gt $cutoff } catch { $false } } |
            Sort-Object { Get-Date $_.ModifiedAt } -Descending |
            Select-Object -First 5)

        if ($recent.Count -gt 0) {
            # Find git root for repo annotation
            $gitRoot = $null
            $g = $vaultRoot
            while ($g) {
                if (Test-Path "$g\.git") { $gitRoot = $g; break }
                $pp = Split-Path $g -Parent
                if (-not $pp -or $pp -eq $g) { break }
                $g = $pp
            }
            $repoNote = if ($gitRoot) { ", git repo: $gitRoot" } else { '' }
            $lines = ($recent | ForEach-Object { "  - $($_.Title) (id: $($_.Id), modified $($_.ModifiedAt))" }) -join [Environment]::NewLine
            $handoffSection = "SESSION RESUME: cwd $vaultRoot$repoNote. Recent #session-handoff notes from this brain (last 21d, newest first):$([Environment]::NewLine)$lines$([Environment]::NewLine)$([Environment]::NewLine)Call brain_get_note <id> on the topmost one to pick up where the last session left off. If none of these handoffs match the user's first prompt, fall through to normal brain_search."
        }
    }
    catch { }
}

# === Build playbook section (procedural memory) ===
# The handoff above says what the LAST session left unfinished. This says what
# traps THIS KIND of work has - it is the part a fresh session cannot search for,
# because you have to already know the trap exists to query for it.
$playbookSection = $null
if ($vaultRoot) {
    try {
        $bp = "$vaultRoot\.obsidianx\bundles\playbook.json"
        if (Test-Path $bp) {
            $pb = [System.IO.File]::ReadAllText($bp) | ConvertFrom-Json
            $ageDays = [math]::Round(((Get-Date).ToUniversalTime() - (Get-Date $pb.generatedAt).ToUniversalTime()).TotalDays, 0)
            $items = @($pb.notes | ForEach-Object { "  - $($_.title): $($_.summary)" }) -join [Environment]::NewLine
            if ($items) {
                $staleNote = if ($ageDays -gt 30) { " (bundle is $ageDays days old - call brain_bundle topic=playbook for a re-baked copy)" } else { '' }
                $playbookSection = "PLAYBOOKS (procedural memory - HOW to do things, not what happened)$staleNote$([Environment]::NewLine)$items$([Environment]::NewLine)$([Environment]::NewLine)Before starting work that matches one of these shapes, brain_get_note the matching playbook FIRST - it exists because these traps already cost real time once. After finishing work that taught a reusable lesson, add it to the matching playbook (brain_append_note) or write a new one to Playbooks/ tagged 'playbook'."
            }
        }
    }
    catch { }
}

# === Build repo warm-start pack (push-pack/repos.json, keyed by cwd) ===
# The handoff says what the LAST session left; the playbooks say what this
# KIND of work trips over; this says what THIS REPO trips over — gotchas,
# open handoffs and hot files scoped to the project the session opened in.
# Measured 2026-08-28: a median session burned ~600k weighted tokens
# re-orienting before its first edit. This is that orientation, prepaid.
$repoPackSection = $null
if ($vaultRoot) {
    try {
        $rp = "$vaultRoot\.obsidianx\push-pack\repos.json"
        if (Test-Path $rp) {
            $packs = [System.IO.File]::ReadAllText($rp) | ConvertFrom-Json
            $leaf = ([System.IO.Path]::GetFileName((Get-Location).Path.TrimEnd('\', '/'))).ToLower()
            $leaf = $leaf -replace '^(code|d|e)[-]+', '' -replace '[^a-z0-9]', ''
            $bestKey = $null; $bestNotes = 0
            if ($leaf) {
                foreach ($prop in $packs.PSObject.Properties) {
                    $k = ($prop.Name -replace '[^a-z0-9]', '')
                    if (-not $k) { continue }
                    if ($leaf.Contains($k) -or $k.Contains($leaf)) {
                        if ($prop.Value.notes -gt $bestNotes) { $bestNotes = $prop.Value.notes; $bestKey = $prop.Name }
                    }
                }
            }
            if ($bestKey) {
                $pk = $packs.$bestKey
                $rlines = @()
                foreach ($g in @($pk.gotchas)) { $rlines += "  ! $($g.t) (id: $($g.id), $($g.m))" }
                foreach ($h in @($pk.handoffs)) { $rlines += "  - $($h.t) (id: $($h.id), $($h.m))" }
                $hot = (@($pk.hot) -join ', ')
                if ($hot) { $rlines += "  hot files: $hot" }
                if ($rlines.Count -gt 0) {
                    $repoPackSection = "REPO PACK ($bestKey, $($pk.notes) notes) — known traps (!) and latest work in THIS project. brain_get_note an id before re-deriving any of it:$([Environment]::NewLine)$($rlines -join [Environment]::NewLine)"
                }
            }
        }
    }
    catch { }
}

# === Build stats line (always — even outside a vault) ===
$statsLine = $null
try {
    $statsLine = & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\scripts\brain-stats.ps1" -Days 2 -Compact 2>$null
    if ($statsLine) { $statsLine = $statsLine.ToString().Trim() }
}
catch { }

# === Compose final context ===
$parts = @()
if ($handoffSection) { $parts += $handoffSection }
if ($repoPackSection) { $parts += $repoPackSection }
if ($playbookSection) { $parts += $playbookSection }
if ($statsLine) { $parts += $statsLine }

if ($parts.Count -eq 0) { exit 0 }

$ctx = $parts -join "$([Environment]::NewLine)$([Environment]::NewLine)"

@{
    hookSpecificOutput = @{
        hookEventName     = 'SessionStart'
        additionalContext = $ctx
    }
} | ConvertTo-Json -Depth 5 -Compress
