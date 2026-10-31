using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>The Picture generator panel, on the left of the chat window: the pictures, the one picked big with its versions, and how it was made.</summary>
public partial class PictureGeneratorPanel : UserControl
{
    public PictureGeneratorPanel()
    {
        InitializeComponent();
        // A drag over the big picture marks a part to redraw; a click picks the version whole.
        Big.Marked += area => (DataContext as PictureGeneratorViewModel)?.PickArea(area);
        // A click on a picture in the list picks it, as a click on a clip does in the Video generator.
        List.AddHandler(TappedEvent, OnListTapped);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PictureGeneratorViewModel vm && (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PictureItem item)
            vm.Pick(item);
    }

    private void OnVersionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PictureGeneratorViewModel { Selected: { } picture } vm && sender is Control { DataContext: VersionItem version })
            vm.Pick(picture, version);
    }

    /// <summary>Esc: what is picked leaves the box.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (DataContext as PictureGeneratorViewModel)?.Unpick() == true)
            e.Handled = true;
    }
}
