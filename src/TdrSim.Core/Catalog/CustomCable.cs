using System.Globalization;
using TdrSim.Core.Engine;

namespace TdrSim.Core.Catalog;

/// <summary>
/// User-defined cables (PLAN section 1.2): the two entry styles - datasheet (Z0, VF,
/// DC resistance, attenuation points) and raw RLGC - both land on an ordinary
/// <see cref="CableSpec"/>, stored in the project document and resolved by id exactly
/// like a catalog preset, so nothing downstream of the compiler knows the difference.
/// </summary>
public static class CustomCable
{
    public const string Section = "Custom cables";

    public const string IdPrefix = "custom-";

    public const double DbPer100FtToDbPer100M = 1 / 0.3048;

    /// <summary>Datasheet-style entry. The capacitance is derived from (Z0, VF), the
    /// engine's primary pair; an empty point list means a lossless dielectric and no
    /// skin loss (DC resistance still applies).</summary>
    public static CableSpec FromDatasheet(
        string id,
        string name,
        string category,
        double z0Ohms,
        double velocityFactor,
        double rdcLoopOhmsPerKm,
        IReadOnlyList<AttenuationPoint> points,
        string? defaultConnector) => new()
    {
        Id = id,
        Name = name,
        Category = category,
        Section = Section,
        Z0Ohms = z0Ohms,
        VelocityFactor = velocityFactor,
        CapacitancePfPerM = 1e12 / (z0Ohms * velocityFactor * ComplexMath.C0),
        RdcCenterOhmsPerKm = rdcLoopOhmsPerKm,
        RdcShieldOhmsPerKm = 0,
        Attenuation = points.Count == 0
            ? null
            : new AttenuationTable { Units = "dB_per_100m", Points = points },
        DefaultConnector = defaultConnector,
        Source = "user-defined (datasheet entry)",
    };

    /// <summary>Raw line constants: Z0 = sqrt(L/C) and VF = 1/(c*sqrt(LC)) are derived
    /// and stored as the spec's primary pair.</summary>
    public static CableSpec FromRlgc(
        string id, string name, string category, RlgcParameters rlgc, string? defaultConnector)
    {
        double z0 = Math.Sqrt(rlgc.LHenriesPerM / rlgc.CFaradsPerM);
        double vf = 1 / (ComplexMath.C0 * Math.Sqrt(rlgc.LHenriesPerM * rlgc.CFaradsPerM));
        if (vf is > 1 and <= 1 + 1e-9)
            vf = 1; // an air line lands a rounding error above c
        return new CableSpec
        {
            Id = id,
            Name = name,
            Category = category,
            Section = Section,
            Z0Ohms = z0,
            VelocityFactor = vf,
            CapacitancePfPerM = rlgc.CFaradsPerM * 1e12,
            RdcCenterOhmsPerKm = rlgc.ROhmsPerM * 1000,
            RdcShieldOhmsPerKm = 0,
            Rlgc = rlgc,
            DefaultConnector = defaultConnector,
            Source = "user-defined (RLGC entry)",
        };
    }

