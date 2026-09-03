# Bahasa query

[English](../en/query-language.md) · [Indeks dokumentasi](README.md)

MemSharp bisa di-query dengan dialek SQL kecil, atau dengan LINQ. Keduanya menelusuri hal yang sama:
**keyspace itu sendiri**, satu baris per key.

Ini penjelajah keyspace, bukan engine relasional. Tidak ada join, tidak ada agregat, dan tidak ada
proyeksi atas elemen di dalam sebuah koleksi. Berpura-pura sebaliknya justru desain yang lebih
menyesatkan — lihat [yang sengaja tidak bisa dilakukannya](#yang-sengaja-tidak-bisa-dilakukannya).

## Satu tabelnya

`keys` punya satu baris per key hidup dan lima kolom:

| Kolom | Tipe | Arti |
|---|---|---|
| `key` | teks | Nama key-nya |
| `type` | teks | `String`, `List`, `Hash`, `Set`, `SortedSet`, `TimeSeries` atau `Stream` |
| `size` | angka | Panjang string, atau jumlah elemen untuk koleksi |
| `ttl` | angka | Masa hidup tersisa dalam detik; `null` untuk key permanen |
| `value` | teks | Nilainya, hanya untuk key `String`; `null` untuk lainnya |

Alias: `len` dan `length` untuk `size`, `val` untuk `value`.

## Tata bahasa

```
SELECT (* | kolom [, kolom]...) FROM KEYS
  [WHERE kondisi]
  [ORDER BY kolom [ASC | DESC]]
  [LIMIT n [OFFSET m]]

DELETE FROM KEYS [WHERE kondisi]

kondisi     := term [(AND | OR) term]...
term        := NOT term | '(' kondisi ')' | perbandingan
perbandingan:= kolom (= | != | <> | < | <= | > | >=) literal
             | kolom [NOT] LIKE pola
             | kolom IN '(' literal [, literal]... ')'
```

Kata kunci dan nama kolom tidak peka huruf besar-kecil. Literal string memakai kutip tunggal atau
ganda; gandakan kutipnya atau escape dengan backslash untuk menyertakan satu.

## Contoh

```csharp
// key order yang terbesar
db.ExecuteSql("SELECT key, size FROM keys WHERE key LIKE 'order:%' ORDER BY size DESC LIMIT 10");

// apa yang segera kedaluwarsa
db.ExecuteSql("SELECT key, ttl FROM keys WHERE ttl < 300 ORDER BY ttl");

// koleksi mana yang besar
db.ExecuteSql(@"SELECT key, type, size FROM keys
                WHERE type IN ('Hash', 'List', 'SortedSet') AND size > 1000
                ORDER BY size DESC");

// pengelompokan, paging, negasi
db.ExecuteSql(@"SELECT key FROM keys
                WHERE (type = 'String' OR type = 'Hash') AND NOT key LIKE 'tmp:%'
                ORDER BY key LIMIT 50 OFFSET 100");

// bersih-bersih
int removed = db.ExecuteSql("DELETE FROM keys WHERE key LIKE 'session:%' AND ttl < 60").Affected;
```

Dari CLI:

```
memsharp> SQL SELECT key, type FROM keys WHERE size > 100
memsharp> .sql SELECT key, type FROM keys WHERE size > 100
```

`.sql` merender tabel dengan nama kolom sungguhan; perintah `SQL` biasa mengembalikan balasan RESP
mentah, yaitu apa yang dilihat klien jarak jauh.

## Membaca hasilnya

```csharp
QueryResult result = db.ExecuteSql("SELECT key, size FROM keys LIMIT 5");

foreach (string?[] row in result.Rows)
{
    string key = row[0]!;
    string size = row[1]!;      // setiap sel adalah teks; null berarti NULL SQL
}

result.Columns;    // ["key", "size"]
result.Count;      // baris yang dikembalikan
result.Affected;   // baris yang dihapus, untuk DELETE
```

Sel selalu string, atau `null`. Engine tidak tahu tipe apa yang Anda maksudkan untuk sebuah nilai,
dan menciptakannya berarti menebak.

## Dua perilaku yang perlu diketahui

### Kolom numerik dibandingkan secara numerik

`size` dan `ttl` dibandingkan sebagai angka, bukan teks. Tanpa ini, `size > 9` akan menempatkan
`"10"` di bawah `"9"` dan diam-diam mengembalikan baris yang salah — justru jenis jawaban salah yang
tak bersuara yang membuat lapisan query tak bisa dipercaya.

### Key permanen mengurut paling akhir menurut TTL

`ORDER BY ttl` menempatkan key yang punya masa hidup lebih dulu dan key permanen di akhir. "Tidak
pernah kedaluwarsa" adalah masa hidup tersisa yang terbesar yang ada; mengurutkannya sebagai nol akan
menempatkan key permanen paling depan, yang terbaca sebagai kebalikan dari maknanya.

## Pushdown pola key

`key LIKE '...'` atau `key = '...'` di tingkat teratas didorong ke dalam pemindaian, jadi query hanya
menyentuh key yang cocok, bukan menelusuri seluruh keyspace.

```csharp
// didorong — hanya mengunjungi key yang berawalan "order:"
db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:%' AND size > 10");

// tidak didorong — penelusuran penuh, karena cabang OR bisa menerima baris yang pola ini tolak
db.ExecuteSql("SELECT key FROM keys WHERE key LIKE 'order:%' OR type = 'Hash'");
```

Aturannya: pola key memenuhi syarat hanya bila ia terjangkau lewat `AND` semata. Di bawah `OR`, baris
yang ditolak cabang ini masih bisa diterima cabang lainnya, jadi mempersempit pemindaian akan
diam-diam menjatuhkan baris. Perencananya berhenti di `OR` pertama, bukan turun ke dalamnya.

Anda bisa melihat rencananya:

```csharp
var query = SqlParser.Parse("SELECT key FROM keys WHERE type = 'Hash' AND key LIKE 'user:%'");
query.KeyPattern;   // "user:*"
```

Pada database 100.000 key ini kira-kira selisih antara 0,4 ms dan 9 ms — lihat
[benchmarks.md](benchmarks.md).

## Menggunakan ulang rencana

Parsing murah tapi tidak gratis. Untuk query yang Anda jalankan berulang kali, parse sekali:

```csharp
var plan = SqlParser.Parse("SELECT key, size FROM keys WHERE key LIKE 'order:%' LIMIT 100");

for (;;)
{
    var result = db.Execute(plan);
    // ...
}
```

## Menangani galat sintaks

```csharp
if (!SqlParser.TryParse(userInput, out var query, out string? error))
{
    Console.WriteLine($"tidak bisa di-parse: {error}");
    return;
}

var result = db.Execute(query!);
```

`ExecuteSql` melempar `MemSharpCommandException` alih-alih itu, dengan pesan yang menyebut posisi dan
apa yang diharapkan:

```
syntax error: unknown column 'name'; the columns are key, type, size, ttl and value
syntax error: expected a comparison operator, found end of query
syntax error: the only table is KEYS, found 'users'
```

## LINQ

`Query()` menghasilkan satu `KeyInfo` per key hidup. Ini seringnya alat yang lebih baik: Anda dapat
tipe, IntelliSense, dan seluruh LINQ.

```csharp
var expiringHashes = db.Query()
    .Where(k => k.Type == MemType.Hash && k.ExpiresAt is not null)
    .OrderBy(k => k.ExpiresAt)
    .Take(20)
    .ToList();

var bytesByType = db.Query()
    .GroupBy(k => k.Type)
    .Select(g => new { Type = g.Key, Keys = g.Count(), Size = g.Sum(k => k.Size) })
    .OrderByDescending(x => x.Size);

// pasangkan penelusuran dengan baca sungguhan di tempat Anda butuh isinya
var biggestHashes = db.Query()
    .Where(k => k.Type == MemType.Hash)
    .OrderByDescending(k => k.Size)
    .Take(5)
    .Select(k => (k.Key, Fields: db.HashGetAll(k.Key)));
```

```csharp
public readonly record struct KeyInfo(
    string Key,
    MemType Type,
    long Size,
    DateTimeOffset? ExpiresAt,
    string? StringValue);
```

**`Query()` aman terhadap tulisan bersamaan.** Ia menyalin metadata satu shard pada satu waktu di
bawah lock shard itu lalu menghasilkan dari salinannya, jadi predikat Anda tidak pernah berjalan
sambil lock dipegang dan penulis bersamaan tidak pernah muncul sebagai exception
collection-was-modified.

**Ia bukan pandangan point-in-time.** Tulisan ke shard yang lebih belakang bisa mendarat setelah
shard yang lebih depan disalin. Lihat [architecture.md](architecture.md#yang-bukan-atomik).

## Mengapa bukan `IQueryable`

`Query()` mengembalikan `IEnumerable<KeyInfo>`, jadi operator LINQ berjalan di memori atas metadatanya
alih-alih diterjemahkan menjadi operasi engine.

Penyedia `IQueryable` akan memungkinkan Anda menulis
`db.AsQueryable().Where(k => k.Key.StartsWith("order:"))` dan membuatnya menjadi pemindaian yang
dipersempit. Ia juga akan memungkinkan Anda menulis seratus ekspresi yang tidak bisa ia terjemahkan,
masing-masing akan melempar saat runtime atau diam-diam jatuh ke penelusuran penuh yang sama.
Lapisan SQL menangani satu kasus yang layak dioptimalkan — pola key — dan melakukannya secara
terlihat.

## Yang sengaja tidak bisa dilakukannya

| Tidak didukung | Alasannya, dan apa yang bisa dipakai |
|---|---|
| `JOIN` | Cuma ada satu tabel. Baca sendiri key-key terkaitnya. |
| `COUNT`, `SUM`, `GROUP BY` | Pakai LINQ atas `Query()`, yang melakukan semuanya dengan tipe. |
| `INSERT`, `UPDATE` | Pakai API bertipe — `Set`, `HashSet`, `SortedSetAdd`. |
| Query ke dalam koleksi | `WHERE` melihat *ukuran* sebuah hash, bukan field-nya. Baca hash-nya. |
| Subquery, `UNION`, `HAVING` | Susun di C#. |
| `DELETE` dengan `ORDER BY` atau `LIMIT` | Ditolak saat parse, karena delete terurut sebagian hampir tidak pernah yang dimaksudkan seseorang. |
