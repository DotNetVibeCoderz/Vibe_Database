using CuteDB.Storage;

namespace CuteDB.Tests;

/// <summary>Creates a database on a throwaway path and deletes the file afterwards.</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _path;

    public TempDatabase(CuteDatabaseOptions? options = null)
    {
        _path = Path.Combine(Path.GetTempPath(), "cutedb-tests", $"{Guid.NewGuid():N}.cute");
        Database = CuteDatabase.Open(_path, options);
    }

    public CuteDatabase Database { get; private set; }

    public string FilePath => _path;

    /// <summary>Closes and reopens the file, which is how persistence is actually verified.</summary>
    public CuteDatabase Reopen(CuteDatabaseOptions? options = null)
    {
        Database.Dispose();
        Database = CuteDatabase.Open(_path, options);
        return Database;
    }

    public void Dispose()
    {
        Database.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test over.
        }
    }
}

public class StorageTests
{
    [Fact]
    public void InsertThenReadBack()
    {
        using var db = CuteDatabase.CreateInMemory();
        var people = db.Collection("people");

        var id = people.Insert(CuteDocument.Parse("""{ "name": "Rina", "age": 29 }"""));
        var found = people.FindById(id);

        Assert.NotNull(found);
        Assert.Equal("Rina", found["name"].AsString);
        Assert.Equal(29, found["age"].AsInt32);
        Assert.Equal(id, found.Id);
        Assert.Equal(1, people.Count);
    }

    [Fact]
    public void InsertRejectsADuplicateId()
    {
        using var db = CuteDatabase.CreateInMemory();
        var people = db.Collection("people");

        var document = new CuteDocument();
        people.Insert(document);

        Assert.Throws<CuteDbException>(() => people.Insert(document));
    }

    [Fact]
    public void SaveReplacesInPlace()
    {
        using var db = CuteDatabase.CreateInMemory();
        var people = db.Collection("people");

        var document = CuteDocument.Parse("""{ "name": "Rina", "age": 29 }""");
        var id = people.Insert(document);

        document["age"] = 30;
        people.Save(document);

        Assert.Equal(1, people.Count);
        Assert.Equal(30, people.FindById(id)!["age"].AsInt32);
    }

    [Fact]
    public void DeleteRemovesTheDocument()
    {
        using var db = CuteDatabase.CreateInMemory();
        var people = db.Collection("people");

        var id = people.Insert(CuteDocument.Parse("""{ "name": "Rina" }"""));

        Assert.True(people.Delete(id));
        Assert.False(people.Delete(id));
        Assert.Null(people.FindById(id));
        Assert.Equal(0, people.Count);
    }

    [Fact]
    public void DeletedRowsAreReusedWithoutBreakingLookups()
    {
        using var db = CuteDatabase.CreateInMemory();
        var items = db.Collection("items");

        var ids = new List<CuteId>();
        for (var i = 0; i < 500; i++)
        {
            ids.Add(items.Insert(new CuteDocument().Set("n", i)));
        }

        // Punch holes, then refill them. Every surviving document must still be findable.
        for (var i = 0; i < 500; i += 2)
        {
            items.Delete(ids[i]);
        }

        for (var i = 0; i < 250; i++)
        {
            items.Insert(new CuteDocument().Set("n", 1000 + i));
        }

        Assert.Equal(500, items.Count);
        for (var i = 1; i < 500; i += 2)
        {
            Assert.Equal(i, items.FindById(ids[i])!["n"].AsInt32);
        }
    }

    [Fact]
    public void SurvivesCloseAndReopen()
    {
        using var temp = new TempDatabase();

        var orders = temp.Database.Collection("orders");
        for (var i = 0; i < 200; i++)
        {
            orders.Insert(new CuteDocument().Set("n", i).Set("city", i % 2 == 0 ? "Jakarta" : "Bandung"));
        }

        var reopened = temp.Reopen();
        var recovered = reopened.Collection("orders");

        Assert.Equal(200, recovered.Count);
        Assert.Equal(100, recovered.CountWhere("city = 'Jakarta'"));
    }

    [Fact]
    public void RecoversFromATornTailWithoutLosingEarlierWrites()
    {
        using var temp = new TempDatabase();

        var notes = temp.Database.Collection("notes");
        for (var i = 0; i < 50; i++)
        {
            notes.Insert(new CuteDocument().Set("n", i));
        }

        temp.Database.Dispose();

        // Simulate a process killed mid-append: a partial frame at the end of the file.
        using (var stream = new FileStream(temp.FilePath, FileMode.Open, FileAccess.Write))
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write([(byte)CuteOpcode.Upsert, 0, 1, 0, 200, 0, 0, 0, 1, 2, 3, 4, 9, 9, 9]);
        }

        using var reopened = CuteDatabase.Open(temp.FilePath);

