namespace CuteDB.Retail;

/// <summary>How much data to generate.</summary>
/// <param name="Customers">Number of customer documents.</param>
/// <param name="Products">Number of product documents.</param>
/// <param name="Orders">Number of order documents.</param>
/// <param name="Seed">Random seed, so a given size always produces the same data.</param>
public readonly record struct RetailScale(int Customers, int Products, int Orders, int Seed = 20260903)
{
    /// <summary>Small enough to page through by hand.</summary>
    public static RetailScale Tiny => new(200, 120, 1_000);

    /// <summary>The demo application's default: big enough to feel real, instant to build.</summary>
    public static RetailScale Demo => new(5_000, 800, 50_000);

    /// <summary>Large enough that an unindexed scan is visibly slower than an indexed seek.</summary>
    public static RetailScale Large => new(50_000, 2_000, 500_000);

    /// <summary>A million orders, for benchmarking.</summary>
    public static RetailScale Huge => new(200_000, 5_000, 1_000_000);

    /// <summary>Total documents across every collection.</summary>
    public int TotalDocuments => Customers + Products + Orders + StoreCount;

    /// <summary>Outlets are a fixed set, not scaled.</summary>
    public const int StoreCount = 24;
}

/// <summary>
/// Generates the sample dataset every CuteDB demo, benchmark and screenshot uses: a fictional
/// Indonesian retail chain called Nusantara Retail.
/// </summary>
/// <remarks>
/// <para>
/// The data is deliberately shaped like a real catalogue rather than like a benchmark: orders
/// carry a nested customer snapshot and a variable-length array of line items, products carry a
/// supplier object and a tag list, and a meaningful fraction of documents are missing fields the
/// rest have. Those are the shapes that make a document store worth using, and a demo built on
/// three flat columns would show none of them.
/// </para>
/// <para>
/// Everything derives from <see cref="RetailScale.Seed"/>, so the same scale always produces
/// byte-identical data. Screenshots, benchmark numbers and the figures quoted in the documentation
/// all refer to the same rows.
/// </para>
/// </remarks>
public static class NusantaraRetail
{
    private static readonly (string City, string Province, double Lat, double Lng)[] Cities =
    [
        ("Jakarta Pusat", "DKI Jakarta", -6.186, 106.834),
        ("Jakarta Selatan", "DKI Jakarta", -6.261, 106.810),
        ("Bandung", "Jawa Barat", -6.917, 107.619),
        ("Bekasi", "Jawa Barat", -6.238, 106.975),
        ("Bogor", "Jawa Barat", -6.595, 106.816),
        ("Semarang", "Jawa Tengah", -6.966, 110.417),
        ("Yogyakarta", "DI Yogyakarta", -7.795, 110.369),
        ("Surabaya", "Jawa Timur", -7.257, 112.752),
        ("Malang", "Jawa Timur", -7.966, 112.632),
        ("Denpasar", "Bali", -8.670, 115.212),
        ("Medan", "Sumatera Utara", 3.595, 98.672),
        ("Palembang", "Sumatera Selatan", -2.976, 104.775),
        ("Pekanbaru", "Riau", 0.507, 101.447),
        ("Makassar", "Sulawesi Selatan", -5.147, 119.432),
        ("Manado", "Sulawesi Utara", 1.474, 124.842),
        ("Balikpapan", "Kalimantan Timur", -1.265, 116.831),
        ("Pontianak", "Kalimantan Barat", -0.026, 109.342),
        ("Padang", "Sumatera Barat", -0.947, 100.417),
    ];

    private static readonly string[] GivenNames =
    [
        "Sari", "Budi", "Rina", "Agus", "Dewi", "Joko", "Siti", "Andi", "Putri", "Bayu",
        "Indah", "Rizki", "Maya", "Fajar", "Ayu", "Dimas", "Nadia", "Hendra", "Lestari", "Yusuf",
        "Citra", "Bagus", "Wulan", "Arif", "Ratna", "Eko", "Intan", "Surya", "Melati", "Iqbal",
    ];

    private static readonly string[] FamilyNames =
    [
        "Wijaya", "Santoso", "Pratama", "Kusuma", "Halim", "Nugroho", "Saputra", "Maulana",
        "Hidayat", "Permana", "Gunawan", "Setiawan", "Rahayu", "Suryani", "Firmansyah",
        "Anggraini", "Ramadhan", "Hartono", "Sihombing", "Situmorang",
    ];

