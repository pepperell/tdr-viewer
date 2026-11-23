using System.Numerics;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// The return-loss curve is the Gamma(f) the simulator already solved, so the tests pin
/// it against closed-form transmission-line theory at every grid frequency.
/// </summary>
public class ReturnLossTests
{
    private const double Vf = 0.66;

    private static (TdrTrace Trace, ReturnLossCurve Curve) Run(double lineZ0, double lengthM, Load load)
    {
        var trace = TdrSimulator.Simulate(
            Network(
                new SourcePort(N(0)),
                new CableSegment(N(0), N(1), Lossless(lineZ0, Vf), lengthM),
                load),
            Tdr1502C);
        var curve = ReturnLoss.FromTrace(trace, Tdr1502C);
        Assert.NotNull(curve);
        return (trace, curve);
    }

    [Fact]
    public void TheTraceCarriesGammaOnItsFrequencyGrid()
    {
        var (trace, curve) = Run(50, 10, new Load(N(1), LoadKind.Resistor, ROhms: 75));
        Assert.Equal(trace.Grid.FrequencyCount, trace.Gamma!.Length);
        Assert.Equal(trace.DcReflection, trace.Gamma[0].Real, 1e-15);
        Assert.Equal(0, curve.FrequencyHz[0]);
        Assert.Equal(trace.Grid.DfHz, curve.DfHz, 1e-9);
    }

    [Fact]
    public void AResistiveMismatchOnAMatchedLineIsFlat()
    {
        // 75 ohm on a lossless 50 ohm line: |Gamma| = 0.2 at every frequency, 13.98 dB.
        var (_, curve) = Run(50, 10, new Load(N(1), LoadKind.Resistor, ROhms: 75));
        foreach (double magnitude in curve.GammaMagnitude)
            Assert.Equal(0.2, magnitude, 1e-9);
        Assert.Equal(-20 * Math.Log10(0.2), -curve.GammaDb[curve.GammaDb.Length / 2], 1e-6);
    }

    [Fact]
    public void AnOpenLosslessLineReflectsEverything()
    {
        var (_, curve) = Run(50, 10, new Load(N(1), LoadKind.Open));
        foreach (double db in curve.GammaDb)
            Assert.Equal(0, db, 1e-6);
    }

    [Fact]
    public void APerfectMatchSitsOnTheFloor()
    {
        var (_, curve) = Run(50, 10, new Load(N(1), LoadKind.Resistor, ROhms: 50));
        foreach (double db in curve.GammaDb)
            Assert.Equal(-ReturnLoss.FloorDb, db);
    }

    [Fact]
    public void AMismatchedSectionRipplesExactlyAsTheLineEquationSays()
    {
        // 50 ohm source, 4 m of 75 ohm line, 50 ohm load: nulls at every half wave,
        // peaks of |Gamma| = (112.5 - 50)/(112.5 + 50) at the quarter waves.
        const double z0 = 75, zl = 50, length = 4;
        var (_, curve) = Run(z0, length, new Load(N(1), LoadKind.Resistor, ROhms: zl));

        double peak = 0;
        for (int n = 0; n < curve.FrequencyHz.Length; n++)
        {
            double t = Math.Tan(2 * Math.PI * curve.FrequencyHz[n] * length / (Vf * C0));
            Complex zin = z0 * new Complex(zl, z0 * t) / new Complex(z0, zl * t);
            double expected = Complex.Abs((zin - 50) / (zin + 50));
            Assert.Equal(expected, curve.GammaMagnitude[n], 1e-7);
            peak = Math.Max(peak, curve.GammaMagnitude[n]);
        }

        Assert.Equal((112.5 - 50) / (112.5 + 50), peak, 1e-3);

        // The first null is the half-wave frequency.
        double halfWave = Vf * C0 / (2 * length);
        Assert.True(curve.GammaMagnitude[curve.IndexAt(halfWave)] < 0.02);

        var (worstHz, worst) = curve.WorstUpTo(curve.InstrumentBandwidthHz);
        Assert.Equal(peak, worst, 1e-3);
        Assert.InRange(worstHz, 0, curve.InstrumentBandwidthHz);
    }

    [Fact]
    public void TheCurveStopsAtTheRequestedFrequencyAndNeverPastNyquist()
    {
        var (trace, curve) = Run(50, 10, new Load(N(1), LoadKind.Open));
        Assert.InRange(curve.FrequencyHz[^1], 1 / Tdr1502C.RiseTimeS - trace.Grid.DfHz, 1 / Tdr1502C.RiseTimeS);
        Assert.Equal(0.34 / Tdr1502C.RiseTimeS, curve.InstrumentBandwidthHz, 1e-3);

        var all = ReturnLoss.FromTrace(trace, Tdr1502C, maxHz: 1e15)!;
        Assert.Equal(trace.Gamma!.Length, all.FrequencyHz.Length);
        var narrow = ReturnLoss.FromTrace(trace, Tdr1502C, maxHz: 0)!;
        Assert.Equal(2, narrow.FrequencyHz.Length);
    }

    [Fact]
    public void DerivedFiguresMatchTheTextbook()
    {
        Assert.Equal(20, ReturnLoss.Db(0.1), 1e-12);
        Assert.Equal(ReturnLoss.FloorDb, ReturnLoss.Db(0));
        Assert.Equal(ReturnLoss.FloorDb, ReturnLoss.Db(1e-9));
        Assert.Equal(0, ReturnLoss.Db(1), 1e-12);
        Assert.Equal(1.5, ReturnLoss.Vswr(0.2), 1e-12);
        Assert.Equal(double.PositiveInfinity, ReturnLoss.Vswr(1));
        Assert.Equal(-10 * Math.Log10(0.75), ReturnLoss.MismatchLossDb(0.5), 1e-12);
    }

    [Fact]
    public void NoiseDoesNotTouchTheFrequencyDomainCurve()
    {
        var (trace, curve) = Run(50, 10, new Load(N(1), LoadKind.Resistor, ROhms: 75));
        var noisy = TraceNoise.Apply(trace, new TraceNoiseSettings(5e-3, 1), 0.3, 50, seed: 1);
        var again = ReturnLoss.FromTrace(noisy, Tdr1502C)!;
        Assert.Equal(curve.GammaDb, again.GammaDb);
    }
}
