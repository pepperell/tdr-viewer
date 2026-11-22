using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdrSim.Core.Catalog;
using TdrSim.Core.Project;

namespace TdrSim.App.Editor;

/// <summary>The TDR instrument: preset plus optional overrides (blank = preset value).</summary>
public sealed partial class InstrumentViewModel : ObservableObject
{
    private string _presetId;
    private string? _portConnectorId;

    public InstrumentViewModel(ComponentCatalog catalog)
    {
        Presets = catalog.Instruments;
        _presetId = DefaultPresetId(catalog);
        ConnectorChoiceList = ConnectorChoices.Build(catalog);
    }

    public static string DefaultPresetId(ComponentCatalog catalog)
        => (catalog.Instruments.FirstOrDefault(p => p.Id == "tek-1502c") ?? catalog.Instruments[0]).Id;

    public IReadOnlyList<InstrumentSpec> Presets { get; }

    /// <summary>Null when the document references a preset missing from the catalog;
    /// the raw id is preserved so saving doesn't silently rewrite the file.</summary>
    public InstrumentSpec? SelectedPreset
    {
        get => Presets.FirstOrDefault(p => p.Id == _presetId);
        set
        {
            if (value is null || value.Id == _presetId)
                return;
            _presetId = value.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveRiseTimePs));
            OnPropertyChanged(nameof(RiseTimeLog));
            NotifyEffectiveValues();
        }
    }

    public IReadOnlyList<ConnectorChoice> ConnectorChoiceList { get; }

    /// <summary>Shows the effective family; stores null when it equals the preset's
    /// default so the field keeps tracking preset switches.</summary>
    public ConnectorChoice? SelectedPortConnector
    {
        get
        {
            string effective = _portConnectorId ?? SelectedPreset?.PortConnector ?? "none";
            return ConnectorChoiceList.FirstOrDefault(c => c.Id == effective)
                ?? ConnectorChoiceList.FirstOrDefault(c => c.Id == "none");
        }
        set
        {
            if (value is null)
                return;
            string presetDefault = SelectedPreset?.PortConnector ?? "none";
            string? stored = value.Id == presetDefault ? null : value.Id;
            if (stored == _portConnectorId)
                return;
            _portConnectorId = stored;
            OnPropertyChanged();
        }
    }

    public string PortConnectorDefaultNote =>
        $"Default: {ConnectorChoices.EffectiveName(ConnectorChoiceList, null, SelectedPreset?.PortConnector)}."
        + " The panel-to-cable mate (and any adapter) is inserted automatically.";

    // ---- effective values shown in the inspector ----
    // Each box is pre-filled with what the engine will use; entering the preset's own
    // value (or clearing the box) stores null, i.e. "track the preset".

    public double? RiseTimePs
    {
        get => RiseTimePsOverride ?? SelectedPreset?.RiseTime1090Ps;
        set => RiseTimePsOverride = Normalize(value, SelectedPreset?.RiseTime1090Ps);
    }

    public double? AmplitudeVolts
    {
        get => AmplitudeVoltsOverride ?? SelectedPreset?.StepAmplitudeVolts;
        set => AmplitudeVoltsOverride = Normalize(value, SelectedPreset?.StepAmplitudeVolts);
    }

    public double? SourceImpedanceOhms
    {
        get => SourceImpedanceOverrideOhms ?? SelectedPreset?.SourceImpedanceOhms;
        set => SourceImpedanceOverrideOhms = Normalize(value, SelectedPreset?.SourceImpedanceOhms);
    }

    /// <summary>Effective noise floor; an explicit 0 still means "off".</summary>
    public double? NoiseRms
    {
        get => NoiseRmsMillivolts ?? SelectedPreset?.NoiseFloorMillivolts;
        set => NoiseRmsMillivolts = Normalize(value, SelectedPreset?.NoiseFloorMillivolts);
    }

    public string RiseTimeDefaultNote => DefaultNote(SelectedPreset?.RiseTime1090Ps);

    public string AmplitudeDefaultNote => DefaultNote(SelectedPreset?.StepAmplitudeVolts);

    public string ImpedanceDefaultNote => DefaultNote(SelectedPreset?.SourceImpedanceOhms);

    public string NoiseDefaultNote =>
        $"{DefaultNote(SelectedPreset?.NoiseFloorMillivolts)} (preset floor) · 0 = off";

    private static string DefaultNote(double? value)
        => value is null ? "Default: —" : $"Default: {value:0.###}";

    private static double? Normalize(double? value, double? presetDefault)
        => value is null || value == presetDefault ? null : value;

    private void NotifyEffectiveValues()
    {
        OnPropertyChanged(nameof(RiseTimePs));
        OnPropertyChanged(nameof(AmplitudeVolts));
        OnPropertyChanged(nameof(SourceImpedanceOhms));
        OnPropertyChanged(nameof(NoiseRms));
        OnPropertyChanged(nameof(SelectedPortConnector));
        OnPropertyChanged(nameof(RiseTimeDefaultNote));
        OnPropertyChanged(nameof(AmplitudeDefaultNote));
        OnPropertyChanged(nameof(ImpedanceDefaultNote));
        OnPropertyChanged(nameof(NoiseDefaultNote));
        OnPropertyChanged(nameof(PortConnectorDefaultNote));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRiseTimePs))]
    [NotifyPropertyChangedFor(nameof(RiseTimeLog))]
    [NotifyPropertyChangedFor(nameof(RiseTimePs))]
    private double? _riseTimePsOverride;

    /// <summary>What the engine will use: the override, else the preset's rise time.</summary>
    public double EffectiveRiseTimePs => RiseTimePsOverride ?? SelectedPreset?.RiseTime1090Ps ?? 0;

    /// <summary>Log10(ps) companion for the 10 ps - 5 ns toolbar slider.</summary>
    public double RiseTimeLog
    {
        get => Math.Log10(Math.Clamp(EffectiveRiseTimePs, 10, 5000));
        set => RiseTimePsOverride = Math.Round(Math.Pow(10, value), 1);
    }

    [RelayCommand]
    private void ClearRiseTimeOverride() => RiseTimePsOverride = null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmplitudeVolts))]
    private double? _amplitudeVoltsOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceImpedanceOhms))]
    private double? _sourceImpedanceOverrideOhms;

    /// <summary>Display-stage noise; null = preset floor, 0 = off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoiseRms))]
    private double? _noiseRmsMillivolts;

    public IReadOnlyList<int> AveragingOptions { get; } = [1, 4, 16, 64, 256];

    [ObservableProperty]
    private int _averagingSweeps = 1;

    public InstrumentDocument ToDocument() => new()
    {
        PresetId = _presetId,
        RiseTimePsOverride = RiseTimePsOverride,
        AmplitudeVoltsOverride = AmplitudeVoltsOverride,
        SourceImpedanceOverrideOhms = SourceImpedanceOverrideOhms,
        NoiseRmsMillivolts = NoiseRmsMillivolts,
        AveragingSweeps = AveragingSweeps == 1 ? null : AveragingSweeps,
        PortConnectorId = _portConnectorId,
    };

    public void LoadDocument(InstrumentDocument document)
    {
        _presetId = document.PresetId;
        OnPropertyChanged(nameof(SelectedPreset));
        RiseTimePsOverride = document.RiseTimePsOverride;
        AmplitudeVoltsOverride = document.AmplitudeVoltsOverride;
        SourceImpedanceOverrideOhms = document.SourceImpedanceOverrideOhms;
        NoiseRmsMillivolts = document.NoiseRmsMillivolts;
        AveragingSweeps = document.AveragingSweeps ?? 1;
        _portConnectorId = document.PortConnectorId;
        NotifyEffectiveValues();
    }
}
