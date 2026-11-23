using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScottPlot;
using TdrSim.Core.Engine;

namespace TdrSim.App.Plot;

/// <summary>
/// The ScottPlot side of the trace panel. The view is always auto-fitted: every new
/// trace and every mode change re-ranges both axes from the data (mouse pan/zoom is
/// disabled). Cursor dragging is our own hit-test on Preview mouse events, so it works
/// with ScottPlot's input processing off.
/// </summary>
public partial class TracePanel : UserControl
{
    private const double HitTestPx = 8;

    private TracePanelViewModel? _vm;
    private ScottPlot.Plottables.VerticalLine? _cursorA;
    private ScottPlot.Plottables.VerticalLine? _cursorB;
    private readonly List<IPlottable> _highlightPlottables = [];
    private double[]? _plottedYs;
    private char _dragging;
    private ScottPlot.Plottables.VerticalLine? _hoverLine;
    private ScottPlot.Plottables.Marker? _hoverMarker;
    private double _hoverTimeS = double.NaN;

    // Axis units follow the displayed range (30 000 ns reads as 30 µs); set per replot.
    private UnitScale _timeUnit = new(1e9, "ns");
    private UnitScale _distanceUnit = new(1, "m");
    private UnitScale _voltUnit = new(1e3, "mV");

