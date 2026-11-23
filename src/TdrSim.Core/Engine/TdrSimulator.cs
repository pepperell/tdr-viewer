using System.Numerics;
using TdrSim.Core.Catalog;
using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>The TDR instrument as the engine sees it.</summary>
public sealed record InstrumentSettings
{
    /// <summary>Step amplitude launched into a matched load.</summary>
    public required double StepAmplitudeVolts { get; init; }

    /// <summary>10-90 % rise time, seconds.</summary>
    public required double RiseTimeS { get; init; }

    public required double SourceImpedanceOhms { get; init; }

    public static InstrumentSettings FromSpec(InstrumentSpec spec) => new()
    {
        StepAmplitudeVolts = spec.StepAmplitudeVolts,
        RiseTimeS = spec.RiseTime1090Ps * 1e-12,
        SourceImpedanceOhms = spec.SourceImpedanceOhms,
    };
}

public sealed record SimulationSettings
{
    public static SimulationSettings Default { get; } = new();

    /// <summary>Overrides the auto-sized time window.</summary>
    public double? WindowOverrideS { get; init; }

    public double KaiserBeta { get; init; } = 6.0;

    /// <summary>Cap on the FFT size exponent (M = 2^k).</summary>
    public int MaxLog2M { get; init; } = 18;

    public CableEvalOptions CableOptions { get; init; } = CableEvalOptions.Default;
}

/// <summary>Which engine computed Gamma(f) for a trace.</summary>
public enum SolverPath
{
    /// <summary>Unbranched network folded by <see cref="ChainSolver"/> (the fast path).</summary>
    Chain,

    /// <summary>General network solved per-frequency by <see cref="NodalSolver"/>.</summary>
    Nodal,
}

/// <summary>A computed TDR trace. All arrays share the same length and indexing.</summary>
public sealed record TdrTrace
{
    /// <summary>Time relative to the 50 % point of the incident edge (negative = pre-edge).</summary>
    public required double[] TimeS { get; init; }

    /// <summary>Reflection step response (reflected wave / incident amplitude).</summary>
    public required double[] Rho { get; init; }

    /// <summary>Displayed voltage: A * (incident edge + rho).</summary>
    public required double[] Volts { get; init; }

    /// <summary>Impedance display: Zref * (1 + rho)/(1 - rho), rho clamped near +/-1.</summary>
    public required double[] Ohms { get; init; }

    /// <summary>Distance display: VF*c*t/2 using <see cref="DisplayVelocityFactor"/>.</summary>
    public required double[] DistanceM { get; init; }

    public required FrequencyGrid Grid { get; init; }

    /// <summary>Internal delay between the window start and the incident edge.</summary>
    public required double EdgeDelayS { get; init; }

    public required double DisplayVelocityFactor { get; init; }

    /// <summary>Exact DC reflection coefficient Gamma(0) - what rho settles to.</summary>
    public required double DcReflection { get; init; }

    /// <summary>False if reflections had not died out by the end of the window.</summary>
    public required bool Settled { get; init; }

    /// <summary>max|Im h| / max|Re h| of the impulse response; rounding-level when healthy.</summary>
    public required double MaxImagRatio { get; init; }

    /// <summary>Which solver produced Gamma(f) (chain fast path vs general nodal).</summary>
    public required SolverPath Solver { get; init; }

    /// <summary>The network's own reflection coefficient at f_n = n * Grid.DfHz,
    /// n = 0..M/2, referenced to the instrument's source impedance - what the trace
    /// was synthesized from, before the instrument's edge and window shaped it.
    /// (Not time-indexed: M/2 + 1 entries.)</summary>
    public Complex[]? Gamma { get; init; }
}

public static class TdrSimulator
{
    public static TdrTrace Simulate(
        NetworkModel model,
        InstrumentSettings instrument,
        SimulationSettings? settings = null,
        SimulationSession? session = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= SimulationSettings.Default;

        // Chains take the M1 fold (fast path); anything else compiles for the nodal
        // solver. For a chain the compiler's TotalCableDelayS equals OneWayDelayS, so
        // grid sizing is identical either way.
        var chain = ChainExtractor.TryExtract(model);
        var net = chain is null ? NetworkCompiler.Compile(model, session) : null;
        double oneWayDelay = chain?.OneWayDelayS ?? net!.TotalCableDelayS;

        var grid = FrequencyGrid.Build(
            oneWayDelay, instrument.RiseTimeS, settings.WindowOverrideS, settings.MaxLog2M);

        var freqs = new double[grid.FrequencyCount];
        for (int n = 0; n < freqs.Length; n++)
            freqs[n] = n * grid.DfHz;

        var gamma = chain is not null
            ? ChainSolver.Solve(
                chain, instrument.SourceImpedanceOhms, freqs, settings.CableOptions,
                session, grid, cancellationToken)
            : NodalSolver.Solve(
                net!, instrument.SourceImpedanceOhms, freqs, settings.CableOptions,
                session, grid, cancellationToken);

        // Delay so the full instrument edge (including its zero-phase leading tail)
        // lands inside the window; displayed time is measured from the edge.
        double edgeDelay = 10 * instrument.RiseTimeS;
        var step = TracePipeline.Synthesize(
            gamma, grid, instrument.RiseTimeS, settings.KaiserBeta, edgeDelay, cancellationToken);

        TracePipeline.StepResult edge;
        if (session is not null)
        {
            edge = session.GetEdgeStep(grid, instrument.RiseTimeS, settings.KaiserBeta, edgeDelay, cancellationToken);
        }
        else
        {
            var ones = new Complex[freqs.Length];
            Array.Fill(ones, Complex.One);
            edge = TracePipeline.Synthesize(ones, grid, instrument.RiseTimeS, settings.KaiserBeta, edgeDelay, cancellationToken);
        }

        int m = grid.M;
        double amplitude = instrument.StepAmplitudeVolts;
        double zRef = instrument.SourceImpedanceOhms;
        double vf = (chain is not null ? chain.FirstSegmentVelocityFactor : net!.DisplayVelocityFactor) ?? 1.0;

        var time = new double[m];
        var volts = new double[m];
        var ohms = new double[m];
        var distance = new double[m];
        var rho = step.Rho;
        const double rhoClamp = 1 - 1e-9;
        for (int k = 0; k < m; k++)
        {
            double t = k * grid.DtS - edgeDelay;
            time[k] = t;
            distance[k] = vf * ComplexMath.C0 * t / 2;
            volts[k] = amplitude * (edge.Rho[k] + rho[k]);
            double r = Math.Clamp(rho[k], -rhoClamp, rhoClamp);
            ohms[k] = zRef * (1 + r) / (1 - r);
        }

        return new TdrTrace
        {
            TimeS = time,
            Rho = rho,
            Volts = volts,
            Ohms = ohms,
            DistanceM = distance,
            Grid = grid,
            EdgeDelayS = edgeDelay,
            DisplayVelocityFactor = vf,
            DcReflection = gamma[0].Real,
            Settled = step.Settled,
            MaxImagRatio = Math.Max(step.MaxImagRatio, edge.MaxImagRatio),
            Solver = chain is not null ? SolverPath.Chain : SolverPath.Nodal,
            Gamma = gamma,
        };
    }
}
