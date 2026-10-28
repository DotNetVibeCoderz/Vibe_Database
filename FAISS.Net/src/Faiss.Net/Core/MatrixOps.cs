using Faiss.Net.Utils;

namespace Faiss.Net.Core;

/// <summary>
/// Dense linear algebra used by the vector transforms (random rotation, PCA, OPQ).
/// <para>
/// FAISS delegates this to BLAS/LAPACK. FAISS.Net has no native dependency, so the few routines
/// actually needed are implemented here: a SIMD matrix product for applying transforms, and Jacobi
/// eigen/SVD solvers for training them. Jacobi is the right trade for this workload — the matrices
/// are <c>d x d</c> with <c>d</c> in the hundreds, it is numerically stable without pivoting, and
/// it runs at training time only, never in a query.
/// </para>
/// <para>Decompositions work in <see cref="double"/> internally; float accumulation over hundreds
/// of rotations loses enough precision to break orthogonality.</para>
/// </summary>
public static class MatrixOps
{
    /// <summary>
    /// Applies a linear map to a batch: <c>y_i = A * x_i + b</c>, where <c>A</c> is
    /// <c>dOut x dIn</c> row-major. Parallel over vectors, SIMD within each dot product.
    /// </summary>
    public static unsafe void ApplyLinear(
        ReadOnlySpan<float> x, int n, int dIn, Span<float> y, int dOut,
        ReadOnlySpan<float> a, ReadOnlySpan<float> bias)
    {
        bool hasBias = bias.Length == dOut;
        fixed (float* px = x)
        fixed (float* py = y)
        fixed (float* pa = a)
        fixed (float* pb = bias)
        {
            nint xp = (nint)px, yp = (nint)py, ap = (nint)pa, bp = (nint)pb;
            void Row(int i)
            {
                float* source = (float*)xp + (long)i * dIn;
                float* target = (float*)yp + (long)i * dOut;
                float* matrix = (float*)ap;
                for (int o = 0; o < dOut; o++)
                {
                    float v = VectorOps.InnerProduct(source, matrix + (long)o * dIn, dIn);
                    target[o] = hasBias ? v + ((float*)bp)[o] : v;
                }
            }

            if ((long)n * dIn * dOut < 1_000_000)
                for (int i = 0; i < n; i++) Row(i);
            else
                Parallel.For(0, n, Row);
        }
    }

    /// <summary>Transposes a row-major matrix.</summary>
    public static float[] Transpose(ReadOnlySpan<float> a, int rows, int cols)
    {
        var t = new float[(long)rows * cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                t[(long)c * rows + r] = a[r * cols + c];
        return t;
    }

    /// <summary>Row-major matrix product <c>c = a * b</c> with shapes <c>(m x k) * (k x n)</c>.</summary>
    public static double[] Multiply(ReadOnlySpan<double> a, ReadOnlySpan<double> b, int m, int k, int n)
    {
        var c = new double[(long)m * n];
        for (int i = 0; i < m; i++)
        {
            for (int p = 0; p < k; p++)
            {
                double aip = a[i * k + p];
                if (aip == 0) continue;
                int rowB = p * n, rowC = i * n;
                for (int j = 0; j < n; j++) c[rowC + j] += aip * b[rowB + j];
            }
        }
        return c;
    }

    /// <summary>
    /// A random orthonormal <c>d x d</c> matrix: Gaussian entries orthonormalized by modified
    /// Gram-Schmidt. Two passes of MGS, because one pass loses orthogonality badly in high dimension.
    /// </summary>
    public static float[] RandomOrthonormal(int d, long seed)
    {
        var rng = new RandomGenerator(seed);
        var m = new double[(long)d * d];
        for (int i = 0; i < m.Length; i++) m[i] = rng.NextGaussian();

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < d; i++)
            {
                var row = m.AsSpan(i * d, d);
                for (int j = 0; j < i; j++)
                {
                    var previous = m.AsSpan(j * d, d);
                    double dot = 0;
                    for (int c = 0; c < d; c++) dot += row[c] * previous[c];
                    for (int c = 0; c < d; c++) row[c] -= dot * previous[c];
                }
                double norm = 0;
                for (int c = 0; c < d; c++) norm += row[c] * row[c];
                norm = Math.Sqrt(norm);
                if (norm < 1e-12)
                {
                    // Degenerate row: restart it from a fresh sample and redo this row next pass.
                    for (int c = 0; c < d; c++) row[c] = rng.NextGaussian();
                    i--;
                    continue;
                }
                for (int c = 0; c < d; c++) row[c] /= norm;
            }
        }

