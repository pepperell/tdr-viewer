using System.Numerics;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// Structural cross-checks for the M2 nodal solver (plans/M2.md step 6): solver
/// equivalence, exact-short folding, DC limits, dispatch, and the Settled flag.
/// </summary>
public class NodalTests
{
    /// <summary>DC plus a dense sweep to ~2 GHz: DC limits, small-|gl|, and deep skin loss.</summary>
    private static double[] TestFrequencies()
    {
        var freqs = new double[1025];
        for (int n = 1; n < freqs.Length; n++)
            freqs[n] = n * 2e6;
        return freqs;
    }

    [Fact]
    public void NodalMatchesChainSolverOnLossyChainWithMidShunts()
    {
        // The strongest net in the suite: a real lossy RG-58 chain with a connector and
        // mid-chain shunt loads, solved by the M1 fold and by the nodal assembly. Skin,
        // dielectric, and DC code paths all disagree loudly if any stamp is wrong.
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var splice = BuiltInCatalog.Connectors.Single(c => c.Id == "splice-corroded");
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 10),
            new CableSegment(N(1), N(2), rg58, 15),
            new Connector(N(2), N(3), splice),
            new CableSegment(N(3), N(4), rg58, 5),
            new CableSegment(N(4), N(5), rg58, 20),
            new Load(N(2), LoadKind.Resistor, ROhms: 200),
            new Load(N(4), LoadKind.Capacitor, CFarads: 10e-12),
            new Load(N(5), LoadKind.Resistor, ROhms: 75));

        var freqs = TestFrequencies();
        var viaChain = ChainSolver.Solve(
            ChainExtractor.Extract(model), 50, freqs, CableEvalOptions.Default);
        var viaNodal = NodalSolver.Solve(
            NetworkCompiler.Compile(model), 50, freqs, CableEvalOptions.Default);

        double worst = 0;
        for (int n = 0; n < freqs.Length; n++)
            worst = Math.Max(worst, (viaChain[n] - viaNodal[n]).Magnitude);
        Assert.True(worst < 1e-9, $"Solvers disagree by {worst:e2}.");
    }

    [Fact]
    public void MidChainShortHidesEverythingBeyondIt()
    {
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var full = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 10),
            new CableSegment(N(1), N(2), rg58, 10),
            new Load(N(2), LoadKind.Short),
            new CableSegment(N(2), N(3), rg58, 10),
            new Load(N(3), LoadKind.Resistor, ROhms: 75));
        var truncated = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 10),
            new CableSegment(N(1), N(2), rg58, 10),
            new Load(N(2), LoadKind.Short));

        var freqs = TestFrequencies();
        var gFull = NodalSolver.Solve(NetworkCompiler.Compile(full), 50, freqs, CableEvalOptions.Default);
        var gTrunc = NodalSolver.Solve(NetworkCompiler.Compile(truncated), 50, freqs, CableEvalOptions.Default);

        for (int n = 0; n < freqs.Length; n++)
            Assert.True((gFull[n] - gTrunc[n]).Magnitude < 1e-12,
                $"Grounded node leaks at {freqs[n]:e2} Hz: diff {(gFull[n] - gTrunc[n]).Magnitude:e2}.");
    }

    [Fact]
    public void ShortAtThePortPinsGammaAtMinusOne()
    {
        var line = Lossless(50, 0.66);
        var model = Network(
            new SourcePort(N(0)),
            new Load(N(0), LoadKind.Short),
            new CableSegment(N(0), N(1), line, 30),
            new CableSegment(N(1), N(2), line, 10),
            new CableSegment(N(1), N(3), line, 10));

        var gamma = NodalSolver.Solve(
            NetworkCompiler.Compile(model), 50, TestFrequencies(), CableEvalOptions.Default);

        foreach (var g in gamma)
            Assert.Equal(new Complex(-1, 0), g); // exact early-out, no solve involved
    }

    [Fact]
    public void AllOpenNetworkSettlesToPlusOneAtDc()
    {
        // Tee of open stubs: at DC every lossless segment is an ideal short, the whole
        // network collapses to one floating node, and only gMin bounds Z_in - so
        // Gamma(0) must be 1 - ~1e-10.
        var line = Lossless(50, 0.66);
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 30),
            new CableSegment(N(1), N(2), line, 10),
            new CableSegment(N(1), N(3), line, 20));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);
        Assert.Equal(SolverPath.Nodal, trace.Solver);
        Assert.InRange(trace.DcReflection, 1 - 1e-9, 1);
    }

    [Fact]
    public void LoadOnlyNetworkMatchesTheAnalyticReflection()
    {
        var model = Network(
            new SourcePort(N(0)),
            new Load(N(0), LoadKind.Resistor, ROhms: 75));

        var gamma = NodalSolver.Solve(
            NetworkCompiler.Compile(model), 50, TestFrequencies(), CableEvalOptions.Default);

        foreach (var g in gamma)
            Assert.True((g - new Complex(0.2, 0)).Magnitude < 1e-9);
    }

    [Fact]
    public void DispatchRoutesChainsToChainSolverAndBranchesToNodal()
    {
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var chain = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 30));
        var tee = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 30),
            new CableSegment(N(1), N(2), rg58, 10),
            new CableSegment(N(1), N(3), rg58, 10));

        Assert.Equal(SolverPath.Chain, TdrSimulator.Simulate(chain, Tdr1502C).Solver);
        Assert.Equal(SolverPath.Nodal, TdrSimulator.Simulate(tee, Tdr1502C).Solver);
    }

    [Fact]
    public void RingStillEchoingPastTheWindowIsFlaggedUnsettled()
    {
        // The case-13 ring with a window override that cuts off its echo train: the
        // k = 2 lap event (step of 8/27) lands inside the final tenth of the window,
        // so the pipeline must report Settled = false rather than silently truncating.
        var line = Lossless(50, 0.66);
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 30),
            new CableSegment(N(1), N(2), line, 10),
            new CableSegment(N(1), N(2), line, 20));

        var trace = TdrSimulator.Simulate(
            model, Tdr1502C, new SimulationSettings { WindowOverrideS = 500e-9 });

        AssertHermitian(trace);
        Assert.False(trace.Settled);
    }

    [Fact]
    public void CompilerRejectsInvalidNetworks()
    {
        var line = Lossless(50, 0.66);

        // Disconnected two-port (valid chain rules carried over from M1).
        Assert.Throws<ArgumentException>(() => NetworkCompiler.Compile(Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 10),
            new CableSegment(N(2), N(3), line, 10))));

        // Disconnected load.
        Assert.Throws<ArgumentException>(() => NetworkCompiler.Compile(Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 10),
            new Load(N(5), LoadKind.Open))));

        // Self-loop and non-positive length.
        Assert.Throws<ArgumentException>(() => NetworkCompiler.Compile(Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(0), line, 10))));
        Assert.Throws<ArgumentException>(() => NetworkCompiler.Compile(Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 0))));

        // TryExtract: null on branches (nodal territory), but invalid networks still throw.
        Assert.Null(ChainExtractor.TryExtract(Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, 10),
            new CableSegment(N(1), N(2), line, 10),
            new CableSegment(N(1), N(3), line, 10))));
        Assert.Throws<ArgumentException>(() => ChainExtractor.TryExtract(Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), line, -5))));
    }
}
