using TdrSim.Core.Catalog;

namespace TdrSim.Core.Tests;

public class CatalogTests
{
    private static readonly ComponentCatalog BuiltIn = CatalogLoader.LoadEmbedded();

    [Fact]
    public void EmbeddedCatalogLoadsAndValidates()
    {
        var errors = CatalogValidator.Validate(BuiltIn);
        Assert.Empty(errors);
    }

    [Fact]
    public void ExpectedPresetsArePresent()
    {
        string[] cables =
        [
            "rg58-belden-9201", "rg6-belden-1694a", "rg174-belden-8216",
            "rg213-belden-8267", "lmr-400", "cat5e-belden-1583a", "cat6-belden-2413",
            "rg8-belden-8237", "lmr-240", "lmr-600", "rg142", "rg316", "rg178",
            "rg59-belden-8241", "rg11-foam", "rg179", "rg62-au",
            "cat3-tia", "cat6a-tia", "rs485-belden-9841", "twinlead-300",
            "ideal-50", "ideal-75",
            "fr4-microstrip-50", "fr4-stripline-50", "fr4-diffpair-100",
            "ro4350-microstrip-50", "flex-microstrip-50",
        ];
        Assert.Equal(cables.Order(), BuiltIn.Cables.Select(c => c.Id).Order());

        string[] instruments = ["tek-80e04", "tek-80e10b", "tek-1502c", "catv-75", "cable-tester"];
        Assert.Equal(instruments.Order(), BuiltIn.Instruments.Select(i => i.Id).Order());

        string[] connectors = ["bnc-barrel", "f-connector", "splice-clean", "splice-corroded"];
        Assert.Equal(connectors.Order(), BuiltIn.Connectors.Select(c => c.Id).Order());

        string[] faults = ["water-ingress", "crush", "resistive-bridge", "series-fault"];
        Assert.Equal(faults.Order(), BuiltIn.Faults.Select(f => f.Id).Order());
    }

    // Guards against well-meaning "corrections" of datasheet values that look like typos.
    [Fact]
    public void DatasheetSpotChecks()
    {
        var rg58 = BuiltIn.Cables.Single(c => c.Id == "rg58-belden-9201");
        Assert.Equal(52.0, rg58.Z0Ohms);   // Belden 9201 really is 52 ohm, not 50

        var lmr400 = BuiltIn.Cables.Single(c => c.Id == "lmr-400");
        Assert.Equal(0.85, lmr400.VelocityFactor);

        var cat5e = BuiltIn.Cables.Single(c => c.Id == "cat5e-belden-1583a");
        Assert.NotNull(cat5e.InsertionLossCoefficients);
        Assert.Equal(new InsertionLossCoefficients(1.967, 0.023, 0.05), cat5e.InsertionLossCoefficients);

        var tek1502c = BuiltIn.Instruments.Single(i => i.Id == "tek-1502c");
        Assert.Equal(200, tek1502c.RiseTime1090Ps);
        Assert.Equal(0.3, tek1502c.StepAmplitudeVolts);
    }

    [Fact]
    public void EveryCableHasAPaletteSection()
    {
        Assert.All(BuiltIn.Cables, c => Assert.False(
            string.IsNullOrWhiteSpace(c.Section), $"{c.Id} has no palette section."));
    }

    [Fact]
    public void Fr4TraceRecoversItsLossTangent()
    {
        // The PCB entries' attenuation tables were built from eps_eff and tan d; the
        // engine's k2 -> tan d inversion must land back on FR-4's ~0.02 (and well
        // below it for the low-loss Rogers laminate).
        var fr4 = Engine.CableModel.FromSpec(BuiltIn.Cables.Single(c => c.Id == "fr4-microstrip-50"));
        Assert.InRange(fr4.TanDelta, 0.017, 0.023);

        var rogers = Engine.CableModel.FromSpec(BuiltIn.Cables.Single(c => c.Id == "ro4350-microstrip-50"));
        Assert.InRange(rogers.TanDelta, 0.002, 0.006);
        Assert.True(rogers.TanDelta < fr4.TanDelta / 3);
    }

    [Fact]
    public void DerivedCapacitanceAgreesWithTheDatasheetValue()
    {
        // The engine derives C from (Z0, VF); the datasheet pF/m is a cross-check
        // (PLAN section 1.2). A big disagreement means a typo'd Z0, VF, or C.
        foreach (var cable in BuiltIn.Cables)
        {
            double derived = 1e12 / (cable.Z0Ohms * cable.VelocityFactor * Engine.ComplexMath.C0);
            Assert.True(
                Math.Abs(derived - cable.CapacitancePfPerM) / cable.CapacitancePfPerM < 0.15,
                $"{cable.Id}: derived C {derived:f1} pF/m vs datasheet {cable.CapacitancePfPerM:f1} pF/m.");
        }
    }

