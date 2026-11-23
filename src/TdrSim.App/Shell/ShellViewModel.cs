using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TdrSim.App.Editor;
using TdrSim.App.Plot;
using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Project;

namespace TdrSim.App.Shell;

/// <summary>One toolbar chip per distinct issue kind, plus an informational chip
/// listing the automatically inserted connector hardware.</summary>
public sealed record IssueBadge(string Label, string Tooltip, bool IsFatal, bool IsInfo = false);

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ComponentCatalog _catalog;
    private readonly SimulationSession _session = new();
    private readonly LiveTraceRunner _runner;
    private readonly DispatcherTimer _shimmerTimer;
    private readonly UndoStack _undoStack = new();
    private readonly DispatcherTimer _undoCommitTimer;
    private readonly Random _noiseRandom = new();
    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _noiseTimer;
    private int _shotSeed;
    private LiveTraceResult? _lastResult;
    private CancellationTokenSource? _pulseCts;
    private int _pulseVersion;
    private string? _filePath;
    private bool _isDirty;
    private bool _loading;
    private DocumentCompilation? _compilation;
    private TraceFeatureMap? _featureMap;
    private IReadOnlyList<string> _traceHoverNodes = [];
    private IReadOnlyList<string> _bounceHoverNodes = [];

    public ShellViewModel()
    {
        _catalog = CatalogLoader.LoadEmbedded();
        Instrument = new InstrumentViewModel(_catalog);
        Editor = new EditorViewModel(_catalog, Instrument);
        Palette = new PaletteViewModel(_catalog, Editor);
        TracePanel = new TracePanelViewModel();
        Pulse = new PulseViewModel();
        Bounce = new BounceViewModel { TitleOf = NodeTitle };
        ReturnLossView = new ReturnLossViewModel();

        // Cross-highlighting: the trace (or a bounce ray) points at canvas nodes;
        // hovered/selected canvas nodes shade their stretch of the trace.
        TracePanel.HoverNodesChanged += nodes =>
        {
            _traceHoverNodes = nodes;
            ApplyNodeGlow();
        };
        Bounce.HoverNodesChanged += nodes =>
        {
            _bounceHoverNodes = nodes;
            ApplyNodeGlow();
        };

        // Pulse view recompute: 30 ms debounce, latest-wins; parameters and probe
        // changes reschedule, "# pulses" is view-only tiling and does not.
        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseTimer.Stop();
            RunPulse();
        };
        Pulse.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PulseViewModel.RiseTimePs)
                or nameof(PulseViewModel.FallTimePs)
                or nameof(PulseViewModel.RateMegahertz)
                or nameof(PulseViewModel.DutyPercent)
                or nameof(PulseViewModel.SelectedProbe))
            {
                SchedulePulse();
            }
        };

        // Shimmer only when a run exceeds 200 ms (one-shot; results stop it first).
        _shimmerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _shimmerTimer.Tick += (_, _) =>
        {
            _shimmerTimer.Stop();
            TracePanel.IsBusy = true;
        };

        // Undo coalescing: a mutation burst (slider/node drag) becomes one step when
        // it has been quiet for 400 ms.
        _undoCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _undoCommitTimer.Tick += (_, _) =>
        {
            _undoCommitTimer.Stop();
            CommitUndoSnapshot();
        };

        _runner = new LiveTraceRunner(_session);
        _runner.TraceReady += result => RunOnUi(() => PublishTrace(result));
        _runner.RunStarted += () => RunOnUi(() =>
        {
            _shimmerTimer.Stop();
            _shimmerTimer.Start();
        });
        _runner.RunFailed += e => RunOnUi(() =>
        {
            _shimmerTimer.Stop();
            TracePanel.IsBusy = false;
            Status = "Simulation failed: " + e.Message;
        });

        Editor.GraphChanged += (_, _) => OnDocumentMutated();
        Instrument.PropertyChanged += (_, _) => OnDocumentMutated();
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.SelectedNode))
                UpdateInspector();
            if (e.PropertyName == nameof(EditorViewModel.HoveredNode))
                UpdateTraceHighlights();
        };
        Editor.SelectedNodes.CollectionChanged += (_, _) => UpdateTraceHighlights();

        // Sweep mode (display noise only): normal re-rolls the noise at the chosen
        // rate like a free-running sweep; one shot holds one seed until "New shot".
        _shotSeed = _noiseRandom.Next();
        _noiseTimer = new DispatcherTimer();
        _noiseTimer.Tick += (_, _) => RedrawNoise();
        Instrument.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InstrumentViewModel.OneShot)
                or nameof(InstrumentViewModel.NoiseRefreshHz))
            {
                UpdateNoiseTimer();
            }
        };
        Instrument.ShotRequested += () =>
        {
            _shotSeed = _noiseRandom.Next();
            RedrawNoise();
        };

        // The plots wear the selected instrument's own screen colours.
        Instrument.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstrumentViewModel.SelectedPreset))
                ApplyPlotTheme();
        };

        LoadIntoEditor(ProjectSerializer.LoadEmbeddedDefault(), null);
        UpdateInspector();
        UpdateNoiseTimer();
        ApplyPlotTheme();
    }

    private void ApplyPlotTheme()
    {
        var theme = PlotTheme.From(Instrument.SelectedPreset?.Display);
        TracePanel.Theme = theme;
        Pulse.Theme = theme;
    }

    private int NextNoiseSeed() => Instrument.OneShot ? _shotSeed : _noiseRandom.Next();

    private void UpdateNoiseTimer()
    {
        _noiseTimer.Stop();
        if (Instrument.OneShot)
        {
            RedrawNoise(); // settle on the held acquisition straight away
            return;
        }

        _noiseTimer.Interval = TimeSpan.FromSeconds(1 / Instrument.NoiseRefreshHz);
        _noiseTimer.Start();
    }

    /// <summary>Same simulation, a fresh acquisition's noise (or the held one-shot
    /// noise): no solver work, just the display stage.</summary>
    private void RedrawNoise()
    {
        if (_compilation?.Noise is null)
            return;
        if (_lastResult is { } result)
            ShowTrace(result);
        Pulse.SetNoiseSeed(NextNoiseSeed());
    }

    private void ShowTrace(LiveTraceResult result)
    {
        var displayed = result.Trace;
        double noiseRmsVolts = 0;
        double amplitudeVolts = 1;
        if (_compilation is { Noise: { } noise, Instrument: { } instrument })
        {
            displayed = TraceNoise.Apply(
                displayed, noise, instrument.StepAmplitudeVolts, instrument.SourceImpedanceOhms,
                NextNoiseSeed());
            noiseRmsVolts = noise.EffectiveRmsVolts;
            amplitudeVolts = instrument.StepAmplitudeVolts;
        }

        TracePanel.SetTrace(
            displayed, result.IsPreview, clean: result.Trace, noiseRmsVolts, amplitudeVolts);
    }

    public EditorViewModel Editor { get; }

    public PaletteViewModel Palette { get; }

    public InstrumentViewModel Instrument { get; }

    public TracePanelViewModel TracePanel { get; }

    public PulseViewModel Pulse { get; }

    public BounceViewModel Bounce { get; }

    public ReturnLossViewModel ReturnLossView { get; }

    private const int BounceViewIndex = 1;
    private const int ReturnLossViewIndex = 2;

    /// <summary>Left plot pane: 0 = TDR trace, 1 = bounce diagram, 2 = return loss;
    /// the last two are computed only while visible. The pulse view has its own pane
    /// beside it and is always live.</summary>
    [ObservableProperty]
    private int _activeViewIndex;

    partial void OnActiveViewIndexChanged(int value)
    {
        if (value == BounceViewIndex)
            UpdateBounce();
        if (value == ReturnLossViewIndex)
            UpdateReturnLoss();
    }

    public IReadOnlyList<ExampleProject> Examples { get; } = ExampleProjects.All;

    public ObservableCollection<IssueBadge> Badges { get; } = [];

    /// <summary>Stops the live pipeline; call once when the window closes.</summary>
    public void Shutdown()
    {
        _noiseTimer.Stop();
        _runner.Dispose();
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    [ObservableProperty]
    private string _title = "TDR Viewer";

    [ObservableProperty]
    private string _status = "Ready.";

    /// <summary>The selected node, or the instrument panel when nothing is selected.</summary>
    [ObservableProperty]
    private object? _inspectorContent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isSimulating;

    /// <summary>Edits recompute the trace automatically; off = M3's manual mode.</summary>
    [ObservableProperty]
    private bool _isLive = true;

    partial void OnIsLiveChanged(bool value)
    {
        if (value)
            SubmitLive();
    }

    private string FileDisplayName
        => _filePath is null ? "untitled" : Path.GetFileNameWithoutExtension(_filePath);

    private const string DialogFilter =
        "TDR Viewer project (*.tdrproj)|*.tdrproj|All files (*.*)|*.*";

    // ---- file commands ----

    [RelayCommand]
    private void New()
    {
        if (!ConfirmDiscard())
            return;
        LoadIntoEditor(NewDocument(), null);
    }

    [RelayCommand]
    private void Open()
    {
        if (!ConfirmDiscard())
            return;
        var dialog = new OpenFileDialog
        {
            Filter = DialogFilter,
            DefaultExt = ProjectSerializer.FileExtension,
        };
        if (dialog.ShowDialog() != true)
            return;
        OpenPath(dialog.FileName);
    }

    /// <summary>Load a built-in example as an unsaved document.</summary>
    [RelayCommand]
    private void OpenExample(ExampleProject? example)
    {
        if (example is null || !ConfirmDiscard())
            return;
        LoadIntoEditor(example.Document, null);
        Status = $"Loaded example: {example.Name}";
    }

    /// <summary>Open a project file directly (Open dialog, command-line argument).</summary>
    public void OpenPath(string path)
    {
        try
        {
            LoadIntoEditor(ProjectSerializer.Deserialize(File.ReadAllText(path)), path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(e.Message, "TDR Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Save() => SaveTo(_filePath ?? AskSavePath());

    [RelayCommand]
    private void SaveAs() => SaveTo(AskSavePath());

    /// <summary>False cancels the pending New/Open/close.</summary>
    public bool ConfirmDiscard()
    {
        if (!_isDirty)
            return true;
        var result = MessageBox.Show($"Save changes to {FileDisplayName}?", "TDR Viewer",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
            return false;
        if (result == MessageBoxResult.Yes)
        {
            Save();
            return !_isDirty; // canceled save dialog keeps the document open
        }

        return true;
    }

    private string? AskSavePath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = DialogFilter,
            DefaultExt = ProjectSerializer.FileExtension,
            FileName = FileDisplayName,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void SaveTo(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.WriteAllText(path,
                ProjectSerializer.Serialize(Editor.ToDocument(Instrument.ToDocument())));
            _filePath = path;
            _isDirty = false;
            UpdateTitle();
            Status = $"Saved {FileDisplayName}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(e.Message, "TDR Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static ProjectDocument NewDocument() => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = new InstrumentDocument { PresetId = "tek-1502c" },
        Nodes = [new DocumentNode { Id = "tdr", Kind = DocumentNodeKind.Tdr, X = 60, Y = 200 }],
        Wires = [],
    };

    private void LoadIntoEditor(ProjectDocument document, string? filePath)
    {
        _loading = true;
        try
        {
            Instrument.LoadDocument(document.Instrument);
            Editor.LoadDocument(document);
        }
        finally
        {
            _loading = false;
        }

        _filePath = filePath;
        _isDirty = false;
        Recompile();
        UpdateTitle();
        _undoCommitTimer.Stop();
        _undoStack.Reset(SerializeCurrent());
        NotifyUndoRedoChanged();
        Status = $"Loaded {FileDisplayName} - {document.Nodes.Count} node(s).";
        DocumentLoaded?.Invoke();
    }

    /// <summary>A whole document was just put on the canvas (New, Open, an example) -
    /// not an undo step. The window re-centers the view.</summary>
    public event Action? DocumentLoaded;

    // ---- undo/redo (snapshot-based; one step per settled mutation burst) ----

    private string SerializeCurrent()
        => ProjectSerializer.Serialize(Editor.ToDocument(Instrument.ToDocument()));

    private void CommitUndoSnapshot()
    {
        if (_undoStack.Commit(SerializeCurrent()))
            NotifyUndoRedoChanged();
    }

    private void FlushPendingUndoCommit()
    {
        if (_undoCommitTimer.IsEnabled)
        {
            _undoCommitTimer.Stop();
            CommitUndoSnapshot();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        FlushPendingUndoCommit();
        if (_undoStack.Undo() is { } snapshot)
            RestoreSnapshot(snapshot);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        FlushPendingUndoCommit();
        if (_undoStack.Redo() is { } snapshot)
            RestoreSnapshot(snapshot);
    }

    private bool CanUndo => _undoStack.CanUndo;

    private bool CanRedo => _undoStack.CanRedo;

    private void NotifyUndoRedoChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Restore an undo/redo snapshot: keeps the file path, marks dirty,
    /// recompiles (which live-updates the trace). Selection/viewport are not restored.</summary>
    private void RestoreSnapshot(string snapshot)
    {
        var document = ProjectSerializer.Deserialize(snapshot);
        _loading = true;
        try
        {
            Instrument.LoadDocument(document.Instrument);
            Editor.LoadDocument(document);
        }
        finally
        {
            _loading = false;
        }

        _isDirty = true;
        Recompile();
        UpdateTitle();
        UpdateInspector();
        NotifyUndoRedoChanged();
    }

    // ---- live simulation ----

    /// <summary>Hand the newest compiled model to the live runner (no-op when live is off).</summary>
    private void SubmitLive()
    {
        if (!IsLive)
            return;
        var compilation = _compilation;
        if (compilation?.Model is null || compilation.Instrument is null)
        {
            // Last good trace stays on screen; the badges explain what is wrong.
            ReportCannotSimulate(compilation);
            return;
        }

        _runner.Submit(compilation.Model, compilation.Instrument);
    }

    private void PublishTrace(LiveTraceResult result)
    {
        _shimmerTimer.Stop();
        TracePanel.IsBusy = false;

        // Display-stage noise: a fresh seed per publish in normal mode, the held
        // seed in one-shot mode.
        _lastResult = result;
        ShowTrace(result);
        UpdateBounce();
        UpdateReturnLoss();
        var trace = result.Trace;
        string solver = trace.Solver == SolverPath.Chain ? "chain" : "nodal";
        Status = $"{(result.IsPreview ? "preview" : "final")}  |  {solver} solver  |  M = {trace.Grid.M}"
            + $"  |  {result.Elapsed.TotalMilliseconds:0} ms"
            + (result.IsPreview || trace.Settled
                ? ""
                : "  |  WARNING: trace not settled - reflections continue past the time window");
    }

    private void ReportCannotSimulate(DocumentCompilation? compilation)
    {
        var fatal = compilation?.Issues.Where(i => i.IsFatal).ToList() ?? [];
        Status = fatal.Count > 0
            ? $"Cannot simulate - {fatal[0].Message}"
              + (fatal.Count > 1 ? $" (+{fatal.Count - 1} more issue(s))" : "")
            : "Cannot simulate - the network is empty.";
    }

    /// <summary>Forces one full-resolution run, live or not (also the manual-mode run).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        var compilation = _compilation;
        if (compilation?.Model is null || compilation.Instrument is null)
        {
            ReportCannotSimulate(compilation);
            return;
        }

        IsSimulating = true;
        _shimmerTimer.Stop();
        _shimmerTimer.Start();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var trace = await Task.Run(
                () => TdrSimulator.Simulate(compilation.Model, compilation.Instrument, null, _session));
            stopwatch.Stop();
            PublishTrace(new LiveTraceResult(trace, IsPreview: false, stopwatch.Elapsed));
        }
        catch (Exception e)
        {
            Status = "Simulation failed: " + e.Message;
        }
        finally
        {
            _shimmerTimer.Stop();
            TracePanel.IsBusy = false;
            IsSimulating = false;
        }
    }

    private bool CanRefresh => !IsSimulating;

    // ---- compile-on-mutation ----

    private void OnDocumentMutated()
    {
        if (_loading)
            return;
        _isDirty = true;
        Recompile();
        UpdateTitle();
        _undoCommitTimer.Stop();
        _undoCommitTimer.Start();
    }

    private void Recompile()
    {
        _compilation = DocumentCompiler.Compile(
            Editor.ToDocument(Instrument.ToDocument()), _catalog);
        Badges.Clear();
        foreach (var group in _compilation.Issues.GroupBy(i => i.Kind))
        {
            int count = group.Count();
            string label = count == 1 ? Describe(group.Key) : $"{Describe(group.Key)} ×{count}";
            Badges.Add(new IssueBadge(
                label,
                string.Join("\n", group.Select(i => i.Message)),
                group.First().IsFatal));
        }

        if (_compilation.InterfaceNotes.Count > 0)
        {
            Badges.Add(new IssueBadge(
                $"connectors ×{_compilation.InterfaceNotes.Count}",
                string.Join("\n", _compilation.InterfaceNotes),
                IsFatal: false,
                IsInfo: true));
        }

        UpdateFeatureMap();
        SubmitLive();
        UpdatePulseProbes();
        SchedulePulse();
        if (_compilation.Model is null)
        {
            UpdateBounce();
            UpdateReturnLoss();
        }
    }

    // ---- M6 teaching views ----

    private string NodeTitle(string nodeId)
    {
        var node = Editor.Nodes.FirstOrDefault(n => n.Id == nodeId);
        return node is null ? nodeId : node.Title;
    }

    /// <summary>First-pass delay map of the newest compiled model: which component
    /// owns which stretch of trace time.</summary>
    private void UpdateFeatureMap()
    {
        var compilation = _compilation;
        _featureMap = compilation?.Model is null
            ? null
            : TraceFeatureMap.Build(
                compilation.Model, compilation.ElementOrigins,
                // Assumed opens are cable ends: the cable's own span already covers them.
                compilation.Probes.Where(p => p.OpenEndPort is null).Select(p => (p.NodeId, p.Net)));
        TracePanel.SetFeatureMap(_featureMap, NodeTitle);
        UpdateTraceHighlights();
    }

    /// <summary>Canvas to trace: shade the footprint of the selected nodes and, more
    /// strongly, of the node under the mouse.</summary>
    private void UpdateTraceHighlights()
    {
        var highlights = new List<TraceHighlight>();
        if (_featureMap is not null)
        {
            var hovered = Editor.HoveredNode;
            foreach (var node in Editor.SelectedNodes)
            {
                if (!ReferenceEquals(node, hovered) && _featureMap.For(node.Id) is { } feature)
                    highlights.Add(new TraceHighlight(feature.StartS, feature.EndS, IsHover: false));
            }

            if (hovered is not null && _featureMap.For(hovered.Id) is { } hoverFeature)
                highlights.Add(new TraceHighlight(hoverFeature.StartS, hoverFeature.EndS, IsHover: true));
        }

        TracePanel.SetHighlights(highlights);
    }

    /// <summary>Trace (or bounce ray) to canvas: glow the nodes being pointed at.</summary>
    private void ApplyNodeGlow()
    {
        foreach (var node in Editor.Nodes)
        {
            node.IsTraceHighlighted = _traceHoverNodes.Contains(node.Id)
                || _bounceHoverNodes.Contains(node.Id);
        }
    }

    /// <summary>Rebuild the lattice for the displayed trace (bounce tab only).</summary>
    private void UpdateBounce()
    {
        if (ActiveViewIndex != BounceViewIndex)
            return;
        var compilation = _compilation;
        var trace = TracePanel.Trace;
        if (compilation?.Model is null || compilation.Instrument is null)
        {
            Bounce.Clear("Cannot draw the bounce diagram - fix the network issues first.");
            return;
        }

        if (trace is null)
        {
            Bounce.Clear("Waiting for the first trace…");
            return;
        }

        double maxTimeS = TracePanel.DisplayEndS;
        var diagram = BounceDiagram.Build(
            compilation.Model, compilation.ElementOrigins, compilation.Instrument.SourceImpedanceOhms,
            new BounceOptions { MaxTimeS = maxTimeS });
        if (diagram is null)
        {
            Bounce.Clear(
                "A bounce diagram can only be drawn for an unbranched network - this one has a branch or a loop, "
                + "where the rays multiply in two dimensions.\n\nHover the TDR trace instead: "
                + "the component responsible for each feature lights up on the canvas for any topology.");
            return;
        }

        // The trace's own extent follows slow loss creep far past the last echo; the
        // lattice has none, so frame it on its own activity: a little past the last
        // arrival still worth a milli-rho or two, and never less than the first round trip.
        double lastArrivalS = diagram.PortArrivals
            .Where(a => Math.Abs(a.Amplitude) >= 2e-3)
            .Select(a => a.TimeS)
            .DefaultIfEmpty(0)
            .Max();
        double roundTripS = 2 * diagram.Interfaces[^1].DelayS;
        double viewS = Math.Min(maxTimeS, Math.Max(lastArrivalS * 1.15, 1.3 * roundTripS));
        Bounce.Set(diagram, trace, viewS > 0 ? viewS : maxTimeS);
    }

    /// <summary>|Gamma(f)| of the displayed trace (return-loss tab only).</summary>
    private void UpdateReturnLoss()
    {
        if (ActiveViewIndex != ReturnLossViewIndex)
            return;
        var compilation = _compilation;
        if (compilation?.Model is null || compilation.Instrument is null)
        {
            ReturnLossView.Clear("Cannot compute the return loss - fix the network issues first.");
            return;
        }

        var curve = TracePanel.Trace is { } trace
            ? ReturnLoss.FromTrace(trace, compilation.Instrument)
            : null;
        if (curve is null)
            ReturnLossView.Clear("Waiting for the first trace…");
        else
            ReturnLossView.Set(curve);
    }

    // ---- pulse view pipeline ----

    private void UpdatePulseProbes()
    {
        var probes = _compilation?.Probes ?? [];
        Pulse.UpdateProbes([.. probes.Select(p => new PulseProbe(ProbeLabel(p.NodeId, p.OpenEndPort), p.NodeId))]);
    }

    private string ProbeLabel(string nodeId, string? openEndPort)
    {
        var node = Editor.Nodes.FirstOrDefault(n => n.Id == nodeId);
        return node switch
        {
            TdrNodeViewModel => "TDR port",
            null => nodeId,
            _ when openEndPort is not null => $"Open (assumed) · end {openEndPort} of {node.Title}  [{nodeId}]",
            _ => $"{node.Title}  [{nodeId}]",
        };
    }

    private void SchedulePulse()
    {
        _pulseTimer.Stop();
        _pulseTimer.Start();
    }

    private async void RunPulse()
    {
        var compilation = _compilation;
        var probe = Pulse.SelectedProbe;
        if (compilation?.Model is null || compilation.Instrument is null)
        {
            Pulse.Info = "Cannot compute - fix the network issues first.";
            return;
        }

        if (probe is null)
        {
            Pulse.Info = "No probe points available.";
            return;
        }

        var documentProbe = compilation.Probes.FirstOrDefault(p => p.NodeId == probe.DocumentNodeId);
        if (documentProbe is null)
            return;

        if (Pulse.RiseTimePs <= 0 || Pulse.FallTimePs <= 0 || Pulse.RateMegahertz <= 0
            || Pulse.DutyPercent is <= 0 or >= 100)
        {
            Pulse.Info = "Invalid pulse parameters (rise/fall/rate > 0, duty 1-99 %).";
            return;
        }

        var settings = new PulseSettings
        {
            RiseTimeS = Pulse.RiseTimePs * 1e-12,
            FallTimeS = Pulse.FallTimePs * 1e-12,
            RateHz = Pulse.RateMegahertz * 1e6,
            DutyFraction = Pulse.DutyPercent / 100,
        };
        int version = ++_pulseVersion;
        _pulseCts?.Cancel();
        var cts = _pulseCts = new CancellationTokenSource();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var trace = await Task.Run(() => PulseSimulator.Simulate(
                compilation.Model, compilation.Instrument, settings, documentProbe.Net, _session, cts.Token));
            stopwatch.Stop();
            if (version != _pulseVersion)
                return;
            Pulse.SetTrace(trace, compilation.Noise?.EffectiveRmsVolts ?? 0, NextNoiseSeed());
            Pulse.Info =
                $"{trace.Volts.Length} samples/period  ·  {stopwatch.Elapsed.TotalMilliseconds:0} ms"
                + (trace.Compromised
                    ? "  ·  WARNING: edges under-resolved - raise the rate or slow the edges"
                    : "");
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer parameter change.
        }
        catch (Exception e)
        {
            if (version == _pulseVersion)
                Pulse.Info = "Pulse failed: " + e.Message;
        }
    }

    private static string Describe(DocumentIssueKind kind) => kind switch
    {
        DocumentIssueKind.NoSource => "no TDR",
        DocumentIssueKind.MultipleSources => "multiple TDRs",
        DocumentIssueKind.DisconnectedFromSource => "disconnected",
        DocumentIssueKind.NonPositiveLength => "zero length",
        DocumentIssueKind.SelfLoop => "self loop",
        DocumentIssueKind.InvalidLoadValues => "invalid load",
        DocumentIssueKind.InvalidCableValues => "invalid override",
        DocumentIssueKind.InvalidInstrumentValues => "invalid instrument",
        DocumentIssueKind.UnknownPresetId => "unknown preset",
        DocumentIssueKind.InvalidFaultValues => "invalid fault",
        DocumentIssueKind.InvalidCustomCable => "invalid custom cable",
        DocumentIssueKind.UnadaptableInterface => "needs balun",
        _ => kind.ToString(),
    };

    private void UpdateInspector()
        => InspectorContent = Editor.SelectedNode ?? (object)Instrument;

    private void UpdateTitle()
        => Title = $"{FileDisplayName}{(_isDirty ? "*" : "")} - TDR Viewer";
}
