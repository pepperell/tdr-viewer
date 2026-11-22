using TdrSim.Core.Catalog;

namespace TdrSim.Core.Model;

public abstract record Element;

/// <summary>The TDR instrument's reference plane. Exactly one per network.</summary>
public sealed record SourcePort(NodeId Node) : Element;

/// <summary>A length of cable between two nodes.</summary>
public sealed record CableSegment(NodeId A, NodeId B, CableSpec Spec, double LengthM) : Element;

/// <summary>A lumped connector/splice between two nodes.</summary>
public sealed record Connector(NodeId A, NodeId B, ConnectorSpec Spec) : Element;

/// <summary>
/// A one-port load from a node to the implicit return. At a chain's far end it
/// terminates the line; at an interior node it acts as a shunt discontinuity.
/// </summary>
public sealed record Load(
    NodeId Node,
    LoadKind Kind,
    double? ROhms = null,
    double? LHenries = null,
    double? CFarads = null) : Element;