    [Fact]
    public void EveryCableBuildsAModelWithCleanLoss()
    {
        foreach (var cable in BuiltIn.Cables)
        {
            var loss = Engine.LossFit.FromSpec(cable);
            Assert.True(
                double.IsFinite(loss.K1DbPer100mSqrtMhz) && loss.K1DbPer100mSqrtMhz >= 0,
                $"{cable.Id}: bad k1 {loss.K1DbPer100mSqrtMhz}.");
            Assert.True(
                double.IsFinite(loss.K2DbPer100mMhz) && loss.K2DbPer100mMhz >= 0,
                $"{cable.Id}: bad k2 {loss.K2DbPer100mMhz}.");

            var model = Engine.CableModel.FromSpec(cable);
            foreach (double fHz in new[] { 1e6, 1e9 })
            {
                var (z, y) = model.EvaluatePerMeter(fHz, Engine.CableEvalOptions.Default);
                Assert.True(double.IsFinite(z.Real) && double.IsFinite(z.Imaginary), $"{cable.Id}: Z'({fHz}) not finite.");
                Assert.True(double.IsFinite(y.Real) && double.IsFinite(y.Imaginary), $"{cable.Id}: Y'({fHz}) not finite.");
            }
        }

        // The teaching ideals must be exactly lossless.
        var ideal = BuiltIn.Cables.Single(c => c.Id == "ideal-50");
        Assert.Equal(Engine.LossCoefficients.Lossless, Engine.LossFit.FromSpec(ideal));
    }

    [Fact]
    public void CablesRoundTripThroughJson()
    {
        var typeInfo = CatalogJsonContext.Default.CatalogFileCableSpec;
        var file = new CatalogFile<CableSpec> { SchemaVersion = 1, Items = BuiltIn.Cables };

        var json = CatalogLoader.Serialize(file, typeInfo);
        var reparsed = CatalogLoader.Deserialize(json, typeInfo);
        var jsonAgain = CatalogLoader.Serialize(reparsed, typeInfo);

        Assert.Equal(json, jsonAgain);
        Assert.Equal(BuiltIn.Cables.Count, reparsed.Items.Count);
    }

    [Fact]
    public void UnknownJsonPropertyIsRejected()
    {
        const string json = """
            { "schemaVersion": 1, "items": [ { "id": "x", "name": "X", "kind": "seriesFault",
              "defaults": { "seriesResistanceOhms": 1 }, "typoProperty": true } ] }
            """;
        Assert.ThrowsAny<Exception>(
            () => CatalogLoader.Deserialize(json, CatalogJsonContext.Default.CatalogFileFaultSpec));
    }

    [Fact]
    public void WrongSchemaVersionIsRejected()
    {
        const string json = """{ "schemaVersion": 2, "items": [] }""";
        Assert.Throws<InvalidDataException>(
            () => CatalogLoader.Deserialize(json, CatalogJsonContext.Default.CatalogFileCableSpec));
    }

    [Fact]
    public void ValidatorFlagsDuplicateIds()
    {
        var cable = BuiltIn.Cables[0];
        var catalog = BuiltIn with { Cables = [cable, cable] };
        Assert.Contains(CatalogValidator.Validate(catalog), e => e.Contains("duplicate id"));
    }

    [Fact]
    public void ValidatorFlagsBadVelocityFactor()
    {
        var cable = BuiltIn.Cables[0] with { VelocityFactor = 1.2 };
        var catalog = BuiltIn with { Cables = [cable] };
        Assert.Contains(CatalogValidator.Validate(catalog), e => e.Contains("velocityFactor"));
    }

    [Fact]
    public void ValidatorFlagsUnsortedAttenuation()
    {
        var cable = BuiltIn.Cables[0] with
        {
            Attenuation = new AttenuationTable
            {
                Units = "dB_per_100m",
                Points = [new(100, 12.5), new(10, 3.61)],
            },
        };
        var catalog = BuiltIn with { Cables = [cable] };
        Assert.Contains(CatalogValidator.Validate(catalog), e => e.Contains("not sorted"));
    }

    [Fact]
    public void ValidatorFlagsCableWithNoLossData()
    {
        var cable = BuiltIn.Cables[0] with { Attenuation = null, InsertionLossCoefficients = null };
        var catalog = BuiltIn with { Cables = [cable] };
        Assert.Contains(CatalogValidator.Validate(catalog), e => e.Contains("attenuation table or insertion-loss"));
    }
}
