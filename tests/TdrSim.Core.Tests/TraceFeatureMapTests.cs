using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// M6 cross-highlighting groundwork: the compiler's element provenance (index-aligned
/// with the model) and the first-pass delay map built from it - spans for cables,
/// points for lumped parts, shared intervals for equally deep branches, the
/// meeting-point rule on rings, and nothing past the deepest first-pass reflection.
/// </summary>
public class TraceFeatureMapTests
{
    private const double Vf = 0.66;
    private static readonly double V = Vf * C0;

    private static IReadOnlyList<ElementOrigin> Origins(params string[] ids)
        => [.. ids.Select(id => new ElementOrigin(id, false))];

    private static ProjectDocument Example(string name)
        => ExampleProjects.All.Single(e => e.Name == name).Document;

    private static TraceFeatureMap MapOf(DocumentCompilation compilation)
        => TraceFeatureMap.Build(
            compilation.Model!, compilation.ElementOrigins,
            compilation.Probes.Select(p => (p.NodeId, p.Net)));

    // ---- provenance ----

    [Fact]
    public void OriginsAlignWithTheModelForEveryExample()
    {
        foreach (var example in ExampleProjects.All)
        {
            var compilation = DocumentCompiler.Compile(example.Document, BuiltInCatalog);
            Assert.NotNull(compilation.Model);
            Assert.Equal(compilation.Model.Elements.Count, compilation.ElementOrigins.Count);

            var ids = example.Document.Nodes.Select(n => n.Id).ToHashSet();
            Assert.All(compilation.ElementOrigins, o => Assert.Contains(o.NodeId, ids));
            Assert.IsType<SourcePort>(compilation.Model.Elements[0]);
            Assert.Equal("tdr", compilation.ElementOrigins[0].NodeId);
            Assert.False(compilation.ElementOrigins[0].IsInterface);
        }
    }

    [Fact]
    public void OriginsNameTheNodeEachElementCameFrom()
    {
        var compilation = DocumentCompiler.Compile(Example("Corroded splice"), BuiltInCatalog);
        var model = compilation.Model!;
        for (int i = 0; i < model.Elements.Count; i++)
        {
            var origin = compilation.ElementOrigins[i];
            if (origin.IsInterface)
                continue;
            string expectedKind = model.Elements[i] switch
            {
                SourcePort => "tdr",
                Connector => "splice",
                Load => "term",
                _ => "c",
            };
            Assert.StartsWith(expectedKind, origin.NodeId);
        }
    }

    [Fact]
    public void InterfaceHardwareIsFlaggedAndOwnedByACable()
    {
        // The 1502C's BNC panel connector meets RG-58's BNC plug: a mated pair.
        var compilation = DocumentCompiler.Compile(Example("Open-ended cable"), BuiltInCatalog);
        Assert.NotEmpty(compilation.InterfaceNotes);
        var flagged = compilation.ElementOrigins.Where(o => o.IsInterface).ToList();
        Assert.NotEmpty(flagged);
        Assert.All(flagged, o => Assert.Equal("c1", o.NodeId));
    }

    [Fact]
    public void IslandsLeaveNoOriginAndIdenticalParallelCablesKeepTheirOwn()
    {
        var document = new ProjectDocument
        {
            SchemaVersion = ProjectDocument.CurrentSchemaVersion,
            Instrument = new InstrumentDocument { PresetId = "tek-1502c", PortConnectorId = "none" },
            Nodes =
            [
                new DocumentNode { Id = "tdr", Kind = DocumentNodeKind.Tdr },
                Cable("p1"), Cable("p2"), Cable("island"),
                new DocumentNode { Id = "j", Kind = DocumentNodeKind.Junction },
            ],
            Wires =
            [
                Wire("tdr", "P", "p1", "A"), Wire("tdr", "P", "p2", "A"),
                Wire("p1", "B", "j", "P"), Wire("p2", "B", "j", "P"),
            ],
        };

        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.NotNull(compilation.Model);
        var ids = compilation.ElementOrigins.Select(o => o.NodeId).ToList();
        Assert.DoesNotContain("island", ids);
        Assert.Contains("p1", ids);
        Assert.Contains("p2", ids);

        // The two cables are equal records - the reason provenance is a list.
        var cables = compilation.Model.Elements.OfType<CableSegment>().ToList();
        Assert.Equal(2, cables.Count);
        Assert.Equal(cables[0], cables[1]);

        static DocumentNode Cable(string id) => new()
        {
            Id = id, Kind = DocumentNodeKind.Cable, CablePresetId = "rg58-belden-9201",
            LengthM = 10, ConnectorAId = "none", ConnectorBId = "none",
        };

        static DocumentWire Wire(string a, string pa, string b, string pb)
            => new() { FromNodeId = a, FromPort = pa, ToNodeId = b, ToPort = pb };
    }

    // ---- the map ----

