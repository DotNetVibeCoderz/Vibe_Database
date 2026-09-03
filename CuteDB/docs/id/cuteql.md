# CuteQL

*[English →](../en/cuteql.md)*

CuteQL berbentuk SQL, karena siapa pun yang pernah menulis klausa `WHERE` sudah bisa membacanya. Ia
menyimpang dari SQL hanya di tempat yang memang harus, karena ini penyimpanan dokumen.

```sql
SELECT address.city AS kota, COUNT(*) AS pesanan, SUM(total) AS pendapatan
FROM   orders
WHERE  status != 'dibatalkan' AND placedAt >= '2026-01-01'
GROUP  BY address.city
HAVING COUNT(*) > 100
ORDER  BY pendapatan DESC
LIMIT  10
```

## Tiga perbedaan yang penting

Baca ini dulu. Selebihnya berperilaku persis seperti dugaan Anda.

### 1. Jalur field adalah warga kelas satu

`customer.address.city` adalah satu pengenal, bukan tiga token. `lines[0].sku` mengindeks ke dalam
larik. `lines[].sku` *memproyeksikan* ke seluruh larik — hasilnya adalah larik SKU dari setiap
baris, dan itulah yang membuat ini bekerja tanpa join:

```sql
SELECT code FROM orders WHERE lines[].sku = 'NR-KO-00042'
```

Sebuah pesanan cocok kalau **ada** salah satu barisnya yang ber-SKU itu.

### 2. Field yang berisi larik dicocokkan per elemen

```sql
WHERE tags = 'promo'          -- benar kalau larik tags memuat 'promo'
WHERE tags != 'promo'         -- benar kalau TIDAK ADA elemen yang 'promo'
WHERE tags = ['promo','baru'] -- larik lawan larik: perbandingan nilai utuh
```

Tanpa ini, indeks atas `tags` tidak akan berguna: indeksnya mencatat tiap elemen, mengembalikan
persis dokumen yang lariknya memuat nilai itu, lalu perbandingan larik utuh akan menolak semuanya.
Ini juga yang orang maksud ketika menulisnya.

### 3. "Tidak ada" bukan null

Field yang tidak pernah ditulis bernilai `MISSING`, dan itu nilai yang berbeda dari `NULL`:

```sql
WHERE barcode IS MISSING       -- field-nya memang tidak ada
WHERE barcode IS NULL          -- field-nya tidak ada ATAU bernilai null eksplisit
WHERE barcode IS NOT MISSING   -- field-nya ada, apa pun isinya
```

Membandingkan dengan field yang tidak ada menghasilkan *tidak diketahui*, bukan salah — jadi baris
tanpa `total` tidak muncul di `total > 0` maupun di `NOT (total > 0)`. Ini logika tiga nilai milik
SQL sendiri; hanya saja jauh lebih sering muncul di penyimpanan tanpa skema.

## Pernyataan

### SELECT

```sql
SELECT * FROM orders
SELECT code, customer.name AS pembeli, total FROM orders
SELECT DISTINCT channel FROM orders
SELECT *, total * 1.11 AS denganPajak FROM orders     -- * ditambah kolom terhitung
```

Urutan klausanya `SELECT … FROM … WHERE … GROUP BY … HAVING … ORDER BY … LIMIT … OFFSET`.

`ORDER BY` boleh menyebut alias proyeksi:

```sql
SELECT customer.name AS pembeli, SUM(total) AS belanja
FROM orders GROUP BY customer.name ORDER BY belanja DESC
```

SQL sendiri berselisih soal apakah itu boleh. Itu yang orang harapkan, dan kalau sebuah alias
bertabrakan dengan nama field sungguhan, aliasnya yang menang.

### INSERT

```sql
INSERT INTO orders VALUES
  { 'code': 'SO-9001', 'total': 125000, 'customer': { 'name': 'Rina' } },
  { 'code': 'SO-9002', 'total': 310000, 'tags': ['promo'] }
```

Literal objek, bukan daftar kolom — tidak ada kolom untuk didaftar. Kuncinya boleh dikutip atau
polos.

### UPDATE

```sql
UPDATE orders SET status = 'dikirim' WHERE code = 'SO-9001'
UPDATE orders SET total = total * 1.1, note = 'harga baru' WHERE address.city = 'Bandung'
UPDATE orders SET address.province = 'Jawa Barat' WHERE address.city = 'Bandung'
```

Yang terakhir menulis lewat jalur yang belum tentu ada; objek perantaranya dibuatkan.

### DELETE

```sql
DELETE FROM orders WHERE status = 'dibatalkan' AND total < 50000
DELETE FROM orders                                  -- mengosongkan koleksi
```

## Operator

