using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>The Image editor panel, on the left of the chat window: the pictures, the one shown big with its versions, and how it was made under it when asked for.</summary>
public partial class ImageEditorPanel : UserControl
{
    public ImageEditorPanel()
    {
        InitializeComponent();
        // A drag over the big picture marks a part to redraw; a click picks the version whole.
        Big.Marked += area => (DataContext as ImageEditorViewModel)?.PickArea(area);
        // A click on a picture in the list picks it, as a click on a clip does in the Video editor.
        List.AddHandler(TappedEvent, OnListTapped);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // Prompt metadata takes at most half the room under the bar, as under a file in the File inspector.
        Middle.SizeChanged += (_, e) => RecipeBox.MaxHeight = Math.Max(120, e.NewSize.Height / 2);
        VersionStrip.Attach(this, VersionScroll, VersionList);
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ImageEditorViewModel vm && (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PictureItem item)
            vm.Pick(item);
    }

    private void OnVersionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageEditorViewModel { Selected: { } picture } vm && sender is Control { DataContext: VersionItem version })
            vm.Pick(picture, version);
    }

    /// <summary>Esc: what is picked leaves the box.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (DataContext as ImageEditorViewModel)?.Unpick() == true)
            e.Handled = true;
    }
}
