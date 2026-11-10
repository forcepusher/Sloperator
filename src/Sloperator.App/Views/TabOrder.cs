using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Sloperator.App.Views;

/// <summary>
/// Tab through a bar as it reads, left to right. A dock panel's children docked right come before the one that
/// fills it (Avalonia docks them in that order), so Tab went to them first, the rightmost first: a panel's × before
/// its Watch and its Open folder, then the folder's path (checked by UI Automation, 2026-11-10). With
/// <c>views:TabOrder.LeftToRight="True"</c> the panel numbers its children's tab places as they show, in a group of its own.
/// </summary>
public static class TabOrder
{
    public static readonly AttachedProperty<bool> LeftToRightProperty = AvaloniaProperty.RegisterAttached<DockPanel, bool>("LeftToRight", typeof(TabOrder));

    static TabOrder() =>
        LeftToRightProperty.Changed.AddClassHandler<DockPanel>((panel, e) =>
        {
            if (e.NewValue is not true)
                return;
            KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Local);
            Number(panel);
            panel.Children.CollectionChanged += (_, _) => Number(panel);
        });

    public static bool GetLeftToRight(DockPanel panel) => panel.GetValue(LeftToRightProperty);
    public static void SetLeftToRight(DockPanel panel, bool value) => panel.SetValue(LeftToRightProperty, value);

    /// <summary>The children docked left and the one filling the rest, in order, then those docked right from the middle out.</summary>
    private static void Number(DockPanel panel)
    {
        var children = panel.Children.ToList();
        bool Right(Control c) => DockPanel.GetDock(c) == Dock.Right && !(panel.LastChildFill && ReferenceEquals(c, children[^1]));
        var place = 0;
        foreach (var child in children.Where(c => !Right(c)).Concat(Enumerable.Reverse(children.Where(Right).ToList())))
        {
            KeyboardNavigation.SetTabIndex(child, place++);
            // A child holding buttons (Watch and Wrap, the ×) takes its place as one: its own buttons kept no number.
            if (!child.Focusable)
                KeyboardNavigation.SetTabNavigation(child, KeyboardNavigationMode.Local);
        }
    }
}
