using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// M5 steps 1-2: fault elements compile onto existing engine elements, and the mapping
/// is pinned to closed forms - series-R plateau R/(R+2Z0), bridge dip -Z0/(2R+Z0),
/// section-override dip level/timing from the lattice, loss injection through the
/// standard k1/k2 fit. Plus the cache-stability guarantee that keeps fault edits live.
/// </summary>
public class FaultTests
{
    private const string Lossless50 = "test-lossless-50";
    private const double Vf = 0.66;
    private const double V = Vf * C0;

    private static readonly ComponentCatalog LosslessCatalog = new()
    {
        Cables = [Lossless(50, Vf)],
        Connectors = [],
        Instruments = [.. BuiltInCatalog.Instruments.Where(i => i.Id == "tek-1502c")],
        Faults = [.. BuiltInCatalog.Faults],
    };

    private static DocumentNode TdrNode() => new() { Id = "tdr", Kind = DocumentNodeKind.Tdr };

    private static DocumentNode CableNode(string id, double lengthM, string preset = Lossless50)
        => new() { Id = id, Kind = DocumentNodeKind.Cable, CablePresetId = preset, LengthM = lengthM };

    private static DocumentNode ResistorNode(string id, double rOhms)
        => new() { Id = id, Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Resistor, ROhms = rOhms };

    private static DocumentNode SectionNode(
        string id, string basePreset, double? lengthM, double? z0Scale, double? extraDb = null) => new()
    {
        Id = id,
        Kind = DocumentNodeKind.Fault,
        FaultKind = FaultKind.SectionOverride,
        CablePresetId = basePreset,
        LengthM = lengthM,
        Z0Scale = z0Scale,
        ExtraDielectricLossDbPer100m = extraDb,
    };

    private static DocumentNode BridgeNode(string id, double? rOhms)
        => new() { Id = id, Kind = DocumentNodeKind.Fault, FaultKind = FaultKind.ShuntBridge, ROhms = rOhms };

    private static DocumentNode SeriesNode(string id, double? rOhms)
        => new() { Id = id, Kind = DocumentNodeKind.Fault, FaultKind = FaultKind.SeriesFault, ROhms = rOhms };

    private static DocumentWire W(string fromNode, string fromPort, string toNode, string toPort)
        => new() { FromNodeId = fromNode, FromPort = fromPort, ToNodeId = toNode, ToPort = toPort };

