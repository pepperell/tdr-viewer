using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;

namespace TdrSim.App.Editor;

/// <summary>
/// One user-defined cable being edited. Fields are text so a half-typed number is a
/// visible error rather than a rejected keystroke; <see cref="Build"/> turns them into
/// a <see cref="CableSpec"/> through the same factories and validator the compiler uses.
/// </summary>
public sealed partial class CustomCableEntryViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly CableSpec? _original;
    private bool _dirty;
    private bool _ready;

    public CustomCableEntryViewModel(
        string id, CableSpec? original, CableSpec seed, IReadOnlyList<ConnectorChoice> connectorChoices)
    {
        Id = id;
        _original = original;
        _dirty = original is null;
        ConnectorChoiceList = connectorChoices;

        _name = seed.Name;
        _category = CategoryList.Contains(seed.Category) ? seed.Category : "coax";
        _connector = connectorChoices.FirstOrDefault(c => c.Id == (seed.DefaultConnector ?? "none"))
            ?? connectorChoices[0];

        // Both forms are pre-filled from the seed, so switching entry style keeps an
        // equivalent cable instead of presenting blank boxes.
        var model = CableModel.FromSpec(seed);
        _isRlgc = seed.Rlgc is not null;
        _z0Text = Format(seed.Z0Ohms);
        _vfText = Format(seed.VelocityFactor);
        _rdcText = Format(model.R0PerM * 1000);
        _pointsText = string.Join("\n", SeedPoints(seed).Select(p => $"{Format(p.Mhz)}  {Format(p.Db)}"));
        _lText = Format(model.LPerM * 1e9);
        _cText = Format(model.CPerM * 1e12);
        _rText = Format(model.R0PerM);
        _skinText = Format(model.RsOhmsPerMSqrtHz * 1e6); // x sqrt(1e6 Hz) x 1e3 mΩ/Ω
        _gText = Format(model.G0PerM * 1e6);
        _tanDeltaText = Format(model.TanDelta);

        _ready = true;
        Refresh();
    }

    private static readonly string[] CategoryList = ["coax", "twisted-pair", "pcb"];

    public IReadOnlyList<string> Categories => CategoryList;

    public string Id { get; }

    public IReadOnlyList<ConnectorChoice> ConnectorChoiceList { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _category;

    [ObservableProperty]
    private ConnectorChoice _connector;

    /// <summary>Entry style: false = datasheet, true = raw RLGC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDatasheet))]
    private bool _isRlgc;

    public bool IsDatasheet
    {
        get => !IsRlgc;
        set => IsRlgc = !value;
    }

    // ---- datasheet style ----

    [ObservableProperty]
    private string _z0Text;

    [ObservableProperty]
    private string _vfText;

    /// <summary>DC loop resistance (center + shield), Ω/km.</summary>
    [ObservableProperty]
    private string _rdcText;

    /// <summary>One "MHz dB" pair per line.</summary>
    [ObservableProperty]
    private string _pointsText;

    /// <summary>The typed attenuation figures are per 100 ft (US datasheets).</summary>
    [ObservableProperty]
    private bool _per100Ft;

    // ---- raw RLGC (per metre) ----

    [ObservableProperty]
    private string _lText;      // nH/m

    [ObservableProperty]
    private string _cText;      // pF/m

    [ObservableProperty]
    private string _rText;      // Ω/m, DC loop

    [ObservableProperty]
    private string _skinText;   // mΩ/m at 1 MHz

    [ObservableProperty]
    private string _gText;      // µS/m

    [ObservableProperty]
    private string _tanDeltaText;

    // ---- derived ----

    /// <summary>What the engine will make of the entry, or what is wrong with it.</summary>
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isValid;

    /// <summary>The spec this entry currently describes; null while invalid. An
    /// untouched existing entry yields its original instance.</summary>
    public CableSpec? Spec { get; private set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_ready || e.PropertyName is nameof(Summary) or nameof(IsValid) or nameof(IsDatasheet))
            return;
        _dirty = true;
        Refresh();
    }

    private void Refresh()
    {
        if (!_dirty && _original is not null)
        {
            Spec = _original;
            IsValid = true;
            Summary = Describe(_original);
            return;
        }

        var spec = Build(out string error);
        if (spec is not null)
        {
            var problems = CustomCable.Validate(spec);
            if (problems.Count > 0)
            {
                // "cable 'custom-1': ..." - the id means nothing to the user.
                int cut = problems[0].IndexOf("': ", StringComparison.Ordinal);
                error = cut < 0 ? problems[0] : problems[0][(cut + 3)..];
                spec = null;
            }
        }

        Spec = spec;
        IsValid = spec is not null;
        Summary = spec is null ? error : Describe(spec);
    }

    private CableSpec? Build(out string error)
    {
        error = "";
        string name = Name.Trim();
        if (name.Length == 0)
        {
            error = "Give the cable a name.";
            return null;
        }

        string? connector = Connector?.Id is null or "none" ? null : Connector.Id;
        if (IsRlgc)
        {
            if (!Number(LText, "L", out double l, ref error) || !Number(CText, "C", out double c, ref error)
                || !Number(RText, "R", out double r, ref error) || !Number(SkinText, "Skin R", out double skin, ref error)
                || !Number(GText, "G", out double g, ref error) || !Number(TanDeltaText, "tan δ", out double tan, ref error))
            {
                return null;
            }

            if (l <= 0 || c <= 0)
            {
                error = "L and C must be greater than zero.";
                return null;
            }

            return CustomCable.FromRlgc(Id, name, Category, new RlgcParameters
            {
                ROhmsPerM = r,
                LHenriesPerM = l * 1e-9,
                GSiemensPerM = g * 1e-6,
                CFaradsPerM = c * 1e-12,
                SkinOhmsPerMSqrtHz = skin * 1e-6,
                TanDelta = tan,
            }, connector);
        }

        if (!Number(Z0Text, "Z0", out double z0, ref error) || !Number(VfText, "VF", out double vf, ref error)
            || !Number(RdcText, "DC resistance", out double rdc, ref error))
        {
            return null;
        }

        if (z0 is < 1 or > 1000)
        {
            error = "Z0 must be between 1 and 1000 Ω.";
            return null;
        }

        if (vf is <= 0 or > 1)
        {
            error = "The velocity factor must be above 0 and at most 1.";
            return null;
        }

        if (rdc < 0)
        {
            error = "The DC resistance cannot be negative.";
            return null;
        }

        double scale = Per100Ft ? CustomCable.DbPer100FtToDbPer100M : 1;
        if (!CustomCable.TryParseAttenuation(PointsText, scale, out var points, out error))
            return null;
        return CustomCable.FromDatasheet(Id, name, Category, z0, vf, rdc, points, connector);
    }

    private static string Describe(CableSpec spec)
    {
        var model = CableModel.FromSpec(spec);
        string line = $"Z0 {spec.Z0Ohms:0.##} Ω  ·  VF {spec.VelocityFactor:0.###}  ·  "
            + $"L {model.LPerM * 1e9:0.#} nH/m  ·  C {model.CPerM * 1e12:0.##} pF/m";
        if (spec.Attenuation is { } table)
        {
            var (fit, worst) = CustomCable.FitQuality(table.Points);
            line += $"\nLoss fit α = {fit.K1DbPer100mSqrtMhz:0.####}·√f + {fit.K2DbPer100mMhz:0.#####}·f dB/100 m (f in MHz)"
                + $"  ·  worst point off by {worst:0.##} dB";
        }
        else if (spec.Rlgc is null)
        {
            line += "\nNo attenuation points: only the DC resistance is lossy.";
        }

        line += $"\nMatched loss: {CustomCable.AttenuationDbPer100M(spec, 10e6):0.##} dB/100 m at 10 MHz"
            + $"  ·  {CustomCable.AttenuationDbPer100M(spec, 100e6):0.##} at 100 MHz"
            + $"  ·  {CustomCable.AttenuationDbPer100M(spec, 1e9):0.##} at 1 GHz";
        return line;
    }

    /// <summary>Attenuation rows to show for a seed: its own table, or samples of
    /// whatever loss model it has (TIA coefficients, RLGC).</summary>
    private static IEnumerable<AttenuationPoint> SeedPoints(CableSpec seed)
    {
        if (seed.Attenuation is { } table)
            return table.Points;
        if (seed.InsertionLossCoefficients is null && seed.Rlgc is null)
            return [];
        double[] sample = seed.InsertionLossCoefficients is null ? [1, 10, 100, 1000] : [1, 10, 100, 250];
        var points = new List<AttenuationPoint>();
        foreach (double mhz in sample)
        {
            double db = CustomCable.AttenuationDbPer100M(seed, mhz * 1e6);
            if (db > 0 && (points.Count == 0 || db >= points[^1].Db))
                points.Add(new AttenuationPoint(mhz, Math.Round(db, 3)));
        }

        return points;
    }

    private static bool Number(string text, string label, out double value, ref string error)
    {
        if (double.TryParse(text.Trim(), NumberStyles.Float, Inv, out value) && double.IsFinite(value))
            return true;
        error = $"{label}: \"{text}\" is not a number.";
        return false;
    }

    private static string Format(double value) => value.ToString("0.######", Inv);
}

