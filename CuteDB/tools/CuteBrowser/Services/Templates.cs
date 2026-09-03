namespace CuteDB.Browser.Services;

/// <summary>A starting point for a new query tab.</summary>
/// <param name="Name">What the picker calls it.</param>
/// <param name="Summary">One line on when to reach for it.</param>
/// <param name="Language">Which language the body is in.</param>
/// <param name="Body">The text the tab opens with.</param>
public sealed record QueryTemplate(string Name, string Summary, QueryLanguage Language, string Body);

/// <summary>A starting point for a new database.</summary>
/// <param name="Name">What the picker calls it.</param>
/// <param name="Summary">One line on what it contains.</param>
/// <param name="Collections">The collections it creates.</param>
/// <param name="Seed">CuteQL run after creation, to put something in them.</param>
/// <param name="Indexes">Indexes created after seeding, as collection and path.</param>
public sealed record DatabaseTemplate(
    string Name,
    string Summary,
    IReadOnlyList<string> Collections,
    string Seed,
    IReadOnlyList<(string Collection, string Path)> Indexes);

/// <summary>
/// The templates offered by New Query and New Database.
/// </summary>
/// <remarks>
/// <para>
/// Every query template is written against the retail schema the database templates create, so
/// picking "Retail" and then any query template gives something that runs immediately. A template
/// that returns an error on a fresh database teaches the wrong thing about the tool.
/// </para>
/// <para>
/// The templates lean on the three parts of CuteQL that are not SQL — field paths into
/// subdocuments, element-wise comparison against an array field, and MISSING as distinct from NULL
/// — because those are what a person coming from SQL will not guess, and a template is where they
/// are cheapest to learn.
/// </para>
/// </remarks>
public static class Templates
{
    /// <summary>What New Query offers, blank first.</summary>
    public static IReadOnlyList<QueryTemplate> Queries { get; } =
    [
        new(
            "Blank",
            "An empty CuteQL tab.",
            QueryLanguage.CuteQL,
            string.Empty),

        new(
            "Blank LINQ",
            "An empty C# tab, with the database already in scope.",
            QueryLanguage.Linq,
            """
            // `db` is the open database. A script's LAST expression is its result, so the types
            // come first and the query comes last. Return an IQueryable and the CuteQL it
            // translated to is shown above the results.

            public class Order
            {
                public CuteId Id { get; set; }
                public string Code { get; set; } = "";
                public decimal Total { get; set; }
                public string Status { get; set; } = "";
            }

            db.Collection("orders").Query<Order>()
              .Where(o => o.Total > 250_000m)
              .OrderByDescending(o => o.Total)
              .Take(50)
            """),

        new(
            "Browse a collection",
            "The first rows, to see what is in there.",
            QueryLanguage.CuteQL,
            """
            SELECT *
            FROM   orders
            LIMIT  100
            """),

        new(
            "Filter on a nested field",
            "A path reaches into a subdocument; no join, no flattening.",
            QueryLanguage.CuteQL,
            """
            SELECT code, customer.name AS buyer, address.city AS city, total
            FROM   orders
            WHERE  address.city = 'Bandung'
               AND total > 250000
            ORDER  BY total DESC
            LIMIT  100
            """),

        new(
            "Group and aggregate",
            "Revenue per city, biggest first.",
            QueryLanguage.CuteQL,
            """
            SELECT address.city AS city,
                   COUNT(*)     AS orders,
                   SUM(total)   AS revenue,
                   AVG(total)   AS average
            FROM   orders
            WHERE  status != 'cancelled'
            GROUP  BY address.city
            ORDER  BY revenue DESC
            """),

        new(
            "Search inside an array",
            "A field holding an array compares element-wise, so `=` means contains.",
            QueryLanguage.CuteQL,
            """
            -- tags = 'promo' asks whether any tag is 'promo'.
            -- lines[].sku reaches every line, so this is "any line has that SKU".

            SELECT code, tags, total
            FROM   orders
            WHERE  tags = 'promo'
               AND lines[].sku = 'NR-KO-00042'
            LIMIT  100
            """),

        new(
            "Missing is not null",
            "Absent and present-but-null are different questions.",
            QueryLanguage.CuteQL,
            """
            -- IS MISSING: the field is not in the document at all.
            -- IS NULL:    the field is there and holds null.
            -- Neither `x > 0` nor `NOT (x > 0)` matches a row where x is either.

            SELECT code, discount
            FROM   orders
            WHERE  discount IS MISSING
            LIMIT  100
            """),

        new(
            "Dates and ranges",
            "A window of time, with the parts pulled out.",
            QueryLanguage.CuteQL,
            """
            SELECT code,
                   placedAt,
                   YEAR(placedAt)  AS year,
                   MONTH(placedAt) AS month,
                   total
            FROM   orders
            WHERE  placedAt BETWEEN '2026-01-01' AND '2026-06-30'
            ORDER  BY placedAt DESC
            LIMIT  100
            """),

        new(
            "Text search",
            "LIKE, upper and lower, and a computed column.",
            QueryLanguage.CuteQL,
            """
            SELECT sku,
                   name,
                   UPPER(name)     AS shouted,
                   LENGTH(name)    AS letters,
                   price
            FROM   products
            WHERE  name LIKE '%kopi%'
               OR  sku  LIKE 'NR-KO-%'
            ORDER  BY name
            LIMIT  100
            """),

        new(
            "Insert, update, delete",
            "The three writes, in one tab. Statements run in order.",
            QueryLanguage.CuteQL,
            """
            INSERT INTO products
            VALUES { 'sku': 'NR-XX-00001',
                     'name': 'Contoh Produk',
                     'price': 15000,
                     'tags': ['baru'],
                     'supplier': { 'name': 'PT Contoh', 'leadTimeDays': 3 } };

            UPDATE products
            SET    price = 16500, tags = ['baru', 'promo']
            WHERE  sku = 'NR-XX-00001';

            SELECT * FROM products WHERE sku = 'NR-XX-00001';

            -- Uncomment to clean up:
            -- DELETE FROM products WHERE sku = 'NR-XX-00001';
            """),

        new(
            "Top N per group",
            "The single biggest order in each city.",
            QueryLanguage.CuteQL,
            """
            SELECT address.city AS city,
                   MAX(total)   AS biggest,
                   MIN(total)   AS smallest,
                   COUNT(*)     AS orders
            FROM   orders
            GROUP  BY address.city
            HAVING COUNT(*) > 2
            ORDER  BY biggest DESC
            """),

        new(
            "LINQ: filter and project",
            "The same question in C#, with the generated CuteQL shown above the grid.",
            QueryLanguage.Linq,
            """
            // Returns an IQueryable, so the tab prints the CuteQL it became.

            public class Address { public string City { get; set; } = ""; }

            public class Order
            {
                public CuteId Id { get; set; }
                public string Code { get; set; } = "";
                public Address Address { get; set; } = new();
                public decimal Total { get; set; }
            }

            db.Collection("orders").Query<Order>()
              .Where(o => o.Address.City == "Bandung" && o.Total > 250_000m)
              .OrderByDescending(o => o.Total)
              .Select(o => new { o.Code, City = o.Address.City, o.Total })
              .Take(50)
            """),

        new(
            "LINQ: group and aggregate",
            "GroupBy becomes GROUP BY; the aggregates run in the engine.",
            QueryLanguage.Linq,
            """
            public class Address { public string City { get; set; } = ""; }

            public class Order
            {
                public CuteId Id { get; set; }
                public Address Address { get; set; } = new();
                public string Status { get; set; } = "";
                public decimal Total { get; set; }
            }

            db.Collection("orders").Query<Order>()
              .Where(o => o.Status != "cancelled")
              .GroupBy(o => o.Address.City)
              .Select(g => new { City = g.Key, Orders = g.Count(), Revenue = g.Sum(o => o.Total) })
              .OrderByDescending(x => x.Revenue)
            """),

        new(
            "LINQ: how did that run?",
            "Rows, timing and access path together.",
            QueryLanguage.Linq,
            """
            // ToListWithDiagnostics returns the rows and what they cost.

            public class Address { public string City { get; set; } = ""; }

            public class Order
            {
                public CuteId Id { get; set; }
                public Address Address { get; set; } = new();
                public decimal Total { get; set; }
            }

            var query = db.Collection("orders").Query<Order>()
                          .Where(o => o.Address.City == "Bandung");

            var (rows, diagnostics) = query.ToListWithDiagnostics();

            new[]
            {
                new { Field = "CuteQL",   Value = diagnostics.CuteQL },
                new { Field = "Rows",     Value = diagnostics.RowsReturned.ToString() },
                new { Field = "Duration", Value = $"{diagnostics.Duration.TotalMilliseconds:N2} ms" },
                new { Field = "Plan",     Value = diagnostics.Plan.ToString() },
            }
            """),
    ];

