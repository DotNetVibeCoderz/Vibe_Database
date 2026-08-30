using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Faiss.Net.Core;

/// <summary>
/// SIMD-accelerated primitives on dense <see cref="float"/> vectors.
/// <para>
/// Every distance computation in FAISS.Net funnels through this class; index implementations never
/// hand-roll their own element loops. Each kernel dispatches to the widest register width the
/// hardware reports (AVX-512 -> AVX2/NEON -> portable <see cref="Vector{T}"/> -> scalar) and unrolls
/// to two independent accumulators so the CPU can keep multiple multiply-add pipelines busy.
/// </para>
/// </summary>
public static unsafe class VectorOps
{
    /// <summary>Widest SIMD width available at runtime, in <see cref="float"/> lanes.</summary>
    public static int SimdWidth =>
        Vector512.IsHardwareAccelerated ? 16 :
        Vector256.IsHardwareAccelerated ? 8 :
        Vector128.IsHardwareAccelerated ? 4 : Vector<float>.Count;

    /// <summary>Human readable description of the active SIMD path, for diagnostics and benchmarks.</summary>
    public static string SimdDescription =>
        Vector512.IsHardwareAccelerated ? "AVX-512 / 512-bit" :
        Vector256.IsHardwareAccelerated ? "AVX2 / 256-bit" :
        Vector128.IsHardwareAccelerated ? "SSE/NEON / 128-bit" :
        $"portable Vector<float> ({Vector<float>.Count} lanes)";

    // ------------------------------------------------------------------ L2

