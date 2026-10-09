# SessionEnd + PreCompact hook: no session ends (or loses its context to
# compaction) without a trace in the brain.
#
# Why: brain notes were written only when the model remembered rule 2, or when
# the owner said "handoff" or "pak kon". Whatever was said and not yet written
# was gone when the window closed, and on 2026-09-25 to 10-09 the Stop
# reminder that nudges saving was dead too. This hook reads the session's own
# transcript and writes a raw digest note: owner prompts, files changed, brain
# notes written, last replies. It is raw material, not a curated note.
#
# Skips: brain mode off, a session that already wrote a #session-handoff
# note, and trivial sessions (fewer than 2 owner prompts and no file edits).
# One file per session, overwritten, so PreCompact then SessionEnd leaves one
# note, not two. Secrets are redacted before anything is written.
#
# PowerShell 5.1 safe: no ConvertFrom-Json on the whole transcript (it can be
# tens of MB). Lines are pre-filtered with string checks and only the few that
# matter are parsed.

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'SilentlyContinue'
$root = "$env:USERPROFILE\.claude"
$decisionLog = "$root\brain-session-end.ndjson"

function Write-Decision($verdict, $extra) {
    try {
        $o = [ordered]@{ ts = (Get-Date).ToString('o'); verdict = $verdict }
        if ($extra) { foreach ($k in $extra.Keys) { $o[$k] = $extra[$k] } }
        [System.IO.File]::AppendAllText($decisionLog, (($o | ConvertTo-Json -Compress) + [Environment]::NewLine), (New-Object System.Text.UTF8Encoding $false))
    } catch { }
}