    private static readonly (string Name, string[] Subcategories)[] Categories =
    [
        ("Kopi & Teh", ["Biji Kopi", "Kopi Bubuk", "Teh Celup", "Teh Daun"]),
        ("Makanan Ringan", ["Keripik", "Kacang", "Biskuit", "Cokelat"]),
        ("Perawatan Diri", ["Sabun", "Sampo", "Pasta Gigi", "Losion"]),
        ("Rumah Tangga", ["Deterjen", "Pembersih", "Peralatan", "Tekstil"]),
        ("Elektronik Kecil", ["Kabel", "Adaptor", "Lampu", "Baterai"]),
        ("Alat Tulis", ["Pena", "Buku", "Kertas", "Perlengkapan"]),
    ];

    private static readonly string[] Suppliers =
    [
        "PT Sumber Makmur", "CV Rejeki Abadi", "PT Nusa Boga", "PT Cahaya Kencana",
        "CV Tirta Jaya", "PT Bintang Timur", "PT Anugerah Sentosa", "CV Mitra Lestari",
    ];

    private static readonly string[] Channels = ["toko", "web", "aplikasi", "marketplace", "grosir"];

    private static readonly string[] PaymentMethods = ["tunai", "kartu debit", "kartu kredit", "qris", "transfer"];

    private static readonly string[] OrderStatuses = ["selesai", "dikirim", "diproses", "dibatalkan", "menunggu bayar"];

    private static readonly string[] LoyaltyTiers = ["bronze", "silver", "gold", "platinum"];

    private static readonly string[] ProductTags =
    [
        "promo", "baru", "terlaris", "impor", "lokal", "grosir", "kadaluarsa-dekat", "halal",
    ];

    /// <summary>
    /// Fills a database with the whole dataset and creates the indexes the demos rely on.
    /// </summary>
    /// <param name="database">The database to populate. Existing collections are left alone.</param>
    /// <param name="scale">How much to generate.</param>
    /// <param name="progress">Reports each stage as it completes, for a CLI progress bar.</param>
    public static void Seed(CuteDatabase database, RetailScale scale, Action<string, int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var random = new Random(scale.Seed);

        var stores = database.Collection("stores");
        stores.InsertMany(Report(GenerateStores(random), "stores", RetailScale.StoreCount, progress));

        var products = database.Collection("products");
        products.InsertMany(Report(GenerateProducts(random, scale.Products), "products", scale.Products, progress));

        var customers = database.Collection("customers");
        customers.InsertMany(Report(GenerateCustomers(random, scale.Customers), "customers", scale.Customers, progress));

        // Orders reference products and customers by the codes generated above, so both have to
        // exist first. Snapshots are embedded rather than joined — there is no join in CuteQL, and
        // an order should record what the customer was called when they placed it anyway.
        var productCatalogue = products.All()
            .Select(p => (Sku: p["sku"].AsString, Name: p["name"].AsString, Price: p["price"].AsDecimal))
            .ToArray();

        var customerDirectory = customers.All()
            .Select(c => (
                Code: c["code"].AsString,
                Name: c["name"].AsString,
                City: c["address"]["city"].AsString,
                Tier: c["loyalty"]["tier"].AsString))
            .ToArray();

        var storeCodes = stores.All().Select(s => s["code"].AsString).ToArray();

        var orders = database.Collection("orders");
        orders.InsertMany(Report(
            GenerateOrders(random, scale.Orders, productCatalogue, customerDirectory, storeCodes),
            "orders",
            scale.Orders,
            progress));

        CreateIndexes(database);
        progress?.Invoke("indexes", 4, 4);
    }

    /// <summary>
    /// Wraps a generator so it reports progress as documents flow past, without materialising the
    /// sequence.
    /// </summary>
    /// <remarks>
    /// Reporting per document would spend more time repainting a progress bar than generating
    /// data, so this reports every 1% or every 500 documents, whichever is coarser. The sequence
    /// stays lazy either way, which is what lets InsertMany hold one write lock for the whole run.
    /// </remarks>
    private static IEnumerable<CuteDocument> Report(
        IEnumerable<CuteDocument> source,
        string stage,
        int total,
        Action<string, int, int>? progress)
    {
        if (progress is null)
        {
            return source;
        }

        return Iterate();

        IEnumerable<CuteDocument> Iterate()
        {
            var step = Math.Max(500, total / 100);
            var done = 0;

            foreach (var document in source)
            {
                yield return document;

                if (++done % step == 0)
                {
                    progress(stage, done, total);
                }
            }

            progress(stage, total, total);
        }
    }

    /// <summary>
    /// Creates the indexes the demo queries assume. Safe to call twice; existing ones are skipped.
    /// </summary>
    public static void CreateIndexes(CuteDatabase database)
    {
        Ensure(database.Collection("orders"), "address.city", "orders_city");
        Ensure(database.Collection("orders"), "status", "orders_status");
        Ensure(database.Collection("customers"), "code", "customers_code", unique: true);
        Ensure(database.Collection("products"), "sku", "products_sku", unique: true);

        static void Ensure(CuteCollection collection, string path, string name, bool unique = false)
        {
            if (collection.Indexes.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            collection.CreateIndex(path, name, unique);
        }
    }

    /// <summary>Generates the outlet documents.</summary>
    public static IEnumerable<CuteDocument> GenerateStores(Random random)
    {
        for (var i = 0; i < RetailScale.StoreCount; i++)
        {
            var (city, province, lat, lng) = Cities[i % Cities.Length];
            var opened = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(random.Next(0, 2_600));

            yield return new CuteDocument()
                .Set("code", $"ST-{i + 1:D3}")
                .Set("name", $"Nusantara {city} {(i >= Cities.Length ? "2" : string.Empty)}".TrimEnd())
                .Set("address", CuteValue.Object(new CuteObject()
                    .Set("city", city)
                    .Set("province", province)
                    .Set("country", "ID")
                    .Set("geo", CuteValue.Object(new CuteObject()
                        .Set("lat", Math.Round(lat + ((random.NextDouble() - 0.5) * 0.08), 4))
                        .Set("lng", Math.Round(lng + ((random.NextDouble() - 0.5) * 0.08), 4))))))
                .Set("format", i % 5 == 0 ? "supermarket" : i % 3 == 0 ? "minimarket" : "toko")
                .Set("floorAreaM2", random.Next(80, 1_400))
                .Set("openedAt", CuteValue.DateTime(opened))
                .Set("active", i % 17 != 0);
        }
    }

    /// <summary>Generates the product catalogue.</summary>
    public static IEnumerable<CuteDocument> GenerateProducts(Random random, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var (category, subcategories) = Categories[i % Categories.Length];
            var subcategory = subcategories[random.Next(subcategories.Length)];

            // Prices are decimals throughout. Rupiah amounts are large and money arithmetic in a
            // demo about a retail chain should be exact, not approximately right.
            var cost = Math.Round(random.Next(2_500, 480_000) / 1m, 2);
            var margin = 1.15m + (random.Next(0, 60) / 100m);

            var tagCount = random.Next(0, 4);
            var tags = new CuteArray(tagCount);
            var chosen = new HashSet<string>(StringComparer.Ordinal);
            while (chosen.Count < tagCount)
            {
                chosen.Add(ProductTags[random.Next(ProductTags.Length)]);
            }

            foreach (var tag in chosen)
            {
                tags.Add(CuteValue.String(tag));
            }

            var document = new CuteDocument()
                .Set("sku", $"NR-{category[..2].ToUpperInvariant()}-{i + 1:D5}")
                .Set("name", $"{subcategory} {GivenNames[random.Next(GivenNames.Length)]} {random.Next(100, 999)}g")
                .Set("category", category)
                .Set("subcategory", subcategory)
                .Set("cost", CuteValue.Decimal(cost))
                .Set("price", CuteValue.Decimal(Math.Round(cost * margin, 2)))
                .Set("stock", random.Next(0, 900))
                .Set("tags", CuteValue.Array(tags))
                .Set("supplier", CuteValue.Object(new CuteObject()
                    .Set("name", Suppliers[random.Next(Suppliers.Length)])
                    .Set("leadTimeDays", random.Next(2, 30))
                    .Set("country", random.Next(10) == 0 ? "SG" : "ID")));

            // About one product in nine has no barcode on file — the sparse-field case the
            // IS MISSING demos and the sparse-index behaviour both need real data for.
            if (i % 9 != 0)
            {
                document.Set("barcode", $"899{random.NextInt64(1_000_000_000L, 9_999_999_999L)}");
            }

            if (random.Next(6) == 0)
            {
                document.Set("discontinuedAt", CuteValue.DateTime(
                    new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(random.Next(0, 600))));
            }

            yield return document;
        }
    }

