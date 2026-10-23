# Experiment: a Rust native layer under MemSharp

**Question.** Would rewriting part of MemSharp in Rust and calling it through P/Invoke make it faster?

**Answer, measured.** Not on the hot path. Crossing into native code costs more than the operations
it would replace, and the large win this experiment found — a 14× faster snapshot checksum — comes
from changing the *algorithm*, which is equally fast in C#. Where Rust could still earn its place is
described at the end, with what it would cost to ship.

Not shipped, not in `MemSharp.slnx`, not run by CI: it needs a Rust toolchain and a natively built
library on every platform, and nothing in the result justifies adding either to the build.

---

## What was measured

`memsharp_native/` is a Rust `cdylib` (no `unsafe` beyond the FFI edge, one dependency:
`xxhash-rust`). `NativeBench/` is a BenchmarkDotNet harness that calls it next to the managed code
it would replace. Three questions, one benchmark class each.

### 1. What does a call into native code cost?

| | Mean |
|---|---:|
| Managed method call (non-inlined) | 0.6 ns |
| P/Invoke, `LibraryImport` | **6.9 ns** |
| P/Invoke with `[SuppressGCTransition]` | 1.2 ns |

The default transition — switching the thread to preemptive GC mode and back — is ~6 ns, about a
quarter of a hot `GET`. `SuppressGCTransition` removes it, but is only legal for calls that are short,
never block and never call back into .NET. A hash-map lookup behind a mutex is none of those
reliably, so the 6.9 ns is the honest figure for anything real.

### 2. Is a Rust hash map behind P/Invoke a faster `GET`?

100,000 keys, UTF-16 in and out (what a .NET caller actually has and needs), single-threaded.

| | Mean | Allocated |
|---|---:|---:|
| `MemDb.Get` | **242 ns** | 0 B |
| Rust `HashMap` via P/Invoke | 468 ns | 71 B |

**Rust is about 2× slower here**, and it is not Rust's fault. `MemDb.Get` hands back the `string`
already stored — no copy, no allocation. Anything stored natively has to be copied back across the
boundary and turned into a new .NET string on *every* read, plus the transition. The engine's cost
is a lock and a dictionary probe; there is nothing left for native code to win back that the copy
does not cost twice over.

The machine was also running other work during this run, which inflates both absolute figures; the
ratio is what matters, and it held across BenchmarkDotNet's iterations.

### 3. For bulk byte work, does Rust win — or does the algorithm?

A 64 MB buffer, about the size of a snapshot holding a million small keys.

| | Mean | vs. today |
|---|---:|---:|
| FNV-1a in C# — **MemSharp's snapshot checksum today** | 85.4 ms | 1.00 |
| FNV-1a in Rust | 84.1 ms | 0.99 |
| XXH3-64 in C# (`System.IO.Hashing`) | **5.9 ms** | **0.07** |
| XXH3-64 in Rust | 6.2 ms | 0.07 |

The same algorithm runs at the same speed in both languages: FNV-1a is one multiply per byte with
each step depending on the last, and no compiler can vectorise that. XXH3 is built for wide
registers and is **14× faster — in C# just as in Rust**. The managed version even edges ahead,
because it does not cross the boundary.

---

## What this means for MemSharp

1. **Do not move the engine, or any per-operation path, into native code.** Its operations cost tens
   to hundreds of nanoseconds, and a boundary crossing plus a copy-back costs about as much again.
   Microsoft's Garnet — a .NET RESP server that outperforms Redis — is the existence proof that the
   ceiling here is design, not the runtime.

2. **The checksum is worth changing, in C#.** Replacing FNV-1a with XXH3 (or with hardware CRC32C,
   which `System.Runtime.Intrinsics` exposes with no package at all) would cut the checksum share of
   a save and of a load-time verification by ~14×. It is a snapshot-format change, so it needs a
   format version bump; and `System.IO.Hashing` is a package, which `MemSharp.Core`'s
   no-dependencies rule forbids — CRC32C or a hand-written XXH3 keeps that rule. Tracked in PLAN.md.

3. **Where native code *could* earn its place** — none of it on the per-operation path:
   - **Off-heap storage for very large keyspaces.** At tens of millions of keys the cost that grows is
     the GC tracing the object graph, not the operations. A native arena would remove that from the
     GC's view entirely. But every read would pay the copy measured above, and pinned or
     `GC.AllocateArray(pinned: true)` byte slabs on the managed side get most of the benefit first.
   - **Snapshot compression** (zstd, lz4) — bulk, once per save, where a boundary crossing is
     amortised over megabytes. Mature native libraries already exist; Rust is one way to bind them.

### What shipping a native library would cost

- A build for every runtime the package claims: win-x64, win-arm64, linux-x64, linux-arm64,
  linux-musl-x64, osx-x64, osx-arm64 — a Rust toolchain and a cross-compile matrix in CI.
- `runtimes/<rid>/native/` layout in the NuGet package, and a package several times larger.
- Debugging across a boundary where a crash is an access violation, not an exception.
- Losing "no dependencies, runs anywhere .NET runs" — which is a feature MemSharp sells.

None of that is unreasonable for an optional `MemSharp.Native` package, *if* a measurement shows a
win large enough to pay for it. This one did not.

---

## Reproducing

```bash
cd experiments/native-rust/memsharp_native && cargo build --release
cd ../NativeBench && dotnet run -c Release -- --filter '*'
```

Raw figures: `results.json`. Measured on an 8-core AMD Ryzen, Windows 11, .NET 10.0.12,
rustc 1.97.1, 2026-10-23.
