using System.Numerics;
using System.Runtime.CompilerServices;

namespace Faiss.Net.Binary;

/// <summary>
/// Hamming-distance kernels over packed bit vectors.
/// <para>
/// Binary embeddings — from hashing, from a binarized network, or from a PQ-style binarization —
/// are 32x smaller than float32 and compare with XOR plus population count, which modern CPUs
/// execute at one instruction per 64 bits. A 256-bit binary vector is 32 bytes and its distance to
/// another costs four instructions, which is why binary indexes remain useful even when a float
/// index would fit in memory.
/// </para>
/// </summary>
public static unsafe class HammingOps
{
    /// <summary>Hamming distance between two equally sized codes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Distance(byte* a, byte* b, int codeSize)
    {
        int distance = 0;
        int i = 0;

        // 64 bits per instruction; the tail handles code sizes that are not a multiple of 8 bytes.
        int words = codeSize / sizeof(ulong);
        var wa = (ulong*)a;
        var wb = (ulong*)b;
        for (; i < words; i++) distance += BitOperations.PopCount(wa[i] ^ wb[i]);

        for (int byteIndex = words * sizeof(ulong); byteIndex < codeSize; byteIndex++)
            distance += BitOperations.PopCount((uint)(a[byteIndex] ^ b[byteIndex]));

        return distance;
    }

    /// <summary>Hamming distance between two spans.</summary>
    public static int Distance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Codes must have equal length.");
        fixed (byte* pa = a)
        fixed (byte* pb = b)
            return Distance(pa, pb, a.Length);
    }

    /// <summary>Number of set bits in a code.</summary>
    public static int PopCount(ReadOnlySpan<byte> code)
    {
        int total = 0;
        foreach (byte b in code) total += BitOperations.PopCount((uint)b);
        return total;
    }

    /// <summary>
    /// Packs a float vector into bits by thresholding at zero — the standard sign binarization used
    /// after a random projection or a learned hashing layer.
    /// </summary>
    public static void Binarize(ReadOnlySpan<float> vector, Span<byte> code, float threshold = 0f)
    {
        code.Clear();
        for (int i = 0; i < vector.Length; i++)
            if (vector[i] > threshold)
                code[i >> 3] |= (byte)(1 << (i & 7));
    }

    /// <summary>Reads bit <paramref name="index"/> of a code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GetBit(ReadOnlySpan<byte> code, int index) => (code[index >> 3] & (1 << (index & 7))) != 0;

    /// <summary>Sets or clears bit <paramref name="index"/> of a code.</summary>
    public static void SetBit(Span<byte> code, int index, bool value)
    {
        if (value) code[index >> 3] |= (byte)(1 << (index & 7));
        else code[index >> 3] &= (byte)~(1 << (index & 7));
    }
}
