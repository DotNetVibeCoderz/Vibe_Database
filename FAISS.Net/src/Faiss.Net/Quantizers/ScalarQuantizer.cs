using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Faiss.Net;

/// <summary>
/// Encoding used by <see cref="ScalarQuantizer"/>. Names mirror <c>faiss.ScalarQuantizer.QT_*</c>.
/// </summary>
public enum ScalarQuantizerType
{
    /// <summary>Half precision. 2 bytes per dimension, near-lossless, no training needed.</summary>
    Float16,

    /// <summary>8 bits per dimension against one global range. 4x smaller than float32.</summary>
    Uniform8Bit,

    /// <summary>8 bits per dimension with a per-dimension range. Same size as <see cref="Uniform8Bit"/>, better accuracy.</summary>
    PerDimension8Bit,

    /// <summary>4 bits per dimension with a per-dimension range. 8x smaller; noticeably lossy.</summary>
    PerDimension4Bit,
}

/// <summary>
/// Scalar quantizer: compresses each dimension independently to a small integer.
/// <para>
/// Where product quantization gives the largest compression at real accuracy cost, scalar
/// quantization is the cheap, safe option — it needs no clustering, trains in one pass over the
/// data (just min/max per dimension), and 8-bit codes typically lose well under a percent of recall
/// while cutting memory 4x. That combination makes it the usual first thing to try when an index no
/// longer fits comfortably in RAM.
/// </para>
/// <para>
/// Distances are computed by decoding a candidate into a small stack buffer and reusing the SIMD
/// kernels, which keeps a single implementation correct for every metric instead of one hand-tuned
/// kernel per (type, metric) pair.
/// </para>
/// </summary>
public sealed class ScalarQuantizer
{
    /// <summary>Vector dimension.</summary>
    public int D { get; private set; }

    /// <summary>Encoding in use.</summary>
    public ScalarQuantizerType Type { get; private set; }

    /// <summary>Bytes per encoded vector.</summary>
    public int CodeSize { get; private set; }

    /// <summary>Set once ranges have been learned (immediately true for <see cref="ScalarQuantizerType.Float16"/>).</summary>
    public bool IsTrained { get; private set; }

    private float[] _min = [];
    private float[] _scale = [];   // (max - min) per dimension, or one global value
    private float[] _step = [];    // scale / levels, precomputed so decode is one multiply-add

