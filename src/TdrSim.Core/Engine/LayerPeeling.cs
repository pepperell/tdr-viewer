namespace TdrSim.Core.Engine;

/// <summary>Why a peel ended where it did.</summary>
public enum PeelStop
{
    /// <summary>Every requested layer was peeled.</summary>
    EndOfData,

    /// <summary>Almost nothing is transmitted past this depth (an open, a short, or a
    /// long run of mismatches): whatever lies beyond cannot be recovered.</summary>
    Opaque,

    /// <summary>A layer asked for |rho| >= 1 - the data is not a lossless layered
    /// line here (noise, loss, branches).</summary>
    Unstable,
}

/// <summary>A peeled impedance profile; sample i sits at <c>StartTimeS + (i+1)*DtS</c>
/// of displayed (round-trip) trace time.</summary>
public sealed record PeelResult
{
    /// <summary>Impedance behind each peeled interface. Entries from
    /// <see cref="ReliableCount"/> on hold the last trustworthy value.</summary>
    public required double[] Ohms { get; init; }

    /// <summary>Local reflection coefficient found at each layer.</summary>
    public required double[] Reflection { get; init; }

    public required double DtS { get; init; }

    public required double StartTimeS { get; init; }

    public required int ReliableCount { get; init; }

    public required PeelStop Stop { get; init; }

    /// <summary>Displayed time of sample <paramref name="index"/>.</summary>
    public double TimeOf(int index) => StartTimeS + (index + 1) * DtS;
}

/// <summary>
/// Layer peeling (dynamic deconvolution, the Schur recursion): treats the line as a
/// stack of equal-delay lossless layers and strips them one at a time, removing every
/// multiple reflection the layers above produced - the "de-ghosted" impedance profile
/// real instruments offer. Exact for a lossless unbranched line; it reads loss as
/// rising impedance and a branch as a parallel combination, which is the lesson.
///
/// Per layer, with a = downgoing and b = upgoing wave just above interface i:
/// r_i = b[0]/a[0], then a' = a - r*b, b' = shift(b - r*a). The common 1/(1-r)
/// scale is dropped (only the ratio matters), which leaves a[0] equal to the
/// cumulative transmission prod(1 - r^2) - the reliability measure, for free.
/// </summary>
public static class LayerPeeling
{
    /// <summary>Cumulative transmission below which deeper layers are not recoverable.</summary>
    public const double OpaqueTransmission = 1e-3;

    /// <summary>Equivalent reflection |(Z - Zref)/(Z + Zref)| beyond which the profile
    /// has effectively reached an open or a short (Z outside Zref/99 .. 99*Zref).</summary>
    public const double OpaqueReflection = 0.98;

    private const double RhoClamp = 0.999;

    /// <summary>
    /// Peels a displayed trace from just before the incident edge to
    /// <paramref name="maxTimeS"/>, on a grid decimated to at most
    /// <paramref name="maxLayers"/> layers (finer than the edge carries no information,
    /// and the recursion is O(N^2)).
    /// </summary>
    public static PeelResult Peel(TdrTrace trace, double maxTimeS, int maxLayers = 2048)
    {
        if (maxLayers < 2)
            throw new ArgumentOutOfRangeException(nameof(maxLayers), maxLayers, "Need at least 2 layers.");

        // EdgeDelayS is 10 rise times; two rise times before the 50 % point the
        // Gaussian edge is still at zero.
        double start = Math.Max(trace.TimeS[0], -trace.EdgeDelayS / 5);
        double end = Math.Min(maxTimeS, trace.TimeS[^1]);
        double dt = trace.Grid.DtS;
        if (end <= start + 2 * dt)
            end = Math.Min(trace.TimeS[^1], start + 2 * dt);

        double span = end - start;
        double dtPeel = Math.Max(dt, span / maxLayers);
        int count = Math.Max(1, (int)Math.Floor(span / dtPeel));

        var impulse = new double[count];
        double previous = RhoAt(trace, start);
        for (int k = 0; k < count; k++)
        {
            double value = RhoAt(trace, start + (k + 1) * dtPeel);
            impulse[k] = value - previous;
            previous = value;
        }

        // Ohms[0] is the pre-edge display value, i.e. exactly Z_ref.
        var (ohms, reflection, reliable, stop) = PeelImpulse(impulse, trace.Ohms[0]);
        return new PeelResult
        {
            Ohms = ohms,
            Reflection = reflection,
            DtS = dtPeel,
            StartTimeS = start,
            ReliableCount = reliable,
            Stop = stop,
        };
    }

    /// <summary>
    /// The recursion itself on a sampled reflection impulse response (sample k = the
    /// reflected wave at round-trip step k for a unit incident impulse).
    /// </summary>
    public static (double[] Ohms, double[] Reflection, int ReliableCount, PeelStop Stop) PeelImpulse(
        ReadOnlySpan<double> impulse, double zRefOhms)
    {
        if (zRefOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(zRefOhms), zRefOhms, "Reference impedance must be > 0.");

        int n = impulse.Length;
        var ohms = new double[n];
        var reflection = new double[n];
        var a = new double[n];
        var b = impulse.ToArray();
        if (n > 0)
            a[0] = 1;

        double z = zRefOhms;
        int reliable = n;
        var stop = PeelStop.EndOfData;
        for (int i = 0; i < n; i++)
        {
            int remaining = n - i;
            double a0 = a[0];
            if (Math.Abs(a0) < OpaqueTransmission)
            {
                reliable = i;
                stop = PeelStop.Opaque;
                break;
            }

            double r = b[0] / a0;
            if (Math.Abs(r) >= RhoClamp)
            {
                // Total reflection is legitimate (an open); anything beyond is not.
                r = Math.Sign(r) * RhoClamp;
                z *= (1 + r) / (1 - r);
                ohms[i] = z;
                reflection[i] = r;
                reliable = i + 1;
                stop = Math.Abs(b[0] / a0) > 1 + 1e-6 ? PeelStop.Unstable : PeelStop.Opaque;
                break;
            }

            z *= (1 + r) / (1 - r);
            ohms[i] = z;
            reflection[i] = r;

            // A band-limited open or short arrives as a smooth taper of many small
            // steps, which stays transparent layer by layer; what gives it away is
            // the impedance itself running off toward infinity or zero.
            if (Math.Abs((z - zRefOhms) / (z + zRefOhms)) > OpaqueReflection)
            {
                reliable = i + 1;
                stop = PeelStop.Opaque;
                break;
            }

            // a' = a - r*b (in place), b' = shift(b - r*a).
            for (int k = 0; k < remaining; k++)
            {
                double ak = a[k];
                double bk = b[k];
                a[k] = ak - r * bk;
                if (k > 0)
                    b[k - 1] = bk - r * ak;
            }

            b[remaining - 1] = 0;
        }

        for (int i = reliable; i < n; i++)
        {
            ohms[i] = reliable > 0 ? ohms[reliable - 1] : zRefOhms;
        }

        return (ohms, reflection, reliable, stop);
    }

    private static double RhoAt(TdrTrace trace, double timeS)
    {
        double x = (timeS + trace.EdgeDelayS) / trace.Grid.DtS;
        int last = trace.Rho.Length - 1;
        if (x <= 0)
            return trace.Rho[0];
        if (x >= last)
            return trace.Rho[last];
        int k = (int)x;
        double frac = x - k;
        return trace.Rho[k] * (1 - frac) + trace.Rho[k + 1] * frac;
    }
}
