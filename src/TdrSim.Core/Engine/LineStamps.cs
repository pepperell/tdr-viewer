using System.Numerics;
using TdrSim.Core.Catalog;

namespace TdrSim.Core.Engine;

/// <summary>
/// Pi-equivalent admittance stamp of a two-node element at one frequency. When
/// <see cref="IsIdealShort"/> is set the series branch has infinite admittance - the
/// caller must merge the two nodes instead of stamping it; the shunt halves still apply
/// (on the merged node).
/// </summary>
public readonly record struct PiStamp(Complex SeriesY, Complex ShuntYA, Complex ShuntYB, bool IsIdealShort)
{
    public static PiStamp IdealShort(Complex shuntYa, Complex shuntYb)
        => new(Complex.Zero, shuntYa, shuntYb, true);
}

/// <summary>
/// Numerically stable Pi stamps for the nodal solver: Yser = 1/(Z0 sinh(gl)) and
/// Ysh = tanh(gl/2)/Z0 per side, computed in e^(-x) and product forms so that neither
/// extreme loss nor the f -> 0 limit (where Z0 -> infinity) can overflow or cancel.
/// The three regimes mirror <see cref="TwoPort.Line"/>.
/// </summary>
public static class LineStamps
{
    /// <summary>
    /// Pi stamp of a uniform line with total series impedance Z'l and total shunt
    /// admittance Y'l (Re(gamma*l) >= 0 for any passive line).
    /// </summary>
    public static PiStamp Line(Complex zTotal, Complex yTotal)
    {
        if (zTotal == Complex.Zero && yTotal == Complex.Zero)
            return PiStamp.IdealShort(Complex.Zero, Complex.Zero); // lossless line at DC

        Complex gl2 = zTotal * yTotal; // (gamma*l)^2
        Complex gl = Complex.Sqrt(gl2);

        if (gl.Magnitude < 1e-4)
        {
            // Product forms that never form Z0 alone: Z0*sinh(gl) = Z'l*sinhc(gl) and
            // tanh(gl/2)/Z0 = (Y'l/2)*tanhc(gl/2). At f = 0 on a lossy line (Y'l = 0)
            // this is exactly the series-resistance stamp.
            if (zTotal == Complex.Zero)
                return PiStamp.IdealShort(yTotal / 2, yTotal / 2);
            Complex sinhc = 1 + gl2 / 6 + gl2 * gl2 / 120;          // sinh(x)/x
            Complex tanhc = 1 - gl2 / 12 + gl2 * gl2 / 120;         // tanh(u)/u at u = gl/2
            Complex ysh = yTotal / 2 * tanhc;
            return new PiStamp(1 / (zTotal * sinhc), ysh, ysh, false);
        }

        // sinh(x) = (1 - e^(-2x))/(2 e^(-x)), tanh(x/2) = (1 - e^(-x))/(1 + e^(-x)).
        // For very lossy lines q1 underflows to 0: Yser -> 0, Ysh -> 1/Z0 - the far end
        // decouples and the port sees matched Z0, which is physically correct.
        Complex z0 = Complex.Sqrt(zTotal / yTotal);
        Complex q1 = Complex.Exp(-gl);
        Complex oneMinusQ1 = -ComplexMath.Expm1(-gl);
        Complex oneMinusQ2 = -ComplexMath.Expm1(-2 * gl);
        return new PiStamp(
            2 * q1 / (z0 * oneMinusQ2),
            oneMinusQ1 / ((1 + q1) * z0),
            oneMinusQ1 / ((1 + q1) * z0),
            false);
    }

    /// <summary>
    /// Connector as a Pi: series 1/(R + jwL) with a shunt C on each side. Node A carries
    /// the near-side capacitance, node B the far side. R = L = 0 (or any purely inductive
    /// connector at DC) collapses to an ideal short.
    /// </summary>
    public static PiStamp Connector(ConnectorSpec spec, double fHz)
    {
        double w = 2 * Math.PI * fHz;
        var ya = new Complex(0, w * spec.ShuntCapacitanceNearPf * 1e-12);
        var yb = new Complex(0, w * spec.ShuntCapacitanceFarPf * 1e-12);
        var z = new Complex(spec.SeriesResistanceOhms, w * spec.SeriesInductanceNh * 1e-9);
        return z == Complex.Zero ? PiStamp.IdealShort(ya, yb) : new PiStamp(1 / z, ya, yb, false);
    }
}
