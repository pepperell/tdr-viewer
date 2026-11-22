using System.Numerics;

namespace TdrSim.Core.Engine;

/// <summary>
/// ABCD two-port, possibly scaled by an arbitrary nonzero factor. The chain fold works in
/// homogeneous (V, I) coordinates where overall scale cancels, so a scaled matrix is as
/// good as the true one - which lets <see cref="Line"/> use e^(-gamma*l)-scaled entries
/// that never overflow regardless of line loss.
/// </summary>
public readonly record struct TwoPort(Complex A, Complex B, Complex C, Complex D)
{
    public static TwoPort Identity => new(Complex.One, Complex.Zero, Complex.Zero, Complex.One);

    public static TwoPort SeriesImpedance(Complex z) => new(Complex.One, z, Complex.Zero, Complex.One);

    public static TwoPort ShuntAdmittance(Complex y) => new(Complex.One, Complex.Zero, y, Complex.One);

    /// <summary>(V, I) at the near side given (V, I) at the far side.</summary>
    public (Complex V, Complex I) Apply(Complex v, Complex i) => (A * v + B * i, C * v + D * i);

    /// <summary>
    /// Transmission line from total series impedance Z'l and total shunt admittance Y'l.
    /// Three regimes, all free of overflow and cancellation:
    ///  - Y'l = 0 (DC with G = 0): exactly a series impedance Z'l.
    ///  - |gamma*l| small: true ABCD via cosh/sinhc series; the product forms
    ///    Z0*sinh = Z'l*sinhc and sinh/Z0 = Y'l*sinhc avoid the Z0 -> infinity singularity.
    ///  - otherwise: entries scaled by e^(-gamma*l); q = e^(-2*gamma*l) underflows
    ///    harmlessly to 0 for very lossy lines, and 1-q uses Expm1 to avoid cancellation.
    /// </summary>
    public static TwoPort Line(Complex zTotal, Complex yTotal)
    {
        if (yTotal == Complex.Zero)
            return SeriesImpedance(zTotal);

        Complex gl2 = zTotal * yTotal; // (gamma*l)^2
        Complex gl = Complex.Sqrt(gl2);

        if (gl.Magnitude < 1e-4)
        {
            Complex sinhc = 1 + gl2 / 6 + gl2 * gl2 / 120; // sinh(x)/x
            Complex cosh = 1 + gl2 / 2 + gl2 * gl2 / 24;
            return new TwoPort(cosh, zTotal * sinhc, yTotal * sinhc, cosh);
        }

        Complex z0 = Complex.Sqrt(zTotal / yTotal);
        Complex q = Complex.Exp(-2 * gl);
        Complex oneMinusQ = -ComplexMath.Expm1(-2 * gl);
        Complex a = (1 + q) / 2;
        return new TwoPort(a, z0 * oneMinusQ / 2, oneMinusQ / (2 * z0), a);
    }
}
