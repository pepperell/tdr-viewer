using System.Windows;
using System.Windows.Controls;

namespace TdrSim.App.Shell;

/// <summary>
/// Freezable data-context relay: a CollectionContainer inside a CompositeCollection is
/// not part of the visual tree, so its bindings cannot reach the window's DataContext.
/// Declared as a resource, this picks the DataContext up through the inheritance
/// context and hands it on via <see cref="Data"/>.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>
/// The Recent menu mixes generated file entries with a literal Separator and toggle
/// item; a plain ItemContainerStyle would be forced onto those too (and throws on the
/// Separator). This applies <see cref="FileStyle"/> to the file entries only.
/// </summary>
public sealed class RecentMenuStyleSelector : StyleSelector
{
    public Style? FileStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
        => item is RecentFile ? FileStyle : null;
}
