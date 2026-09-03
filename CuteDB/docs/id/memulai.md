# Memulai

*[English →](../en/getting-started.md)*

CuteDB adalah basis data dokumen tertanam: berjalan di dalam proses Anda, menyimpan dokumen
berbentuk JSON, dan menyimpannya ke satu berkas. Tidak ada yang perlu dipasang, disetel, atau
dijalankan terpisah.

## Pasang

```bash
dotnet add package CuteDB
```

Butuh .NET 10. Paketnya membawa akselerator pemindaian native untuk enam platform desktop dan server
yang umum; di platform lain akselerator itu memang tidak ada dan mesin terkelola yang mengambil
alih. Kode yang Anda tulis tidak bergantung pada yang mana yang berjalan.

## Basis data pertama Anda

```csharp
using CuteDB;

using var db = CuteDatabase.Open("toko.cute");
var orders = db.Collection("orders");

var id = orders.Insert(CuteDocument.Parse("""
    {
      "code": "SO-0001",
      "customer": { "name": "Sari Wijaya", "tier": "gold" },
      "address":  { "city": "Bandung", "country": "ID" },
      "lines":    [ { "sku": "KB-01", "qty": 2, "lineTotal": 189000 } ],
      "total":    189000,
      "status":   "diproses"
    }
    """));

CuteDocument? found = orders.FindById(id);
Console.WriteLine(found?["customer"]["name"].AsString);   // Sari Wijaya
```

Koleksinya tidak perlu dibuat lebih dulu dan dokumennya tidak perlu skema. `_id` diberikan saat
disisipkan dan ikut kembali bersama dokumennya.

Untuk uji dan coba-coba, pakai basis data di memori — mesin yang sama, tanpa berkas:

```csharp
using var db = CuteDatabase.CreateInMemory();
```

## Menyusun dokumen lewat kode

`CuteDocument.Parse` enak dipakai untuk literal. Untuk data yang sudah Anda punya, susun dokumennya
langsung — menghindari perjalanan bolak-balik lewat JSON dan menjaga `decimal` tetap persis:

```csharp
var order = new CuteDocument()
    .Set("code", "SO-0002")
    .Set("total", CuteValue.Decimal(249_000m))
    .Set("placedAt", CuteValue.DateTime(DateTime.UtcNow))
    .Set("customer", CuteValue.Object(new CuteObject()
        .Set("name", "Budi Santoso")
        .Set("tier", "silver")))
    .Set("tags", CuteValue.ArrayOf(
        CuteValue.String("promo"),
        CuteValue.String("grosir")));

orders.Insert(order);
```

`decimal`, `DateTime`, `Guid`, dan `CuteId` disimpan apa adanya. Total rupiah yang persis di program
Anda tetap persis di basis data dan tetap persis saat dibaca kembali.

## Mengkueri

Dua jalan masuk, keduanya ke mesin yang sama.

**Saringan, kalau yang Anda mau adalah dokumennya:**

```csharp
var besar = orders.Find("address.city = 'Bandung' AND total > 500000", limit: 50);
var satu = orders.FindOne("code = 'SO-0001'");
var jumlah = orders.CountWhere("status = 'dibatalkan'");
```

**CuteQL, kalau yang Anda mau adalah hasil berbentuk tertentu:**

```csharp
var hasil = db.Execute("""
    SELECT address.city AS kota, COUNT(*) AS pesanan, SUM(total) AS pendapatan
    FROM   orders
    WHERE  status != 'dibatalkan'
    GROUP  BY address.city
    ORDER  BY pendapatan DESC
    LIMIT  10
    """);

foreach (var row in hasil.Rows)
{
    Console.WriteLine($"{row["kota"].AsString,-16} {row["pendapatan"].AsDecimal,15:N0}");
}
```

`hasil.Columns` ditemukan dari barisnya, karena koleksi tidak punya skema untuk mendeklarasikannya.
`hasil.Duration` dan `hasil.Plan` memberi tahu berapa biayanya dan bagaimana barisnya ditemukan.

Bahasanya selengkapnya ada di [rujukan CuteQL](cuteql.md).

### Selalu ikat masukan pengguna

```csharp
// Benar: nilainya tidak akan pernah ditafsirkan ulang sebagai sintaks.
db.Execute("SELECT * FROM orders WHERE customer.name = @nama",
    ("nama", CuteValue.String(apaPunYangDiketikPengguna)));

// Salah.
db.Execute($"SELECT * FROM orders WHERE customer.name = '{apaPunYangDiketikPengguna}'");
```

## Muat massal

