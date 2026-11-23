using System.Collections.ObjectModel;
using System.Windows.Media;
using TdrSim.Core.Catalog;
using TdrSim.Core.Model;
using TdrSim.Core.Project;

namespace TdrSim.App.Editor;

/// <summary>A row in a component button's drop-down: a thing to add, or a section header.</summary>
public abstract class PaletteEntry(string label)
{
    public string Label { get; } = label;

    public abstract bool IsHeader { get; }
}

/// <summary>An addable component. Payload: CableSpec / ConnectorSpec / LoadKind / FaultSpec.</summary>
public sealed class PaletteItem(string label, DocumentNodeKind kind, object? payload) : PaletteEntry(label)
{
    public DocumentNodeKind Kind { get; } = kind;

    public object? Payload { get; } = payload;

    public override bool IsHeader => false;
}

/// <summary>A non-selectable section title inside a drop-down ("50 Ω coax").</summary>
public sealed class PaletteHeader(string label) : PaletteEntry(label)
{
    public override bool IsHeader => true;
}

/// <summary>What pressing a component-bar button does.</summary>
public enum PaletteAction
{
    /// <summary>Drop down <see cref="PaletteButton.Entries"/>.</summary>
    Menu,

    /// <summary>Add <see cref="PaletteButton.DirectItem"/> straight away.</summary>
    Add,

    /// <summary>Open the cable manager dialog.</summary>
    CableManager,
}

/// <summary>One square button of the component bar: an icon, a subtitle, and either a
/// drop-down list, one component to add, or the cable manager.</summary>
public sealed class PaletteButton
{
    public PaletteButton(string id, string title, string toolTip, string iconData, PaletteAction action)
    {
        Id = id;
        Title = title;
        ToolTip = toolTip;
        Action = action;
        var icon = Geometry.Parse(iconData);
        icon.Freeze();
        Icon = icon;
    }

    /// <summary>Stable name, used for the automation id ("Palette" + Id + "Button").</summary>
    public string Id { get; }

    public string Title { get; }

    public string ToolTip { get; }

    /// <summary>Stroke-only artwork in a 32 × 32 box.</summary>
    public Geometry Icon { get; }

    public PaletteAction Action { get; }

    public string AutomationId => Action == PaletteAction.CableManager ? "CustomCablesButton" : $"Palette{Id}Button";

    public ObservableCollection<PaletteEntry> Entries { get; } = [];

    public PaletteItem? DirectItem { get; init; }

    /// <summary>Set off from the buttons before it by a divider.</summary>
    public bool StartsGroup { get; init; }
}

/// <summary>The component bar above the canvas: Cables, Pairs, PCB traces, Ideal,
/// Splices, Loads, Faults, Probe, and the cable manager.</summary>
public sealed class PaletteViewModel
{
    private const string Coax50 = "50 Ω coax";
    private const string Coax75 = "75 Ω coax";
    private const string OtherCoax = "Other coax";
    private const string Pairs = "Balanced pairs";
    private const string Pcb = "PCB traces";
    private const string Ideal = "Ideal (lossless)";

    private const string Ring13 = "M 3,16 A 13,13 0 1 1 29,16 A 13,13 0 1 1 3,16 Z";

    private readonly ComponentCatalog _catalog;
    private readonly EditorViewModel _editor;
    private readonly PaletteButton _cables;

