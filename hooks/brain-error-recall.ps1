# PostToolUse hook: when a shell command just FAILED with an error the vault
# has seen before, hand the agent the note that recorded the fix.
#
# Fingerprints are compiler/runtime error codes (CS1061, MSB3027, NU1605,
# ECONNREFUSED, 0x8007...) and specific exception type names -- the same
# extraction the push-pack builder ran over every note, so a match means
# "this exact code appears in that note", not a semantic guess. This is the
# strongest form of "never hit the same wall twice": the error text in the
# terminal and in the note are literally identical tokens.
#
# Output channel is additionalContext (context for the agent), NOT a block:
# a failed command already stopped on its own; the job here is to make the
# next attempt informed, not to interrupt anything.
#
# KEEP THIS FILE ASCII-ONLY (PS 5.1 BOM-less UTF-8 literal mis-decode).

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$ErrorActionPreference = 'SilentlyContinue'
function Quit { exit 0 }

try { $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { Quit }
if (-not $payload) { Quit }
if ($payload.tool_name -notmatch '^(Bash|PowerShell)$') { Quit }

$root = "$env:USERPROFILE\.claude"
$mode = if (Test-Path "$root\brain-mode.txt") { (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower() } else { 'always' }
if ($mode -eq 'off') { Quit }

$vault = if ($env:BRAINX_VAULT) { $env:BRAINX_VAULT } else { '__BRAINX_VAULT__' }
$tsv = Join-Path $vault '.obsidianx\push-pack\errors.tsv'
if (-not (Test-Path $tsv)) { Quit }

# MEASURED 2026-08-28: PostToolUse NEVER fires for a command that exited
# non-zero -- the harness skips hooks on errored tools. So everything this
# hook sees is a tool-level SUCCESS, and the only real failures that reach
# it are MASKED ones: pipes that ate the exit code (`build | tail`),
# -ErrorAction SilentlyContinue, `|| true`, scripts that print errors and
# exit 0. Those are common and worth catching -- but it means the gate must
# demand failure GRAMMAR, not error-looking words: source code being
# printed to stdout is full of the word 'Exception' and is not a failure.
$stderr = ''
$stdout = ''
try { $stderr = [string]$payload.tool_response.stderr } catch { }
try { $stdout = [string]$payload.tool_response.stdout } catch { }
$hay = $stdout + "`n" + $stderr
if ($hay.Trim().Length -eq 0) {
    try { $hay = ($payload.tool_response | ConvertTo-Json -Depth 6 -Compress) } catch { }
}
if (-not $hay) { Quit }
if ($hay.Length -gt 8000) { $hay = $hay.Substring($hay.Length - 8000) }

# Failure grammar: the shapes real toolchains print when something broke.
# A fingerprint may only fire when one of these is ALSO present (or the
# fingerprint itself sits on stderr).
$failGrammar = '(?im)^.*(error\s+[A-Z]{2,7}\d{3,5}|Build FAILED|compilation failed|npm ERR!|fatal:\s|Unhandled exception|Traceback \(most recent call last\)|Exception calling |CategoryInfo\s*:|FullyQualifiedErrorId)'
# Fingerprints may come only from where the failure evidence is: the whole
# output when it carries failure grammar, else stderr alone (progress noise
# on stderr next to source code on stdout must not light up the stdout).
$fpSource = ''
if ($hay -match $failGrammar) { $fpSource = $hay }
elseif ($stderr.Trim().Length -gt 0) { $fpSource = $stderr }
if (-not $fpSource) { Quit }
$hay = $fpSource

# Same fingerprint grammar as Program.PushPack.cs -- keep the two in sync.
$tokens = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
foreach ($m in [regex]::Matches($hay, '\b(?:CS|MSB|NU|NETSDK|CA|SA|IDE|TS|RZ|BC|FS|AL)\d{3,5}\b|\bE[A-Z]{4,15}\b|\b0x8[0-9A-Fa-f]{7}\b|\bGHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}\b')) {
    [void]$tokens.Add($m.Value)
}
$genericEx = @('Exception','Error','SystemException','AggregateException','InnerException',
               'TargetInvocationException','UnhandledException','FatalError','StandardError','InternalError')
foreach ($m in [regex]::Matches($hay, '\b[A-Z][A-Za-z0-9_]{2,60}(?:Exception|Error)\b')) {
    if ($genericEx -notcontains $m.Value) { [void]$tokens.Add($m.Value) }
}
if ($tokens.Count -eq 0) { Quit }
$tokens = @($tokens) | Select-Object -First 12

# Session cooldown: the same failing token in a retry loop must not repeat
# the same context injection every attempt.
$sid = if ($payload.session_id) { $payload.session_id } else { 'nosession' }
$cacheDir = "$root\cache"
if (-not (Test-Path $cacheDir)) { New-Item -ItemType Directory -Force $cacheDir | Out-Null }
$marker = Join-Path $cacheDir "brainx-errseen-$sid.txt"
$seen = @()
if (Test-Path $marker) { $seen = @(Get-Content $marker -ErrorAction SilentlyContinue) }

$alts = ($tokens | Where-Object { $seen -notcontains $_.ToLower() } |
         ForEach-Object { [regex]::Escape($_.ToLower()) }) -join '|'
if (-not $alts) { Quit }

$hits = @(Select-String -Path $tsv -Pattern "^(?:$alts)`t" -Encoding UTF8 | Select-Object -First 3)
if ($hits.Count -eq 0) { Quit }

$lines = @()
$citedIds = @()
foreach ($h in $hits) {
    $parts = $h.Line -split "`t", 2
    $tok = $parts[0]
    Add-Content -Path $marker -Value $tok
    try {
        $entries = $parts[1] | ConvertFrom-Json
        $e = @($entries | Where-Object { $citedIds -notcontains $_.id }) | Select-Object -First 1
        if ($e) {
            $citedIds += $e.id
            $lines += "  - ${tok}: seen before in '$($e.n)' ($($e.m)) -> brain_get_note $($e.id)"
        }
    } catch { }
}
if ($lines.Count -eq 0) { Quit }

$ctx = "BrainX error recall -- this failure matches past incidents:`n" + ($lines -join "`n") +
       "`nRead the note(s) before retrying; the fix is usually recorded there."

@{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $ctx
    }
} | ConvertTo-Json -Depth 5 -Compress
