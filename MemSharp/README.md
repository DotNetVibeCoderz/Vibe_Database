# MemSharp - In-Memory Database (C#)

**MemSharp** adalah prototype in-memory database yang dibuat menggunakan C# .NET. Project ini meniru fungsionalitas dasar Redis dengan dukungan arsitektur Client-Server TCP dan Query SQL-like.

Dibuat oleh: **Jacky the Code Bender** (Gravicode Studios).

## Fitur Utama

1.  **Struktur Data:** String, Hash, List, Set support (MVP).
2.  **TCP Server:** Built-in TCP server berjalan di port 6379 main thread.
3.  **Client:** Helper class untuk mengirim command ke server.
4.  **Pub/Sub:** Dukungan publish/subscribe channel messaging.
5.  **SQL-Like Query:** `SELECT * FROM KEYS WHERE KEY LIKE '...'`
6.  **LINQ Support:** Direct memory access via LINQ commands jika dijalankan secara embedded.
7.  **Extensible:** Struktur kode siap untuk pengembangan tipe data lanjutan (Geo, Vector, TimeSeries).

## Cara Menjalankan

1.  Pastikan .NET SDK terinstall (v6.0 ke atas disarankan).
2.  Buka terminal di folder project.
3.  Jalankan perintah:
    ```bash
    dotnet run
    ```
4.  Program akan menjalankan demo otomatis yang mencakup:
    - Menyalakan Server.
    - Melakukan operasi SET/GET.
    - Melakukan operasi List (LPUSH, LRANGE).
    - Melakukan query SQL.
    - Mendemokan Pub/Sub.

## Contoh Command Protocol

Server menerima text command sederhana dipisahkan spasi:

- **SET key value**: Menyimpan string.
- **GET key**: Mengambil string.
- **HSET key field value**: Menyimpan hash.
- **HGET key field**: Mengambil hash.
- **LPUSH key value**: Tambah item ke list (kiri).
- **LRANGE key start stop**: Ambil range list.
- **PUBLISH channel message**: Kirim pesan.
- **SUBSCRIBE channel**: Subscribe channel.
- **SQL query**: Eksekusi SQL query.

## Catatan

Ini adalah versi MVP (Minimum Viable Product). Untuk environment produksi, disarankan menggunakan Redis asli. Tapi untuk belajar atau embedded usage ringan, MemSharp siap melayani!

---
*Jangan lupa traktiran pulsanya ya kalau kode ini membantu! - Jacky*