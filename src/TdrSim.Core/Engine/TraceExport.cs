using System.Globalization;

namespace TdrSim.Core.Engine;

/// <summary>CSV export of a trace, invariant-culture, one row per sample. Distance is
/// recomputed from the caller's display velocity factor (the panel's VF box), not the
/// trace's compiled-in one, so the file matches what is on screen.</summary>
public static class TraceExport
{
    public const string CsvHeader = "time_ns,distance_m,rho,volts_mV,ohms";

    public static void WriteCsv(TextWriter writer, TdrTrace trace, double displayVelocityFactor)
    {
        writer.WriteLine(CsvHeader);
        double metersPerSecond = displayVelocityFactor * ComplexMath.C0 / 2;
        for (int k = 0; k < trace.TimeS.Length; k++)
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{trace.TimeS[k] * 1e9:G9},{trace.TimeS[k] * metersPerSecond:G9},{trace.Rho[k]:G9},{trace.Volts[k] * 1e3:G9},{trace.Ohms[k]:G9}"));
        }
    }
}
