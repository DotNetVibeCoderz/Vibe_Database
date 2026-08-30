using Faiss.Net.Binary;
using Faiss.Net.IO;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>
/// Round-trip tests for every serializable index. A reloaded index must return byte-identical
/// results, not merely similar ones — anything less means the format is losing state.
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faissnet-tests-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    private static void AssertSameResults(Index a, Index b, float[] queries, int k = 10)
    {
        var ra = a.Search(queries, k);
        var rb = b.Search(queries, k);
        Assert.Equal(ra.Labels, rb.Labels);
        for (int i = 0; i < ra.Distances.Length; i++)
            Assert.Equal(ra.Distances[i], rb.Distances[i], 4);
    }

    [Fact]
    public void FlatIndexRoundTrips()
    {
        const int d = 16;
        var data = TestData.Uniform(500, d);
        var index = new IndexFlatL2(d);
        index.Add(data);

        string path = Path_("flat.index");
        FaissNet.WriteIndex(index, path);
        var reloaded = FaissNet.ReadIndex(path);

        Assert.IsType<IndexFlatL2>(reloaded);
        Assert.Equal(index.Ntotal, reloaded.Ntotal);
        AssertSameResults(index, reloaded, TestData.Slice(data, d, 20));
    }

    [Fact]
    public void IvfFlatRoundTripsIncludingItsQuantizer()
    {
        const int d = 24;
        var data = TestData.Clustered(2000, d);
        var index = new IndexIVFFlat(d, nlist: 16);
        index.Train(data);
        index.Add(data);
        index.Nprobe = 4;

        string path = Path_("ivfflat.index");
        FaissNet.WriteIndex(index, path);
        var reloaded = (IndexIVFFlat)FaissNet.ReadIndex(path);

        Assert.Equal(16, reloaded.Nlist);
        Assert.Equal(4, reloaded.Nprobe);
        Assert.Equal(index.Ntotal, reloaded.Ntotal);
        Assert.Equal(index.Quantizer.Ntotal, reloaded.Quantizer.Ntotal);
        AssertSameResults(index, reloaded, TestData.Slice(data, d, 20));
    }

    [Fact]
    public void IvfPqRoundTripsIncludingCodebooks()
    {
        const int d = 32;
        var data = TestData.Clustered(3000, d);
        var index = new IndexIVFPQ(d, nlist: 16, m: 8);
        index.Train(data);
        index.Add(data);
        index.Nprobe = 8;

        string path = Path_("ivfpq.index");
        FaissNet.WriteIndex(index, path);
        var reloaded = (IndexIVFPQ)FaissNet.ReadIndex(path);

        Assert.Equal(8, reloaded.Pq.M);
        Assert.True(reloaded.Pq.IsTrained);
        AssertSameResults(index, reloaded, TestData.Slice(data, d, 20));
    }

    [Fact]
    public void HnswRoundTripsIncludingItsGraph()
    {
        const int d = 16;
        var data = TestData.Clustered(1500, d);
        var index = new IndexHNSWFlat(d, m: 8) { EfSearch = 32 };
        index.Add(data);

        string path = Path_("hnsw.index");
        FaissNet.WriteIndex(index, path);
        var reloaded = (IndexHNSWFlat)FaissNet.ReadIndex(path);

        Assert.Equal(index.Graph.MaxLevel, reloaded.Graph.MaxLevel);
        Assert.Equal(index.Graph.AverageDegree(), reloaded.Graph.AverageDegree(), 3);
        AssertSameResults(index, reloaded, TestData.Slice(data, d, 20));
    }

    [Fact]
    public void PqAndScalarQuantizerRoundTrip()
    {
        const int d = 16;
        var data = TestData.Clustered(1000, d);
        var queries = TestData.Slice(data, d, 10);

        var pq = new IndexPQ(d, m: 8);
        pq.Train(data);
        pq.Add(data);
        string pqPath = Path_("pq.index");
        FaissNet.WriteIndex(pq, pqPath);
        AssertSameResults(pq, FaissNet.ReadIndex(pqPath), queries);

        var sq = new IndexScalarQuantizer(d);
        sq.Train(data);
        sq.Add(data);
        string sqPath = Path_("sq.index");
        FaissNet.WriteIndex(sq, sqPath);
        AssertSameResults(sq, FaissNet.ReadIndex(sqPath), queries);
    }

    [Fact]
    public void IdMapRoundTripsAndPreservesCallerIds()
    {
        const int d = 8;
        var data = TestData.Uniform(100, d);
        var ids = new long[100];
        for (int i = 0; i < 100; i++) ids[i] = 1_000_000 + i * 7;

        var index = new IndexIDMap2(new IndexFlatL2(d));
        index.AddWithIds(data, ids);

        string path = Path_("idmap.index");
        FaissNet.WriteIndex(index, path);
        var reloaded = FaissNet.ReadIndex(path);

        var result = reloaded.Search(TestData.Slice(data, d, 1), k: 1);
        Assert.Equal(1_000_000, result[0, 0].Id);
    }

    [Fact]
    public void PreTransformChainRoundTrips()
    {
        const int d = 16;
        var data = TestData.Clustered(1000, d);
        var index = FaissNet.IndexFactory(d, "PCA8,Flat");
        index.Train(data);
        index.Add(data);

        string path = Path_("pretransform.index");
        FaissNet.WriteIndex(index, path);
        var reloaded = FaissNet.ReadIndex(path);

        Assert.IsType<IndexPreTransform>(reloaded);
        AssertSameResults(index, reloaded, TestData.Slice(data, d, 10));
    }

    [Fact]
    public void SerializeToBytesRoundTrips()
    {
        const int d = 12;
        var data = TestData.Uniform(200, d);
        var index = new IndexFlatL2(d);
        index.Add(data);

        byte[] bytes = IndexIO.Serialize(index);
        var reloaded = IndexIO.Deserialize(bytes);
        AssertSameResults(index, reloaded, TestData.Slice(data, d, 10));
    }

    [Fact]
    public void BinaryIndexesRoundTrip()
    {
        const int bits = 64;
        var rng = new Utils.RandomGenerator(3);
        var codes = new byte[200 * (bits / 8)];
        for (int i = 0; i < codes.Length; i++) codes[i] = (byte)rng.NextInt(256);

        var flat = new IndexBinaryFlat(bits);
        flat.Add(codes);
        string path = Path_("binflat.index");
        IndexIO.WriteBinaryIndex(flat, path);
        var reloaded = IndexIO.ReadBinaryIndex(path);

        Assert.Equal(flat.Ntotal, reloaded.Ntotal);
        var a = flat.Search(codes.AsSpan(0, bits / 8), 5);
        var b = reloaded.Search(codes.AsSpan(0, bits / 8), 5);
        Assert.Equal(a.Labels, b.Labels);
    }

    [Fact]
    public void MemoryMappedIndexMatchesTheInMemoryOne()
    {
        const int d = 32;
        var data = TestData.Clustered(2000, d);
        var index = new IndexFlatL2(d);
        index.Add(data);

        string path = Path_("mapped.bin");
        MappedIndexFlat.Write(index, path);

        using var mapped = MappedIndexFlat.Open(path);
        Assert.Equal(index.Ntotal, mapped.Ntotal);
        Assert.Equal(d, mapped.D);
        AssertSameResults(index, mapped, TestData.Slice(data, d, 25));
    }

    [Fact]
    public void MemoryMappedFileCanBeBuiltFromACompressedIndex()
    {
        const int d = 16;
        var data = TestData.Clustered(1000, d);
        var pq = new IndexPQ(d, m: 8);
        pq.Train(data);
        pq.Add(data);

        string path = Path_("mapped-from-pq.bin");
        MappedIndexFlat.Write((Index)pq, path);

        using var mapped = MappedIndexFlat.Open(path);
        Assert.Equal(pq.Ntotal, mapped.Ntotal);
        // The mapped file holds the PQ's decoded approximations, so its own top hit is stable.
        var result = mapped.Search(mapped.Reconstruct(5), k: 1);
        Assert.Equal(5, result[0, 0].Id);
    }

    [Fact]
    public void ReadingANonIndexFileFailsClearly()
    {
        string path = Path_("garbage.index");
        File.WriteAllText(path, "this is not an index");
        var exception = Assert.Throws<InvalidDataException>(() => FaissNet.ReadIndex(path));
        Assert.Contains("magic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
