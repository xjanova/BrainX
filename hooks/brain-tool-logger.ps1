# PostToolUse logger for tool calls
# Maintains tool-log.ndjson AND captures brain_search queries to brain-search-log.ndjson
# Tracks response size (chars) as a proxy for token cost.

$ErrorActionPreference = 'SilentlyContinue'
$root = "$env:USERPROFILE\.claude"
$toolLog = "$root\tool-log.ndjson"
$searchLog = "$root\brain-search-log.ndjson"

# Unwrap whatever Claude Code passes as tool_response into the inner JSON body.
# Three observed shapes — handle all three:
#   1. ARRAY [{type:"text", text:"<json>"}]      ← what MCP tools actually deliver in the
#                                                  PostToolUse payload (verified
#                                                  via debug dump 2026-05-07)
#   2. OBJECT {content:[{text:"<json>"}]}        ← official MCP envelope, kept as a
#                                                  fallback in case Claude Code changes
#                                                  the contract
#   3. STRING "<json>"                           ← rarely, when something already
#                                                  serialised the tool_response
function Resolve-Inner($resp) {
    if ($null -eq $resp) { return $null }
    if ($resp -is [string]) {
        try { return ($resp | ConvertFrom-Json) } catch { return $null }
    }
    # Shape 1: array directly. tool_response IS the content array.
    if ($resp -is [array] -and $resp.Count -gt 0 -and $resp[0].text) {
        try { return ($resp[0].text | ConvertFrom-Json) } catch { return $null }
    }
    # Shape 2: object with .content array.
    $contentProp = $resp.PSObject.Properties['content']
    if ($contentProp -and $contentProp.Value) {
        $c = $contentProp.Value
        if ($c -is [array] -and $c.Count -gt 0 -and $c[0].text) {
            try { return ($c[0].text | ConvertFrom-Json) } catch { return $null }
        }
    }
    return $resp
}

try {
    $payload = ([Console]::In.ReadToEnd() | ConvertFrom-Json)
    $tool = $payload.tool_name
    if (-not $tool) { exit 0 }

    $mode = if (Test-Path "$root\brain-mode.txt") {
        (Get-Content "$root\brain-mode.txt" -Raw).Trim().ToLower()
    } else { 'always' }

    # Response size in chars, PLUS how many of those chars are non-ASCII.
    #
    # Size alone cannot be turned into tokens. Measured against Claude's own
    # reported output_tokens over 624 messages, the old "chars / 4" rule was
    # 67.6% wrong at the median: Thai costs about 1 token per CHARACTER while
    # English costs about 1 per 4.9, so a chars-only number understates Thai
    # roughly fourfold. Recording the script mix here is what lets the reader
    # convert honestly instead of assuming a language.
    $respSize = 0
    $respNonAscii = 0
    if ($payload.tool_response) {
        try {
            $rawText = if ($payload.tool_response -is [string]) {
                $payload.tool_response
            } else {
                $payload.tool_response | ConvertTo-Json -Depth 10 -Compress
            }
            $respSize = $rawText.Length
            # One .NET pass, not a PowerShell foreach: this hook fires on EVERY
            # tool call and Edit payloads run to ~320k chars, where a per-char
            # PS loop costs hundreds of milliseconds of the user's turn.
            $respNonAscii = $respSize - [regex]::Replace($rawText, '[^\x00-\x7F]', '').Length
        } catch { $respSize = 0; $respNonAscii = 0 }
    }

    $entry = [ordered]@{
        ts            = (Get-Date).ToString('o')
        tool          = $tool
        mode          = $mode
        resp_size     = $respSize
        resp_nonascii = $respNonAscii
    }
    ($entry | ConvertTo-Json -Compress) | Add-Content -Path $toolLog -Encoding utf8

    # Capture brain_search queries with REAL hit count.
    #
    # Matched by SUFFIX, not by the full tool name. This line read
    # 'mcp__obsidianx-brain__brain_search' from before the ObsidianX -> BrainX
    # rename and therefore matched nothing for 67 days: brain-search-log.ndjson
    # simply stopped being written on 2026-05-25, and every session since has
    # opened with "searches 0 (0% hit)" while the real number was dozens.
    # Nothing complained, because the whole script runs under
    # SilentlyContinue + catch {}.
    # The server prefix is the part that changes; the tool name is the part
    # that identifies the call. Match on that.
    if ($tool -match '__brain_search$') {
        $query = $null
        if ($payload.tool_input -and $payload.tool_input.query) {
            $query = $payload.tool_input.query
        }
        if ($query) {
            $hitCount = 0
            $inner = Resolve-Inner $payload.tool_response
            if ($inner) {
                # Explicit property check avoids PowerShell's .Count autobox
                # (any single object has .Count = 1, even when no real `count` field).
                $countProp = $inner.PSObject.Properties['count']
                if ($countProp -and $null -ne $countProp.Value) {
                    $hitCount = [int]$countProp.Value
                }
                elseif ($inner.PSObject.Properties['results']) {
                    $r = $inner.results
                    if ($r -is [array]) { $hitCount = $r.Count }
                }
            }

            $searchEntry = [ordered]@{
                ts    = (Get-Date).ToString('o')
                query = $query
                hits  = $hitCount
                size  = $respSize
            }
            ($searchEntry | ConvertTo-Json -Compress) | Add-Content -Path $searchLog -Encoding utf8
        }
    }
}
catch { }
