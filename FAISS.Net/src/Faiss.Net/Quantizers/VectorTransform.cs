using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// A learned or fixed mapping applied to vectors before they reach an index. Mirrors
/// <c>faiss.VectorTransform</c>; chain one in front of any index with <see cref="IndexPreTransform"/>.
/// </summary>
public abstract class VectorTransform
{
    /// <summary>Input dimension.</summary>
    public int DIn { get; protected set; }

    /// <summary>Output dimension.</summary>
    public int DOut { get; protected set; }

    /// <summary>False until <see cref="Train"/> has run, for transforms that learn.</summary>
    public bool IsTrained { get; protected set; } = true;

    protected VectorTransform(int dIn, int dOut)
    {
        DIn = dIn;
        DOut = dOut;
    }

    /// <summary>Learns the transform from a sample. A no-op for fixed transforms.</summary>
    public virtual void Train(ReadOnlySpan<float> x) { }

    /// <summary>Transforms a batch into a caller-supplied buffer.</summary>
    public abstract void Apply(ReadOnlySpan<float> x, int n, Span<float> output);

    /// <summary>Transforms a batch, allocating the output.</summary>
    public float[] Apply(ReadOnlySpan<float> x)
    {
        int n = x.Length / DIn;
        var output = new float[(long)n * DOut];
        Apply(x, n, output);
        return output;
    }

    /// <summary>
    /// Maps back to the input space. Exact for orthonormal transforms; a least-squares
    /// approximation for dimensionality-reducing ones, which is what makes reconstruction through
    /// an <see cref="IndexPreTransform"/> approximate.
    /// </summary>
    public virtual void ReverseTransform(ReadOnlySpan<float> y, int n, Span<float> output) =>
        throw new NotSupportedException($"{GetType().Name} is not invertible.");

    protected internal abstract TransformTypeCode TypeCode { get; }

    protected internal abstract void Write(BinaryWriter writer);

    /// <summary>Reads any transform written by <see cref="Write"/>.</summary>
    public static VectorTransform Read(BinaryReader reader)
    {
        var code = (TransformTypeCode)reader.ReadUInt16();
        return code switch
        {
            TransformTypeCode.RandomRotation => LinearTransform.ReadBody(reader, new RandomRotationMatrix()),
            TransformTypeCode.OPQ => LinearTransform.ReadBody(reader, new OPQMatrix()),
            TransformTypeCode.Pca => LinearTransform.ReadBody(reader, new PCAMatrix()),
            TransformTypeCode.Normalization => NormalizationTransform.ReadBody(reader),
            _ => throw new InvalidDataException($"Unknown transform type code {code}."),
        };
    }
}

/// <summary>
/// Base for transforms of the form <c>y = A x + b</c>, with <c>A</c> stored row-major as
/// <c>dOut x dIn</c>. Application is a SIMD matrix product parallel over vectors.
/// </summary>
public abstract class LinearTransform : VectorTransform
{
    /// <summary>Row-major <c>dOut x dIn</c> matrix.</summary>
    public float[] A { get; protected set; } = [];

    /// <summary>Optional length-<c>dOut</c> bias; empty when unused.</summary>
    public float[] B { get; protected set; } = [];

    /// <summary>True when <c>A</c> has orthonormal rows, so the inverse is its transpose.</summary>
    protected bool IsOrthonormal { get; set; }

    protected LinearTransform(int dIn, int dOut) : base(dIn, dOut) { }

    public override void Apply(ReadOnlySpan<float> x, int n, Span<float> output)
    {
        if (!IsTrained)
            throw new InvalidOperationException($"{GetType().Name} must be trained before use.");
        MatrixOps.ApplyLinear(x, n, DIn, output, DOut, A, B);
    }

