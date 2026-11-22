using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TdrSim.Core.Catalog;
using TdrSim.Core.Model;
using TdrSim.Core.Project;

namespace TdrSim.App.Editor;

/// <summary>
/// Base for canvas nodes. View models are thin mirrors of <see cref="DocumentNode"/>:
/// <see cref="ToDocumentNode"/> is the single path into both the serializer and the
/// compiler, and <see cref="EditorViewModel.LoadDocument"/> is its one inverse.
/// </summary>
public abstract partial class NodeViewModelBase(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private bool _isSelected;

    public List<PortViewModel> InputPorts { get; } = [];

    public List<PortViewModel> OutputPorts { get; } = [];

    public IEnumerable<PortViewModel> Ports => [.. InputPorts, .. OutputPorts];

    public abstract string Title { get; }

    public abstract DocumentNodeKind Kind { get; }

    public abstract DocumentNode ToDocumentNode();

    protected DocumentNode BaseNode() => new()
    {
        Id = Id,
        Kind = Kind,
        X = Location.X,
        Y = Location.Y,
    };
}

public sealed class TdrNodeViewModel : NodeViewModelBase
{
    public TdrNodeViewModel(string id, InstrumentViewModel instrument)
        : base(id)
    {
        Instrument = instrument;
        OutputPorts.Add(new PortViewModel(this, DocumentPorts.P));
    }

    /// <summary>The shared instrument settings - the inspector shows these when the
    /// TDR node (or nothing) is selected.</summary>
    public InstrumentViewModel Instrument { get; }

    public override string Title => "TDR";

    public override DocumentNodeKind Kind => DocumentNodeKind.Tdr;

    public override DocumentNode ToDocumentNode() => BaseNode();
}

public sealed partial class CableNodeViewModel : NodeViewModelBase
{
    private string? _presetId;
    private string? _connectorAId;
    private string? _connectorBId;

    public CableNodeViewModel(
        string id,
        IReadOnlyList<CableSpec> presets,
        string? presetId,
        IReadOnlyList<ConnectorChoice> connectorChoices)
        : base(id)
    {
        Presets = presets;
        _presetId = presetId;
        ConnectorChoiceList = connectorChoices;
        InputPorts.Add(new PortViewModel(this, DocumentPorts.A));
        OutputPorts.Add(new PortViewModel(this, DocumentPorts.B));
    }

    public IReadOnlyList<CableSpec> Presets { get; }

    public IReadOnlyList<ConnectorChoice> ConnectorChoiceList { get; }

    /// <summary>Null when the document references a preset missing from the catalog;
    /// the raw id is preserved so saving doesn't silently rewrite the file.</summary>
    public CableSpec? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Id == _presetId);
        set
        {
            if (value is null || value.Id == _presetId)
                return;
            _presetId = value.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(EffectiveConnectorsNote));
        }
    }

    public ConnectorChoice? SelectedConnectorA
    {
        get => ConnectorChoiceList.FirstOrDefault(c => c.Id == _connectorAId);
        set => SetConnector(ref _connectorAId, value, nameof(SelectedConnectorA));
    }

    public ConnectorChoice? SelectedConnectorB
    {
        get => ConnectorChoiceList.FirstOrDefault(c => c.Id == _connectorBId);
        set => SetConnector(ref _connectorBId, value, nameof(SelectedConnectorB));
    }

    /// <summary>Document hydration path (no change notification needed mid-build).</summary>
    public void InitConnectors(string? connectorAId, string? connectorBId)
        => (_connectorAId, _connectorBId) = (connectorAId, connectorBId);

    public string EffectiveConnectorsNote =>
        $"Fitted: A = {Effective(_connectorAId)}, B = {Effective(_connectorBId)}."
        + " Mating hardware is added automatically where ends meet.";

    private string Effective(string? overrideId)
        => ConnectorChoices.EffectiveName(ConnectorChoiceList, overrideId, SelectedPreset?.DefaultConnector);

    private void SetConnector(ref string? field, ConnectorChoice? value, string propertyName)
    {
        if (value is null || value.Id == field)
            return;
        field = value.Id;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(EffectiveConnectorsNote));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(LengthLog))]
    private double _lengthM = 30;

    [ObservableProperty]
    private double? _z0OverrideOhms;

    [ObservableProperty]
    private double? _vfOverride;

    /// <summary>Log10 companion for the 0.1-1000 m inspector slider.</summary>
    public double LengthLog
    {
        get => Math.Log10(Math.Max(LengthM, 0.1));
        set => LengthM = Math.Round(Math.Pow(10, value), 3);
    }

    public override string Title => $"{SelectedPreset?.Name ?? _presetId ?? "cable"} · {LengthM:0.###} m";

    public override DocumentNodeKind Kind => DocumentNodeKind.Cable;

    public override DocumentNode ToDocumentNode() => BaseNode() with
    {
        CablePresetId = _presetId,
        LengthM = LengthM,
        Z0OverrideOhms = Z0OverrideOhms,
        VfOverride = VfOverride,
        ConnectorAId = _connectorAId,
        ConnectorBId = _connectorBId,
    };
}

