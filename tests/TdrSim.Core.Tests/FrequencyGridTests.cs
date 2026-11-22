using TdrSim.Core.Engine;

namespace TdrSim.Core.Tests;

public class FrequencyGridTests
{
    [Fact]
    public void WindowCoversSixRoundTrips()
    {
        // 100 m at VF 0.66: one-way 505 ns; window must cover 6 * 1.01 us.
        double oneWay = 100 / (0.66 * ComplexMath.C0);
        var grid = FrequencyGrid.Build(oneWay, 200e-12);
        Assert.True(grid.WindowS >= 12 * oneWay);
        Assert.Equal(200e-12 / 5, grid.DtS);
        Assert.False(grid.Compromised);
    }

    [Fact]
    public void ShortNetworksGetTheRiseTimeFloor()
    {
        var grid = FrequencyGrid.Build(0, 2e-9);
        Assert.True(grid.WindowS >= 80e-9); // 40 * rise time
        Assert.False(grid.Compromised);
    }

    [Fact]
    public void TinyEverythingGetsTheTenNanosecondFloor()
    {
        var grid = FrequencyGrid.Build(0, 20e-12);
        Assert.True(grid.WindowS >= 10e-9);
    }

    [Fact]
    public void MIsAlwaysAPowerOfTwo()
    {
        foreach (var oneWay in new[] { 0.0, 1e-9, 1e-7, 1e-5 })
        {
            var grid = FrequencyGrid.Build(oneWay, 200e-12);
            Assert.Equal(0, grid.M & (grid.M - 1));
            Assert.True(grid.M >= 1024);
        }
    }

    [Fact]
    public void CapStretchesDtAndFlagsCompromise()
    {
        // 10 km at VF 0.66 with a 23 ps instrument wants ~2^25 samples; cap at 2^18.
        double oneWay = 10_000 / (0.66 * ComplexMath.C0);
        var grid = FrequencyGrid.Build(oneWay, 23e-12);
        Assert.Equal(1 << 18, grid.M);
        Assert.True(grid.Compromised);
        Assert.True(grid.DtS > 23e-12 / 5);
        Assert.Equal(12 * oneWay, grid.WindowS, 12 * oneWay * 1e-12); // window preserved, resolution sacrificed
    }

    [Fact]
    public void WindowOverrideWins()
    {
        var grid = FrequencyGrid.Build(1e-6, 200e-12, windowOverrideS: 32768 * 40e-12);
        Assert.Equal(32768, grid.M);
        Assert.False(grid.Compromised);
    }

    [Fact]
    public void FrequencyCountIsHalfPlusOne()
    {
        var grid = FrequencyGrid.Build(0, 200e-12);
        Assert.Equal(grid.M / 2 + 1, grid.FrequencyCount);
        Assert.Equal(1.0 / grid.WindowS, grid.DfHz, 1e-3);
    }
}
