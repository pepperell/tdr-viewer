using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// Pulse-train view physics: periodic steady state at an arbitrary probe net. Closed
/// forms on lossless lines - propagation delay, open-end doubling, duty-cycle mean,
/// independent rise/fall times - plus the compiler's probe list.
/// </summary>
public class PulseSimulatorTests
{
    private const double Vf = 0.66;
    private const double V = Vf * C0;
    private const double Amplitude = 0.3; // tek-1502c into 50 ohm

    private static readonly PulseSettings DefaultPulse = new()
    {
        RiseTimeS = 1e-9,
        FallTimeS = 1e-9,
        RateHz = 1e6,
        DutyFraction = 0.5,
    };

    private static NetworkModel Line30(Load load) => Network(
        new SourcePort(N(0)),
        new CableSegment(N(0), N(1), Lossless(50, Vf), 30),
        load);

    private static double TimeAt(PulseTrace pulse, int k) => k * pulse.DtS;

    /// <summary>First crossing of the level inside [fromS, toS], rising or falling.</summary>
    private static double CrossingTime(PulseTrace pulse, double level, double fromS, double toS, bool rising)
    {
        for (int k = 1; k < pulse.Volts.Length; k++)
        {
            double t = TimeAt(pulse, k);
            if (t < fromS || t > toS)
                continue;
            double a = pulse.Volts[k - 1];
            double b = pulse.Volts[k];
            bool crosses = rising ? a <= level && b > level : a >= level && b < level;
            if (crosses)
                return TimeAt(pulse, k - 1) + (level - a) / (b - a) * pulse.DtS;
        }

        throw new Xunit.Sdk.XunitException($"No {(rising ? "rising" : "falling")} crossing of {level} in window.");
    }

    private static double Mean(PulseTrace pulse, double fromS, double toS)
    {
        double sum = 0;
        int count = 0;
        for (int k = 0; k < pulse.Volts.Length; k++)
        {
            double t = TimeAt(pulse, k);
            if (t >= fromS && t <= toS)
            {
                sum += pulse.Volts[k];
                count++;
            }
        }

        Assert.True(count > 10, $"Window [{fromS}, {toS}] holds only {count} samples.");
        return sum / count;
    }

    [Fact]
    public void MatchedLineDelaysThePulseAndKeepsTheAmplitude()
    {
        var pulse = PulseSimulator.Simulate(
            Line30(new Load(N(1), LoadKind.Resistor, ROhms: 50)), Tdr1502C, DefaultPulse, N(1));

        Assert.True(pulse.MaxImagRatio < 1e-10, $"Hermitian self-check failed: {pulse.MaxImagRatio:e2}.");
        Assert.False(pulse.Compromised);

        // Rising 50 % arrives one one-way delay after the stimulus edge; the top sits
        // at the matched amplitude; the falling 50 % lands a duty-width later.
        double expectedRise = pulse.EdgeOffsetS + 30 / V;
        double rise = CrossingTime(pulse, Amplitude / 2, expectedRise - 50e-9, expectedRise + 50e-9, rising: true);
        Assert.Equal(expectedRise, rise, 2e-9);

        Assert.Equal(Amplitude, Mean(pulse, rise + 25e-9, rise + 475e-9), 1e-3);
        Assert.Equal(0.0, Mean(pulse, rise + 525e-9, rise + 700e-9), 1e-3); // baseline after the pulse

        double fall = CrossingTime(pulse, Amplitude / 2, rise + 400e-9, rise + 600e-9, rising: false);
        Assert.Equal(rise + 0.5 / 1e6, fall, 2e-9);
    }

    [Fact]
    public void OpenEndDoublesTheVoltage()
    {
        var pulse = PulseSimulator.Simulate(
            Line30(new Load(N(1), LoadKind.Open)), Tdr1502C, DefaultPulse, N(1));

        double rise = CrossingTime(
            pulse, Amplitude, pulse.EdgeOffsetS + 30 / V - 50e-9, pulse.EdgeOffsetS + 30 / V + 50e-9, rising: true);
        Assert.Equal(2 * Amplitude, Mean(pulse, rise + 25e-9, rise + 475e-9), 2e-3);
    }

    [Fact]
    public void MeanOverThePeriodMatchesTheDutyCycle()
    {
        var matched = PulseSimulator.Simulate(
            Line30(new Load(N(1), LoadKind.Resistor, ROhms: 50)), Tdr1502C,
            DefaultPulse with { DutyFraction = 0.3 }, N(1));
        Assert.Equal(Amplitude * 0.3, Mean(matched, 0, matched.PeriodS), 1e-3);
    }

