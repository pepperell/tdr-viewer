using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// M6 bounce diagram: the textbook lossless lattice. Pinned to hand-computed rho/tau
/// products, tied to the engine (the running sum of port arrivals must be the
/// simulated lossless trace's plateaus), and to the M5 fault closed forms. Also
/// pinned: merging of coincident rays, pruning/truncation, the approximate flags, and
/// that automatically inserted connector hardware is present as real sections - no
/// ideal joints.
/// </summary>
public class BounceDiagramTests
{
    private const double Vf = 0.66;
    private static readonly double V = Vf * C0;
    private static readonly CableSpec Line50 = Lossless(50, Vf);

    private static BounceOptions Until(double maxTimeS) => new() { MaxTimeS = maxTimeS };

    private static NetworkModel Cascade() => Network(
        new SourcePort(N(0)),
        new CableSegment(N(0), N(1), Line50, 10),
        new CableSegment(N(1), N(2), Lossless(75, Vf), 10),
        new Load(N(2), LoadKind.Resistor, ROhms: 50));

    [Fact]
    public void TwoSegmentMismatchHasTheHandComputedArrivalsAndFactors()
    {
        double tau = 10 / V;
        var diagram = BounceDiagram.Build(Cascade(), null, 50, Until(7 * tau))!;

        Assert.Equal(2, diagram.Sections.Count);
        Assert.Equal(3, diagram.Interfaces.Count);
        Assert.False(diagram.IsApproximate);
        Assert.False(diagram.Truncated);

        double rho1 = (75.0 - 50) / (75 + 50);
        double rho2 = (50.0 - 75) / (50 + 75);
        var i1 = diagram.Interfaces[1];
        Assert.Equal(rho1, i1.RhoFromLeft, 12);
        Assert.Equal(1 + rho1, i1.TauFromLeft, 12);
        Assert.Equal(-rho1, i1.RhoFromRight, 12);
        Assert.Equal(1 - rho1, i1.TauFromRight, 12);
        Assert.Equal(rho2, diagram.Interfaces[2].RhoFromLeft, 12);
        Assert.True(diagram.Interfaces[2].IsTerminal);
        Assert.Equal(10, i1.PositionM, 12);
        Assert.Equal(tau, i1.DelayS, 15);

        // Matched port: nothing at t = 0, then the classic ladder.
        var arrivals = diagram.PortArrivals;
        Assert.Equal(2 * tau, arrivals[0].TimeS, 15);
        Assert.Equal(rho1, arrivals[0].Amplitude, 12);
        Assert.Equal(4 * tau, arrivals[1].TimeS, 15);
        Assert.Equal((1 + rho1) * rho2 * (1 - rho1), arrivals[1].Amplitude, 12);
        Assert.Equal(6 * tau, arrivals[2].TimeS, 15);
        Assert.Equal((1 + rho1) * rho2 * -rho1 * rho2 * (1 - rho1), arrivals[2].Amplitude, 12);

        Assert.Equal("τ0 ρ1 τ0′", string.Join(" ", arrivals[0].Factors!));
        Assert.Equal("τ0 τ1 ρ2 τ1′ τ0′", string.Join(" ", arrivals[1].Factors!));
        double product = arrivals[1].Factors!.Aggregate(1.0, (p, f) => p * f.Value);
        Assert.Equal(arrivals[1].Amplitude, product, 12);

        // First ray: the incident step crossing the first section, unchanged.
        var first = diagram.Rays[0];
        Assert.Equal((0, 1), (first.FromInterface, first.ToInterface));
        Assert.Equal(0, first.StartS, 15);
        Assert.Equal(tau, first.EndS, 15);
        Assert.Equal(1, first.Amplitude, 12);
    }

    [Fact]
    public void StaircaseMatchesTheSimulatedLosslessPlateausAndConvergesToDc()
    {
        var model = Cascade();
        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        double tau = 10 / V;
        var diagram = BounceDiagram.Build(model, null, 50, Until(trace.TimeS[^1]))!;

        for (int step = 1; step <= 4; step++)
        {
            double from = 2 * step * tau + 15e-9;
            double to = 2 * (step + 1) * tau - 15e-9;
            Assert.Equal(PlateauMean(trace, from, to), diagram.StaircaseAt((from + to) / 2), 2e-3);
        }

        Assert.Equal(trace.DcReflection, diagram.PortArrivals.Sum(a => a.Amplitude), 5e-3);
    }

    [Fact]
    public void MidLineFaultsReproduceTheM5ClosedForms()
    {
        const double r = 470, rs = 10;
        var bridge = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Load(N(1), LoadKind.Resistor, ROhms: r),
            new CableSegment(N(1), N(2), Line50, 10),
            new Load(N(2), LoadKind.Resistor, ROhms: 50));
        var bridged = BounceDiagram.Build(bridge, null, 50, Until(1e-6))!;
        Assert.Equal(-50 / (2 * r + 50), bridged.PortArrivals[0].Amplitude, 12);
        Assert.Equal(20 / V, bridged.PortArrivals[0].TimeS, 15);

