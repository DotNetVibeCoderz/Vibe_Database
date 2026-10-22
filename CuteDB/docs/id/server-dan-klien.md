# Server dan klien

*[English →](../en/server-and-clients.md)*

CuteDB adalah basis data tertanam, jadi klien Python, Go, dan Node.js berbicara dengan server HTTP
kecil yang membungkusnya. Itu pertukaran yang disengaja: satu titik akhir HTTP jauh lebih kecil
untuk dijaga kebenarannya lintas tiga bahasa dan enam platform daripada tiga set binding native,
dan lompatan jaringannya tidak berarti dibanding kerja yang dilakukan kebanyakan panggilan.

## Menjalankan servernya

```bash
dotnet tool install -g CuteDB.Server
cutedb-server toko.cute
```

```
  cutedb-server  CuteDB 2.0.0 · format v2 · scanner: cutedb_core 2.0.0 (win-x64)
  database       /home/kang/toko.cute
  listening      http://127.0.0.1:8420
  api key        not required (bind to localhost or set --api-key)
  mode           read-write, durability flush
  describe       http://127.0.0.1:8420/openapi.json
```

| | |
| --- | --- |
| `--host <alamat>` | antarmuka yang diikat, bawaan `127.0.0.1` |
| `-p, --port <porta>` | bawaan `8420` |
| `--api-key <kunci>` | wajibkan sebagai `X-API-Key` atau bearer token; juga dibaca dari `CUTEDB_API_KEY` |
| `--cors <asal>` | daftar asal (dipisah koma) yang boleh dari peramban |
| `--read-only` | tolak semua penulisan |
| `--durability buffered\|flush\|fsync` | |
| `-q, --quiet` | tanpa log permintaan |

### Sebelum diekspos

Server mengikat ke loopback dan tidak mewajibkan kunci secara bawaan, yang tepat untuk pengembangan
lokal dan salah untuk selain itu. Sebelum ia bisa dijangkau dari mesin lain:

- **Setel `--api-key`.** Perbandingannya berwaktu tetap, jadi kuncinya tidak bocor lewat latensi
  jawaban.
- **Taruh TLS di depannya.** Servernya berbicara HTTP polos; terminasikan TLS di reverse proxy.
- **Daftarkan asal Anda.** `--cors` tidak pernah mengizinkan asal mana pun — API basis data yang
  bisa dipanggil halaman mana pun dari peramban yang sudah login adalah API yang menunggu
  disalahgunakan. Preflight dari asal yang terdaftar dijawab tanpa kunci, karena preflight adalah
  peramban yang bertanya apakah ia boleh mengirim kunci.
- **Pertimbangkan `--read-only`** kalau konsumennya hanya membaca.

Satu proses memiliki berkasnya. Jangan arahkan dua server ke basis data yang sama.

## API-nya

Dideskripsikan di `/openapi.json`. Dokumen diteruskan apa adanya, ditulis dan diurai oleh JSON milik
CuteDB sendiri, bukan oleh serialiser umum, jadi desimalnya tetap persis dan tanggalnya tetap
bertipe.

| | |
| --- | --- |
| `GET /health` | uji hidup; tidak pernah butuh kunci |
| `GET /v1/collections` | daftar koleksi beserta ukurannya |
| `GET /v1/collections/{c}` | statistik dan indeks satu koleksi |
| `DELETE /v1/collections/{c}` | buang koleksinya |
| `GET /v1/collections/{c}/documents` | telusuri berhalaman, `?filter=&limit=&offset=` |
| `POST /v1/collections/{c}/documents` | sisipkan satu objek, atau banyak dari sebuah larik |
| `GET\|PUT\|PATCH\|DELETE /v1/collections/{c}/documents/{id}` | satu dokumen |
| `POST /v1/query` | jalankan CuteQL; bisa berhalaman, atau dialirkan sebagai NDJSON |
| `POST /v1/explain` | bagaimana sebuah kueri akan dijalankan |
| `POST /v1/collections/{c}/indexes` | buat indeks |
| `DELETE /v1/collections/{c}/indexes/{nama}` | buang indeks |
| `GET /v1/stats` | total basis data |
| `POST /v1/compact` | klaim kembali ruang |

