using System.Numerics;

namespace TdrSim.Core.Engine;

/// <summary>
/// Dense in-place LU factorization with partial pivoting for the small complex systems
/// the nodal solver produces (n up to ~64). The caller owns every buffer, so the
/// per-frequency hot loop allocates nothing.
/// </summary>
public static class ComplexLu
{
    /// <summary>
    /// Factors the row-major n x n matrix in place (unit-lower L below the diagonal,
    /// U on and above it, PA = LU with P recorded as row swaps in <paramref name="pivot"/>).
    /// Returns false when a pivot column is exactly zero - the matrix is singular and no
    /// solve should be attempted. <paramref name="minPivot"/> is the smallest pivot
    /// magnitude seen (0 on failure), the caller's conditioning signal.
    /// </summary>
    public static bool Factor(Complex[] a, int n, int[] pivot, out double minPivot)
    {
        minPivot = double.PositiveInfinity;
        for (int k = 0; k < n; k++)
        {
            int p = k;
            double best = Complex.Abs(a[k * n + k]);
            for (int i = k + 1; i < n; i++)
            {
                double mag = Complex.Abs(a[i * n + k]);
                if (mag > best)
                {
                    best = mag;
                    p = i;
                }
            }

            pivot[k] = p;
            if (best == 0)
            {
                minPivot = 0;
                return false;
            }

            minPivot = Math.Min(minPivot, best);
            if (p != k)
            {
                for (int j = 0; j < n; j++)
                    (a[k * n + j], a[p * n + j]) = (a[p * n + j], a[k * n + j]);
            }

            Complex inv = Complex.One / a[k * n + k];
            for (int i = k + 1; i < n; i++)
            {
                Complex m = a[i * n + k] * inv;
                a[i * n + k] = m;
                if (m != Complex.Zero)
                {
                    for (int j = k + 1; j < n; j++)
                        a[i * n + j] -= m * a[k * n + j];
                }
            }
        }

        return true;
    }

    /// <summary>Solves A x = b in place on b, using a matrix factored by <see cref="Factor"/>.</summary>
    public static void Solve(Complex[] a, int n, int[] pivot, Complex[] b)
    {
        for (int k = 0; k < n; k++)
        {
            if (pivot[k] != k)
                (b[k], b[pivot[k]]) = (b[pivot[k]], b[k]);
        }

        for (int i = 1; i < n; i++)
        {
            Complex sum = b[i];
            for (int j = 0; j < i; j++)
                sum -= a[i * n + j] * b[j];
            b[i] = sum;
        }

        for (int i = n - 1; i >= 0; i--)
        {
            Complex sum = b[i];
            for (int j = i + 1; j < n; j++)
                sum -= a[i * n + j] * b[j];
            b[i] = sum / a[i * n + i];
        }
    }
}
