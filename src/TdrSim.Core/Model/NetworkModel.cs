namespace TdrSim.Core.Model;

/// <summary>
/// Identifies a connection point in the network. Ground/return is implicit (all
/// elements are defined between their node and the common return).
/// </summary>
public readonly record struct NodeId(int Value)
{
    public override string ToString() => $"n{Value}";
}

/// <summary>
/// The full network description: a flat list of elements referencing shared nodes.
/// This is the general graph model - M1 solves chains only, but M2's nodal solver and
/// the M3 editor consume the same type unchanged.
/// </summary>
public sealed record NetworkModel
{
    public required IReadOnlyList<Element> Elements { get; init; }
}
