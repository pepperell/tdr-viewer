using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>One two-port in the chain plus the shunt loads at its source-side node.</summary>
public sealed record ChainLink
{
    /// <summary>A <see cref="CableSegment"/> or <see cref="Connector"/>.</summary>
    public required Element TwoPort { get; init; }

    /// <summary>Shunt loads at the node on the source side of this two-port.</summary>
    public required IReadOnlyList<Load> NearLoads { get; init; }
}

/// <summary>A validated source-to-termination ordering of a chain network.</summary>
public sealed record Chain
{
    /// <summary>Two-ports in source-to-far order.</summary>
    public required IReadOnlyList<ChainLink> Links { get; init; }

    /// <summary>Loads at the far end; empty means unterminated (open).</summary>
    public required IReadOnlyList<Load> TerminalLoads { get; init; }

    /// <summary>Sum of cable segment delays, one way.</summary>
    public required double OneWayDelayS { get; init; }

    /// <summary>VF of the segment nearest the source; null if the chain has no cables.</summary>
    public required double? FirstSegmentVelocityFactor { get; init; }
}

/// <summary>
/// Walks a <see cref="NetworkModel"/> from its source port and orders it into a
/// <see cref="Chain"/>. Networks with branch nodes or loops throw
/// <see cref="NotSupportedException"/> - the M2 nodal solver lifts that restriction.
/// </summary>
public static class ChainExtractor
{
    /// <summary>
    /// <see cref="Extract"/>, but returns null where the network is valid yet not a
    /// simple chain (branches, loops, multiple components) - the nodal solver's
    /// territory. Genuinely invalid networks still throw from the compiler.
    /// </summary>
    public static Chain? TryExtract(NetworkModel model)
    {
        try
        {
            return Extract(model);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static Chain Extract(NetworkModel model)
    {
        var sources = model.Elements.OfType<SourcePort>().ToList();
        if (sources.Count != 1)
            throw new ArgumentException($"Network needs exactly one SourcePort, found {sources.Count}.");

        var twoPorts = new List<(Element Element, NodeId A, NodeId B)>();
        var loadsByNode = new Dictionary<NodeId, List<Load>>();
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
                    twoPorts.Add((cable, cable.A, cable.B));
                    break;
                case Connector connector:
                    if (connector.A == connector.B)
                        throw new ArgumentException($"Connector at {connector.A} connects a node to itself.");
                    twoPorts.Add((connector, connector.A, connector.B));
                    break;
                case Load load:
                    LoadModel.Validate(load);
                    (loadsByNode.TryGetValue(load.Node, out var list)
                        ? list
                        : loadsByNode[load.Node] = []).Add(load);
                    break;
                default:
                    throw new ArgumentException($"Unknown element type {element.GetType().Name}.");
            }
        }

        var adjacency = new Dictionary<NodeId, List<int>>();
        for (int i = 0; i < twoPorts.Count; i++)
        {
            foreach (var node in new[] { twoPorts[i].A, twoPorts[i].B })
            {
                (adjacency.TryGetValue(node, out var list) ? list : adjacency[node] = []).Add(i);
            }
        }

        var links = new List<ChainLink>();
        var visitedNodes = new HashSet<NodeId>();
        var visitedEdges = new HashSet<int>();
        double delay = 0;
        double? firstVf = null;

        NodeId current = sources[0].Node;
        visitedNodes.Add(current);
        while (true)
        {
            var onward = adjacency.TryGetValue(current, out var edges)
                ? edges.Where(e => !visitedEdges.Contains(e)).ToList()
                : [];

            if (onward.Count == 0)
                break; // current is the far end

            if (onward.Count > 1)
            {
                throw new NotSupportedException(
                    $"Node {current} joins {adjacency[current].Count} two-ports; only simple chains are " +
                    "supported in M1 (branches and loops arrive in M2).");
            }

            int edge = onward[0];
            var (element, a, b) = twoPorts[edge];
            visitedEdges.Add(edge);
            links.Add(new ChainLink
            {
                TwoPort = element,
                NearLoads = loadsByNode.GetValueOrDefault(current, []),
            });

            if (element is CableSegment seg)
            {
                delay += seg.LengthM / (seg.Spec.VelocityFactor * ComplexMath.C0);
                firstVf ??= seg.Spec.VelocityFactor;
            }

            NodeId next = a == current ? b : a;
            if (!visitedNodes.Add(next))
            {
                throw new NotSupportedException(
                    $"Node {next} closes a loop; only simple chains are supported in M1 " +
                    "(branches and loops arrive in M2).");
            }

            current = next;
        }

        if (visitedEdges.Count != twoPorts.Count)
        {
            throw new NotSupportedException(
                $"{twoPorts.Count - visitedEdges.Count} two-port(s) are not connected to the source port.");
        }

        foreach (var node in loadsByNode.Keys)
        {
            if (!visitedNodes.Contains(node))
                throw new ArgumentException($"Load at {node} is not connected to the source port.");
        }

        return new Chain
        {
            Links = links,
            TerminalLoads = loadsByNode.GetValueOrDefault(current, []),
            OneWayDelayS = delay,
            FirstSegmentVelocityFactor = firstVf,
        };
    }
}
