using TdrSim.Core.Model;

namespace TdrSim.Core.Project;

/// <summary>A built-in example shown in the app's Examples menu.</summary>
public sealed record ExampleProject(string Name, string Description, ProjectDocument Document);

/// <summary>
/// Curated example documents, one per teaching point - opens, mismatches, reactive
/// discontinuities, faults, loops, instruments. Built in code against the built-in
/// catalog ids; a test asserts every one compiles with zero issues and simulates.
/// </summary>
public static class ExampleProjects
{
    private const string Rg58 = "rg58-belden-9201";
    private const string Rg6 = "rg6-belden-1694a";
    private const string Rg174 = "rg174-belden-8216";
    private const string Rg213 = "rg213-belden-8267";
    private const string Lmr400 = "lmr-400";
    private const string Cat5E = "cat5e-belden-1583a";

    public static IReadOnlyList<ExampleProject> All { get; } =
    [
        new("Open-ended cable",
            "50 m of RG-58 with nothing at the end: full reflection, skin-effect edge slur and 1/√t creep.",
            Chain("tek-1502c", Cable("c1", Rg58, 50), Open("end"))),

        new("Matched termination",
            "RG-58 into its own 52 Ω: the flattest trace a lossy line allows (watch the resistive creep).",
            Chain("tek-1502c", Cable("c1", Rg58, 30), Resistor("term", 52))),

        new("Resistive mismatch",
            "RG-58 into 75 Ω: a clean plateau at ρ = (R−Z₀)/(R+Z₀).",
            Chain("tek-1502c", Cable("c1", Rg58, 30), Resistor("term", 75))),

        new("Impedance staircase",
            "52 Ω → 75 Ω → 50 Ω segments into 50 Ω: plateaus, re-reflections, and masking.",
            Chain("tek-1502c",
                Cable("c1", Rg58, 20), Cable("c2", Rg6, 20), Cable("c3", Rg213, 20),
                Resistor("term", 50))),

        new("Capacitive tap (dip)",
            "47 pF shunt at a mid-line junction: negative dip with τ = Z₀C/2, area independent of rise time.",
            CapacitiveTap()),

        new("Corroded splice",
            "A bad splice (5 Ω + 20 nH) mid-span: the classic inductive bump plus a resistive step.",
            Chain("tek-1502c",
                Cable("c1", Rg58, 25), Connector("splice", "splice-corroded"),
                Cable("c2", Rg58, 25), Resistor("term", 52))),

        new("Tee with open stubs",
            "The demo network: ρ = −1/3 at the junction, then stub echoes ping back and forth.",
            Tee(noise: false)),

        new("Bridged tap (CATV)",
            "75 Ω drop with an unterminated tap off the line: the ghost every cable tech knows.",
            BridgedTap()),

        new("Ring loop",
            "Two paths joined at both ends: the multi-bounce lattice climb no bounce diagram wants to draw.",
            Ring()),

        new("Water ingress fault",
            "2 m of soaked dielectric: lower Z₀, slower propagation, extra loss - a sagging dip.",
            WaterIngress()),

        new("Series resistance fault",
            "10 Ω in the signal path: a small step at ρ = R/(R+2Z₀) that is easy to miss.",
            SeriesFault()),

        new("Long run to a short (LMR-400)",
            "200 m into a short: the trace ramps to the R_dc divider level, not to −1 - DC settling in action.",
            Chain("tek-1502c", Cable("c1", Lmr400, 200), Load("end", LoadKind.Short))),

        new("Twisted pair, handheld tester",
            "60 m of Cat5e on a 2 ns handheld: the 50 → 100 Ω step at the front, then the open far end.",
            Chain("cable-tester", Cable("c1", Cat5E, 60), Open("end"))),

        new("Fast lab TDR, 2 m patch",
            "23 ps edge through a BNC barrel into 2 m of RG-174: connector parasitics become visible.",
            Chain("tek-80e04", Connector("bnc", "bnc-barrel"), Cable("c1", Rg174, 2), Open("end"))),

        new("Noisy sweep with averaging",
            "The tee with 2 mV RMS noise averaged over 4 sweeps - crank the averaging to clean it up.",
            Tee(noise: true)),
    ];

    // ---- document builders ----

    private static DocumentNode Tdr() => new() { Id = "tdr", Kind = DocumentNodeKind.Tdr, X = 40, Y = 200 };

    private static DocumentNode Cable(string id, string preset, double lengthM, double x = 0, double y = 200)
        => new() { Id = id, Kind = DocumentNodeKind.Cable, CablePresetId = preset, LengthM = lengthM, X = x, Y = y };

    private static DocumentNode Connector(string id, string preset, double x = 0, double y = 200)
        => new() { Id = id, Kind = DocumentNodeKind.Connector, ConnectorPresetId = preset, X = x, Y = y };

    private static DocumentNode Resistor(string id, double rOhms, double x = 0, double y = 200)
        => new() { Id = id, Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Resistor, ROhms = rOhms, X = x, Y = y };

    private static DocumentNode Open(string id, double x = 0, double y = 200)
        => Load(id, LoadKind.Open, x, y);

    private static DocumentNode Load(string id, LoadKind kind, double x = 0, double y = 200)
        => new() { Id = id, Kind = DocumentNodeKind.Load, LoadKind = kind, X = x, Y = y };

    private static DocumentWire W(string fromNode, string fromPort, string toNode, string toPort) => new()
    {
        FromNodeId = fromNode,
        FromPort = fromPort,
        ToNodeId = toNode,
        ToPort = toPort,
    };

