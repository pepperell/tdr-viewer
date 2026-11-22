using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TdrSim.App.Editor;

/// <summary>A wire between two ports.</summary>
public sealed partial class ConnectionViewModel(
    EditorViewModel editor, PortViewModel source, PortViewModel target) : ObservableObject
{
    public PortViewModel Source { get; } = source;

    public PortViewModel Target { get; } = target;

    [RelayCommand]
    private void Remove() => editor.RemoveConnection(this);
}

/// <summary>
/// Nodify's pending-connection pattern: StartedCommand receives the port the drag
/// started from, CompletedCommand the port it was dropped on (or null for empty space).
/// Ports never refuse a connection - multi-wire ports are how tees are built.
/// </summary>
public sealed partial class PendingConnectionViewModel(EditorViewModel editor) : ObservableObject
{
    private PortViewModel? _source;

    [RelayCommand]
    private void Start(PortViewModel? source) => _source = source;

    [RelayCommand]
    private void Finish(PortViewModel? target)
    {
        if (_source is not null && target is not null)
            editor.Connect(_source, target);
        _source = null;
    }
}
