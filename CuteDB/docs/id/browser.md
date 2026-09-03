# CuteDB Browser

*Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.*

Meja kerja desktop untuk CuteDB: menjelajahi basis data, menulis CuteQL atau LINQ, melihat barisnya,
dan melihat apa yang sebenarnya dikerjakan mesin untuk mendapatkannya. **Jack — The Code Bender**
duduk di panel kanan dan menulis kueri berdasarkan skema yang benar-benar ada di sana.

![Meja kerja](../images/browser/01-workbench.png)

```bash
dotnet run --project tools/CuteBrowser
```

---

## Tata letak

Satu pembagian empat arah, dan tidak ada lagi yang berpindah:

| Di mana | Apa | Sembunyikan |
| --- | --- | --- |
| Kiri | Koleksi, field hasil inferensi, dan indeksnya | seret pemisahnya |
| Tengah | Tab kueri — editor di atas, hasil di bawah | — |
| Kanan | Jack, sang asisten | `Ctrl+J` |
| Bawah | Semua yang dikerjakan aplikasi, berikut waktunya | `Ctrl+L` |

Kedua panel samping bisa ditutup, dan setiap lebar serta tinggi diingat antar sesi.

---

## Pita rencana (plan band)

Strip di antara editor dan grid adalah inti dari seluruh aplikasi ini:

```
COLLECTION SCAN · examined 50,000 · matched 4,182 · returned 12 · 38.50 ms · native
```

Di bawahnya ada garis yang bagian terisinya adalah *matched dibagi examined*. Bilah yang hampir
penuh berarti jalur aksesnya tepat. Sepotong kecil warna kunyit berarti mesin memeriksa lima puluh
ribu dokumen untuk mengembalikan dua belas — dan untuk itulah indeks ada.

- **examined** — baris yang dihasilkan jalur akses sebelum predikat dijalankan
- **matched** — baris yang lolos predikat
- **returned** — baris yang kembali; berbeda dari matched setiap kali kueri melakukan pengelompokan
- **native** — akselerator Rust yang menjalankan pemindaian; tanpanya penilai terkelola yang
  mengerjakannya, dengan hasil yang identik

Pita berubah hijau ketika semua yang diperiksa cocok, dan cokelat soga berisi pesannya ketika kueri
gagal.

---

## Menulis kueri

![Agregat berkelompok](../images/browser/02-query.png)

Setiap tab berisi CuteQL atau C#, bisa ditukar dari pemilih di kiri atasnya. Editornya punya nomor
baris (`Edit ▸ Show Line Numbers`), pewarnaan sintaks yang ditulis khusus untuk CuteQL — jalur field
seperti `address.city` diwarnai sebagai jalur, bukan sebagai identifier biasa — dan penyuntingan
sebagaimana mestinya.

| Perintah | Tombol | Fungsinya |
| --- | --- | --- |
| Run | `F5` | Menjalankan seleksi, atau seluruh tab bila tidak ada yang diseleksi |
| Check | `F7` | Mem-parse tanpa menjalankan, dan melaporkan rencana yang *akan* dipakai |
| Format | `Ctrl+Shift+F` | Menulis ulang lewat parser |
| Go To Line | `Ctrl+G` | |
| New Query | `Ctrl+N` | Kosong, atau dari templat |
| Save / Save As | `Ctrl+S` / `Ctrl+Shift+S` | |

**Format adalah perjalanan bolak-balik sungguhan.** Ia mem-parse CuteQL Anda lalu menuliskannya
kembali lewat penulis milik mesin itu sendiri, jadi memformat sekaligus membuktikan kuerinya valid.
Apa pun yang tidak bisa di-parse dibiarkan persis seperti yang Anda ketik dan kegagalannya
dilaporkan — perintah format yang merusak teks yang sedang setengah Anda ketik lebih buruk daripada
tidak ada perintah format sama sekali.

Satu tab boleh memuat beberapa statement yang dipisahkan titik koma. Semuanya dijalankan berurutan,
dan grid menampilkan yang terakhir mengembalikan baris — jadi "isi data, lalu select" cukup dalam
satu tab. Pemisahannya tetap benar meski ada titik koma di dalam string berkutip.

---

## Tab LINQ