$mode = if (Test-Path "$root\brain-mode.txt") { (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower() } else { 'always' }
if ($mode -eq 'off') { exit 0 }

$payload = $null
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false   # payload is UTF-8; PS 5.1 reads stdin in the ANSI codepage (cp874) and mangled every Thai prompt and path
try { $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { }
if (-not $payload) { exit 0 }
$hookEvent = "$($payload.hook_event_name)"
$transcript = "$($payload.transcript_path)"
$sid = "$($payload.session_id)"
$cwd = "$($payload.cwd)"
if (-not $transcript -or -not (Test-Path -LiteralPath $transcript)) { Write-Decision 'no-transcript' @{ event = $hookEvent }; exit 0 }

# Vault: env first, then the path the installer substitutes.
$vault = $null
foreach ($cand in @($env:BRAINX_VAULT, '__BRAINX_VAULT__')) {
    if ($cand -and (Test-Path "$cand\.obsidianx")) { $vault = $cand; break }
}
if (-not $vault) { Write-Decision 'no-vault' @{ event = $hookEvent }; exit 0 }

function Redact([string]$s) {
    if (-not $s) { return $s }
    $s = $s -replace '(?i)\b(sk|pk|rk)-[A-Za-z0-9_\-]{16,}', '$1-***'
    $s = $s -replace '\bgh[pousr]_[A-Za-z0-9]{20,}', 'gh*_***'
    $s = $s -replace '\bAKIA[0-9A-Z]{16}\b', 'AKIA***'
    $s = $s -replace 'eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}', '***jwt***'
    $s = $s -replace '(?i)(bearer\s+)[A-Za-z0-9\-\._~\+/]{16,}=*', '$1***'
    $s = $s -replace '(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|apikey)\b(\s*[:=]\s*)\S+', '$1$2***'
    $s = $s -replace '\b[A-Fa-f0-9]{40,}\b', '***hex***'
    $s = $s -replace '[A-Za-z0-9+/]{60,}={0,2}', '***b64***'
    return $s
}

function Clip([string]$s, [int]$n) {
    if (-not $s) { return '' }
    $s = ($s -replace '\s+', ' ').Trim()
    if ($s.Length -gt $n) { return $s.Substring(0, $n) + ' ...' }
    return $s
}

function JsonUnescape([string]$s) {
    try { return [System.Text.RegularExpressions.Regex]::Unescape($s) } catch { return $s }
}

$prompts = New-Object System.Collections.Generic.List[string]
$files = New-Object 'System.Collections.Generic.SortedSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$brainNotes = New-Object System.Collections.Generic.List[string]
$lastTexts = New-Object System.Collections.Generic.Queue[string]
$handoff = $false
$scratch = 0
$firstTs = $null; $lastTs = $null

$rxEdit = [regex]'"name":"(?:Edit|Write|MultiEdit|NotebookEdit)"'
$rxPath = [regex]'"(?:file_path|notebook_path)":"((?:[^"\\]|\\.)*)"'
$rxBrainWrite = [regex]'"name":"mcp__[^"]*__(brain_create_note|brain_append_note|brain_remember)"'
$rxTitle = [regex]'"title":"((?:[^"\\]|\\.)*)"'
$rxTs = [regex]'"timestamp":"([^"]+)"'

try {
    foreach ($line in [System.IO.File]::ReadLines($transcript)) {
        if ($line.Length -gt 400000) { continue }   # pasted images / huge tool output
        $m = $rxTs.Match($line)
        if ($m.Success) { if (-not $firstTs) { $firstTs = $m.Groups[1].Value }; $lastTs = $m.Groups[1].Value }

        if ($line.Contains('"type":"user"')) {
            # isCompactSummary: the "This session is being continued..." block a
            # compaction injects is the harness's summary, not the owner talking.
            if ($line.Contains('"tool_result"') -or $line.Contains('"isMeta":true') -or $line.Contains('"isSidechain":true') -or $line.Contains('"isCompactSummary":true')) { continue }
            try {
                $o = $line | ConvertFrom-Json
                $c = $o.message.content
                $t = if ($c -is [string]) { $c } else { (@($c | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join ' ') }
                $t = "$t".Trim()
                # Harness wrappers (<command-name>, <system-reminder>, <task-notification> ...) are not the owner talking.
                if ($t -and -not $t.StartsWith('<') -and -not $t.StartsWith('This session is being continued from a previous conversation')) { $prompts.Add($t) }
            } catch { }
            continue
        }

        if ($line.Contains('"type":"assistant"')) {
            if ($line.Contains('"isSidechain":true')) { continue }
            if ($line.Contains('"tool_use"')) {
                if ($rxEdit.IsMatch($line)) {
                    foreach ($pm in $rxPath.Matches($line)) {
                        $fp = JsonUnescape $pm.Groups[1].Value
                        # A session's own scratchpad is working space, not the work.
                        if ($fp -match '[\\/]scratchpad[\\/]') { $scratch++ } else { [void]$files.Add($fp) }
                    }
                }
                $bw = $rxBrainWrite.Match($line)
                if ($bw.Success) {
                    $tm = $rxTitle.Match($line)
                    $label = if ($tm.Success) { JsonUnescape $tm.Groups[1].Value } else { $bw.Groups[1].Value }
                    $brainNotes.Add($label)
                    if ($line -match 'session-handoff' -or $line -match 'Claude-Sessions') { $handoff = $true }
                }
            }
            if ($line.Contains('"type":"text"')) {
                $lastTexts.Enqueue($line)
                while ($lastTexts.Count -gt 2) { [void]$lastTexts.Dequeue() }
            }
        }
    }
} catch { Write-Decision 'read-failed' @{ event = $hookEvent; error = "$($_.Exception.Message)" }; exit 0 }

if ($handoff) { Write-Decision 'handoff-written' @{ event = $hookEvent; sid = $sid }; exit 0 }
if ($prompts.Count -lt 2 -and $files.Count -eq 0) { Write-Decision 'trivial' @{ event = $hookEvent; sid = $sid; prompts = $prompts.Count }; exit 0 }

$replies = @()
foreach ($l in $lastTexts) {
    try {
        $o = $l | ConvertFrom-Json
        $t = (@($o.message.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join ' ')
        if ($t) { $replies += $t }
    } catch { }
}

$leaf = if ($cwd) { [System.IO.Path]::GetFileName($cwd.TrimEnd('\', '/')) } else { 'unknown' }
if (-not $leaf) { $leaf = 'unknown' }
$project = ($leaf -replace '[\\/:*?"<>|#\[\]]', '').Trim()
if (-not $project) { $project = 'unknown' }
$tagProject = ($project.ToLower() -replace '[^a-z0-9]+', '-').Trim('-')
$sid8 = if ($sid.Length -ge 8) { $sid.Substring(0, 8) } else { $sid }
$day = (Get-Date).ToString('yyyy-MM-dd')

$dir = Join-Path $vault 'Notes\Claude-Sessions\auto'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
# Keyed by session id so a later event overwrites the same note.
$existing = Get-ChildItem -LiteralPath $dir -Filter "* $sid8.md" -File | Select-Object -First 1
$path = if ($existing) { $existing.FullName } else { Join-Path $dir "$day $project auto-digest $sid8.md" }

$nl = "`n"
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append("---$nl")
[void]$sb.Append("created: $((Get-Date).ToUniversalTime().ToString('o'))$nl")
[void]$sb.Append("source: session-end-hook$nl")
[void]$sb.Append("event: $hookEvent$nl")
[void]$sb.Append("session_id: $sid$nl")
[void]$sb.Append("tags:$nl  - session-auto-digest$nl  - auto-digest$nl")
if ($tagProject) { [void]$sb.Append("  - $tagProject$nl") }
[void]$sb.Append("---$nl$nl")
[void]$sb.Append("# Auto digest - $project - $day ($sid8)$nl$nl")
[void]$sb.Append("> Written by the $hookEvent hook because this session ended without a #session-handoff note. Raw material: verify before relying on it. Secrets are redacted.$nl$nl")
[void]$sb.Append("- cwd: ``$cwd```n- span: $firstTs .. $lastTs`n- owner prompts: $($prompts.Count) - files changed: $($files.Count) (+$scratch scratchpad writes) - brain writes: $($brainNotes.Count)$nl$nl")

[void]$sb.Append("## Owner prompts (last 12)$nl")
$start = [Math]::Max(0, $prompts.Count - 12)
for ($i = $start; $i -lt $prompts.Count; $i++) { [void]$sb.Append("$($i + 1). $(Redact (Clip $prompts[$i] 300))$nl") }

if ($files.Count -gt 0) {
    [void]$sb.Append("$nl## Files changed$nl")
    $k = 0
    foreach ($f in $files) { if ($k++ -ge 30) { [void]$sb.Append("- ... and $($files.Count - 30) more$nl"); break }; [void]$sb.Append("- ``$f```n") }
}
if ($brainNotes.Count -gt 0) {
    [void]$sb.Append("$nl## Brain writes this session$nl")
    foreach ($b in ($brainNotes | Select-Object -Unique | Select-Object -First 15)) { [void]$sb.Append("- $(Clip $b 160)$nl") }
}
if ($replies.Count -gt 0) {
    [void]$sb.Append("$nl## Last replies$nl")
    foreach ($r in $replies) { [void]$sb.Append("> $(Redact (Clip $r 900))$nl$nl") }
}

try {
    [System.IO.File]::WriteAllText($path, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
} catch { Write-Decision 'write-failed' @{ event = $hookEvent; sid = $sid; error = "$($_.Exception.Message)" }; exit 0 }

# Best-effort: let a running client index it now rather than at the next scan.
# Not from the test harness (BRAINX_SANDBOX=1): its temp vault must not be
# pushed into the owner's running brain.
if ($env:BRAINX_SANDBOX -ne '1') {
    try {
        $body = @{ path = $path } | ConvertTo-Json
        Invoke-RestMethod -Uri 'http://localhost:5142/api/brain/auto-ingest' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 3 | Out-Null
    } catch { }
}

Write-Decision 'written' @{ event = $hookEvent; sid = $sid; prompts = $prompts.Count; files = $files.Count; path = $path }
exit 0