    /// <summary>Human-readable problems; empty means the engine can take this spec.</summary>
    public static IReadOnlyList<string> Validate(CableSpec spec)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(spec.Id))
            errors.Add("custom cable: empty id");
        if (string.IsNullOrWhiteSpace(spec.Name))
            errors.Add($"cable '{spec.Id}': needs a name");

        if (spec.Rlgc is { } r)
        {
            if (!(r.LHenriesPerM > 0) || !(r.CFaradsPerM > 0))
                errors.Add($"cable '{spec.Id}': L and C must be > 0");
            if (!(r.ROhmsPerM >= 0) || !(r.GSiemensPerM >= 0) || !(r.SkinOhmsPerMSqrtHz >= 0))
                errors.Add($"cable '{spec.Id}': R, G and the skin coefficient must be >= 0");
            if (r.TanDelta is not (>= 0 and < 1))
                errors.Add($"cable '{spec.Id}': tan δ must be in [0, 1)");
            if (r.LHenriesPerM > 0 && r.CFaradsPerM > 0
                && 1 / Math.Sqrt(r.LHenriesPerM * r.CFaradsPerM) > ComplexMath.C0 * (1 + 1e-9))
            {
                errors.Add($"cable '{spec.Id}': L·C is too small - the wave would outrun light (VF > 1)");
            }
        }

        if (!double.IsFinite(spec.Z0Ohms) || !double.IsFinite(spec.VelocityFactor)
            || !double.IsFinite(spec.RdcCenterOhmsPerKm) || !double.IsFinite(spec.RdcShieldOhmsPerKm))
        {
            errors.Add($"cable '{spec.Id}': values must be finite numbers");
            return errors;
        }

        CatalogValidator.ValidateCable(errors, spec, requireLossData: false);
        return errors;
    }

    /// <summary>
    /// Parses pasted datasheet attenuation: one "frequency(MHz) attenuation" pair per
    /// line, separated by whitespace, comma, semicolon, colon or '@'; blank lines and
    /// lines starting with '#' are skipped. Values are scaled by
    /// <paramref name="toDbPer100M"/> and returned sorted by frequency.
    /// </summary>
    public static bool TryParseAttenuation(
        string? text, double toDbPer100M, out IReadOnlyList<AttenuationPoint> points, out string error)
    {
        var list = new List<AttenuationPoint>();
        points = list;
        error = "";
        int lineNumber = 0;
        foreach (string raw in (text ?? "").Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] parts = line.Split(
                [' ', '\t', ',', ';', ':', '@'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
            {
                error = $"Attenuation line {lineNumber}: expected \"MHz dB\", got \"{line}\".";
                return false;
            }

            if (!(mhz > 0) || !(db > 0) || !double.IsFinite(mhz) || !double.IsFinite(db))
            {
                error = $"Attenuation line {lineNumber}: frequency and attenuation must be positive.";
                return false;
            }

            list.Add(new AttenuationPoint(mhz, db * toDbPer100M));
        }

        list.Sort((a, b) => a.Mhz.CompareTo(b.Mhz));
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].Mhz == list[i - 1].Mhz)
            {
                error = $"Attenuation: {list[i].Mhz:0.###} MHz is listed twice.";
                return false;
            }

            if (list[i].Db < list[i - 1].Db)
            {
                error = $"Attenuation must not fall with frequency ({list[i].Mhz:0.###} MHz is below {list[i - 1].Mhz:0.###} MHz).";
                return false;
            }
        }

        return true;
    }

    /// <summary>The k1*sqrt(f) + k2*f fit the engine will use, and its worst miss
    /// against the entered points (dB/100 m).</summary>
    public static (LossCoefficients Fit, double MaxErrorDb) FitQuality(IReadOnlyList<AttenuationPoint> points)
    {
        var fit = LossFit.FitPoints(points.Select(p => (p.Mhz, p.Db)).ToArray());
        double worst = 0;
        foreach (var p in points)
        {
            double model = fit.K1DbPer100mSqrtMhz * Math.Sqrt(p.Mhz) + fit.K2DbPer100mMhz * p.Mhz;
            worst = Math.Max(worst, Math.Abs(model - p.Db));
        }

        return (fit, worst);
    }

    /// <summary>Matched-line attenuation of a spec at one frequency, dB/100 m (the
    /// low-loss formula; a readout for the entry form, not what the engine evaluates).</summary>
    public static double AttenuationDbPer100M(CableSpec spec, double fHz)
    {
        var model = CableModel.FromSpec(spec);
        double nepersPerM = (model.R0PerM + model.RsOhmsPerMSqrtHz * Math.Sqrt(fHz)) / (2 * model.Z0)
            + model.G0PerM * model.Z0 / 2
            + Math.PI * fHz * model.CPerM * model.Z0 * model.TanDelta;
        return nepersPerM / ComplexMath.NepersPerDb * 100;
    }

    /// <summary>First free "custom-N" id.</summary>
    public static string NewId(IEnumerable<string> takenIds)
    {
        var taken = takenIds.ToHashSet(StringComparer.Ordinal);
        for (int n = 1; ; n++)
        {
            string id = IdPrefix + n.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(id))
                return id;
        }
    }
}