![Tab LINQ, dan CuteQL yang dihasilkannya](../images/browser/03-linq.png)

Tab LINQ adalah skrip C# dengan basis data yang terbuka berada dalam cakupan. Ia dikompilasi oleh
Roslyn lalu dijalankan — tidak ada cara mengevaluasi expression tree yang belum dikompilasi, dan
menciptakan bahasa kedua yang sekadar *mirip* LINQ akan lebih buruk daripada C# yang jujur.

Dua nama tersedia:

| Nama | Apa itu |
| --- | --- |
| `db` | `CuteDatabase` yang sedang terbuka |
| `Q<T>("orders")` | Singkatan untuk `db.Collection("orders").Query<T>()` |
| `Sql("SELECT …")` | Menjalankan statement CuteQL dan mengembalikan hasilnya |

Karena CuteDB tidak berskema, Anda mendeklarasikan bentuk yang Anda pedulikan di dalam skrip itu
sendiri:

```csharp
public class Address { public string City { get; set; } = ""; }

public class Order
{
    public CuteId Id { get; set; }
    public string Code { get; set; } = "";
    public Address Address { get; set; } = new();
    public decimal Total { get; set; }
}

db.Collection("orders").Query<Order>()
  .Where(o => o.Total > 100_000m)
  .OrderByDescending(o => o.Total)
  .Select(o => new { o.Code, City = o.Address.City, o.Total })
  .Take(20)
```

**Ekspresi terakhir dalam skrip adalah hasilnya.** Deklarasi di depan; kueri di belakang.
Kembalikan sebuah `IQueryable` dan tab akan mencetak CuteQL hasil terjemahannya, di pita di atas
grid — dan itulah alasan tab LINQ ini ada. Sebuah urutan, POCO, tipe anonim, atau satu skalar
semuanya mendarat di grid.

Skrip berjalan di proses ini dengan kepercayaan penuh. Itu kepercayaan yang sama yang sudah dimiliki
tab kueri — `DELETE FROM orders` tidak jadi kurang merusak hanya karena pendek — tetapi layak
dinyatakan terus terang, dan itulah sebabnya setiap eksekusi dicatat.

Lihat [rujukan LINQ](linq.md) untuk apa yang bisa dan tidak bisa diterjemahkan.

---

## Jack — The Code Bender

![Jack menjawab dengan kueri tervalidasi](../images/browser/04-jack.png)

Jack membaca basis data yang terbuka sebelum menulis apa pun. Itulah beda antara asisten dan sesuatu
yang sekadar terdengar meyakinkan: model yang tidak punya cara melihat akan mengarang `city` padahal
field-nya `address.city`, lalu kueri yang ditulisnya berjalan, tidak mengembalikan apa-apa, dan
tampak seperti jawaban.

**Apa yang bisa dia lakukan:**

| Alat | Untuk apa |
| --- | --- |
| `list_collections` | Apa saja isi basis data, dan seberapa banyak |
| `describe_collection` | Jalur field yang sebenarnya, tipenya, dan seberapa sering ada |
| `preview_query` | Menjalankan `SELECT` dan mengembalikan hingga 20 baris — penulisan ditolak |
| `validate_cuteql` | Mem-parse tanpa menjalankan |
| `explain_query` | Jalur akses, dan berapa banyak kerja yang terbuang |
| `list_indexes`, `database_stats` | |
| `search_internet` | Pencarian web lewat Tavily |
| `scrape_web_page` | Mengambil satu halaman sebagai teks yang terbaca |
| `math_calculate`, `math_summarise` | Aritmetika eksak, karena hitungan di kepala model itu mendekati, bukan benar |
| `current_datetime`, `date_maths` | Tanggal yang sesungguhnya, yang tidak mungkin diketahui model |
| `encode_text` | base64 dan hex, dua arah |

**Jack tidak menjalankan penulisan.** Dia akan menyerahkan `INSERT` atau `DELETE` kepada Anda dan
menjelaskannya; menjalankannya adalah urusan Anda. Setiap blok kode berpagar yang dia hasilkan
mendapat tombol **→ New tab** yang membukanya dalam mode yang tepat.

**Memakai panelnya:**

