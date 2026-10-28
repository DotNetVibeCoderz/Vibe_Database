# Menjalankan index di produksi

*Read this in [English](../en/production.md).*

Minggu pertama menaruh index vektor di balik sebuah API selalu memunculkan empat pertanyaan yang
sama: *bisakah saya mencari hanya sebagian vektor, bisakah saya mendapatkan kembali recall yang
hilang karena kompresi, bisakah saya menambah data sambil mencari, dan bagaimana menyimpan index
yang tidak pernah berhenti tumbuh?* Halaman ini menjawab masing-masing dengan API yang memang
disediakan untuknya.

---

## Pencarian tersaring

Batasi pencarian ke sebagian id dengan sebuah `IDSelector`. Selector diterapkan **di dalam
pemindaian**, jadi permintaan 10 hasil mengembalikan 10 *anggota* terbaik — bukan 10 terbaik secara
keseluruhan yang non-anggotanya dibuang belakangan, cara akal-akalan yang lazim dan diam-diam
mengembalikan kurang dari 10.

```csharp
// Rentang id milik satu tenant
var result = index.Search(query, k: 10, selector: IDSelector.Range(1_000, 2_000));

// Apa pun yang bisa dinyatakan sebagai predikat (satu pemanggilan delegate per kandidat)
var visible = index.Search(query, k: 10, selector: IDSelector.Predicate(id => acl.CanSee(user, id)));

// Himpunan yang sudah dihitung, sebagai bitmap — satu uji bit per kandidat, nyaris gratis
var bitmap = IDSelectorBitmap.FromIds(allowedIds, capacity: index.Ntotal);
var allowed = index.Search(query, k: 10, selector: bitmap);

// Komposisi
var sel = IDSelector.And(IDSelector.Range(0, 50_000), IDSelector.Not(IDSelector.Batch(deletedIds)));
```

| Selector | Biaya per kandidat | Catatan |
|---|---|---|
| `IDSelector.Range(min, max)` | dua perbandingan | IVF melewati seluruh daftar yang rentang id-nya tidak bersinggungan; shard melewati seluruh shard |
| `IDSelector.Bitmap(bytes)` / `IDSelectorBitmap.FromIds` | satu uji bit | pilihan yang tepat untuk "pengguna ini boleh melihat id-id ini" |
| `IDSelector.Batch(ids)` | satu lookup hash | himpunan eksplisit |
| `IDSelector.Predicate(f)` | satu pemanggilan delegate | selain itu semua |
| `Not`, `And`, `Or`, `XOr`, `All` | jumlah biaya bagian-bagiannya | komposisi |

Id yang dimaksud adalah id yang dilaporkan index: posisi untuk `IndexFlat`, id pemanggil di bawah
`IndexIDMap` dan di keluarga IVF. Setiap index di pustaka ini mendukung selector, termasuk index
graf — di sana non-anggota tetap *dilewati* (graf harus tetap bisa dinavigasi) tetapi tidak pernah
dikembalikan. Seperti di FAISS, penyusurannya dibatasi oleh `EfSearch` atas semua node yang
dikunjungi, sehingga pencarian terfilter berbiaya kurang lebih sama dengan pencarian tanpa filter;
di bawah filter yang sangat selektif (beberapa persen dari id) hasilnya bisa kurang dari `k`
anggota — naikkan `EfSearch` untuk kasus itu, atau gunakan index flat atau IVF, yang selector-nya
eksak.

### Parameter per pemanggilan

Selector adalah satu field dari `SearchParameters`. Subkelasnya membawa pengaturan khas tiap index,
dan meneruskannya hanya memengaruhi pemanggilan itu, sehingga pencarian bersamaan bisa memakai
pengaturan berbeda pada satu index tanpa berebut properti bersama:

```csharp
var p = new SearchParametersIVF { Sel = IDSelector.Range(0, 5_000), Nprobe = 32, MaxCodes = 20_000 };
var r = ivf.Search(queries, k: 10, p);                     // ivf.Nprobe tidak berubah

var h = new SearchParametersHNSW { EfSearch = 128 };       // juga dipakai IndexNSGFlat (sebagai ukuran pool-nya)
var g = hnsw.Search(queries, k: 10, h);
```

`MaxCodes` membatasi kerja satu kueri: pemindaian berhenti begitu sebanyak itu kode sudah diskor,
yang membatasi latensi kueri yang sel-sel terdekatnya kebetulan sangat besar.

Index yang tidak bisa memenuhi parameter non-bawaan melempar `NotSupportedException` alih-alih
mengabaikannya diam-diam.

---

## Pemeringkatan ulang dengan `IndexRefine`

Kompresi memakan recall. Sebagian besarnya kembali dengan mengambil lebih banyak kandidat dari index
terkompresi lalu menskor ulang kandidat itu saja terhadap vektor eksak:

```csharp
var index = new IndexRefineFlat(new IndexIVFPQ(d, nlist: 1024, m: 16)) { KFactor = 4 };
index.Train(sample);
index.Add(vectors);
var result = index.Search(query, k: 10);   // 40 kandidat dari IVFPQ, diperingkat ulang secara eksak
```

Atau lewat factory: `"IVF1024,PQ16,RFlat"` dan `"PQ16,Refine(SQfp16)"`. `IndexRefine` menerima dua
index apa pun; `IndexRefineFlat` membuatkan tahap refine `IndexFlat` yang eksak untuk Anda.

Pemindaiannya tetap hanya membaca kode yang ringkas; hanya `k * KFactor` vektor per kueri yang
menyentuh index refine. Karena pembacaan itu sedikit dan acak, index refine boleh tinggal di tempat
yang lebih lambat — misalnya `MappedIndexFlat` di disk — tanpa pemindaiannya ikut menanggung biaya.
`KFactor` bisa diubah per pemanggilan lewat `SearchParametersRefine`.

