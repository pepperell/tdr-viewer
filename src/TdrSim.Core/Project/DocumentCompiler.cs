using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;

namespace TdrSim.Core.Project;

public enum DocumentIssueKind
{
    NoSource,
    MultipleSources,

    /// <summary>Advisory: the elements are omitted from the model, not a blocker.</summary>
    DisconnectedFromSource,

    NonPositiveLength,

    /// <summary>Both ends of a two-port land on the same electrical node.</summary>
    SelfLoop,

    InvalidLoadValues,

    /// <summary>Bad cable override (Z0 must be &gt; 0, VF in (0, 1]).</summary>
    InvalidCableValues,

    /// <summary>Non-positive instrument override, or bad noise/averaging settings.</summary>
    InvalidInstrumentValues,

    UnknownPresetId,

    /// <summary>Bad fault parameters (scale, resistance, derived VF out of range).</summary>
    InvalidFaultValues,

    /// <summary>Advisory: two connectorized ends meet whose families cannot be joined
    /// by an RF adapter (balanced vs coax needs a balun); they are joined ideally.</summary>
    UnadaptableInterface,
}

public sealed record DocumentIssue
{
    public required DocumentIssueKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>Document node ids involved, for canvas highlighting.</summary>
    public IReadOnlyList<string> NodeIds { get; init; } = [];

    /// <summary>Fatal issues prevent a model from being produced; advisory ones
    /// describe elements the compiler omitted or joined ideally.</summary>
    public bool IsFatal => Kind is not DocumentIssueKind.DisconnectedFromSource
        and not DocumentIssueKind.UnadaptableInterface;
}

/// <summary>A net the pulse view can probe, keyed by the document node sitting on it.</summary>
public sealed record DocumentProbe(string NodeId, NodeId Net);

public sealed record DocumentCompilation
{
    public required IReadOnlyList<DocumentIssue> Issues { get; init; }

    /// <summary>Null when any fatal issue is present.</summary>
    public required NetworkModel? Model { get; init; }

    /// <summary>Null when the instrument preset or its overrides are unusable.</summary>
    public required InstrumentSettings? Instrument { get; init; }

    /// <summary>Resolved display-noise settings; null when noise is off or invalid.</summary>
    public required TraceNoiseSettings? Noise { get; init; }

    /// <summary>Observable points for the pulse view (TDR port, loads, junctions,
    /// bridge faults), in document order; empty when there is no model.</summary>
    public required IReadOnlyList<DocumentProbe> Probes { get; init; }

    /// <summary>One line per automatically inserted connector interface (mated pairs,
    /// adapters) describing the physical hardware the model now contains.</summary>
    public required IReadOnlyList<string> InterfaceNotes { get; init; }

    public bool HasFatalIssues => Issues.Any(i => i.IsFatal);
}

