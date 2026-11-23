using TdrSim.Core.Catalog;

namespace TdrSim.App.Plot;

/// <summary>
/// Plot colours. The TDR trace and the pulse view take theirs from the selected
/// instrument's own screen (<see cref="InstrumentDisplaySpec"/>): a lab sampling scope
/// draws yellow on black, a 1502C dark on its yellow-green LCD.
/// </summary>
public sealed record PlotTheme(
    ScottPlot.Color Background, ScottPlot.Color Trace, ScottPlot.Color Grid, ScottPlot.Color Text)
{
    /// <summary>The app's neutral scheme: an instrument without a display spec.</summary>
    public static PlotTheme Default { get; } = new(
        ScottPlot.Colors.White,
        ScottPlot.Color.FromHex("#1F77B4"),
        ScottPlot.Colors.Black.WithAlpha(0.1),
        ScottPlot.Colors.Black);

    public static PlotTheme From(InstrumentDisplaySpec? display)
    {
        if (display is null)
            return Default;
        try
        {
            return new PlotTheme(
                ScottPlot.Color.FromHex(display.Background),
                ScottPlot.Color.FromHex(display.Trace),
                ScottPlot.Color.FromHex(display.Grid),
                ScottPlot.Color.FromHex(display.Text));
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return Default; // a malformed colour must not take the plot down
        }
    }

    /// <summary>Paint the plot's surfaces and axes; traces pick up <see cref="Trace"/>
    /// where they are added.</summary>
    public void Apply(ScottPlot.Plot plot)
    {
        plot.FigureBackground.Color = Background;
        plot.DataBackground.Color = Background;
        plot.Axes.Color(Text);
        plot.Grid.MajorLineColor = Grid;
    }
}
