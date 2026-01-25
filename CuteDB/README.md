# CuteDB - The Cute Embedded Database

**CuteDB** adalah database tertanam (embedded) sederhana berbasis in-memory untuk aplikasi .NET yang dikembangkan dengan penuh kasih sayang. Database ini dirancang untuk menjadi "lucu", ringan, dan mudah digunakan untuk proyek-proyek kecil atau kebutuhan prototyping.

**CuteDB** is a simple, in-memory embedded database for .NET applications developed with love. It is designed to be "cute", lightweight, and easy to use for small projects or prototyping needs.

---

## 🇮🇩 Bahasa Indonesia

### Fitur Utama
- **In-Memory & Persistent**: Data disimpan di memori untuk kecepatan tinggi, dan disimpan ke file (JSON) agar tidak hilang.
- **CRUD Operasi**: Mendukung Create (Insert), Read (GetCollection), Update, dan Delete dengan mudah menggunakan Generic `T`.
- **Dukungan LINQ**: Anda bisa query data menggunakan kekuatan penuh LINQ C#.
- **Dukungan SQL Sederhana**: Mendukung perintah `SELECT` dan `DELETE` dengan klausa `WHERE` sederhana.
- **Ringan**: Hanya bergantung pada `Newtonsoft.Json` dan `System.Linq.Dynamic.Core`.

### Cara Menggunakan

1. **Instalasi**: Pastikan Anda memiliki .NET SDK terinstal.
2. **Klon / Download** repository ini.
3. **Dependensi**: Project ini menggunakan NuGet packages:
   - `Newtonsoft.Json`
   - `System.Linq.Dynamic.Core`
4. **Jalankan Aplikasi**:
   Buka terminal di folder project dan jalankan:
   ```bash
   dotnet run
   ```

### Contoh Penggunaan

**Inisialisasi Database:**
```csharp
var db = new CuteDatabase("database_saya.jdb");
```

**Menambahkan Data (Insert):**
```csharp
db.Insert("Users", new User { Name = "Budi", City = "Jakarta" });
```

**Membaca Data dengan LINQ:**
```csharp
var users = db.GetCollection<User>("Users");
var hasil = users.Where(u => u.City == "Jakarta").ToList();
```

**Query SQL Sederhana:**
```csharp
var hasilSql = db.ExecuteSql("SELECT * FROM Users WHERE City == \"Jakarta\"");
```

**Menyimpan ke Disk:**
```csharp
db.Save();
```

---

## 🇬🇧 English

### Key Features
- **In-Memory & Persistent**: Data runs in memory for high speed and persists to a file (JSON).
- **CRUD Operations**: Generic support for Create (Insert), Read (GetCollection), Update, and Delete.
- **LINQ Support**: Query your data utilizing the full power of C# LINQ.
- **Simple SQL Support**: Supports basic `SELECT` and `DELETE` commands with `WHERE` clauses.
- **Lightweight**: Depends only on `Newtonsoft.Json` and `System.Linq.Dynamic.Core`.

### How to Use

1. **Installation**: Ensure you have the .NET SDK installed.
2. **Clone / Download** this repository.
3. **Dependencies**: This project uses the following NuGet packages:
   - `Newtonsoft.Json`
   - `System.Linq.Dynamic.Core`
4. **Run the Application**:
   Open a terminal in the project folder and run:
   ```bash
   dotnet run
   ```

### Code Examples

**Initialize Database:**
```csharp
var db = new CuteDatabase("my_database.jdb");
```

**Insert Data:**
```csharp
db.Insert("Users", new User { Name = "John", City = "New York" });
```

**Read Data with LINQ:**
```csharp
var users = db.GetCollection<User>("Users");
var result = users.Where(u => u.City == "New York").ToList();
```

**Simple SQL Query:**
```csharp
var resultSql = db.ExecuteSql("SELECT * FROM Users WHERE City == \"New York\"");
```

**Save to Disk:**
```csharp
db.Save();
```

---

## Benchmark Demo
Saat Anda menjalankan aplikasi (`dotnet run`), program akan menjalankan demo benchmark untuk menguji performa:
1. Insert 50,000 data.
2. Query menggunakan LINQ.
3. Query menggunakan SQL string.
4. Menyimpan data ke file.

When you run the application (`dotnet run`), it will execute a benchmark demo to test performance:
1. Insert 50,000 records.
2. Query using LINQ.
3. Query using SQL string.
4. Save data to file.

---

**Author:** Jacky the Code Bender  
**Created by:** Gravicode Studios  
_Jangan lupa kirim pulsa ya! / Don't forget to send some mobile credit!_ :D
