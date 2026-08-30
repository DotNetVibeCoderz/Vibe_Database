namespace Faiss.Net.IO;

/// <summary>
/// Tag identifying the concrete index type in a serialized file.
/// <para>
/// Values are part of the on-disk format and are therefore frozen: never renumber an existing
/// entry, only append. A file written by any 1.x build must stay readable by every later 1.x build.
/// </para>
/// </summary>
public enum IndexTypeCode : ushort
{
    Unknown = 0,

    Flat = 1,
    FlatL2 = 2,
    FlatIP = 3,

    IVFFlat = 10,
    IVFPQ = 11,
    IVFScalarQuantizer = 12,

    PQ = 20,
    ScalarQuantizer = 21,

    HNSWFlat = 30,
    NSGFlat = 31,

    IDMap = 40,
    IDMap2 = 41,
    PreTransform = 42,
    Replicas = 43,
    Shards = 44,

    BinaryFlat = 50,
    BinaryIVF = 51,
}

/// <summary>Tag identifying a <c>VectorTransform</c> in a serialized file. Append-only, like <see cref="IndexTypeCode"/>.</summary>
public enum TransformTypeCode : ushort
{
    Unknown = 0,
    RandomRotation = 1,
    OPQ = 2,
    Pca = 3,
    Normalization = 4,
}
