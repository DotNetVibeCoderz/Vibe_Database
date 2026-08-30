namespace Faiss.Net.Gallery.Services;

/// <summary>A document in the search demo's corpus.</summary>
public sealed record Document(int Id, string Topic, string Text);

/// <summary>
/// The corpus behind the search demo: short technical sentences across eight topics.
/// <para>
/// The text is written rather than generated. Generated filler would still cluster — the embedder
/// would happily separate lorem ipsum — but nobody can tell whether a returned neighbour is a
/// *good* one, and judging result quality is the whole point of a search demo. Real sentences with
/// overlapping vocabulary across topics (quantization appears under both compression and databases,
/// GPUs under both training and search) also give the index something genuinely ambiguous to get
/// right or wrong.
/// </para>
/// </summary>
public static class Corpus
{
    public static IReadOnlyList<Document> Documents { get; } = Build();

    /// <summary>Distinct topic names, in corpus order.</summary>
    public static IReadOnlyList<string> Topics { get; } =
        Documents.Select(d => d.Topic).Distinct().ToArray();

    /// <summary>Queries worth trying, offered in the UI so the demo is usable without inventing one.</summary>
    public static IReadOnlyList<string> SuggestedQueries { get; } =
    [
        "how do I make search use less memory",
        "graph traversal for nearest neighbours",
        "running similarity search on a GPU",
        "storing embeddings in a database",
        "reducing the dimension of vectors",
        "why is my recall low",
    ];

