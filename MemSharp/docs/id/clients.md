# SDK klien

[English](../en/clients.md) · [Indeks dokumentasi](README.md)

MemSharp berbicara RESP2, jadi library klien Redis standar bisa dipakai untuk perintah yang ia
implementasikan. Tiga klien resmi ada di repositori ini — Python, Go dan Node.js — semuanya tanpa
dependensi dan masing-masing diuji terhadap server hidup di CI.

Jalankan server dulu:

```bash
memsharp serve --port 6380
```

## Rancangan bersama

Ketiga klien hanya berbeda di tempat bahasanya menuntut. Di tempat lain mereka sengaja sepakat,
sehingga menguasai satu berarti nyaris menguasai yang lain:

- **Tanpa dependensi.** RESP cukup sederhana sehingga klien yang menyeret pohon dependensi memakan
  biaya penggunanya lebih besar daripada yang ia hemat.
- **Key yang hilang bisa dibedakan dari yang kosong.** `None` di Python, `null` di Node, pointer
  `nil` atau `found bool` di Go.
- **`WRONGTYPE` mendapat tipenya sendiri.** Supaya Anda bisa menangkapnya secara spesifik alih-alih
  mencocokkan string.
- **`hgetall` dan sejenisnya mengembalikan map**, bukan array rata yang dibawa kabelnya.
- **`sql()` mengembalikan baris berkunci nama kolom.** Server mengirim namanya sebagai baris 0; setiap
  klien memasangkannya supaya Anda bekerja dengan nama, bukan posisi.
- **Pipelining mengembalikan error di posisinya** alih-alih melempar, jadi satu perintah yang gagal
  tidak menyembunyikan balasan yang lain.
- **Berlangganan mengambil alih koneksinya.** Server bisa mendorong pesan di antara sebuah permintaan
  dan balasannya, jadi klien yang berlangganan tidak bisa sekaligus menjalankan perintah biasa. Pakai
  klien kedua.

---

## Python

`clients/python/` · Python 3.10+

```bash
pip install ./clients/python
```

```python
from memsharp import MemSharpClient, WrongTypeError

with MemSharpClient(host="127.0.0.1", port=6380) as db:
    db.set("symbol:BTC", "68350.25")
    db.set("session:9f2", "kang", ex=1800)          # ex dalam detik
    print(db.get("symbol:BTC"))                     # "68350.25"
    print(db.mget("a", "hilang", "b"))              # ["1", None, "2"]

    db.incr("stats:fills", 3)
    print(db.ttl("session:9f2"))                    # 1799.9 atau None

    # koleksi
    db.rpush("feed", "a", "b", "c")
    db.ltrim("feed", 0, 99)
    db.hset("user:1", mapping={"name": "Kang Fadhil", "desk": "Jakarta"})
    print(db.hgetall("user:1"))                     # sebuah dict

    db.zadd("book:BTC:bids", {"bid-1": 68349.75, "bid-2": 68348.50})
    print(db.zrange("book:BTC:bids", 0, 9, desc=True, withscores=True))
    #   [("bid-1", 68349.75), ("bid-2", 68348.5)]

    # stream dan time series
    entry_id = db.xadd("trades", {"sym": "BTC", "qty": "5"}, maxlen=100_000)
    for stream_id, fields in db.xrange("trades"):
        print(stream_id, fields)

    db.ts_create("px", retention=100_000)
    db.ts_add("px", 68_350.25)
    print(db.ts_aggregate("px", 0, 10_000, 500, "max"))   # [(0, 4.0), ...]

    # query
    for row in db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'"):
        print(row["key"], row["size"])

    removed = db.sql_delete("DELETE FROM keys WHERE ttl < 60")

    # telusuri keyspace besar tanpa memateri semuanya
    for key in db.scan("user:*", count=500):
        pass

    # pipelining — satu round-trip untuk seluruh batch
    replies = db.pipeline([["SET", f"k{i}", str(i)] for i in range(1000)])

    try:
        db.get("feed")                              # feed adalah list
    except WrongTypeError as error:
        print(error.code)                           # "WRONGTYPE"

    print(db.info()["hit_rate"])
```

Pub/sub butuh koneksinya sendiri:

```python
import threading

def listen():
    with MemSharpClient(port=6380) as subscriber:
        for channel, message in subscriber.subscribe("fills.BTC"):
            print(channel, message)

threading.Thread(target=listen, daemon=True).start()
```

Jalankan suite-nya:

```bash
python clients/python/test_client.py     # 55 pemeriksaan terhadap server hidup
```

---

## Go

`clients/go/` · Go 1.22+