- `Ctrl+Enter` atau tombol **Send**
- **Attach image** untuk tangkapan layar atau diagram — modelnya melihat gambar itu
- **clear** memulai percakapan baru
- Pemilih model di atas menukar penyedia di tengah percakapan
- Seret tepi kirinya untuk mengubah lebar; `Ctrl+J` menyembunyikannya

Teks yang kembali dari `search_internet` dan `scrape_web_page` ditulis orang lain. Teks itu diberikan
ke model sebagai bahan rujukan dan diberi label demikian — halaman yang berisi "abaikan instruksi
sebelumnya" tetaplah sebuah halaman, bukan otoritas.

---

## Penyedia LLM

Enam, semuanya diatur di `Tools ▸ Settings` dan disimpan di `app.config`:

| Penyedia | Catatan |
| --- | --- |
| **OpenAI** | |
| **Azure OpenAI** | Kolom model diisi nama *deployment*; endpoint-nya adalah akar resource |
| **Claude** | Messages API Anthropic secara langsung, termasuk loop tool-nya |
| **Gemini** | Lewat endpoint Google yang kompatibel dengan OpenAI |
| **Ollama** | Lokal, tanpa kunci |
| **Compatible** | Apa pun lain yang berbicara API OpenAI — DeepSeek, Groq, Together, OpenRouter, vLLM |

Kunci boleh dibiarkan kosong di `app.config` dan diberikan lewat environment, sehingga salinan
repositori bersama tidak pernah membawa kunci milik siapa pun:

```
OPENAI_API_KEY  AZURE_OPENAI_API_KEY  ANTHROPIC_API_KEY  GEMINI_API_KEY
OPENAI_COMPATIBLE_API_KEY  TAVILY_API_KEY
```

Kunci yang tertulis di pengaturan menang atas environment; yang kosong jatuh ke environment.

**Model penalaran** — keluarga gpt-5, o1, o3 — menolak temperature apa pun selain bawaannya.
Permintaan dikirim sesuai konfigurasi, dan bila penolakannya menyebut temperature, permintaan
dikirim ulang tanpa temperature. Anda tidak perlu tahu model mana saja itu.

---

## Penjelajah (explorer)

![Penjelajah, dengan field hasil inferensi](../images/browser/05-explorer.png)

Pohon ini menampilkan koleksi, field-nya, dan indeksnya. **Ini bukan skema.** CuteDB tidak punya
skema; yang Anda lihat adalah isi sampel hingga 200 dokumen per koleksi, dan persentasenya adalah
persentase dari sampel itu. Panelnya menyatakan hal ini terang-terangan, karena penjelajah yang
tampak seperti peramban skema akan dibaca sebagai skema.

- **Klik ganda sebuah koleksi** — membuka tab yang menjelajahinya, dan langsung menjalankannya
- **Klik ganda sebuah field** — menyisipkan jalurnya di posisi kursor
- **Klik kanan** — tampilkan data, salin koleksinya, hapus, atau buat indeks pada sebuah field

Menghapus koleksi selalu bertanya lebih dulu dan menyebutkan jumlah dokumennya, karena tidak bisa
dibatalkan.

---

## Templat

**New Database** menawarkan Blank, ditambah empat yang datang lengkap dengan skema dan dokumen:

| Templat | Isinya |
| --- | --- |
| Retail | Produk, pelanggan, dan pesanan — skema yang menjadi acuan semua templat kueri |
| Content | Pos, penulis, dan komentar — bersarang dan penuh array, bentuk nyata sebuah CMS |
| Telemetry | Perangkat dan pembacaan — lebar dan numerik, untuk mencoba agregat dan rentang |
| Task board | Proyek dan tugas, dengan penanggung jawab dan checklist |

**New Query** menawarkan Blank, Blank LINQ, dan selusin contoh kerja — jalur bersarang, perbandingan
array element-wise, `MISSING` versus `NULL`, agregat berkelompok, rentang tanggal, pencarian teks,
tiga jenis penulisan, dan empat tab LINQ. Semuanya berjalan apa adanya di atas templat Retail,
karena templat yang error di basis data baru mengajarkan hal yang salah.

---

## Menu dan toolbar

