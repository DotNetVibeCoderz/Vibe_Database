using Faiss.Net;

namespace Faiss.Net.Gallery.Services;

/// <summary>
/// The data every demo shares: one text corpus with its embeddings, and one synthetic vector set
/// large enough for the geometry and benchmarking views.
/// <para>
/// Built once, lazily, on a background thread at startup. Two datasets rather than one because the
/// demos ask different questions: the corpus has to be readable so a returned neighbour can be
/// judged by eye, while the trade-off and probe views need tens of thousands of vectors before
/// timings mean anything.
/// </para>
/// </summary>
public sealed class Workspace
{
    /// <summary>Dimension of the synthetic vector set.</summary>
    public const int VectorDimension = 64;

    /// <summary>Size of the synthetic vector set. Large enough to time, small enough to build instantly.</summary>
    public const int VectorCount = 40_000;

    /// <summary>Held-out queries for the synthetic set.</summary>
    public const int QueryCount = 200;

    /// <summary>Neighbours evaluated everywhere in the app.</summary>
    public const int K = 10;

    public HashingEmbedder Embedder { get; } = new(96);

    /// <summary>Corpus embeddings, <c>documents * 96</c> row-major, unit length.</summary>
    public float[] CorpusVectors { get; private set; } = [];

    /// <summary>Exact cosine index over the corpus, used by the search demo and as its own reference.</summary>
    public IndexFlatIP CorpusIndex { get; private set; } = null!;

    /// <summary>Synthetic vectors for the geometry and measurement demos.</summary>
    public float[] Vectors { get; private set; } = [];

    /// <summary>Held-out queries, drawn from the same distribution as <see cref="Vectors"/>.</summary>
    public float[] Queries { get; private set; } = [];

    /// <summary>Exact neighbours of <see cref="Queries"/>, the reference every demo measures against.</summary>
    public SearchResult GroundTruth { get; private set; } = null!;

    /// <summary>Exact index over <see cref="Vectors"/>.</summary>
    public IndexFlatL2 ExactIndex { get; private set; } = null!;

    /// <summary>First two principal components of every vector, for the scatter plot.</summary>
    public float[] Projection { get; private set; } = [];

    /// <summary>How many vectors <see cref="Projection"/> covers. Every one of them.</summary>
    public int ProjectedCount { get; private set; }

    /// <summary>True once <see cref="Build"/> has finished.</summary>
    public bool IsReady { get; private set; }

    public void Build()
    {
        // --- corpus
        var documents = Corpus.Documents.Select(d => d.Text).ToArray();
        CorpusVectors = Embedder.EmbedAll(documents);
        CorpusIndex = new IndexFlatIP(Embedder.Dimension);
        CorpusIndex.Add(CorpusVectors);

        // --- synthetic set. Database and queries come from one draw and are then split, so the
        // queries are in-distribution. Generating them separately would make every approximate
        // index look far worse than it is and would teach the wrong lesson.
        var all = FaissNet.RandomClusteredVectors(
            VectorCount + QueryCount, VectorDimension, clusters: 160, spread: 0.07f, seed: 20240815);

        Vectors = all.AsSpan(0, VectorCount * VectorDimension).ToArray();
        Queries = all.AsSpan(VectorCount * VectorDimension, QueryCount * VectorDimension).ToArray();

        ExactIndex = new IndexFlatL2(VectorDimension);
        ExactIndex.Add(Vectors);
        GroundTruth = ExactIndex.Search(Queries, K);

        BuildProjection();
        IsReady = true;
    }

    /// <summary>
    /// Projects every vector onto the first two principal components.
    /// <para>
    /// The components are fitted on a sample — the covariance of a few thousand vectors already
    /// estimates the whole set's well, and fitting on all of them would cost a 64x64 covariance pass
    /// for no gain. The transform is then applied to <em>every</em> vector, which matters: a query's
    /// true neighbours are arbitrary ids, so a partial projection would leave most of them
    /// unplottable and the highlighted results would look wrong when they were merely missing.
    /// </para>
    /// </summary>
    private void BuildProjection()
    {
        const int fitSample = 4_000;
        var subset = Vectors.AsSpan(0, Math.Min(fitSample, VectorCount) * VectorDimension).ToArray();

        var pca = new PCAMatrix(VectorDimension, 2);
        pca.Train(subset);

        Projection = pca.Apply(Vectors);
        ProjectedCount = VectorCount;
    }

    /// <summary>Recall of a result against the shared ground truth.</summary>
    public double Recall(SearchResult candidate) => FaissNet.ComputeRecall(GroundTruth, candidate, K);
}