    public TracePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ThePlot.UserInputProcessor.IsEnabled = false; // always-fit view: no pan/zoom
        ThePlot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
        ThePlot.PreviewMouseMove += OnPlotMouseMove;
        ThePlot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
        ThePlot.MouseLeave += (_, _) =>
        {
            _vm?.ClearHover();
            _hoverTimeS = double.NaN;
            ShowHoverCursor();
            ThePlot.Refresh();
        };
        ThePlot.Plot.XLabel("distance (m)");
        ThePlot.Plot.YLabel("Ω");
        ThePlot.Refresh();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.TraceChanged -= Replot;
            _vm.HighlightsChanged -= RedrawHighlights;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as TracePanelViewModel;
        if (_vm is not null)
        {
            _vm.TraceChanged += Replot;
            _vm.HighlightsChanged += RedrawHighlights;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TracePanelViewModel.YMode):
            case nameof(TracePanelViewModel.XMode):
                Replot();
                break;
            case nameof(TracePanelViewModel.DisplayVf):
                if (_vm!.DisplayVf > 0 && !double.IsNaN(_vm.DisplayVf))
                    Replot(); // ignore half-typed values; keep the last good scale
                break;
            case nameof(TracePanelViewModel.CursorsEnabled):
                ToggleCursors();
                break;
        }
    }

    // ---- export ----

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_vm?.Trace is not { } trace)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "trace",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            using var writer = new System.IO.StreamWriter(dialog.FileName);
            TraceExport.WriteCsv(writer, trace, _vm.DisplayVf);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "TDR Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportPng(object sender, RoutedEventArgs e)
    {
        if (_vm?.Trace is null)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|All files (*.*)|*.*",
            DefaultExt = ".png",
            FileName = "trace",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            double scale = ThePlot.DisplayScale;
            ThePlot.Plot.SavePng(dialog.FileName,
                Math.Max(600, (int)(ThePlot.ActualWidth * scale)),
                Math.Max(300, (int)(ThePlot.ActualHeight * scale)));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "TDR Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- plotting (always re-fitted) ----

    private void Replot()
    {
        var plot = ThePlot.Plot;
        plot.Clear();
        _cursorA = _cursorB = null;
        _hoverLine = null;
        _hoverMarker = null;
        _highlightPlottables.Clear();
        var theme = _vm?.Theme ?? PlotTheme.Default;
        theme.Apply(plot);

        var trace = _vm?.Trace;
        if (trace is null)
        {
            ThePlot.Refresh();
            return;
        }

        _timeUnit = UnitScale.Time(_vm!.DisplayEndS);
        _distanceUnit = UnitScale.Distance(_vm.DisplayEndS * MetersPerSecond());
        double maxVolts = 0;
        foreach (double v in (_vm.CleanTrace ?? trace).Volts)
            maxVolts = Math.Max(maxVolts, Math.Abs(v));
        _voltUnit = UnitScale.Voltage(maxVolts);

        // Canvas-driven shading goes underneath everything.
        AddHighlights();

        // Reference first so the live trace draws over it.
        if (_vm!.ReferenceTrace is { } reference)
        {
            (double refDx, double refX0) = XScale(reference);
            var refSignal = plot.Add.Signal(YArray(reference), refDx);
            refSignal.Data.XOffset = refX0;
            refSignal.LineWidth = 1.5f;
            refSignal.Color = theme.Text.WithAlpha(0.45);
        }

        (double dx, double x0) = XScale(trace);
        _plottedYs = YArray(trace);
        var signal = plot.Add.Signal(_plottedYs, dx);
        signal.Data.XOffset = x0;
        signal.LineWidth = 2;
        signal.Color = theme.Trace;

        AddPeelOverlay();

        plot.XLabel(_vm.XMode == TraceXMode.TimeNs
            ? $"time ({_timeUnit.Unit})"
            : $"distance ({_distanceUnit.Unit})");
        plot.YLabel(_vm.YMode switch
        {
            TraceYMode.MilliVolts => _voltUnit.Unit,
            TraceYMode.MilliRho => "mρ",
            _ => "Ω",
        });

        if (_vm.CursorsEnabled)
            AddCursorLines();

        ShowHoverCursor(); // survives the replot (noise re-rolls several times a second)
        AutoRange();
        ThePlot.Refresh();
    }

    /// <summary>The hover cursor: a dashed line under the mouse and a dot riding the
    /// trace there. Hidden when <see cref="_hoverTimeS"/> is NaN.</summary>
    private void ShowHoverCursor()
    {
        var plot = ThePlot.Plot;
        if (double.IsNaN(_hoverTimeS) || _vm?.Trace is null)
        {
            if (_hoverLine is not null)
                plot.Remove(_hoverLine);
            if (_hoverMarker is not null)
                plot.Remove(_hoverMarker);
            _hoverLine = null;
            _hoverMarker = null;
            return;
        }

        double x = XFromTime(_hoverTimeS);
        if (_hoverLine is null || _hoverMarker is null)
        {
            _hoverLine = plot.Add.VerticalLine(x);
            var mark = _vm.Theme.Text;
            _hoverLine.Color = mark.WithAlpha(0.6);
            _hoverLine.LineWidth = 1;
            _hoverLine.LinePattern = LinePattern.Dashed;
            _hoverMarker = plot.Add.Marker(x, 0, MarkerShape.FilledCircle, 8, mark);
        }

        _hoverLine.X = x;
        // YAt is in the mode's base unit (mV); the plot may be in V or µV.
        double y = _vm.YAt(_hoverTimeS)
            * (_vm.YMode == TraceYMode.MilliVolts ? _voltUnit.Factor / 1e3 : 1);
        _hoverMarker.Location = new Coordinates(x, y);
    }

    /// <summary>The de-ghosted impedance profile over the raw trace (Ω mode only):
    /// solid while trustworthy, dimmed past the point where peeling gives up.</summary>
    private void AddPeelOverlay()
    {
        if (_vm is not { YMode: TraceYMode.Ohms, Peel: { } peel } || peel.Ohms.Length < 2)
            return;

        double dx = XFromTime(peel.DtS);
        var color = Colors.DarkOrange;
        int reliable = Math.Clamp(peel.ReliableCount, 0, peel.Ohms.Length);
        if (reliable >= 2)
        {
            var solid = ThePlot.Plot.Add.Signal(peel.Ohms[..reliable], dx);
            solid.Data.XOffset = XFromTime(peel.TimeOf(0));
            solid.LineWidth = 2;
            solid.Color = color;
        }

        if (reliable < peel.Ohms.Length)
        {
            int from = Math.Max(0, reliable - 1);
            if (peel.Ohms.Length - from >= 2)
            {
                var faded = ThePlot.Plot.Add.Signal(peel.Ohms[from..], dx);
                faded.Data.XOffset = XFromTime(peel.TimeOf(from));
                faded.LineWidth = 1.5f;
                faded.Color = color.WithAlpha(0.3);
            }

            var boundary = ThePlot.Plot.Add.VerticalLine(XFromTime(peel.TimeOf(from)));
            boundary.Color = color.WithAlpha(0.7);
            boundary.LineWidth = 1;
            boundary.LinePattern = LinePattern.Dotted;
        }
    }

    // ---- cross-highlighting ----

    private void AddHighlights()
    {
        if (_vm is null || _vm.Trace is null)
            return;
        var plot = ThePlot.Plot;
        foreach (var highlight in _vm.Highlights)
        {
            var color = highlight.IsHover ? Colors.Orange : Colors.DodgerBlue;
            if (highlight.IsPoint)
            {
                var line = plot.Add.VerticalLine(XFromTime(highlight.StartS));
                line.Color = color.WithAlpha(highlight.IsHover ? 0.95 : 0.7);
                line.LineWidth = highlight.IsHover ? 2.5f : 2;
                line.LinePattern = LinePattern.Dashed;
                _highlightPlottables.Add(line);
            }
            else
            {
                var span = plot.Add.HorizontalSpan(XFromTime(highlight.StartS), XFromTime(highlight.EndS));
                span.FillStyle.Color = color.WithAlpha(highlight.IsHover ? 0.30 : 0.16);
                span.LineStyle.Color = color.WithAlpha(highlight.IsHover ? 0.9 : 0.5);
                span.LineStyle.Width = 1;
                _highlightPlottables.Add(span);
            }
        }
    }

    /// <summary>Hover/selection changed but the data did not: swap the shading in
    /// place - no refit, so the always-fit view stays put.</summary>
    private void RedrawHighlights()
    {
        foreach (var plottable in _highlightPlottables)
            ThePlot.Plot.Remove(plottable);
        _highlightPlottables.Clear();
        AddHighlights();
        ThePlot.Refresh();
    }

    private double[] YArray(TdrTrace trace) => _vm!.YMode switch
    {
        TraceYMode.MilliVolts => Scaled(trace.Volts, _voltUnit.Factor),
        TraceYMode.MilliRho => Scaled(trace.Rho, 1e3),
        _ => trace.Ohms,
    };

    private static double[] Scaled(double[] values, double factor)
    {
        var result = new double[values.Length];
        for (int k = 0; k < values.Length; k++)
            result[k] = values[k] * factor;
        return result;
    }

    /// <summary>
    /// X from just before the reference plane to a bit past the last activity (the
    /// window is ~6x the deepest round trip, so most of it is settled tail); Y per mode.
    /// </summary>
    private void AutoRange()
    {
        double xMax = XFromTime(_vm!.DisplayEndS);
        ThePlot.Plot.Axes.SetLimitsX(-0.03 * xMax, xMax);
        RefitY();
    }

    private void RefitY()
    {
        // Fit on the noise-free trace plus a fixed allowance for the noise, so the
        // axis holds still while the noise is re-rolled several times a second.
        var clean = _vm!.CleanTrace ?? _vm.Trace!;
        if (_vm.YMode == TraceYMode.Ohms)
        {
            // Ohms near an open explode (rho -> 1): pin to 4x the reference impedance.
            double zRef = clean.Ohms[0];
            ThePlot.Plot.Axes.SetLimitsY(-0.1 * zRef, 4 * zRef);
            return;
        }

        double[] cleanYs = YArray(clean);
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (double v in cleanYs)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        // The exact noise RMS in plotted units; 4 sigma covers the peaks.
        double sigma = _vm.YMode == TraceYMode.MilliVolts
            ? _voltUnit.Factor * _vm.NoiseRmsVolts
            : 1e3 * _vm.NoiseRmsRho;
        double pad = Math.Max((max - min) * 0.08, 1e-6) + 4 * sigma;
        ThePlot.Plot.Axes.SetLimitsY(min - pad, max + pad);
    }

    // ---- cursors ----

    private void ToggleCursors()
    {
        var plot = ThePlot.Plot;
        if (_vm!.CursorsEnabled && _vm.Trace is not null)
        {
            var limits = plot.Axes.GetLimits();
            double span = limits.Right - limits.Left;
            _vm.SetCursors(
                TimeFromX(limits.Left + span / 3),
                TimeFromX(limits.Left + 2 * span / 3));
            AddCursorLines();
        }
        else
        {
            if (_cursorA is not null)
                plot.Remove(_cursorA);
            if (_cursorB is not null)
                plot.Remove(_cursorB);
            _cursorA = _cursorB = null;
        }

        ThePlot.Refresh();
    }

    private void AddCursorLines()
    {
        if (double.IsNaN(_vm!.CursorATimeS) || double.IsNaN(_vm.CursorBTimeS))
            return;
        var plot = ThePlot.Plot;
        _cursorA = plot.Add.VerticalLine(XFromTime(_vm.CursorATimeS));
        _cursorA.Color = Colors.OrangeRed;
        _cursorA.LineWidth = 1.5f;
        _cursorB = plot.Add.VerticalLine(XFromTime(_vm.CursorBTimeS));
        _cursorB.Color = Colors.DeepSkyBlue;
        _cursorB.LineWidth = 1.5f;
    }

    private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is not { CursorsEnabled: true } || _cursorA is null || _cursorB is null)
            return;

        // Hit-test in coordinate space: tolerance = what 8 px spans at the current fit.
        var pixel = PixelOf(e);
        var at = ThePlot.Plot.GetCoordinates(pixel);
        var offset = ThePlot.Plot.GetCoordinates(
            new Pixel(pixel.X + (float)(HitTestPx * ThePlot.DisplayScale), pixel.Y));
        double tolerance = Math.Abs(offset.X - at.X);
        double distA = Math.Abs(at.X - _cursorA.X);
        double distB = Math.Abs(at.X - _cursorB.X);
        if (Math.Min(distA, distB) > tolerance)
            return;

        _dragging = distA <= distB ? 'A' : 'B';
        ThePlot.CaptureMouse();
        e.Handled = true;
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.Trace is not { } trace)
            return;

        if (_dragging == '\0')
        {
            // Not dragging a cursor: report what the trace under the mouse is.
            var pixel = PixelOf(e);
            var at = ThePlot.Plot.GetCoordinates(pixel);
            var offset = ThePlot.Plot.GetCoordinates(
                new Pixel(pixel.X + (float)(HitTestPx * ThePlot.DisplayScale), pixel.Y));
            double hoverS = TimeFromX(at.X);
            _hoverTimeS = hoverS >= trace.TimeS[0] && hoverS <= trace.TimeS[^1] ? hoverS : double.NaN;
            _vm.SetHover(hoverS, Math.Abs(TimeFromX(offset.X) - hoverS));
            ShowHoverCursor();
            ThePlot.Refresh();
            return;
        }

        var coordinates = ThePlot.Plot.GetCoordinates(PixelOf(e));
        double timeS = Math.Clamp(TimeFromX(coordinates.X), trace.TimeS[0], trace.TimeS[^1]);
        _vm.SetCursor(_dragging == 'A', timeS);
        var line = _dragging == 'A' ? _cursorA : _cursorB;
        if (line is not null)
            line.X = XFromTime(timeS);
        ThePlot.Refresh();
        e.Handled = true;
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging == '\0')
            return;
        _dragging = '\0';
        ThePlot.ReleaseMouseCapture();
        e.Handled = true;
    }

    private Pixel PixelOf(MouseEventArgs e)
    {
        var position = e.GetPosition(ThePlot);
        double scale = ThePlot.DisplayScale;
        return new Pixel((float)(position.X * scale), (float)(position.Y * scale));
    }

    // ---- unit conversions (cursors are stored in displayed time) ----

    private double MetersPerSecond() => _vm!.DisplayVf * ComplexMath.C0 / 2;

    /// <summary>Plotted x units per second of trace time, in the current x mode.</summary>
    private double XPerSecond() => _vm!.XMode == TraceXMode.TimeNs
        ? _timeUnit.Factor
        : MetersPerSecond() * _distanceUnit.Factor;

    private (double Dx, double X0) XScale(TdrTrace trace)
        => (trace.Grid.DtS * XPerSecond(), trace.TimeS[0] * XPerSecond());

    private double XFromTime(double timeS) => timeS * XPerSecond();

    private double TimeFromX(double x) => x / XPerSecond();
}
