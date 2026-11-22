using System.Numerics;
using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>
/// Per-frequency nodal admittance solve (SPICE-style AC analysis) for arbitrary
/// networks. Ideal shorts never enter the matrix: they merge nodes via union-find, and
/// a port merged into ground is Gamma = -1 with no solve - the MNA counterpart of the
/// chain solver's homogeneous (V, I) coordinates.
///
/// A 1e-12 S leak on every diagonal at every frequency keeps the matrix nonsingular for
/// nodes that float at DC (e.g. between two capacitors) and for lossless-loop resonances
/// landing exactly on a harmonic grid bin. Against ~0.02 S line admittances that is
/// ~1e-10 relative in Gamma, far inside every physics tolerance. (PLAN section 1.3 asked
/// for a port-only leak at f = 0; this is the documented generalization.)
/// </summary>
public static class NodalSolver
{
    private const double GMin = 1e-12;

    public static Complex[] Solve(
        CompiledNetwork net,
        double zRefOhms,
        IReadOnlyList<double> freqHz,
        CableEvalOptions options,
        SimulationSession? session = null,
        FrequencyGrid? grid = null,
        CancellationToken cancellationToken = default)
    {
        if (zRefOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(zRefOhms), zRefOhms, "Reference impedance must be > 0.");

        var spectra = PrefetchSpectra(net, freqHz.Count, options, session, grid, cancellationToken);
        var gamma = new Complex[freqHz.Count];
        Parallel.For(
            0,
            freqHz.Count,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => new Workspace(net),
            (n, _, ws) =>
            {
                gamma[n] = SolveOne(net, zRefOhms, n, freqHz[n], options, spectra, ws, observeIndex: -1);
                return ws;
            },
            _ => { });
        return gamma;
    }

    /// <summary>
    /// Transfer function V(observeNode)/Vs per frequency, where Vs is the source EMF
    /// behind zRef at the port (so a matched port sees Vs/2). Used for pulse-train
    /// views probing arbitrary nets.
    /// </summary>
    public static Complex[] SolveNodeTransfer(
        CompiledNetwork net,
        double zRefOhms,
        NodeId observeNode,
        IReadOnlyList<double> freqHz,
        CableEvalOptions options,
        SimulationSession? session = null,
        FrequencyGrid? grid = null,
        CancellationToken cancellationToken = default)
    {
        if (zRefOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(zRefOhms), zRefOhms, "Reference impedance must be > 0.");
        if (!net.NodeIndexOf.TryGetValue(observeNode, out int observeIndex))
            throw new ArgumentException($"Observation node {observeNode} is not part of the network.");

        var spectra = PrefetchSpectra(net, freqHz.Count, options, session, grid, cancellationToken);
        var transfer = new Complex[freqHz.Count];
        Parallel.For(
            0,
            freqHz.Count,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => new Workspace(net),
            (n, _, ws) =>
            {
                transfer[n] = SolveOne(net, zRefOhms, n, freqHz[n], options, spectra, ws, observeIndex);
                return ws;
            },
            _ => { });
        return transfer;
    }

    /// <summary>One spectrum per distinct cable model, indexed alongside the branches.</summary>
    private static CableSpectrum?[]? PrefetchSpectra(
        CompiledNetwork net,
        int frequencyCount,
        CableEvalOptions options,
        SimulationSession? session,
        FrequencyGrid? grid,
        CancellationToken cancellationToken)
    {
        if (session is null || grid is null)
            return null;
        if (grid.FrequencyCount != frequencyCount)
            throw new ArgumentException($"Grid has {grid.FrequencyCount} bins but {frequencyCount} frequencies were given.");

        var spectra = new CableSpectrum?[net.Branches.Count];
        var byModel = new Dictionary<CableModel, CableSpectrum>();
        for (int b = 0; b < net.Branches.Count; b++)
        {
            if (net.Branches[b] is CompiledCable cable)
            {
                spectra[b] = byModel.TryGetValue(cable.Model, out var s)
                    ? s
                    : byModel[cable.Model] = session.GetSpectrum(cable.Model, grid, options, cancellationToken);
            }
        }

        return spectra;
    }

