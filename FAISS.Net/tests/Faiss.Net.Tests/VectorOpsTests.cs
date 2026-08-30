using Faiss.Net.Core;
using Faiss.Net.Utils;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>
/// Validates the SIMD kernels against straightforward scalar reference implementations.
/// <para>
/// These run at several dimensions on purpose: the kernels dispatch on width and unroll by 32, 16,
/// 8 and 4 lanes, so the interesting bugs live in the tail handling at dimensions that are not a
/// multiple of the register width.
/// </para>
/// </summary>
public class VectorOpsTests
{
    private static readonly int[] Dimensions = [1, 3, 4, 7, 8, 15, 16, 31, 32, 33, 63, 64, 127, 128, 129, 384, 768];

    private static float ScalarL2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += (a[i] - b[i]) * (a[i] - b[i]);
        return sum;
    }

    private static float ScalarIp(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    [Fact]
    public void L2SqrMatchesScalarAtEveryDimension()
    {
        var rng = new RandomGenerator(1);
        foreach (int d in Dimensions)
        {
            var a = new float[d];
            var b = new float[d];
            rng.FillGaussian(a);
            rng.FillGaussian(b);
            Assert.Equal(ScalarL2(a, b), VectorOps.L2Sqr(a, b), 3);
        }
    }

    [Fact]
    public void InnerProductMatchesScalarAtEveryDimension()
    {
        var rng = new RandomGenerator(2);
        foreach (int d in Dimensions)
        {
            var a = new float[d];
            var b = new float[d];
            rng.FillGaussian(a);
            rng.FillGaussian(b);
            Assert.Equal(ScalarIp(a, b), VectorOps.InnerProduct(a, b), 3);
        }
    }

    [Fact]
    public void L2SqrIsZeroForIdenticalVectors()
    {
        var rng = new RandomGenerator(3);
        var a = new float[128];
        rng.FillGaussian(a);
        Assert.Equal(0f, VectorOps.L2Sqr(a, a), 4);
    }

    [Fact]
    public void NormalizeL2ProducesUnitVectors()
    {
        var rng = new RandomGenerator(4);
        var data = new float[10 * 64];
        rng.FillGaussian(data);
        VectorOps.NormalizeL2(data, 64);

        for (int i = 0; i < 10; i++)
            Assert.Equal(1f, MathF.Sqrt(VectorOps.Norm2Sqr(data.AsSpan(i * 64, 64))), 4);
    }

    [Fact]
    public void NormalizeL2LeavesZeroVectorsUntouched()
    {
        var data = new float[8];
        VectorOps.NormalizeL2(data, 8);
        Assert.All(data, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ArithmeticKernelsMatchScalar()
    {
        var rng = new RandomGenerator(5);
        foreach (int d in Dimensions)
        {
            var a = new float[d];
            var b = new float[d];
            rng.FillGaussian(a);
            rng.FillGaussian(b);

            var expected = new float[d];
            for (int i = 0; i < d; i++) expected[i] = a[i] + 2.5f * b[i];

            unsafe
            {
                fixed (float* pa = a)
                fixed (float* pb = b)
                    VectorOps.AxPy(2.5f, pb, pa, d);
            }

            for (int i = 0; i < d; i++) Assert.Equal(expected[i], a[i], 3);
        }
    }
}

/// <summary>Verifies heap ordering, capacity and padding semantics in both directions.</summary>
public class KnnHeapTests
{
    [Fact]
    public void AscendingHeapKeepsSmallestAndSortsBestFirst()
    {
        var scores = new float[3];
        var ids = new long[3];
        var heap = new KnnHeap<AscendingOrder>(scores, ids);

        foreach (var (score, id) in new[] { (5f, 5L), (1f, 1L), (9f, 9L), (3f, 3L), (0.5f, 0L) })
            heap.Push(score, id);
        heap.Finish();

        Assert.Equal([0L, 1L, 3L], ids);
        Assert.Equal(0.5f, scores[0]);
    }

    [Fact]
    public void DescendingHeapKeepsLargest()
    {
        var scores = new float[3];
        var ids = new long[3];
        var heap = new KnnHeap<DescendingOrder>(scores, ids);

        foreach (var (score, id) in new[] { (5f, 5L), (1f, 1L), (9f, 9L), (3f, 3L) })
            heap.Push(score, id);
        heap.Finish();

        Assert.Equal([9L, 5L, 3L], ids);
    }

    [Fact]
    public void UnderfilledHeapPadsWithMinusOne()
    {
        var scores = new float[5];
        var ids = new long[5];
        var heap = new KnnHeap<AscendingOrder>(scores, ids);
        heap.Push(1f, 1);
        heap.Push(2f, 2);
        heap.Finish();

        Assert.Equal([1L, 2L, -1L, -1L, -1L], ids);
        Assert.Equal(float.MaxValue, scores[4]);
    }
}
