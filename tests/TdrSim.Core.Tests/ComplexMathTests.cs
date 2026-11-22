using System.Numerics;
using TdrSim.Core.Engine;

namespace TdrSim.Core.Tests;

public class ComplexMathTests
{
    [Theory]
    [InlineData(0.7, -1.3)]
    [InlineData(-2.0, 3.0)]
    [InlineData(5.0, 0.4)]
    public void ComplexExpm1MatchesNaiveAwayFromCancellation(double re, double im)
    {
        var z = new Complex(re, im);
        var expected = Complex.Exp(z) - Complex.One;
        var actual = ComplexMath.Expm1(z);
        Assert.True((actual - expected).Magnitude < 1e-12 * expected.Magnitude);
    }

    [Fact]
    public void ComplexExpm1AccurateForTinyArguments()
    {
        // e^z - 1 = z + z^2/2 + O(z^3); naive subtraction loses ~8 digits here.
        var z = new Complex(1e-9, -2e-9);
        var expected = z + z * z / 2;
        var actual = ComplexMath.Expm1(z);
        Assert.True((actual - expected).Magnitude < 1e-15 * expected.Magnitude);
    }

    [Fact]
    public void RealExpm1AccurateForTinyArguments()
    {
        const double x = 1e-12;
        Assert.Equal(x + x * x / 2, ComplexMath.Expm1(x), 1e-27);
        Assert.Equal(-x + x * x / 2, ComplexMath.Expm1(-x), 1e-27);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 1.2660658777520084)]
    [InlineData(6.0, 67.234406976478)]
    public void BesselI0KnownValues(double x, double expected)
    {
        Assert.Equal(expected, ComplexMath.BesselI0(x), expected * 1e-12);
    }

    [Fact]
    public void KaiserWindowEndpoints()
    {
        const double beta = 6.0;
        Assert.Equal(1.0, ComplexMath.Kaiser(0, beta), 1e-15);
        Assert.Equal(1 / ComplexMath.BesselI0(beta), ComplexMath.Kaiser(1, beta), 1e-15);
        Assert.True(ComplexMath.Kaiser(0.5, beta) is > 0 and < 1);
    }
}
