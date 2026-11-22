namespace TdrSim.Core.Model;

public enum LoadKind
{
    Open,
    Short,
    Resistor,
    Capacitor,
    Inductor,

    /// <summary>R, L, C in series; absent components are omitted (treated as 0 ohm links).</summary>
    SeriesRlc,

    /// <summary>R, L, C in parallel; absent components are omitted (treated as disconnected).</summary>
    ParallelRlc,
}
