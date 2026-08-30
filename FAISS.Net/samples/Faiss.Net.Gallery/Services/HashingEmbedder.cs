using Faiss.Net;

namespace Faiss.Net.Gallery.Services;

/// <summary>
/// Turns text into vectors with the hashing trick: every token is deterministically mapped to a
/// pseudo-random unit direction, and a document is the normalized sum of its tokens' directions.
/// <para>
/// This is a real embedding, not a placeholder. Documents sharing vocabulary end up with a high
/// cosine similarity, so the search demo genuinely retrieves by meaning-adjacent wording rather than
/// by substring match, and the neighbours it returns are explainable by looking at the text. It is
/// chosen over a learned model for one reason: the Gallery must run offline with no download, no
/// native runtime, and no multi-hundred-megabyte weight file.
/// </para>
/// <para>
/// What it does not do is capture meaning beyond word overlap — "fast" and "quick" are unrelated
/// directions here. A sentence-transformer would fix that and would drop straight into the same
/// index; the retrieval side of this app would not change by a line.
/// </para>
/// </summary>
public sealed class HashingEmbedder
{
    private readonly int _dimension;

    /// <summary>Tokens carrying no topical signal. Left in, they pull every document toward one point.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "for", "from", "has", "have",
        "in", "into", "is", "it", "its", "of", "on", "or", "that", "the", "their", "then", "there",
        "these", "they", "this", "to", "was", "were", "when", "which", "while", "with", "you", "your",
    };

    public HashingEmbedder(int dimension = 96) => _dimension = dimension;

    /// <summary>Embedding dimension.</summary>
    public int Dimension => _dimension;

    /// <summary>Embeds one document into a unit vector.</summary>
    public float[] Embed(string text)
    {
        var vector = new float[_dimension];
        Accumulate(text, vector);
        Normalize(vector);
        return vector;
    }

    /// <summary>Embeds a batch into one flat row-major buffer, ready to hand to an index.</summary>
    public float[] EmbedAll(IReadOnlyList<string> documents)
    {
        var flat = new float[(long)documents.Count * _dimension];
        for (int i = 0; i < documents.Count; i++)
        {
            var row = flat.AsSpan(i * _dimension, _dimension);
            Accumulate(documents[i], row);
            Normalize(row);
        }
        return flat;
    }

    private void Accumulate(string text, Span<float> vector)
    {
        foreach (string token in Tokenize(text))
        {
            uint seed = Fnv1a(token);
            // Rarer tokens carry more signal, so weight decays with token length in a way that keeps
            // topical words ("quantization") ahead of generic ones ("data").
            float weight = 1f + MathF.Min(token.Length, 12) * 0.06f;

            for (int j = 0; j < vector.Length; j++)
            {
                uint mixed = Mix(seed, (uint)j);
                // Map the hash to [-1, 1]: a signed direction, so unrelated tokens cancel instead of
                // piling up in the same corner of the space.
                float component = (mixed & 0xFFFF) / 32767.5f - 1f;
                vector[j] += component * weight;
            }
        }
    }

    private static void Normalize(Span<float> vector)
    {
        float norm = 0;
        foreach (float v in vector) norm += v * v;
        norm = MathF.Sqrt(norm);
        if (norm <= 0) return;
        for (int i = 0; i < vector.Length; i++) vector[i] /= norm;
    }

    /// <summary>Lowercased word tokens, stop words and single letters removed.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool isWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.');
            if (isWord && start < 0) start = i;
            else if (!isWord && start >= 0)
            {
                string token = text[start..i].Trim('.').ToLowerInvariant();
                start = -1;
                if (token.Length > 1 && !StopWords.Contains(token)) yield return token;
            }
        }
    }

    private static uint Fnv1a(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619;
        }
        return hash;
    }

    /// <summary>Mixes a token hash with a dimension index into a well-distributed value.</summary>
    private static uint Mix(uint seed, uint index)
    {
        uint h = seed ^ (index * 2654435761u);
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        h *= 3266489917u;
        h ^= h >> 16;
        return h;
    }
}
