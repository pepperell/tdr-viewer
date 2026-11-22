using System.Numerics;

namespace TdrSim.Core.Engine;

/// <summary>
/// Djordjevic-Sarkar wideband Debye dielectric: eps(f) = eps_inf + eps_d * Fd(f) with
/// Fd(f) = ln((10^m2 + jf)/(10^m1 + jf)) / ((m2 - m1) * ln 10), m1 = 4, m2 = 12.
/// Causal by construction (Kramers-Kronig consistent); identified from a single
/// (DK, tan delta) point at the reference frequency.
/// </summary>
public static class Dielectric
{
    public const double FRefHz = 100e6;

    private const double W1 = 1e4;  // 10^m1
    private const double W2 = 1e12; // 10^m2
    private static readonly double Denominator = 8 * Math.Log(10); // (m2 - m1) * ln 10

    public static Complex Fd(double fHz)
        => Complex.Log(new Complex(W2, fHz) / new Complex(W1, fHz)) / Denominator;

    /// <summary>
    /// Finds (eps_inf, eps_d) such that eps(FRefHz) = dk * (1 - j*tanDelta) exactly.
    /// </summary>
    public static (double EpsInf, double EpsD) Identify(double dk, double tanDelta)
    {
        if (dk < 1)
            throw new ArgumentOutOfRangeException(nameof(dk), dk, "Relative permittivity must be >= 1.");
        if (tanDelta < 0)
            throw new ArgumentOutOfRangeException(nameof(tanDelta), tanDelta, "tan delta must be >= 0.");
        if (tanDelta == 0)
            return (dk, 0);

        Complex fd = Fd(FRefHz);
        double epsD = dk * tanDelta / -fd.Imaginary;
        double epsInf = dk - epsD * fd.Real;
        return (epsInf, epsD);
    }

    public static Complex Evaluate(double epsInf, double epsD, double fHz)
        => epsInf + epsD * Fd(fHz);
}
