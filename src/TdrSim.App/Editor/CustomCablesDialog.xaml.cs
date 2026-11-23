using System.Windows;

namespace TdrSim.App.Editor;

/// <summary>Modal manager for the document's user-defined cables; the caller applies
/// <see cref="CustomCablesViewModel.Result"/> when ShowDialog returns true.</summary>
public partial class CustomCablesDialog : Window
{
    public CustomCablesDialog() => InitializeComponent();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
