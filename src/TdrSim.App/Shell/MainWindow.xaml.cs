using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TdrSim.App.Editor;

namespace TdrSim.App.Shell;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private bool _anchorRefreshQueued;

    public MainWindow()
    {
        InitializeComponent();
        _shell = new ShellViewModel();
        DataContext = _shell;

        // Nodify only recomputes a connector's Anchor on a few triggers (its own
        // drag-start, IsConnected flipping, its node moving/resizing), so a port can
        // keep a stale anchor and its wire visibly misses the circle - classically on
        // whichever end a new connection was dropped on. Re-anchor everything after
        // each mutation, once layout has settled.
        _shell.Editor.GraphChanged += (_, _) => ScheduleAnchorRefresh();
        Loaded += (_, _) => ScheduleAnchorRefresh();

        // Starting up, opening a file or an example: bring the network into view.
        _shell.DocumentLoaded += ScheduleAutoCenter;
        Loaded += (_, _) => ScheduleAutoCenter();
    }

    /// <summary>Center the network once the new nodes have been laid out. Zooms out to
    /// fit a big network but never in past 100 %, so a lone TDR does not fill the canvas.</summary>
    private void ScheduleAutoCenter()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!IsLoaded || EditorCanvas.ActualWidth <= 0)
                return;
            EditorCanvas.FitToScreen();
            if (EditorCanvas.ViewportZoom > 1)
            {
                var extent = EditorCanvas.ItemsExtent;
                EditorCanvas.ViewportZoom = 1;
                EditorCanvas.ViewportLocation = new Point(
                    extent.X + extent.Width / 2 - EditorCanvas.ActualWidth / 2,
                    extent.Y + extent.Height / 2 - EditorCanvas.ActualHeight / 2);
            }

            ScheduleAnchorRefresh();
        });
    }

    private void ScheduleAnchorRefresh()
    {
        if (_anchorRefreshQueued)
            return;
        _anchorRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _anchorRefreshQueued = false;
            foreach (var connector in Descendants<Nodify.Connector>(EditorCanvas))
                connector.UpdateAnchor();
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    /// <summary>Open a .tdrproj passed on the command line.</summary>
    public void OpenProject(string path) => _shell.OpenPath(path);

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (!_shell.ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        _shell.Shutdown();
    }

    // Canvas hover -> trace shading. A press clears it so a node being dragged (or a
    // wire being drawn from it) does not strobe the plot; it returns on re-entry.
    private void OnNodeMouseEnter(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released
            && sender is FrameworkElement { DataContext: NodeViewModelBase node })
        {
            _shell.Editor.HoveredNode = node;
        }
    }

    private void OnNodeMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NodeViewModelBase node }
            && ReferenceEquals(_shell.Editor.HoveredNode, node))
        {
            _shell.Editor.HoveredNode = null;
        }
    }

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
        => _shell.Editor.HoveredNode = null;

    private void OnCustomCablesClick(object sender, RoutedEventArgs e) => ShowCustomCables(null);

    private void OnEditCustomCableClick(object sender, RoutedEventArgs e)
        => ShowCustomCables((sender as FrameworkElement)?.DataContext is CableNodeViewModel cable
            ? cable.PresetId
            : null);

    /// <summary>The custom-cable manager; accepting it is one undoable document edit.</summary>
    private void ShowCustomCables(string? selectId)
    {
        var viewModel = _shell.Editor.CreateCustomCablesViewModel(selectId);
        var dialog = new CustomCablesDialog { Owner = this, DataContext = viewModel };
        if (dialog.ShowDialog() == true)
            _shell.Editor.SetCustomCables(viewModel.Result());
    }

    // ---- component bar ----

    /// <summary>A square of the component bar: drop its list down, add its one
    /// component, or open the cable manager.</summary>
    private void OnPaletteButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PaletteButton button } element)
            return;
        switch (button.Action)
        {
            case PaletteAction.CableManager:
                ShowCustomCables(null);
                break;
            case PaletteAction.Add when button.DirectItem is { } item:
                AddAtViewCentre(item);
                break;
            case PaletteAction.Menu:
                OpenPaletteMenu(element, button);
                break;
        }
    }

    /// <summary>Right-click (or the menu key) opens the same list in the same place as a
    /// click; a button without a list has nothing to show.</summary>
    private void OnPaletteContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: PaletteButton { Action: PaletteAction.Menu } button } element)
            OpenPaletteMenu(element, button);
    }

    private static void OpenPaletteMenu(Button element, PaletteButton button)
    {
        if (element.ContextMenu is not { } menu || button.Entries.Count == 0)
            return;
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnPaletteMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: PaletteItem item })
            AddAtViewCentre(item);
    }

    /// <summary>New components land in the middle of what the canvas is showing (graph
    /// coordinates, less roughly half a node so the node itself looks centred).</summary>
    private void AddAtViewCentre(PaletteItem item)
    {
        var centre = new Point(
            EditorCanvas.ViewportLocation.X + EditorCanvas.ViewportSize.Width / 2 - 70,
            EditorCanvas.ViewportLocation.Y + EditorCanvas.ViewportSize.Height / 2 - 30);
        _shell.Editor.AddFromPalette(item, centre);
        EditorCanvas.Focus();
    }

    // ---- one-touch view controls ----

    private void OnCenterViewClick(object sender, RoutedEventArgs e) => EditorCanvas.FitToScreen();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => EditorCanvas.ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => EditorCanvas.ZoomOut();
}