```bash
go get github.com/DotNetVibeCoderz/Vibe_Database/MemSharp/clients/go
```

```go
package main

import (
    "fmt"
    "log"
    "time"

    memsharp "github.com/DotNetVibeCoderz/Vibe_Database/MemSharp/clients/go"
)

func main() {
    db, err := memsharp.Dial("127.0.0.1:6380")
    if err != nil {
        log.Fatal(err)
    }
    defer db.Close()

    // string — found membedakan key yang hilang dari yang kosong
    db.Set("symbol:BTC", "68350.25")
    db.SetEx("session:9f2", "kang", 30*time.Minute)

    if value, found, err := db.Get("symbol:BTC"); err == nil && found {
        fmt.Println(value)
    }

    // MGet mempertahankan posisi; key yang hilang jadi pointer nil
    values, _ := db.MGet("a", "hilang", "b")
    for i, value := range values {
        if value == nil {
            fmt.Printf("%d: tidak ada\n", i)
        }
    }

    db.Incr("stats:fills", 3)
    if ttl, hasTTL, _ := db.TTL("session:9f2"); hasTTL {
        fmt.Println(ttl)
    }

    // koleksi
    db.RPush("feed", "a", "b", "c")
    db.LTrim("feed", 0, 99)
    db.HSet("user:1", map[string]string{"name": "Kang Fadhil"})

    db.ZAdd("book:BTC:bids",
        memsharp.ScoredMember{Member: "bid-1", Score: 68349.75},
        memsharp.ScoredMember{Member: "bid-2", Score: 68348.50})

    best, _ := db.ZRangeWithScores("book:BTC:bids", 0, 9, true)
    for _, member := range best {
        fmt.Printf("%s @ %.2f\n", member.Member, member.Score)
    }

    // stream
    db.XAdd("trades", map[string]string{"sym": "BTC", "qty": "5"}, 100_000)
    entries, _ := db.XRange("trades", "-", "+", 50)
    for _, entry := range entries {
        fmt.Println(entry.ID, entry.Fields["sym"])
    }

    // time series — TSAdd menerima timestamp eksplisit, TSAddNow pakai jam server
    db.TSCreate("px", 100_000)
    db.TSAddNow("px", 68_350.25)
    db.TSAdd("px", 68_351.00, time.Now().UnixMilli())
    buckets, _ := db.TSAggregate("px", 0, 10_000, 500, "max")

    // query — baris berkunci nama kolom
    rows, _ := db.SQL("SELECT key, size FROM keys WHERE key LIKE 'order:%'")
    for _, row := range rows {
        fmt.Println(row["key"], row["size"])
    }

    // telusuri keyspace besar
    db.Scan("user:*", 500, func(key string) error {
        return nil
    })

    // pipelining
    commands := make([][]any, 0, 1000)
    for i := 0; i < 1000; i++ {
        commands = append(commands, []any{"SET", fmt.Sprintf("k%d", i), i})
    }
    db.Pipeline(commands)

    // error membawa kodenya
    if _, _, err := db.Get("feed"); memsharp.IsWrongType(err) {
        fmt.Println("key itu bukan string")
    }
    _ = buckets
}
```

`Client` aman untuk pemakaian bersamaan — sebuah mutex menyerialkan round-trip-nya. **Untuk beban
paralel, beri setiap goroutine klien sendiri**, karena satu klien bersama akan membariskan mereka.

Pub/sub memblokir, jadi jalankan di koneksinya sendiri:

```go
subscriber, _ := memsharp.Dial("127.0.0.1:6380")
defer subscriber.Close()

go subscriber.Subscribe(func(message memsharp.Message) error {
    fmt.Println(message.Channel, message.Payload)
    return nil
}, "fills.BTC")
```

Dua catatan khas Go:

- **`TSAdd` tidak punya sentinel "sekarang".** `0` adalah timestamp Unix yang sah, dan mencurinya akan
  membuat epoch tak bisa ditulis — jadi `TSAddNow` adalah metode terpisah. Python dan Node
  mengungkapkan ini dengan `None`/`null`.
- **Error itu nilai dulu, baru error.** `readReply` mengembalikan balasan error sebagai nilai `*Error`
  supaya batch ber-pipeline bisa membawa satu kegagalan per posisi; `Do` menaikkannya ke return error
  Go.

Jalankan suite-nya:

```bash
cd clients/go && go test ./...     # melewati dengan bersih bila tak ada server berjalan
```

---

## Node.js

`clients/nodejs/` · Node 18+

```bash
npm install ./clients/nodejs
```

