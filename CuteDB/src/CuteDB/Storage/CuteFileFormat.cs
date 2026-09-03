using System.Buffers.Binary;
using System.Text;

namespace CuteDB.Storage;

/// <summary>
/// The opcode of a single frame in a CuteDB log file.
/// </summary>
/// <remarks>
/// These values are on-disk constants. Append new ones; never renumber.
/// </remarks>
public enum CuteOpcode : byte
{
    /// <summary>Insert or replace a document. Payload: 12-byte id, then the encoded body.</summary>
    Upsert = 1,

    /// <summary>Remove a document. Payload: 12-byte id.</summary>
    Delete = 2,

    /// <summary>Introduce a collection. Payload: varint-prefixed UTF-8 name.</summary>
    DefineCollection = 3,

    /// <summary>Remove a collection and everything in it. No payload.</summary>
    DropCollection = 4,

    /// <summary>Define a secondary index. Payload: kind, unique flag, name, path.</summary>
    DefineIndex = 5,

    /// <summary>Remove a secondary index. Payload: varint-prefixed UTF-8 name.</summary>
    DropIndex = 6,

    /// <summary>
    /// Marks a clean close. Its presence at the tail means the file was not interrupted mid-write,
    /// which lets the next open skip the "is the last frame torn?" question entirely.
    /// </summary>
    Checkpoint = 7,
}

/// <summary>
/// Constants and framing helpers for the CuteDB file format.
/// </summary>
/// <remarks>
/// <para>
/// A database file is a 64-byte header followed by a sequence of frames, each one an atomic
/// change. Writing is append-only, which is what makes crash safety cheap: a frame either landed
/// whole — its length and CRC agree — or it is garbage at the tail and gets discarded on the next
/// open. There is no separate write-ahead log because the file <em>is</em> the log; a compaction
/// pass is what turns accumulated history back into a compact file.
/// </para>
/// <para>
/// Frame header, 12 bytes: opcode (1), reserved (1), collection id (2), payload length (4),
/// payload CRC-32C (4). The collection id is resolved through the
/// <see cref="CuteOpcode.DefineCollection"/> frames that precede it, so collection names are
/// stored once rather than on every document.
/// </para>
/// </remarks>
public static class CuteFileFormat
{
    /// <summary>The eight magic bytes every CuteDB file starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "CUTEDB\0\0"u8;

    /// <summary>The format version this build writes.</summary>
    public const uint Version = 2;

    /// <summary>The size of the file header in bytes.</summary>
    public const int HeaderSize = 64;

    /// <summary>The size of a frame header in bytes.</summary>
    public const int FrameHeaderSize = 12;

    /// <summary>The largest payload a single frame may carry (16 MiB).</summary>
    public const int MaxPayloadSize = 16 * 1024 * 1024;

    /// <summary>The conventional file extension.</summary>
    public const string Extension = ".cute";

    /// <summary>Writes the 64-byte file header into <paramref name="destination"/>.</summary>
    public static void WriteHeader(Span<byte> destination, DateTime createdUtc)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException($"The header needs {HeaderSize} bytes.", nameof(destination));
        }

        destination[..HeaderSize].Clear();
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], createdUtc.Ticks);
    }

    /// <summary>Validates a file header and returns when the database was created.</summary>
    public static DateTime ReadHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new CuteCorruptionException(
                $"The file is only {source.Length} bytes, too short to be a CuteDB database.");
        }

        if (!source[..8].SequenceEqual(Magic))
        {
            throw new CuteCorruptionException(
                "This is not a CuteDB database: the file does not start with the CuteDB signature. " +
                "If it is a version 1 .jdb file, import it with CuteDatabase.ImportLegacyJdb.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        if (version != Version)
        {
            throw new CuteCorruptionException(
                $"This database was written by format version {version}, and this build reads version {Version}.");
        }

        return new DateTime(BinaryPrimitives.ReadInt64LittleEndian(source[16..]), DateTimeKind.Utc);
    }

    /// <summary>Writes a frame header into <paramref name="destination"/>.</summary>
    public static void WriteFrameHeader(Span<byte> destination, CuteOpcode opcode, ushort collectionId, ReadOnlySpan<byte> payload)
    {
        destination[0] = (byte)opcode;
        destination[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], collectionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Crc32C.Compute(payload));
    }

    /// <summary>Reads a frame header. Returns false when the bytes cannot be a valid header.</summary>
    public static bool TryReadFrameHeader(
        ReadOnlySpan<byte> source,
        out CuteOpcode opcode,
        out ushort collectionId,
        out int payloadLength,
        out uint payloadCrc)
    {
        opcode = default;
        collectionId = 0;
        payloadLength = 0;
        payloadCrc = 0;

        if (source.Length < FrameHeaderSize)
        {
            return false;
        }

        var rawOpcode = source[0];
        if (rawOpcode is < (byte)CuteOpcode.Upsert or > (byte)CuteOpcode.Checkpoint)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        if (length > MaxPayloadSize)
        {
            return false;
        }

        opcode = (CuteOpcode)rawOpcode;
        collectionId = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        payloadLength = (int)length;
        payloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        return true;
    }

    /// <summary>Encodes a length-prefixed UTF-8 string the way frame payloads carry names.</summary>
    public static void WriteName(CuteBufferWriter writer, string name) => writer.WriteVarString(name);

    /// <summary>Reads a length-prefixed UTF-8 string from a frame payload.</summary>
    public static string ReadName(ReadOnlySpan<byte> payload, out int consumed)
    {
        var length = (int)CuteBinary.ReadVarUInt(payload, out var prefix);
        if (payload.Length < prefix + length)
        {
            throw new CuteCorruptionException("A name in this frame runs past the end of its payload.");
        }

        consumed = prefix + length;
        return Encoding.UTF8.GetString(payload.Slice(prefix, length));
    }
}