    public PaletteViewModel(ComponentCatalog catalog, EditorViewModel editor)
    {
        _catalog = catalog;
        _editor = editor;

        _cables = new PaletteButton(
            "Cables", "Cables", "Add a coaxial cable",
            Ring13 + " M 9,16 A 7,7 0 1 1 23,16 A 7,7 0 1 1 9,16 Z M 14.5,16 A 1.5,1.5 0 1 1 17.5,16 A 1.5,1.5 0 1 1 14.5,16 Z",
            PaletteAction.Menu);

        var pairs = new PaletteButton(
            "Pairs", "Pairs", "Add a balanced pair",
            "M 2,11 C 8,11 8,21 16,21 S 24,11 30,11 M 2,21 C 8,21 8,11 16,11 S 24,21 30,21",
            PaletteAction.Menu);
        Fill(pairs, Section(Pairs));

        var pcb = new PaletteButton(
            "PcbTraces", "PCB traces", "Add a PCB trace",
            "M 3,6 H 29 V 26 H 3 Z M 5,20 A 2,2 0 1 1 9,20 A 2,2 0 1 1 5,20 Z M 9,20 H 14 L 20,12 H 23 "
            + "M 23,12 A 2,2 0 1 1 27,12 A 2,2 0 1 1 23,12 Z",
            PaletteAction.Menu);
        Fill(pcb, Section(Pcb));

        var ideal = new PaletteButton(
            "Ideal", "Ideal", "Add an ideal (lossless) line",
            "M 3,16 A 2,2 0 1 1 7,16 A 2,2 0 1 1 3,16 Z M 7,16 H 25 M 25,16 A 2,2 0 1 1 29,16 A 2,2 0 1 1 25,16 Z",
            PaletteAction.Menu);
        Fill(ideal, Section(Ideal));

        var splices = new PaletteButton(
            "Splices", "Splices", "Add a splice",
            "M 2,16 H 10 M 22,16 H 30 M 10,11 H 22 V 21 H 10 Z M 16,11 V 21",
            PaletteAction.Menu);
        Fill(splices, catalog.Connectors.Select(c => new PaletteItem(c.Name, DocumentNodeKind.Connector, c)));

        var loads = new PaletteButton(
            "Loads", "Loads", "Add a termination",
            "M 16,2 V 6 L 11,8.5 L 21,11.5 L 11,14.5 L 21,17.5 L 16,20 V 24 M 9,24 H 23 M 12,27 H 20 M 14.5,30 H 17.5",
            PaletteAction.Menu);
        Fill(loads,
        [
            new PaletteItem("Open", DocumentNodeKind.Load, LoadKind.Open),
            new PaletteItem("Short", DocumentNodeKind.Load, LoadKind.Short),
            new PaletteItem("Resistor", DocumentNodeKind.Load, LoadKind.Resistor),
            new PaletteItem("Capacitor", DocumentNodeKind.Load, LoadKind.Capacitor),
            new PaletteItem("Inductor", DocumentNodeKind.Load, LoadKind.Inductor),
            new PaletteItem("Series RLC", DocumentNodeKind.Load, LoadKind.SeriesRlc),
            new PaletteItem("Parallel RLC", DocumentNodeKind.Load, LoadKind.ParallelRlc),
        ]);

        var faults = new PaletteButton(
            "Faults", "Faults", "Add a cable fault",
            "M 2,20 H 9 M 23,20 H 30 M 18,4 L 11,17 H 16 L 13,28 L 22,13 H 17 Z",
            PaletteAction.Menu);
        Fill(faults, catalog.Faults.Select(f => new PaletteItem(f.Name, DocumentNodeKind.Fault, f)));

        // A probe is the document's Junction node: electrically invisible, and a point
        // the pulse view can watch.
        var probe = new PaletteButton(
            "Probe", "Probe", "Add a probe point: wire it anywhere to watch the pulse there",
            "M 2,24 H 30 M 14,24 A 2,2 0 1 1 18,24 A 2,2 0 1 1 14,24 Z M 17.4,22.6 L 21,19 "
            + "M 19.2,17.2 L 22.8,20.8 L 29.8,13.8 L 26.2,10.2 Z",
            PaletteAction.Add)
        {
            DirectItem = new PaletteItem("Probe", DocumentNodeKind.Junction, null),
        };

        var manager = new PaletteButton(
            "CableManager", "Cable manager", "Define your own cables from a datasheet or raw RLGC",
            "M 3,10 A 7,7 0 1 1 17,10 A 7,7 0 1 1 3,10 Z M 7.5,10 A 2.5,2.5 0 1 1 12.5,10 A 2.5,2.5 0 1 1 7.5,10 Z "
            + "M 21,7 H 30 M 21,13 H 30 M 3,22 H 30 M 3,28 H 30",
            PaletteAction.CableManager)
        {
            StartsGroup = true,
        };

        Buttons = [_cables, pairs, pcb, ideal, splices, loads, faults, probe, manager];

        editor.CustomCablesChanged += (_, _) => FillCables();
        FillCables();
    }

    public IReadOnlyList<PaletteButton> Buttons { get; }

    private IEnumerable<PaletteItem> Section(string section)
        => _catalog.Cables.Where(c => c.Section == section)
            .Select(c => new PaletteItem(c.Name, DocumentNodeKind.Cable, c));

    private static void Fill(PaletteButton button, IEnumerable<PaletteItem> items)
    {
        foreach (var item in items)
            button.Entries.Add(item);
    }

    /// <summary>The Cables drop-down: the coax sections under headers, then any catalog
    /// section no other button claims (so a new section is never lost), then the
    /// document's custom cables while there are any.</summary>
    private void FillCables()
    {
        string[] elsewhere = [Pairs, Pcb, Ideal];
        string[] preferred = [Coax50, Coax75, OtherCoax];
        var sections = _catalog.Cables
            .GroupBy(c => c.Section ?? "Cables")
            .Where(g => !elsewhere.Contains(g.Key))
            .OrderBy(g =>
            {
                int index = Array.IndexOf(preferred, g.Key);
                return index < 0 ? int.MaxValue : index;
            });

        _cables.Entries.Clear();
        foreach (var section in sections)
        {
            _cables.Entries.Add(new PaletteHeader(section.Key));
            Fill(_cables, section.Select(c => new PaletteItem(c.Name, DocumentNodeKind.Cable, c)));
        }

        if (_editor.CustomCables.Count > 0)
        {
            _cables.Entries.Add(new PaletteHeader(CustomCable.Section));
            Fill(_cables, _editor.CustomCables.Select(c => new PaletteItem(c.Name, DocumentNodeKind.Cable, c)));
        }
    }
}