    private static Document[] Build()
    {
        var entries = new List<(string Topic, string Text)>();

        void Add(string topic, params string[] lines)
        {
            foreach (string line in lines) entries.Add((topic, line));
        }

        Add("indexing",
            "An inverted file index partitions the vector space into cells and searches only the cells nearest the query.",
            "Choosing nlist near the square root of the dataset size is a reasonable starting point for an IVF index.",
            "Raising nprobe visits more cells, which recovers recall at the cost of a longer search.",
            "A flat index compares the query against every stored vector and is exact by construction.",
            "The coarse quantizer of an IVF index is itself an index over the cell centroids.",
            "An unbalanced partition makes latency erratic because some cells hold far more vectors than others.",
            "Training an index learns its centroids and codebooks from a representative sample of the data.",
            "Adding vectors after training does not require retraining unless the distribution has shifted.",
            "A vector that sits just across a cell boundary is invisible unless that cell is probed.",
            "Index parameters that affect search can be retuned at any time, but build parameters cannot.",
            "Exhaustive search remains the right answer up to a few hundred thousand vectors.",
            "Sharding splits a corpus across several indexes and merges their results into one ranking.");

        Add("compression",
            "Product quantization splits a vector into sub-vectors and replaces each with a centroid index.",
            "A 128-dimensional float vector is 512 bytes; a 16-byte product-quantized code is 32 times smaller.",
            "Scalar quantization stores each dimension as one byte and typically costs less than a point of recall.",
            "Optimized product quantization learns a rotation that spreads variance evenly across subspaces.",
            "Encoding residuals from a cell centroid resolves far more detail than encoding raw vectors.",
            "The codebook of a product quantizer is a fixed cost, independent of how many vectors are stored.",
            "Asymmetric distance computation keeps the query in full precision and only compresses the database.",
            "Half precision halves memory and is nearly lossless for most embedding distributions.",
            "Compression trades recall for the ability to hold the dataset in memory at all.",
            "A binary code compared with XOR and popcount is the cheapest possible distance computation.",
            "Four-bit codes cut memory eightfold but visibly degrade ranking quality.",
            "The right compression level is the one where the index fits and recall is still acceptable.");

        Add("graphs",
            "A navigable small world graph reaches any node in a logarithmic number of hops.",
            "HNSW descends sparse upper layers to land near the query, then explores the base layer.",
            "The efSearch parameter is the width of the beam and is the main recall dial at query time.",
            "Higher graph degree improves recall and costs both memory and build time.",
            "The diversity heuristic keeps neighbours that point in different directions rather than the closest ones.",
            "Without a back-fill step the pruning heuristic leaves the graph too sparse to navigate.",
            "Graph indexes need no training and no partition that can go stale as data is added.",
            "Deleting a node from a proximity graph strands the links that point at it.",
            "Construction runs a full search for every inserted vector, so building is the expensive part.",
            "Graph search wins on latency at high recall and loses on memory footprint.",
            "The entry point of a graph search is the single node occupying the highest layer.",
            "A graph built on isotropic noise navigates poorly because every point is equidistant.");

        Add("hardware",
            "SIMD instructions compute several distance terms per cycle by operating on vector registers.",
            "AVX-512 processes sixteen single-precision lanes at once where AVX2 processes eight.",
            "Brute-force search is memory-bandwidth-bound, which is where a GPU has the largest advantage.",
            "Keeping top-k selection on the device avoids shipping the whole distance matrix back over the bus.",
            "Multiple accelerators can hold replicas of one index so query throughput scales with device count.",
            "Unrolling a distance loop into independent accumulators keeps several pipelines busy at once.",
            "Memory pooling avoids allocating a scratch buffer on every query.",
            "Batch search parallelizes across queries; a single query parallelizes across the database.",
            "A compressed index often scans faster than an uncompressed one simply by moving fewer bytes.",
            "Pinning a buffer once per list is worth more than micro-optimizing the loop inside it.",
            "Memory-mapped storage lets an index larger than RAM be searched without loading it.",
            "Thread count beyond the physical core count usually costs more than it returns.");

        Add("embeddings",
            "An embedding places semantically similar items near each other in a continuous space.",
            "Cosine similarity is the inner product of vectors that have been normalized to unit length.",
            "Normalizing both queries and stored vectors turns maximum inner product search into cosine search.",
            "Sentence embeddings from a transformer typically range from 384 to 1536 dimensions.",
            "Distances concentrate in high dimensions, which is why exact ranking becomes delicate.",
            "The intrinsic dimension of real embeddings is far lower than the number of coordinates.",
            "Principal component analysis reduces dimension by keeping the directions of greatest variance.",
            "A random rotation spreads energy evenly across subspaces before product quantization.",
            "Two embeddings from different models cannot be compared in the same index.",
            "Image and text embeddings can share a space if the model was trained to align them.",
            "Recomputing embeddings after a model upgrade means rebuilding every index that stores them.",
            "An embedding is only as good as the ranking it produces on your own data.");

        Add("retrieval",
            "Retrieval augmented generation grounds a language model in documents fetched by vector search.",
            "Chunk size decides whether a retrieved passage carries enough context to be useful.",
            "Hybrid search combines keyword matching with vector similarity and reranks the union.",
            "A cross-encoder reranker is accurate but too slow to run over the whole corpus.",
            "Recall at ten is the fraction of true neighbours that appear in the top ten results.",
            "Measuring recall requires exact ground truth, which means one flat scan over the corpus.",
            "Filtering by metadata before search is cheaper than filtering the results afterwards.",
            "Deduplication is a radius search: anything within a small distance is a near-duplicate.",
            "An index tuned on one query distribution can behave differently on another.",
            "Latency at the ninety-ninth percentile matters more than the average for interactive search.",
            "Returning stale results is usually worse than returning fewer results.",
            "The top result is what most users judge the system by.");

        Add("storage",
            "Persisting an index avoids paying the training and build cost on every process start.",
            "A versioned binary format lets files written by an older build stay readable.",
            "Memory-mapped files share physical pages between processes searching the same index.",
            "Serializing an index to bytes allows storing it in a database or sending it over a network.",
            "Rebuilding from source vectors is the safe fallback when a format changes incompatibly.",
            "Vector databases add filtering, replication and durability on top of an index like this one.",
            "Application ids rarely match index positions, so an id mapping layer is usually needed.",
            "Removing a vector renumbers positions unless ids are stored explicitly.",
            "Writing an index while it is being searched requires a copy or a lock.",
            "Disk-resident indexes trade cold-start latency for the ability to exceed memory.",
            "Backing up an index is backing up both the vectors and the parameters used to build it.",
            "A checksum on the file header catches truncated writes before they become confusing errors.");

        Add("operations",
            "Benchmark numbers are only comparable when both runs used identical data and ground truth.",
            "An index is faster than another only at equal recall; comparing at different recall proves nothing.",
            "Warming up before measuring keeps compilation and cold caches out of the reported number.",
            "Reporting recall, latency and memory together is the only honest summary of an index.",
            "A regression in the distance kernel shows up in every index at once.",
            "Reproducible seeds make an index build byte-identical, which makes regressions bisectable.",
            "Queries drawn from a different distribution than the database depress every recall number.",
            "Profiling before optimizing avoids tuning code that was never the bottleneck.",
            "Build time matters when the corpus changes daily and not at all when it changes yearly.",
            "Monitoring recall in production requires periodically computing exact answers for a sample.",
            "Capacity planning starts from bytes per vector multiplied by expected corpus growth.",
            "The simplest index that meets the requirement is the one to ship.");

        return [.. entries.Select((entry, index) => new Document(index, entry.Topic, entry.Text))];
    }
}
