Nama: FAISS.Net

Deskripsi: FAISS.Net sebagai porting FAISS ke .NET dengan performa tinggi dan API/SDK mirip Python, memanfaatkan hardware acceleration (SIMD, GPU, multi-threading) sambil tetap menjaga API yang familiar bagi developer Python.  

---

 🔑 Desain Arsitektur FAISS.Net

# 1. Core Numerical Engine
- Dibangun di atas Span<T>, Memory<T>, dan SIMD intrinsics untuk operasi vektor/matriks.  
- Mendukung N-dimensional array dengan layout memory efisien.  
- Optimisasi: `System.Numerics.Vector<T>` untuk CPU, ILGPU/ComputeSharp untuk GPU.  

# 2. Index Structures
- Implementasi paralel dari FAISS:
  - Flat Index (exact search).  
  - IVF (Inverted File Index).  
  - HNSW/NSG graph-based index.  
- API mirip Python: `IndexFlatL2`, `IndexIVFPQ`, dll.  

# 3. Quantization & Compression
- Product Quantization (PQ) dan Optimized PQ.  
- Scalar Quantization untuk memori rendah.  
- Binary indexing dengan `BitArray`/`Span<byte>`.  

# 4. Search Operations
- `Search(k, query)` → KNN.  
- `RangeSearch(query, radius)` → radius-based.  
- `Add(vectors)` → batch insert.  
- `Remove(ids)` → predicate filtering.  

# 5. GPU Acceleration
- Backend CUDA/ROCm via ManagedCUDA atau ILGPU.  
- Multi-GPU support dengan `IndexReplicas`.  
- Drop-in replacement: `IndexFlatL2Gpu`.  

# 6. Persistence & IO
- Save/Load index ke file (binary format).  
- Disk-based indexing dengan memory-mapped files (`MemoryMappedFile`).  

# 7. Interop & SDK
- Namespace: `Faiss.Net`  
- Class mirip Python:
  ```csharp
  var index = new IndexFlatL2(dimension:128);
  index.Add(vectors);
  var results = index.Search(query, k:10);
  ```
- Konsistensi API dengan Python agar developer mudah migrasi.  

---

 📊 Tabel Perbandingan FAISS vs FAISS.Net

| Komponen | FAISS (Python/C++) | FAISS.Net (.NET) |
|----------------|----------------------|----------------------|
| Core Engine | C++ BLAS/LAPACK | Span<T>, SIMD, ILGPU |
| Index | Flat, IVF, HNSW | Flat, IVF, HNSW (C# API) |
| Quantization | PQ, OPQ | PQ, OPQ dengan `Span<byte>` |
| GPU | CUDA/ROCm | ILGPU, ManagedCUDA |
| IO | Binary save/load | MemoryMappedFile, BinaryFormatter |
| API | Pythonic | C# idiomatic, mirip Python |

---

 🚀 Optimisasi Performa di .NET
- SIMD intrinsics (`System.Runtime.Intrinsics`) untuk operasi vektor.  
- Parallel.For / PLINQ untuk batch search.  
- Memory pooling (`ArrayPool<T>`) untuk mengurangi GC overhead.  
- Unsafe code untuk akses pointer langsung bila perlu.  
- GPU backend via ILGPU untuk training/indexing besar.  

---
Notes:
- Buatkan dengan .NET 10
- tambahkan readme (English dan Bahasa Indonesia) dan dokumentasi lengkap di folder docs
- buat sample code dengan console, FAISS.Net Gallery: desktop app dengan Avalonia (berisi aneka use case berbeda yang memanfaatkan FAISS.Net), buatkan dengan UI UX yang keren dengan skill 'frontend-design'
- buatkan benchmark di komparasi dengan versi python
- optimasi code agar dapat performance terbaik dan efisien dalam penggunaan memory
- tambahkan info dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil