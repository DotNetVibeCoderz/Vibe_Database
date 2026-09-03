namespace CuteDB.Demo.Services;

/// <summary>
/// The C# behind each section, shown in the code drawer.
/// </summary>
/// <remarks>
/// These are not illustrations written to look good in a slide: each one is the code the section
/// above it actually runs, trimmed of the interface plumbing. The point of the drawer is that
/// someone can read what just happened and paste it into their own project, so a sample that
/// drifted from the real call would be worse than no sample.
/// </remarks>
public static class CodeSamples
{
    /// <summary>Opening a database and seeding it.</summary>
    public const string Dashboard = """"
        // Everything on this screen comes out of one in-memory database.
        using var db = CuteDatabase.CreateInMemory();
        NusantaraRetail.Seed(db, RetailScale.Demo);   // 55,824 documents

        // Revenue by city: grouping on a nested path, with two aggregates.
        // SUM stays a decimal, so the rupiah totals are exact rather than approximately right.
        var byCity = db.Execute("""
            SELECT address.city AS kota,
                   COUNT(*)     AS pesanan,
                   SUM(total)   AS pendapatan
            FROM   orders
            WHERE  status != 'dibatalkan'
            GROUP  BY address.city
            ORDER  BY pendapatan DESC
            """);

        foreach (var row in byCity.Rows)
            Console.WriteLine($"{row["kota"]}: {row["pendapatan"].AsDecimal:N0}");

        // Monthly trend: grouping by a computed expression, not just a field.
        var monthly = db.Execute("""
            SELECT DATE_TRUNC('month', placedAt) AS bulan, SUM(total) AS pendapatan
            FROM   orders
            WHERE  status = 'selesai'
            GROUP  BY DATE_TRUNC('month', placedAt)
            ORDER  BY bulan
            """);
        """";

    /// <summary>Running queries and reading the plan.</summary>
    public const string Query = """"
        // Run any CuteQL statement. The result carries the rows, the columns discovered from
        // them, how long it took, and how the engine found them.
        var result = db.Execute("SELECT code, customer.name, total FROM orders WHERE total > 500000");

        Console.WriteLine(result.Plan);
        // → Index seek on 'orders_city': 2,944 candidates, 2,944 matched
        // → Collection scan: 50,000 scanned, 1,204 matched (native)

        // Bind user input as a parameter rather than concatenating it into the statement.
        // A bound value is used as a value and can never be reinterpreted as syntax.
        var bandung = db.Execute(
            "SELECT * FROM orders WHERE address.city = @kota AND total > @minimum",
            ("kota",    CuteValue.String("Bandung")),
            ("minimum", CuteValue.Decimal(500_000m)));

        // Ask how a query would run without running it to completion.
        CuteQueryPlan plan = db.Explain("SELECT * FROM orders WHERE address.city = 'Medan'");
        Console.WriteLine($"{plan.Strategy}, native scanner: {plan.UsedNativeScanner}");

        // Three things worth knowing about the dialect:
        //
        //   lines[].sku = 'NR-KO-00042'   an order matches if ANY of its line items does
        //   tags = 'promo'                a field holding an array matches element-wise
        //   barcode IS MISSING            absent is a different question from IS NULL
        """";

    /// <summary>Inserting, updating and deleting.</summary>
    public const string Crud = """"
        var orders = db.Collection("orders");

        // Create. The id comes back on the document; nothing had to be declared first.
        var order = CuteDocument.Parse("""
            {
              "code": "SO-202609-9000001",
              "customer": { "name": "Sari Wijaya", "tier": "gold" },
              "address":  { "city": "Bandung", "country": "ID" },
              "lines":    [ { "sku": "NR-KO-00042", "qty": 2, "lineTotal": 189000 } ],
              "total":    189000,
              "status":   "diproses"
            }
            """);

        CuteId id = orders.Insert(order);

        // Read.
        CuteDocument? found = orders.FindById(id);
        Console.WriteLine(found?["customer"]["name"].AsString);   // reaches into the subdocument

        // Update. Either edit the document and save it back…
        order["status"] = "dikirim";
        orders.Save(order);

        // …or change many at once, writing through a path that need not exist yet.
        db.Execute("UPDATE orders SET address.province = 'Jawa Barat' WHERE address.city = 'Bandung'");

        // Delete.
        orders.Delete(id);
        db.Execute("DELETE FROM orders WHERE status = 'dibatalkan' AND total < 50000");
        """";

    /// <summary>Bulk loading.</summary>
    public const string Bulk = """"
        // InsertMany is not a loop around Insert. It takes the write lock once instead of once
        // per document and leaves the log buffered until the end, which on a bulk load is the
        // difference between tens of thousands and hundreds of thousands of documents a second.
        var orders = db.Collection("orders");

        IEnumerable<CuteDocument> batch = NusantaraRetail.GenerateOrders(
            new Random(seed), count, products, customers, storeCodes);

        int inserted = orders.InsertMany(batch);      // one lock, one flush

        // The sequence stays lazy, so a load larger than memory streams through rather than
        // being materialised first.

        // Documents live in unmanaged slabs, so a million of them are a few hundred blocks the
        // GC never traces — not a million live objects.
        var stats = orders.Stats();
        Console.WriteLine($"{stats.DocumentCount:N0} documents");
        Console.WriteLine($"{stats.AverageDocumentBytes:N0} bytes each");
        Console.WriteLine($"{stats.ReservedBytes / 1024 / 1024:N0} MiB reserved");
        """";

    /// <summary>Paging a large collection into a grid.</summary>
    public const string Grid = """"
        // A grid over 50,000 rows pages through CuteQL rather than loading everything: LIMIT and
        // OFFSET are evaluated by the engine, so the interface only ever holds one page.
        var page = db.Execute($"""
            SELECT code, placedAt, customer.name AS pelanggan, address.city AS kota,
                   channel, status, units, total
            FROM   orders
            {where}
            ORDER  BY {sortColumn} {(descending ? "DESC" : "ASC")}
            LIMIT  {pageSize} OFFSET {page * pageSize}
            """);

        // The columns come back discovered from the rows, not declared in advance — a collection
        // has no schema to ask. That is what lets the grid render a result of any shape.
        foreach (var column in page.Columns)
            grid.Columns.Add(new DataGridTextColumn { Header = column });

        // Sorting on an indexed path is a seek; on any other path it is a scan. Both are correct,
        // and the till roll on the right shows which one just happened.
        orders.CreateIndex("address.city", "orders_city");
        """";

    /// <summary>Import and export.</summary>
    public const string Exchange = """"
        // Export. JSON Lines streams a document per line, so a file larger than memory is fine.
        using var writer = new StreamWriter("orders.jsonl");
        foreach (var document in db.Collection("orders").All())
            writer.WriteLine(document.ToJson());

        // JSON has no spelling for a decimal, a date, a GUID or a document id, so a plain export
        // renders them as numbers and strings. When the file is a backup rather than something a
        // person will read, ask for the lossless form and they round-trip exactly.
        var backup = CuteJson.Write(document.AsValue(), CuteJsonOptions.Lossless);
        // → {"placedAt":{"$date":"2026-03-01T12:00:00.0000000Z"},"total":{"$decimal":"249000.00"}}

        // Import. PreferDecimal matters for money: without it 0.1 comes back as a double, and an
        // invoice that was exact stops being exact.
        var imported = File.ReadLines("orders.jsonl")
            .Where(line => line.Length > 0)
            .Select(line => new CuteDocument(CuteJson.Parse(line, CuteJsonOptions.Financial).AsObject));

        db.Collection("orders_restored").InsertMany(imported);

        // The same thing from the command line:
        //   cutedb export shop.cute orders --out orders.jsonl
        //   cutedb import shop.cute orders.jsonl --collection orders --decimal
        """";

    /// <summary>Measuring the three routes to an answer.</summary>
    public const string Performance = """"
        // The same question, three ways. All three return identical rows; what differs is how
        // many documents were examined to find them.

        // 1. Managed scan — walks every document, reading one field off the encoded bytes without
        //    decoding the rest.
        CuteNative.Disabled = true;
        int a = orders.CountWhere("address.city = 'Bandung'");

        // 2. Native scan — the same walk, executed by the Rust accelerator. The predicate is
        //    compiled to bytecode and the whole scan runs without crossing back into managed code,
        //    so it allocates essentially nothing per row.
        CuteNative.Disabled = false;
        int b = orders.CountWhere("address.city = 'Bandung'");

        // 3. Index seek — skips the documents that cannot match.
        orders.CreateIndex("address.city", "orders_city");
        int c = orders.CountWhere("address.city = 'Bandung'");

        // Which one ran is never a guess:
        var plan = db.Explain("SELECT * FROM orders WHERE address.city = 'Bandung'");
        Console.WriteLine($"{plan.Strategy} · native: {plan.UsedNativeScanner}");

        // An index is not free — it costs memory and slows writes — so the honest comparison is
        // this one, on your data, rather than a rule of thumb.
        """";
}