/// <summary>
/// Turns a <see cref="ProjectDocument"/> into a <see cref="NetworkModel"/> plus a list
/// of issues. Validation is issues-not-exceptions: every state reachable through the
/// editor (dangling ports, missing source, zero length, half-configured loads) produces
/// a <see cref="DocumentIssue"/> badge, never a throw; the engine's ArgumentExceptions
/// are unreachable because the same rules are pre-checked here. Only states the editor
/// cannot produce (duplicate ids, wires to nonexistent ports - i.e. a hand-corrupted
/// file) throw.
/// </summary>
public static class DocumentCompiler
{
    public static DocumentCompilation Compile(ProjectDocument document, ComponentCatalog catalog)
    {
        var issues = new List<DocumentIssue>();

        // Electrical nets by union-find: every (node, port) pair is a terminal, every
        // wire unions its two terminals, and each surviving class is one electrical
        // node. Any number of wires meeting at a port is therefore one node - a tee
        // needs no special element - and a Junction is just a one-port terminal that
        // everything wired to it unions through.
        var terminals = new Dictionary<(string Node, string Port), int>();
        var seenIds = new HashSet<string>();
        foreach (var node in document.Nodes)
        {
            if (!seenIds.Add(node.Id))
                throw new InvalidDataException($"Duplicate node id '{node.Id}' in project.");
            foreach (var port in DocumentPorts.For(node))
                terminals[(node.Id, port)] = terminals.Count;
        }

        var parent = new int[terminals.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        foreach (var wire in document.Wires)
        {
            if (!terminals.TryGetValue((wire.FromNodeId, wire.FromPort), out int a)
                || !terminals.TryGetValue((wire.ToNodeId, wire.ToPort), out int b))
            {
                throw new InvalidDataException(
                    $"Wire references unknown port '{wire.FromNodeId}.{wire.FromPort}' -> " +
                    $"'{wire.ToNodeId}.{wire.ToPort}'.");
            }

            parent[Find(a)] = Find(b);
        }

        // Dense NodeId per union class, assigned in document order for determinism.
        var classIndex = new Dictionary<int, int>();
        NodeId NetOf(string nodeId, string port)
        {
            int root = Find(terminals[(nodeId, port)]);
            if (!classIndex.TryGetValue(root, out int index))
                classIndex[root] = index = classIndex.Count;
            return new NodeId(index);
        }

        foreach (var node in document.Nodes)
        {
            foreach (var port in DocumentPorts.For(node))
                NetOf(node.Id, port);
        }

        // ---- automatic connector interfaces ----
        // Where exactly two connectorized ends (the TDR's panel connector, a cable
        // end's connector) share a net, splice the physical interface in between:
        // a mated pair for matching families, a between-series adapter otherwise.
        // Multi-way nets (junction tees) and nets with other members stay ideal.
        int totalNets = classIndex.Count;
        var remap = new Dictionary<(string Node, string Port), NodeId>();
        var interfaceElements = new List<(DocumentNode Node, Element Element)>();
        var interfaceNotes = new List<string>();

        ConnectorFamilySpec? EndpointFamily(DocumentNode node, string port)
        {
            string? familyId;
            switch (node.Kind)
            {
                case DocumentNodeKind.Tdr:
                    var preset = catalog.Instruments.FirstOrDefault(
                        i => i.Id == document.Instrument.PresetId);
                    familyId = document.Instrument.PortConnectorId ?? preset?.PortConnector;
                    break;
                case DocumentNodeKind.Cable:
                    var spec = node.CablePresetId is null
                        ? null
                        : catalog.Cables.FirstOrDefault(c => c.Id == node.CablePresetId);
                    familyId = (port == DocumentPorts.A ? node.ConnectorAId : node.ConnectorBId)
                        ?? spec?.DefaultConnector;
                    break;
                default:
                    return null;
            }

            // Unknown override ids were already flagged by the sweep below; resolving
            // to null here just suppresses interface insertion for the broken end.
            return familyId is null or "none"
                ? null
                : catalog.ConnectorFamilies.FirstOrDefault(f => f.Id == familyId);
        }

        void CheckFamilyRef(string? familyId, string owner, DocumentNode? node)
        {
            if (familyId is null or "none" || catalog.ConnectorFamilies.Any(f => f.Id == familyId))
                return;
            issues.Add(new DocumentIssue
            {
                Kind = DocumentIssueKind.UnknownPresetId,
                Message = $"{owner} references unknown connector family '{familyId}'.",
                NodeIds = node is null ? [] : [node.Id],
            });
        }

        CheckFamilyRef(document.Instrument.PortConnectorId, "The instrument", null);
        foreach (var node in document.Nodes.Where(n => n.Kind == DocumentNodeKind.Cable))
        {
            CheckFamilyRef(node.ConnectorAId, $"Cable '{node.Id}' end A", node);
            CheckFamilyRef(node.ConnectorBId, $"Cable '{node.Id}' end B", node);
        }

        var termsByClass = new Dictionary<int, List<(DocumentNode Node, string Port)>>();
        foreach (var node in document.Nodes)
        {
            foreach (var port in DocumentPorts.For(node))
            {
                int root = Find(terminals[(node.Id, port)]);
                (termsByClass.TryGetValue(root, out var list)
                    ? list
                    : termsByClass[root] = []).Add((node, port));
            }
        }

        foreach (var members in termsByClass.Values)
        {
            if (members.Count != 2)
                continue;
            var (n1, p1) = members[0];
            var (n2, p2) = members[1];
            if (n1.Id == n2.Id)
                continue; // a self-looped two-port keeps its SelfLoop diagnostic

            var f1 = EndpointFamily(n1, p1);
            var f2 = EndpointFamily(n2, p2);
            if (f1 is null || f2 is null)
                continue;

            // The TDR side keeps its net: the source reference plane never moves.
            if (n2.Kind == DocumentNodeKind.Tdr)
                ((n1, p1, f1), (n2, p2, f2)) = ((n2, p2, f2), (n1, p1, f1));

            if (f1.Id != f2.Id && (f1.Balanced || f2.Balanced))
            {
                issues.Add(new DocumentIssue
                {
                    Kind = DocumentIssueKind.UnadaptableInterface,
                    Message = $"'{n1.Id}' ({f1.Name}) and '{n2.Id}' ({f2.Name}) cannot be " +
                        "joined by an RF adapter (needs a balun); connected ideally.",
                    NodeIds = [n1.Id, n2.Id],
                });
                continue;
            }

            var cursor = NetOf(n1.Id, p1);
            NodeId Next()
            {
                var next = new NodeId(totalNets++);
                return next;
            }

            void AddLump(ConnectorSpec lump)
            {
                var next = Next();
                interfaceElements.Add((n2, new Connector(cursor, next, lump)));
                cursor = next;
            }

            void AddSection(CableSpec section, double lengthMm)
            {
                var next = Next();
                interfaceElements.Add((n2, new CableSegment(cursor, next, section, lengthMm * 1e-3)));
                cursor = next;
            }

            if (f1.Id == f2.Id)
            {
                // Same family: panel-to-plug is one mated pair; cable-to-cable needs a
                // double-female barrel, i.e. two mated pairs back to back.
                bool barrel = n1.Kind == DocumentNodeKind.Cable && n2.Kind == DocumentNodeKind.Cable;
                AddLump(InterfaceSynthesis.ExcessSpec(f1));
                AddSection(InterfaceSynthesis.MatedSpec(f1), f1.MatedLengthMm);
                if (barrel)
                    AddSection(InterfaceSynthesis.MatedSpec(f1), f1.MatedLengthMm);
                interfaceNotes.Add($"{n1.Id} ↔ {n2.Id}: {f1.Name} mated pair"
                    + (barrel ? " ×2 (double-female barrel)" : ""));
            }
            else
            {
                var adapter = catalog.Adapters.FirstOrDefault(a =>
                    (a.FamilyA == f1.Id && a.FamilyB == f2.Id)
                    || (a.FamilyA == f2.Id && a.FamilyB == f1.Id));
                var body = adapter is null
                    ? InterfaceSynthesis.GenericBodySpec(f1, f2)
                    : InterfaceSynthesis.BodySpec(adapter);
                var lump = adapter is null
                    ? InterfaceSynthesis.GenericExcessSpec(f1, f2)
                    : InterfaceSynthesis.ExcessSpec(adapter);
                double bodyLengthMm = adapter?.LengthMm ?? 25.0;

                AddSection(InterfaceSynthesis.MatedSpec(f1), f1.MatedLengthMm);
                AddLump(lump);
                AddSection(body, bodyLengthMm);
                AddSection(InterfaceSynthesis.MatedSpec(f2), f2.MatedLengthMm);
                interfaceNotes.Add($"{n1.Id} ↔ {n2.Id}: {f1.Name} → "
                    + $"{(adapter is null ? body.Name : adapter.Name)} "
                    + $"({body.Z0Ohms:0.#} Ω · {bodyLengthMm:0.#} mm) → {f2.Name}");
            }

            remap[(n2.Id, p2)] = cursor;
        }

        NodeId NetOfFinal(string nodeId, string port)
            => remap.TryGetValue((nodeId, port), out var mapped) ? mapped : NetOf(nodeId, port);

        var tdrs = document.Nodes.Where(n => n.Kind == DocumentNodeKind.Tdr).ToList();
        if (tdrs.Count == 0)
        {
            issues.Add(new DocumentIssue
            {
                Kind = DocumentIssueKind.NoSource,
                Message = "Add a TDR instrument node - nothing drives the network.",
            });
        }
        else if (tdrs.Count > 1)
        {
            issues.Add(new DocumentIssue
            {
                Kind = DocumentIssueKind.MultipleSources,
                Message = $"Only one TDR node is allowed; found {tdrs.Count}.",
                NodeIds = tdrs.Select(t => t.Id).ToList(),
            });
        }

        var instrument = ResolveInstrument(document.Instrument, catalog, issues);
        var noise = ResolveNoise(document.Instrument, catalog, issues);
        var emitted = EmitElements(document, catalog, NetOfFinal, issues);
        emitted.AddRange(interfaceElements);

        // Connectivity from the TDR's net: islands are advisory (their elements are
        // omitted), matching how a real bench works - parts not plugged into the
        // instrument simply don't show up on the trace.
        NetworkModel? model = null;
        var probes = new List<DocumentProbe>();
        if (tdrs.Count == 1)
        {
            var sourceNet = NetOfFinal(tdrs[0].Id, DocumentPorts.P);
            var connected = PartitionByConnectivity(
                emitted, sourceNet, totalNets, issues, out var reachableNets);
            if (!issues.Any(i => i.IsFatal))
            {
                connected.Insert(0, new SourcePort(sourceNet));
                model = new NetworkModel { Elements = connected };

                probes.Add(new DocumentProbe(tdrs[0].Id, sourceNet));
                foreach (var node in document.Nodes)
                {
                    bool isProbeKind = node.Kind is DocumentNodeKind.Load or DocumentNodeKind.Junction
                        || (node.Kind == DocumentNodeKind.Fault && node.FaultKind == Project.FaultKind.ShuntBridge);
                    if (!isProbeKind)
                        continue;
                    var net = NetOfFinal(node.Id, DocumentPorts.P);
                    if (reachableNets.Contains(net.Value))
                        probes.Add(new DocumentProbe(node.Id, net));
                }
            }
        }

        return new DocumentCompilation
        {
            Issues = issues,
            Model = model,
            Instrument = instrument,
            Noise = noise,
            Probes = probes,
            InterfaceNotes = interfaceNotes,
        };
    }

    private static TraceNoiseSettings? ResolveNoise(
        InstrumentDocument instrument, ComponentCatalog catalog, List<DocumentIssue> issues)
    {
        if (instrument.NoiseRmsMillivolts is < 0 || instrument.AveragingSweeps is < 1)
        {
            issues.Add(new DocumentIssue
            {
                Kind = DocumentIssueKind.InvalidInstrumentValues,
                Message = "Noise RMS must be >= 0 and averaging sweeps >= 1.",
            });
            return null;
        }

        // Real instruments are not noiseless: a null document value means the selected
        // preset's noise floor; an explicit value overrides it, and 0 turns noise off.
        var preset = catalog.Instruments.FirstOrDefault(i => i.Id == instrument.PresetId);
        double rms = instrument.NoiseRmsMillivolts ?? preset?.NoiseFloorMillivolts ?? 0;
        return rms > 0
            ? new TraceNoiseSettings(rms * 1e-3, instrument.AveragingSweeps ?? 1)
            : null;
    }

    private static InstrumentSettings? ResolveInstrument(
        InstrumentDocument instrument, ComponentCatalog catalog, List<DocumentIssue> issues)
    {
        var preset = catalog.Instruments.FirstOrDefault(i => i.Id == instrument.PresetId);
        if (preset is null)
        {
            issues.Add(new DocumentIssue
            {
                Kind = DocumentIssueKind.UnknownPresetId,
                Message = $"Instrument preset '{instrument.PresetId}' is not in the catalog.",
            });
            return null;
        }

        if (instrument.RiseTimePsOverride is <= 0
            || instrument.AmplitudeVoltsOverride is <= 0
            || instrument.SourceImpedanceOverrideOhms is <= 0)
        {
            issues.Add(new DocumentIssue
            {
                Kind = DocumentIssueKind.InvalidInstrumentValues,
                Message = "Instrument overrides must be positive.",
            });
            return null;
        }

        return new InstrumentSettings
        {
            StepAmplitudeVolts = instrument.AmplitudeVoltsOverride ?? preset.StepAmplitudeVolts,
            RiseTimeS = (instrument.RiseTimePsOverride ?? preset.RiseTime1090Ps) * 1e-12,
            SourceImpedanceOhms = instrument.SourceImpedanceOverrideOhms ?? preset.SourceImpedanceOhms,
        };
    }

    private static List<(DocumentNode Node, Element Element)> EmitElements(
        ProjectDocument document,
        ComponentCatalog catalog,
        Func<string, string, NodeId> netOf,
        List<DocumentIssue> issues)
    {
        var emitted = new List<(DocumentNode, Element)>();
        foreach (var node in document.Nodes)
        {
            switch (node.Kind)
            {
                case DocumentNodeKind.Tdr or DocumentNodeKind.Junction:
                    break;

                case DocumentNodeKind.Cable:
                {
                    var netA = netOf(node.Id, DocumentPorts.A);
                    var netB = netOf(node.Id, DocumentPorts.B);
                    var spec = node.CablePresetId is null
                        ? null
                        : catalog.Cables.FirstOrDefault(c => c.Id == node.CablePresetId);
                    bool ok = true;
                    if (spec is null)
                    {
                        issues.Add(Issue(DocumentIssueKind.UnknownPresetId,
                            $"Cable '{node.Id}' references unknown preset '{node.CablePresetId}'.", node));
                        ok = false;
                    }

                    if (node.LengthM is not > 0)
                    {
                        issues.Add(Issue(DocumentIssueKind.NonPositiveLength,
                            $"Cable '{node.Id}' needs a positive length.", node));
                        ok = false;
                    }

                    if (node.Z0OverrideOhms is <= 0 || node.VfOverride is <= 0 or > 1)
                    {
                        issues.Add(Issue(DocumentIssueKind.InvalidCableValues,
                            $"Cable '{node.Id}' has invalid overrides (Z0 > 0, VF in (0, 1]).", node));
                        ok = false;
                    }

                    if (netA == netB)
                    {
                        issues.Add(Issue(DocumentIssueKind.SelfLoop,
                            $"Cable '{node.Id}' has both ends wired to the same electrical node.", node));
                        ok = false;
                    }

                    if (ok)
                    {
                        var resolved = node.Z0OverrideOhms is null && node.VfOverride is null
                            ? spec!
                            : spec! with
                            {
                                Z0Ohms = node.Z0OverrideOhms ?? spec!.Z0Ohms,
                                VelocityFactor = node.VfOverride ?? spec!.VelocityFactor,
                            };
                        emitted.Add((node, new CableSegment(netA, netB, resolved, node.LengthM!.Value)));
                    }

                    break;
                }

                case DocumentNodeKind.Connector:
                {
                    var netA = netOf(node.Id, DocumentPorts.A);
                    var netB = netOf(node.Id, DocumentPorts.B);
                    var spec = node.ConnectorPresetId is null
                        ? null
                        : catalog.Connectors.FirstOrDefault(c => c.Id == node.ConnectorPresetId);
                    bool ok = true;
                    if (spec is null)
                    {
                        issues.Add(Issue(DocumentIssueKind.UnknownPresetId,
                            $"Connector '{node.Id}' references unknown preset '{node.ConnectorPresetId}'.", node));
                        ok = false;
                    }

                    if (netA == netB)
                    {
                        issues.Add(Issue(DocumentIssueKind.SelfLoop,
                            $"Connector '{node.Id}' has both ends wired to the same electrical node.", node));
                        ok = false;
                    }

                    if (ok)
                        emitted.Add((node, new Connector(netA, netB, spec!)));
                    break;
                }

                case DocumentNodeKind.Fault:
                    EmitFault(node, catalog, netOf, issues, emitted);
                    break;

                case DocumentNodeKind.Load:
                {
                    var net = netOf(node.Id, DocumentPorts.P);
                    if (node.LoadKind is not { } kind)
                    {
                        issues.Add(Issue(DocumentIssueKind.InvalidLoadValues,
                            $"Load '{node.Id}' has no kind set.", node));
                        break;
                    }

                    var load = new Load(net, kind, node.ROhms, node.LHenries, node.CFarads);
                    try
                    {
                        LoadModel.Validate(load);
                    }
                    catch (ArgumentException)
                    {
                        issues.Add(Issue(DocumentIssueKind.InvalidLoadValues,
                            $"Load '{node.Id}' has invalid values for {kind}.", node));
                        break;
                    }

                    emitted.Add((node, load));
                    break;
                }

                default:
                    throw new InvalidDataException($"Unknown node kind {node.Kind}.");
            }
        }

        return emitted;
    }

    private static void EmitFault(
        DocumentNode node,
        ComponentCatalog catalog,
        Func<string, string, NodeId> netOf,
        List<DocumentIssue> issues,
        List<(DocumentNode Node, Element Element)> emitted)
    {
        if (node.FaultKind is not { } faultKind)
        {
            issues.Add(Issue(DocumentIssueKind.InvalidFaultValues,
                $"Fault '{node.Id}' has no kind set.", node));
            return;
        }

        switch (faultKind)
        {
            case FaultKind.ShuntBridge:
            {
                var net = netOf(node.Id, DocumentPorts.P);
                if (node.ROhms is not > 0)
                {
                    issues.Add(Issue(DocumentIssueKind.InvalidFaultValues,
                        $"Bridge fault '{node.Id}' needs a positive resistance.", node));
                    return;
                }

                emitted.Add((node, new Load(net, LoadKind.Resistor, ROhms: node.ROhms)));
                return;
            }

            case FaultKind.SeriesFault:
            {
                var netA = netOf(node.Id, DocumentPorts.A);
                var netB = netOf(node.Id, DocumentPorts.B);
                bool ok = true;
                if (node.ROhms is not > 0)
                {
                    issues.Add(Issue(DocumentIssueKind.InvalidFaultValues,
                        $"Series fault '{node.Id}' needs a positive resistance.", node));
                    ok = false;
                }

                if (netA == netB)
                {
                    issues.Add(Issue(DocumentIssueKind.SelfLoop,
                        $"Series fault '{node.Id}' has both ends wired to the same electrical node.", node));
                    ok = false;
                }

                if (ok)
                    emitted.Add((node, new Connector(netA, netB, FaultSynthesis.SeriesConnectorSpec(node.ROhms!.Value))));
                return;
            }

            case FaultKind.SectionOverride:
            {
                var netA = netOf(node.Id, DocumentPorts.A);
                var netB = netOf(node.Id, DocumentPorts.B);
                var baseSpec = node.CablePresetId is null
                    ? null
                    : catalog.Cables.FirstOrDefault(c => c.Id == node.CablePresetId);
                bool ok = true;
                if (baseSpec is null)
                {
                    issues.Add(Issue(DocumentIssueKind.UnknownPresetId,
                        $"Fault section '{node.Id}' references unknown base cable '{node.CablePresetId}'.", node));
                    ok = false;
                }

                if (node.LengthM is not > 0)
                {
                    issues.Add(Issue(DocumentIssueKind.NonPositiveLength,
                        $"Fault section '{node.Id}' needs a positive length.", node));
                    ok = false;
                }

                double extra = node.ExtraDielectricLossDbPer100m ?? 0;
                if (node.Z0Scale is not > 0 || extra < 0)
                {
                    issues.Add(Issue(DocumentIssueKind.InvalidFaultValues,
                        $"Fault section '{node.Id}' needs Z0 scale > 0 and extra loss >= 0.", node));
                    ok = false;
                }
                else if (baseSpec is not null && baseSpec.VelocityFactor * node.Z0Scale.Value is <= 0 or > 1)
                {
                    issues.Add(Issue(DocumentIssueKind.InvalidFaultValues,
                        $"Fault section '{node.Id}': Z0 scale {node.Z0Scale.Value} puts the velocity factor outside (0, 1].", node));
                    ok = false;
                }

                if (netA == netB)
                {
                    issues.Add(Issue(DocumentIssueKind.SelfLoop,
                        $"Fault section '{node.Id}' has both ends wired to the same electrical node.", node));
                    ok = false;
                }

                if (ok)
                {
                    var spec = FaultSynthesis.SectionSpec(baseSpec!, node.Z0Scale!.Value, extra);
                    emitted.Add((node, new CableSegment(netA, netB, spec, node.LengthM!.Value)));
                }

                return;
            }

            default:
                throw new InvalidDataException($"Unknown fault kind {faultKind}.");
        }
    }

    /// <summary>Returns the elements reachable from the source net; each unreachable
    /// island becomes one advisory issue and its elements are dropped. Also reports
    /// which nets are in the source's component (for probe points).</summary>
    private static List<Element> PartitionByConnectivity(
        List<(DocumentNode Node, Element Element)> emitted,
        NodeId sourceNet,
        int netCount,
        List<DocumentIssue> issues,
        out HashSet<int> reachableNets)
    {
        var parent = new int[netCount];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        foreach (var (_, element) in emitted)
        {
            if (element is CableSegment c)
                parent[Find(c.A.Value)] = Find(c.B.Value);
            else if (element is Connector k)
                parent[Find(k.A.Value)] = Find(k.B.Value);
        }

        int sourceComponent = Find(sourceNet.Value);
        reachableNets = [];
        for (int i = 0; i < netCount; i++)
        {
            if (Find(i) == sourceComponent)
                reachableNets.Add(i);
        }
        var connected = new List<Element>();
        var islands = new Dictionary<int, List<(DocumentNode Node, Element Element)>>();
        foreach (var (node, element) in emitted)
        {
            int net = element switch
            {
                CableSegment c => c.A.Value,
                Connector k => k.A.Value,
                Load l => l.Node.Value,
                _ => sourceNet.Value,
            };
            if (Find(net) == sourceComponent)
            {
                connected.Add(element);
            }
            else
            {
                (islands.TryGetValue(Find(net), out var list)
                    ? list
                    : islands[Find(net)] = []).Add((node, element));
            }
        }

        foreach (var island in islands.Values)
        {
            issues.Add(new DocumentIssue
            {
                Kind = DocumentIssueKind.DisconnectedFromSource,
                Message = $"{island.Count} element(s) not connected to the TDR are ignored.",
                NodeIds = island.Select(e => e.Node.Id).ToList(),
            });
        }

        return connected;
    }

    private static DocumentIssue Issue(DocumentIssueKind kind, string message, DocumentNode node)
        => new() { Kind = kind, Message = message, NodeIds = [node.Id] };
}
