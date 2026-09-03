# LINQ

*Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.*

Provider LINQ CuteDB menerjemahkan expression tree menjadi **satu statement CuteQL** lalu
menjalankannya di mesin database. Penyaringan, pengurutan, pengelompokan, agregasi, dan paging
semuanya terjadi di dalam database. Tidak ada data yang diambil lalu dibuang.

Dan Anda selalu bisa melihat hasil terjemahannya:

```csharp
Console.WriteLine(query.ToCuteQL());
```

Satu method itulah alasan halaman ini ada. Provider yang tidak bisa Anda lihat isinya adalah
provider yang tidak bisa Anda debug — jadi setiap query bisa mencetak statement yang akan
dijalankannya, berupa teks yang bisa langsung Anda tempel ke `cutedb shell`.

---

## Mendapatkan queryable

```csharp
using CuteDB;
using CuteDB.Linq;

using var db = CuteDatabase.Open("shop.cute");
var orders = db.Collection("orders");

IQueryable<Order> query = orders.Query<Order>();
```

`Query<T>()` tidak butuh registrasi maupun skema. `T` adalah kelas apa pun dengan konstruktor
tanpa parameter dan properti yang bisa di-set.

```csharp
public sealed class Order
{
    public CuteId Id { get; set; }              // id dokumen, disimpan sebagai _id
    public string Code { get; set; } = "";
    public Buyer Customer { get; set; } = new();
    public Address Address { get; set; } = new();
    public List<Line> Lines { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public decimal Total { get; set; }
    public bool Paid { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime PlacedAt { get; set; }
    public string? Note { get; set; }
}
```

### Penamaan

Properti dipetakan ke nama field camelCase secara default, karena dokumen berbentuk JSON dan itulah
yang dikirim setiap penghasil JSON di seberang sana. `PlacedAt` membaca `placedAt`; `Address.City`
membaca `address.city`.

| Atribut | Efek |
| --- | --- |
| `[CuteField("total_amount")]` | Memetakan satu properti ke nama field yang persis |
| `[CuteIgnore]` | Menghilangkan properti dari dokumen |
| `[CuteId]` | Menandai primary key. Properti bernama `Id` bertipe `CuteId` atau `string` sudah dikenali tanpa ini |
| `[CuteNaming(CuteNamingPolicy.SnakeCase)]` | Menetapkan kebijakan penamaan untuk satu tipe |

`CuteNamingPolicy` bernilai `CamelCase` (default), `Exact`, atau `SnakeCase`. Ubah default seluruh
aplikasi lewat `CuteMapper.DefaultNaming`, atau per query lewat
`orders.Query<Order>(CuteNamingPolicy.SnakeCase)`.

---

## Melihat query yang dihasilkan

### `ToCuteQL()`

```csharp
var query = orders.Query<Order>()
    .Where(o => o.Address.City == "Bandung" && o.Total > 500_000m)
    .OrderByDescending(o => o.Total)
    .Take(10);

query.ToCuteQL();
// SELECT * FROM orders WHERE address.city = 'Bandung' AND total > 500000 ORDER BY total DESC LIMIT 10
```

`ToCuteQL(indented: true)` menaruh setiap klausa di barisnya sendiri, lebih enak dibaca di log:

```
SELECT *
FROM orders
WHERE address.city = 'Bandung' AND total > 500000
ORDER BY total DESC
LIMIT 10
```

Keluarannya sengaja dibuat **bisa di-parse ulang**: dijalankan kembali lewat
`CuteParser.ParseStatement` menghasilkan statement yang setara. Output debug yang tidak bisa
di-parse adalah output debug yang berbohong soal apa yang benar-benar jalan — jadi hal ini diuji.

`ToString()` pada sebuah query melakukan hal yang sama, sehingga queryable menampilkan CuteQL-nya di
jendela watch debugger tanpa Anda minta.

### `ToCuteQLStatement()`

Mengembalikan `SelectStatement` hasil parse, bukan teks, untuk tooling yang butuh pohonnya.