**Sisipkan larik, jangan perulangan.** `POST` dengan larik JSON menerapkan seluruh kelompok di bawah
satu kunci dan satu flush — selisih antara satu flush dan sepuluh ribu.

**`PATCH` menggabung dangkal, dan kunci bertitik adalah jalur.** `{"address.city": "Bandung"}`
menjangkau ke dalam subdokumen; `{"address": {…}}` menggantinya. Keduanya berguna dan tidak satu pun
bisa diungkapkan oleh yang lain.

### Membagi kueri per halaman

Beri `POST /v1/query` sebuah `limit` dan ia mengembalikan satu halaman dari sebuah `SELECT` beserta
kursor untuk halaman berikutnya:

```json
{"query": "SELECT code, total FROM orders ORDER BY code", "limit": 100}
```

```json
{"kind":"select","columns":["code","total"],"rows":[…100 baris…],"affected":1000,
 "durationMs":8.9,"plan":"Full collection: 1000 scanned, 1000 matched",
 "page":{"offset":0,"limit":100,"returned":100,"total":1000,"next":"MToxMDA6YzhhOTJi…"}}
```

Kirim `query` dan `parameters` yang sama dengan `"cursor": "<next>"` untuk halaman berikutnya;
`next` bernilai `null` di halaman terakhir. `offset` sebagai ganti `cursor` langsung melompat ke
sebuah baris. `limit` bernilai 1–10.000 (bawaan 100 begitu ada salah satu bidang halaman); membagi
halaman sebuah penulisan ditolak sebelum penulisannya dijalankan. Tanpa `limit`, `offset`, atau
`cursor`, jawabannya persis seperti selama ini — tanpa `page`.

Pahami apa yang didapat. Mesinnya membangun hasil secara utuh sebelum ada yang melihat satu baris
pun, jadi **setiap halaman menjalankan ulang pernyataannya** dan mengembalikan satu potongannya:
pembagian halaman membatasi jawabannya — yang diserialisasi, dikirim, dan diurai — bukan kerja
kuerinya. Halaman adalah offset, jadi penulisan yang terjadi di antara dua permintaan bisa
menggeser baris melewati batas halaman; urutkan dengan sesuatu yang stabil bila itu penting.
`columns` dan `affected` menggambarkan seluruh hasil, jadi setiap halaman punya kolom yang sama.

### Mengalirkan sebagai NDJSON

Kirim `Accept: application/x-ndjson` ke `POST /v1/query` atau `GET …/documents` dan barisnya kembali
satu objek JSON per baris, ditulis dan di-flush selagi diserialisasi alih-alih dirakit menjadi satu
dokumen dulu. Badannya hanya berisi baris; sisanya lewat header — `X-CuteDB-Kind`, `-Affected`,
`-Duration-Ms`, `-Plan`, dan untuk permintaan berhalaman `-Total` serta `-Next-Cursor`.

```bash
curl -sN http://127.0.0.1:8420/v1/query -H 'Accept: application/x-ndjson' \
  -H 'Content-Type: application/json' -d '{"query":"SELECT * FROM orders"}' | wc -l
```

Galat — kueri yang salah, kursor yang salah — tetap berupa galat JSON dengan status 4xx, karena
semua yang bisa gagal sudah gagal sebelum baris pertama ditulis. Ketiga klien mengirim
`Accept: application/json`, jadi tetap menerima objek JSON.

Galat berupa JSON dengan `error` yang bisa dibaca mesin dan `message` yang ditulis untuk manusia:

```json
{"error":"invalid_query","message":"'~' does not belong in a query.\n  SELECT * FROM orders WHERE total ~ 5\n                                   ^"}
```

---

## Python

```bash
pip install cutedb          # atau: pip install -e clients/python
```

Hanya pustaka standar — tanpa dependensi, selamanya.