public sealed partial class ConnectorNodeViewModel : NodeViewModelBase
{
    private string? _presetId;

    public ConnectorNodeViewModel(string id, IReadOnlyList<ConnectorSpec> presets, string? presetId)
        : base(id)
    {
        Presets = presets;
        _presetId = presetId;
        InputPorts.Add(new PortViewModel(this, DocumentPorts.A));
        OutputPorts.Add(new PortViewModel(this, DocumentPorts.B));
    }

    public IReadOnlyList<ConnectorSpec> Presets { get; }

    public ConnectorSpec? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Id == _presetId);
        set
        {
            if (value is null || value.Id == _presetId)
                return;
            _presetId = value.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
        }
    }

    public override string Title => SelectedPreset?.Name ?? _presetId ?? "connector";

    public override DocumentNodeKind Kind => DocumentNodeKind.Connector;

    public override DocumentNode ToDocumentNode() => BaseNode() with
    {
        ConnectorPresetId = _presetId,
    };
}

public sealed partial class LoadNodeViewModel : NodeViewModelBase
{
    public LoadNodeViewModel(string id, LoadKind kind)
        : base(id)
    {
        _loadKind = kind;
        InputPorts.Add(new PortViewModel(this, DocumentPorts.P));
    }

    public static LoadNodeViewModel CreateWithDefaults(string id, LoadKind kind)
    {
        var node = new LoadNodeViewModel(id, kind);
        if (node.UsesR)
            node.ROhms = 50;
        if (node.UsesL)
            node.LNanohenries = 100;
        if (node.UsesC)
            node.CPicofarads = 10;
        return node;
    }

    public IReadOnlyList<LoadKind> Kinds { get; } = Enum.GetValues<LoadKind>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(UsesR))]
    [NotifyPropertyChangedFor(nameof(UsesL))]
    [NotifyPropertyChangedFor(nameof(UsesC))]
    private LoadKind _loadKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private double? _rOhms;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private double? _lNanohenries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private double? _cPicofarads;

    public bool UsesR => LoadKind is LoadKind.Resistor or LoadKind.SeriesRlc or LoadKind.ParallelRlc;

    public bool UsesL => LoadKind is LoadKind.Inductor or LoadKind.SeriesRlc or LoadKind.ParallelRlc;

    public bool UsesC => LoadKind is LoadKind.Capacitor or LoadKind.SeriesRlc or LoadKind.ParallelRlc;

    public override string Title => LoadKind switch
    {
        LoadKind.Open => "Open",
        LoadKind.Short => "Short",
        LoadKind.Resistor => $"R {ROhms:0.###} Ω",
        LoadKind.Capacitor => $"C {CPicofarads:0.###} pF",
        LoadKind.Inductor => $"L {LNanohenries:0.###} nH",
        LoadKind.SeriesRlc => "Series RLC",
        LoadKind.ParallelRlc => "Parallel RLC",
        _ => LoadKind.ToString(),
    };

    public override DocumentNodeKind Kind => DocumentNodeKind.Load;

    public override DocumentNode ToDocumentNode() => BaseNode() with
    {
        LoadKind = LoadKind,
        ROhms = UsesR ? ROhms : null,
        LHenries = UsesL ? LNanohenries * 1e-9 : null,
        CFarads = UsesC ? CPicofarads * 1e-12 : null,
    };
}

public sealed class JunctionNodeViewModel : NodeViewModelBase
{
    public JunctionNodeViewModel(string id)
        : base(id)
        => InputPorts.Add(new PortViewModel(this, DocumentPorts.P));

    public override string Title => "●";

    public override DocumentNodeKind Kind => DocumentNodeKind.Junction;

    public override DocumentNode ToDocumentNode() => BaseNode();
}

public sealed partial class FaultNodeViewModel : NodeViewModelBase
{
    private readonly string? _faultPresetId;
    private readonly string _displayName;
    private string? _basePresetId;

