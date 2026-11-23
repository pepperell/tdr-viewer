namespace TdrSim.Core.Catalog;

public static class CatalogValidator
{
    private static readonly string[] CableCategories = ["coax", "twisted-pair", "pcb"];
    private static readonly string[] FaultKinds = ["sectionOverride", "shuntBridge", "seriesFault"];

    /// <summary>Returns a list of human-readable problems; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(ComponentCatalog catalog)
    {
        var errors = new List<string>();

        CheckIds(errors, "cable", catalog.Cables.Select(c => c.Id));
        CheckIds(errors, "connector", catalog.Connectors.Select(c => c.Id));
        CheckIds(errors, "instrument", catalog.Instruments.Select(i => i.Id));
        CheckIds(errors, "fault", catalog.Faults.Select(f => f.Id));
        CheckIds(errors, "connector family", catalog.ConnectorFamilies.Select(f => f.Id));
        CheckIds(errors, "adapter", catalog.Adapters.Select(a => a.Id));

        var familyIds = catalog.ConnectorFamilies.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        if (familyIds.Contains("none"))
            errors.Add("connector family 'none': the id is reserved for bare ends");

        foreach (var cable in catalog.Cables)
        {
            ValidateCable(errors, cable);
        }

        foreach (var connector in catalog.Connectors)
        {
            if (connector.SeriesResistanceOhms < 0 || connector.SeriesInductanceNh < 0 ||
                connector.ShuntCapacitanceNearPf < 0 || connector.ShuntCapacitanceFarPf < 0)
            {
                errors.Add($"connector '{connector.Id}': negative element value");
            }
        }

        foreach (var instrument in catalog.Instruments)
        {
            if (instrument.StepAmplitudeVolts <= 0)
                errors.Add($"instrument '{instrument.Id}': stepAmplitudeVolts must be > 0");
            if (instrument.RiseTime1090Ps <= 0)
                errors.Add($"instrument '{instrument.Id}': riseTime1090Ps must be > 0");
            if (instrument.SourceImpedanceOhms <= 0)
                errors.Add($"instrument '{instrument.Id}': sourceImpedanceOhms must be > 0");
            if (instrument.NoiseFloorMillivolts < 0)
                errors.Add($"instrument '{instrument.Id}': noiseFloorMillivolts must be >= 0");
        }

        foreach (var fault in catalog.Faults)
        {
            if (!FaultKinds.Contains(fault.Kind))
                errors.Add($"fault '{fault.Id}': unknown kind '{fault.Kind}'");
            if (fault.Defaults.Z0Scale is <= 0)
                errors.Add($"fault '{fault.Id}': z0Scale must be > 0");
            if (fault.Defaults.LengthM is <= 0)
                errors.Add($"fault '{fault.Id}': lengthM must be > 0");
        }

        foreach (var family in catalog.ConnectorFamilies)
        {
            if (family.NominalZ0Ohms is < 1 or > 1000 || family.MatedZ0Ohms is < 1 or > 1000)
                errors.Add($"connector family '{family.Id}': impedance out of range [1, 1000]");
            if (family.VelocityFactor is <= 0 or > 1)
                errors.Add($"connector family '{family.Id}': velocityFactor out of range (0, 1]");
            if (family.MatedLengthMm <= 0)
                errors.Add($"connector family '{family.Id}': matedLengthMm must be > 0");
            if (family.ExcessInductanceNh < 0 || family.ExcessCapacitancePf < 0)
                errors.Add($"connector family '{family.Id}': negative excess reactance");
            if (family.InsertionLossDbAt1Ghz <= 0)
                errors.Add($"connector family '{family.Id}': insertionLossDbAt1Ghz must be > 0");
            if (family.MaxFrequencyGhz <= 0)
                errors.Add($"connector family '{family.Id}': maxFrequencyGhz must be > 0");
        }

        foreach (var adapter in catalog.Adapters)
        {
            foreach (string familyId in new[] { adapter.FamilyA, adapter.FamilyB })
            {
                if (!familyIds.Contains(familyId))
                    errors.Add($"adapter '{adapter.Id}': unknown family '{familyId}'");
                else if (catalog.ConnectorFamilies.First(f => f.Id == familyId).Balanced)
                    errors.Add($"adapter '{adapter.Id}': family '{familyId}' is balanced (needs a balun, not an adapter)");
            }

            if (adapter.FamilyA == adapter.FamilyB)
                errors.Add($"adapter '{adapter.Id}': joins a family to itself");
            if (adapter.Z0Ohms is < 1 or > 1000)
                errors.Add($"adapter '{adapter.Id}': z0Ohms out of range [1, 1000]");
            if (adapter.VelocityFactor is <= 0 or > 1)
                errors.Add($"adapter '{adapter.Id}': velocityFactor out of range (0, 1]");
            if (adapter.LengthMm <= 0)
                errors.Add($"adapter '{adapter.Id}': lengthMm must be > 0");
            if (adapter.ExcessInductanceNh < 0 || adapter.ExcessCapacitancePf < 0)
                errors.Add($"adapter '{adapter.Id}': negative excess reactance");
            if (adapter.InsertionLossDbAt1Ghz <= 0)
                errors.Add($"adapter '{adapter.Id}': insertionLossDbAt1Ghz must be > 0");
            if (adapter.MaxFrequencyGhz <= 0)
                errors.Add($"adapter '{adapter.Id}': maxFrequencyGhz must be > 0");
        }

        var adapterPairs = new HashSet<(string, string)>();
        foreach (var adapter in catalog.Adapters)
        {
            var pair = string.CompareOrdinal(adapter.FamilyA, adapter.FamilyB) <= 0
                ? (adapter.FamilyA, adapter.FamilyB)
                : (adapter.FamilyB, adapter.FamilyA);
            if (!adapterPairs.Add(pair))
                errors.Add($"adapter '{adapter.Id}': duplicate adapter for {pair.Item1} <-> {pair.Item2}");
        }

        foreach (var cable in catalog.Cables)
        {
            if (cable.DefaultConnector is { } dc && !familyIds.Contains(dc))
                errors.Add($"cable '{cable.Id}': unknown defaultConnector '{dc}'");
        }

        foreach (var instrument in catalog.Instruments)
        {
            if (instrument.PortConnector is { } pc && !familyIds.Contains(pc))
                errors.Add($"instrument '{instrument.Id}': unknown portConnector '{pc}'");
        }

        return errors;
    }

