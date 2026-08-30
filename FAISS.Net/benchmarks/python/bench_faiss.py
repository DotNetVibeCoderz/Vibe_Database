#!/usr/bin/env python3
"""Reference benchmark for Python FAISS, matched to the FAISS.Net suite.

This runs the same index configurations, on the same vectors, measured the same way as
``benchmarks/Faiss.Net.Benchmarks``. It deliberately reads the ``.fvecs`` files that the .NET side
generates rather than creating its own data: two independently generated datasets with the same
parameters are still different datasets, and comparing timings across them measures nothing. Ground
truth is read from disk for the same reason, so a recall difference between the two runs can only
come from the index.

Usage
-----
    # once, from the repository root
    dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- gendata --out data
    dotnet run -c Release --project benchmarks/Faiss.Net.Benchmarks -- suite --data data --out results-dotnet.json

    pip install faiss-cpu numpy
    python benchmarks/python/bench_faiss.py --data data --out results-python.json
    python benchmarks/python/compare.py results-dotnet.json results-python.json

Interpreting the result
-----------------------
FAISS's CPU kernels are hand-written SIMD C++ and, for the flat and IVF paths, are usually linked
against a tuned BLAS; matching them exactly is not the goal. What the comparison is for is showing
where a managed implementation lands, and confirming that recall agrees configuration by
configuration — a large recall gap points at an algorithmic difference, which matters far more than
a constant factor in speed.
"""

from __future__ import annotations

import argparse
import json
import platform
import statistics
import time
from pathlib import Path

import numpy as np

try:
    import faiss
except ImportError:  # pragma: no cover - guidance, not logic
    raise SystemExit(
        "faiss is not installed. Install the CPU build with:\n"
        "    pip install faiss-cpu numpy\n"
        "or the GPU build with:\n"
        "    pip install faiss-gpu numpy"
    )

SEARCH_REPEATS = 5


def read_fvecs(path: Path) -> np.ndarray:
    """Reads the standard .fvecs layout: int32 dimension, then that many float32, per record."""
    raw = np.fromfile(path, dtype="int32")
    dim = raw[0]
    return raw.reshape(-1, dim + 1)[:, 1:].copy().view("float32")


def read_ivecs(path: Path) -> np.ndarray:
    raw = np.fromfile(path, dtype="int32")
    dim = raw[0]
    return raw.reshape(-1, dim + 1)[:, 1:].copy()


def recall_at_k(ground_truth: np.ndarray, found: np.ndarray, k: int) -> float:
    """Fraction of true neighbours recovered, identical in definition to the .NET suite."""
    hits = 0
    total = 0
    for truth_row, found_row in zip(ground_truth, found):
        truth = set(int(x) for x in truth_row[:k] if x >= 0)
        total += len(truth)
        hits += sum(1 for x in found_row[:k] if int(x) in truth)
    return hits / total if total else 0.0


def timed(fn):
    start = time.perf_counter()
    result = fn()
    return (time.perf_counter() - start) * 1000.0, result


def measure_search(index, queries, k, ground_truth, records, name, params,
                   train_ms, add_ms, memory_bytes, verbose):
    """Warm up, then take the median of several full passes over the query set."""
    index.search(queries, k)  # warm-up: first call touches the whole index

    timings = []
    labels = None
    for _ in range(SEARCH_REPEATS):
        elapsed, (_, labels) = timed(lambda: index.search(queries, k))
        timings.append(elapsed)
    median = statistics.median(timings)

    nq = queries.shape[0]
    record = {
        "implementation": "FAISS (Python)",
        "index": name,
        "params": params,
        "train_ms": train_ms,
        "add_ms": add_ms,
        "build_ms": train_ms + add_ms,
        "search_ms_per_query": median / nq,
        "queries_per_second": nq / (median / 1000.0),
        "recall_at_k": recall_at_k(ground_truth, labels, k),
        "memory_bytes": memory_bytes,
    }
    records.append(record)

    if verbose:
        print(f"  {name + ' ' + params:<28} {record['build_ms']:9.0f}ms "
              f"{record['search_ms_per_query']:10.4f} {record['queries_per_second']:10,.0f} "
              f"{record['recall_at_k']:9.1%} {human_bytes(memory_bytes):>10}")
    return record


def human_bytes(n: int) -> str:
    for unit, size in (("GB", 1 << 30), ("MB", 1 << 20), ("KB", 1 << 10)):
        if n >= size:
            return f"{n / size:.1f}{unit}"
    return f"{n}B"


def serialized_size(index) -> int:
    """FAISS exposes no memory accounting, so the serialized size stands in for it.

    It is the same quantity the .NET suite reports for compressed indexes — codes plus codebooks —
    and it is the number that decides whether an index fits, so it is the fair comparison.
    """
    return len(faiss.serialize_index(index))


