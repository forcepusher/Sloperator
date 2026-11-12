using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>
/// A right-click menu of an editor panel's own (<see cref="PanelMenuItem"/>: Rename, Delete, Show in folder…) and
/// the box a new name is typed in. The Video editor's, shared with the Image and Audio editors since their
/// pictures, sounds and versions can be renamed and deleted too (2026-11-13): one menu and one box for all three.
/// </summary>
internal static class PanelMenu
{
    /// <summary>A menu of the panel's at <paramref name="at"/>; for what is a file, the file's menu at its end (Open in Inspector, Copy image or Copy file, see <see cref="FileMenu"/>).</summary>
    public static void Open(IReadOnlyList<PanelMenuItem?> items, Control? at, IImage? picture = null, string? file = null)
    {
        var copy = at is null ? [] : FileMenu.Items(at, picture, null, file);
        if (items.Count == 0 && copy.Count == 0)
            return;
        var menu = new ContextMenu();
        foreach (var item in Entries(items))
            menu.Items.Add(item);
        if (items.Count > 0 && copy.Count > 0)
            menu.Items.Add(new Separator());
        foreach (var item in copy)
            menu.Items.Add(item);
        menu.Open(at);
    }

    /// <summary>The menu's entries as controls: a null is a line between two groups, one with <see cref="PanelMenuItem.Items"/> a submenu.</summary>
    public static IEnumerable<Control> Entries(IReadOnlyList<PanelMenuItem?> items)
    {
        foreach (var item in items)
        {
            if (item is null)
            {
                yield return new Separator();
                continue;
            }
            var entry = new MenuItem { Header = item.Header, IsEnabled = item.Enabled, InputGesture = item.Key is { } key ? KeyGesture.Parse(key) : null };
            if (item.Items is { } inner)
                foreach (var sub in Entries(inner))
                    entry.Items.Add(sub);
            else
            {
                var act = item.Do;
                entry.Click += (_, _) => act();
            }
            yield return entry;
        }
    }

    /// <summary>A box over <paramref name="anchor"/> with the name selected, as Explorer's: Enter or a click elsewhere keeps what is typed, Esc doesn't.</summary>
    public static void AskName(Control anchor, string name, Action<string> done)
    {
        var box = new TextBox { Text = name, MinWidth = 220 };
        AutomationProperties.SetName(box, "New name");
        // Over the name, from its top left corner: centred above it, it went over the title bar and past the window's edge.
        var flyout = new Flyout
        {
            Content = box,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
        };
        var kept = false;
        void Keep()
        {
            if (kept)
                return;
            kept = true;
            done(box.Text ?? "");
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Keep();
                flyout.Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                kept = true;
                flyout.Hide();
                e.Handled = true;
            }
        };
        flyout.Closed += (_, _) => Keep();
        flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Background);
        flyout.ShowAt(anchor);
    }
}
