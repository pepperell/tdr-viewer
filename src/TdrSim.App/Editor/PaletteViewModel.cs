using CommunityToolkit.Mvvm.ComponentModel;
using TdrSim.Core.Catalog;
using TdrSim.Core.Model;
using TdrSim.Core.Project;

namespace TdrSim.App.Editor;

/// <summary>A draggable palette entry. Payload: CableSpec / ConnectorSpec / LoadKind.</summary>
public sealed partial class PaletteItem(string label, DocumentNodeKind kind, object? payload) : ObservableObject
{
    public string Label { get; } = label;

    public DocumentNodeKind Kind { get; } = kind;

    public object? Payload { get; } = payload;

    [ObservableProperty]
    private bool _isEnabled = true;
}

public sealed class PaletteGroup(string header, IReadOnlyList<PaletteItem> items)
{
    public string Header { get; } = header;

    public IReadOnlyList<PaletteItem> Items { get; } = items;
}

public sealed class PaletteViewModel
{
    private readonly PaletteItem _tdrItem = new("TDR instrument", DocumentNodeKind.Tdr, null);

    public PaletteViewModel(ComponentCatalog catalog, EditorViewModel editor)
    {
        Groups =
        [
            new PaletteGroup("Instrument", [_tdrItem]),
            .. CableGroups(catalog),
            new PaletteGroup("Connectors",
                [.. catalog.Connectors.Select(c => new PaletteItem(c.Name, DocumentNodeKind.Connector, c))]),
            new PaletteGroup("Loads",
            [
                new PaletteItem("Open", DocumentNodeKind.Load, LoadKind.Open),
                new PaletteItem("Short", DocumentNodeKind.Load, LoadKind.Short),
                new PaletteItem("Resistor", DocumentNodeKind.Load, LoadKind.Resistor),
                new PaletteItem("Capacitor", DocumentNodeKind.Load, LoadKind.Capacitor),
                new PaletteItem("Inductor", DocumentNodeKind.Load, LoadKind.Inductor),
                new PaletteItem("Series RLC", DocumentNodeKind.Load, LoadKind.SeriesRlc),
                new PaletteItem("Parallel RLC", DocumentNodeKind.Load, LoadKind.ParallelRlc),
            ]),
            new PaletteGroup("Faults",
                [.. catalog.Faults.Select(f => new PaletteItem(f.Name, DocumentNodeKind.Fault, f))]),
            new PaletteGroup("Wiring",
                [new PaletteItem("Junction", DocumentNodeKind.Junction, null)]),
        ];

        editor.GraphChanged += (_, _) => _tdrItem.IsEnabled = !editor.HasTdr;
        _tdrItem.IsEnabled = !editor.HasTdr;
    }

    public IReadOnlyList<PaletteGroup> Groups { get; }

    /// <summary>One palette group per catalog section, in display order (unknown
    /// sections follow, in catalog order).</summary>
    private static IEnumerable<PaletteGroup> CableGroups(ComponentCatalog catalog)
    {
        string[] preferredOrder =
        [
            "50 Ω coax", "75 Ω coax", "Other coax", "Balanced pairs", "PCB traces", "Ideal (lossless)",
        ];
        return catalog.Cables
            .GroupBy(c => c.Section ?? "Cables")
            .OrderBy(g =>
            {
                int index = Array.IndexOf(preferredOrder, g.Key);
                return index < 0 ? int.MaxValue : index;
            })
            .Select(g => new PaletteGroup(
                g.Key, [.. g.Select(c => new PaletteItem(c.Name, DocumentNodeKind.Cable, c))]));
    }
}