    private static ProjectDocument Doc(DocumentNode[] nodes, DocumentWire[] wires) => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = new InstrumentDocument { PresetId = "tek-1502c" },
        Nodes = nodes,
        Wires = wires,
    };

    private static TdrTrace SimulateDoc(ProjectDocument document, ComponentCatalog catalog)
    {
        var compilation = DocumentCompiler.Compile(document, catalog);
        Assert.False(compilation.HasFatalIssues,
            "Unexpected fatal issues: " + string.Join("; ", compilation.Issues.Select(i => i.Message)));
        return TdrSimulator.Simulate(compilation.Model!, compilation.Instrument!);
    }

    // ---- closed-form physics ----

    [Fact]
    public void SeriesFaultPlateausAtTheClassicDividerThenOpens()
    {
        // 30 m feed, 10 ohm series fault, 10 m open-ended tail. Between the fault's
        // arrival and the open's echo the line behind the fault looks matched, so
        // rho = R/(R + 2*Z0); the open makes Gamma(0) exactly 1 (no DC current, no drop).
        var document = Doc(
            [TdrNode(), CableNode("feed", 30), SeriesNode("fault", 10), CableNode("tail", 10)],
            [W("tdr", "P", "feed", "A"), W("feed", "B", "fault", "A"), W("fault", "B", "tail", "A")]);
        var trace = SimulateDoc(document, LosslessCatalog);
        AssertHermitian(trace);
        Assert.Equal(SolverPath.Chain, trace.Solver);

        double tFault = 2 * 30 / V;
        double plateau = PlateauMean(trace, tFault + 15e-9, tFault + 85e-9);
        Assert.Equal(10.0 / 110.0, plateau, 1e-3);
        Assert.Equal(1.0, trace.DcReflection, 1e-9);
    }

    [Fact]
    public void SeriesFaultWithMatchedTerminationSettlesToTheExactDivider()
    {
        var document = Doc(
            [TdrNode(), CableNode("feed", 30), SeriesNode("fault", 10), CableNode("tail", 10), ResistorNode("term", 50)],
            [
                W("tdr", "P", "feed", "A"), W("feed", "B", "fault", "A"),
                W("fault", "B", "tail", "A"), W("tail", "B", "term", "P"),
            ]);
        var trace = SimulateDoc(document, LosslessCatalog);

        // DC: Zin = R_fault + R_term = 60 -> Gamma = 10/110, and the trace sits there
        // from the fault's arrival on (the line behind it is matched).
        Assert.Equal(10.0 / 110.0, trace.DcReflection, 1e-9);
        Assert.Equal(10.0 / 110.0, PlateauMean(trace, 2 * 30 / V + 15e-9, 2 * 30 / V + 85e-9), 1e-3);
    }

    [Fact]
    public void BridgeFaultDipsToTheShuntDividerLevel()
    {
        // 470 ohm bridge at a mid-line junction with matched continuation:
        // Zin = R || Z0 -> rho = -Z0/(2R + Z0). Lossless, so DC is the same level.
        var document = Doc(
            [TdrNode(), CableNode("feed", 30), BridgeNode("fault", 470), CableNode("tail", 10), ResistorNode("term", 50)],
            [
                W("tdr", "P", "feed", "A"),
                W("feed", "B", "fault", "P"),
                W("feed", "B", "tail", "A"),
                W("tail", "B", "term", "P"),
            ]);
        var trace = SimulateDoc(document, LosslessCatalog);
        AssertHermitian(trace);

        double expected = -50.0 / (2 * 470 + 50);
        Assert.Equal(expected, PlateauMean(trace, 2 * 30 / V + 15e-9, 2 * 30 / V + 85e-9), 1e-3);
        Assert.Equal(expected, trace.DcReflection, 1e-9);
    }

    [Fact]
    public void SectionOverrideDipHasTheLatticeLevelAndTheScaledVelocityWidth()
    {
        // s = 0.7 on a lossless 50 ohm base: Z0' = 35 -> first dip (35-50)/85 = -3/17;
        // the section's velocity scales by s too, so the dip lasts 2*l/(s*Vf*c).
        var document = Doc(
            [
                TdrNode(), CableNode("feed", 30),
                SectionNode("fault", Lossless50, lengthM: 2, z0Scale: 0.7),
                CableNode("tail", 10), ResistorNode("term", 50),
            ],
            [
                W("tdr", "P", "feed", "A"), W("feed", "B", "fault", "A"),
                W("fault", "B", "tail", "A"), W("tail", "B", "term", "P"),
            ]);
        var trace = SimulateDoc(document, LosslessCatalog);
        AssertHermitian(trace);

        double tFault = 2 * 30 / V;
        double dipWidth = 2 * 2 / (0.7 * V);
        Assert.Equal(-3.0 / 17.0, PlateauMean(trace, tFault + 6e-9, tFault + dipWidth - 6e-9), 2e-3);

        // Recovery edge: rho crosses half the dip level on the way back up.
        double crossing = CrossingTime(trace, -1.5 / 17.0, tFault + dipWidth / 2, tFault + dipWidth + 15e-9);
        Assert.Equal(tFault + dipWidth, crossing, 1e-9);

        // All-lossless and matched: the section is DC-transparent.
        Assert.Equal(0.0, trace.DcReflection, 1e-9);
    }

    [Fact]
    public void ExtraDielectricLossFitsAsAPureK2Bump()
    {
        var spec = FaultSynthesis.SectionSpec(Lossless(50, Vf), 1.0, 3.0);
        var loss = LossFit.FromSpec(spec);
        Assert.Equal(0.0, loss.K1DbPer100mSqrtMhz, 1e-9);
        Assert.Equal(0.03, loss.K2DbPer100mMhz, 1e-9);
    }

    [Fact]
    public void ExtraLossAttenuatesTheReturningReflection()
    {
        // s = 1 isolates the loss term: same impedance everywhere, but the echo from
        // the open end crosses 2 x 10 m of extra-lossy section and comes back smaller.
        ProjectDocument DocWithLoss(double extraDb) => Doc(
            [TdrNode(), CableNode("feed", 30), SectionNode("fault", Lossless50, 10, 1.0, extraDb)],
            [W("tdr", "P", "feed", "A"), W("feed", "B", "fault", "A")]);

        var clean = SimulateDoc(DocWithLoss(0), LosslessCatalog);
        var lossy = SimulateDoc(DocWithLoss(50), LosslessCatalog);

        double tOpen = 2 * 30 / V + 2 * 10 / V; // s = 1: same velocity in the section
        double cleanEdge = RhoAt(clean, tOpen + 5e-9);
        double lossyEdge = RhoAt(lossy, tOpen + 5e-9);
        Assert.True(cleanEdge > 0.9, $"Clean open reflection should be near 1, got {cleanEdge:f3}.");
        Assert.True(lossyEdge < cleanEdge - 0.05,
            $"Extra loss should attenuate the echo: clean {cleanEdge:f3} vs lossy {lossyEdge:f3}.");
    }

    [Fact]
    public void WaterIngressPresetDefaultsCompileAndDip()
    {
        var preset = BuiltInCatalog.Faults.Single(f => f.Id == "water-ingress");
        var document = Doc(
            [
                TdrNode(),
                CableNode("feed", 30, "rg58-belden-9201"),
                SectionNode("fault", "rg58-belden-9201",
                    preset.Defaults.LengthM, preset.Defaults.Z0Scale, preset.Defaults.ExtraDielectricLossDbPer100m),
                CableNode("tail", 20, "rg58-belden-9201"),
            ],
            [W("tdr", "P", "feed", "A"), W("feed", "B", "fault", "A"), W("fault", "B", "tail", "A")]);
        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.Empty(compilation.Issues);

        var trace = TdrSimulator.Simulate(compilation.Model!, compilation.Instrument!);
        AssertHermitian(trace);

        // On real RG-58 the skin-effect creep lifts the pre-fault baseline (~+0.07 by
        // 300 ns) and slurs the dip, so assert the dip DEPTH against the local
        // baseline, not an absolute level (ideal would be -3/17 below it).
        double tFault = 2 * 30 / V;
        double baseline = RhoAt(trace, tFault - 3e-9);
        double minRho = baseline;
        for (int k = 0; k < trace.TimeS.Length; k++)
        {
            if (trace.TimeS[k] >= tFault + 2e-9 && trace.TimeS[k] <= tFault + 28e-9)
                minRho = Math.Min(minRho, trace.Rho[k]);
        }

        Assert.True(baseline - minRho > 0.10,
            $"Water-ingress dip too shallow: baseline {baseline:f3}, min {minRho:f3}.");
    }

    // ---- caching / stability ----

    [Fact]
    public void SectionSpecsAreReferenceStableAcrossCalls()
    {
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        Assert.Same(
            FaultSynthesis.SectionSpec(rg58, 0.7, 3.0),
            FaultSynthesis.SectionSpec(rg58, 0.7, 3.0));
    }

    [Fact]
    public void FaultDocumentsKeepTheSessionCachesWarmAcrossRecompiles()
    {
        // The whole point of FaultSynthesis memoization: recompiling an unchanged
        // document must not create new specs, so warm runs do zero spectrum work.
        var document = Doc(
            [
                TdrNode(), CableNode("feed", 30, "rg58-belden-9201"),
                SectionNode("fault", "rg58-belden-9201", 2, 0.7, 3.0), CableNode("tail", 10, "rg58-belden-9201"),
            ],
            [W("tdr", "P", "feed", "A"), W("feed", "B", "fault", "A"), W("fault", "B", "tail", "A")]);
        var settings = new SimulationSettings { WindowOverrideS = 32768 * 40e-12 };
        var session = new SimulationSession();

        var first = DocumentCompiler.Compile(document, BuiltInCatalog);
        TdrSimulator.Simulate(first.Model!, first.Instrument!, settings, session);
        int afterFirst = session.SpectrumComputeCount;
        Assert.Equal(2, afterFirst); // rg58 + the derived section spec

        var second = DocumentCompiler.Compile(document, BuiltInCatalog);
        TdrSimulator.Simulate(second.Model!, second.Instrument!, settings, session);
        Assert.Equal(afterFirst, session.SpectrumComputeCount);
        Assert.Equal(2, session.ModelComputeCount);
    }

    // ---- validation ----

    [Fact]
    public void BridgeAndSeriesFaultsNeedPositiveResistance()
    {
        foreach (var node in new[] { BridgeNode("f", null), BridgeNode("f", 0), BridgeNode("f", -5) })
        {
            var compilation = DocumentCompiler.Compile(
                Doc([TdrNode(), node], [W("tdr", "P", "f", "P")]), LosslessCatalog);
            Assert.Contains(compilation.Issues, i => i.Kind == DocumentIssueKind.InvalidFaultValues);
            Assert.Null(compilation.Model);
        }

        var series = DocumentCompiler.Compile(
            Doc([TdrNode(), SeriesNode("f", null)], [W("tdr", "P", "f", "A")]), LosslessCatalog);
        Assert.Contains(series.Issues, i => i.Kind == DocumentIssueKind.InvalidFaultValues);
    }

    [Fact]
    public void SectionOverrideValidationCatchesEveryBadInput()
    {
        DocumentCompilation Compile(DocumentNode fault) => DocumentCompiler.Compile(
            Doc([TdrNode(), fault], [W("tdr", "P", "fault", "A")]), LosslessCatalog);

        Assert.Contains(Compile(SectionNode("fault", "no-such-cable", 2, 0.7)).Issues,
            i => i.Kind == DocumentIssueKind.UnknownPresetId);
        Assert.Contains(Compile(SectionNode("fault", Lossless50, 0, 0.7)).Issues,
            i => i.Kind == DocumentIssueKind.NonPositiveLength);
        Assert.Contains(Compile(SectionNode("fault", Lossless50, 2, null)).Issues,
            i => i.Kind == DocumentIssueKind.InvalidFaultValues);
        Assert.Contains(Compile(SectionNode("fault", Lossless50, 2, -0.5)).Issues,
            i => i.Kind == DocumentIssueKind.InvalidFaultValues);
        Assert.Contains(Compile(SectionNode("fault", Lossless50, 2, 0.7, extraDb: -1)).Issues,
            i => i.Kind == DocumentIssueKind.InvalidFaultValues);

        // Scale that pushes the derived velocity factor past 1.
        Assert.Contains(Compile(SectionNode("fault", Lossless50, 2, 1.6)).Issues,
            i => i.Kind == DocumentIssueKind.InvalidFaultValues);

        // Fault with no kind at all (hand-edited file).
        var kindless = DocumentCompiler.Compile(
            Doc(
                [TdrNode(), new DocumentNode { Id = "fault", Kind = DocumentNodeKind.Fault }],
                [W("tdr", "P", "fault", "A")]),
            LosslessCatalog);
        Assert.Contains(kindless.Issues, i => i.Kind == DocumentIssueKind.InvalidFaultValues);
    }

    [Fact]
    public void TwoPortFaultSelfLoopIsFatal()
    {
        var document = Doc(
            [TdrNode(), CableNode("feed", 30), SeriesNode("fault", 10)],
            [
                W("tdr", "P", "feed", "A"), W("feed", "B", "fault", "A"),
                W("fault", "B", "feed", "B"),
            ]);
        var compilation = DocumentCompiler.Compile(document, LosslessCatalog);
        Assert.Contains(compilation.Issues, i => i.Kind == DocumentIssueKind.SelfLoop);
        Assert.Null(compilation.Model);
    }

    [Fact]
    public void DisconnectedFaultIsAdvisoryAndOmitted()
    {
        var document = Doc(
            [TdrNode(), CableNode("feed", 30), BridgeNode("island", 470)],
            [W("tdr", "P", "feed", "A")]);
        var compilation = DocumentCompiler.Compile(document, LosslessCatalog);

        Assert.Single(compilation.Issues);
        Assert.Equal(DocumentIssueKind.DisconnectedFromSource, compilation.Issues[0].Kind);
        Assert.NotNull(compilation.Model);
        Assert.DoesNotContain(compilation.Model!.Elements, e => e is Load);
    }
}
