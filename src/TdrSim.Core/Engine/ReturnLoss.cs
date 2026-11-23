using System.Numerics;

namespace TdrSim.Core.Engine;

/// <summary>
/// |Gamma(f)| in dB against frequency: the frequency-domain twin of a TDR trace, read
/// straight off the Gamma(f) every run already solves. Nothing here is instrument-
/// shaped - no rise-time roll-off, no window - so it is what a VNA would show for the
/// same network at the same reference impedance.
/// </summary>
public sealed record ReturnLossCurve
{
    /// <summary>Ascending, uniformly spaced, starting at DC.</summary>
    public required double[] FrequencyHz { get; init; }

    /// <summary>20*log10|Gamma|, floored at -<see cref="ReturnLoss.FloorDb"/>.</summary>
    public required double[] GammaDb { get; init; }

    public required double[] GammaMagnitude { get; init; }

    public required double ZRefOhms { get; init; }

    /// <summary>The instrument's -3 dB bandwidth (0.34 / rise time): features above
    /// it are real but all but invisible on this instrument's trace.</summary>
    public required double InstrumentBandwidthHz { get; init; }

    public double DfHz => FrequencyHz.Length > 1 ? FrequencyHz[1] - FrequencyHz[0] : 0;

    /// <summary>Index of the sample nearest a frequency.</summary>
    public int IndexAt(double fHz)
    {
        if (FrequencyHz.Length < 2)
            return 0;
        return Math.Clamp((int)Math.Round(fHz / DfHz), 0, FrequencyHz.Length - 1);
    }

    /// <summary>The worst match (largest |Gamma|) at or below a frequency.</summary>
    public (double FrequencyHz, double Magnitude) WorstUpTo(double maxHz)
    {
        int best = 0;
        for (int n = 1; n < FrequencyHz.Length && FrequencyHz[n] <= maxHz; n++)
        {
            if (GammaMagnitude[n] > GammaMagnitude[best])
                best = n;
        }

        return (FrequencyHz[best], GammaMagnitude[best]);
    }
}

public static class ReturnLoss
{
    /// <summary>Display floor: a match better than this is indistinguishable from the
    /// solver's rounding on a perfectly terminated line.</summary>
    public const double FloorDb = 80;

    /// <summary>Return loss in dB (positive; larger = better match), capped at the floor.</summary>
    public static double Db(double gammaMagnitude)
        => gammaMagnitude <= 0 ? FloorDb : Math.Min(FloorDb, -20 * Math.Log10(gammaMagnitude));

    public static double Vswr(double gammaMagnitude)
        => gammaMagnitude >= 1 ? double.PositiveInfinity : (1 + gammaMagnitude) / (1 - gammaMagnitude);

    /// <summary>Power not delivered because of the mismatch, in dB.</summary>
    public static double MismatchLossDb(double gammaMagnitude)
        => gammaMagnitude >= 1
            ? double.PositiveInfinity
            : -10 * Math.Log10(1 - gammaMagnitude * gammaMagnitude);

    /// <summary>
    /// The curve of a simulated trace up to <paramref name="maxHz"/> (default: 1/rise
    /// time, about three instrument bandwidths, never past the grid's Nyquist). Null
    /// when the trace carries no Gamma(f).
    /// </summary>
    public static ReturnLossCurve? FromTrace(TdrTrace trace, InstrumentSettings instrument, double? maxHz = null)
    {
        if (trace.Gamma is not { } gamma)
            return null;
        return FromGamma(
            gamma, trace.Grid.DfHz, instrument.SourceImpedanceOhms,
            0.34 / instrument.RiseTimeS, maxHz ?? 1 / instrument.RiseTimeS);
    }

    public static ReturnLossCurve FromGamma(
        IReadOnlyList<Complex> gamma, double dfHz, double zRefOhms, double instrumentBandwidthHz, double maxHz)
    {
        int count = Math.Clamp((int)Math.Floor(maxHz / dfHz) + 1, Math.Min(2, gamma.Count), gamma.Count);
        var f = new double[count];
        var db = new double[count];
        var magnitude = new double[count];
        for (int n = 0; n < count; n++)
        {
            f[n] = n * dfHz;
            magnitude[n] = gamma[n].Magnitude;
            db[n] = -Db(magnitude[n]);
        }

        return new ReturnLossCurve
        {
            FrequencyHz = f,
            GammaDb = db,
            GammaMagnitude = magnitude,
            ZRefOhms = zRefOhms,
            InstrumentBandwidthHz = instrumentBandwidthHz,
        };
    }
}
