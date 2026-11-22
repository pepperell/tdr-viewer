using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// The heart of M3: documents (canvas nodes + wires) must compile to the same physics
/// the M1/M2 engine tests pinned. Union-find net resolution is checked against the
/// closed-form lattice answers; validation is checked to be issues-not-exceptions.
/// </summary>
public class DocumentCompilerTests
{
    private const string Lossless50 = "test-lossless-50";
    private const double Vf = 0.66;
    private const double V = Vf * C0;

    private static readonly ComponentCatalog LosslessCatalog = new()
    {
        Cables = [Lossless(50, Vf)],
        Connectors = [.. BuiltInCatalog.Connectors.Where(c => c.Id == "splice-clean")],
        Instruments = [.. BuiltInCatalog.Instruments.Where(i => i.Id == "tek-1502c")],
        Faults = [],
    };

    // ---- document builders ----

    private static DocumentNode TdrNode(string id = "tdr")
        => new() { Id = id, Kind = DocumentNodeKind.Tdr };

    private static DocumentNode CableNode(
        string id, double? lengthM, string? preset = Lossless50, double? z0 = null, double? vf = null)
        => new()
        {
            Id = id,
            Kind = DocumentNodeKind.Cable,
            CablePresetId = preset,
            LengthM = lengthM,
            Z0OverrideOhms = z0,
            VfOverride = vf,
        };

    private static DocumentNode ResistorNode(string id, double rOhms)
        => new() { Id = id, Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Resistor, ROhms = rOhms };

    private static DocumentWire W(string fromNode, string fromPort, string toNode, string toPort)
        => new() { FromNodeId = fromNode, FromPort = fromPort, ToNodeId = toNode, ToPort = toPort };

    private static ProjectDocument Doc(
        DocumentNode[] nodes, DocumentWire[] wires, InstrumentDocument? instrument = null) => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = instrument ?? new InstrumentDocument { PresetId = "tek-1502c" },
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

    private static double MaxRhoDifference(TdrTrace a, TdrTrace b)
    {
        Assert.Equal(a.Rho.Length, b.Rho.Length);
        double max = 0;
        for (int k = 0; k < a.Rho.Length; k++)
            max = Math.Max(max, Math.Abs(a.Rho[k] - b.Rho[k]));
        return max;
    }

    // ---- physics through the document path ----

    [Fact]
    public void ChainDocumentTakesTheChainPathAndMatchesTheDcDivider()
    {
        var document = Doc(
            [
                TdrNode(),
                CableNode("feed", 30, "rg58-belden-9201"),
                new DocumentNode { Id = "splice", Kind = DocumentNodeKind.Connector, ConnectorPresetId = "splice-clean" },
                CableNode("tail", 5, "rg58-belden-9201"),
                ResistorNode("term", 75),
            ],
            [
                W("tdr", "P", "feed", "A"),
                W("feed", "B", "splice", "A"),
                W("splice", "B", "tail", "A"),
                W("tail", "B", "term", "P"),
            ]);

        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.Empty(compilation.Issues);

        var trace = TdrSimulator.Simulate(compilation.Model!, compilation.Instrument!);
        Assert.Equal(SolverPath.Chain, trace.Solver);
        AssertHermitian(trace);

        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var splice = BuiltInCatalog.Connectors.Single(c => c.Id == "splice-clean");
        double loopR = (rg58.RdcCenterOhmsPerKm + rg58.RdcShieldOhmsPerKm) / 1000 * 35
            + splice.SeriesResistanceOhms;
        double expectedDc = (75 + loopR - 50) / (75 + loopR + 50);
        Assert.Equal(expectedDc, trace.DcReflection, 1e-9);
    }

    [Fact]
    public void TeeDocumentWithThreeWiresAtOnePortSolvesNodalToMinusOneThird()
    {
        // Case 9 (matched tee) built from a document: three wires meeting at feed.B
        // are one electrical node - no junction element involved.
        var trace = SimulateDoc(MatchedTee(viaJunction: false), LosslessCatalog);

        Assert.Equal(SolverPath.Nodal, trace.Solver);
        AssertHermitian(trace);
        Assert.True(trace.Settled);
        Assert.Equal(-1.0 / 3, trace.DcReflection, 1e-9);

        double tJ = 2 * 30 / V;
        Assert.Equal(-1.0 / 3, PlateauMean(trace, tJ + 20e-9, tJ + 300e-9), 2e-3);
    }

