# Server dan protokol

[English](../en/server.md) · [Indeks dokumentasi](README.md)

MemSharp berbicara **RESP2**, protokol kabel Redis. `redis-cli` dan library klien Redis standar bisa
dipakai untuk perintah yang MemSharp implementasikan.

## Menjalankan server

```csharp
using var db = new MemDb();
await using var server = new MemServer(db, new MemServerOptions
{
    Address = IPAddress.Loopback,
    Port = 6380,
    MaxConnections = 10_000,
    Backlog = 512,
    NoDelay = true,
});

await server.StartAsync();
Console.WriteLine($"mendengarkan di {server.EndPoint}");
```

Atau dari command line:

```bash
memsharp serve --port 6380 --data app.msnap --sync auto --aof
```

`StartAsync` kembali segera setelah listener siap, jadi Anda bisa membaca `EndPoint` — berguna ketika
`Port = 0` dan OS yang memilih portnya, seperti yang dilakukan tes dan benchmark.

Server dan database berbagi satu objek: proses Anda dan kliennya melihat data yang sama.

## Keamanan

**MemSharp tanpa autentikasi dan tanpa TLS.**

`Address` bawaannya `IPAddress.Loopback`, bukan `Any`. Itu disengaja: bawaan "semua antarmuka" akan
menempatkan database tanpa autentikasi di jaringan begitu seseorang menjalankan contohnya. CLI
memperingatkan bila Anda mengikat lebih luas:

```
warning: bound beyond loopback and MemSharp has no authentication -
anyone who can reach this port has full access
```

Kalau Anda perlu menjangkaunya dari mesin lain, letakkan di balik sesuatu yang menangani autentikasi
dan keamanan transport — tunnel SSH, service mesh, atau reverse proxy di jaringan privat.

## Port 6380, bukan 6379

Satu di atas Redis, jadi keduanya bisa berjalan bersebelahan dan `redis-cli` yang tersesat tidak
menyambung ke yang salah tanpa sengaja.

## Perintah yang didukung

### Koneksi dan server

`PING` · `ECHO` · `QUIT` · `DBSIZE` · `FLUSHDB` · `INFO` · `COMMAND`

### Persistensi

`SAVE` · `BGSAVE` · `LASTSAVE`

### Keyspace

`DEL` · `EXISTS` · `TYPE` · `KEYS` · `SCAN` · `RENAME` · `RANDOMKEY` · `EXPIRE` · `PEXPIRE` ·
`PEXPIREAT` · `TTL` · `PTTL` · `PERSIST`

### String

`SET` (dengan `EX`, `PX`, `NX`) · `SETNX` · `GET` · `GETSET` · `MGET` · `MSET` · `INCR` · `DECR` ·
`INCRBY` · `DECRBY` · `INCRBYFLOAT` · `APPEND` · `STRLEN`

### List

`LPUSH` · `RPUSH` · `LPOP` · `RPOP` · `LRANGE` · `LLEN` · `LINDEX` · `LSET` · `LTRIM` · `LREM` ·
`RPOPLPUSH`

### Hash

`HSET` · `HGET` · `HMGET` · `HGETALL` · `HDEL` · `HEXISTS` · `HLEN` · `HKEYS` · `HVALS` ·
`HINCRBY` · `HINCRBYFLOAT`

### Set

`SADD` · `SREM` · `SMEMBERS` · `SISMEMBER` · `SCARD` · `SPOP` · `SINTER` · `SUNION` · `SDIFF`

### Sorted set

`ZADD` · `ZREM` · `ZSCORE` · `ZINCRBY` · `ZCARD` · `ZRANK` · `ZREVRANK` · `ZRANGE` · `ZREVRANGE` ·
`ZRANGEBYSCORE` · `ZREVRANGEBYSCORE` · `ZCOUNT` · `ZREMRANGEBYSCORE`

Batas score menerima `-inf` dan `+inf`. `WITHSCORES` menyisipkan anggota dan score bergantian.

### Stream

`XADD` (dengan `MAXLEN`) · `XLEN` · `XRANGE` (dengan `COUNT`) · `XREVRANGE` · `XTRIM`

`-` dan `+` berarti ujung-ujung stream. `*` meminta server membuat id-nya.

### Time series

`TS.CREATE` (dengan `RETENTION`) · `TS.ADD` · `TS.RANGE` · `TS.AGGREGATE` · `TS.LEN`

`TS.AGGREGATE key dari sampai bucketMs (avg|min|max|sum|count|first|last)`.

### Pub/sub

`PUBLISH` · `SUBSCRIBE` · `PSUBSCRIBE` · `UNSUBSCRIBE` · `PUNSUBSCRIBE`

### Query

`SQL <query>` — lihat [query-language.md](query-language.md).

`COMMAND` mendaftar semua yang didukung server yang sedang berjalan, dan itulah jawaban otoritatif
untuk versi yang Anda punya.

## Perbedaan dari Redis

