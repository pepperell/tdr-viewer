using System.Numerics;
using TdrSim.Core.Engine;

namespace TdrSim.Core.Tests;

public class TwoPortTests
{
    /// <summary>Builds (Z'l, Y'l) that produce the given gamma*l with Z0 = 50.</summary>
    private static (Complex Z, Complex Y) FromGammaL(Complex gl) => (gl * 50, gl / 50);

    [Theory]
    [InlineData(1e-3, 0.5)]
    [InlineData(0.3, 2.0)]
    [InlineData(3.0, 10.0)]
    [InlineData(20.0, 22.0)] // lossy: e^(2*20) ~ 2e17, still fine scaled
    public void ScaledLineMatchesNaiveCoshSinh(double glRe, double glIm)
    {
        var gl = new Complex(glRe, glIm);
        var (z, y) = FromGammaL(gl);
        var line = TwoPort.Line(z, y);

        // The scaled matrix times e^(gamma*l) must be the textbook ABCD.
        var scale = Complex.Exp(gl);
        AssertClose(Complex.Cosh(gl), line.A * scale, 1e-12);
        AssertClose(50 * Complex.Sinh(gl), line.B * scale, 1e-12);
        AssertClose(Complex.Sinh(gl) / 50, line.C * scale, 1e-12);
        AssertClose(Complex.Cosh(gl), line.D * scale, 1e-12);
    }

    [Fact]
    public void ExtremeLossUnderflowsToMatchedHalves()
    {
        // Re(gamma*l) = 800: q = e^(-1600) underflows to exactly 0; the scaled
        // entries collapse to (1/2, Z0/2, 1/(2 Z0), 1/2) with no overflow anywhere.
        var (z, y) = FromGammaL(new Complex(800, 1e5));
        var line = TwoPort.Line(z, y);
        AssertClose(new Complex(0.5, 0), line.A, 1e-12);
        AssertClose(new Complex(25, 0), line.B, 1e-12);
        AssertClose(new Complex(0.01, 0), line.C, 1e-12);
    }

    [Fact]
    public void DcWithNoShuntConductanceIsExactSeriesResistance()
    {
        // Y'l = 0 (f = 0, G = 0): the line is exactly its loop resistance.
        var line = TwoPort.Line(new Complex(35.31, 0), Complex.Zero);
        Assert.Equal(Complex.One, line.A);
        Assert.Equal(new Complex(35.31, 0), line.B);
        Assert.Equal(Complex.Zero, line.C);
        Assert.Equal(Complex.One, line.D);
    }

    [Fact]
    public void ZeroSeriesImpedanceBecomesPureShunt()
    {
        var y = new Complex(0, 1e-3);
        var line = TwoPort.Line(Complex.Zero, y);
        AssertClose(Complex.One, line.A, 1e-12);
        Assert.Equal(Complex.Zero, line.B);
        AssertClose(y, line.C, 1e-12);
    }

    [Fact]
    public void SmallArgumentBranchIsContinuousWithScaledBranch()
    {
        // Straddle the 1e-4 threshold; compare the scale-invariant ratios B/A and C/A.
        var below = new Complex(0.9e-4, 0.3e-4);
        var above = below * (1.1e-4 / below.Magnitude);

        var (zb, yb) = FromGammaL(below);
        var (za, ya) = FromGammaL(above);
        var lb = TwoPort.Line(zb, yb);
        var la = TwoPort.Line(za, ya);

        // Analytic ratios: B/A = Z0 tanh(gl), C/A = tanh(gl)/Z0.
        AssertClose(50 * Complex.Tanh(below), lb.B / lb.A, 1e-10);
        AssertClose(50 * Complex.Tanh(above), la.B / la.A, 1e-10);
        AssertClose(Complex.Tanh(below) / 50, lb.C / lb.A, 1e-10);
    }

    [Fact]
    public void ApplyMatchesMatrixDefinition()
    {
        var tp = new TwoPort(new(1, 2), new(3, -1), new(0, 1), new(2, 0));
        var (v, i) = tp.Apply(new Complex(1, 1), new Complex(2, -1));
        AssertClose(tp.A * new Complex(1, 1) + tp.B * new Complex(2, -1), v, 1e-15);
        AssertClose(tp.C * new Complex(1, 1) + tp.D * new Complex(2, -1), i, 1e-15);
    }

    private static void AssertClose(Complex expected, Complex actual, double relTol)
    {
        double scale = Math.Max(expected.Magnitude, 1e-300);
        Assert.True((actual - expected).Magnitude <= relTol * scale,
            $"Expected {expected}, got {actual} (rel err {(actual - expected).Magnitude / scale:e2}).");
    }
}
