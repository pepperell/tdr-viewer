using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// Automatic connector interfaces: the compiler splices real mating hardware (mated
/// pairs, barrels, between-series adapters) wherever two connectorized ends meet.
/// Pinned here: where insertion happens and where it must not, the physics of the
/// classic UHF ~35 ohm dip, override semantics ("none", unknown ids), balanced
/// refusal, DC-level invariance, and reference stability for the live caches.
/// </summary>
public class InterfaceTests
{
    private static DocumentNode Tdr(string id = "tdr") => new() { Id = id, Kind = DocumentNodeKind.Tdr };

    private static DocumentNode Cable(
        string id, string preset, double lengthM, string? connA = null, string? connB = null) => new()
    {
        Id = id,
        Kind = DocumentNodeKind.Cable,
        CablePresetId = preset,
        LengthM = lengthM,
        ConnectorAId = connA,
        ConnectorBId = connB,
    };

    private static DocumentNode Resistor(string id, double rOhms)
        => new() { Id = id, Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Resistor, ROhms = rOhms };

    private static DocumentNode Junction(string id)
        => new() { Id = id, Kind = DocumentNodeKind.Junction };

    private static DocumentWire W(string fromNode, string fromPort, string toNode, string toPort)
        => new() { FromNodeId = fromNode, FromPort = fromPort, ToNodeId = toNode, ToPort = toPort };