| | |
| --- | --- |
| Perbandingan | `=` (atau `==`), `!=` (atau `<>`), `<`, `<=`, `>`, `>=` |
| Logika | `AND`, `OR`, `NOT` |
| Keanggotaan | `IN (…)`, `NOT IN (…)`, juga `IN ['a','b']` |
| Rentang | `BETWEEN … AND …`, `NOT BETWEEN … AND …` |
| Teks | `LIKE`, `NOT LIKE` — `%` sederet apa saja, `_` tepat satu, `\` untuk meloloskan |
| Keberadaan | `IS NULL`, `IS NOT NULL`, `IS MISSING`, `IS NOT MISSING` |
| Aritmetika | `+`, `-`, `*`, `/`, `%` |

`AND` dan `OR` memotong pendek, jadi taruh syarat yang murah dan selektif di depan.

Dua catatan soal aritmetika. `+` menyambung teks kalau salah satu sisinya string. Pembagian bilangan
bulat melebar, bukan memotong — `7 / 2` adalah `3.5`, karena bahasa kueri yang diam-diam membuang
sisa pembagian adalah bug laporan yang menunggu terjadi.

## Nilai

```sql
'teks'  'itu''nya'  "juga teks"    -- kutip tunggal digandakan; kutip ganda pakai escape \
42      -1       3.14   1.5e3
TRUE    FALSE    NULL   MISSING
['a', 'b', 3]                       -- literal larik
{ 'name': 'Sari', 'tier': 'gold' }  -- literal objek
```

Angka dibandingkan lintas representasi: `1`, `1L`, `1.0`, dan `1.0m` adalah satu nilai. Nilai
bertipe berbeda pun tetap punya urutan yang terdefinisi — missing < null < bool < angka < string <
biner < datetime < guid < id < larik < objek — sehingga mengurutkan field bercampur isi tetap
deterministik, bukan galat.

## Parameter

```csharp
db.Execute("SELECT * FROM orders WHERE address.city = @kota AND total > @minimum",
    ("kota",    CuteValue.String(masukan)),
    ("minimum", CuteValue.Decimal(500_000m)));
```

`@nama` dan `$nama` sama-sama bisa. Nilai yang diikat dipakai sebagai nilai dan tidak akan pernah
ditafsirkan ulang sebagai sintaks, yang menghapus pertanyaan soal injeksi alih-alih mencoba
meloloskannya.

`x IN @daftar` mengikat satu parameter yang berisi larik.

## Fungsi

**Agregat** — `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`.

`COUNT(*)` menghitung baris. Agregat lain mengabaikan baris yang argumennya tidak ada atau null,
dan itulah yang membuat `AVG` atas field jarang berarti sesuai harapan. `SUM` dan `AVG` menjaga
desimal tetap persis dan hanya melebar ke double kalau ada double yang terlibat.

**Teks** — `LENGTH` `UPPER` `LOWER` `TRIM` `SUBSTR` `CONCAT` `REPLACE` `SPLIT` `CONTAINS`
`STARTSWITH` `ENDSWITH`

**Angka** — `ABS` `ROUND` `FLOOR` `CEIL` `SQRT` `POW`

**Tanggal** — `NOW` `YEAR` `MONTH` `DAY` `HOUR` `DATE_PART` `DATE_TRUNC`

```sql
SELECT DATE_TRUNC('month', placedAt) AS bulan, SUM(total) AS pendapatan
FROM orders GROUP BY DATE_TRUNC('month', placedAt) ORDER BY bulan
```

**Nilai** — `COALESCE` `IFNULL` `TYPEOF` `TOSTRING` `TONUMBER` `TOINT` `EXISTS` `KEYS`
`ARRAY_LENGTH` `ELEMENT`

Fungsi yang diberi tipe salah mengembalikan `MISSING`, bukan melempar galat, jadi satu dokumen aneh
di antara sejuta tidak membatalkan kuerinya — barisnya sekadar tidak lolos predikat.

## Komentar

```sql
-- sampai akhir baris
/* atau satu blok */
```

## Yang tidak ada di CuteQL

- **Tidak ada join.** Penyimpanan dokumen menanamkan apa yang di penyimpanan relasional harus
  di-join. Kalau Anda benar-benar butuh join, yang Anda butuhkan adalah basis data relasional.
- **Tidak ada subkueri.**
- **Tidak ada `UNWIND`.** `lines[]` memproyeksikan larik di dalam sebuah ekspresi, tetapi tidak ada
  cara mengubah satu dokumen menjadi beberapa baris. Mengelompokkan menurut `lines[].name`
  mengelompokkan menurut *seluruh lariknya*, yang memang keranjang yang sah tetapi jarang yang Anda
  maksud.
- **Tidak ada transaksi lintas dokumen.** Satu penulisan bersifat atomik; tidak ada `BEGIN`/`COMMIT`.
- **Tidak ada DDL.** Koleksi dan indeks diatur lewat API atau lewat CLI.

## Galatnya menunjuk ke masalahnya

```
'~' does not belong in a query.
  SELECT * FROM orders WHERE total ~ 5
                                   ^
```

`CuteQueryException` membawa `Position`, jadi alat lain bisa menggarisbawahi sendiri karakter yang
bermasalah.

## Bagaimana sebuah kueri dijawab

Perencana memecah predikat menjadi suku-suku `AND` tingkat atas dan mencari yang berbentuk
`jalur.terindeks OP konstanta`, mendahulukan kesamaan pada indeks unik, lalu kesamaan biasa, lalu
rentang. Apa pun yang dihasilkan indeks tetap diperiksa ulang terhadap seluruh predikat, jadi
tebakan yang salah memakan waktu dan tidak pernah memakan kebenaran. Kalau sebuah "lompatan" akan
mengembalikan lebih dari separuh koleksi, ia ditinggalkan demi pemindaian.

Tanpa indeks yang bisa dipakai, ia memindai, dan pemindaian punya dua penerapan — akselerator Rust
ketika predikatnya bisa dikompilasi ke bytecode, penilai terkelola untuk selebihnya. Anda tidak
pernah memilih; Anda hanya bisa mengamati:

```csharp
var plan = db.Explain("SELECT * FROM orders WHERE code LIKE 'SO-2026%'");
// Collection scan: 50,000 scanned, 4,182 matched (native)
```

Lihat [arsitektur](arsitektur.md) untuk biaya dan alasannya.
