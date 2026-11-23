using System.Numerics;
using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using TdrSim.Core.Project;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// User-defined cables: both entry styles must land on the same engine quantities a
/// catalog preset with the same data would, raw line constants must behave like the
/// textbook RLGC line (including DC leakage, which no catalog cable has), and the
/// document path must resolve, validate and round-trip them.
/// </summary>
public class CustomCableTests
{
    private static readonly CableSpec Rg58 = BuiltInCatalog.Cables.Single(c => c.Id == "rg58-belden-9201");

    private static RlgcParameters Rlgc(
        double r = 0, double lNh = 250, double g = 0, double cPf = 100, double skin = 0, double tan = 0) => new()
    {
        ROhmsPerM = r,
        LHenriesPerM = lNh * 1e-9,
        GSiemensPerM = g,
        CFaradsPerM = cPf * 1e-12,
        SkinOhmsPerMSqrtHz = skin,
        TanDelta = tan,
    };

    // ---- datasheet entry ----

    [Fact]
    public void DatasheetEntryMatchesTheCatalogPresetWithTheSameData()
    {
        var custom = CustomCable.FromDatasheet(
            "custom-1", "my RG-58", "coax", Rg58.Z0Ohms, Rg58.VelocityFactor,
            Rg58.RdcCenterOhmsPerKm + Rg58.RdcShieldOhmsPerKm, Rg58.Attenuation!.Points, "bnc-50");
        Assert.Empty(CustomCable.Validate(custom));

        var expected = CableModel.FromSpec(Rg58 with { DielectricConstant = null });
        var actual = CableModel.FromSpec(custom);
        Assert.Equal(expected.RsOhmsPerMSqrtHz, actual.RsOhmsPerMSqrtHz, 1e-15);
        Assert.Equal(expected.TanDelta, actual.TanDelta, 1e-15);
        Assert.Equal(expected.R0PerM, actual.R0PerM, 1e-15);
        Assert.Equal(CustomCable.Section, custom.Section);
    }

    [Fact]
    public void DatasheetEntryWithoutPointsIsLosslessApartFromDcResistance()
    {
        var custom = CustomCable.FromDatasheet("custom-1", "bare", "coax", 93, 0.8, 120, [], null);
        Assert.Empty(CustomCable.Validate(custom));
        var model = CableModel.FromSpec(custom);
        Assert.Equal(0, model.RsOhmsPerMSqrtHz);
        Assert.Equal(0, model.TanDelta);
        Assert.Equal(0.12, model.R0PerM, 1e-12);
    }

    // ---- raw RLGC ----

    [Fact]
    public void RlgcDerivesTheImpedanceAndVelocity()
    {
        var custom = CustomCable.FromRlgc("custom-1", "line", "coax", Rlgc(), null);
        Assert.Empty(CustomCable.Validate(custom));
        Assert.Equal(50, custom.Z0Ohms, 1e-9);
        Assert.Equal(1 / (C0 * Math.Sqrt(250e-9 * 100e-12)), custom.VelocityFactor, 1e-12);

        var model = CableModel.FromSpec(custom);
        Assert.Equal(250e-9, model.LPerM, 1e-18);
        Assert.Equal(100e-12, model.CPerM, 1e-21);
    }

    [Fact]
    public void RlgcReproducesAFittedCableWhenGivenItsDerivedConstants()
    {
        // The datasheet path fits (Rs, tan delta); feeding those same numbers in as
        // raw constants must evaluate to the same per-metre Z'(f), Y'(f).
        var reference = CableModel.FromSpec(Rg58);
        var custom = CustomCable.FromRlgc("custom-1", "rg58 as rlgc", "coax", new RlgcParameters
        {
            ROhmsPerM = reference.R0PerM,
            LHenriesPerM = reference.LPerM,
            GSiemensPerM = 0,
            CFaradsPerM = reference.CPerM,
            SkinOhmsPerMSqrtHz = reference.RsOhmsPerMSqrtHz,
            TanDelta = reference.TanDelta,
        }, null) with { DielectricConstant = Rg58.DielectricConstant };
        var model = CableModel.FromSpec(custom);

        foreach (double f in new[] { 0.0, 1e3, 1e6, 1e8, 3e9 })
        {
            var (ze, ye) = reference.EvaluatePerMeter(f, CableEvalOptions.Default);
            var (za, ya) = model.EvaluatePerMeter(f, CableEvalOptions.Default);
            Assert.True(Complex.Abs(ze - za) <= 1e-9 * (1 + Complex.Abs(ze)), $"Z' differs at {f} Hz");
            Assert.True(Complex.Abs(ye - ya) <= 1e-9 * (1e-12 + Complex.Abs(ye)), $"Y' differs at {f} Hz");
        }
    }