    /// <summary>
    /// observeIndex &lt; 0: reflection mode - inject 1 A at the open port, return
    /// Gamma. observeIndex &gt;= 0: transfer mode - stamp the source admittance
    /// 1/zRef at the port, drive with Vs = 1 (Norton 1/zRef), return V(observe).
    /// </summary>
    private static Complex SolveOne(
        CompiledNetwork net,
        double zRef,
        int n,
        double fHz,
        CableEvalOptions options,
        CableSpectrum?[]? spectra,
        Workspace ws,
        int observeIndex)
    {
        int nodes = net.NodeCount;
        int ground = nodes; // extra union-find slot for the reference node

        // 1. Evaluate stamps and merge ideal shorts (lossless lines at DC, 0-ohm
        //    connectors, Short/0-ohm/inductive-at-DC loads, exact series resonances).
        for (int i = 0; i <= ground; i++)
            ws.Parent[i] = i;

        var branches = net.Branches;
        for (int b = 0; b < branches.Count; b++)
        {
            var stamp = branches[b] switch
            {
                CompiledCable cable => EvaluateCable(cable, spectra?[b], n, fHz, options),
                CompiledConnector conn => LineStamps.Connector(conn.Spec, fHz),
                var other => throw new ArgumentException($"Unexpected branch {other.GetType().Name}."),
            };
            ws.Stamps[b] = stamp;
            if (stamp.IsIdealShort)
                Union(ws.Parent, branches[b].NodeA, branches[b].NodeB);
        }

        var loads = net.Loads;
        for (int k = 0; k < loads.Count; k++)
        {
            var (isShort, y) = LoadModel.ShuntAdmittance(loads[k].Load, fHz);
            ws.LoadY[k] = y;
            if (isShort)
                Union(ws.Parent, loads[k].NodeIndex, ground);
        }

        int groundRep = Find(ws.Parent, ground);
        if (Find(ws.Parent, net.PortIndex) == groundRep)
        {
            // Port shorted to ground: Gamma = -1 exactly; a shorted source drives
            // nothing, so every transfer is 0. No solve either way.
            return observeIndex >= 0 ? Complex.Zero : new Complex(-1, 0);
        }

        if (observeIndex >= 0 && Find(ws.Parent, observeIndex) == groundRep)
            return Complex.Zero; // probe sits on a grounded net: exactly 0 V

        // 2. Dense indices over surviving representatives (grounded classes drop out).
        for (int i = 0; i <= ground; i++)
            ws.RepSlot[i] = -2;
        ws.RepSlot[groundRep] = -1;
        int m = 0;
        for (int i = 0; i < nodes; i++)
        {
            int rep = Find(ws.Parent, i);
            ws.NodeSlot[i] = ws.RepSlot[rep] == -2 ? ws.RepSlot[rep] = m++ : ws.RepSlot[rep];
        }

        // 3. Assemble Y (merged nodes share rows/columns; ground rows drop out).
        var y2 = ws.Matrix;
        Array.Clear(y2, 0, m * m);
        for (int b = 0; b < branches.Count; b++)
        {
            var stamp = ws.Stamps[b];
            int ia = ws.NodeSlot[branches[b].NodeA];
            int ib = ws.NodeSlot[branches[b].NodeB];
            if (!stamp.IsIdealShort && ia != ib)
            {
                if (ia >= 0)
                    y2[ia * m + ia] += stamp.SeriesY;
                if (ib >= 0)
                    y2[ib * m + ib] += stamp.SeriesY;
                if (ia >= 0 && ib >= 0)
                {
                    y2[ia * m + ib] -= stamp.SeriesY;
                    y2[ib * m + ia] -= stamp.SeriesY;
                }
            }

            if (ia >= 0)
                y2[ia * m + ia] += stamp.ShuntYA;
            if (ib >= 0)
                y2[ib * m + ib] += stamp.ShuntYB;
        }

        for (int k = 0; k < loads.Count; k++)
        {
            int i = ws.NodeSlot[loads[k].NodeIndex];
            if (i >= 0)
                y2[i * m + i] += ws.LoadY[k];
        }

        for (int i = 0; i < m; i++)
            y2[i * m + i] += GMin;

        // 4. Reflection mode: inject 1 A at the open port, the port voltage is Z_in.
        //    Transfer mode: the Norton equivalent of Vs = 1 behind zRef.
        var rhs = ws.Rhs;
        Array.Clear(rhs, 0, m);
        int portSlot = ws.NodeSlot[net.PortIndex];
        if (observeIndex >= 0)
        {
            y2[portSlot * m + portSlot] += 1.0 / zRef;
            rhs[portSlot] = 1.0 / zRef;
        }
        else
        {
            rhs[portSlot] = Complex.One;
        }

        // gMin makes Re(Y) positive definite for passive networks, so the factorization
        // cannot fail and Z_in is bounded by 1/gMin; the guard below is a backstop for
        // anything that still escapes (an isolated bad bin is treated as an open).
        if (!ComplexLu.Factor(y2, m, ws.Pivot, out _))
            return observeIndex >= 0 ? Complex.Zero : Complex.One;
        ComplexLu.Solve(y2, m, ws.Pivot, rhs);

        if (observeIndex >= 0)
        {
            Complex v = rhs[ws.NodeSlot[observeIndex]];
            return double.IsFinite(v.Real) && double.IsFinite(v.Imaginary) ? v : Complex.Zero;
        }

        Complex zIn = rhs[portSlot];
        Complex g = (zIn - zRef) / (zIn + zRef);
        return double.IsFinite(g.Real) && double.IsFinite(g.Imaginary) ? g : Complex.One;
    }

    private static PiStamp EvaluateCable(
        CompiledCable cable, CableSpectrum? spectrum, int n, double fHz, CableEvalOptions options)
    {
        var (zPerM, yPerM) = spectrum is null
            ? cable.Model.EvaluatePerMeter(fHz, options)
            : (spectrum.ZPerM[n], spectrum.YPerM[n]);
        return LineStamps.Line(zPerM * cable.LengthM, yPerM * cable.LengthM);
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }

        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra != rb)
            parent[ra] = rb;
    }

    /// <summary>Per-thread buffers; nothing allocates inside the frequency loop.</summary>
    private sealed class Workspace
    {
        public Workspace(CompiledNetwork net)
        {
            int n = net.NodeCount;
            Parent = new int[n + 1];
            RepSlot = new int[n + 1];
            NodeSlot = new int[n];
            Stamps = new PiStamp[net.Branches.Count];
            LoadY = new Complex[net.Loads.Count];
            Matrix = new Complex[n * n];
            Rhs = new Complex[n];
            Pivot = new int[n];
        }

        public int[] Parent { get; }
        public int[] RepSlot { get; }
        public int[] NodeSlot { get; }
        public PiStamp[] Stamps { get; }
        public Complex[] LoadY { get; }
        public Complex[] Matrix { get; }
        public Complex[] Rhs { get; }
        public int[] Pivot { get; }
    }
}