/// <summary>
/// The custom-cable manager: the document's user-defined cables as an editable list.
/// Nothing touches the document until the dialog is accepted and
/// <see cref="Result"/> is handed to <see cref="EditorViewModel.SetCustomCables"/>.
/// </summary>
public sealed partial class CustomCablesViewModel : ObservableObject
{
    private readonly IReadOnlyList<ConnectorChoice> _connectorChoices;
    private readonly HashSet<string> _inUse;
    private readonly HashSet<string> _takenIds;

    public CustomCablesViewModel(
        IReadOnlyList<CableSpec> customCables,
        IReadOnlyList<CableSpec> catalogCables,
        IReadOnlyList<ConnectorChoice> connectorChoices,
        HashSet<string> inUseIds,
        string? selectId)
    {
        _connectorChoices = connectorChoices;
        _inUse = inUseIds;
        CatalogCables = catalogCables;
        _takenIds = [.. catalogCables.Select(c => c.Id), .. customCables.Select(c => c.Id), .. inUseIds];
        foreach (var cable in customCables)
            Watch(new CustomCableEntryViewModel(cable.Id, cable, cable, connectorChoices));
        Selected = Entries.FirstOrDefault(e => e.Id == selectId) ?? Entries.FirstOrDefault();
    }

    public ObservableCollection<CustomCableEntryViewModel> Entries { get; } = [];