        var series = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Connector(N(1), N(2), FaultSynthesis.SeriesConnectorSpec(rs)),
            new CableSegment(N(2), N(3), Line50, 10),
            new Load(N(3), LoadKind.Resistor, ROhms: 50));
        var faulted = BounceDiagram.Build(series, null, 50, Until(1e-6))!;
        Assert.Equal(rs / (rs + 2 * 50), faulted.PortArrivals[0].Amplitude, 12);
        Assert.False(faulted.IsApproximate);

        // And the lattice total agrees with the engine's exact DC level.
        var trace = TdrSimulator.Simulate(series, Tdr1502C);
        Assert.Equal(trace.DcReflection, faulted.PortArrivals.Sum(a => a.Amplitude), 5e-3);
    }

    [Fact]
    public void MismatchedInstrumentReflectsAtThePortItself()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Load(N(1), LoadKind.Resistor, ROhms: 50));
        var diagram = BounceDiagram.Build(model, null, 75, Until(1e-6))!;

        Assert.Equal(0, diagram.PortArrivals[0].TimeS, 15);
        Assert.Equal((50.0 - 75) / (50 + 75), diagram.PortArrivals[0].Amplitude, 12);
        Assert.Single(diagram.PortArrivals);   // matched far end: nothing else returns
    }

    [Fact]
    public void CoincidentRaysMergeInsteadOfDoubling()
    {
        // Eight equal-delay sections: unmerged, the path count doubles every bounce;
        // merged, the lattice has at most two rays (one each way) per section per step.
        var elements = new List<Element> { new SourcePort(N(0)) };
        double[] z = [50, 75, 40, 90, 55, 30, 70, 45];
        for (int i = 0; i < z.Length; i++)
            elements.Add(new CableSegment(N(i), N(i + 1), Lossless(z[i], Vf), 5));
        var model = new NetworkModel { Elements = elements };

        double tau = 5 / V;
        const int steps = 40;
        var diagram = BounceDiagram.Build(
            model, null, 50,
            new BounceOptions { MaxTimeS = (steps - 0.5) * tau, MinAmplitude = 0, MaxRays = 100_000 })!;

        Assert.False(diagram.Truncated);
        Assert.True(diagram.Rays.Count <= 2 * z.Length * steps,
            $"{diagram.Rays.Count} rays - merging is not collapsing the lattice.");
        Assert.Contains(diagram.Rays, ray => ray.PathCount > 100 && ray.Factors is null);

        // Open far end, lossless: everything eventually comes back.
        var full = BounceDiagram.Build(
            model, null, 50,
            new BounceOptions { MaxTimeS = 4000 * tau, MinAmplitude = 1e-9, MaxRays = 200_000 })!;
        Assert.Equal(1.0, full.PortArrivals.Sum(a => a.Amplitude), 1e-4);
    }

    [Fact]
    public void MergedArrivalsEqualTheUnmergedEnumeration()
    {
        // Slightly detuned lengths defeat merging without changing the physics: the
        // same staircase must come out either way.
        NetworkModel Build(double second) => Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 5),
            new CableSegment(N(1), N(2), Lossless(75, Vf), second),
            new CableSegment(N(2), N(3), Lossless(30, Vf), 5),
            new Load(N(3), LoadKind.Resistor, ROhms: 100));
        double tau = 5 / V;
        var options = new BounceOptions { MaxTimeS = 20.5 * tau, MinAmplitude = 0, MaxRays = 100_000 };
        var merged = BounceDiagram.Build(Build(5), null, 50, options)!;
        var loose = BounceDiagram.Build(Build(5.0001), null, 50, options)!;

        Assert.True(loose.Rays.Count > 2 * merged.Rays.Count);
        for (int step = 1; step <= 9; step++)
        {
            double t = (2 * step + 1) * tau;
            Assert.Equal(loose.StaircaseAt(t), merged.StaircaseAt(t), 9);
        }
    }

    [Fact]
    public void FlagsReportApproximationTruncationAndNonChains()
    {
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var lossy = Network(new SourcePort(N(0)), new CableSegment(N(0), N(1), rg58, 10));
        Assert.True(BounceDiagram.Build(lossy, null, 50, Until(1e-6))!.IsApproximate);

        var capacitive = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Load(N(1), LoadKind.Capacitor, CFarads: 100e-12));
        var cap = BounceDiagram.Build(capacitive, null, 50, Until(1e-6))!;
        Assert.True(cap.IsApproximate);
        Assert.True(cap.Interfaces[^1].IsReactiveApprox);
        Assert.Equal(1, cap.Interfaces[^1].RhoFromLeft, 12);   // open at DC

        var shorted = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Load(N(1), LoadKind.Short));
        Assert.Equal(-1, BounceDiagram.Build(shorted, null, 50, Until(1e-6))!.Interfaces[^1].RhoFromLeft, 9);

        var capped = BounceDiagram.Build(
            Cascade(), null, 50, new BounceOptions { MaxTimeS = 1e-3, MinAmplitude = 0, MaxRays = 12 })!;
        Assert.True(capped.Truncated);
        Assert.Equal(12, capped.Rays.Count);

        var tee = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new CableSegment(N(1), N(2), Line50, 10),
            new CableSegment(N(1), N(3), Line50, 10));
        Assert.Null(BounceDiagram.Build(tee, null, 50, Until(1e-6)));
    }

    [Fact]
    public void ConnectorHardwareAppearsAsRealSectionsNeverAsIdealJoints()
    {
        // "Impedance staircase": panel BNC -> RG-58 -> RG-6 -> RG-213, i.e. a mated
        // pair at the port and adapters/barrels between the cables.
        var document = ExampleProjects.All.Single(e => e.Name == "Impedance staircase").Document;
        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.NotEmpty(compilation.InterfaceNotes);
        var model = compilation.Model!;

        var trace = TdrSimulator.Simulate(model, compilation.Instrument!);
        var diagram = BounceDiagram.Build(
            model, compilation.ElementOrigins, compilation.Instrument!.SourceImpedanceOhms,
            Until(trace.TimeS[^1] / 3))!;

        // One section per cable element of the model, interface hardware included.
        int cableElements = model.Elements.OfType<CableSegment>().Count();
        int interfaceCables = model.Elements
            .Select((e, i) => (e, compilation.ElementOrigins[i]))
            .Count(p => p.e is CableSegment && p.Item2.IsInterface);
        Assert.True(interfaceCables > 0);
        Assert.Equal(cableElements, diagram.Sections.Count);
        Assert.Equal(interfaceCables, diagram.Sections.Count(s => s.IsInterface));
        Assert.All(diagram.Sections.Where(s => s.IsInterface), s =>
        {
            Assert.InRange(s.LengthM, 1e-4, 0.2);
            Assert.True(s.DelayS > 0);
        });

        // The mated pair's own lump is a (reactive-flagged) plane with owners.
        Assert.Contains(diagram.Interfaces, i => i.IsReactiveApprox);
        Assert.All(diagram.Interfaces, i => Assert.NotEmpty(i.NodeIds));
        Assert.True(diagram.IsApproximate);

        // Incommensurate mm-scale delays cannot merge; the amplitude floor keeps the
        // lattice finite and far from the cap.
        Assert.False(diagram.Truncated);
        Assert.True(diagram.Rays.Count < 1500, $"{diagram.Rays.Count} rays for a connectorized chain.");
    }

    [Fact]
    public void MatedPairSectionReflectsWithItsOwnImpedanceStep()
    {
        // A UHF joint sags to ~35 ohm through the mated region: the lattice must show
        // that step at the joint, not a transparent connection.
        var uhf = BuiltInCatalog.ConnectorFamilies.Single(f => f.Id == "uhf");
        var document = new ProjectDocument
        {
            SchemaVersion = ProjectDocument.CurrentSchemaVersion,
            Instrument = new InstrumentDocument { PresetId = "tek-1502c", PortConnectorId = "none" },
            Nodes =
            [
                new DocumentNode { Id = "tdr", Kind = DocumentNodeKind.Tdr },
                Cable("c1", connB: "uhf"), Cable("c2", connA: "uhf"),
            ],
            Wires =
            [
                new DocumentWire { FromNodeId = "tdr", FromPort = "P", ToNodeId = "c1", ToPort = "A" },
                new DocumentWire { FromNodeId = "c1", FromPort = "B", ToNodeId = "c2", ToPort = "A" },
            ],
        };
        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.False(compilation.HasFatalIssues);
        var diagram = BounceDiagram.Build(compilation.Model!, compilation.ElementOrigins, 50, Until(1e-6))!;

        var rg213 = BuiltInCatalog.Cables.Single(c => c.Id == "rg213-belden-8267");
        var mated = diagram.Sections.First(s => s.IsInterface);
        Assert.Equal(uhf.MatedZ0Ohms, mated.Z0Ohms, 6);
        Assert.Equal("c2", mated.NodeId);

        double expected = (uhf.MatedZ0Ohms - rg213.Z0Ohms) / (uhf.MatedZ0Ohms + rg213.Z0Ohms);
        double tJoint = 2 * 10 / (rg213.VelocityFactor * C0);
        var firstFromJoint = diagram.PortArrivals.First(a => a.TimeS >= tJoint - 1e-12);
        Assert.Equal(tJoint, firstFromJoint.TimeS, 12);
        Assert.Equal(expected, firstFromJoint.Amplitude, 3);
        Assert.True(expected < -0.1, "A UHF mated pair is a clearly visible dip.");

        static DocumentNode Cable(string id, string connA = "none", string connB = "none") => new()
        {
            Id = id, Kind = DocumentNodeKind.Cable, CablePresetId = "rg213-belden-8267",
            LengthM = 10, ConnectorAId = connA, ConnectorBId = connB,
        };
    }
}
