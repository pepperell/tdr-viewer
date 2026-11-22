using TdrSim.Core.Catalog;
using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>A two-node element with endpoints resolved to dense node indices.</summary>
public abstract record CompiledBranch
{
    public required int NodeA { get; init; }
    public required int NodeB { get; init; }
}

public sealed record CompiledCable : CompiledBranch
{
    public required CableModel Model { get; init; }
    public required double LengthM { get; init; }
}

public sealed record CompiledConnector : CompiledBranch
{
    public required ConnectorSpec Spec { get; init; }
}

/// <summary>A validated general network, indexed and ready for the nodal solver.</summary>
public sealed record CompiledNetwork
{
    public required int NodeCount { get; init; }

    /// <summary>Dense index of the source-port node.</summary>
    public required int PortIndex { get; init; }

    /// <summary>Model-level NodeId to dense index (for probing arbitrary nets).</summary>
    public required IReadOnlyDictionary<NodeId, int> NodeIndexOf { get; init; }

    public required IReadOnlyList<CompiledBranch> Branches { get; init; }

    public required IReadOnlyList<(int NodeIndex, Load Load)> Loads { get; init; }

    /// <summary>
    /// Sum of all cable segment delays - an upper bound on the principal echo path for
    /// window sizing (ring recirculation decays geometrically and is covered by the 6x
    /// round-trip factor plus the Settled flag). Equals the chain one-way delay when the
    /// network is a chain.
    /// </summary>
    public required double TotalCableDelayS { get; init; }

    /// <summary>VF of the first cable reached by BFS from the port; null if none.</summary>
    public required double? DisplayVelocityFactor { get; init; }
}

/// <summary>
/// Validates and indexes an arbitrary <see cref="NetworkModel"/> for the nodal solver.
/// Validation rules match M1's chain extractor: exactly one source port, everything
/// reachable from it, positive lengths, no self-loops, well-formed loads. Branches,
/// loops, and parallel edges are all legal here.
/// </summary>
public static class NetworkCompiler
{
    public static CompiledNetwork Compile(NetworkModel model, SimulationSession? session = null)
    {
        var sources = model.Elements.OfType<SourcePort>().ToList();
        if (sources.Count != 1)
            throw new ArgumentException($"Network needs exactly one SourcePort, found {sources.Count}.");

        var nodeIndex = new Dictionary<NodeId, int>();
        int IndexOf(NodeId node)
            => nodeIndex.TryGetValue(node, out int i) ? i : nodeIndex[node] = nodeIndex.Count;

        int port = IndexOf(sources[0].Node);

        var branches = new List<CompiledBranch>();
        var loads = new List<(int NodeIndex, Load Load)>();
        var modelCache = new Dictionary<CableSpec, CableModel>();
        double totalDelay = 0;
        foreach (var element in model.Elements)
        {
            switch (element)
            {
                case SourcePort:
                    break;
                case CableSegment cable:
                    if (cable.LengthM <= 0)
                        throw new ArgumentException($"Cable segment {cable.A}-{cable.B} needs LengthM > 0, got {cable.LengthM}.");
                    if (cable.A == cable.B)
                        throw new ArgumentException($"Cable segment at {cable.A} connects a node to itself.");
                    branches.Add(new CompiledCable
                    {
                        NodeA = IndexOf(cable.A),
                        NodeB = IndexOf(cable.B),
                        Model = session is not null
                            ? session.GetModel(cable.Spec)
                            : modelCache.TryGetValue(cable.Spec, out var m)
                                ? m
                                : modelCache[cable.Spec] = CableModel.FromSpec(cable.Spec),
                        LengthM = cable.LengthM,
                    });
                    totalDelay += cable.LengthM / (cable.Spec.VelocityFactor * ComplexMath.C0);
                    break;
                case Connector connector:
                    if (connector.A == connector.B)
                        throw new ArgumentException($"Connector at {connector.A} connects a node to itself.");
                    branches.Add(new CompiledConnector
                    {
                        NodeA = IndexOf(connector.A),
                        NodeB = IndexOf(connector.B),
                        Spec = connector.Spec,
                    });
                    break;
                case Load load:
                    LoadModel.Validate(load);
                    loads.Add((IndexOf(load.Node), load));
                    break;
                default:
                    throw new ArgumentException($"Unknown element type {element.GetType().Name}.");
            }
        }

        // BFS from the port for connectivity and the display VF (first cable reached).
        var adjacency = new List<int>[nodeIndex.Count];
        for (int b = 0; b < branches.Count; b++)
        {
            (adjacency[branches[b].NodeA] ??= []).Add(b);
            (adjacency[branches[b].NodeB] ??= []).Add(b);
        }

        var reached = new bool[nodeIndex.Count];
        var queue = new Queue<int>();
        reached[port] = true;
        queue.Enqueue(port);
        double? displayVf = null;
        int reachedBranches = 0;
        var branchSeen = new bool[branches.Count];
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            foreach (int b in adjacency[node] ?? [])
            {
                if (!branchSeen[b])
                {
                    branchSeen[b] = true;
                    reachedBranches++;
                    if (displayVf is null && branches[b] is CompiledCable cable)
                        displayVf = cable.Model.VelocityFactor;
                }

                int other = branches[b].NodeA == node ? branches[b].NodeB : branches[b].NodeA;
                if (!reached[other])
                {
                    reached[other] = true;
                    queue.Enqueue(other);
                }
            }
        }

        if (reachedBranches != branches.Count)
        {
            throw new ArgumentException(
                $"{branches.Count - reachedBranches} two-port(s) are not connected to the source port.");
        }

        foreach (var (node, load) in loads)
        {
            if (!reached[node])
                throw new ArgumentException($"Load at {load.Node} is not connected to the source port.");
        }

        return new CompiledNetwork
        {
            NodeCount = nodeIndex.Count,
            PortIndex = port,
            NodeIndexOf = nodeIndex,
            Branches = branches,
            Loads = loads,
            TotalCableDelayS = totalDelay,
            DisplayVelocityFactor = displayVf,
        };
    }
}
