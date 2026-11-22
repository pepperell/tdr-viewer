using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;

namespace TdrSim.Core.Tests;

/// <summary>Shared builders and trace-measurement helpers for engine tests.</summary>
internal static class TestNetworks
{
    public const double C0 = ComplexMath.C0;

    public static readonly ComponentCatalog BuiltInCatalog = CatalogLoader.LoadEmbedded();

    /// <summary>Tek 1502C-class instrument: 300 mV, 200 ps, 50 ohm.</summary>
    public static InstrumentSettings Tdr1502C { get; } =
        InstrumentSettings.FromSpec(BuiltInCatalog.Instruments.Single(i => i.Id == "tek-1502c"));

    /// <summary>Ideal lossless line; no attenuation data means the engine fits k1 = k2 = 0.</summary>
    public static CableSpec Lossless(double z0, double vf) => new()
    {
        Id = $"test-lossless-{z0}",
        Name = $"ideal {z0} ohm",
        Category = "coax",
        Z0Ohms = z0,
        VelocityFactor = vf,
        CapacitancePfPerM = 1e12 / (z0 * vf * C0),
        RdcCenterOhmsPerKm = 0,
        RdcShieldOhmsPerKm = 0,
    };

    /// <summary>
    /// Pure skin-effect line: attenuation exactly k1*sqrt(f), no DC resistance, no
    /// dielectric loss - the Wigington-Nahman erfc ground truth applies exactly.
    /// </summary>
    public static CableSpec SkinOnly(double z0, double vf, double k1DbPer100MSqrtMhz) => new()
    {
        Id = $"test-skin-{k1DbPer100MSqrtMhz}",
        Name = "skin-only line",
        Category = "coax",
        Z0Ohms = z0,
        VelocityFactor = vf,
        CapacitancePfPerM = 1e12 / (z0 * vf * C0),
        RdcCenterOhmsPerKm = 0,
        RdcShieldOhmsPerKm = 0,
        Attenuation = new AttenuationTable
        {
            Units = "dB_per_100m",
            Points = [.. new double[] { 1, 10, 100, 1000 }
                .Select(f => new AttenuationPoint(f, k1DbPer100MSqrtMhz * Math.Sqrt(f)))],
        },
    };

    public static NetworkModel Network(params Element[] elements) => new() { Elements = elements };

    public static NodeId N(int value) => new(value);

    /// <summary>Mean rho over a displayed-time interval.</summary>
    public static double PlateauMean(TdrTrace trace, double fromS, double toS)
    {
        double sum = 0;
        int count = 0;
        for (int k = 0; k < trace.TimeS.Length; k++)
        {
            if (trace.TimeS[k] >= fromS && trace.TimeS[k] <= toS)
            {
                sum += trace.Rho[k];
                count++;
            }
        }

        Assert.True(count > 10, $"Plateau window [{fromS}, {toS}] contains only {count} samples.");
        return sum / count;
    }

    public static double MaxAbsRho(TdrTrace trace, double fromS, double toS)
    {
        double max = 0;
        for (int k = 0; k < trace.TimeS.Length; k++)
        {
            if (trace.TimeS[k] >= fromS && trace.TimeS[k] <= toS)
                max = Math.Max(max, Math.Abs(trace.Rho[k]));
        }

        return max;
    }

    /// <summary>Linearly interpolated rho at a displayed time.</summary>
    public static double RhoAt(TdrTrace trace, double timeS)
    {
        double internalT = (timeS + trace.EdgeDelayS) / trace.Grid.DtS;
        int k = (int)Math.Floor(internalT);
        Assert.InRange(k, 0, trace.Rho.Length - 2);
        double frac = internalT - k;
        return trace.Rho[k] * (1 - frac) + trace.Rho[k + 1] * frac;
    }

    /// <summary>
    /// First upward (or downward, by sign of direction) crossing of rho through level
    /// inside the search interval, located by linear interpolation.
    /// </summary>
    public static double CrossingTime(TdrTrace trace, double level, double fromS, double toS)
    {
        for (int k = 1; k < trace.TimeS.Length; k++)
        {
            if (trace.TimeS[k] < fromS || trace.TimeS[k] > toS)
                continue;
            double a = trace.Rho[k - 1];
            double b = trace.Rho[k];
            if ((a - level) * (b - level) <= 0 && a != b)
            {
                double frac = (level - a) / (b - a);
                return trace.TimeS[k - 1] + frac * (trace.TimeS[k] - trace.TimeS[k - 1]);
            }
        }

        throw new Xunit.Sdk.XunitException($"rho never crosses {level} in [{fromS}, {toS}].");
    }

    /// <summary>Trapezoidal integral of rho over a displayed-time interval.</summary>
    public static double IntegrateRho(TdrTrace trace, double fromS, double toS)
    {
        double sum = 0;
        for (int k = 1; k < trace.TimeS.Length; k++)
        {
            if (trace.TimeS[k - 1] >= fromS && trace.TimeS[k] <= toS)
                sum += (trace.Rho[k - 1] + trace.Rho[k]) / 2 * (trace.TimeS[k] - trace.TimeS[k - 1]);
        }

        return sum;
    }

    /// <summary>
    /// Least-squares slope/intercept of y(t) over the sample points in the interval.
    /// </summary>
    public static (double Slope, double Intercept) LinearFit(IReadOnlyList<(double T, double Y)> points)
    {
        double st = 0, sy = 0, stt = 0, sty = 0;
        foreach (var (t, y) in points)
        {
            st += t;
            sy += y;
            stt += t * t;
            sty += t * y;
        }

        int n = points.Count;
        double slope = (n * sty - st * sy) / (n * stt - st * st);
        return (slope, (sy - slope * st) / n);
    }

    /// <summary>Case 12: the Hermitian/causality self-check applied to every trace.</summary>
    public static void AssertHermitian(TdrTrace trace)
        => Assert.True(trace.MaxImagRatio < 1e-10,
            $"Impulse response has non-rounding imaginary content: ratio {trace.MaxImagRatio:e2}.");

    /// <summary>Complementary error function (no BCL equivalent); |error| &lt; 1.2e-7.</summary>
    public static double Erfc(double x)
    {
        // Numerical Recipes rational Chebyshev approximation.
        double z = Math.Abs(x);
        double t = 1 / (1 + z / 2);
        double ans = t * Math.Exp(-z * z - 1.26551223 +
            t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
            t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 +
            t * 0.17087277)))))))));
        return x >= 0 ? ans : 2 - ans;
    }
}
