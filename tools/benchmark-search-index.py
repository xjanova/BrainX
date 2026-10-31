"""Compare lexical indexing on/off against a disposable copy of a vault snapshot.

No packages needed. Neither child ever opens the original vault. This measures
keyword MCP latency and equivalence, not semantic quality or Graphify speed.
"""
import argparse
import json
import math
import os
from pathlib import Path
import queue
import shutil
import statistics
import subprocess
import tempfile
import threading
import time

QUERIES = [
    "supersession", "embedding", "CodeIndexer", "HttpClient", "permissionDecision",
    "session handoff", "hybrid ranking", "MSB3027", "bge-m3", "missing-embeddings",
    "ระบบค้นหาโน้ตทำงานอย่างไร", "การค้นหา", "สมอง", "AI", "ui",
    "no_such_symbol_lexical_bench_731",
]


class Brain:
    def __init__(self, exe, vault, indexed):
        env = dict(os.environ, BRAINX_VAULT=str(vault), BRAINX_SANDBOX="1",
                   BRAINX_MCP_LAUNCHER_CHILD="1", BRAINX_CANARY="0",
                   BRAINX_SEARCH_ESCALATE="0", BRAINX_DISABLE_MEMO="1",
                   BRAINX_OVERLAY="0", BRAINX_KEYWORD_INDEX="1" if indexed else "0")
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
                                "clientInfo": {"name": "search-index-benchmark", "version": "1"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.p.stdin.flush()

    def call(self, method, params):
        self.seq += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.seq, "method": method, "params": params}, ensure_ascii=False) + "\n")
        self.p.stdin.flush()
        deadline = time.monotonic() + 120
        while True:
            row = self.inbox.get(timeout=max(.01, deadline - time.monotonic()))
            if row is None:
                raise RuntimeError("MCP child exited")
            if row.get("id") == self.seq:
                if "error" in row:
                    raise RuntimeError(row["error"])
                return row["result"]

    def search(self, query):
        start = time.perf_counter()
        result = self.call("tools/call", {"name": "brain_search", "arguments": {
            "query": query, "limit": 10, "compact": True, "bypass_cache": True}})
        ms = (time.perf_counter() - start) * 1000
        if result.get("isError"):
            raise RuntimeError("search returned a tool error")
        data = json.loads(next(c["text"] for c in result["content"] if c["type"] == "text"))
        if "results" not in data:
            raise RuntimeError("search did not return full results")
        return ms, [(n["id"], n["score"]) for n in data["results"]]

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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mcp", type=Path, required=True)
    parser.add_argument("--source-vault", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rounds", type=int, default=3)
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error("--rounds must be positive")
    source = args.source_vault.resolve()
    export = json.loads((source / ".obsidianx" / "brain-export.json").read_text(encoding="utf-8-sig"))
    with tempfile.TemporaryDirectory(prefix="brainx-search-bench-") as root:
        vault = Path(root) / "vault"
        meta = vault / ".obsidianx"
        meta.mkdir(parents=True)
        nodes = []
        size = 0
        for node in export["Nodes"]:
            rel = node["RelativePath"]
            origin = (source / rel).resolve()
            if not origin.is_relative_to(source) or not origin.is_file():
                continue
            target = vault / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(origin, target)
            nodes.append(node)
            size += origin.stat().st_size
        export.update(VaultPath=str(vault), Nodes=nodes, TotalNotes=len(nodes))
        (meta / "brain-export.json").write_text(json.dumps(export, ensure_ascii=False), encoding="utf-8")
        brains = {}
        try:
            brains = {"scan": Brain(args.mcp.resolve(), vault, False)}
            brains["index"] = Brain(args.mcp.resolve(), vault, True)
            cold = {name: brain.search(QUERIES[0])[0] for name, brain in brains.items()}
            # Warm query IDF in both, not merely the result/embedding memo.
            for q in QUERIES:
                for brain in brains.values():
                    brain.search(q)
            timings = {name: [] for name in brains}
            mismatches = []
            rows = []
            for turn in range(args.rounds):
                for q in QUERIES:
                    results = {}
                    pair = {}
                    order = ["scan", "index"] if turn % 2 == 0 else ["index", "scan"]
                    for name in order:
                        ms, results[name] = brains[name].search(q)
                        timings[name].append(ms)
                        pair[name + "_ms"] = round(ms, 2)
                    if results["scan"] != results["index"]:
                        mismatches.append({"query": q, "round": turn})
                    rows.append({"query": q, "round": turn, **pair})
            report = {"notes": len(nodes), "body_bytes": size, "queries": len(QUERIES), "rounds": args.rounds,
                      "method": "Same binary; lexical index on/off; copied vault; keyword-only; overlay and result memo disabled; IDF warm; includes MCP and journaling",
                      "first_query_ms": {k: round(v, 2) for k, v in cold.items()},
                      "first_query_caveat": "Sequential first use, scan before index; shared filesystem/antivirus caches are not reset. These numbers cannot establish cold-start speedup.",
                      "warm": {name: summary(times) for name, times in timings.items()},
                      "ranking_mismatches": mismatches, "samples": rows}
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps({k: v for k, v in report.items() if k != "samples"}, ensure_ascii=False, indent=2))
            return 1 if mismatches else 0
        finally:
            for brain in brains.values():
                brain.close()


if __name__ == "__main__":
    raise SystemExit(main())
