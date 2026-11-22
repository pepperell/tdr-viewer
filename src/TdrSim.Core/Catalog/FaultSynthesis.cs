namespace TdrSim.Core.Catalog;

/// <summary>
/// Builds the specs that fault elements compile onto.
///
/// Section overrides model a dielectric change at constant L (what water ingress and
/// crushing physically do): C scales by 1/s², so Z0 = sqrt(L/C) and v = 1/sqrt(LC)
/// both scale by s = z0Scale. Extra dielectric loss (dB/100 m at 100 MHz, linear in f)
/// is injected into the attenuation data ahead of the standard k1/k2 fit, so the
/// causal Djordjevic-Sarkar pipeline is reused untouched.
///
/// Derived section specs are memoized and reference-stable: the compiler rebuilds the
/// whole document on every edit, and a fresh AttenuationTable each time would break
/// CableSpec record equality (the points list compares by reference), silently turning
/// every live edit into a cold model + spectrum rebuild. Same inputs must return the
/// same instance so the M4 session caches keep hitting.
/// </summary>
public static class FaultSynthesis
{
    private const int CacheCapacity = 64;

    private static readonly object Gate = new();
    private static readonly Dictionary<SectionKey, CableSpec> Sections = [];
    private static readonly Queue<SectionKey> SectionOrder = new();

    public static CableSpec SectionSpec(CableSpec baseSpec, double z0Scale, double extraLossDbPer100m)
    {
        var key = new SectionKey(baseSpec, z0Scale, extraLossDbPer100m);
        lock (Gate)
        {
            if (Sections.TryGetValue(key, out var hit))
                return hit;

            var spec = BuildSection(baseSpec, z0Scale, extraLossDbPer100m);
            Sections[key] = spec;
            SectionOrder.Enqueue(key);
            while (Sections.Count > CacheCapacity)
                Sections.Remove(SectionOrder.Dequeue());
            return spec;
        }
    }

    /// <summary>Pure series R; all-value record, so equality across compiles is free.</summary>
    public static ConnectorSpec SeriesConnectorSpec(double rOhms) => new()
    {
        Id = "fault-series",
        Name = "Series resistance fault",
        SeriesResistanceOhms = rOhms,
        SeriesInductanceNh = 0,
        ShuntCapacitanceNearPf = 0,
        ShuntCapacitanceFarPf = 0,
    };

    private static CableSpec BuildSection(CableSpec baseSpec, double s, double extraDb)
    {
        var attenuation = baseSpec.Attenuation;
        var insertionLoss = baseSpec.InsertionLossCoefficients;
        if (extraDb > 0)
        {
            if (attenuation is not null)
            {
                attenuation = attenuation with
                {
                    Points = [.. attenuation.Points.Select(
                        p => new AttenuationPoint(p.Mhz, p.Db + extraDb * (p.Mhz / 100.0)))],
                };
            }
            else if (insertionLoss is not null)
            {
                // IL = k1*sqrt(f) + k2*f + k3/sqrt(f) dB/100 m, f in MHz: linear-in-f
                // extra loss normalized to 100 MHz is a k2 bump.
                insertionLoss = insertionLoss with { K2 = insertionLoss.K2 + extraDb / 100.0 };
            }
            else
            {
                // Lossless base: synthesize a purely linear table; the fit recovers
                // k1 = 0, k2 = extraDb/100 exactly.
                attenuation = new AttenuationTable
                {
                    Units = "dB_per_100m",
                    Points = [.. new double[] { 1, 10, 100, 1000 }
                        .Select(f => new AttenuationPoint(f, extraDb * (f / 100.0)))],
                };
            }
        }

        return baseSpec with
        {
            Id = $"{baseSpec.Id}#fault-s{s:R}-x{extraDb:R}",
            Name = $"{baseSpec.Name} (fault section)",
            Z0Ohms = baseSpec.Z0Ohms * s,
            VelocityFactor = baseSpec.VelocityFactor * s,
            CapacitancePfPerM = baseSpec.CapacitancePfPerM / (s * s),
            DielectricConstant = baseSpec.DielectricConstant is { } dk ? dk / (s * s) : null,
            Attenuation = attenuation,
            InsertionLossCoefficients = insertionLoss,
        };
    }

    // CableSpec record equality works here: base specs come from the catalog singleton,
    // so their attenuation point lists are reference-shared and hash consistently.
    private readonly record struct SectionKey(CableSpec BaseSpec, double Z0Scale, double ExtraLossDb);
}
