namespace TdrSim.Core.Catalog;

/// <summary>Envelope shared by every catalog JSON file.</summary>
public sealed record CatalogFile<T>
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<T> Items { get; init; }
}

/// <summary>One datasheet attenuation point.</summary>
public sealed record AttenuationPoint(double Mhz, double Db);

public sealed record AttenuationTable
{
    /// <summary>Only "dB_per_100m" is defined in schema v1.</summary>
    public required string Units { get; init; }

    public required IReadOnlyList<AttenuationPoint> Points { get; init; }
}

/// <summary>
/// TIA/EIA-568 style insertion loss model: IL = k1*sqrt(f) + k2*f + k3/sqrt(f) dB/100 m,
/// f in MHz. Used for twisted-pair entries.
/// </summary>
public sealed record InsertionLossCoefficients(double K1, double K2, double K3);

/// <summary>
/// Datasheet-style cable description. Derived engine quantities (skin coefficient Rs,
/// k1/k2 fit, Djordjevic-Sarkar dielectric parameters) are computed in the engine (M1),
/// never stored here.
/// </summary>
public sealed record CableSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>"coax", "twisted-pair", or "pcb".</summary>
    public required string Category { get; init; }

    /// <summary>Palette section header this cable is listed under (display only).</summary>
    public string? Section { get; init; }

    public required double Z0Ohms { get; init; }
    public required double VelocityFactor { get; init; }
    public required double CapacitancePfPerM { get; init; }

    /// <summary>
    /// DC resistance of each conductor. For coax: center conductor and shield. For
    /// twisted pair both fields carry the per-conductor value. Loop resistance is
    /// always the sum of the two.
    /// </summary>
    public required double RdcCenterOhmsPerKm { get; init; }
    public required double RdcShieldOhmsPerKm { get; init; }

    public double? DielectricConstant { get; init; }

    /// <summary>At least one of <see cref="Attenuation"/> /
    /// <see cref="InsertionLossCoefficients"/> must be present.</summary>
    public AttenuationTable? Attenuation { get; init; }

    public InsertionLossCoefficients? InsertionLossCoefficients { get; init; }

    public string? Source { get; init; }
}

/// <summary>Lumped connector/splice model: series R+L, shunt C on each side.</summary>
public sealed record ConnectorSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double SeriesResistanceOhms { get; init; }
    public required double SeriesInductanceNh { get; init; }
    public required double ShuntCapacitanceNearPf { get; init; }
    public required double ShuntCapacitanceFarPf { get; init; }
    public string? Source { get; init; }
}

/// <summary>TDR instrument preset.</summary>
public sealed record InstrumentSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Step amplitude launched into a matched load (the datasheet convention,
    /// e.g. "250 mV into 50 ohm").</summary>
    public required double StepAmplitudeVolts { get; init; }

    public required double RiseTime1090Ps { get; init; }
    public required double SourceImpedanceOhms { get; init; }

    /// <summary>Display-referred RMS noise of a single sweep for this instrument
    /// class, in mV. Applied automatically; the document can override (0 = off).</summary>
    public double NoiseFloorMillivolts { get; init; }

    public string? Source { get; init; }
}

/// <summary>Parameterized fault preset (consumed by the editor in M5).</summary>
public sealed record FaultSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>"sectionOverride", "shuntBridge", or "seriesFault".</summary>
    public required string Kind { get; init; }

    public required FaultDefaults Defaults { get; init; }
}

public sealed record FaultDefaults
{
    public double? LengthM { get; init; }
    public double? Z0Scale { get; init; }
    public double? ExtraDielectricLossDbPer100m { get; init; }
    public double? BridgeResistanceOhms { get; init; }
    public double? SeriesResistanceOhms { get; init; }
}

/// <summary>The full built-in catalog.</summary>
public sealed record ComponentCatalog
{
    public required IReadOnlyList<CableSpec> Cables { get; init; }
    public required IReadOnlyList<ConnectorSpec> Connectors { get; init; }
    public required IReadOnlyList<InstrumentSpec> Instruments { get; init; }
    public required IReadOnlyList<FaultSpec> Faults { get; init; }
}