    /// <summary>Generates the customer directory.</summary>
    public static IEnumerable<CuteDocument> GenerateCustomers(Random random, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var (city, province, lat, lng) = Cities[random.Next(Cities.Length)];
            var name = $"{GivenNames[random.Next(GivenNames.Length)]} {FamilyNames[random.Next(FamilyNames.Length)]}";
            var joined = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(random.Next(0, 1_800));

            // Tier correlates with spend, so the loyalty breakdown in the dashboard is not uniform
            // noise — a demo chart with four equal bars teaches nothing.
            var roll = random.NextDouble();
            var tier = roll switch
            {
                < 0.55 => "bronze",
                < 0.82 => "silver",
                < 0.95 => "gold",
                _ => "platinum",
            };

            var document = new CuteDocument()
                .Set("code", $"CUST-{i + 1:D6}")
                .Set("name", name)
                .Set("email", $"{name.Replace(' ', '.').ToLowerInvariant()}{i}@contoh.id")
                .Set("address", CuteValue.Object(new CuteObject()
                    .Set("city", city)
                    .Set("province", province)
                    .Set("country", "ID")
                    .Set("geo", CuteValue.Object(new CuteObject()
                        .Set("lat", Math.Round(lat + ((random.NextDouble() - 0.5) * 0.2), 4))
                        .Set("lng", Math.Round(lng + ((random.NextDouble() - 0.5) * 0.2), 4))))))
                .Set("loyalty", CuteValue.Object(new CuteObject()
                    .Set("tier", tier)
                    .Set("points", random.Next(0, 40_000))
                    .Set("joinedAt", CuteValue.DateTime(joined))))
                .Set("channels", CuteValue.ArrayOf(
                    CuteValue.String(Channels[random.Next(Channels.Length)]),
                    CuteValue.String(Channels[random.Next(Channels.Length)])))
                .Set("active", random.Next(12) != 0);

            if (random.Next(4) != 0)
            {
                document.Set("phone", $"08{random.NextInt64(1_000_000_000L, 9_999_999_999L)}");
            }

            yield return document;
        }
    }

    /// <summary>Generates the order history.</summary>
    public static IEnumerable<CuteDocument> GenerateOrders(
        Random random,
        int count,
        (string Sku, string Name, decimal Price)[] products,
        (string Code, string Name, string City, string Tier)[] customers,
        string[] storeCodes)
    {
        var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var span = (DateTime.UtcNow - start).TotalMinutes;

        for (var i = 0; i < count; i++)
        {
            var customer = customers[random.Next(customers.Length)];
            var placedAt = start.AddMinutes(random.NextDouble() * span);

            // A power-law-ish basket size: most orders are one or two lines, a few are wholesale
            // runs. A uniform 1..10 would make every aggregate look the same.
            var lineCount = random.NextDouble() switch
            {
                < 0.45 => 1,
                < 0.72 => 2,
                < 0.87 => 3,
                < 0.95 => random.Next(4, 7),
                _ => random.Next(7, 16),
            };

            var lines = new CuteArray(lineCount);
            decimal subtotal = 0;
            var units = 0;

            for (var line = 0; line < lineCount; line++)
            {
                var product = products[random.Next(products.Length)];
                var quantity = random.Next(1, 6);
                var lineTotal = Math.Round(product.Price * quantity, 2);
                subtotal += lineTotal;
                units += quantity;

                lines.Add(CuteValue.Object(new CuteObject()
                    .Set("sku", product.Sku)
                    .Set("name", product.Name)
                    .Set("qty", quantity)
                    .Set("unitPrice", CuteValue.Decimal(product.Price))
                    .Set("lineTotal", CuteValue.Decimal(lineTotal))));
            }

            var discountRate = customer.Tier switch
            {
                "platinum" => 0.10m,
                "gold" => 0.07m,
                "silver" => 0.03m,
                _ => 0m,
            };

            var discount = Math.Round(subtotal * discountRate, 2);
            var shipping = subtotal > 500_000m ? 0m : 15_000m;
            var total = subtotal - discount + shipping;

            var status = random.NextDouble() switch
            {
                < 0.72 => "selesai",
                < 0.84 => "dikirim",
                < 0.92 => "diproses",
                < 0.97 => "menunggu bayar",
                _ => "dibatalkan",
            };

            var document = new CuteDocument()
                .Set("code", $"SO-{placedAt:yyyyMM}-{i + 1:D7}")
                .Set("placedAt", CuteValue.DateTime(placedAt))
                .Set("status", status)
                .Set("channel", Channels[random.Next(Channels.Length)])
                .Set("storeCode", storeCodes[random.Next(storeCodes.Length)])
                .Set("customer", CuteValue.Object(new CuteObject()
                    .Set("code", customer.Code)
                    .Set("name", customer.Name)
                    .Set("tier", customer.Tier)))

                // The delivery city is denormalised to the top level because it is what almost
                // every report groups by, and a top-level field is one object hop for the scanner
                // instead of two.
                .Set("address", CuteValue.Object(new CuteObject()
                    .Set("city", customer.City)
                    .Set("country", "ID")))
                .Set("lines", CuteValue.Array(lines))
                .Set("units", units)
                .Set("subtotal", CuteValue.Decimal(subtotal))
                .Set("discount", CuteValue.Decimal(discount))
                .Set("shipping", CuteValue.Decimal(shipping))
                .Set("total", CuteValue.Decimal(total))
                .Set("payment", CuteValue.Object(new CuteObject()
                    .Set("method", PaymentMethods[random.Next(PaymentMethods.Length)])
                    .Set("paid", status is "selesai" or "dikirim")));

            if (status == "dibatalkan")
            {
                document.Set("cancelledReason", random.Next(2) == 0 ? "stok habis" : "dibatalkan pembeli");
            }

            // Roughly one order in six carries a free-text note, and a handful carry an explicit
            // null so that IS NULL and IS MISSING can be told apart in the demos.
            if (random.Next(6) == 0)
            {
                document.Set("note", "Mohon dibungkus rapi, untuk hadiah.");
            }
            else if (random.Next(20) == 0)
            {
                document.Set("note", CuteValue.Null);
            }

            yield return document;
        }
    }

    /// <summary>The CuteQL statements the demos, docs and screenshots use, in one place.</summary>
    public static IReadOnlyList<(string Title, string Query, string Explanation)> ShowcaseQueries { get; } =
    [
        (
            "Semua pesanan hari ini",
            "SELECT code, customer.name, total, status FROM orders ORDER BY placedAt DESC LIMIT 20",
            "A plain projection over a nested path. Note that customer.name reaches into a subdocument with no join."
        ),
        (
            "Pesanan besar di Bandung",
            "SELECT code, customer.name, total FROM orders WHERE address.city = 'Bandung' AND total > 500000 ORDER BY total DESC",
            "Two predicates combined; the index on address.city narrows the candidates before total is checked."
        ),
        (
            "Pendapatan per kota",
            "SELECT address.city AS kota, COUNT(*) AS pesanan, SUM(total) AS pendapatan " +
            "FROM orders WHERE status != 'dibatalkan' GROUP BY address.city ORDER BY pendapatan DESC",
            "Grouping on a nested path with two aggregates. SUM stays a decimal, so the rupiah totals are exact."
        ),
        (
            "Pelanggan yang paling sering belanja",
            "SELECT customer.name AS pelanggan, COUNT(*) AS pesanan, SUM(total) AS belanja " +
            "FROM orders GROUP BY customer.name HAVING COUNT(*) > 3 ORDER BY belanja DESC LIMIT 25",
            "HAVING filters the groups after aggregation, which WHERE cannot do."
        ),
        (
            "Produk yang belum punya barcode",
            "SELECT sku, name, category FROM products WHERE barcode IS MISSING ORDER BY category",
            "IS MISSING asks whether the field is absent, which is a different question from IS NULL."
        ),
        (
            "Pesanan yang memuat SKU tertentu",
            "SELECT code, customer.name, units, total FROM orders WHERE lines[].sku = @sku LIMIT 50",
            "lines[] projects across the line-item array, so this matches an order if any of its lines has that SKU."
        ),
        (
            "Produk promo dari pemasok impor",
            "SELECT sku, name, price, supplier.name AS pemasok FROM products " +
            "WHERE tags = 'promo' AND supplier.country != 'ID' ORDER BY price DESC",
            "A field holding an array matches when any element matches, so tags = 'promo' means 'tagged promo'."
        ),
        (
            "Penjualan bulanan",
            "SELECT DATE_TRUNC('month', placedAt) AS bulan, COUNT(*) AS pesanan, SUM(total) AS pendapatan " +
            "FROM orders WHERE status = 'selesai' GROUP BY DATE_TRUNC('month', placedAt) ORDER BY bulan",
            "Grouping by a computed expression, not just a field."
        ),
        (
            "Rata-rata keranjang per saluran",
            "SELECT channel AS saluran, COUNT(*) AS pesanan, ROUND(AVG(total), 0) AS rataRata, MAX(total) AS terbesar " +
            "FROM orders WHERE status != 'dibatalkan' GROUP BY channel ORDER BY rataRata DESC",
            "Several aggregates at once, wrapped in a scalar function."
        ),
        (
            "Pencarian teks pada kode pesanan",
            "SELECT code, placedAt, total FROM orders WHERE code LIKE 'SO-2026%' ORDER BY placedAt DESC LIMIT 40",
            "LIKE with the usual % and _ wildcards. This one runs on the native scanner."
        ),
    ];
}