        Assert.True(reopened.DiscardedBytesOnOpen > 0, "The torn frame should have been discarded.");
        Assert.Equal(50, reopened.Collection("notes").Count);
    }

    [Fact]
    public void CompactShrinksAFileFullOfHistory()
    {
        using var temp = new TempDatabase(CuteDatabaseOptions.Fast);
        var counters = temp.Database.Collection("counters");

        var document = new CuteDocument().Set("value", 0);
        counters.Insert(document);

        // Every save appends a new frame; the file becomes almost entirely history.
        for (var i = 1; i <= 5_000; i++)
        {
            document["value"] = i;
            counters.Save(document);
        }

        temp.Database.Flush();
        var before = temp.Database.Stats().FileBytes;
        var reclaimed = temp.Database.Compact();
        var after = temp.Database.Stats().FileBytes;

        Assert.True(reclaimed > 0, "Compaction should have reclaimed something.");
        Assert.True(after < before / 10, $"Expected a much smaller file, went from {before} to {after}.");
        Assert.Equal(1, counters.Count);
        Assert.Equal(5_000, counters.FindById(document.Id)!["value"].AsInt32);

        // And the compacted file must still open.
        var reopened = temp.Reopen();
        Assert.Equal(5_000, reopened.Collection("counters").FindById(document.Id)!["value"].AsInt32);
    }

    [Fact]
    public void ReadOnlyDatabasesRefuseWrites()
    {
        using var temp = new TempDatabase();
        temp.Database.Collection("things").Insert(new CuteDocument().Set("a", 1));
        temp.Database.Dispose();

        using var readOnly = CuteDatabase.Open(temp.FilePath, new CuteDatabaseOptions { ReadOnly = true });

        Assert.Equal(1, readOnly.Collection("things").Count);
        Assert.Throws<CuteDbException>(() => readOnly.Collection("things").Insert(new CuteDocument()));
    }

    [Theory]
    [InlineData("short", "too short")]
    [InlineData(
        "this is definitely not a CuteDB file, it is just a long enough run of ordinary text to " +
        "reach past the sixty-four byte header",
        "not a CuteDB database")]
    public void RefusesToOpenAFileThatIsNotADatabase(string contents, string expectedHint)
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-a-db-{Guid.NewGuid():N}.cute");
        File.WriteAllText(path, contents);

        try
        {
            var error = Assert.Throws<CuteCorruptionException>(() => CuteDatabase.Open(path));
            Assert.Contains(expectedHint, error.Message, StringComparison.Ordinal);

            // The failed open must not have kept the file locked.
            File.Delete(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void RefusesAVersion1FileAndSaysWhatToDoAboutIt()
    {
        // samples/data/my_cute.jdb is a real CuteDB 1.x database: Newtonsoft JSON with
        // TypeNameHandling.All. Version 2 cannot read it, and the thing that matters is that it
        // says so usefully rather than reporting a generic corruption.
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "data", "my_cute.jdb");

        if (!File.Exists(path))
        {
            // The fixture is large; a shallow or partial checkout may not have it.
            return;
        }

        var error = Assert.Throws<CuteCorruptionException>(() => CuteDatabase.Open(path));

        Assert.Contains("not a CuteDB database", error.Message, StringComparison.Ordinal);
        Assert.Contains(".jdb", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BulkInsertIsCorrectAndKeepsMemoryProportional()
    {
        using var db = CuteDatabase.CreateInMemory();
        var products = db.Collection("products");

        var documents = Enumerable.Range(0, 50_000)
            .Select(i => new CuteDocument()
                .Set("sku", $"SKU-{i:D6}")
                .Set("price", i * 100)
                .Set("tags", CuteValue.ArrayOf(CuteValue.String("a"), CuteValue.String("b"))));

        var inserted = products.InsertMany(documents);
        var stats = products.Stats();

        Assert.Equal(50_000, inserted);
        Assert.Equal(50_000, products.Count);
        Assert.Equal(0, stats.DeadBytes);

        // Slabs are bump-allocated, so reserved memory should track live bytes closely rather
        // than ballooning. Allowing 2x covers the partially filled tail slab.
        Assert.True(
            stats.ReservedBytes < (stats.LiveBytes * 2) + SlabAllocator.DefaultSlabSize,
            $"Reserved {stats.ReservedBytes} for {stats.LiveBytes} live bytes.");
    }

    [Fact]
    public void ConcurrentReadersAndWritersStayConsistent()
    {
        using var db = CuteDatabase.CreateInMemory();
        var events = db.Collection("events");

        for (var i = 0; i < 1_000; i++)
        {
            events.Insert(new CuteDocument().Set("n", i));
        }

        var errors = new List<Exception>();
        var writer = Task.Run(() =>
        {
            try
            {
                for (var i = 1_000; i < 5_000; i++)
                {
                    events.Insert(new CuteDocument().Set("n", i));
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 200; i++)
                {
                    _ = events.CountWhere("n < 1000");
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        })).ToArray();

        Task.WaitAll([writer, .. readers]);

        Assert.Empty(errors);
        Assert.Equal(5_000, events.Count);
        Assert.Equal(1_000, events.CountWhere("n < 1000"));
    }
}

public class IndexTests
{
    private static CuteDatabase Seeded(out CuteCollection collection)
    {
        var db = CuteDatabase.CreateInMemory();
        collection = db.Collection("orders");

        var cities = new[] { "Jakarta", "Bandung", "Surabaya", "Medan" };
        collection.InsertMany(Enumerable.Range(0, 10_000).Select(i => new CuteDocument()
            .Set("n", i)
            .Set("city", cities[i % cities.Length])
            .Set("total", i * 1_000)
            .Set("tags", i % 3 == 0
                ? CuteValue.ArrayOf(CuteValue.String("promo"), CuteValue.String("bulk"))
                : CuteValue.ArrayOf(CuteValue.String("retail")))));

        return db;
    }

    [Fact]
    public void EqualityLookupUsesTheIndexAndReturnsTheSameRows()
    {
        using var db = Seeded(out var orders);

        var withoutIndex = orders.CountWhere("city = 'Bandung'");
        orders.CreateIndex("city");
        var withIndex = orders.CountWhere("city = 'Bandung'");

        Assert.Equal(2_500, withoutIndex);
        Assert.Equal(withoutIndex, withIndex);
        Assert.Equal("Index seek", db.Explain("SELECT * FROM orders WHERE city = 'Bandung'").Strategy);
    }

    [Fact]
    public void RangeLookupMatchesAScan()
    {
        using var db = Seeded(out var orders);

        var expected = orders.CountWhere("total >= 100000 AND total < 200000");
        orders.CreateIndex("total");

        Assert.Equal(expected, orders.CountWhere("total >= 100000 AND total < 200000"));
    }

    [Fact]
    public void ArrayFieldsIndexEachElement()
    {
        using var db = Seeded(out var orders);
        orders.CreateIndex("tags");

        var promo = orders.CountWhere("tags = 'promo'");

        Assert.Equal(3_334, promo);
        Assert.Equal(promo, orders.Find("tags = 'promo'").Count);
    }

    [Fact]
    public void IndexesStayCorrectAcrossUpdatesAndDeletes()
    {
        using var db = Seeded(out var orders);
        orders.CreateIndex("city");

        db.Execute("UPDATE orders SET city = 'Bali' WHERE n < 100");
        db.Execute("DELETE FROM orders WHERE n >= 9900");

        Assert.Equal(100, orders.CountWhere("city = 'Bali'"));
        Assert.Equal(9_900, orders.Count);

        // Cross-check against an unindexed collection holding the same data.
        using var control = CuteDatabase.CreateInMemory();
        var mirror = control.Collection("orders");
        mirror.InsertMany(orders.All().Select(d => d.DeepClone()));

        foreach (var city in new[] { "Jakarta", "Bandung", "Surabaya", "Medan", "Bali" })
        {
            Assert.Equal(
                mirror.CountWhere($"city = '{city}'"),
                orders.CountWhere($"city = '{city}'"));
        }
    }

    [Fact]
    public void UniqueIndexRejectsDuplicates()
    {
        using var db = CuteDatabase.CreateInMemory();
        var users = db.Collection("users");
        users.CreateIndex("email", unique: true);

        users.Insert(CuteDocument.Parse("""{ "email": "a@example.com" }"""));

        Assert.Throws<CuteDbException>(() =>
            users.Insert(CuteDocument.Parse("""{ "email": "a@example.com" }""")));
    }

    [Fact]
    public void UniqueIndexIsSparse()
    {
        using var db = CuteDatabase.CreateInMemory();
        var users = db.Collection("users");
        users.CreateIndex("email", unique: true);

        // Two documents with no email at all are not duplicates of each other.
        users.Insert(CuteDocument.Parse("""{ "name": "no email" }"""));
        users.Insert(CuteDocument.Parse("""{ "name": "also none" }"""));

        Assert.Equal(2, users.Count);
    }

    [Fact]
    public void IndexesSurviveReopening()
    {
        using var temp = new TempDatabase();
        var orders = temp.Database.Collection("orders");
        orders.InsertMany(Enumerable.Range(0, 100).Select(i => new CuteDocument().Set("city", $"City{i % 5}")));
        orders.CreateIndex("city");

        var reopened = temp.Reopen();
        var recovered = reopened.Collection("orders");

        Assert.Single(recovered.Indexes);
        Assert.Equal("city", recovered.Indexes[0].Path);
        Assert.Equal(20, recovered.CountWhere("city = 'City3'"));
    }
}
