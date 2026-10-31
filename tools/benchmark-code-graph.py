"""Import a real Graphify graph.json into a disposable vault and time brain_code_trace.

No packages needed. The MCP child never opens the source vault: notes are copied
into a temporary vault first. For each question it reports first-call and warm
latency (MCP round trip included) and the size of the answer, beside a grep
baseline for the same question: the lines naming the symbol, and the files an
agent would then open to read them. grep matches names, not resolved calls, so
the comparison is about context size, not about which answer is more correct.
Notes are ranked keyword-only (escalation off) so runs are repeatable.
"""
import argparse
import json
import math
import os
from pathlib import Path
import queue
import re
import shutil
import statistics
import subprocess
import tempfile
import threading
import time

QUESTIONS = [
    {"name": "callers of KeywordScores", "grep": "KeywordScores",
     "args": {"symbol": "Program.KeywordScores", "direction": "callers", "relations": "calls", "notes": 0}},
    {"name": "callees of ScoreNode", "grep": "ScoreNode",
     "args": {"symbol": "Program.ScoreNode", "direction": "callees", "relations": "calls", "notes": 0}},
    {"name": "callers of HybridRankFused, 2 hops", "grep": "HybridRankFused",
     "args": {"symbol": "Program.HybridRankFused", "direction": "callers", "relations": "calls", "hops": 2, "notes": 0}},
    {"name": "BrainLookup neighbourhood", "grep": "BrainLookup",
     "args": {"symbol": "Program.BrainLookup", "notes": 0}},
    {"name": "ambiguous bare ScoreNode", "grep": "ScoreNode",
     "args": {"symbol": "ScoreNode", "notes": 0}},
    {"name": "why KeywordScores preseeds IDF, with notes", "grep": "KeywordScores",
     "args": {"query": "why does Program.KeywordScores preseed IDF before refreshing postings", "notes": 3,
              "direction": "callers", "relations": "calls"}},
]
CODE = {".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs"}
SKIP = {"bin", "obj", "node_modules", ".git", "graphify-out", "vendor"}


class Brain:
    def __init__(self, exe, vault):
        env = dict(os.environ, BRAINX_VAULT=str(vault), BRAINX_SANDBOX="1",
                   BRAINX_MCP_LAUNCHER_CHILD="1", BRAINX_CANARY="0",
                   BRAINX_SEARCH_ESCALATE="0", BRAINX_DISABLE_MEMO="1", BRAINX_OVERLAY="0")
        self.p = subprocess.Popen([str(exe), "--serve"], env=env, stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                  encoding="utf-8", creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.inbox = queue.Queue()
        self.seq = 0

        def read():
            for line in self.p.stdout:
                try:
                    self.inbox.put(json.loads(line))
                except json.JSONDecodeError:
                    pass
            self.inbox.put(None)

        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()
        self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                "clientInfo": {"name": "code-graph-benchmark", "version": "1"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.p.stdin.flush()

    def call(self, method, params):
        self.seq += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.seq, "method": method, "params": params}, ensure_ascii=False) + "\n")
        self.p.stdin.flush()
        deadline = time.monotonic() + 300
        while True:
            row = self.inbox.get(timeout=max(.01, deadline - time.monotonic()))
            if row is None:
                raise RuntimeError("MCP child exited")
            if row.get("id") == self.seq:
                if "error" in row:
                    raise RuntimeError(row["error"])
                return row["result"]

    def tool(self, name, arguments):
        start = time.perf_counter()
        result = self.call("tools/call", {"name": name, "arguments": arguments})
        ms = (time.perf_counter() - start) * 1000
        text = next(c["text"] for c in result["content"] if c["type"] == "text")
        if result.get("isError"):
            raise RuntimeError(f"{name} failed: {text[:300]}")
        return ms, text, json.loads(text)

    def close(self):
        self.p.terminate()
        try:
            self.p.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.p.kill()
            self.p.wait()
        self.reader.join(timeout=2)
        self.p.stdin.close()
        self.p.stdout.close()


def summary(values):
    ordered = sorted(values)
    return {"p50_ms": round(statistics.median(values), 2),
            "p95_ms": round(ordered[math.ceil(.95 * len(ordered)) - 1], 2)}


def tokens(chars):
    return round(chars / 4)


def grep_baseline(root, name):
    """Lines naming the symbol as a whole word, and the files holding them."""
    start = time.perf_counter()
    pattern = re.compile(r"\b" + re.escape(name) + r"\b")
    lines, files = [], {}
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP and not d.startswith(".")]
        for f in filenames:
            if Path(f).suffix.lower() not in CODE:
                continue
            path = Path(dirpath) / f
            try:
                text = path.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            rel = path.relative_to(root).as_posix()
            for i, line in enumerate(text.splitlines(), 1):
                if pattern.search(line):
                    lines.append(f"{rel}:{i}:{line.strip()}")
                    files[rel] = len(text)
    ms = (time.perf_counter() - start) * 1000
    out = "\n".join(lines)
    return {"ms": round(ms, 1), "matching_lines": len(lines), "files": len(files),
            "grep_output_tokens": tokens(len(out)), "open_those_files_tokens": tokens(sum(files.values()))}


