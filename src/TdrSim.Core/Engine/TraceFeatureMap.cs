using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>
/// The stretch of trace time a document node owns on first pass. Times are round-trip
/// (what the TDR displays); a lumped part is a point (<see cref="StartS"/> ==
/// <see cref="EndS"/>).
/// </summary>
public sealed record TraceFeature(string NodeId, double StartS, double EndS)
{
    public bool IsPoint => EndS <= StartS;
}

/// <summary>
/// Maps trace time to the components responsible on *first pass*, from path delays
/// alone: Dijkstra over cable delays gives every net its earliest one-way arrival
/// t(n); a cable between nets A/B with delay tau owns [2*min(tA,tB), tA+tB+tau] (for a
/// tree edge that is [2tA, 2tB]; a ring edge reached from both ends stops at the
/// meeting point), and lumped parts own the point 2*t(n). Deliberately honest about
/// what a TDR can know: equally deep branches share an interval, and beyond
/// <see cref="DeepestS"/> there are only re-reflections.
/// </summary>
public sealed class TraceFeatureMap
{
    private TraceFeatureMap(IReadOnlyList<TraceFeature> features, double deepestS)
    {
        Features = features;
        DeepestS = deepestS;
    }

    public IReadOnlyList<TraceFeature> Features { get; }

    /// <summary>Round-trip time of the deepest first-pass reflection.</summary>
    public double DeepestS { get; }

    /// <param name="origins">Index-aligned with <paramref name="model"/>'s elements.</param>
    /// <param name="extraPoints">Nodes that are not elements (junctions) by net; ids
    /// that already own a feature, and the source's own node, are ignored.</param>
    public static TraceFeatureMap Build(
        NetworkModel model,
        IReadOnlyList<ElementOrigin> origins,
        IEnumerable<(string NodeId, NodeId Net)>? extraPoints = null)
    {
        if (origins.Count != model.Elements.Count)
            throw new ArgumentException(
                $"{origins.Count} origins for {model.Elements.Count} elements.", nameof(origins));

        var arrival = Arrivals(model);
        double T(NodeId n) => arrival.GetValueOrDefault(n.Value, double.PositiveInfinity);

        // Per node: the union of its line sections (its own cable plus interface
        // hardware inserted on its behalf), or a point for lumped parts.
        var spans = new Dictionary<string, (double Start, double End)>();
        var points = new Dictionary<string, double>();
        var order = new List<string>();
        string? sourceId = null;

        void Touch(string id)
        {
            if (!spans.ContainsKey(id) && !points.ContainsKey(id))
                order.Add(id);
        }

        for (int i = 0; i < model.Elements.Count; i++)
        {
            string id = origins[i].NodeId;
            switch (model.Elements[i])
            {
                case SourcePort:
                    sourceId = id;
                    break;

                case CableSegment cable:
                {
                    double ta = T(cable.A), tb = T(cable.B);
                    if (double.IsInfinity(ta) || double.IsInfinity(tb))
                        break;
                    double tau = cable.LengthM / (cable.Spec.VelocityFactor * ComplexMath.C0);
                    double start = 2 * Math.Min(ta, tb);
                    double end = ta + tb + tau;
                    Touch(id);
                    points.Remove(id);
                    spans[id] = spans.TryGetValue(id, out var s)
                        ? (Math.Min(s.Start, start), Math.Max(s.End, end))
                        : (start, end);
                    break;
                }

                case Connector connector:
                {
                    double t = 2 * Math.Min(T(connector.A), T(connector.B));
                    if (double.IsInfinity(t))
                        break;
                    AddPoint(id, t);
                    break;
                }

                case Load load:
                {
                    double t = 2 * T(load.Node);
                    if (!double.IsInfinity(t))
                        AddPoint(id, t);
                    break;
                }
            }
        }

        void AddPoint(string id, double t)
        {
            if (spans.TryGetValue(id, out var s))
            {
                // A lump belonging to a node that also owns line: widen the span.
                spans[id] = (Math.Min(s.Start, t), Math.Max(s.End, t));
                return;
            }

            Touch(id);
            points.TryAdd(id, t);
        }

        if (extraPoints is not null)
        {
            foreach (var (id, net) in extraPoints)
            {
                if (id == sourceId || spans.ContainsKey(id) || points.ContainsKey(id))
                    continue;
                double t = 2 * T(net);
                if (!double.IsInfinity(t))
                    AddPoint(id, t);
            }
        }

        var features = new List<TraceFeature>(order.Count);
        double deepest = 0;
        foreach (string id in order)
        {
            var feature = spans.TryGetValue(id, out var s)
                ? new TraceFeature(id, s.Start, s.End)
                : new TraceFeature(id, points[id], points[id]);
            features.Add(feature);
            deepest = Math.Max(deepest, feature.EndS);
        }

        return new TraceFeatureMap(features, deepest);
    }

    /// <summary>Node ids owning trace time <paramref name="timeS"/>: lumped parts
    /// within the tolerance first, then every span containing it. Empty past the
    /// deepest first-pass reflection (re-reflections only) and before the port.</summary>
    public IReadOnlyList<string> At(double timeS, double toleranceS)
    {
        var hits = new List<string>();
        foreach (var feature in Features)
        {
            if (feature.IsPoint && Math.Abs(timeS - feature.StartS) <= toleranceS)
                hits.Add(feature.NodeId);
        }

        foreach (var feature in Features)
        {
            if (!feature.IsPoint && timeS >= feature.StartS && timeS <= feature.EndS)
                hits.Add(feature.NodeId);
        }

        return hits;
    }

    public TraceFeature? For(string nodeId) => Features.FirstOrDefault(f => f.NodeId == nodeId);

    /// <summary>Earliest one-way arrival per net, from the source port.</summary>
    private static Dictionary<int, double> Arrivals(NetworkModel model)
    {
        var adjacency = new Dictionary<int, List<(int To, double Delay)>>();
        void Edge(NodeId a, NodeId b, double delay)
        {
            (adjacency.TryGetValue(a.Value, out var la) ? la : adjacency[a.Value] = []).Add((b.Value, delay));
            (adjacency.TryGetValue(b.Value, out var lb) ? lb : adjacency[b.Value] = []).Add((a.Value, delay));
        }

        int? source = null;
        foreach (var element in model.Elements)
        {
            switch (element)
            {
                case SourcePort port:
                    source ??= port.Node.Value;
                    break;
                case CableSegment cable:
                    Edge(cable.A, cable.B, cable.LengthM / (cable.Spec.VelocityFactor * ComplexMath.C0));
                    break;
                case Connector connector:
                    Edge(connector.A, connector.B, 0);
                    break;
            }
        }

        var best = new Dictionary<int, double>();
        if (source is not { } start)
            return best;

        var queue = new PriorityQueue<int, double>();
        best[start] = 0;
        queue.Enqueue(start, 0);
        while (queue.TryDequeue(out int node, out double time))
        {
            if (time > best[node])
                continue;
            if (!adjacency.TryGetValue(node, out var edges))
                continue;
            foreach (var (to, delay) in edges)
            {
                double candidate = time + delay;
                if (!best.TryGetValue(to, out double known) || candidate < known)
                {
                    best[to] = candidate;
                    queue.Enqueue(to, candidate);
                }
            }
        }

        return best;
    }
}
