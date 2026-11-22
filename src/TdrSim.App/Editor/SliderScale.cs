namespace TdrSim.App.Editor;

/// <summary>
/// Companions for slider-first numeric entry. Quantities spanning decades (ohms,
/// picofarads, picoseconds) get log10 sliders; values coming off a slider are rounded
/// to a sensible number of significant digits so the exact-entry box stays readable.
/// </summary>
public static class SliderScale
{
    /// <summary>Natural value -> slider position in decades, clamped to the rail.</summary>
    public static double ToLog(double? value, double fallback, double min, double max)
        => Math.Log10(Math.Clamp(value ?? fallback, min, max));

    /// <summary>Slider position in decades -> natural value, rounded.</summary>
    public static double FromLog(double logValue, int significantDigits = 3)
        => RoundSig(Math.Pow(10, logValue), significantDigits);

    public static double RoundSig(double value, int digits)
    {
        if (value == 0)
            return 0;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(value))) - digits + 1);
        return Math.Round(Math.Round(value / magnitude) * magnitude, 12);
    }
}