    public override void ReverseTransform(ReadOnlySpan<float> y, int n, Span<float> output)
    {
        // For an orthonormal A the inverse is A^T; otherwise A^T is the least-squares pseudo-inverse
        // of a row-orthonormal projection, which is the best available without storing a factorization.
        var at = MatrixOps.Transpose(A, DOut, DIn);
        if (B.Length == DOut)
        {
            var centered = new float[(long)n * DOut];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < DOut; j++)
                    centered[(long)i * DOut + j] = y[i * DOut + j] - B[j];
            MatrixOps.ApplyLinear(centered, n, DOut, output, DIn, at, []);
        }
        else
        {
            MatrixOps.ApplyLinear(y, n, DOut, output, DIn, at, []);
        }
    }

    protected internal override void Write(BinaryWriter writer)
    {
        writer.Write((ushort)TypeCode);
        writer.Write(DIn);
        writer.Write(DOut);
        writer.Write(IsTrained);
        writer.Write(IsOrthonormal);
        writer.Write(A.Length);
        foreach (float v in A) writer.Write(v);
        writer.Write(B.Length);
        foreach (float v in B) writer.Write(v);
    }

    internal static VectorTransform ReadBody(BinaryReader reader, LinearTransform target)
    {
        target.DIn = reader.ReadInt32();
        target.DOut = reader.ReadInt32();
        target.IsTrained = reader.ReadBoolean();
        target.IsOrthonormal = reader.ReadBoolean();
        target.A = new float[reader.ReadInt32()];
        for (int i = 0; i < target.A.Length; i++) target.A[i] = reader.ReadSingle();
        target.B = new float[reader.ReadInt32()];
        for (int i = 0; i < target.B.Length; i++) target.B[i] = reader.ReadSingle();
        return target;
    }
}

/// <summary>
/// Fixed random rotation. Rotating before product quantization spreads energy evenly across
/// subspaces, which matters because PQ assumes each subspace carries comparable variance — on raw
/// embeddings where a few dimensions dominate, this alone recovers a large part of the accuracy OPQ
/// would give, at zero training cost.
/// </summary>
public sealed class RandomRotationMatrix : LinearTransform
{
    public RandomRotationMatrix(int d, long seed = 1234) : this(d, d, seed) { }

    public RandomRotationMatrix(int dIn, int dOut, long seed = 1234) : base(dIn, dOut)
    {
        if (dOut > dIn)
            throw new ArgumentException("A random rotation cannot increase dimension.", nameof(dOut));
        var full = MatrixOps.RandomOrthonormal(dIn, seed);
        A = new float[(long)dOut * dIn];
        Array.Copy(full, A, A.Length); // first dOut orthonormal rows
        IsOrthonormal = dIn == dOut;
        IsTrained = true;
    }

    internal RandomRotationMatrix() : base(1, 1) { }

    protected internal override TransformTypeCode TypeCode => TransformTypeCode.RandomRotation;
}

/// <summary>
/// PCA projection, optionally with whitening. Reduces dimension by keeping the directions of
/// greatest variance, which both shrinks the index and speeds every distance computation.
/// </summary>
public sealed class PCAMatrix : LinearTransform
{
    /// <summary>
    /// Exponent applied to eigenvalues: <c>0</c> keeps a plain rotation, <c>-0.5</c> whitens
    /// (equalizes variance across output dimensions).
    /// </summary>
    public float EigenPower { get; set; }

    /// <summary>Variance captured by each retained component, after training.</summary>
    public double[] Eigenvalues { get; private set; } = [];

    public PCAMatrix(int dIn, int dOut, float eigenPower = 0f) : base(dIn, dOut)
    {
        if (dOut > dIn) throw new ArgumentException("PCA cannot increase dimension.", nameof(dOut));
        EigenPower = eigenPower;
        IsTrained = false;
    }

    internal PCAMatrix() : base(1, 1) { }