```python
from decimal import Decimal
from cutedb import CuteClient, CuteQueryError

with CuteClient("http://127.0.0.1:8420", api_key="rahasia") as db:
    orders = db.collection("orders")

    # Decimal dikodekan dengan digit persisnya, bukan lewat float.
    orders.insert({"customer": "Sari", "total": Decimal("249000.00")})

    ids = orders.insert_many([{"n": i} for i in range(10_000)])   # satu permintaan

    hasil = db.query(
        "SELECT address.city AS kota, SUM(total) AS pendapatan "
        "FROM orders WHERE status = @status GROUP BY address.city",
        {"status": "selesai"},
    )

    for row in hasil:                    # hasilnya bisa diiterasi
        print(row["kota"], row["pendapatan"])

    print(hasil.plan, hasil.duration_ms)

    try:
        db.query("SELECT * FROM orders WHERE total ~ 5")
    except CuteQueryError as error:
        print(error)                      # termasuk baris tanda sisipan
```

`get`, `delete`, dan `drop_index` mengembalikan `None`/`False` untuk sasaran yang tidak ada, bukan
melempar galat — "memang tidak ada" itu jawaban, bukan kesalahan.

## Go

```bash
go get github.com/DotNetVibeCoderz/Vibe_Database/CuteDB/clients/go
```

Hanya pustaka standar. Setiap metode menerima `context.Context`.

```go
client := cutedb.New("http://127.0.0.1:8420", cutedb.WithAPIKey("rahasia"))
orders := client.Collection("orders")

if _, err := orders.Insert(ctx, cutedb.Document{
    "customer": map[string]any{"name": "Sari", "tier": "gold"},
    "total":    249000,
}); err != nil {
    log.Fatal(err)
}

hasil, err := client.Query(ctx,
    "SELECT address.city AS kota, SUM(total) AS pendapatan FROM orders GROUP BY address.city",
    nil)

// Koleksi tidak punya skema, jadi dokumennya map[string]any. Dekode ke struct kalau bentuknya
// sudah diketahui.
type Order struct {
    Code  string  `json:"code"`
    Total float64 `json:"total"`
}

var order Order
if err := cutedb.Decode(hasil.Rows[0], &order); err != nil { /* … */ }

// "Tidak ada" bukan galat.
document, err := orders.Get(ctx, id)      // document == nil, err == nil kalau tidak ada
if cutedb.IsQueryError(err) { /* CuteQL-nya salah */ }
```

## Node.js

```bash
npm install cutedb
```

ESM, Node 18+, tanpa dependensi. Dikirim sebagai sumber yang bisa dibaca dengan deklarasi tipe yang
ditulis tangan, jadi tidak ada langkah build antara yang Anda baca dan yang berjalan.

```javascript
import { CuteClient, CuteError } from "cutedb";

const db = new CuteClient("http://127.0.0.1:8420", { apiKey: "rahasia" });
const orders = db.collection("orders");

await orders.insert({ customer: { name: "Sari" }, total: 249000 });
await orders.insertMany(batch);                     // satu permintaan

const hasil = await db.query(
  "SELECT address.city AS kota, SUM(total) AS pendapatan FROM orders GROUP BY address.city"
);

for (const row of hasil.rows) console.log(row.kota, row.pendapatan);

console.log(await orders.get(idYangTidakAda));      // null, bukan lemparan galat
console.log(await orders.count("total > 500000"));

try {
  await db.query("SELECT nope(");
} catch (error) {
  if (error instanceof CuteError && error.isQueryError) console.error(error.message);
}
```

## Pembangun kueri

Menyambung filter dengan tangan adalah cara bug injeksi ditulis. Ketiga klien punya pembangun kueri
dengan bentuk yang sama seperti LINQ di sisi .NET: susun kuerinya, tanyakan CuteQL apa yang
dihasilkannya, lalu jalankan.

```python
from cutedb import field

q = (orders.query()
     .where(field("address.city") == kota, field("total") > 500_000)
     .select("code", "total", pembeli="customer.name")
     .order_by("total", desc=True)
     .limit(10))

q.to_cuteql()  # SELECT code, total, customer.name AS pembeli FROM orders
               #   WHERE address.city = @p0 AND total > @p1 ORDER BY total DESC LIMIT 10
q.parameters   # {'p0': 'Bandung', 'p1': 500000}
q.run()        # atau db.query(q)
```

