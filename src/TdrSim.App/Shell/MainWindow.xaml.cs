using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TdrSim.App.Editor;

namespace TdrSim.App.Shell;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow()
    {
        InitializeComponent();
        _shell = new ShellViewModel();
        DataContext = _shell;
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
