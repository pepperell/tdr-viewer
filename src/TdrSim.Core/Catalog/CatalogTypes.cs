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

    /// <summary>The connector family this cable is normally terminated with
    /// (<see cref="ConnectorFamilySpec.Id"/>), or null for bare ends (screw
    /// terminals, probed PCB traces, teaching ideals).</summary>
    public string? DefaultConnector { get; init; }

    public string? Source { get; init; }
}

/// <summary>
/// A physical connector interface family (BNC, F, SMA, ...). Where two connectorized
/// ends meet, the compiler inserts the mated pair as a short line section at
/// <see cref="MatedZ0Ohms"/> plus residual lumped parasitics - which is why a UHF
/// joint shows its famous ~35 ohm dip while a mated SMA pair is nearly invisible.
/// </summary>
public sealed record ConnectorFamilySpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The system impedance the family is sold for (display/matching).</summary>
    public required double NominalZ0Ohms { get; init; }

    /// <summary>Impedance through the mated region. Constant-impedance families equal
    /// their nominal; non-constant ones (UHF, RCA) sag well below it.</summary>
    public required double MatedZ0Ohms { get; init; }

    /// <summary>Physical length of the mated pair region.</summary>
    public required double MatedLengthMm { get; init; }

    /// <summary>Velocity factor of the connector body dielectric.</summary>
    public required double VelocityFactor { get; init; }

    /// <summary>Residual series inductance of the junction (pin/socket transition).</summary>
    public required double ExcessInductanceNh { get; init; }

    /// <summary>Residual shunt capacitance, split evenly across the two sides.</summary>
    public required double ExcessCapacitancePf { get; init; }

    /// <summary>Typical mated-pair insertion loss at 1 GHz (scaled as sqrt(f)).</summary>
    public required double InsertionLossDbAt1Ghz { get; init; }

    /// <summary>Rated usable bandwidth (informational).</summary>
    public required double MaxFrequencyGhz { get; init; }

    /// <summary>Balanced interfaces (RJ45) cannot be adapted to coax without a balun;
    /// the compiler refuses to auto-adapt them and flags the joint instead.</summary>
    public bool Balanced { get; init; }

    public string? Source { get; init; }
}

/// <summary>
/// A between-series adapter (F-to-BNC, N-to-SMA, ...): a short body section at
/// <see cref="Z0Ohms"/> that the compiler splices between the two mated pairs when
/// differently-connectorized ends meet.
/// </summary>
public sealed record AdapterSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The two <see cref="ConnectorFamilySpec.Id"/>s it joins (unordered).</summary>
    public required string FamilyA { get; init; }
    public required string FamilyB { get; init; }

    public required double Z0Ohms { get; init; }
    public required double LengthMm { get; init; }
    public required double VelocityFactor { get; init; }
    public required double ExcessInductanceNh { get; init; }
    public required double ExcessCapacitancePf { get; init; }
    public required double InsertionLossDbAt1Ghz { get; init; }
    public required double MaxFrequencyGhz { get; init; }
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

    /// <summary>The front-panel connector family (<see cref="ConnectorFamilySpec.Id"/>),
    /// or null for bare terminals. The document can override it.</summary>
    public string? PortConnector { get; init; }

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

    /// <summary>Optional so hand-built test catalogs keep compiling; empty lists
    /// simply disable automatic interface insertion.</summary>
    public IReadOnlyList<ConnectorFamilySpec> ConnectorFamilies { get; init; } = [];

    public IReadOnlyList<AdapterSpec> Adapters { get; init; } = [];
}