    /// <summary>Catalog cables must carry loss data; user-defined ones may be lossless
    /// or carry raw line constants instead.</summary>
    internal static void ValidateCable(List<string> errors, CableSpec cable, bool requireLossData = true)
    {
        if (!CableCategories.Contains(cable.Category))
            errors.Add($"cable '{cable.Id}': unknown category '{cable.Category}'");
        if (cable.Z0Ohms is < 1 or > 1000)
            errors.Add($"cable '{cable.Id}': z0Ohms out of range [1, 1000]");
        if (cable.VelocityFactor is <= 0 or > 1)
            errors.Add($"cable '{cable.Id}': velocityFactor out of range (0, 1]");
        if (cable.CapacitancePfPerM <= 0)
            errors.Add($"cable '{cable.Id}': capacitancePfPerM must be > 0");
        if (cable.RdcCenterOhmsPerKm < 0 || cable.RdcShieldOhmsPerKm < 0)
            errors.Add($"cable '{cable.Id}': negative DC resistance");
        if (cable.DielectricConstant is < 1)
            errors.Add($"cable '{cable.Id}': dielectricConstant must be >= 1");
        if (requireLossData && cable.Attenuation is null && cable.InsertionLossCoefficients is null)
            errors.Add($"cable '{cable.Id}': needs attenuation table or insertion-loss coefficients");

        if (cable.Attenuation is { } att)
        {
            if (att.Units != "dB_per_100m")
                errors.Add($"cable '{cable.Id}': unsupported attenuation units '{att.Units}'");
            if (att.Points.Count == 0)
                errors.Add($"cable '{cable.Id}': attenuation table is empty");

            for (int i = 0; i < att.Points.Count; i++)
            {
                if (att.Points[i].Mhz <= 0 || att.Points[i].Db <= 0)
                    errors.Add($"cable '{cable.Id}': attenuation point {i} must be positive");
                if (i > 0 && att.Points[i].Mhz <= att.Points[i - 1].Mhz)
                    errors.Add($"cable '{cable.Id}': attenuation points not sorted by MHz");
                if (i > 0 && att.Points[i].Db < att.Points[i - 1].Db)
                    errors.Add($"cable '{cable.Id}': attenuation must not decrease with frequency");
            }
        }

        if (cable.InsertionLossCoefficients is { } il && (il.K1 < 0 || il.K2 < 0 || il.K3 < 0))
        {
            errors.Add($"cable '{cable.Id}': negative insertion-loss coefficient");
        }
    }

    private static void CheckIds(List<string> errors, string kind, IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                errors.Add($"{kind}: empty id");
            else if (!seen.Add(id))
                errors.Add($"{kind} '{id}': duplicate id");
        }
    }
}
