using System.Windows;
using TdrSim.App.Shell;

namespace TdrSim.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow();
        window.Show();

        // A .tdrproj passed on the command line (file association, drag-onto-exe,
        // headless smoke tests) opens over the default document; otherwise the last
        // session's project comes back (when that setting is on).
        if (e.Args.Length > 0)
            window.OpenProject(e.Args[0]);
        else
            window.ReopenLastProject();
    }
}