    [Fact]
    public void JunctionNodeIsPurelyVisual()
    {
        var direct = SimulateDoc(MatchedTee(viaJunction: false), LosslessCatalog);
        var viaJunction = SimulateDoc(MatchedTee(viaJunction: true), LosslessCatalog);
        Assert.True(MaxRhoDifference(direct, viaJunction) < 1e-12,
            "A junction node changed the trace.");
    }

    private static ProjectDocument MatchedTee(bool viaJunction)
    {
        DocumentNode[] nodes =
        [
            TdrNode(),
            CableNode("feed", 30),
            CableNode("arm10", 10),
            CableNode("arm20", 20),
            ResistorNode("loadA", 50),
            ResistorNode("loadB", 50),
        ];
        DocumentWire[] armWires =
        [
            W("arm10", "B", "loadA", "P"),
            W("arm20", "B", "loadB", "P"),
        ];
        return viaJunction
            ? Doc(
                [.. nodes, new DocumentNode { Id = "j", Kind = DocumentNodeKind.Junction }],
                [
                    W("tdr", "P", "feed", "A"),
                    W("feed", "B", "j", "P"),
                    W("j", "P", "arm10", "A"),
                    W("j", "P", "arm20", "A"),
                    .. armWires,
                ])
            : Doc(
                nodes,
                [
                    W("tdr", "P", "feed", "A"),
                    W("feed", "B", "arm10", "A"),
                    W("feed", "B", "arm20", "A"),
                    .. armWires,
                ]);
    }

    [Fact]
    public void RingDocumentReproducesTheLatticeClimb()
    {
        // Case 13 from a document: 30 m feed into a 10 m + 20 m ring. Every circulating
        // path is 30 m, so plateaus follow p_k = 1 - 4*3^(-k) on a lap grid of 30m/v.
        var document = Doc(
            [
                TdrNode(),
                CableNode("feed", 30),
                CableNode("ringA", 10),
                CableNode("ringB", 20),
            ],
            [
                W("tdr", "P", "feed", "A"),
                W("feed", "B", "ringA", "A"),
                W("feed", "B", "ringB", "A"),
                W("ringA", "B", "ringB", "B"),
            ]);

        var trace = SimulateDoc(document, LosslessCatalog);
        Assert.Equal(SolverPath.Nodal, trace.Solver);
        AssertHermitian(trace);
        Assert.True(trace.Settled);

        double tJ = 2 * 30 / V;
        double lap = 30 / V;
        for (int k = 1; k <= 3; k++)
        {
            double plateau = PlateauMean(trace, tJ + (k - 1) * lap + 20e-9, tJ + k * lap - 20e-9);
            Assert.Equal(1 - 4 * Math.Pow(3, -k), plateau, 2e-3);
        }

        Assert.InRange(trace.DcReflection, 1 - 1e-9, 1);
    }

    [Fact]
    public void DanglingEndMatchesExplicitOpenLoad()
    {
        var dangling = Doc(
            [TdrNode(), CableNode("feed", 30)],
            [W("tdr", "P", "feed", "A")]);
        var explicitOpen = Doc(
            [
                TdrNode(),
                CableNode("feed", 30),
                new DocumentNode { Id = "open", Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Open },
            ],
            [W("tdr", "P", "feed", "A"), W("feed", "B", "open", "P")]);

        var a = SimulateDoc(dangling, LosslessCatalog);
        var b = SimulateDoc(explicitOpen, LosslessCatalog);
        Assert.Equal(SolverPath.Chain, a.Solver);
        Assert.True(MaxRhoDifference(a, b) < 1e-12,
            "A dangling end differs from an explicit open load.");
    }

    [Fact]
    public void CableOverridesReachTheEngine()
    {
        // Z0 override 75 into a matched 75 ohm termination: Gamma(0) = (75-50)/(75+50).
        var document = Doc(
            [TdrNode(), CableNode("feed", 30, z0: 75, vf: 0.5), ResistorNode("term", 75)],
            [W("tdr", "P", "feed", "A"), W("feed", "B", "term", "P")]);

        var trace = SimulateDoc(document, LosslessCatalog);
        Assert.Equal(0.2, trace.DcReflection, 1e-9);
        Assert.Equal(0.5, trace.DisplayVelocityFactor);
    }

