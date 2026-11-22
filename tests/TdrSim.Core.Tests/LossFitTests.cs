using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;

namespace TdrSim.Core.Tests;

public class LossFitTests
{
    [Fact]
    public void ExactTwoTermDataIsRecoveredExactly()
    {
        const double k1 = 0.55, k2 = 0.004;
        var points = new double[] { 1, 5, 20, 100, 400, 1000 }
            .Select(f => (f, k1 * Math.Sqrt(f) + k2 * f))
            .ToArray();
        var fit = LossFit.FitPoints(points);
        Assert.Equal(k1, fit.K1DbPer100mSqrtMhz, k1 * 1e-9);
        Assert.Equal(k2, fit.K2DbPer100mMhz, k2 * 1e-9);
    }

    [Fact]
    public void SinglePointFitsSkinTermOnly()
    {
        var fit = LossFit.FitPoints([(100.0, 12.5)]);
        Assert.Equal(1.25, fit.K1DbPer100mSqrtMhz, 1e-12);
        Assert.Equal(0, fit.K2DbPer100mMhz);
    }

    [Fact]
    public void Lmr400FitRecoversDatasheetClosedForm()
    {
        // Times Microwave closed form: 0.122290*sqrt(f) + 0.000260*f dB/100ft.
        // The catalog table is that curve rounded to 0.1 dB/100m, which limits how
        // precisely k2 (the small term) can be recovered - hence 1 % / 3 %.
        const double feetPerMeter = 3.280839895;
        const double k1Ref = 0.122290 * feetPerMeter;
        const double k2Ref = 0.000260 * feetPerMeter;

        var lmr400 = TestNetworks.BuiltInCatalog.Cables.Single(c => c.Id == "lmr-400");
        var fit = LossFit.FromSpec(lmr400);

        Assert.Equal(k1Ref, fit.K1DbPer100mSqrtMhz, k1Ref * 0.01);
        Assert.Equal(k2Ref, fit.K2DbPer100mMhz, k2Ref * 0.03);

        foreach (var p in lmr400.Attenuation!.Points)
        {
            double model = fit.K1DbPer100mSqrtMhz * Math.Sqrt(p.Mhz) + fit.K2DbPer100mMhz * p.Mhz;
            Assert.Equal(p.Db, model, 0.05);
        }
    }

    [Fact]
    public void TiaThreeTermRefitsWithinDocumentedError()
    {
        // The k3/sqrt(f) term has no causal RLGC mapping; the 2-term refit must stay
        // within 0.4 dB/100 m of the 3-term curve over the structured-cabling band.
        var cat5E = TestNetworks.BuiltInCatalog.Cables.Single(c => c.Id == "cat5e-belden-1583a");
        var spec = cat5E with { Attenuation = null };
        var fit = LossFit.FromSpec(spec);
        var il = spec.InsertionLossCoefficients!;

        for (double f = 1; f <= 250; f *= 1.2)
        {
            double tia = LossFit.TiaInsertionLoss(il, f);
            double twoTerm = fit.K1DbPer100mSqrtMhz * Math.Sqrt(f) + fit.K2DbPer100mMhz * f;
            Assert.True(Math.Abs(tia - twoTerm) < 0.4,
                $"2-term refit off by {Math.Abs(tia - twoTerm):f3} dB/100m at {f:f1} MHz.");
        }
    }

    [Fact]
    public void UnitConversionsAreCorrect()
    {
        var c = new LossCoefficients(1.0, 1.0);
        // dB -> Np: /8.6859; per 100 m -> per m: /100; per sqrt(MHz) -> per sqrt(Hz): /1000.
        Assert.Equal(1.1512925464970229e-6, c.K1NpPerMSqrtHz, 1e-18);
        // per MHz -> per Hz: /1e6.
        Assert.Equal(1.1512925464970229e-9, c.K2NpPerMHz, 1e-21);
    }

    [Fact]
    public void NoLossDataMeansLossless()
    {
        var spec = TestNetworks.Lossless(50, 0.66);
        var fit = LossFit.FromSpec(spec);
        Assert.Equal(0, fit.K1DbPer100mSqrtMhz);
        Assert.Equal(0, fit.K2DbPer100mMhz);
    }
}
