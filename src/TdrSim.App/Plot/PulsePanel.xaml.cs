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

    public PulsePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ThePlot.UserInputProcessor.IsEnabled = false; // always-fit view: no pan/zoom
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

        var trace = _vm?.Trace;
        if (trace is null)
        {
            ThePlot.Refresh();
            return;
        }

        int count = Math.Max(1, _vm!.PulsesDisplayed);
        int m = trace.Volts.Length;
        var millivolts = new double[count * m];
        for (int repeat = 0; repeat < count; repeat++)
        {
            for (int k = 0; k < m; k++)
                millivolts[repeat * m + k] = trace.Volts[k] * 1e3;
        }

        // The instrument's noise floor, rolled over the whole display so each tiled
        // repetition is an independent sweep (a noisy period tiled N times would show
        // an obviously repeating pattern).
        TraceNoise.AddGaussian(millivolts, _vm.NoiseRmsVolts * 1e3, Random.Shared.Next());

        var signal = plot.Add.Signal(millivolts, trace.DtS * 1e9);
        signal.LineWidth = 2;

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (double v in millivolts)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        double pad = Math.Max((max - min) * 0.1, 1e-3);
        plot.Axes.SetLimits(0, count * trace.PeriodS * 1e9, min - pad, max + pad);
        ThePlot.Refresh();
    }
}
