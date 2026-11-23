using TdrSim.Core.Catalog;

namespace TdrSim.App.Editor;

/// <summary>One entry of a connector-type combo: "none" = bare end, otherwise a
/// ConnectorFamilySpec id. Combos show the EFFECTIVE family; the view model stores
/// null (= track the preset) whenever the selection equals the preset's default.</summary>
public sealed record ConnectorChoice(string Id, string Label, string ShortName = "")
{
    public override string ToString() => Label;
}

public static class ConnectorChoices
{
    public static IReadOnlyList<ConnectorChoice> Build(ComponentCatalog catalog) =>
    [
        new("none", "None (bare)"),
        .. catalog.ConnectorFamilies.Select(f => new ConnectorChoice(f.Id, f.Name, f.ShortName)),
    ];

    /// <summary>Human name of the effective family for an override + preset default.</summary>
    public static string EffectiveName(
        IReadOnlyList<ConnectorChoice> choices, string? overrideId, string? presetDefaultId)
    {
        string? id = overrideId ?? presetDefaultId;
        if (id is null or "none")
            return "bare";
        return choices.FirstOrDefault(c => c.Id == id)?.Label ?? id;
    }
}
