using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TdrSim.Core.Engine;

namespace TdrSim.App.Plot;

/// <summary>A probe choice in the pulse view, keyed by the canvas node sitting on the net.</summary>
public sealed record PulseProbe(string Label, string DocumentNodeId)
{
    public override string ToString() => Label;
}

/// <summary>
/// Pulse-train view state: stimulus parameters (rise/fall/rate/duty), the probe point,
/// the number of pulses displayed (pure view tiling - no recompute), and the latest
/// one-period waveform. The shell owns the recompute pipeline.
/// </summary>
public sealed partial class PulseViewModel : ObservableObject
{
    [ObservableProperty]
    private double _riseTimePs = 200;

    [ObservableProperty]
    private double _fallTimePs = 200;

    [ObservableProperty]
    private double _rateMegahertz = 1;

    [ObservableProperty]
    private double _dutyPercent = 50;

    /// <summary>View setting: how many repetitions of the period are tiled.</summary>
    [ObservableProperty]
    private int _pulsesDisplayed = 3;

    [ObservableProperty]
    private PulseProbe? _selectedProbe;

    [ObservableProperty]
    private string _info = "";

    public IReadOnlyList<int> PulseCountOptions { get; } = [1, 2, 3, 5, 10];

    public ObservableCollection<PulseProbe> Probes { get; } = [];

    public PulseTrace? Trace { get; private set; }

    /// <summary>Effective display-noise RMS (volts, averaging applied) resolved from
    /// the instrument; the view adds it per displayed sweep.</summary>
    public double NoiseRmsVolts { get; private set; }

    /// <summary>A new waveform arrived - the view replots (always re-fitted).</summary>
    public event Action? WaveformChanged;

    public void SetTrace(PulseTrace trace, double noiseRmsVolts)
    {
        Trace = trace;
        NoiseRmsVolts = noiseRmsVolts;
        WaveformChanged?.Invoke();
    }

    /// <summary>Rebuilt on every recompile; the selection survives by node id, and
    /// the default is the first non-port probe ("at the load").</summary>
    public void UpdateProbes(IReadOnlyList<PulseProbe> probes)
    {
        string? selectedId = SelectedProbe?.DocumentNodeId;
        Probes.Clear();
        foreach (var probe in probes)
            Probes.Add(probe);
        SelectedProbe = Probes.FirstOrDefault(p => p.DocumentNodeId == selectedId)
            ?? Probes.Skip(1).FirstOrDefault()
            ?? Probes.FirstOrDefault();
    }
}
