using Faiss.Net.IO;
using Faiss.Net.Sparse;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>Sparse inner-product search, checked against brute-force dot products.</summary>
public class SparseTests
{
    private const int Vocabulary = 5000;

    /// <summary>Zipf-like sparse vectors: a few common dimensions, a long tail of rare ones.</summary>
    private static List<SparseVector> RandomSparse(int n, int nnz, int seed)
    {
        var random = new Random(seed);
        var vectors = new List<SparseVector>(n);
        for (int v = 0; v < n; v++)
        {
            var indices = new HashSet<int>();
            while (indices.Count < nnz) indices.Add((int)(Vocabulary * Math.Pow(random.NextDouble(), 2.5)));
            var sorted = indices.ToArray();
            vectors.Add(new SparseVector(sorted, sorted.Select(_ => (float)random.NextDouble()).ToArray()));
        }
        return vectors;
    }

    private static long[] BruteForce(List<SparseVector> data, SparseVector query, int k, Func<long, bool>? keep = null) =>
        data.Select((v, i) => (Id: (long)i, Score: v.Dot(query)))
            .Where(x => keep?.Invoke(x.Id) ?? true)
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Id)
            .Take(k).Select(x => x.Id).ToArray();

    [Fact]
    public void SparseSearchIsExact()
    {
        var data = RandomSparse(3000, 40, 1);
        var queries = RandomSparse(20, 30, 2);
        var index = new IndexSparseFlat(Vocabulary);
        index.Add(data);

        var result = index.Search(queries, 10);
        for (int q = 0; q < queries.Count; q++)
        {
            var expected = BruteForce(data, queries[q], 10);
            Assert.Equal(expected, result.LabelsFor(q)[..expected.Length].ToArray());
            Assert.Equal(data[(int)expected[0]].Dot(queries[q]), result.DistancesFor(q)[0], 4);
        }
    }

    [Fact]
    public void IncrementalAddsSelectorsAndDeletionsCompose()
    {
        var data = RandomSparse(2000, 25, 3);
        var queries = RandomSparse(10, 20, 4);
        var index = new IndexSparseFlat(Vocabulary);
        for (int start = 0; start < data.Count; start += 137)
            index.Add(data.Skip(start).Take(137).ToList());
        Assert.Equal(2000, index.Ntotal);

        var deleted = Enumerable.Range(0, 2000).Where(i => i % 5 == 0).Select(i => (long)i).ToList();
        Assert.Equal(deleted.Count, index.RemoveIds(deleted));
        var sel = IDSelector.Range(0, 1500);
        bool Keep(long id) => id % 5 != 0 && id < 1500;

        void Check()
        {
            var result = index.Search(queries, 10, sel);
            for (int q = 0; q < queries.Count; q++)
            {
                var expected = BruteForce(data, queries[q], 10, Keep);
                Assert.Equal(expected, result.LabelsFor(q)[..expected.Length].ToArray());
            }
        }

        Check();
        index.Compact();
        Check();
    }

    [Fact]
    public void CosineModeNormalizesAndRoundTrips()
    {
        var data = RandomSparse(500, 20, 5);
        var index = new IndexSparseFlat(Vocabulary, normalizeOnAdd: true);
        index.Add(data);
        index.RemoveIds([3, 7]);

        // A vector's cosine with itself is 1.
        var self = index.Search(data[10], 1);
        Assert.Equal(10, self.Labels[0]);
        Assert.Equal(1f, self.Distances[0], 4);

        string path = Path.Combine(Path.GetTempPath(), $"faissnet-sparse-{Guid.NewGuid():N}.index");
        try
        {
            IndexIO.WriteSparseIndex(index, path);
            var reloaded = IndexIO.ReadSparseIndex(path);
            Assert.True(reloaded.NormalizeOnAdd);
            Assert.Equal(2, reloaded.DeletedCount);
            var queries = RandomSparse(10, 15, 6);
            Assert.Equal(index.Search(queries, 5).Labels, reloaded.Search(queries, 5).Labels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidVectorsAreRejected()
    {
        var index = new IndexSparseFlat(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Add(new SparseVector([10], [1f])));
        Assert.Throws<ArgumentException>(() => index.Add(new SparseVector([1, 1], [1f, 2f])));
        Assert.Throws<ArgumentException>(() => index.Add(new SparseVector([1], [1f, 2f])));
    }
}
