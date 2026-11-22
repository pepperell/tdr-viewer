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
    private double[]? _plottedYs;
    private char _dragging;

    public TracePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ThePlot.UserInputProcessor.IsEnabled = false; // always-fit view: no pan/zoom
        ThePlot.PreviewMouseLeftButtonDown += OnPlotMouseDown;
        ThePlot.PreviewMouseMove += OnPlotMouseMove;
        ThePlot.PreviewMouseLeftButtonUp += OnPlotMouseUp;
        ThePlot.Plot.XLabel("distance (m)");
        ThePlot.Plot.YLabel("Ω");
        ThePlot.Refresh();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.TraceChanged -= Replot;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as TracePanelViewModel;
        if (_vm is not null)
        {
            _vm.TraceChanged += Replot;
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

        var trace = _vm?.Trace;
        if (trace is null)
        {
            ThePlot.Refresh();
            return;
        }

        // Reference first so the live trace draws over it.
        if (_vm!.ReferenceTrace is { } reference)
        {
            (double refDx, double refX0) = XScale(reference);
            var refSignal = plot.Add.Signal(YArray(reference), refDx);
            refSignal.Data.XOffset = refX0;
            refSignal.LineWidth = 1.5f;
            refSignal.Color = Colors.Gray.WithAlpha(0.55);
        }

        (double dx, double x0) = XScale(trace);
        _plottedYs = YArray(trace);
        var signal = plot.Add.Signal(_plottedYs, dx);
        signal.Data.XOffset = x0;
        signal.LineWidth = 2;

        plot.XLabel(_vm.XMode == TraceXMode.TimeNs ? "time (ns)" : "distance (m)");
        plot.YLabel(_vm.YMode switch
        {
            TraceYMode.MilliVolts => "mV",
            TraceYMode.MilliRho => "mρ",
            _ => "Ω",
        });

        if (_vm.CursorsEnabled)
            AddCursorLines();

        AutoRange();
        ThePlot.Refresh();
    }

    private double[] YArray(TdrTrace trace) => _vm!.YMode switch
    {
        TraceYMode.MilliVolts => Scaled(trace.Volts, 1e3),
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
        var trace = _vm!.Trace!;
        (double dx, double x0) = XScale(trace);

        double settledRho = trace.Rho[^1];
        int last = trace.Rho.Length - 1;
        while (last > 1 && Math.Abs(trace.Rho[last] - settledRho) < 1e-3)
            last--;
        int idx = Math.Min(last + 16, trace.Rho.Length - 1);
        double xMax = Math.Max((x0 + dx * idx) * 1.15, (x0 + dx * (trace.Rho.Length - 1)) * 0.02);
        ThePlot.Plot.Axes.SetLimitsX(-0.03 * xMax, xMax);
        RefitY();
    }

    private void RefitY()
    {
        var trace = _vm!.Trace!;
        if (_vm.YMode == TraceYMode.Ohms)
        {
            // Ohms near an open explode (rho -> 1): pin to 4x the reference impedance.
            double zRef = trace.Ohms[0];
            ThePlot.Plot.Axes.SetLimitsY(-0.1 * zRef, 4 * zRef);
            return;
        }

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (double v in _plottedYs!)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        double pad = Math.Max((max - min) * 0.08, 1e-6);
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
        if (_dragging == '\0' || _vm?.Trace is not { } trace)
            return;

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

    private (double Dx, double X0) XScale(TdrTrace trace) => _vm!.XMode == TraceXMode.TimeNs
        ? (trace.Grid.DtS * 1e9, trace.TimeS[0] * 1e9)
        : (trace.Grid.DtS * MetersPerSecond(), trace.TimeS[0] * MetersPerSecond());

    private double XFromTime(double timeS)
        => _vm!.XMode == TraceXMode.TimeNs ? timeS * 1e9 : timeS * MetersPerSecond();

    private double TimeFromX(double x)
        => _vm!.XMode == TraceXMode.TimeNs ? x * 1e-9 : x / MetersPerSecond();
}