def main() -> int:
    parser = argparse.ArgumentParser(description="Python FAISS benchmark matched to the FAISS.Net suite.")
    parser.add_argument("--data", default="data", help="directory holding base.fvecs / query.fvecs / groundtruth.ivecs")
    parser.add_argument("--out", default="results-python.json", help="where to write the JSON report")
    parser.add_argument("--threads", type=int, default=0, help="OMP threads; 0 leaves the FAISS default")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    data_dir = Path(args.data)
    base = read_fvecs(data_dir / "base.fvecs")
    queries = read_fvecs(data_dir / "query.fvecs")
    ground_truth = read_ivecs(data_dir / "groundtruth.ivecs")

    n, d = base.shape
    nq = queries.shape[0]
    k = ground_truth.shape[1]
    nlist = max(16, int(np.sqrt(n)))
    m = next(m for m in range(min(16, d), 0, -1) if d % m == 0)

    if args.threads:
        faiss.omp_set_num_threads(args.threads)

    verbose = not args.quiet
    if verbose:
        print("Python FAISS matched benchmark suite")
        print("=" * 84)
        print(f"  faiss {faiss.__version__}, numpy {np.__version__}, "
              f"{faiss.omp_get_max_threads()} threads")
        print(f"  {n:,} x {d} vectors, {nq} queries, k={k}, nlist={nlist}")
        print()
        print(f"  {'index':<28} {'build':>11} {'ms/query':>10} {'qps':>10} {'recall':>9} {'memory':>10}")
        print("  " + "-" * 82)

    records: list[dict] = []

    def flat():
        index = faiss.IndexFlatL2(d)
        add_ms, _ = timed(lambda: index.add(base))
        measure_search(index, queries, k, ground_truth, records, "IndexFlatL2", "exact",
                       0.0, add_ms, serialized_size(index), verbose)

    def ivf(name, factory, probes):
        index = factory()
        train_ms, _ = timed(lambda: index.train(base))
        add_ms, _ = timed(lambda: index.add(base))
        size = serialized_size(index)
        for nprobe in probes:
            index.nprobe = nprobe
            measure_search(index, queries, k, ground_truth, records, name,
                           f"nlist={nlist},nprobe={nprobe}", train_ms, add_ms, size, verbose)

    def standalone(name, params, factory, needs_training=True):
        index = factory()
        train_ms = timed(lambda: index.train(base))[0] if needs_training else 0.0
        add_ms, _ = timed(lambda: index.add(base))
        measure_search(index, queries, k, ground_truth, records, name, params,
                       train_ms, add_ms, serialized_size(index), verbose)

    def hnsw(ef):
        index = faiss.IndexHNSWFlat(d, 32)
        index.hnsw.efConstruction = 80
        add_ms, _ = timed(lambda: index.add(base))
        index.hnsw.efSearch = ef
        measure_search(index, queries, k, ground_truth, records, "IndexHNSWFlat",
                       f"M=32,efSearch={ef}", 0.0, add_ms, serialized_size(index), verbose)

    flat()
    ivf("IndexIVFFlat", lambda: faiss.IndexIVFFlat(faiss.IndexFlatL2(d), d, nlist), [1, 4, 8, 16, 32])
    ivf("IndexIVFPQ", lambda: faiss.IndexIVFPQ(faiss.IndexFlatL2(d), d, nlist, m, 8), [1, 4, 8, 16, 32])
    ivf("IndexIVFSQ8",
        lambda: faiss.IndexIVFScalarQuantizer(faiss.IndexFlatL2(d), d, nlist,
                                              faiss.ScalarQuantizer.QT_8bit),
        [1, 8, 32])
    standalone("IndexPQ", f"m={m}", lambda: faiss.IndexPQ(d, m, 8))
    standalone("IndexSQ8", "8-bit",
               lambda: faiss.IndexScalarQuantizer(d, faiss.ScalarQuantizer.QT_8bit))
    for ef in (16, 32, 64, 128):
        hnsw(ef)

    report = {
        "implementation": "FAISS (Python)",
        "version": faiss.__version__,
        "runtime": f"Python {platform.python_version()}",
        "simd": "native (compiled C++)",
        "cpu_cores": faiss.omp_get_max_threads(),
        "os": f"{platform.system()} {platform.release()}",
        "dimension": int(d),
        "database_size": int(n),
        "query_count": int(nq),
        "k": int(k),
        "records": records,
    }

    Path(args.out).write_text(json.dumps(report, indent=2))
    if verbose:
        print()
        print(f"Wrote {args.out} ({len(records)} configurations).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