    public override void Train(ReadOnlySpan<float> x)
    {
        int n = x.Length / DIn;
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));

        var mean = new double[DIn];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < DIn; j++) mean[j] += x[i * DIn + j];
        for (int j = 0; j < DIn; j++) mean[j] /= n;

        var covariance = new double[(long)DIn * DIn];
        var centered = new double[DIn];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < DIn; j++) centered[j] = x[i * DIn + j] - mean[j];
            for (int a = 0; a < DIn; a++)
            {
                double va = centered[a];
                if (va == 0) continue;
                int row = a * DIn;
                for (int b = 0; b < DIn; b++) covariance[row + b] += va * centered[b];
            }
        }
        for (int i = 0; i < covariance.Length; i++) covariance[i] /= n;

        MatrixOps.SymmetricEigen(covariance, DIn, out double[] values, out double[] vectors);

        Eigenvalues = values.Take(DOut).ToArray();
        A = new float[(long)DOut * DIn];
        for (int o = 0; o < DOut; o++)
        {
            double scale = 1.0;
            if (EigenPower != 0f)
            {
                double eigenvalue = Math.Max(values[o], 1e-12);
                scale = Math.Pow(eigenvalue, EigenPower);
            }
            for (int j = 0; j < DIn; j++) A[(long)o * DIn + j] = (float)(vectors[(long)o * DIn + j] * scale);
        }

        // Centre the data as part of the transform: y = A (x - mean) = A x - A mean.
        B = new float[DOut];
        for (int o = 0; o < DOut; o++)
        {
            double bias = 0;
            for (int j = 0; j < DIn; j++) bias += A[(long)o * DIn + j] * mean[j];
            B[o] = (float)(-bias);
        }

        IsOrthonormal = EigenPower == 0f && DIn == DOut;
        IsTrained = true;
    }

    protected internal override TransformTypeCode TypeCode => TransformTypeCode.Pca;
}

/// <summary>
/// Optimized product quantization: learns the rotation that minimizes the quantization error of the
/// PQ that will follow it.
/// <para>
/// Training alternates two steps that each have a closed-form optimum — encode the rotated data with
/// a freshly trained PQ, then solve the orthogonal Procrustes problem
/// <c>argmin_R ||R X - X_hat||</c> via SVD for the rotation that best aligns the data with its own
/// reconstruction. Each pass is guaranteed not to increase the error, so the loop converges quickly;
/// a handful of iterations captures most of the gain, and OPQ typically buys several points of
/// recall over PQ at identical code size and identical query cost.
/// </para>
/// </summary>
public sealed class OPQMatrix : LinearTransform
{
    /// <summary>Sub-quantizer count of the PQ this rotation is optimized for.</summary>
    public int M { get; private set; }

    /// <summary>
    /// Outer alternation steps. Error is monotonically non-increasing, so more is always safe — but
    /// each step retrains a full PQ and solves a <c>d x d</c> SVD, and in practice the curve is
    /// almost flat after about ten.
    /// </summary>
    public int Iterations { get; set; } = 10;

    /// <summary>Lloyd iterations inside each inner PQ training pass. Kept low; the outer loop refines.</summary>
    public int PqIterations { get; set; } = 6;

    /// <summary>
    /// Cap on training vectors. The Procrustes step accumulates a <c>d x d</c> cross-product over
    /// every training vector, so its cost is <c>n * d^2</c> per iteration — by far the most expensive
    /// thing in the library. Rotation quality saturates well before this many points.
    /// </summary>
    public int MaxTrainingPoints { get; set; } = 16_384;

    /// <summary>Reconstruction error after each outer iteration, for convergence reporting.</summary>
    public IReadOnlyList<double> ErrorHistory => _errors;
    private readonly List<double> _errors = [];

    public OPQMatrix(int d, int m, int dOut = -1) : base(d, dOut > 0 ? dOut : d)
    {
        if (DOut % m != 0)
            throw new ArgumentException($"Sub-quantizer count m={m} must divide the output dimension {DOut}.", nameof(m));
        M = m;
        IsTrained = false;
    }

    internal OPQMatrix() : base(1, 1) { }

