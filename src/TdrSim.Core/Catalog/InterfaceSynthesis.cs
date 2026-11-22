namespace TdrSim.Core.Catalog;

/// <summary>
/// Builds the engine specs that automatic connector interfaces compile onto: a mated
/// pair or adapter body becomes a short <see cref="CableSpec"/> section at its through
/// impedance, and the residual junction parasitics become a lumped
/// <see cref="ConnectorSpec"/>.
///
/// All specs are memoized and reference-stable for the same reason as
/// <see cref="FaultSynthesis"/>: the compiler rebuilds the document on every edit, and
/// a fresh attenuation list each time would silently defeat the M4 session caches.
/// Every spec here has exactly zero DC resistance, so inserting interfaces never
/// perturbs the trace's settled DC levels - only edges and local reflections.
/// </summary>
public static class InterfaceSynthesis
{
    private const double C0 = 299_792_458.0;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CableSpec> Sections = [];
    private static readonly Dictionary<string, ConnectorSpec> Lumps = [];

    /// <summary>The mated-pair region of one connector family.</summary>
    public static CableSpec MatedSpec(ConnectorFamilySpec family) => Section(
        $"iface-mated-{family.Id}",
        $"{family.Name} mated pair",
        family.MatedZ0Ohms, family.VelocityFactor, family.MatedLengthMm,
        family.InsertionLossDbAt1Ghz);

    /// <summary>The body of a cataloged between-series adapter.</summary>
    public static CableSpec BodySpec(AdapterSpec adapter) => Section(
        $"iface-body-{adapter.Id}",
        adapter.Name,
        adapter.Z0Ohms, adapter.VelocityFactor, adapter.LengthMm,
        adapter.InsertionLossDbAt1Ghz);

    /// <summary>Fallback for coax pairs with no cataloged adapter: a representative
    /// 25 mm body at the average of the two nominal impedances.</summary>
    public static CableSpec GenericBodySpec(ConnectorFamilySpec a, ConnectorFamilySpec b)
    {
        var (x, y) = string.CompareOrdinal(a.Id, b.Id) <= 0 ? (a, b) : (b, a);
        return Section(
            $"iface-generic-{x.Id}-{y.Id}",
            $"Generic {x.Name} to {y.Name} adapter",
            (x.NominalZ0Ohms + y.NominalZ0Ohms) / 2, 0.70, 25.0, 0.10);
    }

    public static ConnectorSpec ExcessSpec(ConnectorFamilySpec family) => Lump(
        $"iface-excess-{family.Id}", $"{family.Name} junction parasitics",
        family.ExcessInductanceNh, family.ExcessCapacitancePf);

    public static ConnectorSpec ExcessSpec(AdapterSpec adapter) => Lump(
        $"iface-excess-adapter-{adapter.Id}", $"{adapter.Name} parasitics",
        adapter.ExcessInductanceNh, adapter.ExcessCapacitancePf);

    public static ConnectorSpec GenericExcessSpec(ConnectorFamilySpec a, ConnectorFamilySpec b)
    {
        var (x, y) = string.CompareOrdinal(a.Id, b.Id) <= 0 ? (a, b) : (b, a);
        return Lump($"iface-excess-generic-{x.Id}-{y.Id}", "Generic adapter parasitics", 1.0, 0.3);
    }

    private static CableSpec Section(
        string id, string name, double z0, double vf, double lengthMm, double lossDbAt1Ghz)
    {
        lock (Gate)
        {
            if (Sections.TryGetValue(id, out var hit))
                return hit;

            // sqrt(f)-shaped loss anchored at 1 GHz, rescaled from the physical length
            // to the table's per-100 m convention (huge numbers, correct at mm scale).
            double perLength = 100.0 / (lengthMm * 1e-3);
            var spec = new CableSpec
            {
                Id = id,
                Name = name,
                Category = "coax",
                Z0Ohms = z0,
                VelocityFactor = vf,
                CapacitancePfPerM = 1e12 / (z0 * vf * C0),
                RdcCenterOhmsPerKm = 0,
                RdcShieldOhmsPerKm = 0,
                DielectricConstant = 1 / (vf * vf),
                Attenuation = new AttenuationTable
                {
                    Units = "dB_per_100m",
                    Points = [.. new double[] { 100, 1000, 10000 }.Select(f =>
                        new AttenuationPoint(f, lossDbAt1Ghz * Math.Sqrt(f / 1000.0) * perLength))],
                },
            };
            Sections[id] = spec;
            return spec;
        }
    }

    private static ConnectorSpec Lump(string id, string name, double lNh, double cPf)
    {
        lock (Gate)
        {
            if (Lumps.TryGetValue(id, out var hit))
                return hit;

            var spec = new ConnectorSpec
            {
                Id = id,
                Name = name,
                SeriesResistanceOhms = 0, // contact resistance ~ milliohms: below the engine's DC identities
                SeriesInductanceNh = lNh,
                ShuntCapacitanceNearPf = cPf / 2,
                ShuntCapacitanceFarPf = cPf / 2,
            };
            Lumps[id] = spec;
            return spec;
        }
    }
}
