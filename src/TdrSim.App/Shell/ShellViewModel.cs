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

/// <summary>One toolbar chip per distinct issue kind.</summary>
public sealed record IssueBadge(string Label, string Tooltip, bool IsFatal);

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
    private CancellationTokenSource? _pulseCts;
    private int _pulseVersion;
    private string? _filePath;
    private bool _isDirty;
    private bool _loading;
    private DocumentCompilation? _compilation;

    public ShellViewModel()
    {
        _catalog = CatalogLoader.LoadEmbedded();
        Instrument = new InstrumentViewModel(_catalog);
        Editor = new EditorViewModel(_catalog, Instrument);
        Palette = new PaletteViewModel(_catalog, Editor);
        TracePanel = new TracePanelViewModel();
        Pulse = new PulseViewModel();

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
        };

        LoadIntoEditor(ProjectSerializer.LoadEmbeddedDefault(), null);
        UpdateInspector();
    }

    public EditorViewModel Editor { get; }

    public PaletteViewModel Palette { get; }

    public InstrumentViewModel Instrument { get; }

    public TracePanelViewModel TracePanel { get; }

    public PulseViewModel Pulse { get; }

    /// <summary>0 = TDR trace, 1 = pulse view; switching to the pulse view computes it.</summary>
    [ObservableProperty]
    private int _activeViewIndex;

    partial void OnActiveViewIndexChanged(int value)
    {
        if (value == 1)
            SchedulePulse();
    }

    public IReadOnlyList<ExampleProject> Examples { get; } = ExampleProjects.All;

    public ObservableCollection<IssueBadge> Badges { get; } = [];

    /// <summary>Stops the live pipeline; call once when the window closes.</summary>
    public void Shutdown() => _runner.Dispose();

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
    }

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

        // Display-stage noise, fresh seed per publish - a live repetitive sweep.
        var displayed = result.Trace;
        if (_compilation is { Noise: { } noise, Instrument: { } instrument })
        {
            displayed = TraceNoise.Apply(
                displayed, noise, instrument.StepAmplitudeVolts, instrument.SourceImpedanceOhms,
                _noiseRandom.Next());
        }

        TracePanel.SetTrace(displayed, result.IsPreview);
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

        SubmitLive();
        UpdatePulseProbes();
        SchedulePulse();
    }

    // ---- pulse view pipeline ----

    private void UpdatePulseProbes()
    {
        var probes = _compilation?.Probes ?? [];
        Pulse.UpdateProbes([.. probes.Select(p => new PulseProbe(ProbeLabel(p.NodeId), p.NodeId))]);
    }

    private string ProbeLabel(string nodeId)
    {
        var node = Editor.Nodes.FirstOrDefault(n => n.Id == nodeId);
        return node switch
        {
            TdrNodeViewModel => "TDR port",
            null => nodeId,
            _ => $"{node.Title}  [{nodeId}]",
        };
    }

    private void SchedulePulse()
    {
        if (ActiveViewIndex != 1)
            return;
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
            Pulse.SetTrace(trace, compilation.Noise?.EffectiveRmsVolts ?? 0);
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
        _ => kind.ToString(),
    };

    private void UpdateInspector()
        => InspectorContent = Editor.SelectedNode ?? (object)Instrument;

    private void UpdateTitle()
        => Title = $"{FileDisplayName}{(_isDirty ? "*" : "")} - TDR Viewer";
}
