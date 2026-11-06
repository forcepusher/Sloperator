using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Sloperator.App.Views;

/// <summary>
/// The model dropdown: a dropdown as any other, with a search box over its list when the rows don't
/// fit, and a pin on each row (MainWindow.axaml). A row that doesn't match is hidden, never taken out
/// of the list: the ComboBox would lose a model picked that the words leave out (see
/// <see cref="ViewModels.MainWindowViewModel.SetModels"/>). So its rows sit in a plain StackPanel, as
/// a virtualizing one has no containers to hide for the rows out of view.
/// </summary>
public sealed class ModelDropdown : ComboBox
{
    /// <summary>A row's height: Inter's 14 px line and 6 over and under it (ComboBoxItem).</summary>
    private const double RowHeight = 30;

    private TextBox? _search;
    private TextBlock? _noMatch;
    private DockPanel? _body;

    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <summary>Whether a model is pinned, for its row's pin.</summary>
    public Func<string, bool>? IsPinned { get; set; }

    public ModelDropdown()
    {
        ContainerPrepared += (_, e) => UpdateRow(e.Container);
        DropDownClosed += (_, _) =>
        {
            if (_search is not null)
                _search.Text = "";
            if (_body is not null)
                _body.MinWidth = 0;
        };
    }

    /// <summary>Whether a model's name has all the words typed, in any order and case.</summary>
    public static bool Matches(string model, string? words) =>
        (words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => model.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The list's rows show who is pinned again, after a pin was clicked.</summary>
    public void UpdateRows()
    {
        foreach (var row in GetRealizedContainers())
            UpdateRow(row);
    }

    /// <summary>The search box goes over the list inside Fluent's own popup, so the rest of the dropdown stays as every other.</summary>
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Popup>("PART_Popup")?.Child is not Border { Child: ScrollViewer list } border)
            return;
        _search = new TextBox { PlaceholderText = "Search models", Margin = new(4), IsVisible = false };
        Avalonia.Automation.AutomationProperties.SetName(_search, "Search models");
        _search.TextChanged += (_, _) => Filter();
        _search.AddHandler(KeyDownEvent, SearchKey, RoutingStrategies.Tunnel);
        _noMatch = new TextBlock { Text = "No models match", Margin = new(12, 6), IsVisible = false };
        _noMatch.Classes.Add("muted");
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_noMatch, Dock.Top);
        border.Child = null;
        border.Child = _body = new DockPanel { Children = { _search, _noMatch, list } };
    }

    /// <summary>The search box shows before the list does, which puts the picked row in view and the focus on it, so the search takes the focus after that.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsDropDownOpenProperty && change.GetNewValue<bool>() && _search is { } search)
        {
            search.IsVisible = ItemCount * RowHeight > MaxDropDownHeight;
            if (search.IsVisible)
                Dispatcher.UIThread.Post(() => search.Focus());
        }
        base.OnPropertyChanged(change);
    }

    /// <summary>Shows the rows that match. The list keeps its width, or it would jump with every letter.</summary>
    private void Filter()
    {
        if (_body is { MinWidth: 0 } body)
            body.MinWidth = body.Bounds.Width;
        UpdateRows();
        if (_noMatch is not null)
            _noMatch.IsVisible = FirstShown() is null;
    }

    private void UpdateRow(Control row)
    {
        if (row.DataContext is not string model)
            return;
        row.IsVisible = Matches(model, _search?.Text);
        row.Classes.Set("pinned", IsPinned?.Invoke(model) == true);
    }

    private Control? FirstShown() => GetRealizedContainers().FirstOrDefault(row => row.IsVisible);

    /// <summary>On its tunnel, as a text box takes the arrows for its caret: down goes to the rows, Enter picks the first.</summary>
    private void SearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
            FirstShown()?.Focus(NavigationMethod.Directional);
        else if (e.Key == Key.Enter && FirstShown() is { DataContext: string model })
        {
            SetCurrentValue(SelectedItemProperty, model);
            SetCurrentValue(IsDropDownOpenProperty, false);
        }
        else
            return;
        e.Handled = true;
    }

    /// <summary>A press the search box or a pin took: an open dropdown closes on any press that isn't its own rows'.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.Handled || !IsDropDownOpen)
            base.OnPointerPressed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // A space in the words typed, which an open dropdown takes for picking the row. Marked
        // handled it would never reach the box: Windows then drops the character that goes with it.
        if (e.Key == Key.Space && e.Source == _search)
            return;
        if (e.Key == Key.Up && _search is { IsVisible: true } search && e.Source == FirstShown())
        {
            search.Focus();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Typing with a row focused goes on with the search, where the dropdown would jump to a row and pick it.</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (IsDropDownOpen && _search is { IsVisible: true } search)
        {
            search.Text += e.Text;
            search.CaretIndex = search.Text?.Length ?? 0;
            search.Focus();
            e.Handled = true;
            return;
        }
        base.OnTextInput(e);
    }
}