    [Theory]
    [InlineData(0.5, 2e-4)]   // leaky and resistive: Zin = sqrt(R/G)*coth(sqrt(RG)*l)
    [InlineData(0.0, 2e-4)]   // leaky only: Zin = 1/(G*l)
    public void LeakyLineSettlesAtItsDcInputResistance(double rPerM, double gPerM)
    {
        const double length = 20;
        var custom = CustomCable.FromRlgc("custom-1", "leaky", "coax", Rlgc(r: rPerM, g: gPerM), null);
        var trace = TdrSimulator.Simulate(
            Network(
                new SourcePort(N(0)),
                new CableSegment(N(0), N(1), custom, length),
                new Load(N(1), LoadKind.Open)),
            Tdr1502C);

        double zin = rPerM == 0
            ? 1 / (gPerM * length)
            : Math.Sqrt(rPerM / gPerM) / Math.Tanh(Math.Sqrt(rPerM * gPerM) * length);
        Assert.Equal((zin - 50) / (zin + 50), trace.DcReflection, 1e-9);
        AssertHermitian(trace);
    }

    [Fact]
    public void LeakyLineAgreesOnBothSolverPaths()
    {
        // A zero-ohm-ish stub off a junction forces the nodal solver; the chain and
        // nodal stamps of a line with G0 must describe the same network.
        var custom = CustomCable.FromRlgc("custom-1", "leaky", "coax", Rlgc(r: 0.3, g: 1e-4, skin: 2e-5, tan: 1e-3), null);
        var chain = TdrSimulator.Simulate(
            Network(
                new SourcePort(N(0)),
                new CableSegment(N(0), N(1), custom, 12),
                new Load(N(1), LoadKind.Resistor, ROhms: 75)),
            Tdr1502C);
        var nodal = TdrSimulator.Simulate(
            Network(
                new SourcePort(N(0)),
                new CableSegment(N(0), N(1), custom, 12),
                new Load(N(1), LoadKind.Resistor, ROhms: 150),
                new Load(N(1), LoadKind.Resistor, ROhms: 150),
                new CableSegment(N(1), N(2), Lossless(50, 0.66), 1e-6),
                new CableSegment(N(1), N(3), Lossless(50, 0.66), 1e-6)),
            Tdr1502C);

        Assert.Equal(SolverPath.Chain, chain.Solver);
        Assert.Equal(SolverPath.Nodal, nodal.Solver);
        Assert.Equal(chain.DcReflection, nodal.DcReflection, 1e-9);
        for (int k = 0; k < chain.Rho.Length; k += 37)
            Assert.Equal(chain.Rho[k], nodal.Rho[k], 1e-6);
    }

    [Fact]
    public void ValidationRejectsUnphysicalConstants()
    {
        // L*C below 1/c^2: the wave would outrun light.
        Assert.NotEmpty(CustomCable.Validate(
            CustomCable.FromRlgc("custom-1", "fast", "coax", Rlgc(lNh: 100, cPf: 10), null)));
        Assert.NotEmpty(CustomCable.Validate(
            CustomCable.FromRlgc("custom-1", "neg", "coax", Rlgc(r: -1), null)));
        Assert.NotEmpty(CustomCable.Validate(
            CustomCable.FromRlgc("custom-1", "tan", "coax", Rlgc(tan: 1.5), null)));
        Assert.NotEmpty(CustomCable.Validate(
            CustomCable.FromDatasheet("custom-1", "vf", "coax", 50, 1.2, 0, [], null)));
        Assert.NotEmpty(CustomCable.Validate(
            CustomCable.FromDatasheet("custom-1", " ", "coax", 50, 0.66, 0, [], null)));
        Assert.NotEmpty(CustomCable.Validate(
            CustomCable.FromDatasheet("custom-1", "nan", "coax", double.NaN, 0.66, 0, [], null)));
    }

    [Fact]
    public void AnAirLineIsNotRejectedForRoundingAboveC()
    {
        double l = 50 / C0, c = 1 / (50 * C0);
        var custom = CustomCable.FromRlgc("custom-1", "air", "coax", new RlgcParameters
        {
            ROhmsPerM = 0, LHenriesPerM = l, GSiemensPerM = 0, CFaradsPerM = c,
        }, null);
        Assert.Empty(CustomCable.Validate(custom));
        Assert.InRange(custom.VelocityFactor, 1 - 1e-12, 1);
        CableModel.FromSpec(custom); // must not throw on DK < 1
    }

