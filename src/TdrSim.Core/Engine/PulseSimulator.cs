using System.Numerics;
using FftFlat;
using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>A periodic pulse-train stimulus: independent Gaussian rise and fall edges,
/// repetition rate, and duty cycle (50 %-to-50 % width / period).</summary>
public sealed record PulseSettings
{
    public required double RiseTimeS { get; init; }

    public required double FallTimeS { get; init; }

    public required double RateHz { get; init; }

    /// <summary>Pulse width as a fraction of the period, in (0, 1).</summary>
    public required double DutyFraction { get; init; }

    public int MaxLog2M { get; init; } = 18;

    public double KaiserBeta { get; init; } = 6.0;

    public CableEvalOptions CableOptions { get; init; } = CableEvalOptions.Default;
}

/// <summary>One repetition period of the periodic steady-state voltage at the probe.</summary>
public sealed record PulseTrace
{
    /// <summary>Samples of one full period; tile for a multi-pulse display.</summary>
    public required double[] Volts { get; init; }

    public required double PeriodS { get; init; }

    public required double DtS { get; init; }

    /// <summary>Where the first rising edge's 50 % point sits inside the period.</summary>
    public required double EdgeOffsetS { get; init; }

    /// <summary>True when the sample cap left dt too coarse for the edges.</summary>
    public required bool Compromised { get; init; }

    /// <summary>Hermitian self-check, as on the TDR trace.</summary>
    public required double MaxImagRatio { get; init; }
}

/// <summary>
/// Periodic steady-state pulse-train analysis: because the stimulus is periodic, one
/// nodal solve per harmonic f_k = k·rate gives the exact waveform at any net - no
/// windowing heuristics, no settling question; reflections that outlive a period are
/// exactly what the wrap-around represents. The source is Vs(f)·transfer(f) where the
/// pulse spectrum is two Gaussian-filtered step edges a duty-width apart:
/// c_0 = EMF·D, c_k = EMF·(H_rise − H_fall·e^(−j2πf·W)) / (j2πf·T).
/// </summary>
public static class PulseSimulator
{
    public static PulseTrace Simulate(
        NetworkModel model,
        InstrumentSettings instrument,
        PulseSettings pulse,
        NodeId observeNode,
        SimulationSession? session = null,
        CancellationToken cancellationToken = default)
    {
        if (pulse.RateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(pulse), pulse.RateHz, "Pulse rate must be > 0.");
        if (pulse.DutyFraction is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(pulse), pulse.DutyFraction, "Duty must be in (0, 1).");
        if (pulse.RiseTimeS <= 0 || pulse.FallTimeS <= 0)
            throw new ArgumentOutOfRangeException(nameof(pulse), "Rise and fall times must be > 0.");

        var net = NetworkCompiler.Compile(model, session);

        // Harmonic grid of the repetition period: M·dt == T exactly, dt fine enough
        // for the faster edge, power-of-two M capped like the TDR grid.
        double period = 1.0 / pulse.RateHz;
        double dtTarget = Math.Min(pulse.RiseTimeS, pulse.FallTimeS) / 5;
        int log2 = 10;
        while (log2 < pulse.MaxLog2M && period / (1L << log2) > dtTarget)
            log2++;
        int m = 1 << log2;
        double dt = period / m;
        var grid = new FrequencyGrid { M = m, DtS = dt, Compromised = dt > dtTarget * (1 + 1e-9) };

        var freqs = new double[grid.FrequencyCount];
        for (int n = 0; n < freqs.Length; n++)
            freqs[n] = n * grid.DfHz;

        var transfer = NodalSolver.SolveNodeTransfer(
            net, instrument.SourceImpedanceOhms, observeNode, freqs, pulse.CableOptions,
            session, grid, cancellationToken);

        // Fourier series of the stimulus times the transfer. EMF = 2A so a matched
        // line sees the instrument's amplitude convention (A into Z_ref).
        double emf = 2 * instrument.StepAmplitudeVolts;
        double width = pulse.DutyFraction * period;
        double edgeOffset = period / 8; // keep the leading edge clear of the wrap
        double riseCoeff = GaussCoeff(pulse.RiseTimeS);
        double fallCoeff = GaussCoeff(pulse.FallTimeS);
        int half = m / 2;

        var x = new Complex[m];
        x[0] = emf * pulse.DutyFraction * transfer[0];
        for (int k = 1; k <= half; k++)
        {
            double f = freqs[k];
            var rise = new Complex(Math.Exp(-riseCoeff * f * f), 0);
            var fall = Complex.FromPolarCoordinates(
                Math.Exp(-fallCoeff * f * f), -2 * Math.PI * f * width);
            Complex coefficient = emf * (rise - fall) / new Complex(0, 2 * Math.PI * f * period);
            double window = ComplexMath.Kaiser((double)k / half, pulse.KaiserBeta);
            x[k] = coefficient * transfer[k] * window
                * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * edgeOffset);
        }

        x[0] = new Complex(x[0].Real, 0);
        x[half] = new Complex(x[half].Real, 0);
        for (int k = 1; k < half; k++)
            x[m - k] = Complex.Conjugate(x[k]);

        cancellationToken.ThrowIfCancellationRequested();
        new FastFourierTransform(m).Inverse(x);
        cancellationToken.ThrowIfCancellationRequested();

        // FftFlat's inverse carries 1/M; Fourier synthesis wants the plain sum.
        var volts = new double[m];
        double maxRe = 0, maxIm = 0;
        for (int k = 0; k < m; k++)
        {
            volts[k] = x[k].Real * m;
            maxRe = Math.Max(maxRe, Math.Abs(x[k].Real));
            maxIm = Math.Max(maxIm, Math.Abs(x[k].Imaginary));
        }

        return new PulseTrace
        {
            Volts = volts,
            PeriodS = period,
            DtS = dt,
            EdgeOffsetS = edgeOffset,
            Compromised = grid.Compromised,
            MaxImagRatio = maxRe > 0 ? maxIm / maxRe : 0,
        };
    }

    /// <summary>Gaussian edge filter coefficient: H(f) = exp(−c·f²), f3dB = 0.34/t_rise.</summary>
    private static double GaussCoeff(double riseTimeS)
    {
        double f3Db = 0.34 / riseTimeS;
        return Math.Log(2) / 2 / (f3Db * f3Db);
    }
}
