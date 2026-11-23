using TdrSim.Core.Catalog;
using TdrSim.Core.Model;

namespace TdrSim.Core.Project;

/// <summary>
/// The serializable description of everything on the editor canvas: nodes with
/// positions and properties, the wires between their ports, and the instrument.
/// This is the .tdrproj on-disk format (schema-versioned JSON) and the single
/// input to <see cref="DocumentCompiler"/>.
/// </summary>
public sealed record ProjectDocument
{
    /// <summary>v2 (M5) added fault nodes and instrument noise; v3 added connector-type
    /// overrides on the TDR and cable ends; v4 added user-defined cables. Each older
    /// version is a strict subset and loads unchanged. Files are always written at the
    /// current version.</summary>
    public const int CurrentSchemaVersion = 4;

    public const int OldestReadableSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required InstrumentDocument Instrument { get; init; }

    public required IReadOnlyList<DocumentNode> Nodes { get; init; }

    public required IReadOnlyList<DocumentWire> Wires { get; init; }

    /// <summary>User-defined cables (schema v4), referenced from cable and fault nodes
    /// by <see cref="CableSpec.Id"/> exactly like catalog presets; null = none.</summary>
    public IReadOnlyList<CableSpec>? CustomCables { get; init; }
}

public enum DocumentNodeKind
{
    Tdr,
    Cable,
    Connector,
    Load,

    /// <summary>A purely visual wire anchor: unions everything wired to it into one
    /// electrical node and emits no element.</summary>
    Junction,

    /// <summary>A catalog fault (schema v2): compiles onto existing engine elements
    /// per <see cref="FaultKind"/>.</summary>
    Fault,
}

/// <summary>
/// The three fault shapes from the catalog. Stored on the node (not looked up from the
/// preset) so a document is self-contained: ports and compilation never need the
/// catalog's fault list.
/// </summary>
public enum FaultKind
{
    /// <summary>Dielectric-change section (water ingress, crush): Z0 and velocity both
    /// scale by z0Scale (constant L), optional extra dielectric loss.</summary>
    SectionOverride,

    /// <summary>Shunt resistance to the return at one point.</summary>
    ShuntBridge,

    /// <summary>Series resistance in the signal path.</summary>
    SeriesFault,
}

/// <summary>Fixed port names per node kind: two-ports (Cable, Connector, section/series
/// faults) expose A/B, one-ports (Tdr, Load, Junction, bridge faults) expose P.</summary>
public static class DocumentPorts
{
    public const string A = "A";
    public const string B = "B";
    public const string P = "P";

    private static readonly string[] TwoPort = [A, B];
    private static readonly string[] OnePort = [P];

    public static IReadOnlyList<string> For(DocumentNode node) => For(node.Kind, node.FaultKind);

    public static IReadOnlyList<string> For(DocumentNodeKind kind, FaultKind? faultKind = null) => kind switch
    {
        DocumentNodeKind.Cable or DocumentNodeKind.Connector => TwoPort,
        DocumentNodeKind.Fault => faultKind == Project.FaultKind.ShuntBridge ? OnePort : TwoPort,
        _ => OnePort,
    };
}

/// <summary>
/// One canvas node. Properties are a flat nullable bag rather than JSON polymorphism
/// ($type): each kind reads only its own fields and leaves the rest null, which keeps
/// the format trivially versionable and the serializer free of type discriminators.
/// </summary>
public sealed record DocumentNode
{
    public required string Id { get; init; }

    public required DocumentNodeKind Kind { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    // Cable
    public string? CablePresetId { get; init; }
    public double? LengthM { get; init; }
    public double? Z0OverrideOhms { get; init; }
    public double? VfOverride { get; init; }

    /// <summary>Connector family on each cable end (schema v3): null = the preset's
    /// default, "none" = bare end, otherwise a ConnectorFamilySpec id.</summary>
    public string? ConnectorAId { get; init; }
    public string? ConnectorBId { get; init; }

    // Load
    public LoadKind? LoadKind { get; init; }
    public double? ROhms { get; init; }
    public double? LHenries { get; init; }
    public double? CFarads { get; init; }

    // Connector
    public string? ConnectorPresetId { get; init; }

    // Fault (schema v2). SectionOverride reuses CablePresetId (base cable) and LengthM;
    // ShuntBridge/SeriesFault reuse ROhms. FaultPresetId is display/defaults provenance
    // only - the compiler never reads it.
    public FaultKind? FaultKind { get; init; }
    public string? FaultPresetId { get; init; }
    public double? Z0Scale { get; init; }
    public double? ExtraDielectricLossDbPer100m { get; init; }
}

/// <summary>A wire between two ports. Direction carries no electrical meaning.</summary>
public sealed record DocumentWire
{
    public required string FromNodeId { get; init; }
    public required string FromPort { get; init; }
    public required string ToNodeId { get; init; }
    public required string ToPort { get; init; }
}

/// <summary>Instrument preset reference plus optional overrides (null = preset value).</summary>
public sealed record InstrumentDocument
{
    public required string PresetId { get; init; }
    public double? RiseTimePsOverride { get; init; }
    public double? AmplitudeVoltsOverride { get; init; }
    public double? SourceImpedanceOverrideOhms { get; init; }

    /// <summary>Display-stage additive noise (schema v2); null or 0 = off.</summary>
    public double? NoiseRmsMillivolts { get; init; }

    /// <summary>Simulated sweep averaging (schema v2); null = 1 (no averaging).</summary>
    public int? AveragingSweeps { get; init; }

    /// <summary>Front-panel connector family (schema v3): null = the preset's default,
    /// "none" = bare terminals, otherwise a ConnectorFamilySpec id.</summary>
    public string? PortConnectorId { get; init; }

    /// <summary>Sweep mode (schema v4), display noise only: null/false = normal, the
    /// noise is re-rolled <see cref="NoiseRefreshHz"/> times a second like a
    /// free-running sweep; true = one shot, a single frozen acquisition.</summary>
    public bool? OneShot { get; init; }

    /// <summary>Normal-mode noise refresh rate (schema v4); null = 4 per second.</summary>
    public double? NoiseRefreshHz { get; init; }
}