### `ExplainCuteQL()`

Bagaimana mesin akan *menemukan* barisnya, tanpa memateralisasi satu pun:

```csharp
var plan = query.ExplainCuteQL();
// Index seek on 'orders_city': 2,944 candidates, 2,944 matched
```

Angka yang perlu diperhatikan adalah candidates dibanding matched. Scan yang memeriksa sejuta
dokumen untuk mengembalikan sebelas baris adalah scan yang butuh index.

### `ToListWithDiagnostics()`

Hasil beserta biayanya, dalam satu panggilan:

```csharp
var (rows, diagnostics) = query.ToListWithDiagnostics();

Console.WriteLine(diagnostics.CuteQL);
Console.WriteLine(diagnostics);   // 11 rows · 4.52 ms · Index seek on 'orders_city'
```

---

## Yang bisa diterjemahkan

Semua di bagian ini berjalan di mesin database. Tiap contoh menunjukkan CuteQL yang dihasilkannya.

### Penyaringan

```csharp
.Where(o => o.Address.City == "Medan" && o.Total > 500_000m)
// WHERE address.city = 'Medan' AND total > 500000
```

`Where` berantai digabung dengan `AND`. Variabel yang ter-capture dievaluasi saat penerjemahan dan
dikirim sebagai nilai, bukan sebagai sintaks:

```csharp
var city = "Medan";
var floor = 500_000m;
.Where(o => o.Address.City == city && o.Total > floor)
// WHERE address.city = 'Medan' AND total > 500000
```

Kelompok `OR` tetap berkurung, begitu pula aritmetika:

```csharp
.Where(o => (o.Address.City == "Medan" || o.Address.City == "Jakarta") && o.Total > 100_000m)
// WHERE (address.city = 'Medan' OR address.city = 'Jakarta') AND total > 100000

.Where(o => (o.Total + 1000m) * 2m > 600_000m)
// WHERE (total + 1000) * 2 > 600000
```

### Null dan missing

`== null` menjadi `IS NULL`, bukan `= NULL` — yang terakhir bernilai *unknown* untuk setiap baris,
dan itu tidak pernah menjadi pertanyaan yang dimaksud.

```csharp
.Where(o => o.Note == null)     // WHERE note IS NULL
.Where(o => o.Note != null)     // WHERE note IS NOT NULL
```

CuteQL membedakan *null* dari *missing*; lihat [referensi CuteQL](cuteql.md). Untuk menanyakan yang
missing, tulis langsung dalam CuteQL.

### String

| C# | CuteQL |
| --- | --- |
| `o.Code.StartsWith("SO-00")` | `code LIKE 'SO-00%'` |
| `o.Code.EndsWith("3")` | `code LIKE '%3'` |
| `o.Customer.Name.Contains("ar")` | `customer.name LIKE '%ar%'` |
| `o.Code.ToUpper()` | `UPPER(code)` |
| `o.Code.ToLower()` | `LOWER(code)` |
| `o.Code.Trim()` | `TRIM(code)` |
| `o.Code.Substring(0, 2)` | `SUBSTR(code, 0, 2)` |
| `o.Code.Replace("-", "")` | `REPLACE(code, '-', '')` |
| `string.IsNullOrEmpty(o.Note)` | `note IS NULL OR LENGTH(note) = 0` |
| `o.Code.Length` | `LENGTH(code)` |
| `string.Concat(a, b)` | `CONCAT(a, b)` |

Karakter `%` dan `_` di dalam teks pencarian Anda di-escape, sehingga kode produk yang mengandung
`50%` cocok dengan dirinya sendiri, bukan berlaku sebagai wildcard:

```csharp
.Where(o => o.Code.Contains("50%"))
// WHERE code LIKE '%50\%%'
```

### Tanggal

```csharp
.Where(o => o.PlacedAt.Year == 2026 && o.PlacedAt.Month == 3)
// WHERE YEAR(placedAt) = 2026 AND MONTH(placedAt) = 3
```

`Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `DayOfYear`, `DayOfWeek`, dan `.Date` semuanya
dipetakan ke fungsi.

### Angka

`Math.Abs`, `Round`, `Floor`, `Ceiling`, `Sqrt`, dan `Pow` dipetakan ke padanan CuteQL-nya. `+`,
`-`, `*`, `/`, dan `%` diterjemahkan langsung dan mempertahankan presedensi C#.

### Enum

Enum disimpan dan dibandingkan **berdasarkan nama**, bukan ordinal — dokumen yang berisi `"Shipped"`
tetap berarti sama setelah seseorang menyisipkan anggota baru di tengah enum.

```csharp
.Where(o => o.Status == OrderStatus.Shipped)
// WHERE status = 'Shipped'
```

### Keanggotaan

`Contains` atas **koleksi lokal** menjadi `IN`:

```csharp
var cities = new[] { "Bandung", "Medan" };
.Where(o => cities.Contains(o.Address.City))
// WHERE address.city IN ('Bandung', 'Medan')
```

Himpunan kosong tidak cocok dengan apa pun, alih-alih menghasilkan sintaks tak valid.

`Contains` atas **field array tersimpan** bersifat element-wise, karena begitulah CuteQL
membandingkan field array:

```csharp
.Where(o => o.Tags.Contains("promo"))
// WHERE tags = 'promo'
```

### Masuk ke array subdokumen

`Any` dengan predikat menjadi projecting path. `lines[].qty` menunjuk ke kuantitas *setiap* baris
dan CuteQL membandingkannya element-wise, sehingga hasilnya "ada baris yang cocok" — pertanyaan yang
sama, yang di database relasional butuh join:

```csharp
.Where(o => o.Lines.Any(l => l.Qty > 3))
// WHERE lines[].qty > 3
```

| C# | CuteQL |
| --- | --- |
| `o.Lines.Any()` | `ARRAY_LENGTH(lines) > 0` |
| `o.Lines.Count()` | `ARRAY_LENGTH(lines)` |
| `o.Lines.Count` (propertinya) | `LENGTH(lines)` |

### Proyeksi

Proyeksi didorong masuk ke statement, jadi hanya field yang Anda minta yang kembali:

```csharp
.Where(o => o.Total > 200_000m)
.Select(o => new { o.Code, o.Total })
// SELECT code AS Code, total AS Total FROM orders WHERE total > 200000
```

Alias-nya adalah nama anggota tipe anonim, karena itulah yang dipakai membaca kembali barisnya.
`Select` ke sebuah DTO (`new OrderSummary { ... }`) bekerja dengan cara yang sama.

Filter *setelah* proyeksi tetap berjalan di mesin — alias-nya diselesaikan kembali ke ekspresi yang
diwakilinya:

```csharp
.Select(o => new { o.Code, Amount = o.Total })
.Where(x => x.Amount > 500_000m)
// SELECT code AS Code, total AS Amount FROM orders WHERE total > 500000
```

### Pengurutan dan paging

```csharp
.OrderByDescending(o => o.Total).ThenBy(o => o.Code).Skip(1).Take(2)
// ORDER BY total DESC, code LIMIT 2 OFFSET 1
```

`Reverse()` membalik urutan yang sudah ada. `Distinct()` menjadi `SELECT DISTINCT`.

### Pengelompokan dan agregat

```csharp
orders.Query<Order>()
    .Where(o => o.Status != OrderStatus.Cancelled)
    .GroupBy(o => o.Address.City)
    .Select(g => new { City = g.Key, Orders = g.Count(), Revenue = g.Sum(o => o.Total) })
    .OrderByDescending(x => x.Revenue);