```go
q := orders.Query().
    Where(cutedb.Field("address.city").Eq(kota), cutedb.Field("total").Gt(500000)).
    Select("code", "total").SelectAs("customer.name", "pembeli").
    OrderByDesc("total").
    Limit(10)

text, params, err := q.Build()   // atau q.ToCuteQL()
hasil, err := q.Run(ctx)         // atau client.Run(ctx, cutedb.From("orders")…)
```

```javascript
import { field } from "cutedb";

const q = orders.query()
  .where(field("address.city").eq(kota), field("total").gt(500_000))
  .select("code", "total", { pembeli: "customer.name" })
  .orderBy("total", "desc")
  .limit(10);

q.toCuteQL();      // teks yang sama, karakter demi karakter
q.parameters;      // { p0: "Bandung", p1: 500000 }
await q.run();     // atau db.query(q)
```

Tersedia `= != < <= > >=`, `AND`/`OR`/`NOT` (operator `&` `|` `~` di Python, `and`/`or`/`not` di
Node, `And`/`Or`/`Not` di Go), `IN`/`NOT IN`, `BETWEEN`, `LIKE` mentah, `startswith`/`endswith`/
`contains`, `IS [NOT] NULL`, `IS [NOT] MISSING`, path bersarang `a.b`, `lines[0].sku` dan
`lines[].sku`, proyeksi dengan alias, `ORDER BY`, `LIMIT`/`OFFSET`. Beberapa `where` digabung
dengan `AND`.

Dua aturan membuatnya aman:

- **Nilai tidak pernah masuk ke teks.** Setiap nilai menjadi `@p0`, `@p1`, … dan dikirim di
  `parameters`, yang diikat server sebagai nilai. Tidak ada escaping yang bisa salah karena memang
  tidak ada yang di-escape. Tanggal dikirim sebagai `{"$date": "…Z"}` dalam UTC.
- **Nama diperiksa terhadap lexer.** Path field, alias, dan nama koleksi *adalah* bagian dari teks,
  jadi harus terbaca sebagai tepat satu identifier: huruf ASCII, angka, dan `_`, disambung `.`,
  dengan `[n]` atau `[]` setelah segmen, dan bukan kata kunci tunggal seperti `order` atau `count`.
  Selain itu ditolak, bukan dikutip — CuteQL tidak punya identifier berkutip. Hanya ASCII dengan
  sengaja: lexer menerima huruf Unicode apa pun, tetapi tiap runtime membawa tabel Unicode-nya
  sendiri, dan ketiga klien harus sepakat untuk setiap masukan.

`startswith`, `endswith`, dan `contains` meng-escape `\`, `%`, dan `_` pada teks pengguna persis
seperti `StartsWith` di provider LINQ, jadi kode produk `100%` cocok dengan dirinya sendiri, bukan
menjadi wildcard. `like` menerima pola mentah. Membandingkan dengan `None`/`nil`/`null` lewat
`=`/`!=` menjadi `IS NULL`/`IS NOT NULL`, karena `= NULL` tidak pernah benar.

Ketiganya merender teks dan parameter yang sama persis, dan itu dibuktikan, bukan diasumsikan:
`clients/querybuilder-golden.json` berisi kasus bersama, setiap suite klien mencocokkannya, dan
`ClientGoldenTests` di suite .NET mem-parse setiap pernyataan dengan parser sungguhan lalu
menjalankannya terhadap data uji. Set `CUTEDB_TEST_SERVER` agar suite klien juga menjalankan semua
kasus terhadap server yang hidup.

---

## Apa pun yang berbicara HTTP

```bash
curl -s http://127.0.0.1:8420/v1/query \
  -H 'Content-Type: application/json' \
  -d '{"query":"SELECT address.city AS kota, COUNT(*) AS n FROM orders GROUP BY address.city",
       "parameters":{}}'
```

Dokumen OpenAPI di `/openapi.json` sudah cukup untuk generator kalau Anda lebih suka tidak menulis
klien dengan tangan.
