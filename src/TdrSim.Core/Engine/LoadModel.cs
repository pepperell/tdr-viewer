using System.Numerics;
using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>Evaluates one-port loads as shunt admittances with exact DC limits.</summary>
public static class LoadModel
{
    /// <summary>Throws if the load's parameters don't match its kind.</summary>
    public static void Validate(Load load)
    {
        switch (load.Kind)
        {
            case LoadKind.Open or LoadKind.Short:
                break;
            case LoadKind.Resistor when load.ROhms is not (>= 0):
                throw new ArgumentException($"Resistor load at {load.Node} needs ROhms >= 0.");
            case LoadKind.Capacitor when load.CFarads is not (> 0):
                throw new ArgumentException($"Capacitor load at {load.Node} needs CFarads > 0.");
            case LoadKind.Inductor when load.LHenries is not (> 0):
                throw new ArgumentException($"Inductor load at {load.Node} needs LHenries > 0.");
            case LoadKind.SeriesRlc or LoadKind.ParallelRlc
                when load.ROhms is null && load.LHenries is null && load.CFarads is null:
                throw new ArgumentException($"RLC load at {load.Node} needs at least one of R, L, C.");
        }
    }

    /// <summary>
    /// Shunt admittance at frequency f. IsShort = true means infinite admittance
    /// (the node is forced to 0 V); Admittance is unused in that case.
    /// </summary>
    public static (bool IsShort, Complex Admittance) ShuntAdmittance(Load load, double fHz)
    {
        double w = 2 * Math.PI * fHz;
        switch (load.Kind)
        {
            case LoadKind.Open:
                return (false, Complex.Zero);

            case LoadKind.Short:
                return (true, Complex.Zero);

            case LoadKind.Resistor:
            {
                double r = load.ROhms!.Value;
                return r == 0 ? (true, Complex.Zero) : (false, new Complex(1 / r, 0));
            }

            case LoadKind.Capacitor:
                return (false, new Complex(0, w * load.CFarads!.Value));

            case LoadKind.Inductor:
                return fHz == 0
                    ? (true, Complex.Zero)
                    : (false, new Complex(0, -1 / (w * load.LHenries!.Value)));

            case LoadKind.SeriesRlc:
            {
                if (fHz == 0)
                {
                    if (load.CFarads is not null)
                        return (false, Complex.Zero); // series C blocks DC
                    double r = load.ROhms ?? 0;
                    return r == 0 ? (true, Complex.Zero) : (false, new Complex(1 / r, 0));
                }

                Complex z = load.ROhms ?? 0;
                if (load.LHenries is { } l)
                    z += new Complex(0, w * l);
                if (load.CFarads is { } c)
                    z += new Complex(0, -1 / (w * c));
                return z == Complex.Zero ? (true, Complex.Zero) : (false, 1 / z);
            }

            case LoadKind.ParallelRlc:
            {
                if (fHz == 0)
                {
                    if (load.LHenries is not null)
                        return (true, Complex.Zero); // parallel L shorts DC
                    return load.ROhms is { } r0
                        ? (r0 == 0 ? (true, Complex.Zero) : (false, new Complex(1 / r0, 0)))
                        : (false, Complex.Zero);
                }

                Complex y = Complex.Zero;
                if (load.ROhms is { } r)
                {
                    if (r == 0)
                        return (true, Complex.Zero);
                    y += new Complex(1 / r, 0);
                }

                if (load.CFarads is { } c)
                    y += new Complex(0, w * c);
                if (load.LHenries is { } l)
                    y += new Complex(0, -1 / (w * l));
                return (false, y);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(load), load.Kind, "Unknown load kind.");
        }
    }
}