    /// <summary>Presets a new custom cable can start from.</summary>
    public IReadOnlyList<CableSpec> CatalogCables { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(DeleteNote))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private CustomCableEntryViewModel? _selected;

    /// <summary>"Start from a preset" combo: picking one creates a copy, then resets.</summary>
    [ObservableProperty]
    private CableSpec? _copySource;

    public bool HasSelection => Selected is not null;

    public bool CanAccept => Entries.All(e => e.IsValid);

    public string DeleteNote => Selected is not null && _inUse.Contains(Selected.Id)
        ? "In use on the canvas - cannot be deleted."
        : "";

    partial void OnCopySourceChanged(CableSpec? value)
    {
        if (value is null)
            return;
        Add(value with { Name = value.Name + " (custom)" });
        CopySource = null;
    }

    [RelayCommand]
    private void New() => Add(CustomCable.FromDatasheet(
        "", "My cable", "coax", 50, 0.66, 0, [], null));

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (Selected is not { } entry)
            return;
        int index = Entries.IndexOf(entry);
        entry.PropertyChanged -= OnEntryChanged;
        Entries.Remove(entry);
        Selected = Entries.Count == 0 ? null : Entries[Math.Min(index, Entries.Count - 1)];
        OnPropertyChanged(nameof(CanAccept));
    }

    private bool CanDelete => Selected is not null && !_inUse.Contains(Selected.Id);

    /// <summary>The accepted list. Untouched cables come back as their original
    /// instances, which keeps the engine's per-spec caches warm.</summary>
    public IReadOnlyList<CableSpec> Result() => [.. Entries.Select(e => e.Spec!)];

    private void Add(CableSpec seed)
    {
        string id = CustomCable.NewId(_takenIds);
        _takenIds.Add(id);
        var entry = new CustomCableEntryViewModel(id, null, seed, _connectorChoices);
        Watch(entry);
        Selected = entry;
        OnPropertyChanged(nameof(CanAccept));
    }

    private void Watch(CustomCableEntryViewModel entry)
    {
        entry.PropertyChanged += OnEntryChanged;
        Entries.Add(entry);
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomCableEntryViewModel.IsValid))
            OnPropertyChanged(nameof(CanAccept));
    }
}