| | MemSharp |
|---|---|
| Kursor `SCAN` | Offset ke dalam urutan pemindaian yang stabil, bukan kursor tahan-rehash. Kontrak "ulangi sampai kursornya 0" tetap berlaku; key yang ditambahkan di tengah iterasi bisa terlewat. |
| `XADD MAXLEN ~` | Bentuk aproksimasi `~` diterima dan diperlakukan **tepat**. |
| `SELECT` | Tidak ada. Satu keyspace per server. |
| `MULTI` / `EXEC` | Tidak ada. Operasi satu key bersifat atomik. |
| `AUTH` | Tidak ada. |
| RESP3 / `HELLO` | Tidak ada. Hanya RESP2. |
| Notifikasi keyspace | Tidak ada; pakai `PUBLISH` sendiri. |
| `SINTER` dll. | Bukan pandangan point-in-time lintas key. Lihat [architecture.md](architecture.md#yang-bukan-atomik). |

## Pipelining

Hal paling efektif yang bisa Anda lakukan untuk throughput. Satu tulisan, satu bacaan, N perintah:

| | Tanpa pipelining | Pipeline ×16 |
|---|---:|---:|
| `SET` | 47,1R ops/s | **394R ops/s** |
| `GET` | 43,1R ops/s | **470R ops/s** |
| `PING` | 50,2R ops/s | **1,09J ops/s** |

Kira-kira 10×, karena ia menghapus satu round-trip per perintah. Server mengeksekusi satu batch penuh
dari satu bacaan socket dan menulis setiap balasan dalam satu tulisan.

```csharp
await using var client = new MemClient();
await client.ConnectAsync("127.0.0.1", 6380);

var batch = Enumerable.Range(0, 1000)
    .Select(i => new[] { "SET", $"key:{i}", i.ToString() })
    .ToList();

RespValue[] replies = await client.PipelineAsync(batch);
```

Semua SDK klien di sini mendukungnya: `pipeline()` di Python dan Node, `Pipeline()` di Go.

## Klien .NET

```csharp
await using var client = new MemClient();
await client.ConnectAsync("127.0.0.1", 6380);

var reply = await client.ExecuteAsync("SET", "k", "v");
var value = await client.ExecuteAsync("GET", "k");

Console.WriteLine(value.Text);              // "v"
Console.WriteLine(value.Kind);              // RespKind.BulkString
Console.WriteLine(value.ToDisplayString()); // versi yang mudah dibaca
```

Satu koneksi, dipakai berurutan. Perintah dari beberapa thread diserialkan di dalamnya, karena
balasan RESP datang dalam urutan permintaan dan menyisipkan dua permintaan akan memberi satu
pemanggil balasan pemanggil lain. **Untuk beban paralel, beri setiap worker klien sendiri** — dan
itulah yang dilakukan benchmark.

### Berlangganan

```csharp
await using var subscriber = new MemClient();
await subscriber.ConnectAsync("127.0.0.1", 6380);

await foreach (var message in subscriber.SubscribeAsync("fills.BTC", cancellationToken))
{
    Console.WriteLine($"{message.Channel}: {message.Message}");
}
```

Berlangganan mengambil alih koneksinya: server bisa mendorong pesan di antara sebuah permintaan dan
balasannya, jadi klien yang berlangganan tidak bisa sekaligus menjalankan perintah biasa. Pakai klien
kedua.

## Rincian protokol

### Membaca

Loop koneksinya dibangun di atas `System.IO.Pipelines`. Parser mengambil apa yang bisa diambil dari
apa pun yang sudah datang dan meninggalkan sisanya, sehingga:

- perintah yang terpecah antar segmen TCP di-parse lagi begitu byte sisanya mendarat;
- batch ber-pipeline dieksekusi seluruhnya dari satu bacaan.

Perintah inline juga diterima — `SET a b\r\n` biasa tanpa pembungkus RESP — jadi Anda bisa
mengendalikan server dari netcat:

```bash
printf 'SET greeting "hello world"\r\nGET greeting\r\n' | nc 127.0.0.1 6380
```

### Pengaman

| Batas | Nilai |
|---|---|
| Argumen maksimum dalam satu perintah | 1.048.576 |
| Bulk string maksimum | 512 MB |
| Koneksi maksimum | `MaxConnections`, bawaan 10.000 |

Prefiks panjang yang jahat ditolak, bukan menjadi alokasi bergigabyte. Pada plafon koneksi server
menjawab `-ERR max number of clients reached` lalu menutup, alih-alih membiarkan klien menunggu
accept yang tidak akan datang.

### Galat

Balasan galat berbentuk `-KODE pesan`. Kodenya adalah token pertama dan merupakan bagian dari
kontrak:

| Kode | Arti |
|---|---|
| `WRONGTYPE` | Operasi menemui key bertipe lain |
| `ERR` | Selain itu — arity salah, nilai salah bentuk, perintah tak dikenal |

Galat tidak menutup koneksi. SDK klien mengubah `WRONGTYPE` menjadi tipe exception tersendiri supaya
Anda bisa menangkapnya secara spesifik.

## Pemantauan

```
INFO
```

```
# Server
product:MemSharp
vendor:Gravicode Studios
version:1.0.0.0

# Keyspace
keys:1048576
shards:64

# Stats
uptime_seconds:3600
commands_processed:19283746
connections_accepted:42
keyspace_hits:18000000
keyspace_misses:1283746
hit_rate:0.9334
writes:5000000
expired_keys:120394
pubsub_messages:88000

# Persistence
last_save:1788434080
pending_changes:2841
```

Secara embedded, counter yang sama ada di `db.Statistics`. Masing-masing memakan satu penambahan
interlocked, dan itulah sebabnya ia aktif secara bawaan; setel `EnableStatistics = false` untuk
mematikannya.

Membacanya bukan snapshot yang konsisten — counter dibaca satu per satu. Itu cukup untuk pemantauan
dan salah untuk pembukuan.

`memsharp serve` merender angka yang sama sebagai dashboard hidup, disegarkan dua kali per detik.
