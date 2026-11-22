using System.Numerics;
using TdrSim.Core.Engine;

namespace TdrSim.Core.Tests;

public class ComplexLuTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void RecoversKnownSolution(int n)
    {
        var rng = new Random(1234 + n);
        var a = new Complex[n * n];
        for (int k = 0; k < a.Length; k++)
            a[k] = new Complex(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);
        for (int i = 0; i < n; i++)
            a[i * n + i] += n; // diagonal dominance keeps the system well-conditioned

        var x = new Complex[n];
        for (int i = 0; i < n; i++)
            x[i] = new Complex(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1);

        var b = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex sum = Complex.Zero;
            for (int j = 0; j < n; j++)
                sum += a[i * n + j] * x[j];
            b[i] = sum;
        }

        var pivot = new int[n];
        Assert.True(ComplexLu.Factor(a, n, pivot, out double minPivot));
        Assert.True(minPivot > 0);
        ComplexLu.Solve(a, n, pivot, b);

        double maxX = x.Max(v => v.Magnitude);
        for (int i = 0; i < n; i++)
            Assert.True((b[i] - x[i]).Magnitude <= 1e-10 * maxX,
                $"x[{i}] off by {(b[i] - x[i]).Magnitude:e2} (n = {n}).");
    }

    [Fact]
    public void PartialPivotingHandlesZeroDiagonal()
    {
        // A = [[0, 1], [2, 3]] needs a row swap before the first elimination.
        var a = new Complex[] { 0, 1, 2, 3 };
        var x = new Complex[] { 1, -2 };
        var b = new Complex[] { a[0] * x[0] + a[1] * x[1], a[2] * x[0] + a[3] * x[1] };

        var pivot = new int[2];
        Assert.True(ComplexLu.Factor(a, 2, pivot, out _));
        ComplexLu.Solve(a, 2, pivot, b);

        Assert.True((b[0] - x[0]).Magnitude < 1e-14);
        Assert.True((b[1] - x[1]).Magnitude < 1e-14);
    }

    [Fact]
    public void SingularMatrixReportsFailureInsteadOfNaN()
    {
        // Second row is twice the first: rank 1.
        var a = new Complex[] { 1, 2, 2, 4 };
        var pivot = new int[2];

        Assert.False(ComplexLu.Factor(a, 2, pivot, out double minPivot));
        Assert.Equal(0, minPivot);
    }

    [Fact]
    public void ReportsSmallestPivotMagnitude()
    {
        // Diagonal matrix: pivots are the diagonal entries themselves.
        var a = new Complex[] { 4, 0, 0, new Complex(0, 0.25) };
        var pivot = new int[2];

        Assert.True(ComplexLu.Factor(a, 2, pivot, out double minPivot));
        Assert.Equal(0.25, minPivot, 1e-15);
    }
}
