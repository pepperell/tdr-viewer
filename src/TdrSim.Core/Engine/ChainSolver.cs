using System.Numerics;
using TdrSim.Core.Catalog;
using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>
/// Computes the port reflection coefficient of a <see cref="Chain"/> on a frequency grid
/// by folding from the termination back to the source in homogeneous (V, I) coordinates:
/// open circuits are (1, 0) and shorts (0, 1) - no infinities anywhere - and each step
/// renormalizes so nothing can overflow. Gamma = (V - Zref*I)/(V + Zref*I) at the port.
/// </summary>
public static class ChainSolver
{
    public static Complex[] Solve(
        Chain chain,
        double zRefOhms,
        IReadOnlyList<double> freqHz,
        CableEvalOptions options,
        SimulationSession? session = null,
        FrequencyGrid? grid = null,
        CancellationToken cancellationToken = default)
    {
        if (zRefOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(zRefOhms), zRefOhms, "Reference impedance must be > 0.");
        bool useSpectra = session is not null && grid is not null;
        if (useSpectra && grid!.FrequencyCount != freqHz.Count)
            throw new ArgumentException($"Grid has {grid.FrequencyCount} bins but {freqHz.Count} frequencies were given.");

        // Fold order: terminal loads first, then each link far -> near with its near-side loads.
        var pieces = new List<Piece> { new ShuntLoadsPiece([.. chain.TerminalLoads]) };
        var modelCache = new Dictionary<CableSpec, CableModel>();
        foreach (var link in chain.Links.Reverse())
        {
            pieces.Add(link.TwoPort switch
            {
                CableSegment cable => MakeCablePiece(cable),
                Connector connector => new ConnectorPiece(connector.Spec),
                var other => throw new ArgumentException($"Unexpected two-port element {other.GetType().Name}."),
            });
            pieces.Add(new ShuntLoadsPiece([.. link.NearLoads]));
        }

        CablePiece MakeCablePiece(CableSegment cable)
        {
            var model = session is not null
                ? session.GetModel(cable.Spec)
                : modelCache.TryGetValue(cable.Spec, out var m)
                    ? m
                    : modelCache[cable.Spec] = CableModel.FromSpec(cable.Spec);
            var spectrum = useSpectra ? session!.GetSpectrum(model, grid!, options, cancellationToken) : null;
            return new CablePiece(model, cable.LengthM, options, spectrum);
        }

        var pieceArray = pieces.ToArray();
        var gamma = new Complex[freqHz.Count];
        var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, freqHz.Count, parallelOptions, n =>
        {
            double f = freqHz[n];
            Complex v = Complex.One;
            Complex i = Complex.Zero;
            foreach (var piece in pieceArray)
            {
                piece.Fold(n, f, ref v, ref i);
                double scale = Math.Max(Complex.Abs(v), Complex.Abs(i));
                if (scale > 0)
                {
                    v /= scale;
                    i /= scale;
                }
            }

            gamma[n] = (v - zRefOhms * i) / (v + zRefOhms * i);
        });

        return gamma;
    }

    private abstract class Piece
    {
        public abstract void Fold(int n, double fHz, ref Complex v, ref Complex i);
    }

    private sealed class CablePiece(
        CableModel model, double lengthM, CableEvalOptions options, CableSpectrum? spectrum) : Piece
    {
        public override void Fold(int n, double fHz, ref Complex v, ref Complex i)
        {
            var (zPerM, yPerM) = spectrum is null
                ? model.EvaluatePerMeter(fHz, options)
                : (spectrum.ZPerM[n], spectrum.YPerM[n]);
            (v, i) = TwoPort.Line(zPerM * lengthM, yPerM * lengthM).Apply(v, i);
        }
    }

    private sealed class ConnectorPiece(ConnectorSpec spec) : Piece
    {
        public override void Fold(int n, double fHz, ref Complex v, ref Complex i)
        {
            // Near-side shunt C, series R+jwL, far-side shunt C. Folding far -> near
            // applies them in reverse order.
            double w = 2 * Math.PI * fHz;
            i += new Complex(0, w * spec.ShuntCapacitanceFarPf * 1e-12) * v;
            v += new Complex(spec.SeriesResistanceOhms, w * spec.SeriesInductanceNh * 1e-9) * i;
            i += new Complex(0, w * spec.ShuntCapacitanceNearPf * 1e-12) * v;
        }
    }

    private sealed class ShuntLoadsPiece(Load[] loads) : Piece
    {
        public override void Fold(int n, double fHz, ref Complex v, ref Complex i)
        {
            foreach (var load in loads)
            {
                var (isShort, y) = LoadModel.ShuntAdmittance(load, fHz);
                if (isShort)
                {
                    // The node is forced to 0 V; any nonzero current works in
                    // homogeneous coordinates.
                    (v, i) = (Complex.Zero, Complex.One);
                }
                else
                {
                    i += y * v;
                }
            }
        }
    }
}
