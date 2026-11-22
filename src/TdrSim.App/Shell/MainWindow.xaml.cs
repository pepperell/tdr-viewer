using System.ComponentModel;
using System.Windows;
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

    private void OnPaletteItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        if (sender is not FrameworkElement { DataContext: PaletteItem { IsEnabled: true } item } element)
            return;
        DragDrop.DoDragDrop(element, new DataObject(typeof(PaletteItem), item), DragDropEffects.Copy);
    }

    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PaletteItem))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PaletteItem)) is not PaletteItem item)
            return;

        // Mouse position -> graph coordinates through the viewport transform.
        var mouse = e.GetPosition(EditorCanvas);
        var graph = new Point(
            EditorCanvas.ViewportLocation.X + mouse.X / EditorCanvas.ViewportZoom,
            EditorCanvas.ViewportLocation.Y + mouse.Y / EditorCanvas.ViewportZoom);
        _shell.Editor.AddFromPalette(item, graph);
    }
}
