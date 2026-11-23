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

/// <summary>A stretch (or, when <see cref="EndS"/> == <see cref="StartS"/>, a point)
/// of displayed trace time to shade: the first-pass footprint of a canvas node.</summary>
public sealed record TraceHighlight(double StartS, double EndS, bool IsHover)
{
    public bool IsPoint => EndS <= StartS;
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

    /// <summary>Layer-peeling overlay (drawn in the Ω mode only).</summary>
    [ObservableProperty]
    private bool _peelEnabled;

    /// <summary>Where and why the peeled profile stops being trustworthy.</summary>
    [ObservableProperty]
    private string _peelCaption = "";

    /// <summary>What the trace under the mouse is, from first-pass path delays.</summary>
    [ObservableProperty]
    private string _hoverReadout = "";

    private TraceFeatureMap? _featureMap;
    private Func<string, string> _titleOf = id => id;
    private IReadOnlyList<string> _hoverNodes = [];

    /// <summary>Peeled impedance profile of the displayed trace; null when off.</summary>
    public PeelResult? Peel { get; private set; }

    /// <summary>Canvas-driven shading (hovered and selected nodes).</summary>
    public IReadOnlyList<TraceHighlight> Highlights { get; private set; } = [];

    /// <summary>The shading changed but the data did not: redraw without refitting.</summary>
    public event Action? HighlightsChanged;

    /// <summary>The set of nodes the hovered trace point belongs to changed.</summary>
    public event Action<IReadOnlyList<string>>? HoverNodesChanged;

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

    /// <summary>Displayed x extent in trace time (see <see cref="DisplayEndTimeS"/>),
    /// measured on the noise-free trace so display noise cannot hide the quiet tail.</summary>
    public double DisplayEndS { get; private set; }

    private PlotTheme _theme = PlotTheme.Default;

