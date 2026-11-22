namespace TdrSim.Core.Engine;

/// <summary>Additive instrument noise: RMS of a single sweep, and how many sweeps the
/// displayed trace averages (RMS shrinks by sqrt(N), like a real repetitive sweep).</summary>
public sealed record TraceNoiseSettings(double RmsVolts, int Sweeps)
{
    public double EffectiveRmsVolts => RmsVolts / Math.Sqrt(Sweeps);
}

/// <summary>
/// Display-stage post-processing - applied after <see cref="TdrSimulator.Simulate"/>,
/// outside every cache and every bitwise guarantee. Noise is physical on the volts
/// scale; rho shifts by noise/amplitude and ohms is recomputed from the clamped noisy
/// rho, so all three y-modes stay mutually consistent. Time/distance/grid arrays are
/// shared with the input trace.
/// </summary>
public static class TraceNoise
{
    public static TdrTrace Apply(
        TdrTrace trace,
        TraceNoiseSettings settings,
        double stepAmplitudeVolts,
        double zRefOhms,
        int seed)
    {
        if (settings.RmsVolts <= 0)
            return trace;

        double sigma = settings.EffectiveRmsVolts;
        var random = new Random(seed);
        int m = trace.Rho.Length;
        var rho = new double[m];
        var volts = new double[m];
        var ohms = new double[m];
        const double rhoClamp = 1 - 1e-9;
        for (int k = 0; k < m; k++)
        {
            double noise = sigma * NextGaussian(random);
            volts[k] = trace.Volts[k] + noise;
            rho[k] = trace.Rho[k] + noise / stepAmplitudeVolts;
            double r = Math.Clamp(rho[k], -rhoClamp, rhoClamp);
            ohms[k] = zRefOhms * (1 + r) / (1 - r);
        }

        return trace with { Rho = rho, Volts = volts, Ohms = ohms };
    }

    /// <summary>Adds Gaussian noise of the given RMS to the samples in place - the
    /// pulse view uses this on its tiled display array so every repetition carries
    /// independent noise, like distinct sweeps on a real screen.</summary>
    public static void AddGaussian(double[] values, double rms, int seed)
    {
        if (rms <= 0)
            return;
        var random = new Random(seed);
        for (int k = 0; k < values.Length; k++)
            values[k] += rms * NextGaussian(random);
    }

    private static double NextGaussian(Random random)
    {
        // Box-Muller; 1 - NextDouble() keeps u1 in (0, 1] so the log is finite.
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
