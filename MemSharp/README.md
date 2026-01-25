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

---

# English Version

**MemSharp** is an in-memory database prototype built using C# .NET. This project mimics basic Redis functionality with TCP Client-Server architecture support and SQL-like Queries.

Created by: **Jacky the Code Bender** (Gravicode Studios).

## Key Features

1.  **Data Structures:** String, Hash, List, Set support (MVP).
2.  **TCP Server:** Built-in TCP server running on port 6379 main thread.
3.  **Client:** Helper class to send commands to the server.
4.  **Pub/Sub:** Support for publish/subscribe channel messaging.
5.  **SQL-Like Query:** `SELECT * FROM KEYS WHERE KEY LIKE '...'`
6.  **LINQ Support:** Direct memory access via LINQ commands if run in embedded mode.
7.  **Extensible:** Code structure ready for advanced data type development (Geo, Vector, TimeSeries).

## How to Run

1.  Ensure .NET SDK is installed (v6.0 or higher recommended).
2.  Open a terminal in the project folder.
3.  Run the command:
    ```bash
    dotnet run
    ```
4.  The program will run an automatic demo covering:
    - Starting the Server.
    - Performing SET/GET operations.
    - Performing List operations (LPUSH, LRANGE).
    - Executing SQL queries.
    - Demoing Pub/Sub.

## Command Protocol Example

The server accepts simple text commands separated by spaces:

- **SET key value**: Store a string.
- **GET key**: Retrieve a string.
- **HSET key field value**: Store a hash.
- **HGET key field**: Retrieve a hash.
- **LPUSH key value**: Add item to list (left).
- **LRANGE key start stop**: Get list range.
- **PUBLISH channel message**: Send message.
- **SUBSCRIBE channel**: Subscribe to channel.
- **SQL query**: Execute SQL query.

## Notes

This is an MVP (Minimum Viable Product) version. For production environments, it is recommended to use actual Redis. But for learning or lightweight embedded usage, MemSharp is ready to serve!

---
*Don't forget the phone credit treat if this code helps! - Jacky*