    /// <summary>Plot colours - the selected instrument's own screen.</summary>
    public PlotTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value)
                return;
            _theme = value;
            TraceChanged?.Invoke();
        }
    }

    /// <summary>The displayed trace before display noise: the view fits its y axis
    /// on this so re-rolled noise cannot make the axis jitter.</summary>
    public TdrTrace? CleanTrace { get; private set; }

    /// <summary>Exact RMS of the display noise, for a fixed y-axis allowance.</summary>
    public double NoiseRmsVolts { get; private set; }

    public double NoiseRmsRho { get; private set; }

    /// <param name="clean">The same trace before display noise; defaults to
    /// <paramref name="trace"/>.</param>
    /// <param name="noiseRmsVolts">Effective RMS of the display noise on
    /// <paramref name="trace"/>, 0 when there is none.</param>
    /// <param name="stepAmplitudeVolts">Converts that RMS to rho.</param>
    public void SetTrace(
        TdrTrace trace, bool isPreview, TdrTrace? clean = null,
        double noiseRmsVolts = 0, double stepAmplitudeVolts = 1)
    {
        NoiseRmsVolts = noiseRmsVolts;
        NoiseRmsRho = stepAmplitudeVolts > 0 ? noiseRmsVolts / stepAmplitudeVolts : 0;
        bool firstTrace = Trace is null;
        Trace = trace;
        CleanTrace = clean ?? trace;
        IsPreview = isPreview;
        DisplayEndS = Math.Min(
            DisplayEndTimeS(clean ?? trace, _featureMap?.DeepestS ?? 0), trace.TimeS[^1]);
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

        UpdatePeel();

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

    // ---- M6: layer peeling ----

    partial void OnPeelEnabledChanged(bool value)
    {
        UpdatePeel();
        TraceChanged?.Invoke();
    }

    private void UpdatePeel()
    {
        if (!PeelEnabled || Trace is null)
        {
            Peel = null;
            PeelCaption = "";
            return;
        }

        var peel = Peel = LayerPeeling.Peel(Trace, DisplayEndS);
        if (peel.ReliableCount >= peel.Ohms.Length)
        {
            PeelCaption = "peeled Z: lossless layers assumed - loss reads as rising Z, a branch as Z in parallel";
            return;
        }

        double timeS = peel.TimeOf(Math.Max(0, peel.ReliableCount - 1));
        PeelCaption = $"peeled Z unreliable beyond {UnitScale.FormatTime(timeS)} · {UnitScale.FormatDistance(DistanceOf(timeS))} - "
            + (peel.Stop == PeelStop.Unstable
                ? "not a lossless layered line here"
                : "nearly total reflection, nothing deeper is recoverable");
    }

    /// <summary>
    /// The displayed x extent in trace time: a bit past the last real edge - the last
    /// place the trace moves by 2 mrho or more within two rise times - and never short
    /// of <paramref name="floorS"/> (the deepest component's first-pass round trip).
    /// Slow settling (loss creep, ever-smaller re-reflections) is deliberately not
    /// "activity": it used to stretch the axis to several times the network's length.
    /// Shared by the plot's auto-range, the peel depth and the bounce diagram so all
    /// three agree. Run it on a noise-free trace.
    /// </summary>
    public static double DisplayEndTimeS(TdrTrace trace, double floorS = 0)
    {
        const double edgeRho = 2e-3;
        int n = trace.Rho.Length;
        double dt = n > 1 ? trace.TimeS[1] - trace.TimeS[0] : 0;
        double riseS = trace.EdgeDelayS / 10; // the simulator places the edge 10 rise times in
        int window = dt > 0
            ? Math.Clamp((int)Math.Ceiling(2 * riseS / dt), 2, Math.Max(2, n / 4))
            : 2;
        int last = n - 1;
        while (last > window && Math.Abs(trace.Rho[last] - trace.Rho[last - window]) < edgeRho)
            last--;
        double endS = trace.TimeS[Math.Min(last + window, n - 1)];
        return Math.Max(Math.Max(endS, floorS) * 1.15, trace.TimeS[^1] * 0.02);
    }

    // ---- M6: cross-highlighting ----

    /// <summary>New first-pass map (every recompile); titles resolve node ids for the
    /// hover readout at the moment they are shown.</summary>
    public void SetFeatureMap(TraceFeatureMap? map, Func<string, string> titleOf)
    {
        _featureMap = map;
        _titleOf = titleOf;
    }

    public void SetHighlights(IReadOnlyList<TraceHighlight> highlights)
    {
        if (highlights.SequenceEqual(Highlights))
            return;
        Highlights = highlights;
        HighlightsChanged?.Invoke();
    }

    /// <summary>The mouse is over the plot at displayed time <paramref name="timeS"/>;
    /// <paramref name="pixelToleranceS"/> is what a few pixels span in time.</summary>
    public void SetHover(double timeS, double pixelToleranceS)
    {
        if (_featureMap is null || Trace is null)
        {
            ClearHover();
            return;
        }

        // Lumped parts are smeared over a rise time on the trace (EdgeDelayS = 10 rise).
        double tolerance = Math.Max(pixelToleranceS, Trace.EdgeDelayS / 10);
        IReadOnlyList<string> nodes = timeS < -tolerance ? [] : _featureMap.At(Math.Max(timeS, 0), tolerance);
        string where = Describe(timeS, FormatY(SampleY(timeS)));
        if (nodes.Count > 0)
            HoverReadout = $"{where}  →  {string.Join("  +  ", nodes.Select(_titleOf))}";
        else if (timeS > _featureMap.DeepestS)
            HoverReadout = $"{where}  →  re-reflections only - no component is this deep on first pass";
        else
            HoverReadout = where;
        PublishHoverNodes(nodes);
    }

    /// <summary>Y of the displayed trace at <paramref name="timeS"/> in the current
    /// y mode - where the hover cursor's marker sits.</summary>
    public double YAt(double timeS) => SampleY(timeS);

    public void ClearHover()
    {
        HoverReadout = "";
        PublishHoverNodes([]);
    }

    private void PublishHoverNodes(IReadOnlyList<string> nodes)
    {
        if (nodes.SequenceEqual(_hoverNodes))
            return;
        _hoverNodes = nodes;
        HoverNodesChanged?.Invoke(nodes);
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
        string deltaY = IsOffScale(ya) || IsOffScale(yb) ? $"— {YUnit}" : FormatY(yb - ya);
        CursorReadout =
            $"A  {Describe(CursorATimeS, FormatY(ya))}      B  {Describe(CursorBTimeS, FormatY(yb))}      "
            + $"Δ  {Describe(CursorBTimeS - CursorATimeS, deltaY)}";
    }

    private string Describe(double timeS, string yText)
        => $"{UnitScale.FormatTime(timeS)} · {UnitScale.FormatDistance(DistanceOf(timeS))} · {yText}";

    /// <summary>Clamped rho near ±1 makes ohms explode; a real TDR reads "open" there.</summary>
    private bool IsOffScale(double y) => YMode == TraceYMode.Ohms && Math.Abs(y) >= 1e5;

    /// <summary>A y value with its unit; volts pick their own prefix (µV / mV / V).</summary>
    private string FormatY(double y)
    {
        if (IsOffScale(y))
            return $"∞ {YUnit}";
        return YMode == TraceYMode.MilliVolts ? UnitScale.FormatVoltage(y * 1e-3) : $"{y:0.###} {YUnit}";
    }

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
