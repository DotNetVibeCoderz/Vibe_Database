# Dokumentasi MemSharp

[English](../en/README.md) · [README proyek](../../README.id.md)

Dibuat oleh **Gravicode Studios**, dipimpin oleh **Kang Fadhil**.

## Mulai dari sini

| | |
|---|---|
| **[Mulai cepat](getting-started.md)** | Pemasangan, tujuh tipe, dan hal pertama yang perlu dijalankan |
| **[Tipe data](data-types.md)** | Setiap operasi, biayanya, dan struktur di baliknya |
| **[Bahasa query](query-language.md)** | Dialek SQL dan LINQ di atas keyspace |
| **[Persistensi](persistence.md)** | Snapshot, log append-only, dan format filenya |

## Lebih jauh

| | |
|---|---|
| **[Arsitektur](architecture.md)** | Cara kerjanya dan alasannya — baca ini sebelum mengubah engine |
| **[Server dan protokol](server.md)** | RESP2, perintah yang didukung, pipelining |
| **[Perkakas command-line](cli.md)** | `repl`, `serve`, `browse`, `bench`, `demo` |
| **[SDK klien](clients.md)** | Python, Go dan Node.js |
| **[Benchmark](benchmarks.md)** | Angka terukur, metodologi, dan catatan jujurnya |
| **[Demo trading](trading-demo.md)** | Aplikasi Avalonia, dan dua bug yang ia ungkap |

## Gambaran utuhnya dalam satu halaman

```csharp
using MemSharp;

using var db = new MemDb();

// tujuh tipe, satu keyspace
db.Set("symbol:BTC", "68350.25", TimeSpan.FromMinutes(5));
db.ListPushRight("feed", "a", "b");
db.HashSet("user:1", "name", "Kang Fadhil");
db.SetAdd("tags", "crypto");
db.SortedSetAdd("book", "bid-1", 68_349.75);
db.TimeSeriesAdd("px", 68_350.25);
db.StreamAdd("events", ["kind", "fill"]);

// query-nya
db.ExecuteSql("SELECT key, type, size FROM keys ORDER BY size DESC LIMIT 10");
db.Query().Where(k => k.ExpiresAt is not null).OrderBy(k => k.ExpiresAt);

// simpan
db.Save();

// layani lewat jaringan
await using var server = new MemServer(db, new MemServerOptions { Port = 6380 });
await server.StartAsync();
```

## Jawaban atas pertanyaan yang paling sering muncul

**Apakah thread-safe?** Ya, untuk setiap operasi. Operasi satu key bersifat atomik. Baca lintas-key
bukan point-in-time — [alasannya](architecture.md#yang-bukan-atomik).

**Secepat apa?** 8,9 juta `HGET`/detik embedded, 470 ribu `GET`/detik lewat TCP ber-pipeline, di
Ryzen 8-core. [Tabel lengkap dan catatannya](benchmarks.md).

**Apakah data saya bertahan setelah restart?** Hanya jika Anda memintanya. Bawaannya hanya memori.
[Empat konfigurasi](persistence.md#memilih-konfigurasi).

**Bisakah pakai `redis-cli`?** Bisa, untuk perintah yang MemSharp implementasikan.
[Yang mana saja](server.md#perintah-yang-didukung).

**Sebaiknya pakai ini daripada Redis?** Untuk menanam penyimpanan cepat di dalam proses .NET, atau
untuk mempelajari cara sebuah database dibangun — ya. Untuk caching produksi berskala besar, pakai
Redis. [Batasan yang jujur](../../README.id.md#batasan-yang-jujur).

## Berkontribusi

```bash
dotnet build -c Release
dotnet test tests/MemSharp.Tests/MemSharp.Tests.csproj -c Release    # 214 tes
python .github/scripts/check_docs.py .
```

Perhatikan perintah tes itu tidak memakai `--nologo`: SDK meneruskan argumen yang tidak dikenalnya ke
test runner, yang menolaknya lalu melaporkan *zero tests ran* alih-alih gagal dengan jelas.

Dokumentasi dicerminkan antara `docs/en` dan `docs/id`, dan CI gagal bila sebuah halaman ada di satu
sisi tapi tidak di sisi lain. Perbarui keduanya.
