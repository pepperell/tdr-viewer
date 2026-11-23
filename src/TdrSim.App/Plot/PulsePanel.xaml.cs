using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TdrSim.Core.Engine;

namespace TdrSim.App.Plot;

/// <summary>
/// Plot side of the pulse view: tiles the one-period waveform "# pulses" times and
/// renders it in millivolts against nanoseconds. The view is always auto-fitted
/// (mouse pan/zoom disabled); every new waveform or pulse-count change re-ranges.
/// </summary>
public partial class PulsePanel : UserControl
{
    private PulseViewModel? _vm;
    private double[]? _plottedMillivolts; // plotted y values, in _voltUnit
    private double _dtNs;                 // sample spacing in plotted x units (_timeUnit)
    private UnitScale _timeUnit = new(1e9, "ns");
    private UnitScale _voltUnit = new(1e3, "mV");
    private ScottPlot.Plottables.VerticalLine? _hoverLine;
    private ScottPlot.Plottables.Marker? _hoverMarker;
    private double _hoverNs = double.NaN;

    public PulsePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ThePlot.UserInputProcessor.IsEnabled = false; // always-fit view: no pan/zoom
        ThePlot.PreviewMouseMove += (_, e) =>
        {
            var position = e.GetPosition(ThePlot);
            double scale = ThePlot.DisplayScale;
            var at = ThePlot.Plot.GetCoordinates(
                new ScottPlot.Pixel((float)(position.X * scale), (float)(position.Y * scale)));
            SetHover(at.X);
        };
        ThePlot.MouseLeave += (_, _) => SetHover(double.NaN);
        ThePlot.Plot.XLabel("time (ns)");
        ThePlot.Plot.YLabel("mV");
        ThePlot.Refresh();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.WaveformChanged -= Replot;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as PulseViewModel;
        if (_vm is not null)
        {
            _vm.WaveformChanged += Replot;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Changing how many pulses are shown is a view change: retile and re-fit.
        if (e.PropertyName == nameof(PulseViewModel.PulsesDisplayed))
            Replot();
    }

    private void Replot()
    {
        var plot = ThePlot.Plot;
        plot.Clear();
        _hoverLine = null;
        _hoverMarker = null;
        var theme = _vm?.Theme ?? PlotTheme.Default;
        theme.Apply(plot);

        var trace = _vm?.Trace;
        if (trace is null)
        {
            _plottedMillivolts = null;
            ThePlot.Refresh();
            return;
        }

        int count = Math.Max(1, _vm!.PulsesDisplayed);
        int m = trace.Volts.Length;

        // Axis units follow the displayed range: 30 000 ns reads as 30 µs.
        double maxVolts = 0;
        foreach (double v in trace.Volts)
            maxVolts = Math.Max(maxVolts, Math.Abs(v));
        _timeUnit = UnitScale.Time(count * trace.PeriodS);
        _voltUnit = UnitScale.Voltage(maxVolts);
        double perVolt = _voltUnit.Factor;
        plot.XLabel($"time ({_timeUnit.Unit})");
        plot.YLabel(_voltUnit.Unit);

        var millivolts = new double[count * m]; // in the plotted voltage unit
        for (int repeat = 0; repeat < count; repeat++)
        {
            for (int k = 0; k < m; k++)
                millivolts[repeat * m + k] = trace.Volts[k] * perVolt;
        }

        // Fit on the noise-free waveform plus a fixed 4-sigma allowance, so the axis
        // holds still while the noise is re-rolled.
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int k = 0; k < m; k++)
        {
            min = Math.Min(min, millivolts[k]);
            max = Math.Max(max, millivolts[k]);
        }

        // The instrument's noise floor, rolled over the whole display so each tiled
        // repetition is an independent sweep (a noisy period tiled N times would show
        // an obviously repeating pattern).
        TraceNoise.AddGaussian(millivolts, _vm.NoiseRmsVolts * perVolt, _vm.NoiseSeed);

        var signal = plot.Add.Signal(millivolts, trace.DtS * _timeUnit.Factor);
        signal.LineWidth = 2;
        signal.Color = theme.Trace;

        double pad = Math.Max((max - min) * 0.1, 1e-6 * perVolt) + 4 * _vm.NoiseRmsVolts * perVolt;
        plot.Axes.SetLimits(0, count * trace.PeriodS * _timeUnit.Factor, min - pad, max + pad);

        _plottedMillivolts = millivolts;
        _dtNs = trace.DtS * _timeUnit.Factor;
        ShowHoverCursor(); // survives the replot (noise re-rolls several times a second)
        ThePlot.Refresh();
    }

    private void SetHover(double ns)
    {
        _hoverNs = ns;
        ShowHoverCursor();
        ThePlot.Refresh();
    }

    /// <summary>The hover cursor: a dashed line under the mouse, a dot riding the
    /// waveform there, and the time/voltage in the bottom bar. Hidden off the data.</summary>
    private void ShowHoverCursor()
    {
        var plot = ThePlot.Plot;
        var ys = _plottedMillivolts;
        int k = ys is null || _dtNs <= 0 || double.IsNaN(_hoverNs) ? -1 : (int)Math.Round(_hoverNs / _dtNs);
        if (ys is null || k < 0 || k >= ys.Length)
        {
            if (_hoverLine is not null)
                plot.Remove(_hoverLine);
            if (_hoverMarker is not null)
                plot.Remove(_hoverMarker);
            _hoverLine = null;
            _hoverMarker = null;
            if (_vm is not null)
                _vm.HoverReadout = "";
            return;
        }

        double x = k * _dtNs;
        if (_hoverLine is null || _hoverMarker is null)
        {
            _hoverLine = plot.Add.VerticalLine(x);
            var mark = (_vm?.Theme ?? PlotTheme.Default).Text;
            _hoverLine.Color = mark.WithAlpha(0.6);
            _hoverLine.LineWidth = 1;
            _hoverLine.LinePattern = ScottPlot.LinePattern.Dashed;
            _hoverMarker = plot.Add.Marker(x, 0, ScottPlot.MarkerShape.FilledCircle, 8, mark);
        }

        _hoverLine.X = x;
        _hoverMarker.Location = new ScottPlot.Coordinates(x, ys[k]);
        if (_vm is not null)
        {
            _vm.HoverReadout = $"{UnitScale.FormatTime(x / _timeUnit.Factor)}  ·  "
                + UnitScale.FormatVoltage(ys[k] / _voltUnit.Factor);
        }
    }
}
