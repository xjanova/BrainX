# Code graph: who calls what, and why

BrainX stores a repository's symbols and typed, directed relationships beside
the vault. `brain_code_build` reads C# with Roslyn and JavaScript, TypeScript,
TSX, Python and PHP with tree-sitter; it refreshes changed files and uses
Graphify's graph model without installing or running Graphify. An
existing [Graphify](https://github.com/Graphify-Labs/graphify) `graph.json` can
still be imported with `brain_code_import`. `CodeIndexer` remains the separate
regex symbol list used by ordinary search.

## Build from source

From a local MCP client:

```json
{"repo_root": "C:/src/myrepo", "repo": "myrepo"}
```

Pass this to `brain_code_build`. C# binds calls, creations, inheritance,
implementations, overrides and member uses with the compiler. The other
languages record AST declarations, calls and imports, then resolve names with
conservative lexical, import and unique-name rules. Those edges say INFERRED or
AMBIGUOUS when the syntax cannot establish a unique target. The command stores
a `brainx-code-graph/v2` graph plus a compressed per-file cache in the vault.
A second call checks file stamps and content hashes. After a C# edit it
rebinds unchanged C# callers against the new compilation, so adding an overload
cannot leave an old call target in the graph. `full: true` ignores the cache.

The report says which files were read or rebound, the language count, relation
and provenance counts, unresolved references, project state and time per phase.

Both build and import scan the note vault for exact, unambiguous code names.
Qualified names such as `A.Run` and unique names written in backticks become
note-to-symbol links. `brain_code_trace` accepts `note_id` (id or vault-relative
path) to start from those symbols and includes `linkedNotes` for symbol seeds.
The Universe's galaxy view keeps existing note stars and wiki-links, and draws
the most relevant symbols as a loose core at the centre, inside every note
galaxy: the most connected file in the middle, each repo a cluster of its own.
The neural-brain theme draws the same symbols in the thalamus. Exact note
mentions form the bridges to them. The info card labels a code node's source as
BrainX or Graphify.

Points on one subject act as one point in both themes. Notes join by a title
that differs only by date or hash, else by the rarest tag they share; symbols
join by file. Members sit beside the group's head and flow into it once the
group is too small on screen to tell them apart, and flow back out on zooming
in (`nodegroups.js`, and the star shader in `scene.js`). Edges keep their real
ends, so a merged group's lines stop a few pixels short of the point.

The **Code search** button on the HUD opens the code dashboard: one card per
stored graph (symbols, edges, commit, build age, whether the checkout has moved
on), how sure the edges are, relation and coverage counts, the most connected
symbols, and search across the whole graph. A symbol's page lists its callers,
callees and the notes that name it, and flies to its star. **Enable code search**
picks a folder and runs `brainx-mcp code-build <folder>`; **Refresh**, **Full
rebuild** and **Stop** act on the build, one at a time. A second folder with the
same name gets its own graph instead of replacing the first. Only Graphify
imports have no build button: rebuilding would swap their source, so refresh
them as below.

## Nothing to install

The builder ships inside `brainx-mcp`, and a new install needs no step to get
graphs. Every agent starts an MCP server in the project it opened in, and the
server leaves a request in `.obsidianx/code-graphs/cache/auto/`. The BrainX app
reads those requests and builds one project at a time through the same build the
dashboard runs, so it shows a progress bar and a **Stop** button.

- A project is a git checkout. A plain folder, a drive, the home folder, anything
  in temp, and a repo holding the vault are left alone.
- A linked worktree counts as its main checkout, so worktrees do not each store a
  copy of the graph.
- A later session refreshes a project when its commit has moved. A build that
  failed waits six hours; a graph deleted by hand comes back.
- **Map projects automatically** on the dashboard turns this off for this machine.

Requests wait while the app is closed. An agent whose server does not start in
the project folder leaves no request; use **Enable code search** there.

A scan skips `tmp` and `temp` anywhere, `output` and `out` at the top of a repo,
and any folder with its own `.git`, on top of `.gitignore` and the usual
dependency and build folders.

## Import an existing Graphify graph

If a `graph.json` already exists, pass it from a local MCP client:

```json
{"path": "C:/graphs/myrepo/graphify-out/graph.json", "repo": "myrepo", "repo_root": "C:/src/myrepo"}
```

`brain_code_import` takes a `graph.json` or a folder holding
`graphify-out/graph.json`. Paths in the graph are relative to the folder
Graphify scanned; `repo_root` defaults to `graphify-out/.graphify_root`, then to
the folder above `graphify-out`. The graph is stored as
`.obsidianx/code-graphs/<repo>.json` (schema `brainx-code-graph/v2`), written
atomically. Import again after re-running Graphify; it replaces the old graph.

The import checks and reports:

- `graph.schema_version`: 1 is read; a newer version is refused; a file with
  none is read by field name with a warning (pre-0.9.97 Graphify).
- `built_at_commit`, recorded as the graph's commit.
- Edges naming a node the file does not contain: dropped, counted, sampled.
- Paths that are absolute or climb out of the scan root: kept without a file.
- A fingerprint (length, write time, SHA-256) of every cited file, and how
  many were written after `graph.json` (stale on arrival).
- Graphify's own limit: it keeps **one edge per pair of nodes**, so a pair that
  both contains and calls shows one relation, and a call site repeated in one
  caller shows one line. A missing edge is not proof that no call exists.

A file that is not node-link JSON is refused without echoing its content.
`brain_code_import` reads files anywhere on the machine, so it refuses to run
on the remote endpoint and in cloud-cache sessions, and `McpRemotePolicy`
hard-blocks it by name.

## Trace

```json
{"symbol": "Program.KeywordScores", "direction": "callers", "relations": "calls"}
{"symbol": "lib.py#process_item", "hops": 2, "max_edges": 40}
{"note_id": "Notes/Call flow.md", "repo": "myrepo", "notes": 0}
{"query": "ทำไม KeywordScores ต้อง preseed IDF", "notes": 3, "scope": "brainx"}
```

**Identity.** A symbol id is `repo:path#name@Lline` (files: `repo:path`;
nodes with no file, such as external modules: `repo:~<graphify id>`). A
native symbol id omits the line number so it survives edits above its
declaration; overloads include their parameter signature when needed. A
reference resolves as an id, then `path#name`, then a qualified name
(`Program.ScoreNode` as C# labels carry it, or `Class.method` through
Graphify's `method`/`contains` edges), then a bare name, case-sensitively;
case-insensitive only when nothing else matched. More than one match returns
`ambiguous` with every candidate and walks nothing.

With `query` instead of `symbol`, only words shaped like code (camelCase,
snake_case, digits, `Class.member`, `path#name`) are tried, and only those that
resolve to exactly one symbol seed the walk. "run" in a sentence is English;
name such symbols with `symbol`.

**Walk.** Breadth-first, one hop at a time, so a budget cut always drops the
far end first. Within a hop: EXTRACTED before INFERRED before AMBIGUOUS, then
confidence, relation, name and location, so the same graph and question return
the same slice. `direction` is `callers` (in), `callees` (out) or `both`;
edges always read source → target (caller → callee, subclass → base,
importer → imported). Limits: `hops` ≤ 3, `max_edges` ≤ 150, `max_tokens`
≤ 6000 (default 1500), and nodes with more than `hub_degree` (50) edges are
reported in `hubsNotExpanded` instead of being walked through. `truncatedBy`
says which limit stopped the walk.

`max_tokens` bounds the printed answer: each row is priced as `tools/call`
prints it (indented JSON, characters / 4) and the envelope is paid first;
`estimatedTokens` is the size of the answer actually returned. Seeds are always
shown, even over budget.

**Evidence.** Every edge carries `relation`, `provenance`, `confidence` (when
Graphify gives one) and `file`/`line` where it is written. Graphify gives start
lines only. `freshness` per file is `unchanged`, `changed`, `missing`,
`unverifiable` (no checkout, or not fingerprinted) or `external`. The graph
block compares `commit` with the checkout's current `head`, read from `.git`
directly (worktrees and packed refs included): `current`, `behind-head` or
`unknown`. When either signal fires, `stale` says so in words.

**Notes.** Up to `notes` (default 3, max 10) related notes, ranked by
`brain_search`'s rule in the caller's `scope`, never broadened: keyword first
(a bare identifier is a lookup), escalating to the shared hybrid ranker when
the best keyword hit holds less than 75% of the query's words. So a Thai
decision note, or one that shares no word with the question, can still answer
a question; `notesMode` says which ranking answered. Without `query`, the note
query is the seeds' names. Notes share the token budget after the graph.

`brain_code_trace` reads the stored graph and the files it cites. It is not in
the remote read scope yet: it stats files outside the vault, and that exposure
is the owner's call.

## Verification

```powershell
dotnet build BrainX.Mcp -c Debug
dotnet build BrainX.Tests -c Debug
& .\BrainX.Tests\bin\Debug\net10.0\BrainX.Tests.exe --only 'code graph:'
node BrainX.Client/wwwroot/universe/code-layer.test.mjs
python -I tools/benchmark-code-graph.py --mcp BrainX.Mcp/bin/Release/net9.0/brainx-mcp.exe `
  --graph C:/graphs/brainx/graphify-out/graph.json --repo-root C:/src/brainx `
  --source-vault C:/vault --output code-graph-benchmark.json
```

The checks cover reading HEAD (loose, packed, worktree, detached, refused
refs), path containment, freshness, the store, traversal direction, relation
filters, hops, hubs, both budgets and determinism, and, through the real MCP
endpoint with a fixture in graphifyy 0.9.99's shape: the import report,
qualified C# labels, ambiguity, staleness after an edit and a new HEAD, budgets
against the printed answer, Thai and semantic-only notes, a strict notes scope,
refused files and schemas, and `brain_search` answering identically before and
after an import.

The benchmark imports a real graph into a copied vault (the MCP child never
opens the source vault) and, per question, reports first-call and warm latency
including MCP and journaling, the answer's size, and a grep baseline for the
same question: lines naming the symbol, and the size of the files they are in.
grep matches names, not resolved calls; the comparison is about context size.

A native smoke build on this checkout (403 C#/JS/Python files, no notes in the
throwaway vault) produced 12,433 symbols and 40,032 edges in 18.3 s. An
unchanged second build read no source files and completed in 1.94 s. This is a
local check of the extractor and cache, not a vault-wide indexing benchmark.

### Measured on 2026-10-31

Release build on the development machine. Graph: graphifyy 0.9.99,
`graphify extract <repo> --code-only` on this repository at `1610fc9`
(614 files; a first full run over 612 files, on an earlier tree, took about
3 minutes by its output timestamps, and the re-run at `1610fc9` re-extracted
only the 7 changed files). Vault: a copy of 2753 notes. 5 warm rounds.

Import: 3.97 s for 14,536 symbols and 36,185 edges (graph.json 18.98 MB,
stored graph 13.80 MB), no missing endpoints, nothing stale on arrival.

| Question | Status | First call | Warm p50 / p95 | Edges | Answer | grep lines (tokens) | Files grep hits |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| callers of `Program.KeywordScores` | ok | 621 ms | 7.1 / 8.2 ms | 3 | 778 tok | 12 (331) | 8 files, 181,820 tok |
| callees of `Program.ScoreNode` | ok | 5.7 ms | 6.1 / 8.7 ms | 4 | 906 tok | 22 (546) | 11 files, 249,652 tok |
| callers of `Program.HybridRankFused`, 2 hops | ok, cut by tokens | 10.3 ms | 6.3 / 12.2 ms | 6 | 1298 tok | 4 (124), one hop only | 2 files, 29,248 tok |
| `Program.BrainLookup`, both ways | ok, cut by tokens | 6.1 ms | 7.4 / 29.1 ms | 7 | 1398 tok | 4 (86) | 3 files, 134,542 tok |
| bare `ScoreNode` | ambiguous, 3 candidates | 8.0 ms | 11.4 / 16.4 ms | 0 | 378 tok | 22 (546) | 11 files, 249,652 tok |
| why `KeywordScores` preseeds IDF, 3 notes | ok | 229 ms | 144 / 181 ms | 3 | 1352 tok | 12 (331) | 8 files, 181,820 tok |

The first trace pays for reading the stored graph and building its lookups;
later traces reuse them. `estimatedTokens` matched the printed answer
(characters / 4) on every row. The first `brain_search` on the copied vault
took 52.7 s before any of this; that is the existing keyword path reading every
note in a fresh process (and the antivirus scanning freshly copied files), and
it is not part of these figures or a cold-start claim.

How to read the comparison: grep's output is smaller than a trace answer, but
it lists every line naming the word — definitions, comments, three different
`ScoreNode` methods — and says nothing about which call resolves where; telling
them apart means opening the files. The trace resolves the callee, says how
(EXTRACTED or INFERRED), follows a second hop in the same call and reports its
own staleness. It also inherits Graphify's limit: `KeywordScores` is called
twice in `HybridRankFused` (lines 450 and 511) and the graph keeps line 450 only.

The notes on the last row were ranked keyword-only (escalation off, for
repeatable runs); the session note that explains the IDF preseed was not in
the top 3. With escalation on, as it ships, a question that keyword covers
poorly is re-ranked by the hybrid ranker; that path is covered by the tests,
not measured here. None of this is a comparison with Graphify's own query
tools or MCP server, which were not run.

## Limits and next steps

- Graphify records start lines only and one edge per node pair (above).
- The syntax-only languages cannot bind dynamically dispatched calls as exactly
  as the C# compiler. Unresolved calls are counted rather than guessed.
- The Universe draws a bounded code neighborhood to keep the scene responsive;
  `brain_code_trace` queries the full stored graph.
- BrainX never runs Graphify itself; if using an imported graph, refresh that
  graph externally and import again when code moves.
  `stale`, `graph.state` and per-file `freshness` say when that is due.
- `brain_code_trace` is local only until the owner decides on remote exposure.
- Each MCP process holds an imported graph in memory after its first trace
  (13.8 MB of JSON for this repository).
