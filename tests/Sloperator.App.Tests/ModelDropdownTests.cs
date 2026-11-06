using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The model dropdown: search over a list too long to fit, pinned models first, and the picked model never lost to either.</summary>
public sealed class ModelDropdownTests
{
    private sealed record Shown(Window Window, ModelDropdown Box, ObservableCollection<string> Models);

    private static Shown Show(int count)
    {
        var models = new ObservableCollection<string>(Enumerable.Range(0, count).Select(i => $"vendor/model-{i:000}"));
        var box = new ModelDropdown
        {
            ItemsSource = models,
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel()),
            SelectedIndex = 0,
            Width = 300,
        };
        var window = new Window { Width = 800, Height = 600, Content = box };
        window.Show();
        return new Shown(window, box, models);
    }

    private static TextBox Search(ModelDropdown box) =>
        box.GetVisualDescendants().OfType<Popup>().Single().Child!.GetVisualDescendants().OfType<TextBox>().Single();

    private static void Open(ModelDropdown box)
    {
        box.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(TextBox search, Key key) =>
        search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = search });

    private static void Ui(Action act) => HeadlessApp.Run(() =>
    {
        act();
        return 0;
    });

    private static string[] Rows(ModelDropdown box) =>
        box.GetRealizedContainers().Where(row => row.IsVisible).Select(row => (string)row.DataContext!).ToArray();

    [Theory]
    [InlineData("qwen3.8-27b@q6_k_xl", "QWEN", true)]
    [InlineData("qwen3.8-27b@q6_k_xl", "27b qwen", true)]
    [InlineData("qwen3.8-27b@q6_k_xl", "27b gemma", false)]
    [InlineData("anything", "  ", true)]
    [InlineData("anything", null, true)]
    public void Words_match_in_any_order_and_case(string model, string? words, bool matches) =>
        Assert.Equal(matches, ModelDropdown.Matches(model, words));

    [Fact]
    public void A_short_list_has_no_search_box()
    {
        Ui(() =>
        {
                var shown = Show(5);
                Open(shown.Box);
                Assert.False(Search(shown.Box).IsVisible);
        });
    }

    [Fact]
    public void A_list_that_does_not_fit_has_one()
    {
        Ui(() =>
        {
                var shown = Show(50);
                Open(shown.Box);
                Assert.True(Search(shown.Box).IsVisible);
        });
    }

    [Fact]
    public void Typing_leaves_the_rows_that_match_and_keeps_the_pick_and_the_width()
    {
        Ui(() =>
        {
                var shown = Show(50);
                Open(shown.Box);
                var search = Search(shown.Box);
                var list = (Control)search.GetVisualParent()!;
                list.UpdateLayout();
                var width = list.Bounds.Width;
                search.Text = "model-04";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(10, Rows(shown.Box).Length);
                Assert.Equal("vendor/model-000", shown.Box.SelectedItem);
                list.UpdateLayout();
                Assert.Equal(width, list.Bounds.Width);
        });
    }

    [Fact]
    public void Enter_picks_the_first_match_and_closing_clears_the_search()
    {
        Ui(() =>
        {
                var shown = Show(50);
                Open(shown.Box);
                var search = Search(shown.Box);
                search.Text = "model-012";
                Dispatcher.UIThread.RunJobs();
                Press(search, Key.Enter);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("vendor/model-012", shown.Box.SelectedItem);
                Assert.False(shown.Box.IsDropDownOpen);
                Assert.Equal("", search.Text);
                Assert.Equal(50, Rows(shown.Box).Length);
        });
    }

    [Fact]
    public void A_space_stays_in_the_search_and_the_list_open()
    {
        Ui(() =>
        {
                var shown = Show(50);
                Open(shown.Box);
                Press(Search(shown.Box), Key.Space);
                Dispatcher.UIThread.RunJobs();
                Assert.True(shown.Box.IsDropDownOpen);
        });
    }

    [Fact]
    public void Pinned_models_go_first_sorted_and_the_rest_after_them()
    {
        var models = new List<string>();
        string[] pinned = ["z-model", "m-model"];
        foreach (var model in new[] { "b-model", "z-model", "a-model", "m-model" })
            MainWindowViewModel.InsertModel(models, model, pinned);
        Assert.Equal(["m-model", "z-model", "a-model", "b-model"], models);
    }

    [Fact]
    public void The_pin_shows_under_the_pointer_and_stays_once_pinned()
    {
        Ui(() =>
        {
            var icon = new PathIcon();
            var row = new ComboBoxItem { Content = new Button { Classes = { "pin" }, Content = icon } };
            new Window { Content = new ListBox { Items = { row } } }.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, icon.Opacity);
            ((IPseudoClasses)row.Classes).Add(":pointerover");
            Assert.Equal(1, icon.Opacity);
            ((IPseudoClasses)row.Classes).Remove(":pointerover");
            row.Classes.Add("pinned");
            Assert.Equal(1, icon.Opacity);
        });
    }

    [Fact]
    public void A_moved_row_keeps_the_pick()
    {
        Ui(() =>
        {
                var shown = Show(5);
                shown.Box.SelectedIndex = 3;
                Dispatcher.UIThread.RunJobs();
                shown.Models.Remove("vendor/model-003");
                shown.Models.Insert(0, "vendor/model-003");
                shown.Box.SelectedItem = "vendor/model-003";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(0, shown.Box.SelectedIndex);
                Assert.Equal("vendor/model-003", shown.Box.SelectionBoxItem);
        });
    }
}
