using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdrSim.Core.Engine;

namespace TdrSim.App.Plot;

public enum TraceYMode
{
    MilliVolts,
    MilliRho,
    Ohms,
}

public enum TraceXMode
{
    TimeNs,
    DistanceM,
}

/// <summary>
/// Display state for the trace panel: the latest trace, axis modes, cursors, warnings.
/// Axis-mode switches replot from the stored arrays - never a recompute. The view
/// (TracePanel.xaml.cs) owns the ScottPlot calls and listens to <see cref="TraceChanged"/>
/// / property changes; cursors live here in displayed time so unit switches don't move
/// them physically.
/// </summary>
public sealed partial class TracePanelViewModel : ObservableObject
{
    private bool _vfInitialized;

    [ObservableProperty]
    private TraceYMode _yMode = TraceYMode.Ohms;

    [ObservableProperty]
    private TraceXMode _xMode = TraceXMode.DistanceM;

    /// <summary>Display-only velocity factor for the distance axis (PLAN §3.1 "VF choice").</summary>
    [ObservableProperty]
    private double _displayVf = 1.0;

    [ObservableProperty]
    private bool _cursorsEnabled;

    /// <summary>Settled/compromised warning; final traces only (previews never flicker it).</summary>
    [ObservableProperty]
    private string _warning = "";

    /// <summary>Progress shimmer, set by the shell when a run exceeds 200 ms.</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _cursorReadout = "";

    public TdrTrace? Trace { get; private set; }

    public bool IsPreview { get; private set; }

    public bool HasTrace => Trace is not null;

    /// <summary>Session-only snapshot rendered dimmed behind the live trace.</summary>
    public TdrTrace? ReferenceTrace { get; private set; }

    public bool HasReference => ReferenceTrace is not null;

    /// <summary>Cursor positions in displayed time, seconds; NaN until placed.</summary>
    public double CursorATimeS { get; private set; } = double.NaN;

    public double CursorBTimeS { get; private set; } = double.NaN;

    /// <summary>New trace data arrived - the view replots (always re-fitted).</summary>
    public event Action? TraceChanged;

    public void SetTrace(TdrTrace trace, bool isPreview)
    {
        bool firstTrace = Trace is null;
        Trace = trace;
        IsPreview = isPreview;
        if (firstTrace)
        {
            OnPropertyChanged(nameof(HasTrace));
            SetReferenceCommand.NotifyCanExecuteChanged();
        }

        if (!_vfInitialized)
        {
            _vfInitialized = true;
            DisplayVf = trace.DisplayVelocityFactor;
        }

        if (!isPreview)
        {
            Warning = (trace.Settled ? "" : "not settled - reflections continue past the window")
                + (trace.Settled || !trace.Grid.Compromised ? "" : "  ·  ")
                + (trace.Grid.Compromised ? "grid compromised - edge resolution reduced" : "");
        }

        TraceChanged?.Invoke();
        UpdateCursorReadout();
    }

    /// <summary>Snapshot whatever is displayed right now (noise and all) - like
    /// storing a reference waveform on a real scope.</summary>
    [RelayCommand(CanExecute = nameof(HasTrace))]
    private void SetReference()
    {
        ReferenceTrace = Trace;
        OnPropertyChanged(nameof(HasReference));
        ClearReferenceCommand.NotifyCanExecuteChanged();
        TraceChanged?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(HasReference))]
    private void ClearReference()
    {
        ReferenceTrace = null;
        OnPropertyChanged(nameof(HasReference));
        ClearReferenceCommand.NotifyCanExecuteChanged();
        TraceChanged?.Invoke();
    }

    public void SetCursor(bool isA, double timeS)
    {
        if (isA)
            CursorATimeS = timeS;
        else
            CursorBTimeS = timeS;
        UpdateCursorReadout();
    }

    public void SetCursors(double aTimeS, double bTimeS)
    {
        CursorATimeS = aTimeS;
        CursorBTimeS = bTimeS;
        UpdateCursorReadout();
    }

    partial void OnYModeChanged(TraceYMode value) => UpdateCursorReadout();

    partial void OnDisplayVfChanged(double value) => UpdateCursorReadout();

    partial void OnCursorsEnabledChanged(bool value) => UpdateCursorReadout();

    private string YUnit => YMode switch
    {
        TraceYMode.MilliVolts => "mV",
        TraceYMode.MilliRho => "mρ",
        _ => "Ω",
    };

    private void UpdateCursorReadout()
    {
        if (!CursorsEnabled || Trace is null
            || double.IsNaN(CursorATimeS) || double.IsNaN(CursorBTimeS))
        {
            CursorReadout = "";
            return;
        }

        double ya = SampleY(CursorATimeS);
        double yb = SampleY(CursorBTimeS);
        string deltaY = IsOffScale(ya) || IsOffScale(yb) ? "—" : FormatY(yb - ya);
        CursorReadout =
            $"A  {Describe(CursorATimeS, FormatY(ya))}      B  {Describe(CursorBTimeS, FormatY(yb))}      "
            + $"Δ  {Describe(CursorBTimeS - CursorATimeS, deltaY)}";
    }

    private string Describe(double timeS, string yText)
        => $"{timeS * 1e9:0.##} ns · {DistanceOf(timeS):0.###} m · {yText} {YUnit}";

    /// <summary>Clamped rho near ±1 makes ohms explode; a real TDR reads "open" there.</summary>
    private bool IsOffScale(double y) => YMode == TraceYMode.Ohms && Math.Abs(y) >= 1e5;

    private string FormatY(double y) => IsOffScale(y) ? "∞" : $"{y:0.###}";

    /// <summary>Distance for the readout (both units always shown - distance-to-fault workflow).</summary>
    private double DistanceOf(double timeS) => DisplayVf * ComplexMath.C0 * timeS / 2;

    /// <summary>Y of the nearest trace sample in the current y mode.</summary>
    private double SampleY(double timeS)
    {
        var trace = Trace!;
        int k = (int)Math.Round((timeS + trace.EdgeDelayS) / trace.Grid.DtS);
        k = Math.Clamp(k, 0, trace.Rho.Length - 1);
        return YMode switch
        {
            TraceYMode.MilliVolts => trace.Volts[k] * 1e3,
            TraceYMode.MilliRho => trace.Rho[k] * 1e3,
            _ => trace.Ohms[k],
        };
    }
}