    [Fact]
    public void RiseAndFallTimesAreIndependent()
    {
        var pulse = PulseSimulator.Simulate(
            Line30(new Load(N(1), LoadKind.Resistor, ROhms: 50)), Tdr1502C,
            DefaultPulse with { RiseTimeS = 1e-9, FallTimeS = 5e-9 }, N(1));

        double edge = pulse.EdgeOffsetS + 30 / V;
        double rise10 = CrossingTime(pulse, 0.1 * Amplitude, edge - 30e-9, edge + 30e-9, rising: true);
        double rise90 = CrossingTime(pulse, 0.9 * Amplitude, edge - 30e-9, edge + 30e-9, rising: true);
        Assert.Equal(1e-9, rise90 - rise10, 0.3e-9);

        double fallStart = edge + 450e-9;
        double fall90 = CrossingTime(pulse, 0.9 * Amplitude, fallStart, fallStart + 120e-9, rising: false);
        double fall10 = CrossingTime(pulse, 0.1 * Amplitude, fallStart, fallStart + 120e-9, rising: false);
        Assert.Equal(5e-9, fall10 - fall90, 1.2e-9);
    }

    [Fact]
    public void MidLineJunctionProbeSeesTheThroughPulse()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Lossless(50, Vf), 15),
            new CableSegment(N(1), N(2), Lossless(50, Vf), 15),
            new Load(N(2), LoadKind.Resistor, ROhms: 50));
        var pulse = PulseSimulator.Simulate(model, Tdr1502C, DefaultPulse, N(1));

        double expectedRise = pulse.EdgeOffsetS + 15 / V;
        double rise = CrossingTime(pulse, Amplitude / 2, expectedRise - 50e-9, expectedRise + 50e-9, rising: true);
        Assert.Equal(expectedRise, rise, 2e-9);
        Assert.Equal(Amplitude, Mean(pulse, rise + 25e-9, rise + 475e-9), 1e-3);
    }

    [Fact]
    public void ShortedProbeNetIsExactlyZero()
    {
        var pulse = PulseSimulator.Simulate(
            Line30(new Load(N(1), LoadKind.Short)), Tdr1502C, DefaultPulse, N(1));
        Assert.All(pulse.Volts, v => Assert.Equal(0.0, v));
    }

    [Fact]
    public void SampleCapSetsTheCompromisedFlag()
    {
        var pulse = PulseSimulator.Simulate(
            Line30(new Load(N(1), LoadKind.Resistor, ROhms: 50)), Tdr1502C,
            DefaultPulse with { RiseTimeS = 200e-12, FallTimeS = 200e-12, MaxLog2M = 12 }, N(1));
        Assert.True(pulse.Compromised);
        Assert.Equal(4096, pulse.Volts.Length);
    }

    // ---- probe points from the document compiler ----

    [Fact]
    public void CompilationListsReachableProbesOnly()
    {
        var document = new ProjectDocument
        {
            SchemaVersion = ProjectDocument.CurrentSchemaVersion,
            Instrument = new InstrumentDocument { PresetId = "tek-1502c" },
            Nodes =
            [
                new DocumentNode { Id = "tdr", Kind = DocumentNodeKind.Tdr },
                new DocumentNode { Id = "feed", Kind = DocumentNodeKind.Cable, CablePresetId = "rg58-belden-9201", LengthM = 30 },
                new DocumentNode { Id = "j1", Kind = DocumentNodeKind.Junction },
                new DocumentNode { Id = "tail", Kind = DocumentNodeKind.Cable, CablePresetId = "rg58-belden-9201", LengthM = 20 },
                new DocumentNode { Id = "term", Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Resistor, ROhms = 75 },
                new DocumentNode { Id = "island", Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Open },
            ],
            Wires =
            [
                new DocumentWire { FromNodeId = "tdr", FromPort = "P", ToNodeId = "feed", ToPort = "A" },
                new DocumentWire { FromNodeId = "feed", FromPort = "B", ToNodeId = "j1", ToPort = "P" },
                new DocumentWire { FromNodeId = "j1", FromPort = "P", ToNodeId = "tail", ToPort = "A" },
                new DocumentWire { FromNodeId = "tail", FromPort = "B", ToNodeId = "term", ToPort = "P" },
            ],
        };
        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);

        Assert.Equal(["tdr", "j1", "term"], compilation.Probes.Select(p => p.NodeId).ToArray());

        // The junction's net really is mid-line: pulse-probe it end to end.
        var junctionNet = compilation.Probes.Single(p => p.NodeId == "j1").Net;
        var pulse = PulseSimulator.Simulate(
            compilation.Model!, compilation.Instrument!, DefaultPulse, junctionNet);
        Assert.True(pulse.MaxImagRatio < 1e-10);
    }
}
