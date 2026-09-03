using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using CuteDB.Retail;
using CuteDB.Storage;
using LiteDB;
using Newtonsoft.Json;

namespace CuteDB.Benchmarks;

/// <summary>
/// What it costs to get documents into a database, measured against two reference points.
/// </summary>
/// <remarks>
/// <para>
/// LiteDB is here because it is the embedded document database most .NET developers would
/// otherwise reach for, and a claim about speed means nothing without something to compare
/// against. The Newtonsoft column is CuteDB v1's storage model — a <c>List&lt;object&gt;</c>
/// serialised with <c>TypeNameHandling.All</c> — which is what this rewrite replaced.
/// </para>
/// <para>
/// The comparison is deliberately like-for-like on the write path only. LiteDB is a B-tree store
/// that does not hold everything in memory, so it wins on databases larger than RAM and loses on
/// bulk load; saying that plainly is more useful than picking whichever benchmark flatters CuteDB.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 1, iterationCount: 5)]
public class WriteBenchmarks
{
    private CuteDocument[] _documents = null!;
    private BsonDocument[] _liteDocuments = null!;
    private string _directory = null!;

    /// <summary>Documents written per iteration.</summary>
    [Params(50_000)]
    public int Documents { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260903);
        _documents = [.. NusantaraRetail.GenerateCustomers(random, Documents)];

        // The same data in LiteDB's own model, built once so the benchmark measures storing rather
        // than converting.
        _liteDocuments = [.. _documents.Select(d => LiteDB.JsonSerializer.Deserialize(d.ToJson()).AsDocument)];

        _directory = Path.Combine(Path.GetTempPath(), "cutedb-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a benchmark run over.
        }
    }

    [Benchmark(Baseline = true, Description = "CuteDB, in memory")]
    public int CuteDbMemory()
    {
        using var database = CuteDatabase.CreateInMemory();
        return database.Collection("customers").InsertMany(_documents);
    }

    [Benchmark(Description = "CuteDB, to a file (buffered)")]
    public int CuteDbFileBuffered()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.cute");
        using (var database = CuteDatabase.Open(path, CuteDatabaseOptions.Fast))
        {
            return database.Collection("customers").InsertMany(_documents);
        }
    }

    [Benchmark(Description = "CuteDB, to a file (flush per batch)")]
    public int CuteDbFileFlushed()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.cute");
        using (var database = CuteDatabase.Open(path))
        {
            return database.Collection("customers").InsertMany(_documents);
        }
    }

    [Benchmark(Description = "LiteDB, to a file")]
    public int LiteDbFile()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.litedb");
        using var database = new LiteDatabase($"Filename={path};Connection=direct");
        return database.GetCollection("customers").InsertBulk(_liteDocuments);
    }

    [Benchmark(Description = "CuteDB v1 model: List + Newtonsoft")]
    public int LegacyModel()
    {
        // What version 1 did: keep boxed objects in a list, then serialise the whole thing with
        // type names embedded. Included to show what the rewrite actually bought.
        var storage = new List<object>(_documents.Length);
        foreach (var document in _documents)
        {
            storage.Add(document.Root);
        }

        var json = JsonConvert.SerializeObject(storage, new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All,
        });

        return json.Length > 0 ? storage.Count : 0;
    }
}

/// <summary>
/// Encoding and decoding one document — the operation every read and write is built on.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 8)]
public class SerializationBenchmarks
{
    private CuteDocument _document = null!;
    private byte[] _encoded = null!;
    private string _json = null!;
    private CutePath _shallowPath = null!;
    private CutePath _deepPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(20260903);
        var products = NusantaraRetail.GenerateProducts(random, 20)
            .Select(p => (Sku: p["sku"].AsString, Name: p["name"].AsString, Price: p["price"].AsDecimal))
            .ToArray();

        _document = NusantaraRetail
            .GenerateOrders(random, 1, products, [("C-1", "Sari", "Bandung", "gold")], ["ST-001"])
            .First();

        _encoded = CuteBinary.Encode(_document.AsValue());
        _json = _document.ToJson();
        _shallowPath = CutePath.Parse("status");
        _deepPath = CutePath.Parse("customer.tier");
    }

    [Benchmark(Baseline = true, Description = "encode to CuteDB binary")]
    public byte[] Encode() => CuteBinary.Encode(_document.AsValue());

    [Benchmark(Description = "decode a whole document")]
    public CuteValue Decode() => CuteBinary.Decode(_encoded);

    [Benchmark(Description = "read one top-level field, no decode")]
    public CuteValue ResolveShallow() => _shallowPath.ResolveEncoded(_encoded);

    [Benchmark(Description = "read one nested field, no decode")]
    public CuteValue ResolveDeep() => _deepPath.ResolveEncoded(_encoded);

    [Benchmark(Description = "decode the whole document, then read one field")]
    public CuteValue DecodeThenRead() => _deepPath.Resolve(CuteBinary.Decode(_encoded));

    [Benchmark(Description = "parse JSON text")]
    public CuteValue ParseJson() => CuteJson.Parse(_json);

    [Benchmark(Description = "write JSON text")]
    public string WriteJson() => CuteJson.Write(_document.AsValue());
}
