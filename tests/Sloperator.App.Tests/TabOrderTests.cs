using Avalonia.Controls;
using Avalonia.Input;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>A bar's dock panel numbers its children's tab places as they show, left to right.</summary>
public sealed class TabOrderTests
{
    [Fact]
    public void Children_docked_right_come_after_the_rest_from_the_middle_out() => HeadlessApp.Run(() =>
    {
        // As a panel's bar is written: the × first, Watch, Open folder, then the path filling the rest.
        var close = new Button { Name = "Close" };
        var watch = new StackPanel { Name = "Watch" };
        var open = new Button { Name = "Open" };
        var path = new TextBlock { Name = "Path" };
        var bar = new DockPanel();
        foreach (var right in (Control[])[close, watch, open])
        {
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
        }
        bar.Children.Add(path);
        TabOrder.SetLeftToRight(bar, true);

        Assert.Equal(KeyboardNavigationMode.Local, KeyboardNavigation.GetTabNavigation(bar));
        Assert.Equal(["Path", "Open", "Watch", "Close"],
            bar.Children.OrderBy(KeyboardNavigation.GetTabIndex).Select(c => c.Name));
        // A child holding buttons takes its place as one.
        Assert.Equal(KeyboardNavigationMode.Local, KeyboardNavigation.GetTabNavigation(watch));

        // One added later is numbered too.
        var left = new Button { Name = "Area" };
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Insert(0, left);
        Assert.Equal(["Area", "Path", "Open", "Watch", "Close"], bar.Children.OrderBy(KeyboardNavigation.GetTabIndex).Select(c => c.Name));
        return true;
    });
}
