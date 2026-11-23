using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TdrSim.App.Editor;

/// <summary>
/// One connection point on a node. Named Port (never Connector) throughout the view
/// model layer: Connector is taken by both the model element and Nodify's control.
/// Ports accept any number of wires - several wires meeting at a port is a tee.
/// </summary>
public partial class PortViewModel(NodeViewModelBase owner, string name) : ObservableObject
{
    public NodeViewModelBase Owner { get; } = owner;

    /// <summary>"A"/"B" on two-ports, "P" on one-ports (matches DocumentPorts).</summary>
    public string Name { get; } = name;

    /// <summary>What the canvas shows beside the port: the name, or on a cable end
    /// the name plus its connector type ("A:BNC").</summary>
    [ObservableProperty]
    private string _label = name;

    /// <summary>Set by Nodify's connector control; connections draw between anchors.</summary>
    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;
}
