using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TdrSim.Core.Catalog;
using TdrSim.Core.Model;
using TdrSim.Core.Project;

namespace TdrSim.App.Editor;

/// <summary>
/// The canvas: nodes, wires, selection, and the document mapping. ToDocument() is the
/// single source for both serialization and compilation; LoadDocument is its inverse.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly ComponentCatalog _catalog;
    private readonly InstrumentViewModel _instrument;
    private readonly IReadOnlyList<ConnectorChoice> _cableConnectorChoices;
    private int _nextId;

    public EditorViewModel(ComponentCatalog catalog, InstrumentViewModel instrument)
    {
        _catalog = catalog;
        _instrument = instrument;
        _cableConnectorChoices = ConnectorChoices.Build(catalog);
        PendingConnection = new PendingConnectionViewModel(this);
        SelectedNodes.CollectionChanged += (_, _) =>
            SelectedNode = SelectedNodes.Count == 1 ? SelectedNodes[0] : null;
    }

    public ObservableCollection<NodeViewModelBase> Nodes { get; } = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<NodeViewModelBase> SelectedNodes { get; } = [];

    public PendingConnectionViewModel PendingConnection { get; }

    [ObservableProperty]
    private NodeViewModelBase? _selectedNode;

    /// <summary>Raised on any mutation that changes what ToDocument would return
    /// (structure, values, or positions) - the shell recompiles badges and sets dirty.</summary>
    public event EventHandler? GraphChanged;

    public bool HasTdr => Nodes.Any(n => n is TdrNodeViewModel);

    // ---- graph mutations ----

    public void AddFromPalette(PaletteItem item, Point location)
    {
        if (item.Kind == DocumentNodeKind.Tdr && HasTdr)
            return;

        NodeViewModelBase node = item.Kind switch
        {
            DocumentNodeKind.Tdr => new TdrNodeViewModel(NewId("tdr"), _instrument),
            DocumentNodeKind.Cable => new CableNodeViewModel(
                NewId("cable"), _catalog.Cables, ((CableSpec)item.Payload!).Id, _cableConnectorChoices)
            {
                // PCB traces are centimeters, not tens of meters.
                LengthM = ((CableSpec)item.Payload!).Category == "pcb" ? 0.1 : 30,
            },
            DocumentNodeKind.Connector => new ConnectorNodeViewModel(
                NewId("connector"), _catalog.Connectors, ((ConnectorSpec)item.Payload!).Id),
            DocumentNodeKind.Load => LoadNodeViewModel.CreateWithDefaults(
                NewId("load"), (LoadKind)item.Payload!),
            DocumentNodeKind.Fault => FaultNodeViewModel.CreateFromPreset(
                NewId("fault"), (FaultSpec)item.Payload!, _catalog.Cables),
            DocumentNodeKind.Junction => new JunctionNodeViewModel(NewId("junction")),
            _ => throw new InvalidOperationException($"Unknown palette kind {item.Kind}."),
        };
        node.Location = location;
        AttachNode(node);
        OnPropertyChanged(nameof(HasTdr));
        RaiseGraphChanged();
    }

    public void Connect(PortViewModel a, PortViewModel b)
    {
        if (ReferenceEquals(a, b))
            return;
        bool exists = Connections.Any(c =>
            (c.Source == a && c.Target == b) || (c.Source == b && c.Target == a));
        if (exists)
            return;
        Connections.Add(NewConnection(a, b));
        RefreshPortStates();
        RaiseGraphChanged();
    }

    /// <summary>Wires carry no electrical direction, but the bezier leaves its source
    /// rightward and enters its target from the left; putting the output-side port
    /// (right edge of its node) first keeps the curve outside both node bodies no
    /// matter which way the user dragged.</summary>
    private ConnectionViewModel NewConnection(PortViewModel a, PortViewModel b)
    {
        bool aIsOutput = a.Owner.OutputPorts.Contains(a);
        bool bIsOutput = b.Owner.OutputPorts.Contains(b);
        return bIsOutput && !aIsOutput
            ? new ConnectionViewModel(this, b, a)
            : new ConnectionViewModel(this, a, b);
    }

    public void RemoveConnection(ConnectionViewModel connection)
    {
        if (Connections.Remove(connection))
        {
            RefreshPortStates();
            RaiseGraphChanged();
        }
    }

    /// <summary>Alt-click on a port (Nodify's DisconnectConnectorCommand).</summary>
    [RelayCommand]
    private void DisconnectPort(PortViewModel? port)
    {
        if (port is null)
            return;
        var touching = Connections.Where(c => c.Source == port || c.Target == port).ToList();
        if (touching.Count == 0)
            return;
        foreach (var connection in touching)
            Connections.Remove(connection);
        RefreshPortStates();
        RaiseGraphChanged();
    }

    [RelayCommand]
    private void DeleteSelection()
    {
        if (SelectedNodes.Count == 0)
            return;
        foreach (var node in SelectedNodes.ToList())
        {
            foreach (var connection in Connections
                .Where(c => c.Source.Owner == node || c.Target.Owner == node).ToList())
            {
                Connections.Remove(connection);
            }

            node.PropertyChanged -= OnNodePropertyChanged;
            Nodes.Remove(node);
        }

        SelectedNodes.Clear();
        RefreshPortStates();
        OnPropertyChanged(nameof(HasTdr));
        RaiseGraphChanged();
    }

    // ---- document mapping ----

    public ProjectDocument ToDocument(InstrumentDocument instrument) => new()
    {
        SchemaVersion = ProjectDocument.CurrentSchemaVersion,
        Instrument = instrument,
        Nodes = Nodes.Select(n => n.ToDocumentNode()).ToList(),
        Wires = Connections.Select(c => new DocumentWire
        {
            FromNodeId = c.Source.Owner.Id,
            FromPort = c.Source.Name,
            ToNodeId = c.Target.Owner.Id,
            ToPort = c.Target.Name,
        }).ToList(),
    };

    public void LoadDocument(ProjectDocument document)
    {
        foreach (var node in Nodes)
            node.PropertyChanged -= OnNodePropertyChanged;
        SelectedNodes.Clear();
        Connections.Clear();
        Nodes.Clear();

        var ports = new Dictionary<(string Node, string Port), PortViewModel>();
        foreach (var docNode in document.Nodes)
        {
            var node = CreateFromDocument(docNode);
            node.Location = new Point(docNode.X, docNode.Y);
            AttachNode(node);
            foreach (var port in node.Ports)
                ports[(node.Id, port.Name)] = port;
        }

        foreach (var wire in document.Wires)
        {
            if (ports.TryGetValue((wire.FromNodeId, wire.FromPort), out var source)
                && ports.TryGetValue((wire.ToNodeId, wire.ToPort), out var target))
            {
                Connections.Add(NewConnection(source, target));
            }
        }

        RefreshPortStates();
        OnPropertyChanged(nameof(HasTdr));
        RaiseGraphChanged();
    }

    private NodeViewModelBase CreateFromDocument(DocumentNode docNode) => docNode.Kind switch
    {
        DocumentNodeKind.Tdr => new TdrNodeViewModel(docNode.Id, _instrument),
        DocumentNodeKind.Cable => Hydrate(
            new CableNodeViewModel(docNode.Id, _catalog.Cables, docNode.CablePresetId, _cableConnectorChoices)
            {
                LengthM = docNode.LengthM ?? 0,
                Z0OverrideOhms = docNode.Z0OverrideOhms,
                VfOverride = docNode.VfOverride,
            }, docNode),
        DocumentNodeKind.Connector => new ConnectorNodeViewModel(
            docNode.Id, _catalog.Connectors, docNode.ConnectorPresetId),
        DocumentNodeKind.Load => new LoadNodeViewModel(docNode.Id, docNode.LoadKind ?? LoadKind.Open)
        {
            ROhms = docNode.ROhms,
            LNanohenries = docNode.LHenries * 1e9,
            CPicofarads = docNode.CFarads * 1e12,
        },
        DocumentNodeKind.Fault => FaultNodeViewModel.FromDocument(docNode, _catalog),
        DocumentNodeKind.Junction => new JunctionNodeViewModel(docNode.Id),
        _ => throw new InvalidOperationException($"Unknown node kind {docNode.Kind}."),
    };

    private static CableNodeViewModel Hydrate(CableNodeViewModel cable, DocumentNode docNode)
    {
        cable.InitConnectors(docNode.ConnectorAId, docNode.ConnectorBId);
        return cable;
    }

    // ---- plumbing ----

    private void AttachNode(NodeViewModelBase node)
    {
        Nodes.Add(node);
        node.PropertyChanged += OnNodePropertyChanged;
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Selection and display-only mirror properties don't change the document.
        // Slider companions (Z0Log, RLog, ...) mirror the stored fields, which raise
        // their own notifications - skipping them here avoids duplicate recompiles.
        if (e.PropertyName is nameof(NodeViewModelBase.IsSelected)
            or nameof(CableNodeViewModel.Title)
            or nameof(CableNodeViewModel.LengthLog)
            or nameof(CableNodeViewModel.Z0Log)
            or nameof(CableNodeViewModel.VfSlider)
            or nameof(CableNodeViewModel.Z0DefaultNote)
            or nameof(CableNodeViewModel.VfDefaultNote)
            or nameof(CableNodeViewModel.ConnectorDefaultNote)
            or nameof(LoadNodeViewModel.RLog)
            or nameof(LoadNodeViewModel.LLog)
            or nameof(LoadNodeViewModel.CLog)
            or nameof(FaultNodeViewModel.Z0ScaleSlider)
            or nameof(FaultNodeViewModel.ExtraLossSlider)
            or nameof(LoadNodeViewModel.UsesR)
            or nameof(LoadNodeViewModel.UsesL)
            or nameof(LoadNodeViewModel.UsesC))
        {
            return;
        }

        RaiseGraphChanged();
    }

    private void RefreshPortStates()
    {
        var connected = new HashSet<PortViewModel>();
        foreach (var connection in Connections)
        {
            connected.Add(connection.Source);
            connected.Add(connection.Target);
        }

        foreach (var node in Nodes)
        {
            foreach (var port in node.Ports)
                port.IsConnected = connected.Contains(port);
        }
    }

    private void RaiseGraphChanged() => GraphChanged?.Invoke(this, EventArgs.Empty);

    private string NewId(string prefix)
    {
        while (true)
        {
            string id = $"{prefix}-{++_nextId}";
            if (Nodes.All(n => n.Id != id))
                return id;
        }
    }
}
