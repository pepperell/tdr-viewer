using TdrSim.Core.Model;
using TdrSim.Core.Project;

namespace TdrSim.Core.Tests;

public class ProjectDocumentTests
{
    private static ProjectDocument FullDocument() => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = new InstrumentDocument
        {
            PresetId = "tek-1502c",
            RiseTimePsOverride = 150,
            AmplitudeVoltsOverride = 0.5,
            SourceImpedanceOverrideOhms = 75,
            NoiseRmsMillivolts = 2.5,
            AveragingSweeps = 16,
        },
        Nodes =
        [
            new DocumentNode { Id = "tdr", Kind = DocumentNodeKind.Tdr, X = 1.5, Y = -2 },
            new DocumentNode
            {
                Id = "c1",
                Kind = DocumentNodeKind.Cable,
                X = 10,
                Y = 20,
                CablePresetId = "rg58-belden-9201",
                LengthM = 30,
                Z0OverrideOhms = 52,
                VfOverride = 0.7,
            },
            new DocumentNode { Id = "k1", Kind = DocumentNodeKind.Connector, ConnectorPresetId = "bnc-barrel" },
            new DocumentNode
            {
                Id = "l1",
                Kind = DocumentNodeKind.Load,
                LoadKind = LoadKind.SeriesRlc,
                ROhms = 50,
                LHenries = 1e-9,
                CFarads = 2e-12,
            },
            new DocumentNode { Id = "j1", Kind = DocumentNodeKind.Junction, X = 5, Y = 6 },
            new DocumentNode
            {
                Id = "f1",
                Kind = DocumentNodeKind.Fault,
                FaultKind = FaultKind.SectionOverride,
                FaultPresetId = "water-ingress",
                CablePresetId = "rg58-belden-9201",
                LengthM = 2,
                Z0Scale = 0.7,
                ExtraDielectricLossDbPer100m = 3,
            },
            new DocumentNode
            {
                Id = "f2",
                Kind = DocumentNodeKind.Fault,
                FaultKind = FaultKind.ShuntBridge,
                FaultPresetId = "resistive-bridge",
                ROhms = 470,
            },
        ],
        Wires =
        [
            new DocumentWire { FromNodeId = "tdr", FromPort = "P", ToNodeId = "c1", ToPort = "A" },
            new DocumentWire { FromNodeId = "c1", FromPort = "B", ToNodeId = "j1", ToPort = "P" },
        ],
    };

    [Fact]
    public void RoundTripPreservesEveryProperty()
    {
        var document = FullDocument();
        string json = ProjectSerializer.Serialize(document);
        var back = ProjectSerializer.Deserialize(json);

        // Serialize(Deserialize(json)) == json is the strongest structural check a
        // record with list members allows (record equality is reference-based there).
        Assert.Equal(json, ProjectSerializer.Serialize(back));

        var cable = back.Nodes.Single(n => n.Id == "c1");
        Assert.Equal(DocumentNodeKind.Cable, cable.Kind);
        Assert.Equal(30, cable.LengthM);
        Assert.Equal(0.7, cable.VfOverride);
        Assert.Equal(52, cable.Z0OverrideOhms);
        Assert.Equal(10, cable.X);

        var load = back.Nodes.Single(n => n.Id == "l1");
        Assert.Equal(LoadKind.SeriesRlc, load.LoadKind);
        Assert.Equal(2e-12, load.CFarads);

        Assert.Equal("bnc-barrel", back.Nodes.Single(n => n.Id == "k1").ConnectorPresetId);
        Assert.Equal(("c1", "B", "j1", "P"),
            (back.Wires[1].FromNodeId, back.Wires[1].FromPort, back.Wires[1].ToNodeId, back.Wires[1].ToPort));
        Assert.Equal(150, back.Instrument.RiseTimePsOverride);
        Assert.Equal(75, back.Instrument.SourceImpedanceOverrideOhms);
        Assert.Equal(2.5, back.Instrument.NoiseRmsMillivolts);
        Assert.Equal(16, back.Instrument.AveragingSweeps);

        var section = back.Nodes.Single(n => n.Id == "f1");
        Assert.Equal(FaultKind.SectionOverride, section.FaultKind);
        Assert.Equal("water-ingress", section.FaultPresetId);
        Assert.Equal(0.7, section.Z0Scale);
        Assert.Equal(3, section.ExtraDielectricLossDbPer100m);
        Assert.Equal(470, back.Nodes.Single(n => n.Id == "f2").ROhms);
    }

    [Fact]
    public void NewerSchemaVersionIsRejectedWithAClearMessage()
    {
        int future = ProjectDocument.CurrentSchemaVersion + 1;
        string json = ProjectSerializer.Serialize(FullDocument() with { SchemaVersion = future });
        var e = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains($"version {future}", e.Message);
    }

    [Fact]
    public void Version1DocumentStillLoads()
    {
        // A pre-M5 file: no fault nodes, no noise fields. v1 is a strict subset of v2.
        const string json = """
            {
              "schemaVersion": 1,
              "instrument": { "presetId": "tek-1502c" },
              "nodes": [
                { "id": "tdr", "kind": "Tdr", "x": 40, "y": 200 },
                { "id": "feed", "kind": "Cable", "x": 260, "y": 200,
                  "cablePresetId": "rg58-belden-9201", "lengthM": 30 }
              ],
              "wires": [
                { "fromNodeId": "tdr", "fromPort": "P", "toNodeId": "feed", "toPort": "A" }
              ]
            }
            """;
        var document = ProjectSerializer.Deserialize(json);
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(2, document.Nodes.Count);
        Assert.Null(document.Instrument.NoiseRmsMillivolts);
    }

    [Fact]
    public void MissingSchemaVersionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize("{}"));
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize("[1, 2]"));
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize("not json at all"));
    }

    [Fact]
    public void UnknownNodeKindIsRejected()
    {
        string json = ProjectSerializer.Serialize(FullDocument())
            .Replace("\"Junction\"", "\"Doodad\"");
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void UnknownPropertyIsRejected()
    {
        // Unmapped members are disallowed so a field from a future schema can't be
        // silently dropped on a load-edit-save round trip.
        int version = ProjectDocument.CurrentSchemaVersion;
        string json = ProjectSerializer.Serialize(FullDocument())
            .Replace($"\"schemaVersion\": {version},", $"\"schemaVersion\": {version}, \"futureField\": true,");
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void EmbeddedDefaultDocumentLoads()
    {
        // The shipped default deliberately stays at v1, so the v1-compat path is
        // exercised on every launch.
        var document = ProjectSerializer.LoadEmbeddedDefault();
        Assert.InRange(document.SchemaVersion,
            ProjectDocument.OldestReadableSchemaVersion, ProjectDocument.CurrentSchemaVersion);
        Assert.Contains(document.Nodes, n => n.Kind == DocumentNodeKind.Tdr);
    }
}