    [Fact]
    public void InstrumentOverridesResolve()
    {
        var document = Doc(
            [TdrNode(), CableNode("feed", 30)],
            [W("tdr", "P", "feed", "A")],
            new InstrumentDocument
            {
                PresetId = "tek-1502c",
                RiseTimePsOverride = 100,
                AmplitudeVoltsOverride = 0.5,
                SourceImpedanceOverrideOhms = 75,
            });

        var compilation = DocumentCompiler.Compile(document, LosslessCatalog);
        Assert.Empty(compilation.Issues);
        Assert.Equal(100e-12, compilation.Instrument!.RiseTimeS, 1e-20);
        Assert.Equal(0.5, compilation.Instrument.StepAmplitudeVolts);
        Assert.Equal(75, compilation.Instrument.SourceImpedanceOhms);
    }

    [Fact]
    public void DefaultDocumentCompilesCleanAndReproducesTheM2Demo()
    {
        var compilation = DocumentCompiler.Compile(ProjectSerializer.LoadEmbeddedDefault(), BuiltInCatalog);
        Assert.Empty(compilation.Issues);

        var fromDocument = TdrSimulator.Simulate(compilation.Model!, compilation.Instrument!);
        Assert.Equal(SolverPath.Nodal, fromDocument.Solver);

        // The hand-built M2 demo network (what MainWindow.PlotDemoTrace hardcoded),
        // plus the BNC mated pair the compiler now splices at the TDR's front panel
        // (1502C port is bnc-50, RG-58 defaults to bnc-50).
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var bnc = BuiltInCatalog.ConnectorFamilies.Single(f => f.Id == "bnc-50");
        var handBuilt = TdrSimulator.Simulate(
            Network(
                new SourcePort(N(0)),
                new Connector(N(0), N(4), InterfaceSynthesis.ExcessSpec(bnc)),
                new CableSegment(N(4), N(5), InterfaceSynthesis.MatedSpec(bnc), bnc.MatedLengthMm * 1e-3),
                new CableSegment(N(5), N(1), rg58, 30),
                new CableSegment(N(1), N(2), rg58, 10),
                new CableSegment(N(1), N(3), rg58, 20)),
            Tdr1502C);

        Assert.True(MaxRhoDifference(fromDocument, handBuilt) < 1e-12,
            "Default document does not reproduce the M2 demo trace.");
    }

    // ---- issues, not exceptions ----

    [Fact]
    public void NoSourceIsFatal()
    {
        var compilation = DocumentCompiler.Compile(
            Doc([CableNode("feed", 30)], []), LosslessCatalog);
        Assert.Contains(compilation.Issues, i => i.Kind == DocumentIssueKind.NoSource);
        Assert.True(compilation.HasFatalIssues);
        Assert.Null(compilation.Model);
        Assert.NotNull(compilation.Instrument);
    }

    [Fact]
    public void TwoSourcesAreFatal()
    {
        var compilation = DocumentCompiler.Compile(
            Doc([TdrNode("tdr1"), TdrNode("tdr2"), CableNode("feed", 30)],
                [W("tdr1", "P", "feed", "A"), W("tdr2", "P", "feed", "B")]),
            LosslessCatalog);
        var issue = Assert.Single(compilation.Issues, i => i.Kind == DocumentIssueKind.MultipleSources);
        Assert.Equal(["tdr1", "tdr2"], issue.NodeIds);
        Assert.Null(compilation.Model);
    }

    [Fact]
    public void DisconnectedIslandIsAdvisoryAndOmitted()
    {
        var connectedOnly = Doc(
            [TdrNode(), CableNode("feed", 30)],
            [W("tdr", "P", "feed", "A")]);
        var withIslands = Doc(
            [TdrNode(), CableNode("feed", 30), CableNode("orphan", 5), ResistorNode("orphanLoad", 50)],
            [W("tdr", "P", "feed", "A")]);

        var compilation = DocumentCompiler.Compile(withIslands, LosslessCatalog);
        Assert.False(compilation.HasFatalIssues);
        Assert.NotNull(compilation.Model);
        Assert.Equal(2, compilation.Issues.Count); // the unwired cable and load are separate islands
        Assert.All(compilation.Issues, i => Assert.Equal(DocumentIssueKind.DisconnectedFromSource, i.Kind));
        Assert.Contains(compilation.Issues, i => i.NodeIds.Contains("orphan"));

        var a = SimulateDoc(connectedOnly, LosslessCatalog);
        var b = TdrSimulator.Simulate(compilation.Model!, compilation.Instrument!);
        Assert.True(MaxRhoDifference(a, b) < 1e-12, "Omitted island changed the trace.");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(null)]
    public void NonPositiveLengthIsFatal(double? lengthM)
    {
        var compilation = DocumentCompiler.Compile(
            Doc([TdrNode(), CableNode("feed", lengthM)], [W("tdr", "P", "feed", "A")]),
            LosslessCatalog);
        var issue = Assert.Single(compilation.Issues);
        Assert.Equal(DocumentIssueKind.NonPositiveLength, issue.Kind);
        Assert.Null(compilation.Model);
    }