        var result = new float[(long)d * d];
        for (int i = 0; i < result.Length; i++) result[i] = (float)m[i];
        return result;
    }

    /// <summary>
    /// Eigen-decomposition of a symmetric <c>n x n</c> matrix by cyclic Jacobi rotations.
    /// Eigenvalues come back descending, with <c>eigenvectors</c> row-major (row <c>i</c> is the
    /// eigenvector for <c>eigenvalues[i]</c>).
    /// </summary>
    public static void SymmetricEigen(double[] matrix, int n, out double[] eigenvalues, out double[] eigenvectors)
    {
        var a = (double[])matrix.Clone();
        var v = new double[(long)n * n];
        for (int i = 0; i < n; i++) v[i * n + i] = 1.0;

        const int maxSweeps = 100;
        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double off = 0;
            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                    off += a[p * n + q] * a[p * n + q];
            if (off < 1e-24) break;

            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double apq = a[p * n + q];
                    if (Math.Abs(apq) < 1e-18) continue;

                    double theta = (a[q * n + q] - a[p * n + p]) / (2 * apq);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1);
                    double s = t * c;

                    for (int i = 0; i < n; i++)
                    {
                        double aip = a[i * n + p], aiq = a[i * n + q];
                        a[i * n + p] = c * aip - s * aiq;
                        a[i * n + q] = s * aip + c * aiq;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double api = a[p * n + i], aqi = a[q * n + i];
                        a[p * n + i] = c * api - s * aqi;
                        a[q * n + i] = s * api + c * aqi;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double vip = v[i * n + p], viq = v[i * n + q];
                        v[i * n + p] = c * vip - s * viq;
                        v[i * n + q] = s * vip + c * viq;
                    }
                }
            }
        }

        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = a[i * n + i];

        // Sort descending and emit eigenvectors as rows.
        var order = Enumerable.Range(0, n).OrderByDescending(i => values[i]).ToArray();
        eigenvalues = new double[n];
        eigenvectors = new double[(long)n * n];
        for (int rank = 0; rank < n; rank++)
        {
            int source = order[rank];
            eigenvalues[rank] = values[source];
            for (int i = 0; i < n; i++) eigenvectors[(long)rank * n + i] = v[i * n + source];
        }
    }

    /// <summary>
    /// One-sided Jacobi SVD of a square <c>n x n</c> matrix: <c>a = u * diag(s) * v^T</c>, with
    /// <c>u</c> and <c>v</c> row-major and orthogonal.
    /// <para>
    /// Only the square case is needed (the orthogonal Procrustes step inside OPQ), and one-sided
    /// Jacobi delivers high relative accuracy on exactly this kind of well-scaled matrix.
    /// </para>
    /// </summary>
    public static void Svd(double[] matrix, int n, out double[] u, out double[] s, out double[] v)
    {
        // Work on columns of `a`; `vt` accumulates the right rotations.
        var a = (double[])matrix.Clone();
        var vAcc = new double[(long)n * n];
        for (int i = 0; i < n; i++) vAcc[i * n + i] = 1.0;

        const int maxSweeps = 60;
        const double tolerance = 1e-14;

        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            double maxOff = 0;
            for (int p = 0; p < n - 1; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    double alpha = 0, beta = 0, gamma = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double aip = a[i * n + p], aiq = a[i * n + q];
                        alpha += aip * aip;
                        beta += aiq * aiq;
                        gamma += aip * aiq;
                    }
                    if (Math.Abs(gamma) <= tolerance * Math.Sqrt(alpha * beta) || gamma == 0) continue;
                    maxOff = Math.Max(maxOff, Math.Abs(gamma) / Math.Sqrt(alpha * beta));

                    double zeta = (beta - alpha) / (2 * gamma);
                    double t = Math.Sign(zeta) / (Math.Abs(zeta) + Math.Sqrt(1 + zeta * zeta));
                    if (zeta == 0) t = 1;
                    double c = 1 / Math.Sqrt(1 + t * t);
                    double sn = c * t;

                    for (int i = 0; i < n; i++)
                    {
                        double aip = a[i * n + p], aiq = a[i * n + q];
                        a[i * n + p] = c * aip - sn * aiq;
                        a[i * n + q] = sn * aip + c * aiq;

                        double vip = vAcc[i * n + p], viq = vAcc[i * n + q];
                        vAcc[i * n + p] = c * vip - sn * viq;
                        vAcc[i * n + q] = sn * vip + c * viq;
                    }
                }
            }
            if (maxOff < tolerance) break;
        }

        // Column norms are the singular values; normalizing the columns of `a` gives U.
        s = new double[n];
        u = new double[(long)n * n];
        for (int j = 0; j < n; j++)
        {
            double norm = 0;
            for (int i = 0; i < n; i++) norm += a[i * n + j] * a[i * n + j];
            norm = Math.Sqrt(norm);
            s[j] = norm;
            if (norm > 1e-300)
                for (int i = 0; i < n; i++) u[i * n + j] = a[i * n + j] / norm;
            else
                u[j * n + j] = 1.0;
        }

        v = vAcc;
    }

    /// <summary>
    /// Solves <c>A X = B</c> in place for a symmetric positive-definite <c>n x n</c> matrix
    /// <paramref name="a"/> and <c>n x nrhs</c> right-hand sides <paramref name="b"/> (both row-major),
    /// by Cholesky factorization — LAPACK's <c>posv</c>. On return <paramref name="a"/> holds the
    /// lower factor and <paramref name="b"/> the solution.
    /// <para>
    /// Used by the local-search quantizer's codebook update, where <c>n = M * 2^nbits</c> reaches a few
    /// thousand. The factorization is column-oriented: once column <c>j</c>'s pivot is known, every
    /// entry below it depends only on already-finished columns, so those rows are computed in
    /// parallel — and because each entry is still one fixed-order dot product, the result does not
    /// depend on how the rows were scheduled.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The matrix is not positive definite.</exception>
    public static void CholeskySolve(double[] a, int n, double[] b, int nrhs)
    {
        if (a.Length < (long)n * n) throw new ArgumentException("Matrix is smaller than n x n.", nameof(a));
        if (b.Length < (long)n * nrhs) throw new ArgumentException("Right-hand side is smaller than n x nrhs.", nameof(b));

        // Factor A = L L^T, storing L in the lower triangle row by row.
        for (int j = 0; j < n; j++)
        {
            double pivot = a[(long)j * n + j] - Dot(a, (long)j * n, (long)j * n, j);
            if (!(pivot > 0))
                throw new InvalidOperationException($"Matrix is not positive definite (pivot {pivot} at column {j}).");
            double diagonal = Math.Sqrt(pivot);
            a[(long)j * n + j] = diagonal;

            int column = j;
            void Row(int i)
            {
                long row = (long)i * n;
                a[row + column] = (a[row + column] - Dot(a, row, (long)column * n, column)) / diagonal;
            }

            int remaining = n - j - 1;
            if ((long)remaining * j < 32_768)
                for (int i = j + 1; i < n; i++) Row(i);
            else
                Parallel.For(j + 1, n, Row);
        }

        // Forward substitution L Y = B, then back substitution L^T X = Y, one row of B at a time.
        for (int i = 0; i < n; i++)
        {
            long row = (long)i * n;
            var target = b.AsSpan(i * nrhs, nrhs);
            for (int p = 0; p < i; p++)
            {
                double l = a[row + p];
                if (l == 0) continue;
                var source = b.AsSpan(p * nrhs, nrhs);
                for (int c = 0; c < nrhs; c++) target[c] -= l * source[c];
            }
            double inverse = 1.0 / a[row + i];
            for (int c = 0; c < nrhs; c++) target[c] *= inverse;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            var target = b.AsSpan(i * nrhs, nrhs);
            for (int p = i + 1; p < n; p++)
            {
                double l = a[(long)p * n + i];
                if (l == 0) continue;
                var source = b.AsSpan(p * nrhs, nrhs);
                for (int c = 0; c < nrhs; c++) target[c] -= l * source[c];
            }
            double inverse = 1.0 / a[(long)i * n + i];
            for (int c = 0; c < nrhs; c++) target[c] *= inverse;
        }
    }

    /// <summary>Dot product of <paramref name="length"/> doubles starting at two offsets of one array.</summary>
    private static double Dot(double[] data, long first, long second, int length)
    {
        var x = data.AsSpan((int)first, length);
        var y = data.AsSpan((int)second, length);
        int i = 0;
        double sum = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && length >= System.Numerics.Vector<double>.Count)
        {
            var acc = System.Numerics.Vector<double>.Zero;
            int width = System.Numerics.Vector<double>.Count;
            for (; i <= length - width; i += width)
                acc += new System.Numerics.Vector<double>(x[i..]) * new System.Numerics.Vector<double>(y[i..]);
            sum = System.Numerics.Vector.Sum(acc);
        }
        for (; i < length; i++) sum += x[i] * y[i];
        return sum;
    }
}