---

## Mencari sambil menambah

Sebagian besar index mengizinkan **satu penulis dan pembaca bersamaan berapa pun tanpa kunci**:

| Index | Cari sementara satu thread menambah | Cari sementara satu thread menghapus |
|---|:-:|:-:|
| `IndexFlat`, `IndexFlatL2`, `IndexFlatIP` | ✅ | ✅ |
| `IndexIVFFlat`, `IndexIVFPQ`, `IndexIVFScalarQuantizer` | ✅ | ✅ |
| `IndexPQ`, `IndexScalarQuantizer` | ✅ | ✅ |
| `IndexHNSWFlat`, `IndexHNSWSQ`, `IndexHNSWPQ`, `IndexBinaryHNSW` | ✅ | — (tanpa penghapusan) |
| `IndexIDMap`, `IndexPreTransform`, `IndexRefine` di atas index-index tersebut | ✅ | bungkus dengan `SynchronizedIndex` — tabel id milik wrapper tidak copy-on-write |
| `IndexSparseFlat` | ✅ | ✅ |
| selain itu, atau lebih dari satu penulis | bungkus dengan `SynchronizedIndex` | bungkus dengan `SynchronizedIndex` |

Cara kerjanya: penyimpanan dipublikasikan sebagai snapshot yang tidak berubah. Sebuah pencarian
mengambil satu snapshot di awal dan melihat persis vektor yang ada saat itu. Penambahan menulis
melewati ujung yang sudah dipublikasikan lalu memublikasikan snapshot yang lebih panjang sesudahnya;
penghapusan membangun array baru (copy-on-write — untuk IVF, hanya daftar yang benar-benar
berubah) lalu memublikasikannya. Keluarga HNSW membaca titik masuknya sebelum penyimpanannya dan
mengabaikan tautan ke simpul di luar snapshot-nya, sehingga graf yang sedang diperluas oleh
penambahan bersamaan tidak pernah disusuri sampai ke vektor yang tidak bisa dilihat pencarian itu.

Yang *tidak* tercakup: dua penulis sekaligus, serta `Train`/`Reset` selagi pencarian berjalan. Untuk
itu, dan untuk index apa pun yang tidak ada di tabel, bungkus index-nya:

```csharp
using var shared = new SynchronizedIndex(index);   // reader-writer lock
```

Pencarian lewat pembungkus ini tetap berjalan bersamaan satu sama lain; setiap mutasi menunggu
pencarian selesai lalu berjalan sendirian.

---

## Menyimpan index yang terus tumbuh

`FaissNet.WriteIndex` menulis ulang seluruh index, dan itu alat yang keliru untuk index yang menerima
beberapa vektor setiap detik. `PersistentIndex` menambahkan journal write-ahead:

```csharp
using var index = PersistentIndex.Open("vectors.index", () => new IndexIDMap2(new IndexHNSWFlat(384)));

index.AddWithIds(batch, ids);   // ditambahkan ke vectors.index.journal, lalu diterapkan
index.RemoveIds(staleIds);      // ikut dicatat di journal
index.Checkpoint();             // snapshot ditulis ulang, journal dikosongkan
```

- Setiap mutasi adalah satu record berawalan panjang yang diperiksa CRC-32, ditulis **sebelum**
  diterapkan. `DurableWrites` (aktif secara bawaan) mem-flush setiap record ke perangkat sebelum
  pemanggilan kembali.
- Saat dibuka, snapshot dimuat lalu journal diputar ulang. Record terakhir yang robek — crash di
  tengah penulisan — gagal checksum, dilaporkan lewat `RecoveredFromTornWrite`, lalu dibuang; setiap
  record sebelumnya tetap diputar ulang.
- `Checkpoint()` menulis snapshot baru ke berkas sementara, mem-flush-nya, mengganti snapshot lama
  secara atomik, lalu memulai journal baru. Snapshot dan journal membawa nomor generasi, sehingga
  crash di antara langkah-langkah itu meninggalkan journal yang dikenali pembukaan berikutnya sebagai
  sudah terlipat ke snapshot.
- `CheckpointAfterBytes` melakukan checkpoint otomatis begitu journal melewati ukuran tertentu.
- Penghapusan berdasarkan predikat ditolak: delegate tidak bisa ditulis ke journal. Berikan id secara
  eksplisit.

---

## Vektor sparse

Embedding sparse terpelajar (SPLADE) dan vektor bag-of-words berbobot punya puluhan ribu dimensi dan
hanya beberapa ratus nilai bukan nol. Bila disimpan padat, masing-masing akan dipindai sebagai 30.000
float; `IndexSparseFlat` justru menyimpan inverted index dan hanya menskor posting yang disentuh
kueri:

```csharp
using Faiss.Net.Sparse;

var index = new IndexSparseFlat(dimension: 30_522, normalizeOnAdd: true); // cosine
index.Add(documents);                                   // IReadOnlyList<SparseVector>
var hits = index.Search(new SparseVector(termIds, weights), k: 10);
```

Skornya inner product eksak. Penghapusan menandai id sebagai terhapus (id tetap stabil) dan
`Compact()` mengambil kembali ruang posting-nya. Index ini tersimpan lewat `IndexIO.WriteSparseIndex`
/ `ReadSparseIndex`, mendukung selector, dan mengizinkan pencarian sementara satu penulis menambah.

---

Dibuat oleh **Gravicode Studios**, dipimpin oleh **Kang Fadhil**.
