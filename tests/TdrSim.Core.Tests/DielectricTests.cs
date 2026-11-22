using TdrSim.Core.Engine;

namespace TdrSim.Core.Tests;

public class DielectricTests
{
    [Theory]
    [InlineData(2.3, 0.02)]
    [InlineData(1.38, 0.0002)]
    [InlineData(4.4, 0.1)]
    public void IdentificationReproducesReferencePoint(double dk, double tanDelta)
    {
        // The mandatory self-check: eps(f_ref) = DK * (1 - j*tanDelta) to 1e-9.
        var (epsInf, epsD) = Dielectric.Identify(dk, tanDelta);
        var eps = Dielectric.Evaluate(epsInf, epsD, Dielectric.FRefHz);
        Assert.Equal(dk, eps.Real, dk * 1e-9);
        Assert.Equal(-dk * tanDelta, eps.Imaginary, dk * tanDelta * 1e-9 + 1e-15);
    }

    [Fact]
    public void DcPermittivityIsRealAndAboveReference()
    {
        var (epsInf, epsD) = Dielectric.Identify(2.3, 0.02);
        var eps0 = Dielectric.Evaluate(epsInf, epsD, 0);
        Assert.Equal(0, eps0.Imaginary, 1e-12);
        Assert.True(eps0.Real > 2.3, "DC permittivity must exceed the 100 MHz value (normal dispersion).");
    }

    [Fact]
    public void RealPartDecreasesWithFrequencyAndImagStaysNegative()
    {
        var (epsInf, epsD) = Dielectric.Identify(2.3, 0.02);
        double previous = double.MaxValue;
        for (double f = 1e3; f <= 1e11; f *= 10)
        {
            var eps = Dielectric.Evaluate(epsInf, epsD, f);
            Assert.True(eps.Real < previous, $"Re(eps) not monotone at {f:e0} Hz.");
            Assert.True(eps.Imaginary < 0, $"Im(eps) not negative at {f:e0} Hz (passivity).");
            previous = eps.Real;
        }
    }

    [Fact]
    public void ZeroTanDeltaIsDispersionless()
    {
        var (epsInf, epsD) = Dielectric.Identify(2.3, 0);
        Assert.Equal(2.3, epsInf);
        Assert.Equal(0, epsD);
    }
}
