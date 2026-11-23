using System.Diagnostics;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using Xunit.Abstractions;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// M6 layer peeling. The recursion is pinned against an independent forward lattice
/// simulation (no FFT pipeline involved), then against the engine: on a lossless
/// cascade it must read the true Z0 of every section exactly where the raw Z(t)
/// readout is ghost-contaminated. Its documented failures (loss read as rising
/// impedance, a branch read as a parallel combination, nothing past an open) are
/// asserted behaviours, not surprises.
/// </summary>
public class LayerPeelingTests(ITestOutputHelper output)
{
    private const double Vf = 0.66;
    private static readonly double V = Vf * C0;

    /// <summary>Exact impulse response of a stack of equal-delay lossless layers,
    /// by brute-force time stepping of the up/down waves in every layer.</summary>
    private static double[] ForwardLattice(double[] r, int samples)
    {
        int n = r.Length;
        // down[i]/up[i]: waves inside layer i (between interface i-1 and i), layer 0
        // being the instrument side. One time step = one layer transit (half a
        // round-trip sample).
        var down = new double[n + 1];
        var up = new double[n + 1];
        var response = new double[samples];
        down[0] = 1;
        for (int half = 0; half < 2 * samples; half++)
        {
            var nextDown = new double[n + 1];
            var nextUp = new double[n + 1];
            for (int i = 0; i < n; i++)
            {
                // Interface i: layer i above, layer i+1 below.
                double a = down[i];
                double b = up[i + 1];
                nextUp[i] = r[i] * a + (1 - r[i]) * b;
                nextDown[i + 1] = (1 + r[i]) * a - r[i] * b;
            }

            if (half % 2 == 0)
                response[half / 2] = nextUp[0];
            down = nextDown;
            up = nextUp;
            up[0] = 0;     // absorbed by the matched instrument
            down[0] = 0;   // the impulse is launched once
        }

        return response;
    }

    [Fact]
    public void RecursionRecoversAKnownLayerStackExactly()
    {
        // Interfaces at odd depths only would alias in the forward stepper; any
        // sequence works for the half-step lattice used here.
        double[] r = [0.2, 0, -0.4, 0.1, 0, 0, 0.55, -0.3, 0.05, 0, 0.25, -0.6];
        var impulse = ForwardLattice(r, r.Length);

        var (ohms, reflection, reliable, stop) = LayerPeeling.PeelImpulse(impulse, 50);

        Assert.Equal(PeelStop.EndOfData, stop);
        Assert.Equal(r.Length, reliable);
        double z = 50;
        for (int i = 0; i < r.Length; i++)
        {
            z *= (1 + r[i]) / (1 - r[i]);
            Assert.Equal(r[i], reflection[i], 9);
            Assert.Equal(z, ohms[i], 6);
        }
    }

