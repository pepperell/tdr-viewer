using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdrSim.Core.Catalog;
using TdrSim.Core.Project;

namespace TdrSim.App.Editor;

/// <summary>The TDR instrument: preset plus optional overrides (blank = preset value).</summary>
public sealed partial class InstrumentViewModel : ObservableObject
{
    private string _presetId;

    public InstrumentViewModel(ComponentCatalog catalog)
    {
        Presets = catalog.Instruments;
        _presetId = DefaultPresetId(catalog);
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
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRiseTimePs))]
    [NotifyPropertyChangedFor(nameof(RiseTimeLog))]
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
    private double? _amplitudeVoltsOverride;

    [ObservableProperty]
    private double? _sourceImpedanceOverrideOhms;

    /// <summary>Display-stage noise; null/0 = off.</summary>
    [ObservableProperty]
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
    }
}
