namespace TdrSim.App.Plot;

/// <summary>
/// A display unit chosen from the range being shown: 30 000 ns reads as 30 µs, 0.02 V
/// as 20 mV. <see cref="Factor"/> multiplies the SI value (seconds, volts, metres) to
/// get the number shown next to <see cref="Unit"/>.
/// </summary>
public readonly record struct UnitScale(double Factor, string Unit)
{
    private static readonly UnitScale[] TimeUnits =
        [new(1, "s"), new(1e3, "ms"), new(1e6, "µs"), new(1e9, "ns"), new(1e12, "ps")];

    private static readonly UnitScale[] VoltageUnits =
        [new(1, "V"), new(1e3, "mV"), new(1e6, "µV")];

    private static readonly UnitScale[] DistanceUnits =
        [new(1e-3, "km"), new(1, "m"), new(1e3, "mm")];

    /// <summary>The unit for an axis (or value) whose largest magnitude is
    /// <paramref name="maxSeconds"/>: the biggest one that still shows it as 1 or more.</summary>
    public static UnitScale Time(double maxSeconds) => Pick(TimeUnits, maxSeconds, 3);

    public static UnitScale Voltage(double maxVolts) => Pick(VoltageUnits, maxVolts, 1);

    public static UnitScale Distance(double maxMeters) => Pick(DistanceUnits, maxMeters, 1);

    public static string FormatTime(double seconds) => Format(Time(seconds), seconds, 5);

    public static string FormatVoltage(double volts) => Format(Voltage(volts), volts, 4);

    public static string FormatDistance(double meters) => Format(Distance(meters), meters, 5);

    /// <summary>A fixed number of significant digits, never in exponent form.</summary>
    private static string Format(UnitScale unit, double value, int digits)
    {
        double scaled = value * unit.Factor;
        double magnitude = Math.Abs(scaled);
        int whole = magnitude < 1 ? 1 : (int)Math.Floor(Math.Log10(magnitude)) + 1;
        int decimals = Math.Clamp(digits - whole, 0, digits);
        return $"{scaled.ToString("0." + new string('#', decimals), System.Globalization.CultureInfo.CurrentCulture).TrimEnd('.')} {unit.Unit}";
    }

    private static UnitScale Pick(UnitScale[] units, double max, int zeroIndex)
    {
        max = Math.Abs(max);
        if (max == 0 || !double.IsFinite(max))
            return units[zeroIndex]; // nothing to scale by: the everyday unit (ns, mV, m)
        foreach (var unit in units)
        {
            // A hair under 1 still counts, so 1000 ns of float arithmetic reads as 1 µs.
            if (max * unit.Factor >= 1 - 1e-9)
                return unit;
        }

        return units[^1];
    }
}
