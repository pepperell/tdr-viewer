using System.Globalization;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>M5 step 6: the CSV generator - column math and culture independence.</summary>
public class TraceExportTests
{
    private static TdrTrace SmallTrace() => TdrSimulator.Simulate(
        Network(new SourcePort(N(0)), new Load(N(0), LoadKind.Resistor, ROhms: 100)), Tdr1502C);

    private static string[] Lines(TdrTrace trace, double vf)
    {
        using var writer = new StringWriter();
        TraceExport.WriteCsv(writer, trace, vf);
        return writer.ToString().Split(writer.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void ColumnsMatchTheTraceArrays()
    {
        var trace = SmallTrace();
        const double vf = 0.66;
        var lines = Lines(trace, vf);

        Assert.Equal(TraceExport.CsvHeader, lines[0]);
        Assert.Equal(trace.TimeS.Length + 1, lines.Length);

        // Spot-check a mid-trace row against the arrays, including the display-VF
        // distance (not the trace's own DistanceM).
        int k = trace.TimeS.Length / 2;
        var cells = lines[k + 1].Split(',').Select(
            c => double.Parse(c, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(5, cells.Length);
        Assert.Equal(trace.TimeS[k] * 1e9, cells[0], 1e-6);
        Assert.Equal(trace.TimeS[k] * vf * C0 / 2, cells[1], 1e-6);
        Assert.Equal(trace.Rho[k], cells[2], 1e-9);
        Assert.Equal(trace.Volts[k] * 1e3, cells[3], 1e-6);
        Assert.Equal(trace.Ohms[k], cells[4], 1e-4);
    }

    [Fact]
    public void OutputIsCultureInvariant()
    {
        var trace = SmallTrace();
        var invariant = Lines(trace, 0.66);

        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE uses ',' as the decimal separator - the classic CSV corrupter.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(invariant, Lines(trace, 0.66));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