`InsertMany` bukan perulangan di sekitar `Insert`. Kuncinya diambil sekali, bukan sekali per
dokumen, dan lognya dibiarkan tertahan sampai selesai:

```csharp
IEnumerable<CuteDocument> masuk = BacaDariManaPun();   // tetap malas / lazy
int tersisip = orders.InsertMany(masuk);
```

Di mesin uji, itu selisih antara sekitar 40.000 dan 390.000 dokumen per detik. Karena urutannya
tetap malas, muatan yang lebih besar dari memori mengalir lewat, bukan diwujudkan lebih dulu.

## Indeks

Indeks mengubah pemindaian menjadi lompatan. Buat satu untuk jalur yang sering Anda saring:

```csharp
orders.CreateIndex("address.city");                          // dinamai sesuai jalurnya
orders.CreateIndex("code", name: "orders_code", unique: true);
```

Dua perilaku yang perlu diketahui:

- **Jarang / sparse.** Dokumen yang jalur terindeksnya tidak ada sama sekali tidak diindeks, jadi
  indeks unik tidak menganggap dua dokumen yang sama-sama tidak punya field itu sebagai tabrakan.
- **Sadar larik.** Jalur yang berisi larik diindeks satu kali per elemen, jadi indeks atas `tags`
  membuat `WHERE tags = 'promo'` menjadi lompatan.

Periksa apakah indeksnya benar-benar dipakai:

```csharp
var plan = db.Explain("SELECT * FROM orders WHERE address.city = 'Bandung'");
Console.WriteLine(plan);
// Index seek on 'address.city': 2,944 candidates, 2,944 matched
```

Indeks memakan memori dan memperlambat penulisan. Tambahkan kalau sebuah rencana berkata
`Collection scan` pada kueri yang sering Anda jalankan — bukan sebelum itu.

## Ketahanan

Penulisan masuk ke log yang hanya bertambah. Seberapa keras tiap penulisan bekerja adalah pilihan:

```csharp
// Tercepat. Kehilangan ekor yang tertahan kalau prosesnya dimatikan. Cocok untuk cache dan impor.
using var cepat = CuteDatabase.Open("cache.cute", CuteDatabaseOptions.Fast);

// Bawaan. Selamat kalau prosesnya dimatikan; bukan kalau listrik padam.
using var db = CuteDatabase.Open("toko.cute");

// Selamat dari listrik padam, dengan biaya sekitar dua orde besaran per penulisan.
using var aman = CuteDatabase.Open("buku-besar.cute", CuteDatabaseOptions.Safest);
```

Pemulihan berjalan otomatis dan tidak butuh mode apa pun: satu bingkai entah mendarat utuh atau
dibuang.

```csharp
if (db.DiscardedBytesOnOpen > 0)
{
    logger.LogWarning("Pulih dari penulisan yang terputus; {Bytes} bita dibuang.",
        db.DiscardedBytesOnOpen);
}
```

## Menjaga berkas tetap kecil

Setiap pembaruan menambah; tidak ada yang diubah di tempat. Dokumen yang diperbarui seribu kali
punya seribu bingkai di belakangnya. `Compact()` menulis ulang berkas hanya dengan keadaan terkini:

```csharp
var stats = db.Stats();
if (stats.FileAmplification > 3)
{
    long dihemat = db.Compact();
}
```

`FileAmplification` adalah ukuran berkas dibagi data hidup. Sekitar 1 berarti tidak ada yang bisa
diklaim kembali; jauh di atas 2 berarti sebagian besar berkasnya adalah riwayat. Memori dipadatkan
otomatis sambil jalan; berkasnya tidak, karena menulis ulang berkas adalah keputusan soal I/O yang
menjadi hak Anda.

## Threading

`CuteDatabase` aman dipakai banyak thread. Pembacaan berjalan bersamaan; penulisan diserialkan
terhadap satu sama lain dan terhadap pembaca. Satu `CuteDatabase` per berkas per proses, dipakai
bersama — jangan membuka berkas yang sama dua kali.

## Selanjutnya

- [LINQ](linq.md) — kueri bertipe, dan `ToCuteQL()` untuk melihat statement yang dijalankan
- [Rujukan CuteQL](cuteql.md) — seluruh bahasanya, termasuk tiga tempat yang sengaja berbeda dari SQL
- [Arsitektur](arsitektur.md) — kenapa membaca satu field dari dokumen tersimpan 66× lebih murah
  daripada mendekodenya
- [Baris perintah](cli.md) — `cutedb shell`, impor, ekspor, tolok ukur
- [Server & klien](server-dan-klien.md) — menjangkau basis data CuteDB dari Python, Go, atau Node.js
