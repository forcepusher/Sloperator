using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>The Audio editor panel, on the left of the chat window: the sounds, the one shown as its waveform across the panel, its edits and versions in two lines under it, and how it was made when asked for.</summary>
public partial class AudioEditorPanel : UserControl
{
    public AudioEditorPanel()
    {
        InitializeComponent();
        // A click on a sound in the list picks it, as a click on a picture does in the Image editor.
        List.AddHandler(TappedEvent, OnListTapped);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // Prompt metadata takes at most half the room over the foot's lines, as under a picture in the Image editor.
        Middle.SizeChanged += (_, e) => RecipeBox.MaxHeight = Math.Max(120, e.NewSize.Height / 2);
        VersionStrip.Attach(this, VersionScroll, VersionList);
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AudioEditorViewModel vm && (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is AudioItem item)
            vm.Pick(item);
    }

    private void OnVersionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AudioEditorViewModel { Selected: { } sound } vm && sender is Control { DataContext: VersionItem version })
            vm.Pick(sound, version);
    }

    /// <summary>
    /// Esc: what is picked leaves the box. Space plays or pauses, Delete cuts the stretch picked; on the
    /// waveform, Left and Right move the playhead a hundredth of the view (with Shift a tenth), Home and End
    /// to its ends, as in the File inspector.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AudioEditorViewModel vm)
            return;
        if (e.Key == Key.Escape)
        {
            e.Handled = vm.Unpick();
            return;
        }
        if (vm.Sound is not { } sound || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None || e.Source is TextBox)
            return;
        var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 0.01;
        switch (e.Key)
        {
            case Key.Space:
                sound.PlayCommand.Execute(null);
                break;
            case Key.Delete when vm.CutCommand.CanExecute(null):
                vm.CutCommand.Execute(null);
                break;
            case Key.Left when Waveform.IsFocused:
                sound.Nudge(-step);
                break;
            case Key.Right when Waveform.IsFocused:
                sound.Nudge(step);
                break;
            case Key.Home when Waveform.IsFocused:
                sound.Seek(0);
                break;
            case Key.End when Waveform.IsFocused:
                sound.Seek(sound.Length);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
