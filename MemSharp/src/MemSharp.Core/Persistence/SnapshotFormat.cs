namespace MemSharp.Persistence;

/// <summary>
/// Constants and the checksum for the <c>MEMSHRP</c> snapshot format.
/// </summary>
/// <remarks>
/// <para>
/// A length-prefixed binary format, not JSON. The engine this replaced serialised with
/// <c>TypeNameHandling.All</c>, which embedded CLR type names in the file - renaming a class or the
/// assembly broke every existing file on disk. Nothing here refers to a .NET type: the format is
/// a byte for the kind, then a payload whose shape that byte defines, which is why the Python, Go
/// and Node clients can read a snapshot without a .NET runtime anywhere.
/// </para>
/// <para>
/// Layout:
/// </para>
/// <code>
/// magic     8 bytes   "MEMSHRP1"
/// version   int32     format version
/// flags     int32     reserved, currently 0
/// count     int64     number of entries
/// checksum  uint64    FNV-1a over every byte after this field
/// entries   count x   type:byte, key:string, expiry:int64 (UTC ticks, 0 = none), payload
/// </code>
/// <para>
/// Strings are written with <see cref="System.IO.BinaryWriter"/>'s 7-bit-encoded length prefix
/// followed by UTF-8 bytes.
/// </para>
/// </remarks>
internal static class SnapshotFormat
{
    /// <summary>File magic. The trailing digit is the format generation.</summary>
    public static ReadOnlySpan<byte> Magic => "MEMSHRP1"u8;

    /// <summary>Current format version.</summary>
    public const int Version = 1;

    /// <summary>Byte offset of the checksum field, so the writer can seek back and fill it in.</summary>
    public const int ChecksumOffset = 8 + 4 + 4 + 8;

    /// <summary>Bytes before the first entry.</summary>
    public const int HeaderLength = ChecksumOffset + 8;

    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    /// <summary>
    /// FNV-1a over a span, chainable through <paramref name="seed"/>.
    /// </summary>
    /// <remarks>
    /// FNV rather than a cryptographic hash, and rather than a package reference: this detects a
    /// truncated or bit-rotted file, which is what a snapshot checksum is for. It is not a defence
    /// against a deliberately forged file, and a snapshot from an untrusted source should not be
    /// loaded on that basis alone.
    /// </remarks>
    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = FnvOffsetBasis)
    {
        ulong hash = seed;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash;
    }

    /// <summary>The starting value for a chained <see cref="Hash(ReadOnlySpan{byte}, ulong)"/>.</summary>
    public static ulong Seed => FnvOffsetBasis;
}

/// <summary>
/// A pass-through stream that hashes everything written through it.
/// </summary>
/// <remarks>
/// Lets the snapshot be written in one streaming pass and still carry a checksum, without buffering
/// the whole file in memory to hash it afterwards. A ten-million-key snapshot is hundreds of
/// megabytes; holding a second copy of it to compute a hash would be the largest allocation the
/// process ever makes.
/// </remarks>
internal sealed class HashingStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner;
    private ulong _hash = SnapshotFormat.Seed;

    public ulong Hash => _hash;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash = SnapshotFormat.Hash(buffer, _hash);
        _inner.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Span<byte> single = [value];
        Write(single);
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
