using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

public class ChainExtractorTests
{
    private static readonly Catalog.CableSpec Line50 = Lossless(50, 0.66);

    [Fact]
    public void SimpleChainIsOrderedSourceToFar()
    {
        var splice = BuiltInCatalog.Connectors.Single(c => c.Id == "splice-clean");
        var midLoad = new Load(N(1), LoadKind.Capacitor, CFarads: 2e-12);
        var model = Network(
            new Load(N(3), LoadKind.Resistor, ROhms: 50),
            new Connector(N(2), N(3), splice),
            new CableSegment(N(2), N(1), Line50, 20), // deliberately reversed endpoints
            new SourcePort(N(0)),
            midLoad,
            new CableSegment(N(0), N(1), Line50, 30));

        var chain = ChainExtractor.Extract(model);

        Assert.Equal(3, chain.Links.Count);
        Assert.Equal(30, Assert.IsType<CableSegment>(chain.Links[0].TwoPort).LengthM);
        Assert.Equal(20, Assert.IsType<CableSegment>(chain.Links[1].TwoPort).LengthM);
        Assert.IsType<Connector>(chain.Links[2].TwoPort);

        Assert.Empty(chain.Links[0].NearLoads);
        Assert.Equal([midLoad], chain.Links[1].NearLoads);
        Assert.Equal(LoadKind.Resistor, Assert.Single(chain.TerminalLoads).Kind);

        Assert.Equal(50.0 / (0.66 * C0), chain.OneWayDelayS, 1e-18);
        Assert.Equal(0.66, chain.FirstSegmentVelocityFactor);
    }

    [Fact]
    public void LoadOnlyNetworkIsAnEmptyChain()
    {
        var model = Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 100));
        var chain = ChainExtractor.Extract(model);
        Assert.Empty(chain.Links);
        Assert.Single(chain.TerminalLoads);
        Assert.Equal(0, chain.OneWayDelayS);
        Assert.Null(chain.FirstSegmentVelocityFactor);
    }

    [Fact]
    public void BranchNodeIsRejectedWithClearMessage()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new CableSegment(N(1), N(2), Line50, 10),
            new CableSegment(N(1), N(3), Line50, 10));

        var ex = Assert.Throws<NotSupportedException>(() => ChainExtractor.Extract(model));
        Assert.Contains("M2", ex.Message);
        Assert.Contains("n1", ex.Message);
    }

    [Fact]
    public void LoopIsRejected()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new CableSegment(N(1), N(2), Line50, 10),
            new CableSegment(N(2), N(0), Line50, 10));

        Assert.Throws<NotSupportedException>(() => ChainExtractor.Extract(model));
    }

    [Fact]
    public void DisconnectedTwoPortIsRejected()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new CableSegment(N(5), N(6), Line50, 10));

        var ex = Assert.Throws<NotSupportedException>(() => ChainExtractor.Extract(model));
        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public void DisconnectedLoadIsRejected()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Load(N(9), LoadKind.Short));

        Assert.Throws<ArgumentException>(() => ChainExtractor.Extract(model));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void WrongSourcePortCountIsRejected(int count)
    {
        var elements = new List<Element> { new CableSegment(N(0), N(1), Line50, 10) };
        for (int i = 0; i < count; i++)
            elements.Add(new SourcePort(N(0)));

        Assert.Throws<ArgumentException>(() => ChainExtractor.Extract(new() { Elements = elements }));
    }

    [Fact]
    public void NonPositiveCableLengthIsRejected()
    {
        var model = Network(new SourcePort(N(0)), new CableSegment(N(0), N(1), Line50, 0));
        Assert.Throws<ArgumentException>(() => ChainExtractor.Extract(model));
    }

    [Fact]
    public void InvalidLoadParametersAreRejected()
    {
        var model = Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Capacitor));
        Assert.Throws<ArgumentException>(() => ChainExtractor.Extract(model));
    }
}
