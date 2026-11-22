using System.Numerics;

namespace TdrSim.Core.Engine;

/// <summary>Numeric helpers shared by the synthesis engine.</summary>
public static class ComplexMath
{
    /// <summary>Speed of light in vacuum, m/s.</summary>
    public const double C0 = 299_792_458.0;

    /// <summary>ln(10)/20: multiply dB by this to get nepers.</summary>
    public const double NepersPerDb = 0.11512925464970229;

    /// <summary>
    /// e^z - 1 without cancellation for small |z|. Real part uses
    /// expm1(a)*cos(b) - 2*sin^2(b/2), which is exact-to-rounding as z -> 0.
    /// </summary>
    public static Complex Expm1(Complex z)
    {
        double sinHalf = Math.Sin(z.Imaginary / 2);
        double re = Expm1(z.Real) * Math.Cos(z.Imaginary) - 2 * sinHalf * sinHalf;
        double im = Math.Exp(z.Real) * Math.Sin(z.Imaginary);
        return new Complex(re, im);
    }

    /// <summary>
    /// e^x - 1 for real x (the BCL has no Math.Expm1). Kahan's formula: compute
    /// e^x - 1 naively, then correct the cancellation via x/log(e^x).
    /// </summary>
    public static double Expm1(double x)
    {
        double u = Math.Exp(x);
        if (u == 1.0)
            return x;
        if (double.IsPositiveInfinity(u))
            return u;
        double um1 = u - 1.0;
        return um1 == -1.0 ? -1.0 : um1 * x / Math.Log(u);
    }

    /// <summary>
    /// Modified Bessel function I0 by power series. Converges quickly for the
    /// Kaiser-window argument range (x up to ~20).
    /// </summary>
    public static double BesselI0(double x)
    {
        double q = x * x / 4;
        double term = 1.0;
        double sum = 1.0;
        for (int k = 1; k < 200; k++)
        {
            term *= q / (k * (double)k);
            sum += term;
            if (term < sum * 1e-17)
                break;
        }

        return sum;
    }

    /// <summary>
    /// Kaiser half-window value at fraction x in [0, 1] of the band edge
    /// (x = 0 at DC, x = 1 at Nyquist). W(0) = 1.
    /// </summary>
    public static double Kaiser(double x, double beta)
        => BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - x * x))) / BesselI0(beta);
}