    /// <summary>Squared Euclidean distance. FAISS reports squared L2, so no square root is taken.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float L2Sqr(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vectors must have equal length.");
        fixed (float* pa = a)
        fixed (float* pb = b)
            return L2Sqr(pa, pb, a.Length);
    }

    public static float L2Sqr(float* a, float* b, int d)
    {
        int i = 0;
        float sum = 0f;

        if (Vector512.IsHardwareAccelerated && d >= 32)
        {
            Vector512<float> acc0 = Vector512<float>.Zero, acc1 = Vector512<float>.Zero;
            for (; i <= d - 32; i += 32)
            {
                var d0 = Vector512.Load(a + i) - Vector512.Load(b + i);
                var d1 = Vector512.Load(a + i + 16) - Vector512.Load(b + i + 16);
                acc0 += d0 * d0;
                acc1 += d1 * d1;
            }
            for (; i <= d - 16; i += 16)
            {
                var d0 = Vector512.Load(a + i) - Vector512.Load(b + i);
                acc0 += d0 * d0;
            }
            sum = Vector512.Sum(acc0 + acc1);
        }
        else if (Vector256.IsHardwareAccelerated && d >= 16)
        {
            Vector256<float> acc0 = Vector256<float>.Zero, acc1 = Vector256<float>.Zero;
            for (; i <= d - 16; i += 16)
            {
                var d0 = Vector256.Load(a + i) - Vector256.Load(b + i);
                var d1 = Vector256.Load(a + i + 8) - Vector256.Load(b + i + 8);
                acc0 += d0 * d0;
                acc1 += d1 * d1;
            }
            for (; i <= d - 8; i += 8)
            {
                var d0 = Vector256.Load(a + i) - Vector256.Load(b + i);
                acc0 += d0 * d0;
            }
            sum = Vector256.Sum(acc0 + acc1);
        }
        else if (Vector128.IsHardwareAccelerated && d >= 4)
        {
            var acc = Vector128<float>.Zero;
            for (; i <= d - 4; i += 4)
            {
                var d0 = Vector128.Load(a + i) - Vector128.Load(b + i);
                acc += d0 * d0;
            }
            sum = Vector128.Sum(acc);
        }

        for (; i < d; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }
        return sum;
    }

    // ------------------------------------------------------- Inner product

    /// <summary>Dot product of two equal-length vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float InnerProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vectors must have equal length.");
        fixed (float* pa = a)
        fixed (float* pb = b)
            return InnerProduct(pa, pb, a.Length);
    }

    public static float InnerProduct(float* a, float* b, int d)
    {
        int i = 0;
        float sum = 0f;

        if (Vector512.IsHardwareAccelerated && d >= 32)
        {
            Vector512<float> acc0 = Vector512<float>.Zero, acc1 = Vector512<float>.Zero;
            for (; i <= d - 32; i += 32)
            {
                acc0 += Vector512.Load(a + i) * Vector512.Load(b + i);
                acc1 += Vector512.Load(a + i + 16) * Vector512.Load(b + i + 16);
            }
            for (; i <= d - 16; i += 16)
                acc0 += Vector512.Load(a + i) * Vector512.Load(b + i);
            sum = Vector512.Sum(acc0 + acc1);
        }
        else if (Vector256.IsHardwareAccelerated && d >= 16)
        {
            Vector256<float> acc0 = Vector256<float>.Zero, acc1 = Vector256<float>.Zero;
            for (; i <= d - 16; i += 16)
            {
                acc0 += Vector256.Load(a + i) * Vector256.Load(b + i);
                acc1 += Vector256.Load(a + i + 8) * Vector256.Load(b + i + 8);
            }
            for (; i <= d - 8; i += 8)
                acc0 += Vector256.Load(a + i) * Vector256.Load(b + i);
            sum = Vector256.Sum(acc0 + acc1);
        }
        else if (Vector128.IsHardwareAccelerated && d >= 4)
        {
            var acc = Vector128<float>.Zero;
            for (; i <= d - 4; i += 4)
                acc += Vector128.Load(a + i) * Vector128.Load(b + i);
            sum = Vector128.Sum(acc);
        }

        for (; i < d; i++) sum += a[i] * b[i];
        return sum;
    }

    // ----------------------------------------------------------- L1 / Linf

    public static float L1(float* a, float* b, int d)
    {
        int i = 0;
        float sum = 0f;
        if (Vector256.IsHardwareAccelerated && d >= 8)
        {
            var acc = Vector256<float>.Zero;
            for (; i <= d - 8; i += 8)
                acc += Vector256.Abs(Vector256.Load(a + i) - Vector256.Load(b + i));
            sum = Vector256.Sum(acc);
        }
        for (; i < d; i++) sum += MathF.Abs(a[i] - b[i]);
        return sum;
    }

    public static float Linf(float* a, float* b, int d)
    {
        int i = 0;
        float best = 0f;
        if (Vector256.IsHardwareAccelerated && d >= 8)
        {
            var acc = Vector256<float>.Zero;
            for (; i <= d - 8; i += 8)
                acc = Vector256.Max(acc, Vector256.Abs(Vector256.Load(a + i) - Vector256.Load(b + i)));
            for (int lane = 0; lane < Vector256<float>.Count; lane++) best = MathF.Max(best, acc[lane]);
        }
        for (; i < d; i++) best = MathF.Max(best, MathF.Abs(a[i] - b[i]));
        return best;
    }

    /// <summary>Dispatches to the kernel for <paramref name="metric"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(float* a, float* b, int d, MetricType metric) => metric switch
    {
        MetricType.L2 => L2Sqr(a, b, d),
        MetricType.InnerProduct => InnerProduct(a, b, d),
        MetricType.L1 => L1(a, b, d),
        MetricType.Linf => Linf(a, b, d),
        _ => throw new NotSupportedException($"Metric {metric} is not supported."),
    };

    // --------------------------------------------------------------- Norms

    /// <summary>Squared L2 norm of a vector.</summary>
    public static float Norm2Sqr(float* a, int d)
    {
        int i = 0;
        float sum = 0f;
        if (Vector256.IsHardwareAccelerated && d >= 8)
        {
            var acc = Vector256<float>.Zero;
            for (; i <= d - 8; i += 8)
            {
                var v = Vector256.Load(a + i);
                acc += v * v;
            }
            sum = Vector256.Sum(acc);
        }
        for (; i < d; i++) sum += a[i] * a[i];
        return sum;
    }

    public static float Norm2Sqr(ReadOnlySpan<float> a)
    {
        fixed (float* pa = a) return Norm2Sqr(pa, a.Length);
    }

    /// <summary>Fills <paramref name="norms"/> with the squared L2 norm of each of <paramref name="n"/> rows.</summary>
    public static void ComputeNorms(float* x, int n, int d, float* norms)
    {
        for (int i = 0; i < n; i++) norms[i] = Norm2Sqr(x + (long)i * d, d);
    }

    /// <summary>
    /// L2-normalizes rows in place. Rows with zero norm are left untouched, matching
    /// <c>faiss.normalize_L2</c>, the standard preprocessing step for cosine similarity via
    /// <see cref="MetricType.InnerProduct"/>.
    /// </summary>
    public static void NormalizeL2(Span<float> x, int d)
    {
        int n = x.Length / d;
        fixed (float* px = x)
        {
            for (int i = 0; i < n; i++)
            {
                float* row = px + (long)i * d;
                float norm = MathF.Sqrt(Norm2Sqr(row, d));
                if (norm > 0f) Scale(row, 1f / norm, d);
            }
        }
    }

    // ----------------------------------------------------------- Arithmetic

    /// <summary>In place <c>a *= scale</c>.</summary>
    public static void Scale(float* a, float scale, int d)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && d >= 8)
        {
            var s = Vector256.Create(scale);
            for (; i <= d - 8; i += 8) Vector256.Store(Vector256.Load(a + i) * s, a + i);
        }
        for (; i < d; i++) a[i] *= scale;
    }

    /// <summary>In place <c>y += alpha * x</c> (BLAS axpy).</summary>
    public static void AxPy(float alpha, float* x, float* y, int d)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && d >= 8)
        {
            var a = Vector256.Create(alpha);
            for (; i <= d - 8; i += 8)
                Vector256.Store(Vector256.Load(y + i) + a * Vector256.Load(x + i), y + i);
        }
        for (; i < d; i++) y[i] += alpha * x[i];
    }

    /// <summary>In place <c>a += b</c>.</summary>
    public static void Add(float* a, float* b, int d)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && d >= 8)
            for (; i <= d - 8; i += 8)
                Vector256.Store(Vector256.Load(a + i) + Vector256.Load(b + i), a + i);
        for (; i < d; i++) a[i] += b[i];
    }

    /// <summary>Writes <c>result = a - b</c>.</summary>
    public static void Sub(float* a, float* b, float* result, int d)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && d >= 8)
            for (; i <= d - 8; i += 8)
                Vector256.Store(Vector256.Load(a + i) - Vector256.Load(b + i), result + i);
        for (; i < d; i++) result[i] = a[i] - b[i];
    }
}
