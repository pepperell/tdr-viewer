using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

public class PipelineTests
{
    [Fact]
    public void FftNormalizationIsPinnedByDcIdentity()
    {
        // A bare resistor at the port: Gamma(f) = 1/3 at every frequency. By the DFT
        // identity sum(h) = X[0], the last trace sample must equal Gamma(0) to rounding.
        // This pins FftFlat's 1/N inverse normalization - if that convention ever
        // changes, this fails loudly and the pipeline compensates in one place.
        var model = Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 100));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);

        Assert.Equal(1.0 / 3.0, trace.DcReflection, 1e-15);
        Assert.Equal(1.0 / 3.0, trace.Rho[^1], 1e-12);

        // And the plateau itself sits at Gamma well before the window ends.
        double windowEnd = trace.TimeS[^1];
        Assert.Equal(1.0 / 3.0, PlateauMean(trace, windowEnd * 0.5, windowEnd * 0.8), 1e-9);
        AssertHermitian(trace);
        Assert.True(trace.Settled);
    }

    [Fact]
    public void MatchedResistorGivesZeroReflectionEverywhere()
    {
        var model = Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 50));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        Assert.All(trace.Rho, r => Assert.True(Math.Abs(r) < 1e-12));
    }

    [Fact]
    public void IncidentEdgeCrossesHalfAmplitudeAtTimeZero()
    {
        var model = Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 50));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);

        // Volts = A * (edge + rho); rho = 0 here, so find the edge's 50 % crossing.
        double half = Tdr1502C.StepAmplitudeVolts / 2;
        for (int k = 1; k < trace.Volts.Length; k++)
        {
            if (trace.Volts[k - 1] < half && trace.Volts[k] >= half)
            {
                double frac = (half - trace.Volts[k - 1]) / (trace.Volts[k] - trace.Volts[k - 1]);
                double t = trace.TimeS[k - 1] + frac * trace.Grid.DtS;
                Assert.True(Math.Abs(t) <= 2 * trace.Grid.DtS, $"Edge at {t:e3} s, expected 0.");
                return;
            }
        }

        Assert.Fail("Incident edge never crosses half amplitude.");
    }

    [Fact]
    public void OhmsDisplayMatchesImpedance()
    {
        var model = Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 100));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        double windowEnd = trace.TimeS[^1];
        int k = Array.FindIndex(trace.TimeS, t => t > windowEnd * 0.6);
        Assert.Equal(100.0, trace.Ohms[k], 0.01);
    }

    [Fact]
    public void VoltsSettleToDcDivider()
    {
        // 100 ohm load: V_inf = A * (1 + Gamma(0)) = 0.3 * 4/3 = 0.4 V.
        var model = Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 100));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        Assert.Equal(0.4, trace.Volts[^1], 0.0005);
    }
}
