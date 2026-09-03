namespace MemSharp;

/// <summary>
/// The value kinds MemSharp stores. Every key has exactly one, fixed at creation: an operation
/// against the wrong kind fails with <see cref="WrongTypeException"/> rather than coercing.
/// </summary>
/// <remarks>
/// The numeric values are part of the on-disk snapshot format (<c>MEMSHRP1</c>). Never renumber an
/// existing member; append new kinds with the next free value and bump the format version if the
/// payload encoding of an existing kind changes.
/// </remarks>
public enum MemType : byte
{
    /// <summary>No value - the key does not exist.</summary>
    None = 0,

    /// <summary>A UTF-16 string. Also the numeric type: <c>INCR</c> parses and rewrites it.</summary>
    String = 1,

    /// <summary>An ordered sequence with O(1) push and pop at both ends.</summary>
    List = 2,

    /// <summary>A field-to-value map.</summary>
    Hash = 3,

    /// <summary>An unordered collection of distinct members.</summary>
    Set = 4,

    /// <summary>Distinct members ordered by a double score, with range queries by score or rank.</summary>
    SortedSet = 5,

    /// <summary>An append-only series of (timestamp, value) samples with optional retention.</summary>
    TimeSeries = 6,

    /// <summary>An append-only log of field maps, each with a monotonic <c>ms-seq</c> id.</summary>
    Stream = 7,
}