    [Fact]
    public void PeelingRemovesTheGhostsTheRawImpedanceReadoutKeeps()
    {
        // 50 -> 75 -> 30 -> matched. Behind the second step the raw readout is wrong
        // (it is built from a partial sum that includes the first step's shadow), and
        // later still it shows re-reflections as impedance wiggles on a uniform line.
        var instrument = Tdr1502C with { RiseTimeS = 100e-12 };
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Lossless(50, Vf), 5),
            new CableSegment(N(1), N(2), Lossless(75, Vf), 5),
            new CableSegment(N(2), N(3), Lossless(30, Vf), 12),
            new Load(N(3), LoadKind.Resistor, ROhms: 30));
        var trace = TdrSimulator.Simulate(model, instrument);
        AssertHermitian(trace);

        double t1 = 10 / V, t2 = 20 / V, tEnd = 44 / V;
        var peel = LayerPeeling.Peel(trace, tEnd + 5e-9);
        Assert.Equal(PeelStop.EndOfData, peel.Stop);

        double PeeledMean(double from, double to) => Mean(peel, from, to);
        double RawMean(double from, double to)
        {
            double sum = 0;
            int count = 0;
            for (int k = 0; k < trace.TimeS.Length; k++)
            {
                if (trace.TimeS[k] >= from && trace.TimeS[k] <= to)
                {
                    sum += trace.Ohms[k];
                    count++;
                }
            }

            return sum / count;
        }

        const double margin = 2e-9;
        double p50 = PeeledMean(margin, t1 - margin);
        double p75 = PeeledMean(t1 + margin, t2 - margin);
        double p30 = PeeledMean(t2 + margin, t2 + t1 - margin);
        double p30Late = PeeledMean(t2 + t1 + margin, tEnd - margin);
        double raw30 = RawMean(t2 + margin, t2 + t1 - margin);
        output.WriteLine($"peeled {p50:0.00} / {p75:0.00} / {p30:0.00} / late {p30Late:0.00}; raw third section {raw30:0.00}");

        Assert.Equal(50, p50, 0.25);
        Assert.Equal(75, p75, 0.75);
        Assert.Equal(30, p30, 0.3);
        Assert.Equal(30, p30Late, 0.3);     // the ghost region reads flat once peeled
        Assert.True(Math.Abs(raw30 - 30) > 1.5, $"Raw readout {raw30:0.00} should be ghost-contaminated.");
    }

    [Fact]
    public void MatchedLineStaysFlatAtTheReference()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Lossless(50, Vf), 20),
            new Load(N(1), LoadKind.Resistor, ROhms: 50));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        var peel = LayerPeeling.Peel(trace, 250e-9);

        Assert.Equal(peel.Ohms.Length, peel.ReliableCount);
        Assert.All(peel.Ohms, z => Assert.Equal(50, z, 0.1));
    }

    [Fact]
    public void NothingBehindAnOpenIsRecoverable()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Lossless(50, Vf), 20));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        double tOpen = 40 / V;
        var peel = LayerPeeling.Peel(trace, 3 * tOpen);

        Assert.Equal(PeelStop.Opaque, peel.Stop);
        Assert.True(peel.ReliableCount < peel.Ohms.Length);
        double cutoff = peel.TimeOf(peel.ReliableCount - 1);
        Assert.InRange(cutoff, tOpen - 1e-9, tOpen + 1.5e-9);
        Assert.True(peel.Ohms[peel.ReliableCount - 1] > 1000, "The open must read as a very high impedance.");

        // Before the open the line still reads 50 ohm.
        Assert.Equal(50, Mean(peel, 5e-9, tOpen - 3e-9), 0.2);
    }

    [Fact]
    public void LossIsMisreadAsRisingImpedance()
    {
        // The classic: skin-effect creep on a matched lossy line peels to a steadily
        // climbing "impedance" - the algorithm assumes lossless layers.
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 100),
            new Load(N(1), LoadKind.Resistor, ROhms: rg58.Z0Ohms));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        double tEnd = 200 / (rg58.VelocityFactor * C0);
        var peel = LayerPeeling.Peel(trace, tEnd);

        double early = Mean(peel, 0.1 * tEnd, 0.2 * tEnd);
        double middle = Mean(peel, 0.45 * tEnd, 0.55 * tEnd);
        double late = Mean(peel, 0.8 * tEnd, 0.9 * tEnd);
        output.WriteLine($"lossy matched RG-58: {early:0.00} -> {middle:0.00} -> {late:0.00} ohm");
        Assert.True(middle > early + 0.2 && late > middle + 0.2,
            $"Expected a climb, got {early:0.00} -> {middle:0.00} -> {late:0.00}.");
    }

    [Fact]
    public void ABranchIsReadAsTheParallelCombination()
    {
        var line = Lossless(50, Vf);
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 10),
            new CableSegment(N(1), N(2), line, 10),
            new CableSegment(N(1), N(3), line, 10),
            new Load(N(2), LoadKind.Resistor, ROhms: 50),
            new Load(N(3), LoadKind.Resistor, ROhms: 50));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        Assert.Equal(SolverPath.Nodal, trace.Solver);

        double tTee = 20 / V;
        var peel = LayerPeeling.Peel(trace, 3 * tTee);
        Assert.Equal(25, Mean(peel, tTee + 3e-9, 2 * tTee - 3e-9), 0.3);
    }

    [Fact]
    public void DecimationCapsTheLayerCountAndTheCost()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Lossless(50, Vf), 300),
            new Load(N(1), LoadKind.Resistor, ROhms: 75));
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        Assert.True(trace.Rho.Length > 30_000);

        LayerPeeling.Peel(trace, trace.TimeS[^1]); // warm-up
        var stopwatch = Stopwatch.StartNew();
        var peel = LayerPeeling.Peel(trace, trace.TimeS[^1]);
        stopwatch.Stop();
        output.WriteLine($"{peel.Ohms.Length} layers in {stopwatch.Elapsed.TotalMilliseconds:0.00} ms");

        Assert.InRange(peel.Ohms.Length, 1024, 2048);
        Assert.True(peel.DtS > trace.Grid.DtS);
        Assert.True(stopwatch.Elapsed.TotalMilliseconds < 100, "Informational target is a few ms.");
        Assert.Equal(75, peel.Ohms[^1], 0.5);
    }

    private static double Mean(PeelResult peel, double fromS, double toS)
    {
        double sum = 0;
        int count = 0;
        for (int i = 0; i < peel.ReliableCount; i++)
        {
            double t = peel.TimeOf(i);
            if (t >= fromS && t <= toS)
            {
                sum += peel.Ohms[i];
                count++;
            }
        }

        Assert.True(count > 3, $"Peel window [{fromS}, {toS}] contains only {count} samples.");
        return sum / count;
    }
}