def copy_vault(source, vault):
    meta = vault / ".obsidianx"
    meta.mkdir(parents=True)
    export = json.loads((source / ".obsidianx" / "brain-export.json").read_text(encoding="utf-8-sig"))
    nodes = []
    for node in export["Nodes"]:
        origin = (source / node["RelativePath"]).resolve()
        if not origin.is_relative_to(source) or not origin.is_file():
            continue
        target = vault / node["RelativePath"]
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(origin, target)
        nodes.append(node)
    export.update(VaultPath=str(vault), Nodes=nodes, TotalNotes=len(nodes))
    (meta / "brain-export.json").write_text(json.dumps(export, ensure_ascii=False), encoding="utf-8")
    return len(nodes)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mcp", type=Path, required=True)
    parser.add_argument("--graph", type=Path, required=True, help="graphify-out/graph.json")
    parser.add_argument("--repo-root", type=Path, required=True, help="the folder Graphify scanned")
    parser.add_argument("--source-vault", type=Path, help="copied for the notes arm; omit for an empty vault")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rounds", type=int, default=5)
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error("--rounds must be positive")
    with tempfile.TemporaryDirectory(prefix="brainx-codegraph-bench-") as tmp:
        vault = Path(tmp) / "vault"
        notes = copy_vault(args.source_vault.resolve(), vault) if args.source_vault else 0
        if not notes:
            (vault / ".obsidianx").mkdir(parents=True, exist_ok=True)
        brain = Brain(args.mcp.resolve(), vault)
        try:
            import_ms, _, imported = brain.tool("brain_code_import", {
                "path": str(args.graph.resolve()), "repo": "bench", "repo_root": str(args.repo_root.resolve())})
            sidecar = vault / ".obsidianx" / "code-graphs" / "bench.json"
            # The first keyword query on a fresh process reads every copied note.
            # Pay it here, separately, so the notes question below is not billed
            # for the vault's cold start.
            cold_search_ms = brain.tool("brain_search", {"query": "warm the keyword index", "limit": 1, "bypass_cache": True})[0] if notes else None
            rows = []
            for q in QUESTIONS:
                first_ms, text, data = brain.tool("brain_code_trace", {"repo": "bench", **q["args"]})
                warm = [brain.tool("brain_code_trace", {"repo": "bench", **q["args"]})[0] for _ in range(args.rounds)]
                rows.append({
                    "question": q["name"], "args": q["args"], "status": data.get("status"),
                    "first_call_ms": round(first_ms, 2), "warm": summary(warm),
                    "edges": len(data.get("edges", [])), "nodes": len(data.get("nodes", [])),
                    "truncatedBy": data.get("truncatedBy"), "graphState": data.get("graph", {}).get("state"),
                    "notes": [n.get("title") for n in data.get("notes", [])],
                    "answer_tokens": tokens(len(text)), "estimatedTokens_reported": data.get("estimatedTokens"),
                    "max_tokens": q["args"].get("max_tokens", 1500),
                    "sample_edges": [f"{e.get('fromName')} -{e.get('relation')}/{e.get('provenance')}-> {e.get('toName')} "
                                     f"@ {e.get('file')}:{e.get('line')}" for e in data.get("edges", [])[:6]],
                    "candidates": [c.get("qualified") or c.get("name") for c in data.get("ambiguity", {}).get("candidates", [])],
                    "grep_baseline": grep_baseline(args.repo_root.resolve(), q["grep"]),
                })
            report = {
                "method": "One MCP child on a copied vault; brain_code_import then brain_code_trace; first call then warm rounds; "
                          "includes MCP and journaling; notes keyword-only (escalation off). Baseline = whole-word grep over code files "
                          "in the scan root and the size of the files it hits. Tokens = characters / 4.",
                "notes_in_vault": notes, "rounds": args.rounds,
                "first_brain_search_ms": round(cold_search_ms, 1) if cold_search_ms is not None else None,
                "first_brain_search_caveat": "Existing keyword path, cold process over the copied vault; not part of the graph and not a cold-start claim.",
                "import": {"ms": round(import_ms, 1), "symbols": imported["symbols"], "edges": imported["edges"],
                           "missingEndpoints": imported["missingEndpoints"], "filesNewerThanGraph": imported["filesNewerThanGraph"],
                           "graph_json_bytes": args.graph.stat().st_size, "sidecar_bytes": sidecar.stat().st_size,
                           "graphify_version": imported["source"]["version"], "commit": imported["commit"],
                           "warnings": imported["warnings"]},
                "questions": rows,
            }
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0
        finally:
            brain.close()


if __name__ == "__main__":
    raise SystemExit(main())
