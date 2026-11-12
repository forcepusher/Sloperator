using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>
/// The versions' strip in the foot of the Image and Audio editors: more versions than fit scroll with the wheel
/// (a scroll bar has no room in a line of the foot), and the one shown is brought into sight, so the version the
/// agent just made is there to see without scrolling to it. Before, the ones past the edge were cut off.
/// </summary>
internal static class VersionStrip
{
    /// <summary>How far a notch of the wheel moves the strip: about a version's button.</summary>
    private const double Notch = 64;

    /// <param name="panel">The editor's panel, whose DataContext is its view model (it says when another version is shown).</param>
    public static void Attach(Control panel, ScrollViewer scroll, ItemsControl list)
    {
        scroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            var most = scroll.Extent.Width - scroll.Viewport.Width;
            if (most <= 0)
                return;
            scroll.Offset = scroll.Offset.WithX(Math.Clamp(scroll.Offset.X - e.Delta.Y * Notch, 0, most));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        INotifyPropertyChanged? shown = null;
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Shown")
                return;
            // Once its button is laid out: a version just added has none yet.
            Dispatcher.UIThread.Post(() =>
            {
                if (list.Items.OfType<VersionItem>().FirstOrDefault(v => v.IsShown) is { } version)
                    list.ContainerFromItem(version)?.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
        panel.DataContextChanged += (_, _) =>
        {
            if (shown is not null)
                shown.PropertyChanged -= Changed;
            shown = panel.DataContext as INotifyPropertyChanged;
            if (shown is not null)
                shown.PropertyChanged += Changed;
        };
    }
}
