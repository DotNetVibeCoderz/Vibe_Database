# FAISS.Net.Gpu

GPU acceleration for [FAISS.Net](https://www.nuget.org/packages/Gravicode.FaissNet), via [ILGPU](https://ilgpu.net/). Drop-in replacements for the flat indexes that run on CUDA or OpenCL.

```csharp
using Faiss.Net.Gpu;

using var index = new IndexFlatL2Gpu(dimension: 128);
index.Add(database);
var results = index.Search(queries, k: 10);   // identical API, identical results
```

## Why brute force belongs on a GPU

Exhaustive search is the ideal GPU workload: every candidate is independent, the arithmetic is a pure multiply-add chain, and the access pattern is a straight sequential read. It is also memory-bandwidth-bound, which is precisely where a GPU has an order-of-magnitude advantage — so the speedup is largest exactly where the CPU path hurts most.

Two kernels run per query chunk. The first fills a `chunk × ntotal` distance matrix, one thread per (query, vector) pair. The second selects the top k per query on the device, so only `chunk × k` results cross the bus instead of the whole matrix — the transfer, not the arithmetic, is what would otherwise dominate.

Query batches are chunked automatically so the distance matrix stays inside a configurable device-memory budget, which lets a database far larger than device memory still be searched in one call.

## It runs without a GPU

With no CUDA or OpenCL device present, ILGPU falls back to a CPU accelerator and the *same kernels* run. Code written against a GPU index keeps working on a machine without one — just without the speedup.

```csharp
if (StandardGpuResources.IsGpuAvailable())
    Console.WriteLine(string.Join("\n", StandardGpuResources.EnumerateDevices()));

using var resources = new StandardGpuResources();
Console.WriteLine(resources.DeviceName);
Console.WriteLine(resources.IsHardwareAccelerated);   // false on the CPU fallback
```

Check `IsHardwareAccelerated` before drawing conclusions from a benchmark.

## Moving indexes between CPU and GPU

```csharp
var cpu = new IndexFlatL2(128);
cpu.Add(database);

using var gpu = GpuIndexFlat.FromCpu(cpu);   // faiss.index_cpu_to_gpu
var back = gpu.ToCpu();                      // faiss.index_gpu_to_cpu
```

## Multi-GPU

One replica per device, with queries split across them — the pattern `IndexReplicas` exists for:

```csharp
var replicas = new IndexReplicas(128);
foreach (var device in StandardGpuResources.ForEachGpu())
    replicas.AddReplica(new IndexFlatL2Gpu(128, device));
replicas.Add(database);
```

## Scope and honesty

- **Flat indexes only.** GPU IVF and PQ are on the roadmap, not in this release.
- **Validated against ILGPU's CPU fallback accelerator.** The kernels are covered by tests that assert results identical to the CPU library, but the backend has not yet been benchmarked on real CUDA hardware — so this package makes no speed claims.
- **Vectors live in device memory.** `Add` re-uploads the database, so build once and query many times.

## Documentation

- [Getting started](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/getting-started.md)
- [API reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/api-reference.md#gpu--namespace-faissnetgpu)
- [Architecture](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/FAISS.Net/docs/en/architecture.md#the-gpu-backend)

---

MIT licensed. Built by **Gravicode Studios**, led by **Kang Fadhil**.