    // ---- attenuation text ----

    [Fact]
    public void AttenuationTextAcceptsCommonSeparatorsAndSorts()
    {
        Assert.True(CustomCable.TryParseAttenuation(
            "# MHz  dB/100m\n100, 16.1\r\n10\t4.9\n\n1000 : 58.5\n400@33", 1, out var points, out string error), error);
        Assert.Equal([10, 100, 400, 1000], points.Select(p => p.Mhz));
        Assert.Equal([4.9, 16.1, 33, 58.5], points.Select(p => p.Db));
    }

    [Fact]
    public void AttenuationTextConvertsPer100Feet()
    {
        Assert.True(CustomCable.TryParseAttenuation(
            "100 3.048", CustomCable.DbPer100FtToDbPer100M, out var points, out _));
        Assert.Equal(10, points[0].Db, 1e-12);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("100 abc")]
    [InlineData("100 5 7")]
    [InlineData("-1 5")]
    [InlineData("100 0")]
    [InlineData("100 5\n100 6")]
    [InlineData("10 9\n100 5")]
    public void AttenuationTextReportsBadInput(string text)
    {
        Assert.False(CustomCable.TryParseAttenuation(text, 1, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void EmptyAttenuationTextIsALosslessTable()
    {
        Assert.True(CustomCable.TryParseAttenuation("  \n", 1, out var points, out _));
        Assert.Empty(points);
    }

    [Fact]
    public void FitQualityIsExactForATwoTermTable()
    {
        AttenuationPoint[] points = [.. new double[] { 1, 10, 100, 1000 }
            .Select(f => new AttenuationPoint(f, 1.5 * Math.Sqrt(f) + 0.004 * f))];
        var (fit, worst) = CustomCable.FitQuality(points);
        Assert.Equal(1.5, fit.K1DbPer100mSqrtMhz, 1e-9);
        Assert.Equal(0.004, fit.K2DbPer100mMhz, 1e-9);
        Assert.True(worst < 1e-9);

        // The entry form's readout agrees with the table it was built from.
        var spec = CustomCable.FromDatasheet("custom-1", "t", "coax", 50, 0.66, 0, points, null);
        Assert.Equal(points[2].Db, CustomCable.AttenuationDbPer100M(spec, 100e6), 1e-6);
    }

    [Fact]
    public void NewIdSkipsTakenIds()
    {
        Assert.Equal("custom-1", CustomCable.NewId([]));
        Assert.Equal("custom-3", CustomCable.NewId(["custom-1", "custom-2", "rg58"]));
    }

    // ---- through the document ----

    private static DocumentWire W(string fromNode, string fromPort, string toNode, string toPort)
        => new() { FromNodeId = fromNode, FromPort = fromPort, ToNodeId = toNode, ToPort = toPort };

    private static ProjectDocument Doc(CableSpec[] custom, string cableId, double? z0Override = null) => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = new InstrumentDocument { PresetId = "tek-1502c", PortConnectorId = "none" },
        Nodes =
        [
            new DocumentNode { Id = "tdr", Kind = DocumentNodeKind.Tdr },
            new DocumentNode
            {
                Id = "c1", Kind = DocumentNodeKind.Cable, CablePresetId = cableId, LengthM = 10,
                Z0OverrideOhms = z0Override,
            },
            new DocumentNode { Id = "r", Kind = DocumentNodeKind.Load, LoadKind = LoadKind.Resistor, ROhms = 75 },
        ],
        Wires = [W("tdr", "P", "c1", "A"), W("c1", "B", "r", "P")],
        CustomCables = custom.Length == 0 ? null : custom,
    };

    private static CableSpec Datasheet(string id = "custom-1", string? connector = null)
        => CustomCable.FromDatasheet(id, "bench cable", "coax", 62, 0.7, 40,
            [new(10, 3), new(100, 10), new(1000, 36)], connector);

    [Fact]
    public void ACableNodeResolvesACustomCableById()
    {
        var custom = Datasheet();
        var compilation = DocumentCompiler.Compile(Doc([custom], "custom-1"), BuiltInCatalog);
        Assert.Empty(compilation.Issues);
        var segment = compilation.Model!.Elements.OfType<CableSegment>().Single();
        Assert.Same(custom, segment.Spec);
    }

    [Fact]
    public void NodeOverridesApplyOnTopOfACustomCable()
    {
        var compilation = DocumentCompiler.Compile(Doc([Datasheet()], "custom-1", z0Override: 90), BuiltInCatalog);
        Assert.Empty(compilation.Issues);
        var segment = compilation.Model!.Elements.OfType<CableSegment>().Single();
        Assert.Equal(90, segment.Spec.Z0Ohms);
        Assert.Equal(0.7, segment.Spec.VelocityFactor);
    }

    [Fact]
    public void ABrokenCustomCableInUseIsOneFatalIssueOnItsNodes()
    {
        var broken = Datasheet() with { VelocityFactor = 1.4 };
        var compilation = DocumentCompiler.Compile(Doc([broken], "custom-1"), BuiltInCatalog);
        // (The load it would have fed is separately reported as disconnected.)
        var issue = Assert.Single(compilation.Issues, i => i.IsFatal);
        Assert.Equal(DocumentIssueKind.InvalidCustomCable, issue.Kind);
        Assert.DoesNotContain(compilation.Issues, i => i.Kind == DocumentIssueKind.UnknownPresetId);
        Assert.Equal(["c1"], issue.NodeIds);
        Assert.Null(compilation.Model);
    }

    [Fact]
    public void ABrokenCustomCableNobodyUsesIsIgnored()
    {
        var broken = Datasheet("custom-2") with { VelocityFactor = 1.4 };
        var compilation = DocumentCompiler.Compile(Doc([Datasheet(), broken], "custom-1"), BuiltInCatalog);
        Assert.Empty(compilation.Issues);
        Assert.NotNull(compilation.Model);
    }

    [Fact]
    public void ACustomIdThatShadowsACatalogCableIsRejected()
    {
        var clash = Datasheet(Rg58.Id);
        var compilation = DocumentCompiler.Compile(Doc([clash], Rg58.Id), BuiltInCatalog);
        Assert.Contains(compilation.Issues, i => i.Kind == DocumentIssueKind.InvalidCustomCable);
    }

    [Fact]
    public void ACustomCablesDefaultConnectorMatesWithTheInstrument()
    {
        var document = Doc([Datasheet(connector: "bnc-50")], "custom-1") with
        {
            Instrument = new InstrumentDocument { PresetId = "tek-1502c" },
        };
        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.Empty(compilation.Issues);
        Assert.Single(compilation.InterfaceNotes);
    }

    [Fact]
    public void AFaultSectionCanBeCutFromACustomCable()
    {
        var document = Doc([Datasheet()], "custom-1");
        document = document with
        {
            Nodes =
            [
                .. document.Nodes.Where(n => n.Id != "r"),
                new DocumentNode
                {
                    Id = "wet", Kind = DocumentNodeKind.Fault, FaultKind = FaultKind.SectionOverride,
                    CablePresetId = "custom-1", LengthM = 1, Z0Scale = 0.8,
                },
            ],
            Wires = [W("tdr", "P", "c1", "A"), W("c1", "B", "wet", "A")],
        };
        var compilation = DocumentCompiler.Compile(document, BuiltInCatalog);
        Assert.Empty(compilation.Issues);
        var section = compilation.Model!.Elements.OfType<CableSegment>().Last();
        Assert.Equal(62 * 0.8, section.Spec.Z0Ohms, 1e-9);
    }

    [Fact]
    public void CustomCablesRoundTripThroughTheProjectFile()
    {
        var rlgc = CustomCable.FromRlgc("custom-2", "raw line", "pcb", Rlgc(r: 0.4, g: 1e-6, skin: 1e-5, tan: 0.02), "sma-50");
        var document = Doc([Datasheet(), rlgc], "custom-2");
        var back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(document));

        Assert.Equal(ProjectDocument.CurrentSchemaVersion, back.SchemaVersion);
        Assert.Equal(2, back.CustomCables!.Count);
        var first = back.CustomCables[0];
        Assert.Equal("bench cable", first.Name);
        Assert.Equal(document.CustomCables![0].Attenuation!.Points, first.Attenuation!.Points);
        Assert.Equal(rlgc.Rlgc, back.CustomCables[1].Rlgc);
        Assert.Equal("sma-50", back.CustomCables[1].DefaultConnector);

        // Same physics after the round trip.
        var before = DocumentCompiler.Compile(document, BuiltInCatalog);
        var after = DocumentCompiler.Compile(back, BuiltInCatalog);
        Assert.Empty(after.Issues);
        var a = TdrSimulator.Simulate(before.Model!, before.Instrument!);
        var b = TdrSimulator.Simulate(after.Model!, after.Instrument!);
        Assert.Equal(a.Rho, b.Rho);
    }

    [Fact]
    public void ADocumentWithoutCustomCablesWritesNoSuchMember()
    {
        string json = ProjectSerializer.Serialize(Doc([], Rg58.Id));
        Assert.DoesNotContain("customCables", json);
    }
}
