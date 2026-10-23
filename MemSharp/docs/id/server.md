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

`PING` · `ECHO` · `QUIT` · `DBSIZE` · `FLUSHDB` · `INFO` (dengan bagian) · `COMMAND` ·
`CONFIG GET` · `CONFIG RESETSTAT` · `MEMORY USAGE`

### Persistensi

`SAVE` · `BGSAVE` · `LASTSAVE`

### Keyspace

`DEL` · `EXISTS` · `TYPE` · `KEYS` · `SCAN` · `RENAME` · `RANDOMKEY` · `EXPIRE` · `PEXPIRE` ·
`PEXPIREAT` · `TTL` · `PTTL` · `PERSIST`

### String

`SET` (dengan `EX`, `PX`, `NX`) · `SETNX` · `GET` · `GETSET` · `MGET` · `MSET` · `INCR` · `DECR` ·
`INCRBY` · `DECRBY` · `INCRBYFLOAT` · `APPEND` · `STRLEN` · `GETRANGE` · `SETRANGE`

Panjang dan offset dalam byte UTF-8. Rentang yang akan membelah karakter multi-byte menjadi error,
bukan byte parsial — lihat [data-types.md](data-types.md#string).

### List

`LPUSH` · `RPUSH` · `LPOP` · `RPOP` · `LRANGE` · `LLEN` · `LINDEX` · `LSET` · `LTRIM` · `LREM` ·
`RPOPLPUSH` · `LPOS` (dengan `RANK`, `COUNT`, `MAXLEN`)

### Hash

`HSET` · `HGET` · `HMGET` · `HGETALL` · `HDEL` · `HEXISTS` · `HLEN` · `HKEYS` · `HVALS` ·
`HINCRBY` · `HINCRBYFLOAT`

### Set

`SADD` · `SREM` · `SMEMBERS` · `SISMEMBER` · `SCARD` · `SPOP` · `SINTER` · `SUNION` · `SDIFF` ·
`SINTERSTORE` · `SUNIONSTORE` · `SDIFFSTORE`

### Sorted set

`ZADD` · `ZREM` · `ZSCORE` · `ZINCRBY` · `ZCARD` · `ZRANK` · `ZREVRANK` · `ZRANGE` · `ZREVRANGE` ·
`ZRANGEBYSCORE` · `ZREVRANGEBYSCORE` · `ZCOUNT` · `ZREMRANGEBYSCORE` · `ZUNIONSTORE` · `ZINTERSTORE`
(dengan `WEIGHTS` dan `AGGREGATE SUM|MIN|MAX`)

Batas score menerima `-inf` dan `+inf`. `WITHSCORES` menyisipkan anggota dan score bergantian.

### Stream

`XADD` (dengan `MAXLEN`) · `XLEN` · `XRANGE` (dengan `COUNT`) · `XREVRANGE` · `XTRIM` · `XREAD` (dengan
`COUNT`, `BLOCK`)

Consumer group: `XGROUP CREATE|DESTROY|CREATECONSUMER|DELCONSUMER|SETID` · `XREADGROUP` (dengan
`COUNT`, `BLOCK`, `NOACK`) · `XACK` · `XPENDING` (ringkasan dan diperluas, dengan `IDLE`) · `XCLAIM` (dengan
`IDLE`, `TIME`, `RETRYCOUNT`, `FORCE`, `JUSTID`, `LASTID`) · `XAUTOCLAIM` · `XINFO STREAM|GROUPS|CONSUMERS`

`-` dan `+` berarti ujung-ujung stream. `*` meminta server membuat id-nya. Di `XREAD` dan `XGROUP`,
`$` berarti "setelah entri terakhir saat ini"; di `XREADGROUP`, `>` berarti "entri yang belum
diantarkan group ini".

### Time series

`TS.CREATE` (dengan `RETENTION`) · `TS.ADD` · `TS.RANGE` · `TS.AGGREGATE` · `TS.LEN`

`TS.AGGREGATE key dari sampai bucketMs (avg|min|max|sum|count|first|last)`.

### Pub/sub

`PUBLISH` · `SUBSCRIBE` · `PSUBSCRIBE` · `UNSUBSCRIBE` · `PUNSUBSCRIBE`

### Query

`SQL <query>` — lihat [query-language.md](query-language.md).

`COMMAND` mendaftar semua yang didukung server yang sedang berjalan — 114 perintah di 1.1 — dan itulah
jawaban otoritatif untuk versi yang Anda punya.

### Bacaan blocking

`XREAD` dan `XREADGROUP` menerima `BLOCK milidetik`, dengan `0` berarti "tunggu selamanya". Ketika
bacaan tidak menemukan apa pun, koneksinya diparkir sampai `XADD` berikutnya di mana pun dalam
database atau sampai timeout, yang membalas nil. Selama diparkir ia tidak membaca apa pun lagi dari
socket-nya — kontrak Redis: perintah berikutnya dari klien yang terblokir menunggu gilirannya — tetapi
balasan yang sudah terutang untuk perintah sebelumnya dalam batch ber-pipeline yang sama dikirim lebih
dulu.

Dua detail disengaja. `$` dalam `XREAD` yang memblok dipatok ke id terakhir saat bacaan dimulai; bila
dievaluasi ulang setelah bangun, ia akan berarti id terakhir yang *baru*, dan klien akan tertidur
melewati persis entri yang membangunkannya. Dan koneksi yang diparkir bangun setidaknya sekali per
detik untuk memeriksa apakah kliennya sudah memutus, jadi `BLOCK 0` yang ditinggalkan tidak memegang
koneksi sepanjang hidup server. `INFO` melaporkan koneksi yang diparkir sebagai `blocked_clients`.

Blocking hanya fitur server. Tabel perintahnya sinkron — tabel itu juga yang memutar ulang log
append-only — jadi pemanggil embedded dan pemutaran ulang selalu mendapat jawaban yang tidak memblok.

## Perbedaan dari Redis

| | MemSharp |
|---|---|
| Kursor `SCAN` | Tahan-rehash seperti di Redis, tetapi nilainya milik MemSharp sendiri: indeks shard di 32 bit atas dan ambang hash di 32 bit bawah. Perlakukan sebagai opaque. Tidak bertahan melewati restart. |
| `XADD MAXLEN ~` | Bentuk aproksimasi `~` diterima dan diperlakukan **tepat**. |
| `GETRANGE` / `SETRANGE` | Rentang yang akan membelah karakter UTF-8 multi-byte menjadi error. Redis akan mengembalikan atau menulis byte parsialnya. |
| `CONFIG SET` | Ditolak. MemSharp dikonfigurasi lewat `MemDbOptions` saat konstruksi; `CONFIG GET` melaporkan opsi-opsi itu. |
| `MEMORY USAGE` | Perkiraan dari tata letak objek CLR, bukan angka persis dari allocator. |
| `INFO` `redis_version` | Melaporkan `6.2.0`, rilis Redis yang permukaan perintahnya paling mirip dengan MemSharp, jadi klien yang memilih fitur berdasarkan versi tetap berada di dalamnya. Ini tingkat kompatibilitas, bukan klaim sebagai Redis; `memsharp_version` adalah versi sebenarnya. |
| Stream | Tanpa `XDEL` atau `XSETID`; tanpa pelacakan lag atau `entries-read` di `XINFO`. `XAUTOCLAIM` membalas dengan bentuk tiga elemen Redis 7 (berikut id yang terhapus), meskipun `redis_version` menyebut 6.2. |
| `SELECT` | Tidak ada. Satu keyspace per server. |
| `MULTI` / `EXEC` | Tidak ada. Operasi satu key bersifat atomik. |
| `AUTH` | Tidak ada. |
| RESP3 / `HELLO` | Tidak ada. Hanya RESP2. |
| Notifikasi keyspace | Tidak ada; pakai `PUBLISH` sendiri. |
| `SINTER` dll. | Bukan pandangan point-in-time lintas key. Lihat [architecture.md](architecture.md#yang-bukan-atomik). Varian `*STORE` *memang* point-in-time. |

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
INFO [section ...]
```

`INFO` memakai nama bagian dan field milik Redis — `server`, `clients`, `memory`, `persistence`,
`stats`, `replication`, `cpu`, `keyspace` — jadi dashboard dan exporter yang sudah ada bisa
membacanya. Minta satu bagian atau lebih untuk mendapatkan hanya bagian itu. Nama field 1.0
(`product`, `keys`, `uptime_seconds`, `commands_processed` dan lainnya) masih ada, karena klien 1.0
yang sudah dipublikasikan mem-parse-nya. Diringkas, dari server sungguhan:

```
# Server
redis_version:6.2.0
memsharp_version:1.1.0
redis_mode:standalone
runtime:.NET 10.0.12
uptime_in_seconds:151
product:MemSharp
vendor:Gravicode Studios

# Clients
connected_clients:1
blocked_clients:0

# Memory
used_memory:1783344
used_memory_human:1.70M
used_memory_rss:40140800
maxmemory:0
mem_allocator:dotnet-server-gc

# Persistence
rdb_changes_since_last_save:0
rdb_last_save_time:0
aof_enabled:0

# Stats
total_connections_received:5
total_commands_processed:69
keyspace_hits:0
keyspace_misses:0
expired_keys:0

# Keyspace
db0:keys=14,expires=0,avg_ttl=0
keys:14
shards:32
```

`CONFIG GET pattern` menjawab parameter yang ditanyakan perkakas pemantauan — `maxmemory`, `save`,
`appendonly`, `appendfsync`, `dir`, `dbfilename`, `databases`, plus `shards` milik MemSharp — dari
opsi yang dipakai saat database dibangun. `CONFIG RESETSTAT` mengenolkan counter-nya. `MEMORY USAGE key
[SAMPLES n]` memperkirakan jejak memori sebuah key (lihat [data-types.md](data-types.md#operasi-keyspace)).

Secara embedded, counter yang sama ada di `db.Statistics`. Masing-masing memakan satu penambahan
interlocked, dan itulah sebabnya ia aktif secara bawaan; setel `EnableStatistics = false` untuk
mematikannya.

Membacanya bukan snapshot yang konsisten — counter dibaca satu per satu. Itu cukup untuk pemantauan
dan salah untuk pembukuan.

`memsharp serve` merender angka yang sama sebagai dashboard hidup, disegarkan dua kali per detik.
