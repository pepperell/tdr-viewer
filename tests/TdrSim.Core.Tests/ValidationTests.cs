using System.Diagnostics;
using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using Xunit.Abstractions;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// The physics validation suite: every case asserts the simulated trace against a
/// closed-form ground truth (PLAN.md section 5 / plans/M1.md step 6). Case numbers in
/// test names refer to that table. Case 12 (Hermitian self-check) runs inside every
/// test via AssertHermitian.
/// </summary>
public class ValidationTests(ITestOutputHelper output)
{
    private const double Vf = 0.66;
    private static readonly double V = Vf * C0; // 1.9786e8 m/s

    private static readonly CableSpec Line50 = Lossless(50, Vf);

    // ---- Case 1: matched system is flat and settles to the DC divider ----

    [Fact]
    public void Case1MatchedLosslessSystemIsFlat()
    {
        var instrument = new InstrumentSettings
        {
            StepAmplitudeVolts = 0.3,
            RiseTimeS = 200e-12,
            SourceImpedanceOhms = 52,
        };
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Lossless(52, Vf), 30),
            new Load(N(1), LoadKind.Resistor, ROhms: 52));

        var trace = TdrSimulator.Simulate(model, instrument);
        AssertHermitian(trace);
        Assert.True(trace.Settled);

        Assert.True(MaxAbsRho(trace, trace.TimeS[0], trace.TimeS[^1]) < 2e-3);
        Assert.Equal(0.3, trace.Volts[^1], 0.0005);
    }

    [Fact]
    public void Case1RealRg58SettlesToDcDividerIncludingLoopResistance()
    {
        // 30 m of real RG-58 (52 ohm, 56.8 ohm/km loop DCR) into 52 ohm from a 50 ohm
        // instrument: V_inf = 2A * (R_dc*l + R_L) / (Zref + R_dc*l + R_L).
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 30),
            new Load(N(1), LoadKind.Resistor, ROhms: 52));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);

        double rLoop = (33.8 + 23.0) / 1000 * 30;
        double vInf = 2 * 0.3 * (rLoop + 52) / (50 + rLoop + 52);
        Assert.Equal(vInf, trace.Volts[^1], 0.0005);
    }

    // ---- Case 2: open and short echo timing and plateau ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Case2OpenAndShortEchoTimingAndPlateau(bool open)
    {
        var elements = new List<Element>
        {
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 30),
        };
        if (!open)
            elements.Add(new Load(N(1), LoadKind.Short));

        var trace = TdrSimulator.Simulate(new NetworkModel { Elements = elements }, Tdr1502C);
        AssertHermitian(trace);
        Assert.True(trace.Settled);

        double tEcho = 60 / V;
        double level = open ? 0.5 : -0.5;
        double crossing = CrossingTime(trace, level, tEcho - 3e-9, tEcho + 3e-9);
        Assert.True(Math.Abs(crossing - tEcho) <= 2 * trace.Grid.DtS,
            $"Echo at {crossing:e4}, expected {tEcho:e4} (dt {trace.Grid.DtS:e2}).");

        double plateau = PlateauMean(trace, tEcho + 20e-9, tEcho + 100e-9);
        Assert.Equal(open ? 1.0 : -1.0, plateau, 2e-3);
        Assert.Equal(open ? 1.0 : -1.0, trace.DcReflection, 1e-12);
    }

    // ---- Case 3: resistive mismatch plateaus ----

    [Theory]
    [InlineData(25)]
    [InlineData(75)]
    [InlineData(100)]
    public void Case3ResistiveMismatchPlateau(double rOhms)
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 30),
            new Load(N(1), LoadKind.Resistor, ROhms: rOhms));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);

        double tEcho = 60 / V;
        double expected = (rOhms - 50) / (rOhms + 50);
        Assert.Equal(expected, PlateauMean(trace, tEcho + 20e-9, tEcho + 100e-9), 1e-3);
    }

    // ---- Cases 4/5: reactive terminations expose their time constants ----

    [Fact]
    public void Case4CapacitiveLoadTimeConstant()
    {
        // 100 pF on a 50 ohm line: rho = 1 - 2*exp(-t'/tau), tau = Z0*C = 5 ns.
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Load(N(1), LoadKind.Capacitor, CFarads: 100e-12));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);

        double tEcho = 20 / V;
        double tau = FitExponentialTau(trace, tEcho, from: 1e-9, to: 8e-9, settlesTo: +1);
        Assert.Equal(50 * 100e-12, tau, 50 * 100e-12 * 0.02);

        // Initial dip toward -1 (capacitor starts as a short), final plateau at +1 (open at DC).
        Assert.True(MaxAbsRho(trace, tEcho - 1e-9, tEcho + 1e-9) > 0.5);
        Assert.True(trace.Rho[FindIndex(trace, tEcho + 0.3e-9)] < 0);
        Assert.Equal(1.0, PlateauMean(trace, tEcho + 50e-9, tEcho + 150e-9), 2e-3);
    }

    [Fact]
    public void Case5InductiveLoadTimeConstant()
    {
        // 1 uH on a 50 ohm line: rho = -1 + 2*exp(-t'/tau), tau = L/Z0 = 20 ns.
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new Load(N(1), LoadKind.Inductor, LHenries: 1e-6));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);

        double tEcho = 20 / V;
        double tau = FitExponentialTau(trace, tEcho, from: 2e-9, to: 30e-9, settlesTo: -1);
        Assert.Equal(1e-6 / 50, tau, 1e-6 / 50 * 0.02);

        Assert.True(trace.Rho[FindIndex(trace, tEcho + 0.3e-9)] > 0, "Inductor must first reflect like an open.");
        Assert.Equal(-1.0, PlateauMean(trace, tEcho + 300e-9, tEcho + 450e-9), 2e-3);
    }

    // ---- Cases 6/7: mid-line parasitics obey the area rules ----

    [Fact]
    public void Case6ShuntCapacitorDipAreaIsRiseTimeInvariant()
    {
        // 2 pF shunt C mid-line, matched beyond: dip area = -Z0*C/2, invariant to rise time.
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 15),
            new Load(N(1), LoadKind.Capacitor, CFarads: 2e-12),
            new CableSegment(N(1), N(2), Line50, 15),
            new Load(N(2), LoadKind.Resistor, ROhms: 50));

        double expected = -50 * 2e-12 / 2;
        double tDip = 30 / V;

        double AreaAt(double riseTimeS)
        {
            var instrument = Tdr1502C with { RiseTimeS = riseTimeS };
            var trace = TdrSimulator.Simulate(model, instrument);
            AssertHermitian(trace);
            return IntegrateRho(trace, tDip - 5e-9, tDip + 5e-9);
        }

        double area200 = AreaAt(200e-12);
        double area400 = AreaAt(400e-12);
        Assert.Equal(expected, area200, Math.Abs(expected) * 0.02);
        Assert.Equal(expected, area400, Math.Abs(expected) * 0.02);
        Assert.Equal(area200, area400, Math.Abs(expected) * 0.02);
    }

    [Fact]
    public void Case7SeriesInductorBumpArea()
    {
        // 10 nH series L mid-line (modeled as a connector): bump area = +L/(2*Z0).
        var seriesL = new ConnectorSpec
        {
            Id = "test-series-l",
            Name = "10 nH series inductance",
            SeriesResistanceOhms = 0,
            SeriesInductanceNh = 10,
            ShuntCapacitanceNearPf = 0,
            ShuntCapacitanceFarPf = 0,
        };
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 15),
            new Connector(N(1), N(2), seriesL),
            new CableSegment(N(2), N(3), Line50, 15),
            new Load(N(3), LoadKind.Resistor, ROhms: 50));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);

        double tBump = 30 / V;
        double expected = 10e-9 / (2 * 50);
        double area = IntegrateRho(trace, tBump - 5e-9, tBump + 5e-9);
        Assert.Equal(expected, area, expected * 0.02);
        Assert.True(MaxAbsRho(trace, tBump - 1e-9, tBump + 1e-9) > 0.01, "Bump must be positive and visible.");
    }

    // ---- Case 8: impedance cascade matches the lattice diagram ----

    [Fact]
    public void Case8CascadePlateausMatchLatticePartialSums()
    {
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 10),
            new CableSegment(N(1), N(2), Lossless(75, Vf), 10),
            new Load(N(2), LoadKind.Resistor, ROhms: 50));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);

        double t1 = 20 / V;
        double rho1 = (75.0 - 50) / (75 + 50);
        double rho2 = (50.0 - 75) / (50 + 75);
        double p2 = rho1 + (1 - rho1 * rho1) * rho2;
        double p3 = p2 + (1 - rho1 * rho1) * rho2 * -rho1 * rho2;

        Assert.Equal(rho1, PlateauMean(trace, t1 + 15e-9, 2 * t1 - 15e-9), 2e-3);
        Assert.Equal(p2, PlateauMean(trace, 2 * t1 + 15e-9, 3 * t1 - 15e-9), 2e-3);
        Assert.Equal(p3, PlateauMean(trace, 3 * t1 + 15e-9, 4 * t1 - 15e-9), 2e-3);
    }

    // ---- Case 10: skin-effect edge shape (Wigington-Nahman) ----

    [Fact]
    public void Case10LossyOpenEdgeFollowsErfcShape()
    {
        // Pure skin-effect line, reflected path 2l = 600 m: the reflected edge is
        // erfc(sqrt(tau0/t')/2) with tau0 = (k1'*2l)^2/pi, t' from the ideal arrival
        // 2l/v. Launch-mismatch creep (Z0(f) != 50) is physical and sits in the 2 %
        // budget after baseline subtraction at t'=0.
        const double k1 = 0.4;
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), SkinOnly(50, Vf, k1), 300));

        var trace = TdrSimulator.Simulate(model, Tdr1502C, new SimulationSettings { MaxLog2M = 20 });
        AssertHermitian(trace);
        Assert.False(trace.Grid.Compromised);

        double k1Np = k1 * ComplexMath.NepersPerDb / 100 / 1000;
        double tau0 = Math.Pow(k1Np * 600, 2) / Math.PI;
        double tArr = 600 / V;
        double baseline = RhoAt(trace, tArr);

        for (double tp = 0.1 * tau0; tp <= 40 * tau0; tp *= 1.25)
        {
            double expected = Erfc(0.5 * Math.Sqrt(tau0 / tp));
            double actual = RhoAt(trace, tArr + tp) - baseline;
            Assert.True(Math.Abs(actual - expected) < 0.02,
                $"erfc mismatch at t'={tp / tau0:f2}*tau0: expected {expected:f4}, got {actual:f4}.");
        }

        // The long-tail "creep" must follow 1 - rho ~ 1/sqrt(t): quadrupling t' halves it.
        double r10 = 1 - (RhoAt(trace, tArr + 10 * tau0) - baseline);
        double r40 = 1 - (RhoAt(trace, tArr + 40 * tau0) - baseline);
        Assert.Equal(2.0, r10 / r40, 0.2);
    }

    // ---- Case 11: causality regression ----

    [Fact]
    public void Case11NonCausalSkinProducesPrecursorCausalDoesNot()
    {
        // The (1+j) in the skin term is mandatory for causality. With it, the region
        // just before the reflected edge holds only the smooth launch creep; without
        // it, energy arrives before the wavefront (the precursor bug this test pins).
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), SkinOnly(50, Vf, 0.4), 300));

        var causal = TdrSimulator.Simulate(model, Tdr1502C, new SimulationSettings { MaxLog2M = 20 });
        var nonCausal = TdrSimulator.Simulate(model, Tdr1502C, new SimulationSettings
        {
            MaxLog2M = 20,
            CableOptions = CableEvalOptions.Default with { CausalSkin = false },
        });
        AssertHermitian(causal);
        AssertHermitian(nonCausal);

        double tArr = 600 / V;
        var window = new List<(double T, double Causal, double NonCausal)>();
        for (double t = tArr - 80e-9; t <= tArr - 2e-9; t += 0.5e-9)
            window.Add((t, RhoAt(causal, t), RhoAt(nonCausal, t)));

        double maxDelta = window.Max(p => Math.Abs(p.NonCausal - p.Causal));
        Assert.True(maxDelta > 1e-3, $"Non-causal precursor not reproduced (max delta {maxDelta:e2}).");

        // Causal pre-arrival content detrends to nearly nothing.
        var (slope, intercept) = LinearFit(window.Select(p => (p.T, p.Causal)).ToList());
        double maxResidual = window.Max(p => Math.Abs(p.Causal - (slope * p.T + intercept)));
        Assert.True(maxResidual < 1e-4, $"Causal trace has pre-arrival structure (residual {maxResidual:e2}).");
    }

    // ---- Case 14: DC realism - a short through long thin cable doesn't read -1 ----

    [Fact]
    public void Case14ShortThroughLongRg174SettlesToLoopResistance()
    {
        var rg174 = BuiltInCatalog.Cables.Single(c => c.Id == "rg174-belden-8216");
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg174, 100),
            new Load(N(1), LoadKind.Short));

        // The approach to the DC level is genuinely slow: below ~1 MHz the skin term
        // still rivals the loop DCR, so rho creeps for tens of microseconds (real TDRs
        // show the same). The default 6x-roundtrip window correctly displays that creep
        // still in progress; settling to 1 % needs a much longer window.
        var settings = new SimulationSettings { WindowOverrideS = 40e-6, MaxLog2M = 20 };
        var trace = TdrSimulator.Simulate(model, Tdr1502C, settings);
        AssertHermitian(trace);
        Assert.True(trace.Settled);

        double rLoop = (318.0 + 35.1) / 1000 * 100; // 35.31 ohm
        double rhoDc = (rLoop - 50) / (rLoop + 50);  // -0.172, nowhere near -1

        // The exact DC limit is guaranteed by the DFT identity.
        Assert.Equal(rhoDc, trace.DcReflection, 1e-9);

        // The plateau still carries the causal dielectric's logarithmic relaxation
        // creep (amplitude ~0.004 in rho, decaying over decades of time), so assert
        // the instrument-meaningful reading: the displayed impedance equals the loop
        // resistance within 1 % - and is nowhere near a dead short.
        double windowEnd = trace.TimeS[^1];
        double plateau = PlateauMean(trace, windowEnd * 0.85, windowEnd * 0.98);
        Assert.InRange(plateau, -0.2, -0.1);
        double ohms = 50 * (1 + plateau) / (1 - plateau);
        Assert.Equal(rLoop, ohms, rLoop * 0.01);
    }

    // ---- Cases 9 and 13: branched networks (M2 nodal solver) ----
    //
    // All lattice cases use lossless 50 ohm line at one shared VF so event times
    // coincide exactly, and the matched 50 ohm source means returning waves die at the
    // port. Junction rule for m equal lines meeting at a node: rho = (2 - m)/m with
    // voltage transmission 2/m into each other line. Every plateau series below sums to
    // the independently computed Gamma(0) - the self-check quoted in each derivation.

    [Fact]
    public void Case9MatchedTeePlateausAtMinusOneThird()
    {
        // m = 3 junction: rho = -1/3; both arms are terminated in 50 ohm and absorb
        // everything, so -1/3 is the only event. Gamma(0) = (50||50 - 50)/(50||50 + 50)
        // = -1/3 exactly.
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 30),
            new CableSegment(N(1), N(2), Line50, 30),
            new CableSegment(N(1), N(3), Line50, 30),
            new Load(N(2), LoadKind.Resistor, ROhms: 50),
            new Load(N(3), LoadKind.Resistor, ROhms: 50));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);
        Assert.Equal(SolverPath.Nodal, trace.Solver);
        Assert.True(trace.Settled);

        double tJ = 2 * 30 / V;
        Assert.True(MaxAbsRho(trace, 3e-9, tJ - 3e-9) < 2e-3); // matched up to the tee
        Assert.Equal(-1.0 / 3, PlateauMean(trace, tJ + 20e-9, tJ + 500e-9), 2e-3);
        Assert.Equal(-1.0 / 3, trace.DcReflection, 1e-9);
    }

    [Fact]
    public void Case9bOpenStubEchoLatticeDecaysToZero()
    {
        // Tee of a 10 m open stub and a matched 30 m through-arm. The stub re-emits
        // every tau = 2*10m/v, and the k-th return adds (2/3)*(2/3)*(-1/3)^(k-1) at
        // the port, so the plateaus are p_k = (-1/3)^k: -1/3, 1/9, -1/27, ... -> 0.
        // Self-check: -1/3 + (4/9)/(1 + 1/3) = 0 = Gamma(0) (an open stub is invisible
        // at DC and the through arm is matched).
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 30),
            new CableSegment(N(1), N(2), Line50, 10),
            new CableSegment(N(1), N(3), Line50, 30),
            new Load(N(3), LoadKind.Resistor, ROhms: 50));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);
        Assert.Equal(SolverPath.Nodal, trace.Solver);

        double tJ = 2 * 30 / V;
        double tau = 2 * 10 / V;
        for (int k = 1; k <= 4; k++)
        {
            double plateau = PlateauMean(trace, tJ + (k - 1) * tau + 20e-9, tJ + k * tau - 20e-9);
            Assert.Equal(Math.Pow(-1.0 / 3, k), plateau, 2e-3);
        }

        Assert.Equal(0, trace.DcReflection, 1e-9);
    }

    [Fact]
    public void Case9cShortedStubEchoLatticeDecaysToMinusOne()
    {
        // Same tee with the stub shorted: each stub cycle multiplies by (-1)*(-1/3) =
        // +1/3 and the first return contributes -4/9, so p_k = 2*3^(-k) - 1: -1/3,
        // -7/9, -25/27, ... -> -1. Self-check: -1/3 - (4/9)/(1 - 1/3) = -1 = Gamma(0)
        // (the stub shorts the junction at DC) - which the solver reaches exactly via
        // union-find, with no matrix solve at all.
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 30),
            new CableSegment(N(1), N(2), Line50, 10),
            new CableSegment(N(1), N(3), Line50, 30),
            new Load(N(2), LoadKind.Short),
            new Load(N(3), LoadKind.Resistor, ROhms: 50));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);
        Assert.Equal(SolverPath.Nodal, trace.Solver);

        double tJ = 2 * 30 / V;
        double tau = 2 * 10 / V;
        for (int k = 1; k <= 4; k++)
        {
            double plateau = PlateauMean(trace, tJ + (k - 1) * tau + 20e-9, tJ + k * tau - 20e-9);
            Assert.Equal(2 * Math.Pow(3, -k) - 1, plateau, 2e-3);
        }

        Assert.Equal(-1.0, trace.DcReflection, 1e-12);
    }

    [Fact]
    public void Case9dFourWayStarPlateausAtMinusOneHalf()
    {
        // m = 4 junction: rho = (2 - 4)/4 = -1/2; three matched arms absorb everything.
        // Gamma(0) = (50/3 - 50)/(50/3 + 50) = -1/2 exactly.
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 30),
            new CableSegment(N(1), N(2), Line50, 30),
            new CableSegment(N(1), N(3), Line50, 30),
            new CableSegment(N(1), N(4), Line50, 30),
            new Load(N(2), LoadKind.Resistor, ROhms: 50),
            new Load(N(3), LoadKind.Resistor, ROhms: 50),
            new Load(N(4), LoadKind.Resistor, ROhms: 50));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);
        Assert.Equal(SolverPath.Nodal, trace.Solver);
        Assert.True(trace.Settled);

        double tJ = 2 * 30 / V;
        Assert.Equal(-0.5, PlateauMean(trace, tJ + 20e-9, tJ + 500e-9), 2e-3);
        Assert.Equal(-0.5, trace.DcReflection, 1e-9);
    }

    [Fact]
    public void Case13RingEchoLatticeClimbsToPlusOne()
    {
        // 30 m feed into a ring: 10 m and 20 m arms rejoining at an unloaded node. Two
        // equal-Z0 lines meeting there are seamless (m = 2: rho = 0), so every
        // circulating path is exactly 30 m and all events land on a lap grid of
        // tau = 30m/v. Each lap's arrivals total 4*3^(-k), adding (2/3)*4*3^(-k) at
        // the port: p_k = 1 - 4*3^(-k): -1/3, 5/9, 23/27, ... -> +1. Self-check:
        // -1/3 + (8/3)*sum_k 3^(-k) = -1/3 + 4/3 = +1 = Gamma(0) (all open at DC).
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), Line50, 30),
            new CableSegment(N(1), N(2), Line50, 10),
            new CableSegment(N(1), N(2), Line50, 20));

        var trace = TdrSimulator.Simulate(model, Tdr1502C);
        AssertHermitian(trace);
        Assert.Equal(SolverPath.Nodal, trace.Solver);
        Assert.True(trace.Settled);

        double tJ = 2 * 30 / V;
        double lap = 30 / V;
        for (int k = 1; k <= 4; k++)
        {
            double plateau = PlateauMean(trace, tJ + (k - 1) * lap + 20e-9, tJ + k * lap - 20e-9);
            Assert.Equal(1 - 4 * Math.Pow(3, -k), plateau, 2e-3);
        }

        Assert.InRange(trace.DcReflection, 1 - 1e-9, 1);
    }

    // ---- Performance smoke (M1 definition of done #2) ----

    [Fact]
    public void PerformanceTenSegmentChainAt32K()
    {
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var elements = new List<Element> { new SourcePort(N(0)) };
        for (int i = 0; i < 10; i++)
            elements.Add(new CableSegment(N(i), N(i + 1), rg58, 30));
        var model = new NetworkModel { Elements = elements };
        var settings = new SimulationSettings { WindowOverrideS = 32768 * 40e-12 };

        var warmup = TdrSimulator.Simulate(model, Tdr1502C, settings);
        Assert.Equal(32768, warmup.Grid.M);

        var times = new List<double>();
        for (int run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            TdrSimulator.Simulate(model, Tdr1502C, settings);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        double median = times[2];
        output.WriteLine($"10-segment chain, M=32768: median {median:f1} ms (runs: {string.Join(", ", times.Select(t => $"{t:f1}"))})");
        Assert.True(median <= 500, $"Median {median:f1} ms exceeds the 500 ms assert (target 30 ms).");
    }

    // ---- Performance (M2 definition of done #3) ----

    [Fact]
    public void PerformanceTeeAt32K()
    {
        // Branching must be ~free at editor scale: the 4-node tee carries the same
        // 30 ms budget as the M1 chain benchmark's target.
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var model = Network(
            new SourcePort(N(0)),
            new CableSegment(N(0), N(1), rg58, 30),
            new CableSegment(N(1), N(2), rg58, 30),
            new CableSegment(N(1), N(3), rg58, 30),
            new Load(N(2), LoadKind.Resistor, ROhms: 50),
            new Load(N(3), LoadKind.Resistor, ROhms: 50));
        var settings = new SimulationSettings { WindowOverrideS = 32768 * 40e-12 };

        var warmup = TdrSimulator.Simulate(model, Tdr1502C, settings);
        Assert.Equal(32768, warmup.Grid.M);
        Assert.Equal(SolverPath.Nodal, warmup.Solver);

        var times = new List<double>();
        for (int run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            TdrSimulator.Simulate(model, Tdr1502C, settings);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        double median = times[2];
        output.WriteLine($"3-arm tee, M=32768: median {median:f1} ms (runs: {string.Join(", ", times.Select(t => $"{t:f1}"))})");
        // Asserted on the minimum: the suite's test classes run in parallel, so the
        // median inflates with whatever else happens to be solving on the box.
        Assert.True(times[0] <= 30, $"Minimum {times[0]:f1} ms exceeds the 30 ms budget.");
    }

    [Fact]
    public void PerformanceStress31NodeTeeAt32K()
    {
        // Three 10-segment arms radiating from the port: 31 nodes, mixed loads. Loose
        // assert at 500 ms (target 100 ms); the dense LU at n = 31 must stay cheap
        // next to cable evaluation.
        var rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");
        var elements = new List<Element> { new SourcePort(N(0)) };
        int node = 1;
        for (int arm = 0; arm < 3; arm++)
        {
            int prev = 0;
            for (int s = 0; s < 10; s++)
            {
                elements.Add(new CableSegment(N(prev), N(node), rg58, 5));
                prev = node++;
            }
        }

        elements.Add(new Load(N(5), LoadKind.Capacitor, CFarads: 5e-12));
        elements.Add(new Load(N(10), LoadKind.Resistor, ROhms: 75));
        elements.Add(new Load(N(20), LoadKind.Short));
        elements.Add(new Load(N(25), LoadKind.Resistor, ROhms: 1000));
        // third arm left open
        var model = new NetworkModel { Elements = elements };
        var settings = new SimulationSettings { WindowOverrideS = 32768 * 40e-12 };

        var warmup = TdrSimulator.Simulate(model, Tdr1502C, settings);
        Assert.Equal(32768, warmup.Grid.M);
        Assert.Equal(SolverPath.Nodal, warmup.Solver);

        var times = new List<double>();
        for (int run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            TdrSimulator.Simulate(model, Tdr1502C, settings);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        double median = times[2];
        output.WriteLine($"31-node tee, M=32768: median {median:f1} ms (runs: {string.Join(", ", times.Select(t => $"{t:f1}"))})");
        Assert.True(median <= 500, $"Median {median:f1} ms exceeds the 500 ms assert (target 100 ms).");
    }

    // ---- helpers ----

    private static int FindIndex(TdrTrace trace, double timeS)
    {
        int k = Array.FindIndex(trace.TimeS, t => t >= timeS);
        Assert.True(k > 0, $"Time {timeS:e3} outside trace.");
        return k;
    }

    /// <summary>
    /// Fits tau of rho(t) = settlesTo - 2*settlesTo*exp(-t'/tau) by linear regression
    /// on ln|settlesTo - rho| over displayed t' in [from, to] past the echo.
    /// </summary>
    private static double FitExponentialTau(TdrTrace trace, double tEcho, double from, double to, double settlesTo)
    {
        var points = new List<(double T, double Y)>();
        for (int k = 0; k < trace.TimeS.Length; k++)
        {
            double tp = trace.TimeS[k] - tEcho;
            if (tp >= from && tp <= to)
                points.Add((tp, Math.Log(Math.Abs(settlesTo - trace.Rho[k]))));
        }

        Assert.True(points.Count > 20, "Too few samples for the exponential fit.");
        var (slope, _) = LinearFit(points);
        return -1 / slope;
    }
}