    public ScalarQuantizer(int d, ScalarQuantizerType type = ScalarQuantizerType.PerDimension8Bit)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d));
        D = d;
        Type = type;
        CodeSize = type switch
        {
            ScalarQuantizerType.Float16 => d * 2,
            ScalarQuantizerType.Uniform8Bit => d,
            ScalarQuantizerType.PerDimension8Bit => d,
            ScalarQuantizerType.PerDimension4Bit => (d + 1) / 2,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        IsTrained = type == ScalarQuantizerType.Float16;
    }

    /// <summary>Learns the value range. One pass, no clustering.</summary>
    public void Train(ReadOnlySpan<float> x)
    {
        if (Type == ScalarQuantizerType.Float16) { IsTrained = true; return; }

        int n = x.Length / D;
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));

        _min = new float[D];
        _scale = new float[D];
        var max = new float[D];
        _min.AsSpan().Fill(float.MaxValue);
        max.AsSpan().Fill(float.MinValue);

        for (int i = 0; i < n; i++)
        {
            var row = x.Slice(i * D, D);
            for (int j = 0; j < D; j++)
            {
                if (row[j] < _min[j]) _min[j] = row[j];
                if (row[j] > max[j]) max[j] = row[j];
            }
        }

        if (Type == ScalarQuantizerType.Uniform8Bit)
        {
            float globalMin = float.MaxValue, globalMax = float.MinValue;
            for (int j = 0; j < D; j++)
            {
                globalMin = MathF.Min(globalMin, _min[j]);
                globalMax = MathF.Max(globalMax, max[j]);
            }
            _min.AsSpan().Fill(globalMin);
            max.AsSpan().Fill(globalMax);
        }

        for (int j = 0; j < D; j++)
        {
            float range = max[j] - _min[j];
            // A constant dimension carries no information; a zero scale keeps decode exact there.
            _scale[j] = range > 0 ? range : 0f;
        }

        RebuildStep();
        IsTrained = true;
    }

    private int Levels => Type == ScalarQuantizerType.PerDimension4Bit ? 15 : 255;

    /// <summary>
    /// Folds the division by <see cref="Levels"/> into a per-dimension step, so decoding a value is
    /// a single fused multiply-add. Decoding sits in the innermost loop of every scalar-quantized
    /// search, and removing that division from it is worth several times the cost of storing the array.
    /// </summary>
    private void RebuildStep()
    {
        _step = new float[_scale.Length];
        float inverseLevels = 1f / Levels;
        for (int j = 0; j < _scale.Length; j++) _step[j] = _scale[j] * inverseLevels;
    }

    /// <summary>Encodes one vector.</summary>
    public void Encode(ReadOnlySpan<float> vector, Span<byte> code)
    {
        EnsureTrained();
        switch (Type)
        {
            case ScalarQuantizerType.Float16:
                for (int j = 0; j < D; j++)
                {
                    ushort bits = BitConverter.HalfToUInt16Bits((Half)vector[j]);
                    code[2 * j] = (byte)bits;
                    code[2 * j + 1] = (byte)(bits >> 8);
                }
                break;

            case ScalarQuantizerType.PerDimension4Bit:
                code[..CodeSize].Clear();
                for (int j = 0; j < D; j++)
                {
                    byte q = Quantize(vector[j], j);
                    if ((j & 1) == 0) code[j >> 1] |= q;
                    else code[j >> 1] |= (byte)(q << 4);
                }
                break;

            default:
                for (int j = 0; j < D; j++) code[j] = Quantize(vector[j], j);
                break;
        }
    }

    /// <summary>
    /// Decodes one vector back to floats.
    /// <para>
    /// This runs once per candidate in a scalar-quantized scan, so it is written against raw
    /// pointers with the range parameters hoisted out of the loop — the bounds checks and repeated
    /// field loads a straightforward version emits cost more than the arithmetic does.
    /// </para>
    /// </summary>
    public unsafe void Decode(ReadOnlySpan<byte> code, Span<float> output)
    {
        EnsureTrained();
        fixed (byte* pcode = code)
        fixed (float* pout = output)
        fixed (float* pmin = _min)
        fixed (float* pstep = _step)
        {
            switch (Type)
            {
                case ScalarQuantizerType.Float16:
                    // Assembled byte by byte rather than read as ushort*, so the on-disk code stays
                    // little-endian regardless of the host.
                    for (int j = 0; j < D; j++)
                        pout[j] = (float)BitConverter.UInt16BitsToHalf(
                            (ushort)(pcode[2 * j] | (pcode[2 * j + 1] << 8)));
                    break;

                case ScalarQuantizerType.PerDimension4Bit:
                    for (int j = 0; j < D; j++)
                    {
                        int q = (j & 1) == 0 ? pcode[j >> 1] & 0x0F : (pcode[j >> 1] >> 4) & 0x0F;
                        pout[j] = pmin[j] + q * pstep[j];
                    }
                    break;

                default:
                    Decode8Bit(pcode, pout, pmin, pstep);
                    break;
            }
        }
    }

    private byte Quantize(float value, int dimension)
    {
        float scale = _scale[dimension];
        if (scale <= 0) return 0;
        float normalized = (value - _min[dimension]) / scale;
        int level = (int)MathF.Round(normalized * Levels);
        return (byte)Math.Clamp(level, 0, Levels);
    }

    /// <summary>
    /// Per-dimension offsets. Exposed so a scanning loop can pin them once for a whole list instead
    /// of once per candidate; see <see cref="DecodeUnchecked"/>.
    /// </summary>
    public float[] Offsets => _min;

    /// <summary>Per-dimension steps, the companion of <see cref="Offsets"/>.</summary>
    public float[] Steps => _step;

    /// <summary>
    /// Decode with no validation and no pinning, for callers that have already pinned
    /// <see cref="Offsets"/> and <see cref="Steps"/> around a whole scan.
    /// <para>
    /// Decoding happens once per candidate, and at that rate the four <c>fixed</c> statements and the
    /// trained-state check in the safe overload cost more than the arithmetic they guard. Hoisting
    /// them out of the loop is worth several times the speed of the decode itself.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void DecodeUnchecked(byte* code, float* output, float* offsets, float* steps)
    {
        switch (Type)
        {
            case ScalarQuantizerType.Float16:
                for (int j = 0; j < D; j++)
                    output[j] = (float)BitConverter.UInt16BitsToHalf((ushort)(code[2 * j] | (code[2 * j + 1] << 8)));
                break;

            case ScalarQuantizerType.PerDimension4Bit:
                for (int j = 0; j < D; j++)
                {
                    int q = (j & 1) == 0 ? code[j >> 1] & 0x0F : (code[j >> 1] >> 4) & 0x0F;
                    output[j] = offsets[j] + q * steps[j];
                }
                break;

            default:
                Decode8Bit(code, output, offsets, steps);
                break;
        }
    }

    /// <summary>
    /// Vectorized 8-bit decode: <c>output = offset + code * step</c>, eight lanes at a time.
    /// <para>
    /// The byte-to-float widening is the expensive part, not the arithmetic — a scalar loop spends
    /// most of its time converting. Eight bytes are read as one <see cref="ulong"/> and widened
    /// through <c>byte -&gt; ushort -&gt; uint -&gt; float</c> in a few instructions, which is what
    /// closes most of the gap between a scalar-quantized scan and a raw float scan.
    /// </para>
    /// </summary>
    private unsafe void Decode8Bit(byte* code, float* output, float* offsets, float* steps)
    {
        int j = 0;
        if (Vector256.IsHardwareAccelerated && D >= 8)
        {
            for (; j <= D - 8; j += 8)
            {
                // Read exactly eight bytes: a 16-byte vector load would run past the end of the
                // final code in a packed buffer.
                ulong packed = Unsafe.ReadUnaligned<ulong>(code + j);
                var bytes = Vector128.CreateScalar(packed).AsByte();
                (var words, _) = Vector128.Widen(bytes);
                (var low, var high) = Vector128.Widen(words);

                var values = Vector256.ConvertToSingle(
                    Vector256.Create(low, high).AsInt32());

                var decoded = Vector256.Load(offsets + j) +
                              values * Vector256.Load(steps + j);
                Vector256.Store(decoded, output + j);
            }
        }
        for (; j < D; j++) output[j] = offsets[j] + code[j] * steps[j];
    }

    /// <summary>Encodes a batch, parallel across vectors.</summary>
    public unsafe void EncodeBatch(ReadOnlySpan<float> x, int n, Span<byte> codes)
    {
        if (n == 0) return;
        fixed (float* px = x)
        fixed (byte* pc = codes)
        {
            nint xp = (nint)px, cp = (nint)pc;
            int d = D, codeSize = CodeSize;
            if (n < 64)
            {
                for (int i = 0; i < n; i++)
                    Encode(new ReadOnlySpan<float>((float*)xp + (long)i * d, d),
                           new Span<byte>((byte*)cp + (long)i * codeSize, codeSize));
            }
            else
            {
                Parallel.For(0, n, i =>
                    Encode(new ReadOnlySpan<float>((float*)xp + (long)i * d, d),
                           new Span<byte>((byte*)cp + (long)i * codeSize, codeSize)));
            }
        }
    }

    /// <summary>Root-mean-square reconstruction error over a sample; a quick accuracy sanity check.</summary>
    public double MeasureError(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        Span<byte> code = new byte[CodeSize];
        Span<float> decoded = new float[D];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            var row = x.Slice(i * D, D);
            Encode(row, code);
            Decode(code, decoded);
            for (int j = 0; j < D; j++)
            {
                double diff = row[j] - decoded[j];
                sum += diff * diff;
            }
        }
        return Math.Sqrt(sum / Math.Max(1, (long)n * D));
    }

    private void EnsureTrained()
    {
        if (!IsTrained)
            throw new InvalidOperationException("ScalarQuantizer must be trained before encoding.");
    }

    public override string ToString() => $"SQ({Type}, d={D}, code={CodeSize}B)";

    // -------------------------------------------------------- Serialization

    public void Write(BinaryWriter writer)
    {
        writer.Write(D);
        writer.Write((int)Type);
        writer.Write(IsTrained);
        writer.Write(_min.Length);
        foreach (float v in _min) writer.Write(v);
        writer.Write(_scale.Length);
        foreach (float v in _scale) writer.Write(v);
    }

    public static ScalarQuantizer Read(BinaryReader reader)
    {
        int d = reader.ReadInt32();
        var type = (ScalarQuantizerType)reader.ReadInt32();
        var sq = new ScalarQuantizer(d, type) { IsTrained = reader.ReadBoolean() };
        sq._min = new float[reader.ReadInt32()];
        for (int i = 0; i < sq._min.Length; i++) sq._min[i] = reader.ReadSingle();
        sq._scale = new float[reader.ReadInt32()];
        for (int i = 0; i < sq._scale.Length; i++) sq._scale[i] = reader.ReadSingle();
        sq.RebuildStep();
        return sq;
    }
}
