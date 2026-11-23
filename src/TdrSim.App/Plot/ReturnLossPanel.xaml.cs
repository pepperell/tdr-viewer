using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScottPlot;

namespace TdrSim.App.Plot;

/// <summary>
/// The ScottPlot side of the return-loss tab: |Gamma| in dB (0 dB = total reflection
/// at the top, a better match further down) against frequency, linear or logarithmic.
/// Always-fit like the other plots; the instrument's -3 dB bandwidth is marked because
/// everything to its right is real but nearly invisible on the TDR trace.
/// </summary>
public partial class ReturnLossPanel : UserControl
{
    private ReturnLossViewModel? _vm;
    private double _unitHz = 1e6;
    private bool _log;

    public ReturnLossPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ThePlot.UserInputProcessor.IsEnabled = false; // always-fit view: no pan/zoom
        ThePlot.Plot.YLabel("|Γ| (dB)");
        ThePlot.Plot.Axes.Top.MinimumSize = 20; // room for the bandwidth marker's label
        ThePlot.PreviewMouseMove += OnPlotMouseMove;
        ThePlot.MouseLeave += (_, _) => _vm?.SetHover(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.Changed -= Replot;
        _vm = e.NewValue as ReturnLossViewModel;
        if (_vm is not null)
            _vm.Changed += Replot;
        Replot();
    }

    private void Replot()
    {
        var plot = ThePlot.Plot;
        plot.Clear();
        if (_vm?.Curve is not { } curve || curve.FrequencyHz.Length < 2)
        {
            ThePlot.Refresh();
            return;
        }

        double fMax = curve.FrequencyHz[^1];
        _unitHz = fMax >= 2e9 ? 1e9 : 1e6;
        _log = _vm.LogFrequency;
        string unit = _unitHz == 1e9 ? "GHz" : "MHz";

        // A log axis has no DC: start at the first harmonic, at most four decades down.
        int first = _log ? Math.Max(1, curve.IndexAt(fMax * 1e-4)) : 0;
        int count = curve.FrequencyHz.Length - first;
        var xs = new double[count];
        var ys = new double[count];
        double yMin = 0;
        for (int i = 0; i < count; i++)
        {
            double f = curve.FrequencyHz[first + i] / _unitHz;
            xs[i] = _log ? Math.Log10(f) : f;
            ys[i] = curve.GammaDb[first + i];
            yMin = Math.Min(yMin, ys[i]);
        }

        var line = plot.Add.SignalXY(xs, ys);
        line.LineWidth = 2;

        double band = curve.InstrumentBandwidthHz / _unitHz;
        var marker = plot.Add.VerticalLine(_log ? Math.Log10(band) : band);
        marker.Color = Colors.OrangeRed.WithAlpha(0.8);
        marker.LineWidth = 1;
        marker.LinePattern = LinePattern.Dashed;
        marker.Text = "instrument −3 dB";
        marker.LabelOppositeAxis = true;
        marker.LabelFontSize = 10;

        if (_log)
        {
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
            {
                MinorTickGenerator = new ScottPlot.TickGenerators.LogMinorTickGenerator(),
                IntegerTicksOnly = true,
                LabelFormatter = exponent => Math.Pow(10, exponent).ToString("0.####"),
            };
        }
        else
        {
            plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        }

        plot.XLabel($"frequency ({unit})");
        double bottom = Math.Floor((yMin - 2) / 10) * 10;
        plot.Axes.SetLimits(xs[0], xs[^1], Math.Max(bottom, -TdrSim.Core.Engine.ReturnLoss.FloorDb), 3);
        ThePlot.Refresh();
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.Curve is null)
            return;
        var position = e.GetPosition(ThePlot);
        double scale = ThePlot.DisplayScale;
        var coordinates = ThePlot.Plot.GetCoordinates(
            new Pixel((float)(position.X * scale), (float)(position.Y * scale)));
        double x = _log ? Math.Pow(10, coordinates.X) : coordinates.X;
        _vm.SetHover(x * _unitHz);
    }
}