    private static ProjectDocument Doc(
        string instrumentPreset, DocumentNode[] nodes, DocumentWire[] wires,
        double? noiseRmsMv = null, int? averagingSweeps = null) => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = new InstrumentDocument
        {
            PresetId = instrumentPreset,
            NoiseRmsMillivolts = noiseRmsMv,
            AveragingSweeps = averagingSweeps,
        },
        Nodes = nodes,
        Wires = wires,
    };

    /// <summary>TDR followed by two-ports in order, ending in a one-port; nodes are
    /// laid out left to right and wired A-to-B down the line.</summary>
    private static ProjectDocument Chain(string instrumentPreset, params DocumentNode[] elements)
    {
        var nodes = new List<DocumentNode> { Tdr() };
        var wires = new List<DocumentWire>();
        string previousId = "tdr";
        string previousPort = DocumentPorts.P;
        double x = 40;
        foreach (var element in elements)
        {
            x += 230;
            bool onePort = DocumentPorts.For(element).Count == 1;
            nodes.Add(element with { X = x });
            wires.Add(W(previousId, previousPort, element.Id, onePort ? DocumentPorts.P : DocumentPorts.A));
            previousId = element.Id;
            previousPort = DocumentPorts.B;
        }

        return Doc(instrumentPreset, [.. nodes], [.. wires]);
    }

    private static ProjectDocument CapacitiveTap() => Doc("tek-1502c",
        [
            Tdr(),
            Cable("c1", Rg58, 20, 270),
            new DocumentNode
            {
                Id = "tap", Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Capacitor,
                CFarads = 47e-12, X = 500, Y = 80,
            },
            Cable("c2", Rg58, 20, 500, 320),
            Resistor("term", 52, 730, 320),
        ],
        [
            W("tdr", "P", "c1", "A"),
            W("c1", "B", "tap", "P"),
            W("c1", "B", "c2", "A"),
            W("c2", "B", "term", "P"),
        ]);

    private static ProjectDocument Tee(bool noise) => Doc("tek-1502c",
        [
            Tdr(),
            Cable("feed", Rg58, 30, 270),
            Cable("stub", Rg58, 10, 540, 90),
            Cable("tail", Rg58, 20, 540, 310),
            Open("stubEnd", 790, 90),
            Open("tailEnd", 790, 310),
        ],
        [
            W("tdr", "P", "feed", "A"),
            W("feed", "B", "stub", "A"),
            W("feed", "B", "tail", "A"),
            W("stub", "B", "stubEnd", "P"),
            W("tail", "B", "tailEnd", "P"),
        ],
        noiseRmsMv: noise ? 2 : null,
        averagingSweeps: noise ? 4 : null);

    private static ProjectDocument BridgedTap() => Doc("catv-75",
        [
            Tdr(),
            Cable("drop", Rg6, 40, 270),
            Cable("tap", Rg6, 15, 540, 80),
            Cable("trunk", Rg6, 40, 540, 320),
            Open("tapEnd", 790, 80),
            Resistor("term", 75, 790, 320),
        ],
        [
            W("tdr", "P", "drop", "A"),
            W("drop", "B", "tap", "A"),
            W("drop", "B", "trunk", "A"),
            W("tap", "B", "tapEnd", "P"),
            W("trunk", "B", "term", "P"),
        ]);

    private static ProjectDocument Ring() => Doc("tek-1502c",
        [
            Tdr(),
            Cable("feed", Rg58, 30, 270),
            new DocumentNode { Id = "j1", Kind = DocumentNodeKind.Junction, X = 540, Y = 200 },
            Cable("top", Rg58, 10, 700, 80),
            Cable("bottom", Rg58, 20, 700, 320),
            new DocumentNode { Id = "j2", Kind = DocumentNodeKind.Junction, X = 950, Y = 200 },
        ],
        [
            W("tdr", "P", "feed", "A"),
            W("feed", "B", "j1", "P"),
            W("j1", "P", "top", "A"),
            W("j1", "P", "bottom", "A"),
            W("top", "B", "j2", "P"),
            W("bottom", "B", "j2", "P"),
        ]);

    private static ProjectDocument WaterIngress() => Doc("tek-1502c",
        [
            Tdr(),
            Cable("feed", Rg58, 30, 270),
            new DocumentNode
            {
                Id = "wet", Kind = DocumentNodeKind.Fault, FaultKind = Project.FaultKind.SectionOverride,
                FaultPresetId = "water-ingress", CablePresetId = Rg58,
                LengthM = 2, Z0Scale = 0.7, ExtraDielectricLossDbPer100m = 3, X = 500, Y = 200,
            },
            Cable("tail", Rg58, 20, 730),
            Open("end", 960),
        ],
        [
            W("tdr", "P", "feed", "A"),
            W("feed", "B", "wet", "A"),
            W("wet", "B", "tail", "A"),
            W("tail", "B", "end", "P"),
        ]);

    private static ProjectDocument SeriesFault() => Doc("tek-1502c",
        [
            Tdr(),
            Cable("c1", Rg58, 40, 270),
            new DocumentNode
            {
                Id = "fault", Kind = DocumentNodeKind.Fault, FaultKind = Project.FaultKind.SeriesFault,
                FaultPresetId = "series-fault", ROhms = 10, X = 500, Y = 200,
            },
            Cable("c2", Rg58, 40, 730),
            Resistor("term", 52, 960),
        ],
        [
            W("tdr", "P", "c1", "A"),
            W("c1", "B", "fault", "A"),
            W("fault", "B", "c2", "A"),
            W("c2", "B", "term", "P"),
        ]);
}