    /// <summary>What New Database offers, blank first.</summary>
    public static IReadOnlyList<DatabaseTemplate> Databases { get; } =
    [
        new(
            "Blank",
            "An empty file. Nothing in it.",
            [],
            string.Empty,
            []),

        new(
            "Retail",
            "Products, customers and orders for a small shop. The schema every query template is written against.",
            ["products", "customers", "orders"],
            """
            INSERT INTO products VALUES
              { 'sku': 'NR-KO-00042', 'name': 'Kopi Gayo 250g', 'price': 68000, 'stock': 120,
                'tags': ['promo', 'lokal'], 'supplier': { 'name': 'PT Sumber Makmur', 'leadTimeDays': 7 } },
              { 'sku': 'NR-TE-00011', 'name': 'Teh Melati 100g', 'price': 24000, 'stock': 300,
                'tags': ['lokal'], 'supplier': { 'name': 'PT Daun Hijau', 'leadTimeDays': 4 } },
              { 'sku': 'NR-GU-00003', 'name': 'Gula Aren 500g', 'price': 32000, 'stock': 80,
                'tags': ['promo'], 'supplier': { 'name': 'PT Sumber Makmur', 'leadTimeDays': 7 } },
              { 'sku': 'NR-AT-00007', 'name': 'Pena Biru', 'price': 4500, 'stock': 1500,
                'tags': [], 'supplier': { 'name': 'CV Alat Tulis', 'leadTimeDays': 2 } };

            INSERT INTO customers VALUES
              { 'name': 'Sari Wulandari', 'tier': 'gold',   'address': { 'city': 'Bandung',  'country': 'ID' }, 'joinedAt': '2024-03-11' },
              { 'name': 'Budi Santoso',   'tier': 'silver', 'address': { 'city': 'Medan',    'country': 'ID' }, 'joinedAt': '2025-01-20' },
              { 'name': 'Rina Hartati',   'tier': 'bronze', 'address': { 'city': 'Surabaya', 'country': 'ID' }, 'joinedAt': '2025-08-02' },
              { 'name': 'Agus Prasetyo',  'tier': 'bronze', 'address': { 'city': 'Jakarta',  'country': 'ID' }, 'joinedAt': '2026-01-15' };

            INSERT INTO orders VALUES
              { 'code': 'SO-001', 'customer': { 'name': 'Sari Wulandari', 'tier': 'gold' },
                'address': { 'city': 'Bandung' }, 'placedAt': '2026-01-05', 'status': 'paid',
                'tags': ['promo'], 'total': 250000,
                'lines': [ { 'sku': 'NR-KO-00042', 'qty': 2, 'lineTotal': 136000 },
                           { 'sku': 'NR-TE-00011', 'qty': 4, 'lineTotal': 96000 } ] },
              { 'code': 'SO-002', 'customer': { 'name': 'Budi Santoso', 'tier': 'silver' },
                'address': { 'city': 'Medan' }, 'placedAt': '2026-01-12', 'status': 'paid',
                'tags': ['retail'], 'total': 125000, 'discount': null,
                'lines': [ { 'sku': 'NR-GU-00003', 'qty': 3, 'lineTotal': 96000 } ] },
              { 'code': 'SO-003', 'customer': { 'name': 'Sari Wulandari', 'tier': 'gold' },
                'address': { 'city': 'Bandung' }, 'placedAt': '2026-02-02', 'status': 'shipped',
                'tags': ['promo', 'bulk'], 'total': 980000,
                'lines': [ { 'sku': 'NR-KO-00042', 'qty': 7, 'lineTotal': 476000 } ] },
              { 'code': 'SO-004', 'customer': { 'name': 'Rina Hartati', 'tier': 'bronze' },
                'address': { 'city': 'Surabaya' }, 'placedAt': '2026-02-14', 'status': 'cancelled',
                'tags': [], 'total': 45000,
                'lines': [ { 'sku': 'NR-AT-00007', 'qty': 10, 'lineTotal': 45000 } ] },
              { 'code': 'SO-005', 'customer': { 'name': 'Budi Santoso', 'tier': 'silver' },
                'address': { 'city': 'Medan' }, 'placedAt': '2026-03-01', 'status': 'paid',
                'tags': ['bulk'], 'total': 610000,
                'lines': [ { 'sku': 'NR-TE-00011', 'qty': 20, 'lineTotal': 480000 } ] },
              { 'code': 'SO-006', 'customer': { 'name': 'Agus Prasetyo', 'tier': 'bronze' },
                'address': { 'city': 'Jakarta' }, 'placedAt': '2026-03-09', 'status': 'shipped',
                'tags': ['retail'], 'total': 310000,
                'lines': [ { 'sku': 'NR-GU-00003', 'qty': 6, 'lineTotal': 192000 } ] },
              { 'code': 'SO-007', 'customer': { 'name': 'Agus Prasetyo', 'tier': 'bronze' },
                'address': { 'city': 'Jakarta' }, 'placedAt': '2026-03-20', 'status': 'pending',
                'tags': [], 'total': 75000,
                'lines': [ { 'sku': 'NR-AT-00007', 'qty': 15, 'lineTotal': 67500 } ] };
            """,
            [("orders", "address.city"), ("orders", "code"), ("products", "sku")]),

        new(
            "Content",
            "Posts, authors and comments — nested and array-heavy, the shape a CMS actually has.",
            ["authors", "posts", "comments"],
            """
            INSERT INTO authors VALUES
              { 'handle': 'kangfadhil', 'name': 'Kang Fadhil', 'bio': 'Founder, Gravicode Studios.',
                'links': { 'site': 'https://gravicode.com' } },
              { 'handle': 'sari',       'name': 'Sari Wulandari', 'bio': 'Writes about databases.' };

            INSERT INTO posts VALUES
              { 'slug': 'kenapa-dokumen', 'title': 'Kenapa basis data dokumen?',
                'author': { 'handle': 'kangfadhil', 'name': 'Kang Fadhil' },
                'publishedAt': '2026-02-01', 'status': 'published',
                'tags': ['database', 'indonesia'],
                'stats': { 'views': 4210, 'shares': 88 },
                'body': 'Dokumen menyimpan bentuk data apa adanya.' },
              { 'slug': 'cuteql-tour', 'title': 'A tour of CuteQL',
                'author': { 'handle': 'sari', 'name': 'Sari Wulandari' },
                'publishedAt': '2026-02-18', 'status': 'published',
                'tags': ['database', 'query'],
                'stats': { 'views': 1890, 'shares': 24 },
                'body': 'Paths, arrays and MISSING.' },
              { 'slug': 'draft-idea', 'title': 'Something not finished',
                'author': { 'handle': 'sari', 'name': 'Sari Wulandari' },
                'status': 'draft', 'tags': [], 'stats': { 'views': 0, 'shares': 0 },
                'body': '…' };

            INSERT INTO comments VALUES
              { 'post': 'kenapa-dokumen', 'by': 'Budi',  'at': '2026-02-02', 'body': 'Mantap!', 'approved': true },
              { 'post': 'kenapa-dokumen', 'by': 'Rina',  'at': '2026-02-03', 'body': 'Ada benchmark?', 'approved': true },
              { 'post': 'cuteql-tour',    'by': 'Agus',  'at': '2026-02-19', 'body': 'Thanks.', 'approved': false };
            """,
            [("posts", "slug"), ("posts", "status"), ("comments", "post")]),

        new(
            "Telemetry",
            "Device readings — a wide, mostly-numeric collection, for trying aggregates and ranges.",
            ["devices", "readings"],
            """
            INSERT INTO devices VALUES
              { 'deviceId': 'SNS-001', 'kind': 'temperature', 'site': { 'name': 'Gudang A', 'city': 'Bandung' },
                'installedAt': '2025-11-02', 'tags': ['indoor'] },
              { 'deviceId': 'SNS-002', 'kind': 'humidity',    'site': { 'name': 'Gudang A', 'city': 'Bandung' },
                'installedAt': '2025-11-02', 'tags': ['indoor'] },
              { 'deviceId': 'SNS-003', 'kind': 'temperature', 'site': { 'name': 'Gudang B', 'city': 'Surabaya' },
                'installedAt': '2026-01-19', 'tags': ['outdoor', 'exposed'] };

            INSERT INTO readings VALUES
              { 'deviceId': 'SNS-001', 'at': '2026-03-01T06:00:00', 'value': 24.4, 'unit': 'C', 'ok': true },
              { 'deviceId': 'SNS-001', 'at': '2026-03-01T12:00:00', 'value': 29.1, 'unit': 'C', 'ok': true },
              { 'deviceId': 'SNS-001', 'at': '2026-03-01T18:00:00', 'value': 26.0, 'unit': 'C', 'ok': true },
              { 'deviceId': 'SNS-002', 'at': '2026-03-01T06:00:00', 'value': 71.0, 'unit': '%', 'ok': true },
              { 'deviceId': 'SNS-002', 'at': '2026-03-01T12:00:00', 'value': 64.5, 'unit': '%', 'ok': true },
              { 'deviceId': 'SNS-003', 'at': '2026-03-01T12:00:00', 'value': 33.8, 'unit': 'C', 'ok': false };
            """,
            [("readings", "deviceId"), ("devices", "deviceId")]),

        new(
            "Task board",
            "Projects and tasks, with assignees and checklists. Small, and useful for trying updates.",
            ["projects", "tasks"],
            """
            INSERT INTO projects VALUES
              { 'key': 'CUTE', 'name': 'CuteDB 2.x', 'lead': { 'name': 'Kang Fadhil' }, 'active': true },
              { 'key': 'BRWS', 'name': 'CuteDB Browser', 'lead': { 'name': 'Kang Fadhil' }, 'active': true };

            INSERT INTO tasks VALUES
              { 'ref': 'CUTE-1', 'project': 'CUTE', 'title': 'Binary format', 'state': 'done',
                'points': 8, 'assignee': { 'name': 'Sari' }, 'labels': ['engine'],
                'checklist': [ { 'text': 'Length prefixes', 'done': true },
                               { 'text': 'CRC frames', 'done': true } ] },
              { 'ref': 'BRWS-1', 'project': 'BRWS', 'title': 'Query tabs', 'state': 'doing',
                'points': 5, 'assignee': { 'name': 'Budi' }, 'labels': ['ui'],
                'checklist': [ { 'text': 'Line numbers', 'done': true },
                               { 'text': 'Templates', 'done': false } ] },
              { 'ref': 'BRWS-2', 'project': 'BRWS', 'title': 'Jack the assistant', 'state': 'todo',
                'points': 13, 'labels': ['ui', 'ai'],
                'checklist': [ { 'text': 'Tool calls', 'done': false } ] };
            """,
            [("tasks", "project"), ("tasks", "state")]),
    ];

    /// <summary>Creates a database from a template, seeds it and builds its indexes.</summary>
    /// <returns>A line for the log saying what was made.</returns>
    public static string Apply(DatabaseTemplate template, Workspace workspace)
    {
        var database = workspace.Require();

        foreach (var collection in template.Collections)
        {
            database.Collection(collection);
        }

        var inserted = 0;
        foreach (var statement in QueryRunner.SplitStatements(template.Seed))
        {
            inserted += database.Execute(statement).AffectedCount;
        }

        foreach (var (collection, path) in template.Indexes)
        {
            database.Collection(collection).CreateIndex(path);
        }

        workspace.NotifySchemaChanged();

        return template.Collections.Count == 0
            ? "Created an empty database."
            : $"Created {template.Collections.Count} collections, {inserted:N0} documents "
                + $"and {template.Indexes.Count} indexes from the '{template.Name}' template.";
    }
}
