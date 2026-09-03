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
  disalahgunakan.
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
| `POST /v1/query` | jalankan CuteQL |
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