    public override void Train(ReadOnlySpan<float> x)
    {
        int n = x.Length / DIn;
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));

        // OPQ only makes sense as a square rotation; a reducing OPQ is a PCA followed by a rotation,
        // which the caller can build explicitly with IndexPreTransform.
        if (DIn != DOut)
            throw new NotSupportedException(
                "OPQMatrix requires dOut == dIn. For dimensionality reduction, chain PCAMatrix then OPQMatrix.");

        int d = DIn;
        var rng = new Utils.RandomGenerator(1234);
        float[] data;
        if (n > MaxTrainingPoints)
        {
            var perm = rng.Permutation(n);
            data = new float[(long)MaxTrainingPoints * d];
            for (int i = 0; i < MaxTrainingPoints; i++)
                x.Slice(perm[i] * d, d).CopyTo(data.AsSpan(i * d, d));
            n = MaxTrainingPoints;
        }
        else
        {
            data = x.ToArray();
        }

        // Start from a random rotation: identity is a saddle point when subspace variances differ.
        A = MatrixOps.RandomOrthonormal(d, 1234);
        IsOrthonormal = true;
        IsTrained = true;
        _errors.Clear();

        var rotated = new float[(long)n * d];
        var reconstructed = new float[(long)n * d];

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            MatrixOps.ApplyLinear(data, n, d, rotated, d, A, []);

            var pq = new ProductQuantizer(d, M)
            {
                ClusteringParameters = new ClusteringParameters { Iterations = PqIterations, Seed = 1234 + iteration }
            };
            pq.Train(rotated);
            byte[] codes = pq.ComputeCodes(rotated);
            pq.Decode(codes, n, reconstructed);

            double error = 0;
            for (long i = 0; i < (long)n * d; i++)
            {
                double diff = rotated[i] - reconstructed[i];
                error += diff * diff;
            }
            _errors.Add(error / n);

            // Procrustes: M = sum_i x_i * xhat_i^T, then SVD(M) = U S V^T gives R = V U^T.
            // This rank-1 accumulation is the hot spot of OPQ training (n * d^2 per iteration), so
            // it runs on per-thread matrices that are summed once at the end rather than contending
            // on a shared one.
            var cross = new double[(long)d * d];
            var partials = new System.Collections.Concurrent.ConcurrentBag<double[]>();
            int threads = Environment.ProcessorCount;
            int chunk = (n + threads - 1) / threads;

            Parallel.For(0, threads, t =>
            {
                int start = t * chunk;
                int end = Math.Min(n, start + chunk);
                if (start >= end) return;

                var local = new double[(long)d * d];
                for (int i = start; i < end; i++)
                {
                    long xOffset = (long)i * d;
                    for (int a = 0; a < d; a++)
                    {
                        double va = data[xOffset + a];
                        if (va == 0) continue;
                        long row = (long)a * d;
                        for (int b = 0; b < d; b++) local[row + b] += va * reconstructed[xOffset + b];
                    }
                }
                partials.Add(local);
            });

            foreach (var local in partials)
                for (long i = 0; i < cross.Length; i++) cross[i] += local[i];

            MatrixOps.Svd(cross, d, out double[] u, out _, out double[] v);
            var next = new float[(long)d * d];
            for (int i = 0; i < d; i++)
                for (int j = 0; j < d; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < d; k++) sum += v[(long)i * d + k] * u[(long)j * d + k];
                    next[(long)i * d + j] = (float)sum;
                }
            A = next;
        }
    }

    protected internal override TransformTypeCode TypeCode => TransformTypeCode.OPQ;

    protected internal override void Write(BinaryWriter writer)
    {
        base.Write(writer);
        writer.Write(M);
    }
}

/// <summary>
/// L2-normalizes each vector. Placed in front of an inner-product index it turns maximum inner
/// product search into exact cosine similarity, without the caller having to remember to normalize
/// queries at search time.
/// </summary>
public sealed class NormalizationTransform : VectorTransform
{
    public NormalizationTransform(int d) : base(d, d) { }

    public override void Apply(ReadOnlySpan<float> x, int n, Span<float> output)
    {
        x[..(n * DIn)].CopyTo(output);
        Core.VectorOps.NormalizeL2(output[..(n * DIn)], DIn);
    }

    /// <summary>Normalization discards magnitude, so the reverse is the identity on the unit sphere.</summary>
    public override void ReverseTransform(ReadOnlySpan<float> y, int n, Span<float> output) =>
        y[..(n * DOut)].CopyTo(output);

    protected internal override TransformTypeCode TypeCode => TransformTypeCode.Normalization;

    protected internal override void Write(BinaryWriter writer)
    {
        writer.Write((ushort)TypeCode);
        writer.Write(DIn);
    }

    internal static VectorTransform ReadBody(BinaryReader reader) => new NormalizationTransform(reader.ReadInt32());
}