    [Fact]
    public void HalfConfiguredLoadIsFatal()
    {
        var capacitorWithoutValue = new DocumentNode
        {
            Id = "cap",
            Kind = DocumentNodeKind.Load,
            LoadKind = LoadKind.Capacitor,
        };
        var compilation = DocumentCompiler.Compile(
            Doc([TdrNode(), CableNode("feed", 30), capacitorWithoutValue],
                [W("tdr", "P", "feed", "A"), W("feed", "B", "cap", "P")]),
            LosslessCatalog);
        var issue = Assert.Single(compilation.Issues);
        Assert.Equal(DocumentIssueKind.InvalidLoadValues, issue.Kind);
        Assert.Null(compilation.Model);
    }

    [Fact]
    public void UnknownPresetIdsAreFatal()
    {
        var compilation = DocumentCompiler.Compile(
            Doc([TdrNode(), CableNode("feed", 30, preset: "no-such-cable")],
                [W("tdr", "P", "feed", "A")],
                new InstrumentDocument { PresetId = "no-such-tdr" }),
            LosslessCatalog);
        Assert.Equal(2, compilation.Issues.Count(i => i.Kind == DocumentIssueKind.UnknownPresetId));
        Assert.Null(compilation.Model);
        Assert.Null(compilation.Instrument);
    }

    [Fact]
    public void SelfLoopIsFatal()
    {
        var compilation = DocumentCompiler.Compile(
            Doc([TdrNode(), CableNode("feed", 30)],
                [W("tdr", "P", "feed", "A"), W("feed", "A", "feed", "B")]),
            LosslessCatalog);
        var issue = Assert.Single(compilation.Issues);
        Assert.Equal(DocumentIssueKind.SelfLoop, issue.Kind);
        Assert.Null(compilation.Model);
    }

    [Fact]
    public void BadOverrideValuesAreFatal()
    {
        var badCable = DocumentCompiler.Compile(
            Doc([TdrNode(), CableNode("feed", 30, z0: -5)], [W("tdr", "P", "feed", "A")]),
            LosslessCatalog);
        Assert.Contains(badCable.Issues, i => i.Kind == DocumentIssueKind.InvalidCableValues);
        Assert.Null(badCable.Model);

        var badVf = DocumentCompiler.Compile(
            Doc([TdrNode(), CableNode("feed", 30, vf: 1.5)], [W("tdr", "P", "feed", "A")]),
            LosslessCatalog);
        Assert.Contains(badVf.Issues, i => i.Kind == DocumentIssueKind.InvalidCableValues);

        var badInstrument = DocumentCompiler.Compile(
            Doc([TdrNode(), CableNode("feed", 30)], [W("tdr", "P", "feed", "A")],
                new InstrumentDocument { PresetId = "tek-1502c", RiseTimePsOverride = -1 }),
            LosslessCatalog);
        Assert.Contains(badInstrument.Issues, i => i.Kind == DocumentIssueKind.InvalidInstrumentValues);
        Assert.Null(badInstrument.Instrument);
    }

    [Fact]
    public void CorruptFilesThrowInsteadOfCompiling()
    {
        // Duplicate ids and wires to nonexistent ports can't be produced by the editor,
        // only by hand-editing - those throw rather than badge.
        Assert.Throws<InvalidDataException>(() => DocumentCompiler.Compile(
            Doc([TdrNode("x"), CableNode("x", 30)], []), LosslessCatalog));
        Assert.Throws<InvalidDataException>(() => DocumentCompiler.Compile(
            Doc([TdrNode()], [W("tdr", "P", "ghost", "A")]), LosslessCatalog));
    }
}