    public FaultNodeViewModel(
        string id,
        FaultKind faultKind,
        IReadOnlyList<CableSpec> cablePresets,
        string? faultPresetId,
        string displayName)
        : base(id)
    {
        FaultKind = faultKind;
        CablePresets = cablePresets;
        _faultPresetId = faultPresetId;
        _displayName = displayName;
        InputPorts.Add(new PortViewModel(this, faultKind == FaultKind.ShuntBridge ? DocumentPorts.P : DocumentPorts.A));
        if (faultKind != FaultKind.ShuntBridge)
            OutputPorts.Add(new PortViewModel(this, DocumentPorts.B));
    }

    public static FaultNodeViewModel CreateFromPreset(
        string id, FaultSpec preset, IReadOnlyList<CableSpec> cablePresets)
    {
        var kind = KindOf(preset.Kind);
        return new FaultNodeViewModel(
            id, kind, cablePresets, preset.Id, preset.Name)
        {
            BasePresetId = kind == FaultKind.SectionOverride ? cablePresets.FirstOrDefault()?.Id : null,
            LengthM = preset.Defaults.LengthM,
            Z0Scale = preset.Defaults.Z0Scale,
            ExtraLossDbPer100m = preset.Defaults.ExtraDielectricLossDbPer100m,
            ROhms = preset.Defaults.BridgeResistanceOhms ?? preset.Defaults.SeriesResistanceOhms,
        };
    }

    public static FaultNodeViewModel FromDocument(DocumentNode docNode, ComponentCatalog catalog)
    {
        var kind = docNode.FaultKind ?? FaultKind.SectionOverride;
        var preset = docNode.FaultPresetId is null
            ? null
            : catalog.Faults.FirstOrDefault(f => f.Id == docNode.FaultPresetId);
        return new FaultNodeViewModel(
            docNode.Id, kind, catalog.Cables, docNode.FaultPresetId, preset?.Name ?? DefaultName(kind))
        {
            BasePresetId = docNode.CablePresetId,
            LengthM = docNode.LengthM,
            Z0Scale = docNode.Z0Scale,
            ExtraLossDbPer100m = docNode.ExtraDielectricLossDbPer100m,
            ROhms = docNode.ROhms,
        };
    }

    public FaultKind FaultKind { get; }

    public IReadOnlyList<CableSpec> CablePresets { get; }

    public bool IsSection => FaultKind == FaultKind.SectionOverride;

    public bool IsBridge => FaultKind == FaultKind.ShuntBridge;

    public bool IsSeries => FaultKind == FaultKind.SeriesFault;

    /// <summary>Base cable id for sections; preserved raw like the cable VM's preset.</summary>
    public string? BasePresetId
    {
        get => _basePresetId;
        set
        {
            if (value == _basePresetId)
                return;
            _basePresetId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedBasePreset));
        }
    }

    public CableSpec? SelectedBasePreset
    {
        get => CablePresets.FirstOrDefault(p => p.Id == _basePresetId);
        set
        {
            if (value is null || value.Id == _basePresetId)
                return;
            _basePresetId = value.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BasePresetId));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private double? _lengthM;

    [ObservableProperty]
    private double? _z0Scale;

    [ObservableProperty]
    private double? _extraLossDbPer100m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private double? _rOhms;

    public override string Title => FaultKind switch
    {
        FaultKind.SectionOverride => $"⚡ {_displayName} · {LengthM:0.###} m",
        FaultKind.ShuntBridge => $"⚡ Bridge {ROhms:0.###} Ω",
        _ => $"⚡ Series {ROhms:0.###} Ω",
    };

    public override DocumentNodeKind Kind => DocumentNodeKind.Fault;

    public override DocumentNode ToDocumentNode() => BaseNode() with
    {
        FaultKind = FaultKind,
        FaultPresetId = _faultPresetId,
        CablePresetId = IsSection ? _basePresetId : null,
        LengthM = IsSection ? LengthM : null,
        Z0Scale = IsSection ? Z0Scale : null,
        ExtraDielectricLossDbPer100m = IsSection ? ExtraLossDbPer100m : null,
        ROhms = IsSection ? null : ROhms,
    };

    private static FaultKind KindOf(string catalogKind) => catalogKind switch
    {
        "sectionOverride" => FaultKind.SectionOverride,
        "shuntBridge" => FaultKind.ShuntBridge,
        "seriesFault" => FaultKind.SeriesFault,
        _ => throw new InvalidOperationException($"Unknown fault kind '{catalogKind}' in catalog."),
    };

    private static string DefaultName(FaultKind kind) => kind switch
    {
        FaultKind.SectionOverride => "Fault section",
        FaultKind.ShuntBridge => "Bridge fault",
        _ => "Series fault",
    };
}
