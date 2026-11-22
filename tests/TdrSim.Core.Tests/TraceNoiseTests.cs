using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// M5 step 3: display-stage noise. The statistics must be honest (RMS as configured,
/// sqrt(N) averaging), the three y-representations must stay mutually consistent, and
/// the clean path must be entirely untouched when noise is off.
/// </summary>
public class TraceNoiseTests
{
    private static TdrTrace CleanTrace() => TdrSimulator.Simulate(
        Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 100)), Tdr1502C);

    private static double DiffRms(TdrTrace clean, TdrTrace noisy)
    {
        double sum = 0;
        for (int k = 0; k < clean.Volts.Length; k++)
        {
            double d = noisy.Volts[k] - clean.Volts[k];
            sum += d * d;
        }

        return Math.Sqrt(sum / clean.Volts.Length);
    }

    [Fact]
    public void MeasuredRmsMatchesTheSetting()
    {
        var clean = CleanTrace();
        var noisy = TraceNoise.Apply(clean, new TraceNoiseSettings(0.002, Sweeps: 1),
            Tdr1502C.StepAmplitudeVolts, Tdr1502C.SourceImpedanceOhms, seed: 42);
        Assert.InRange(DiffRms(clean, noisy), 0.0017, 0.0023);
    }

    [Fact]
    public void SixteenSweepAveragingReducesRmsFourfold()
    {
        var clean = CleanTrace();
        var noisy = TraceNoise.Apply(clean, new TraceNoiseSettings(0.002, Sweeps: 16),
            Tdr1502C.StepAmplitudeVolts, Tdr1502C.SourceImpedanceOhms, seed: 42);
        Assert.InRange(DiffRms(clean, noisy), 0.000425, 0.000575);
    }

    [Fact]
    public void SameSeedIsDeterministicDifferentSeedIsNot()
    {
        var clean = CleanTrace();
        var settings = new TraceNoiseSettings(0.002, 1);
        var a = TraceNoise.Apply(clean, settings, 0.3, 50, seed: 7);
        var b = TraceNoise.Apply(clean, settings, 0.3, 50, seed: 7);
        var c = TraceNoise.Apply(clean, settings, 0.3, 50, seed: 8);

        Assert.Equal(a.Volts, b.Volts); // exact element-wise equality
        Assert.NotEqual(a.Volts[0], c.Volts[0]);
    }

    [Fact]
    public void ZeroRmsReturnsTheInputInstance()
    {
        var clean = CleanTrace();
        Assert.Same(clean, TraceNoise.Apply(clean, new TraceNoiseSettings(0, 4), 0.3, 50, 1));
    }

    [Fact]
    public void RhoVoltsAndOhmsStayMutuallyConsistent()
    {
        var clean = CleanTrace();
        double amplitude = Tdr1502C.StepAmplitudeVolts;
        double zRef = Tdr1502C.SourceImpedanceOhms;
        var noisy = TraceNoise.Apply(clean, new TraceNoiseSettings(0.005, 1), amplitude, zRef, 3);

        const double rhoClamp = 1 - 1e-9;
        for (int k = 0; k < clean.Rho.Length; k += 97)
        {
            double voltNoise = noisy.Volts[k] - clean.Volts[k];
            Assert.Equal(voltNoise / amplitude, noisy.Rho[k] - clean.Rho[k], 1e-12);
            double r = Math.Clamp(noisy.Rho[k], -rhoClamp, rhoClamp);
            Assert.Equal(zRef * (1 + r) / (1 - r), noisy.Ohms[k]);
        }
    }

    [Fact]
    public void TimingArraysAndFlagsAreCarriedUnchanged()
    {
        var clean = CleanTrace();
        var noisy = TraceNoise.Apply(clean, new TraceNoiseSettings(0.002, 1), 0.3, 50, 5);

        Assert.Same(clean.TimeS, noisy.TimeS);
        Assert.Same(clean.DistanceM, noisy.DistanceM);
        Assert.Same(clean.Grid, noisy.Grid);
        Assert.Equal(clean.DcReflection, noisy.DcReflection);
        Assert.Equal(clean.Settled, noisy.Settled);
        Assert.Equal(clean.Solver, noisy.Solver);
    }

    // ---- document resolution ----

    private static DocumentCompilation CompileWithInstrument(InstrumentDocument instrument)
        => DocumentCompiler.Compile(
            new ProjectDocument
            {
                SchemaVersion = ProjectDocument.CurrentSchemaVersion,
                Instrument = instrument,
                Nodes = [new DocumentNode { Id = "tdr", Kind = DocumentNodeKind.Tdr }],
                Wires = [],
            },
            BuiltInCatalog);

    [Fact]
    public void NoiseSettingsResolveFromTheDocument()
    {
        var compilation = CompileWithInstrument(new InstrumentDocument
        {
            PresetId = "tek-1502c",
            NoiseRmsMillivolts = 2.5,
            AveragingSweeps = 16,
        });
        Assert.Empty(compilation.Issues);
        Assert.Equal(new TraceNoiseSettings(0.0025, 16), compilation.Noise);
        Assert.Equal(0.0025 / 4, compilation.Noise!.EffectiveRmsVolts, 1e-15);

        var sweepsDefault = CompileWithInstrument(new InstrumentDocument
        {
            PresetId = "tek-1502c",
            NoiseRmsMillivolts = 3,
        });
        Assert.Equal(new TraceNoiseSettings(0.003, 1), sweepsDefault.Noise);

        // No document value -> the selected preset's own noise floor (1502C: 2 mV);
        // an explicit 0 is the off switch.
        var presetFloor = CompileWithInstrument(new InstrumentDocument { PresetId = "tek-1502c" });
        Assert.Equal(new TraceNoiseSettings(0.002, 1), presetFloor.Noise);

        var off = CompileWithInstrument(new InstrumentDocument
        {
            PresetId = "tek-1502c",
            NoiseRmsMillivolts = 0,
        });
        Assert.Null(off.Noise);
    }

    [Fact]
    public void EveryInstrumentPresetHasARealisticNoiseFloor()
    {
        Assert.All(BuiltInCatalog.Instruments, i => Assert.True(
            i.NoiseFloorMillivolts > 0, $"{i.Id} has no noise floor."));

        // The lab sampling head is far quieter than the handheld tester.
        double lab = BuiltInCatalog.Instruments.Single(i => i.Id == "tek-80e04").NoiseFloorMillivolts;
        double handheld = BuiltInCatalog.Instruments.Single(i => i.Id == "cable-tester").NoiseFloorMillivolts;
        Assert.Equal(2.0, BuiltInCatalog.Instruments.Single(i => i.Id == "tek-1502c").NoiseFloorMillivolts);
        Assert.True(lab < handheld / 10);
    }

    [Fact]
    public void AddGaussianMatchesTheRequestedRmsAndIsSeedDeterministic()
    {
        var a = new double[8192];
        TraceNoise.AddGaussian(a, 0.002, seed: 42);
        double rms = Math.Sqrt(a.Sum(v => v * v) / a.Length);
        Assert.InRange(rms, 0.0017, 0.0023);

        var b = new double[8192];
        TraceNoise.AddGaussian(b, 0.002, seed: 42);
        Assert.Equal(a, b);

        var untouched = new double[16];
        TraceNoise.AddGaussian(untouched, 0, seed: 1);
        Assert.All(untouched, v => Assert.Equal(0.0, v));
    }

    [Fact]
    public void BadNoiseSettingsAreFatal()
    {
        foreach (var instrument in new InstrumentDocument[]
        {
            new() { PresetId = "tek-1502c", NoiseRmsMillivolts = -1 },
            new() { PresetId = "tek-1502c", NoiseRmsMillivolts = 2, AveragingSweeps = 0 },
        })
        {
            var compilation = CompileWithInstrument(instrument);
            Assert.Contains(compilation.Issues,
                i => i.Kind == DocumentIssueKind.InvalidInstrumentValues);
            Assert.True(compilation.HasFatalIssues);
        }
    }
}