```
File      New Database…  Open Database…  Close Database
          New Query…  Open Query…  Save  Save As…  Exit
Edit      Go To Line…  Format Query  Show Line Numbers
Database  Add Table…  Compact  Statistics
Query     Run (F5)  Check (F7)
View      Assistant (Ctrl+J)  Logs (Ctrl+L)
Tools     Settings…  About
```

Setiap perintah ada di menu, di toolbar, dan di tombol pintas — jadi tidak ada perintah yang bisa
dicapai lewat satu jalan tetapi tidak lewat jalan lain.

---

## Pengaturan

`Tools ▸ Settings` menulis kembali ke `app.config` di sebelah executable, dan semua isi berkas itu
juga bisa disunting manual.

| Kelompok | Pengaturan |
| --- | --- |
| Asisten | System prompt, temperature, jumlah giliran riwayat, batas panggilan tool |
| Penyedia | Model, kunci, dan endpoint untuk masing-masing dari enam penyedia |
| Alat | Apakah alat web ditawarkan sama sekali, dan kunci Tavily |
| Meja kerja | Nomor baris, bungkus kata, ukuran font editor, jumlah baris di grid |

Kegagalan menyimpan dilaporkan, bukan dilempar sebagai exception — direktori instalasi yang hanya
bisa dibaca itu situasi nyata, dan kehilangan apa yang baru Anda ketik lebih buruk daripada aplikasi
melupakannya setelah ditutup.

---

## Pemasangan

**Windows:**

```powershell
./tools/CuteBrowser/scripts/install.ps1
# atau: ./install.ps1 -InstallPath 'D:\Tools\CuteBrowser' -SelfContained
```

Mem-publish ke `%LOCALAPPDATA%\CuteBrowser` dan menaruh pintasan di Start menu.

**Linux dan macOS:**

```bash
chmod +x tools/CuteBrowser/scripts/install.sh
./tools/CuteBrowser/scripts/install.sh
# atau: ./install.sh --prefix /opt/cutebrowser --self-contained
```

Mem-publish ke `~/.local/share/cutebrowser`, menautkan `cutebrowser` ke `~/.local/bin`, dan menulis
desktop entry di Linux. Skripnya menyebutkan pustaka X11 yang hilang, alih-alih membiarkan Anda
menemukannya sendiri saat peluncuran pertama.

Kedua skrip hanya butuh .NET 10 SDK, keduanya mempertahankan berkas pengaturan yang sudah ada saat
pemasangan ulang, dan `--self-contained` membundel runtime untuk mesin yang tidak punya .NET.

---

## Menguji asisten tanpa jendela

```bash
CuteBrowser --ask "Kota mana yang pendapatannya paling besar? Berikan CuteQL-nya."
CuteBrowser --ask "Ada berapa pesanan?" --db shop.cute
```

Satu giliran lewat agen, plugin, dan pengaturan yang sama dengan yang dipakai panelnya, dicetak ke
konsol lengkap dengan setiap panggilan tool saat terjadi. Apakah kernel-nya terpasang benar, apakah
penyedianya menjawab, apakah panggilan tool benar-benar sampai ke basis data — tidak satu pun
terlihat di tangkapan layar, dan semuanya rusak dalam diam.

Tanpa `--db`, ia mengisi basis data sementara dari templat Retail supaya tool-nya punya sesuatu
untuk ditemukan.

---

## Tangkapan layar

Gambar di halaman ini dirender dari jendela sungguhan oleh
`dotnet run --project tools/CuteBrowser -- --screenshot docs/images/browser`, di atas basis data yang
diisi dari templat Retail. Gambarnya tidak mungkin melenceng dari aplikasi yang diklaimnya.

Percakapan di panel Jack pada gambar itu adalah naskah, bukan percakapan langsung: gambar
dokumentasi tidak boleh bergantung pada kunci API, jaringan, atau apa pun yang kebetulan dikatakan
model sore ini.

---

## Terkait

- [LINQ](linq.md) — apa yang diterjemahkan, dan `ToCuteQL()`
- [Rujukan CuteQL](cuteql.md) — dialeknya, dan tiga tempat ia berbeda dari SQL
- [Baris perintah](cli.md) — `cutedb`, untuk hal-hal yang lebih cocok di terminal