    [Fact]
    public void ChainSpansTileTheRoundTripAndLumpsSitOnTheJoints()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Lossless(50, Vf), 10),
            new Load(N(1), LoadKind.Capacitor, CFarads: 2e-12),
            new CableSegment(N(1), N(2), Lossless(75, 0.8), 20),
            new Load(N(2), LoadKind.Resistor, ROhms: 50));
        var map = TraceFeatureMap.Build(model, Origins("tdr", "c1", "tap", "c2", "term"));

        double t1 = 2 * 10 / V;
        double t2 = t1 + 2 * 20 / (0.8 * C0);
        var c1 = map.For("c1")!;
        var c2 = map.For("c2")!;
        Assert.Equal(0, c1.StartS, 15);
        Assert.Equal(t1, c1.EndS, 15);
        Assert.Equal(t1, c2.StartS, 15);   // time, not metres: c2 has a different VF
        Assert.Equal(t2, c2.EndS, 15);
        Assert.Equal(t2, map.DeepestS, 15);

        Assert.True(map.For("tap")!.IsPoint);
        Assert.Equal(t1, map.For("tap")!.StartS, 15);
        Assert.Equal(t2, map.For("term")!.StartS, 15);
        Assert.Null(map.For("tdr"));

        Assert.Equal(["c1"], map.At(t1 / 2, 1e-10));
        Assert.Equal(["tap", "c1", "c2"], map.At(t1, 1e-10));
        Assert.Equal(["tap", "c2"], map.At(t1 + 5e-11, 1e-10));   // tolerance reaches back
        Assert.Equal(["tap", "c1"], map.At(t1 - 5e-11, 1e-10));
        Assert.Empty(map.At(t2 + 1e-9, 1e-10));
        Assert.Empty(map.At(-1e-9, 1e-10));
    }

    [Fact]
    public void EquallyDeepBranchesShareTheirInterval()
    {
        var compilation = DocumentCompiler.Compile(Example("Tee with open stubs"), BuiltInCatalog);
        var map = MapOf(compilation);

        var feed = map.For("feed")!;
        var stub = map.For("stub")!;
        var tail = map.For("tail")!;
        Assert.True(stub.StartS >= feed.EndS - 1e-12);
        Assert.Equal(stub.StartS, tail.StartS, 12);
        Assert.True(tail.EndS > stub.EndS);

        // Inside the stub's depth a TDR cannot tell the two branches apart.
        double inside = (stub.StartS + stub.EndS) / 2;
        var hits = map.At(inside, 1e-10);
        Assert.Contains("stub", hits);
        Assert.Contains("tail", hits);
        Assert.DoesNotContain("feed", hits);

        // Past the stub's end only the longer branch is still first-pass.
        Assert.Equal(["tail"], map.At((stub.EndS + tail.EndS) / 2, 1e-10));
    }

    [Fact]
    public void RingCablesStopAtTheMeetingPoint()
    {
        // 10 m and 20 m paths between the same two nets: the wave reaches the far
        // junction through the short one at 10 m, and the two fronts meet 15 m along
        // the long one.
        var line = Lossless(50, Vf);
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 30),
            new CableSegment(N(1), N(2), line, 10),
            new CableSegment(N(1), N(2), line with { Name = "long" }, 20));
        var map = TraceFeatureMap.Build(model, Origins("tdr", "feed", "top", "bottom"));

        double tFeed = 2 * 30 / V;
        var top = map.For("top")!;
        var bottom = map.For("bottom")!;
        Assert.Equal(tFeed, top.StartS, 15);
        Assert.Equal(tFeed + 2 * 10 / V, top.EndS, 15);
        Assert.Equal(tFeed, bottom.StartS, 15);
        Assert.Equal(tFeed + 2 * 15 / V, bottom.EndS, 15);
        Assert.Equal(bottom.EndS, map.DeepestS, 15);
    }

    [Fact]
    public void JunctionsArePlacedFromProbesAndInterfaceHardwareFoldsIntoItsOwner()
    {
        var ring = MapOf(DocumentCompiler.Compile(Example("Ring loop"), BuiltInCatalog));
        Assert.True(ring.For("j1")!.IsPoint);
        Assert.Equal(ring.For("top")!.StartS, ring.For("j1")!.StartS, 12);
        Assert.Null(ring.For("tdr"));

        // With a mated pair in front, the cable's span still starts at the port and
        // covers the pair: one feature per document node.
        var open = MapOf(DocumentCompiler.Compile(Example("Open-ended cable"), BuiltInCatalog));
        Assert.Single(open.Features, f => f.NodeId == "c1");
        var c1 = open.For("c1")!;
        Assert.Equal(0, c1.StartS, 15);
        Assert.True(c1.EndS > 2 * 50 / (0.66 * C0));
        Assert.Equal(c1.EndS, open.DeepestS, 12);
    }

    [Fact]
    public void OriginCountMustMatchTheModel()
    {
        var model = Network(new SourcePort(N(0)), new CableSegment(N(0), N(1), Lossless(50, Vf), 1));
        Assert.Throws<ArgumentException>(() => TraceFeatureMap.Build(model, Origins("tdr")));
    }
}