```javascript
const { MemSharpClient, WrongTypeError } = require('memsharp');

const db = new MemSharpClient({ host: '127.0.0.1', port: 6380 });
await db.connect();

// string
await db.set('symbol:BTC', '68350.25');
await db.set('session:9f2', 'kang', { ex: 1800 });      // ex dalam detik
console.log(await db.get('symbol:BTC'));
console.log(await db.mget('a', 'hilang', 'b'));         // ['1', null, '2']

await db.incr('stats:fills', 3);
console.log(await db.ttl('session:9f2'));               // 1799.9 atau null

// koleksi
await db.rpush('feed', 'a', 'b', 'c');
await db.ltrim('feed', 0, 99);
await db.hset('user:1', { name: 'Kang Fadhil', desk: 'Jakarta' });
console.log(await db.hgetall('user:1'));                // sebuah objek

await db.zadd('book:BTC:bids', { 'bid-1': 68349.75, 'bid-2': 68348.50 });
console.log(await db.zrange('book:BTC:bids', 0, 9, { desc: true, withScores: true }));
//   [{ member: 'bid-1', score: 68349.75 }, ...]

// stream dan time series
await db.xadd('trades', { sym: 'BTC', qty: '5' }, { maxLen: 100_000 });
for (const { id, fields } of await db.xrange('trades')) {
  console.log(id, fields.sym);
}

await db.tsCreate('px', 100_000);
await db.tsAdd('px', 68_350.25);
console.log(await db.tsAggregate('px', 0, 10_000, 500, 'max'));
//   [{ timestamp: 0, value: 4 }, ...]

// query — baris sebagai objek
for (const row of await db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%'")) {
  console.log(row.key, row.size);
}

// scan adalah async iterator
for await (const key of db.scan('user:*', 500)) {
  // ...
}

// pipelining
const batch = Array.from({ length: 1000 }, (_, i) => ['SET', `k${i}`, String(i)]);
await db.pipeline(batch);

try {
  await db.get('feed');
} catch (error) {
  if (error instanceof WrongTypeError) console.log(error.code);   // 'WRONGTYPE'
}

await db.close();
```

Pub/sub berupa `EventEmitter`, di koneksinya sendiri:

```javascript
const subscriber = new MemSharpClient({ port: 6380 });
await subscriber.connect();

subscriber.on('message', (channel, message, pattern) => {
  console.log(channel, message, pattern);
});

await subscriber.subscribe('fills.BTC');
await subscriber.psubscribe('fills.*');
```

Jalankan suite-nya:

```bash
node clients/nodejs/test/client.test.js     # 53 pemeriksaan terhadap server hidup
```

---

## Memakai klien Redis saja

Tidak ada yang menghalangi. RESP2 tetap RESP2:

```python
import redis
db = redis.Redis(port=6380, decode_responses=True)
db.set("k", "v")
db.zadd("book", {"bid-1": 68349.75})
```

Dua batasan. Pertama, hanya perintah yang MemSharp implementasikan yang akan bekerja — lihat
[server.md](server.md#perintah-yang-didukung). Kedua, `TS.*` dan `SQL` bukan perintah Redis, jadi
Anda menjangkaunya lewat antarmuka mentah:

```python
db.execute_command("TS.ADD", "px", "*", 68350.25)
db.execute_command("SQL", "SELECT key FROM keys LIMIT 10")
```

Klien resmi membungkus keduanya dengan semestinya, dan itulah sebagian besar alasan keberadaannya.

## Menulis klien sendiri

Anda butuh kira-kira dua ratus baris: encode perintah sebagai array RESP berisi bulk string, decode
lima tipe balasan, dan buffer secara bertahap supaya balasan yang terpecah antar paket tetap
ter-parse. `clients/python/memsharp/_resp.py` adalah yang terpendek dari ketiganya untuk dibaca
sebagai rujukan.

Tiga hal yang perlu dilakukan dengan benar, dan ketiga klien di sini mendemonstrasikan semuanya:

1. **Decode secara bertahap.** Sebuah balasan bisa datang berkeping-keping, dan batch ber-pipeline
   datang sebagai beberapa balasan dalam satu bacaan. Buffer apa yang belum bisa Anda parse lalu coba
   lagi.
2. **Jangan mengorelasikan lewat id.** RESP tidak punya — balasan ke-N milik permintaan ke-N. Queue
   berisi resolver yang menunggu adalah struktur yang benar.
3. **Saring pesan dorongan saat berlangganan.** Pesan itu tidak menjawab permintaan apa pun, jadi
   mencocokkannya dengan queue yang menunggu akan memberi perintah setelahnya pesan milik orang lain
   dan mengacaukan semua yang sesudahnya.
