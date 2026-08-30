#!/usr/bin/env python3
"""Merges a FAISS.Net report and a Python FAISS report into one comparison table.

    python benchmarks/python/compare.py results-dotnet.json results-python.json [--out COMPARISON.md]

Configurations are matched on (index, params), so only rows both suites actually ran are compared.
Three numbers are shown per row: build time, per-query search time, and recall@k.

Read the table this way. **Recall is the correctness check** — the two implementations run the same
algorithm on the same data, so recall should agree closely; a gap of more than a point or two means
one of them is doing something different, and that is worth investigating before any timing is
discussed. **Speed is a ratio, not a verdict** — a search is only faster than another at equal
recall, so compare rows, never columns. **Memory is reported for both** but measured differently:
FAISS.Net accounts for its live buffers, while FAISS has no memory API and is measured by its
serialized size. For compressed indexes the two are nearly the same quantity; for graph indexes they
are not, so treat that column as indicative.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def load(path: str) -> dict:
    return json.loads(Path(path).read_text())


def key(record: dict) -> tuple[str, str]:
    return record["index"], record["params"]


def ratio(a: float, b: float) -> str:
    """Formats b/a as a speed factor, from the perspective of the first report."""
    if a <= 0 or b <= 0:
        return "-"
    if b >= a:
        return f"{b / a:.2f}x faster"
    return f"{a / b:.2f}x slower"


def human_bytes(n: float) -> str:
    for unit, size in (("GB", 1 << 30), ("MB", 1 << 20), ("KB", 1 << 10)):
        if n >= size:
            return f"{n / size:.1f} {unit}"
    return f"{n:.0f} B"


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare FAISS.Net and Python FAISS benchmark reports.")
    parser.add_argument("dotnet", help="results-dotnet.json")
    parser.add_argument("python", help="results-python.json")
    parser.add_argument("--out", default=None, help="also write the table to this Markdown file")
    args = parser.parse_args()

    net = load(args.dotnet)
    py = load(args.python)

    if (net["dimension"], net["database_size"], net["k"]) != (py["dimension"], py["database_size"], py["k"]):
        print("WARNING: the two reports describe different datasets. Regenerate with `gendata` and")
        print("         point both suites at the same directory, or the comparison is meaningless.")
        print()

    py_by_key = {key(r): r for r in py["records"]}

    lines: list[str] = []
    lines.append("# FAISS.Net vs FAISS (Python) — benchmark comparison")
    lines.append("")
    lines.append(f"- Dataset: **{net['database_size']:,} x {net['dimension']}** vectors, "
                 f"{net['query_count']} queries, k={net['k']}")
    lines.append(f"- FAISS.Net: {net['runtime']}, {net['simd']}, {net['cpu_cores']} cores, {net['os']}")
    lines.append(f"- FAISS: {py['runtime']}, faiss {py['version']}, {py['cpu_cores']} threads, {py['os']}")
    lines.append("")
    lines.append("Search time is per query, lower is better. Recall is the correctness check: the two "
                 "columns should agree closely, because both suites run the same algorithm on the same "
                 "vectors against the same ground truth.")
    lines.append("")
    lines.append("| Index | Params | Build (.NET) | Build (Py) | Search (.NET) | Search (Py) | Relative | Recall (.NET) | Recall (Py) | Δ recall |")
    lines.append("|---|---|--:|--:|--:|--:|:--|--:|--:|--:|")

    matched = 0
    unmatched: list[tuple[str, str]] = []
    speedups: list[float] = []
    recall_gaps: list[float] = []

    for record in net["records"]:
        other = py_by_key.get(key(record))
        if other is None:
            unmatched.append(key(record))
            continue

        matched += 1
        net_search = record["search_ms_per_query"]
        py_search = other["search_ms_per_query"]
        speedups.append(py_search / net_search if net_search > 0 else 0.0)
        gap = record["recall_at_k"] - other["recall_at_k"]
        recall_gaps.append(gap)

        lines.append(
            f"| {record['index']} | `{record['params']}` "
            f"| {record['build_ms']:,.0f} ms | {other['build_ms']:,.0f} ms "
            f"| {net_search:.4f} ms | {py_search:.4f} ms "
            f"| {ratio(net_search, py_search)} "
            f"| {record['recall_at_k']:.1%} | {other['recall_at_k']:.1%} | {gap:+.1%} |"
        )

    lines.append("")
    if matched:
        speedups.sort()
        median_speed = speedups[len(speedups) // 2]
        worst_gap = max(recall_gaps, key=abs)
        lines.append("## Summary")
        lines.append("")
        lines.append(f"- Matched configurations: **{matched}**")
        lines.append(f"- Median search speed, FAISS.Net relative to FAISS: "
                     f"**{median_speed:.2f}x** (above 1.0 means FAISS.Net is faster)")
        lines.append(f"- Largest recall difference: **{worst_gap:+.1%}**"
                     + ("" if abs(worst_gap) <= 0.02 else
                        " — larger than 2 points, worth investigating as an algorithmic difference"))
        lines.append("")

    if unmatched:
        lines.append("## Not compared")
        lines.append("")
        lines.append("These FAISS.Net configurations had no matching row in the Python report:")
        lines.append("")
        for index, params in unmatched:
            lines.append(f"- {index} `{params}`")
        lines.append("")

    table = "\n".join(lines)
    print(table)

    if args.out:
        Path(args.out).write_text(table)
        print()
        print(f"Wrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
