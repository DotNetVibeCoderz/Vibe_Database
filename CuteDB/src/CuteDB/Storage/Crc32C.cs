using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace CuteDB.Storage;

/// <summary>
/// CRC-32C (Castagnoli, polynomial 0x1EDC6F41), the checksum on every frame in a CuteDB file.
/// </summary>
/// <remarks>
/// <para>
/// Castagnoli rather than the more familiar zlib CRC-32 for one reason: both x86-64 and ARM64
/// have had a single-instruction implementation of it for over a decade, so checksumming every
/// write costs essentially nothing and there is no temptation to make integrity checks optional.
/// The table-driven fallback is only reached on hardware without those instructions.
/// </para>
/// <para>
/// This is deliberately hand-rolled rather than taken from <c>System.IO.Hashing</c>: the core
/// library ships with no package dependencies, and eighty lines is a fair price for keeping it
/// that way.
/// </para>
/// </remarks>
public static class Crc32C
{
    private const uint Polynomial = 0x82F63B78; // 0x1EDC6F41 bit-reversed.

    private static readonly uint[] Table = BuildTable();

    /// <summary>Computes the CRC-32C of a buffer.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>Continues a running CRC-32C over another chunk.</summary>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var running = ~crc;

        if (Sse42.IsSupported)
        {
            running = AppendSse42(running, data);
        }
        else if (Crc32.IsSupported)
        {
            running = AppendArm(running, data);
        }
        else
        {
            running = AppendTable(running, data);
        }

        return ~running;
    }

    private static uint AppendSse42(uint running, ReadOnlySpan<byte> data)
    {
        var index = 0;

        if (Sse42.X64.IsSupported)
        {
            // The 64-bit form folds eight bytes per instruction; it takes and returns a 64-bit
            // value whose upper half is always zero.
            ulong wide = running;
            while (index + 8 <= data.Length)
            {
                wide = Sse42.X64.Crc32(wide, BitConverter.ToUInt64(data.Slice(index, 8)));
                index += 8;
            }

            running = (uint)wide;
        }

        while (index + 4 <= data.Length)
        {
            running = Sse42.Crc32(running, BitConverter.ToUInt32(data.Slice(index, 4)));
            index += 4;
        }

        while (index < data.Length)
        {
            running = Sse42.Crc32(running, data[index++]);
        }

        return running;
    }

    private static uint AppendArm(uint running, ReadOnlySpan<byte> data)
    {
        var index = 0;

        if (Crc32.Arm64.IsSupported)
        {
            while (index + 8 <= data.Length)
            {
                running = Crc32.Arm64.ComputeCrc32C(running, BitConverter.ToUInt64(data.Slice(index, 8)));
                index += 8;
            }
        }

        while (index + 4 <= data.Length)
        {
            running = Crc32.ComputeCrc32C(running, BitConverter.ToUInt32(data.Slice(index, 4)));
            index += 4;
        }

        while (index < data.Length)
        {
            running = Crc32.ComputeCrc32C(running, data[index++]);
        }

        return running;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AppendTable(uint running, ReadOnlySpan<byte> data)
    {
        var table = Table;
        foreach (var b in data)
        {
            running = table[(running ^ b) & 0xFF] ^ (running >> 8);
        }

        return running;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
