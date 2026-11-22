namespace TdrSim.Core.Engine;

/// <summary>
/// The harmonic frequency grid / time window for step synthesis. Frequencies are
/// f_n = n * Df for n = 0..M/2; the time trace has M samples of Dt.
/// </summary>
public sealed record FrequencyGrid
{
    /// <summary>FFT size; always a power of two.</summary>
    public required int M { get; init; }

    public required double DtS { get; init; }

    /// <summary>
    /// True when the window could not be covered at the requested time resolution
    /// within the sample cap, and Dt was stretched (edge fidelity reduced).
    /// </summary>
    public required bool Compromised { get; init; }

    public double DfHz => 1.0 / (M * DtS);

    public double WindowS => M * DtS;

    public int FrequencyCount => M / 2 + 1;

    /// <summary>
    /// Sizes the grid: window = max(6 * round trip, 40 * rise time, 10 ns) so multiple
    /// re-reflections settle in view, dt = riseTime/5 to resolve the edge, M the next
    /// power of two (floor 2^10, cap 2^maxLog2M - when capped, dt stretches and the
    /// grid is flagged <see cref="Compromised"/>).
    /// </summary>
    public static FrequencyGrid Build(
        double oneWayDelayS,
        double riseTimeS,
        double? windowOverrideS = null,
        int maxLog2M = 18)
    {
        if (riseTimeS <= 0)
            throw new ArgumentOutOfRangeException(nameof(riseTimeS), riseTimeS, "Rise time must be > 0.");
        if (maxLog2M is < 10 or > 24)
            throw new ArgumentOutOfRangeException(nameof(maxLog2M), maxLog2M, "maxLog2M must be in [10, 24].");

        double window = windowOverrideS
            ?? Math.Max(Math.Max(6 * 2 * oneWayDelayS, 40 * riseTimeS), 10e-9);
        double dt = riseTimeS / 5;

        int log2 = 10;
        while (log2 < maxLog2M && (1L << log2) * dt < window)
            log2++;

        int m = 1 << log2;
        bool compromised = m * dt < window;
        if (compromised)
            dt = window / m;

        return new FrequencyGrid { M = m, DtS = dt, Compromised = compromised };
    }
}
