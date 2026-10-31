# Lexical candidates and exact references

`brain_search` and the keyword arm of hybrid retrieval use a conservative
trigram candidate index. `ScoreNode` still assigns every score, and results
retain their original tie order. The semantic arm still searches all vectors
within the caller's scope, including notes with zero lexical overlap.

The index covers titles, tags, categories, previews, full bodies and extracted
headings. Query needles include the full phrase, scored words and the existing
Thai fallback grams. Matches are unioned across needles, not intersected across
the whole query: the scorer intentionally accepts partial word matches.

Trigrams hash into 4096 bitset postings. Hash collisions only admit extra
candidates; they cannot exclude a real substring. Per-document bucket lists
allow changed/deleted documents to be removed without rebuilding the corpus.
Unchanged bodies reuse their cached strings. Short or unsupported-script
queries use the original full scan, and broad candidate sets also skip pruning.
First use pays for index construction. Each subsequent query still checks file
freshness; this optimises body scoring, not all filesystem or MCP overhead.

Set `BRAINX_KEYWORD_INDEX=0` before starting the MCP process to use the previous
full-scan path, for rollback or comparisons. No vector store or external service
is needed for lexical indexing.

## Exact lookup

The new read-only `brain_lookup` tool bypasses query embeddings:

```json
{"reference":"Notes/Deployment guide.md","kind":"path"}
{"reference":"Launch","kind":"symbol","scope":"my-project"}
{"reference":"012345abcdef","kind":"id"}
```

Kinds are `id`, `path`, `title`, `symbol` and `auto` (the default). Auto unions
matches across all kinds. Titles and symbols match case-insensitively, without
fuzzy matching. Paths are relative to the vault and accept Windows separators.
The tool returns `exact`, `ambiguous`, `not-found` or `out-of-scope`. It never
broadens a scope or chooses among duplicate names; resolve ambiguity by path/id.
It returns at most 20 matches, with the total count and a truncation flag.

Supersession, validity and provenance remain attached to results. A resolved
identity is not a claim that the note is current or correct. Use
`brain_get_note` to read the resolved id, or normal search for discovery.

Code indexing now retains the second regex capture branch (methods and arrow
functions as well as declarations). All extracted symbol names are exported in
`Properties.symbols`; tags remain capped at 40 names. Old exports fall back to
their symbol tags until re-indexed. This remains a regex symbol index, not AST
call analysis.

## Verification and timing

```powershell
dotnet build BrainX.Mcp -c Debug
dotnet build BrainX.Tests -c Debug
& .\BrainX.Tests\bin\Debug\net10.0\BrainX.Tests.exe --only 'search index:'
python tools/benchmark-search-index.py --mcp BrainX.Mcp/bin/Debug/net9.0/brainx-mcp.exe --source-vault C:/vault --output benchmark.json
```

The harness compares ids, order and scores against the full scan, exercises
Thai grams, short terms, word boundaries, metadata/body/heading hits, live edits
and deletion, and verifies semantic-only evidence is retained. Exact lookup is
checked through the real MCP endpoint with a fake local embedder, including
ambiguity, strict scope, symbols beyond the tag cap and no embedding calls.

The benchmark copies the snapshot's files into a disposable vault before
starting either process. Both use the same binary and corpus, with keyword
escalation, overlay and result memo disabled. It reports first-query cost and
warm p50/p95 including MCP/journaling; IDF is warmed in both arms. It checks
identical top-10 ids and scores for every sample. This is a speed/equivalence
comparison, not a relevance benchmark, semantic benchmark or Graphify comparison.

First-query timings are order-dependent: the scan process goes first and can
warm filesystem/antivirus caches for the indexed process. They are not evidence
of a cold-start speedup.

On 2026-10-31, a Debug build on the development machine, using a copy of 2748
notes (28,873,329 bytes), 16 queries and 5 measured rounds returned identical
top-10 ids/scores in all 80 pairs. Warm MCP latency was:

| Path | p50 | p95 |
| --- | ---: | ---: |
| Full scan | 59.24 ms | 79.41 ms |
| Trigram candidates | 49.68 ms | 74.68 ms |

This is about 16% lower median latency on that workload, with most remaining
time in freshness checks, MCP and journaling. Do not generalise it to semantic
search, other vault sizes or other hardware. An initial implementation checked
candidate file freshness twice and regressed p95; prepared bodies are now
reused for scoring. Query IDF is seeded in full-scan order before refreshing
postings, so an external edit does not silently change a cached weight.