    private static ProjectDocument Doc(
        string instrumentPreset, DocumentNode[] nodes, DocumentWire[] wires,
        string? portConnectorId = null) => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = new InstrumentDocument
        {
            PresetId = instrumentPreset,
            PortConnectorId = portConnectorId,
        },
        Nodes = nodes,
        Wires = wires,
    };

    private static DocumentCompilation CompileClean(ProjectDocument document)
    {
        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.False(compilation.HasFatalIssues,
            "Unexpected fatal issues: " + string.Join("; ", compilation.Issues.Select(i => i.Message)));
        return compilation;
    }

    // ---- where interfaces appear ----

    [Fact]
    public void MatchingFamiliesGetOneMatedPairAtTheTdrPort()
    {
        // 1502C (bnc-50) into RG-58 (bnc-50): one excess lump + one mated section.
        var compilation = CompileClean(Doc("tek-1502c",
            [Tdr(), Cable("c1", "rg58-belden-9201", 30), Resistor("term", 50)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "term", "P")]));

        var note = Assert.Single(compilation.InterfaceNotes);
        Assert.Contains("BNC (50 Ω) mated pair", note);
        Assert.Single(compilation.Model!.Elements, e => e is Connector c && c.Spec.Id == "iface-excess-bnc-50");
        var mated = Assert.Single(compilation.Model.Elements,
            e => e is CableSegment s && s.Spec.Id == "iface-mated-bnc-50");
        Assert.Equal(0.020, ((CableSegment)mated).LengthM, 1e-12);
    }

    [Fact]
    public void CableToCableSameFamilyGetsADoubleFemaleBarrel()
    {
        var compilation = CompileClean(Doc("tek-1502c",
            [Tdr(), Cable("c1", "rg58-belden-9201", 20), Cable("c2", "rg58-belden-9201", 20), Resistor("term", 50)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "c2", "A"), W("c2", "B", "term", "P")]));

        Assert.Equal(2, compilation.InterfaceNotes.Count);
        Assert.Contains(compilation.InterfaceNotes, n => n.Contains("double-female barrel"));
        // TDR joint: 1 mated section; barrel joint: 2 mated sections.
        Assert.Equal(3, compilation.Model!.Elements.Count(
            e => e is CableSegment s && s.Spec.Id == "iface-mated-bnc-50"));
    }

    [Fact]
    public void MismatchedCoaxFamiliesGetTheCatalogedAdapter()
    {
        // CATV meter (f-75 panel) into RG-58 (bnc-50): F mated pair, F-to-BNC(50)
        // adapter body, BNC mated pair.
        var compilation = CompileClean(Doc("catv-75",
            [Tdr(), Cable("c1", "rg58-belden-9201", 30), Resistor("term", 50)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "term", "P")]));

        var note = Assert.Single(compilation.InterfaceNotes);
        Assert.Contains("F ↔ BNC (50 Ω) adapter", note);
        var elements = compilation.Model!.Elements;
        Assert.Single(elements, e => e is CableSegment s && s.Spec.Id == "iface-mated-f-75");
        Assert.Single(elements, e => e is CableSegment s && s.Spec.Id == "iface-mated-bnc-50");
        var body = (CableSegment)Assert.Single(elements,
            e => e is CableSegment s && s.Spec.Id == "iface-body-f75-bnc50");
        Assert.Equal(63.0, body.Spec.Z0Ohms);
        Assert.Equal(0.028, body.LengthM, 1e-12);
    }

    [Fact]
    public void UncatalogedCoaxPairFallsBackToAGenericAdapter()
    {
        // F-75 cable end forced against a UHF end: no cataloged part, generic body
        // at the average impedance.
        var compilation = CompileClean(Doc("tek-1502c",
            [
                Tdr(), Cable("c1", "rg58-belden-9201", 20, connB: "f-75"),
                Cable("c2", "rg213-belden-8267", 20), Resistor("term", 50),
            ],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "c2", "A"), W("c2", "B", "term", "P")]));

        Assert.Contains(compilation.InterfaceNotes, n => n.Contains("Generic"));
        var body = (CableSegment)Assert.Single(compilation.Model!.Elements,
            e => e is CableSegment s && s.Spec.Id.StartsWith("iface-generic-", StringComparison.Ordinal));
        Assert.Equal((75.0 + 50.0) / 2, body.Spec.Z0Ohms);
    }

    [Fact]
    public void JunctionTeesAndBareEndsStayIdeal()
    {
        // Tee through a junction: 3-way net, no hardware. "none" overrides and
        // bare-by-default cables (ideal teaching lines) insert nothing either.
        var tee = CompileClean(Doc("tek-1502c",
            [
                Tdr(), Cable("feed", "rg58-belden-9201", 30, connB: "none"), Junction("j"),
                Cable("a", "rg58-belden-9201", 10, connA: "none", connB: "none"),
                Cable("b", "rg58-belden-9201", 20, connA: "none", connB: "none"),
            ],
            [
                W("tdr", "P", "feed", "A"), W("feed", "B", "j", "P"),
                W("j", "P", "a", "A"), W("j", "P", "b", "A"),
            ],
            portConnectorId: "none"));
        Assert.Empty(tee.InterfaceNotes);

        var ideal = CompileClean(Doc("tek-1502c",
            [Tdr(), Cable("c1", "ideal-50", 30), Resistor("term", 50)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "term", "P")]));
        Assert.Empty(ideal.InterfaceNotes);
        Assert.Equal(3, ideal.Model!.Elements.Count); // source + cable + load only
    }

    [Fact]
    public void BalancedToCoaxIsRefusedWithAnAdvisoryIssue()
    {
        var compilation = DocumentCompiler.Compile(Doc("tek-1502c",
            [Tdr(), Cable("c1", "cat5e-belden-1583a", 30), Resistor("term", 100)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "term", "P")]), BuiltInCatalog);

        var issue = Assert.Single(compilation.Issues);
        Assert.Equal(DocumentIssueKind.UnadaptableInterface, issue.Kind);
        Assert.False(issue.IsFatal);
        Assert.Contains("balun", issue.Message);
        Assert.NotNull(compilation.Model); // joined ideally, still simulates
        Assert.Empty(compilation.InterfaceNotes);
    }

    [Fact]
    public void UnknownConnectorFamilyIdIsAFatalIssue()
    {
        var compilation = DocumentCompiler.Compile(Doc("tek-1502c",
            [Tdr(), Cable("c1", "rg58-belden-9201", 30, connA: "no-such-family")],
            [W("tdr", "P", "c1", "A")]), BuiltInCatalog);
        Assert.Contains(compilation.Issues,
            i => i.Kind == DocumentIssueKind.UnknownPresetId && i.Message.Contains("no-such-family"));
        Assert.True(compilation.HasFatalIssues);
    }

    // ---- physics ----

    [Fact]
    public void UhfJointShowsItsClassicThirtyFiveOhmDip()
    {
        // Two RG-213 runs (PL-259 ends) joined through a UHF barrel, swept with the
        // fast lab head (sma panel -> generic adapter -> uhf cable, then the barrel).
        // The mated UHF region is 35 ohm over 2 x 14 mm: at a 23 ps incident edge the
        // dip is fully resolved, so the trace must sag to ~35 ohm at the joint.
        var compilation = CompileClean(Doc("tek-80e04",
            [Tdr(), Cable("c1", "rg213-belden-8267", 2), Cable("c2", "rg213-belden-8267", 2), Resistor("term", 50)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "c2", "A"), W("c2", "B", "term", "P")]));
        var trace = TdrSimulator.Simulate(compilation.Model!, compilation.Instrument!);

        // Round trip through c1, plus up to ~0.6 ns for the panel-side hardware the
        // compiler spliced ahead of it (sma mate, generic adapter, uhf mate).
        var rg213 = BuiltInCatalog.Cables.Single(c => c.Id == "rg213-belden-8267");
        double tJoint = 2 * 2.0 / (rg213.VelocityFactor * C0);
        double minOhms = double.MaxValue;
        for (int k = 0; k < trace.TimeS.Length; k++)
        {
            if (trace.TimeS[k] > tJoint && trace.TimeS[k] < tJoint + 2e-9)
                minOhms = Math.Min(minOhms, trace.Ohms[k]);
        }

        Assert.InRange(minOhms, 30, 40);
    }

    [Fact]
    public void InterfacesNeverPerturbSettledDcLevels()
    {
        // All interface specs have zero DC resistance, so the Z0-mismatch divider
        // identity at DC must survive the inserted hardware exactly.
        var compilation = CompileClean(Doc("tek-1502c",
            [Tdr(), Cable("c1", "ideal-50", 30, connA: "bnc-50", connB: "bnc-50"), Resistor("term", 150)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "term", "P")]));
        Assert.Single(compilation.InterfaceNotes);
        var trace = TdrSimulator.Simulate(compilation.Model!, compilation.Instrument!);
        Assert.Equal(0.5, trace.DcReflection, 1e-9); // (150-50)/(150+50)
    }

    // ---- stability / serialization ----

    [Fact]
    public void InterfaceSpecsAreReferenceStableAcrossRecompiles()
    {
        var document = Doc("tek-1502c",
            [Tdr(), Cable("c1", "rg58-belden-9201", 30), Resistor("term", 50)],
            [W("tdr", "P", "c1", "A"), W("c1", "B", "term", "P")]);
        var first = CompileClean(document);
        var second = CompileClean(document);

        CableSpec MatedSpec(DocumentCompilation c) => ((CableSegment)c.Model!.Elements
            .Single(e => e is CableSegment s && s.Spec.Id == "iface-mated-bnc-50")).Spec;
        ConnectorSpec LumpSpec(DocumentCompilation c) => ((Connector)c.Model!.Elements
            .Single(e => e is Connector k && k.Spec.Id == "iface-excess-bnc-50")).Spec;

        Assert.Same(MatedSpec(first), MatedSpec(second));
        Assert.Same(LumpSpec(first), LumpSpec(second));
    }

    [Fact]
    public void ConnectorOverridesRoundTripThroughSchemaV3()
    {
        var document = Doc("tek-1502c",
            [Tdr(), Cable("c1", "rg58-belden-9201", 30, connA: "sma-50", connB: "none")],
            [W("tdr", "P", "c1", "A")],
            portConnectorId: "n-50");
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(document));

        Assert.Equal(3, back.SchemaVersion);
        Assert.Equal("n-50", back.Instrument.PortConnectorId);
        var cable = back.Nodes.Single(n => n.Id == "c1");
        Assert.Equal("sma-50", cable.ConnectorAId);
        Assert.Equal("none", cable.ConnectorBId);
    }

    // ---- catalog sanity ----

    [Fact]
    public void BuiltInFamiliesAndAdaptersMatchTheResearchedSpecs()
    {
        var families = BuiltInCatalog.ConnectorFamilies;
        Assert.Equal(
            ["bnc-50", "bnc-75", "f-75", "n-50", "sma-50", "mm185", "uhf", "rca", "rj45"],
            families.Select(f => f.Id).ToList());

        // The UHF connector is the textbook non-constant interface (~35 ohm);
        // precision mm connectors are nearly transparent air lines.
        Assert.Equal(35.0, families.Single(f => f.Id == "uhf").MatedZ0Ohms);
        Assert.Equal(50.0, families.Single(f => f.Id == "mm185").MatedZ0Ohms);
        Assert.True(families.Single(f => f.Id == "rj45").Balanced);
        Assert.True(families.Single(f => f.Id == "mm185").MaxFrequencyGhz
            > families.Single(f => f.Id == "sma-50").MaxFrequencyGhz);

        // Every instrument preset declares its real front-panel interface.
        Assert.Equal("sma-50", Instrument("tek-80e04").PortConnector);   // 3.5 mm female
        Assert.Equal("mm185", Instrument("tek-80e10b").PortConnector);   // 1.85 mm female
        Assert.Equal("bnc-50", Instrument("tek-1502c").PortConnector);
        Assert.Equal("f-75", Instrument("catv-75").PortConnector);
        Assert.Equal("rj45", Instrument("cable-tester").PortConnector);

        // Spot-check cable defaults: ham classics take PL-259, CATV takes F,
        // 93-ohm ARCnet genuinely shipped with 50-ohm BNCs.
        Assert.Equal("uhf", CableSpecOf("rg213-belden-8267").DefaultConnector);
        Assert.Equal("f-75", CableSpecOf("rg6-belden-1694a").DefaultConnector);
        Assert.Equal("bnc-50", CableSpecOf("rg62-au").DefaultConnector);
        Assert.Null(CableSpecOf("ideal-50").DefaultConnector);
        Assert.Null(CableSpecOf("twinlead-300").DefaultConnector);

        static InstrumentSpec Instrument(string id)
            => BuiltInCatalog.Instruments.Single(i => i.Id == id);
        static CableSpec CableSpecOf(string id)
            => BuiltInCatalog.Cables.Single(c => c.Id == id);
    }

    [Fact]
    public void SynthesizedSpecsDeriveConsistentLineConstants()
    {
        var uhf = BuiltInCatalog.ConnectorFamilies.Single(f => f.Id == "uhf");
        var spec = InterfaceSynthesis.MatedSpec(uhf);

        // C = 1/(Z0 * v): the derived capacitance must close the loop exactly.
        Assert.Equal(1e12 / (35.0 * 0.66 * C0), spec.CapacitancePfPerM, 1e-9);
        Assert.Equal(0, spec.RdcCenterOhmsPerKm);
        Assert.Equal(0, spec.RdcShieldOhmsPerKm);
        Assert.All(spec.Attenuation!.Points, p => Assert.True(p.Db > 0));

        // And it must build a clean engine model.
        var model = new SimulationSession().GetModel(spec);
        Assert.NotNull(model);
    }
}
