using CommunityToolkit.Mvvm.ComponentModel;
using TdrSim.Core.Engine;

namespace TdrSim.App.Plot;

/// <summary>
/// Display state for the bounce-diagram tab: the lossless lattice of the current chain
/// network next to the simulated trace it explains. The shell rebuilds it on every
/// published trace while the tab is visible; the view (BouncePanel.xaml.cs) owns the
/// ScottPlot calls.
/// </summary>
public sealed partial class BounceViewModel : ObservableObject
{
    private IReadOnlyList<string> _hoverNodes = [];

    /// <summary>Shown instead of the plots (non-chain network, nothing to simulate).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "Open this tab with an unbranched network to see its bounce diagram.";

    /// <summary>What the lattice idealizes away for this network.</summary>
    [ObservableProperty]
    private string _caption = "";

    /// <summary>The rho/tau product of the ray under the mouse.</summary>
    [ObservableProperty]
    private string _hoverReadout = "";

    public bool HasMessage => Message.Length > 0;

    public BounceDiagram? Diagram { get; private set; }

    public TdrTrace? Trace { get; private set; }

    /// <summary>Displayed time extent, shared with the trace panel.</summary>
    public double MaxTimeS { get; private set; }

    /// <summary>Resolves a document node id to its canvas title.</summary>
    public Func<string, string> TitleOf { get; set; } = id => id;

    public event Action? Changed;

    /// <summary>Document nodes at the two ends of the hovered ray (empty = none).</summary>
    public event Action<IReadOnlyList<string>>? HoverNodesChanged;

    public void Set(BounceDiagram diagram, TdrTrace trace, double maxTimeS)
    {
        Diagram = diagram;
        Trace = trace;
        MaxTimeS = maxTimeS;
        Message = "";

        var notes = new List<string>();
        if (diagram.IsApproximate)
            notes.Add("approximate: the lattice is lossless and uses the DC limit of reactive parts - the gap to the simulated trace is the loss and the reactance");
        else
            notes.Add("exact for this lossless, resistive network (the simulated trace only adds the instrument's rise time)");
        if (diagram.Truncated)
            notes.Add($"truncated at {diagram.Rays.Count} rays");
        Caption = string.Join("  ·  ", notes);
        Changed?.Invoke();
    }

    public void Clear(string message)
    {
        Diagram = null;
        Trace = null;
        Message = message;
        Caption = "";
        HoverReadout = "";
        PublishHoverNodes([]);
        Changed?.Invoke();
    }

    public void SetHoveredRay(BounceRay? ray)
    {
        if (ray is null || Diagram is null)
        {
            HoverReadout = "";
            PublishHoverNodes([]);
            return;
        }

        string amplitude = $"{ray.Amplitude:+0.0000;−0.0000}";
        if (ray.Factors is { } factors)
        {
            HoverReadout = factors.Count == 0
                ? $"incident step = {amplitude}"
                : factors.Count <= 9
                    ? $"{string.Join("·", factors)} = {string.Join(" · ", factors.Select(f => $"{f.Value:0.###;−0.###}"))} = {amplitude}"
                    : $"{string.Join("·", factors.Take(4))}·…·{factors[^1]}  ({factors.Count} factors) = {amplitude}";
        }
        else
        {
            HoverReadout = $"Σ of {ray.PathCount} coincident paths = {amplitude}";
        }

        HoverReadout += $"    ({ray.StartS * 1e9:0.##} → {ray.EndS * 1e9:0.##} ns)";
        var nodes = Diagram.Interfaces[ray.FromInterface].NodeIds
            .Concat(Diagram.Interfaces[ray.ToInterface].NodeIds)
            .Distinct()
            .ToList();
        PublishHoverNodes(nodes);
    }

    private void PublishHoverNodes(IReadOnlyList<string> nodes)
    {
        if (nodes.SequenceEqual(_hoverNodes))
            return;
        _hoverNodes = nodes;
        HoverNodesChanged?.Invoke(nodes);
    }
}