```

```sql
SELECT address.city AS City, COUNT(*) AS Orders, SUM(total) AS Revenue
FROM   orders
WHERE  status != 'Cancelled'
GROUP  BY address.city
ORDER  BY Revenue DESC
```

`Where` yang ditulis **setelah** `GroupBy` menjadi `HAVING`, persis seperti di SQL:

```csharp
.GroupBy(o => o.Customer.Name)
.Where(g => g.Count() > 1)
.Select(g => new { Name = g.Key, N = g.Count() })
// … GROUP BY customer.name HAVING COUNT(*) > 1
```

Kelompokkan dengan kunci gabungan memakai tipe anonim, lalu proyeksikan bagiannya per nama
(`g.Key.City`). Agregat yang tersedia: `Count`, `Sum`, `Average`, `Min`, dan `Max`.

### Operator terminal

`First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, `Last`, `LastOrDefault`, `ElementAt`,
`ElementAtOrDefault`, `Any`, `All`, `Count`, `LongCount`, `Sum`, `Average`, `Min`, dan `Max`
semuanya berjalan di mesin dan berperilaku persis seperti spesifikasi LINQ — termasuk melempar
exception di tempat LINQ melempar.

Semuanya dijawab oleh mesin, bukan dengan menghitung baris di memori:

- `Count()` menjalankan `SELECT COUNT(*)`, yang mengembalikan satu baris berapa pun ukuran koleksi.
- `First()` menambahkan `LIMIT 1`; `Single()` menambahkan `LIMIT 2`, cukup untuk tahu ada yang kedua.
- `Any(p)` adalah `LIMIT 1` di atas filter. `All(p)` menanyakan apakah ada yang *gagal* memenuhi `p`.
- `Sum()` atas kumpulan kosong bernilai nol, seperti di LINQ — bukan null.

---

## Yang tidak bisa diterjemahkan

Ada dua hal berbeda yang bisa terjadi, dan perbedaannya penting.

**`Select` yang tidak bisa diungkapkan** jatuh ke pembentukan di memori — *setelah* mesin selesai
menyaring, mengurutkan, dan mem-paging. Hanya pembentukan akhirnya yang dikerjakan lokal:

```csharp
.Where(o => o.Total > 500_000m)          // di mesin
.OrderBy(o => o.Code)                    // di mesin
.Take(10)                                // di mesin
.Select(o => Format(o))                  // di memori, atas sepuluh dokumen
```

**Selain itu** akan melempar `CuteTranslationException` yang menyebutkan apa yang tidak dipahaminya,
alih-alih diam-diam memuat seluruh koleksi ke memori:

```csharp
orders.Query<Order>().Where(o => o.Code.PadLeft(10) == "x").ToList();
// CuteTranslationException: 'String.PadLeft' has no CuteQL equivalent.
// Supported: string StartsWith/EndsWith/Contains/ToUpper/ToLower/Trim/Substring/Replace/
// IsNullOrEmpty, Math Abs/Round/Floor/Ceiling/Sqrt/Pow, DateTime parts, Contains for
// membership, and Any/Count over a stored array.
```

Jatuh diam-diam ke `AsEnumerable()` adalah cara sebuah query yang tampak baik-baik saja saat
pengujian berubah menjadi full scan di produksi. CuteDB memilih memberi tahu Anda.

Tidak ada `Join`, karena CuteQL memang tidak punya — document store menyematkan apa yang di
database relasional harus di-join.

---

## Baca-tulis bertipe

Mapper yang sama bekerja tanpa LINQ:

```csharp
var id = orders.Insert(new Order { Code = "SO-001", Total = 250_000m });
orders.InsertMany(batch);

var one  = orders.FindById<Order>(id);
var some = orders.Find<Order>("total > 500000");
var all  = orders.All<Order>();

order.Total = 275_000m;
orders.Save(order);          // insert atau replace, berdasarkan properti kunci
```

---

## Terkait

- [Referensi CuteQL](cuteql.md) — dialeknya, dan tiga tempat ia berbeda dari SQL
- [Memulai](memulai.md)
- [CuteDB Browser](browser.md) — tab LINQ yang mencetak CuteQL-nya, dan asisten yang menuliskannya
- [Arsitektur](arsitektur.md) — kenapa scan cukup cepat sehingga desain ini masuk akal
