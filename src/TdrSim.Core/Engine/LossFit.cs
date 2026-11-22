using TdrSim.Core.Catalog;

namespace TdrSim.Core.Engine;

/// <summary>
/// Fitted cable loss model: alpha_dB(f) = K1*sqrt(f) + K2*f in dB/100 m with f in MHz.
/// K1 captures skin effect, K2 dielectric loss.
/// </summary>
public sealed record LossCoefficients(double K1DbPer100mSqrtMhz, double K2DbPer100mMhz)
{
    public static LossCoefficients Lossless { get; } = new(0, 0);

    /// <summary>Skin coefficient in Np/(m*sqrt(Hz)).</summary>
    public double K1NpPerMSqrtHz => K1DbPer100mSqrtMhz * ComplexMath.NepersPerDb / 100 / 1000;

    /// <summary>Dielectric coefficient in Np/(m*Hz).</summary>
    public double K2NpPerMHz => K2DbPer100mMhz * ComplexMath.NepersPerDb / 100 / 1e6;
}

public static class LossFit
{
    /// <summary>
    /// Derives the 2-term loss model from a cable spec: attenuation table preferred,
    /// else TIA insertion-loss coefficients (sampled and refitted - the k3/sqrt(f)
    /// term has no causal RLGC mapping), else lossless.
    /// </summary>
    public static LossCoefficients FromSpec(CableSpec spec)
    {
        if (spec.Attenuation is { } table)
            return FitPoints(table.Points.Select(p => (p.Mhz, p.Db)).ToArray());

        if (spec.InsertionLossCoefficients is { } il)
        {
            const int samples = 50;
            var pts = new (double, double)[samples];
            for (int i = 0; i < samples; i++)
            {
                // log-spaced 1..250 MHz, the TIA structured-cabling band
                double f = Math.Pow(10, Math.Log10(250) * i / (samples - 1));
                pts[i] = (f, TiaInsertionLoss(il, f));
            }

            return FitPoints(pts);
        }

        return LossCoefficients.Lossless;
    }

    /// <summary>TIA/EIA-568 3-term insertion loss in dB/100 m, f in MHz.</summary>
    public static double TiaInsertionLoss(InsertionLossCoefficients c, double fMhz)
        => c.K1 * Math.Sqrt(fMhz) + c.K2 * fMhz + c.K3 / Math.Sqrt(fMhz);

    /// <summary>
    /// Non-negative least squares for alpha = k1*sqrt(f) + k2*f via the closed-form
    /// 2x2 normal equations; if a coefficient comes out negative it is clamped to 0
    /// and the other refit alone.
    /// </summary>
    public static LossCoefficients FitPoints(IReadOnlyList<(double FMhz, double ADb)> points)
    {
        if (points.Count == 0)
            return LossCoefficients.Lossless;

        double s11 = 0, s12 = 0, s22 = 0, b1 = 0, b2 = 0;
        foreach (var (f, a) in points)
        {
            if (f <= 0)
                throw new ArgumentException($"Attenuation frequency must be positive, got {f} MHz.");
            double sf = Math.Sqrt(f);
            s11 += f;
            s12 += f * sf;
            s22 += f * f;
            b1 += sf * a;
            b2 += f * a;
        }

        double k1, k2;
        if (points.Count == 1)
        {
            k1 = b1 / s11;
            k2 = 0;
        }
        else
        {
            double det = s11 * s22 - s12 * s12;
            k1 = (b1 * s22 - b2 * s12) / det;
            k2 = (s11 * b2 - s12 * b1) / det;
        }

        if (k2 < 0)
        {
            k2 = 0;
            k1 = b1 / s11;
        }

        if (k1 < 0)
        {
            k1 = 0;
            k2 = b2 / s22;
        }

        return new LossCoefficients(k1, k2);
    }
}
