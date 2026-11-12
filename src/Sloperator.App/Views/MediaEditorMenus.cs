using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>
/// What a right-click does in the Image and Audio editors, the same in both: on a picture or a sound in the list
/// its menu (Rename, Delete, Show in folder, then the file's own entries for its newest version), on a version's
/// button that version's (Delete, Show in folder, the file's entries); F2 on the list renames the one shown, in a
/// box over its name.
/// </summary>
internal static class MediaEditorMenus
{
    /// <param name="panel">The editor's panel, whose DataContext is its view model.</param>
    /// <param name="list">The list of pictures or sounds.</param>
    /// <param name="versions">The strip of the versions' buttons.</param>
    /// <param name="small">An item's small picture in the list, for Copy image; null for a sound.</param>
    public static void Attach<TItem>(Control panel, ListBox list, ItemsControl versions, Func<TItem, IImage?> small) where TItem : MediaFolderItem
    {
        MediaEditorViewModel<TItem>? Vm() => panel.DataContext as MediaEditorViewModel<TItem>;

        list.ContextRequested += (_, e) =>
        {
            if (Vm() is not { } vm || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: TItem item } row)
                return;
            e.Handled = true;
            PanelMenu.Open(vm.MenuFor(item), row, small(item), item.Versions.LastOrDefault());
        };
        versions.ContextRequested += (_, e) =>
        {
            if (Vm() is not { } vm || (e.Source as Control)?.FindAncestorOfType<Button>(includeSelf: true) is not { DataContext: VersionItem version } button)
                return;
            e.Handled = true;
            PanelMenu.Open(vm.MenuFor(version), button, version.Thumbnail, version.Path);
        };
        list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None && Vm()?.AskRename() == true)
                e.Handled = true;
        }, RoutingStrategies.Tunnel);

        // The box for the new name, over the name in the list as Explorer's is; once the menu has closed.
        void Asked(TItem item) => Dispatcher.UIThread.Post(() =>
        {
            var row = list.ContainerFromItem(item);
            var anchor = row?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() ?? row ?? list;
            PanelMenu.AskName(anchor, item.Name, typed => Vm()?.Rename(item, typed));
        }, DispatcherPriority.Background);
        MediaEditorViewModel<TItem>? asking = null;
        panel.DataContextChanged += (_, _) =>
        {
            if (asking is not null)
                asking.RenameAsked -= Asked;
            asking = Vm();
            if (asking is not null)
                asking.RenameAsked += Asked;
        };
    }
}
