using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>The File changes panel, on the left of the chat window: the files the agent changed, and their diff, whose lines are picked by selecting them.</summary>
public partial class FileChangesPanel : UserControl
{
    public FileChangesPanel()
    {
        InitializeComponent();
        // Lines and files take their own clicks; what gets here is a click away from them.
        AddHandler(PointerPressedEvent, OnAwayPressed);
        Diff.Picked += rows => _vm?.PickRows(rows);
        Diff.ClickedAway += () => _vm?.Unpick();
        // A file picked by hand, with the mouse or the keys: a section's list made again after a refresh
        // selects its file too, which must not go there.
        FileLists.AddHandler(PointerPressedEvent, OnFilePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileLists.AddHandler(KeyDownEvent, (_, _) => _byHand = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileLists.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            // The only file selected, clicked again with no key held: let go, which the list itself doesn't do.
            if (_lone is { } file)
                _vm?.LetGoOf(file);
            _lone = null;
            Dispatcher.UIThread.Post(() => _byHand = false, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        FileLists.AddHandler(KeyUpEvent, (_, _) => Dispatcher.UIThread.Post(() => _byHand = false, DispatcherPriority.Background), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Set from a press or key in the file lists until it is let go.</summary>
    private bool _byHand;

    /// <summary>The file pressed while it was the only one selected, no key held: let go on the release.</summary>
    private DiffFileItem? _lone;

    private void OnFilePressed(object? sender, PointerPressedEventArgs e)
    {
        _byHand = true;
        _lone = e.KeyModifiers == KeyModifiers.None && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && (e.Source as Control)?.FindLogicalAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: DiffFileItem file }
            && _vm?.IsOnlySelected(file) == true ? file : null;
    }

    /// <summary>
    /// A click in the panel away from its lines (the bar at the top, the list of files but not on a file)
    /// unpicks them, as Esc does; one in the diff that leaves nothing selected does too (<see cref="DiffView.ClickedAway"/>).
    /// A click on a file only goes there, so lines are picked in one file and Ctrl+clicked in another; a click
    /// in the chat keeps them, as the box is clicked to ask about them. Shift or Ctrl held keeps them too: those add.
    /// </summary>
    private void OnAwayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0
            || (e.Source as Visual)?.FindAncestorOfType<DiffView>(includeSelf: true) is not null)
            return;
        _vm?.Unpick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && _vm?.Unpick() == true)
            e.Handled = true;
    }

    private FileChangesViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.GoToRow -= Diff.ShowFile;
            _vm.ShowRow -= Diff.ShowChange;
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Selection.SelectionChanged -= OnPicksChanged;
        }
        _vm = DataContext as FileChangesViewModel;
        if (_vm is not null)
        {
            _vm.GoToRow += Diff.ShowFile;
            _vm.ShowRow += Diff.ShowChange;
            _vm.PropertyChanged += OnVmChanged;
            _vm.Selection.SelectionChanged += OnPicksChanged;
        }
        Diff.Show(_vm?.Rows ?? []);
        OnPicksChanged(null, null);
    }

    /// <summary>The diff read again: its rows into the editor.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileChangesViewModel.Rows))
            Diff.Show(_vm?.Rows ?? []);
    }

    /// <summary>Set while the editor is to select what is picked once the panel is done changing it.</summary>
    private bool _picksPending;

    /// <summary>
    /// What is picked changed (a refresh, a message sent, Esc, a file taken off the box): the editor selects it,
    /// once the change is whole. A refresh lets go of the picks and picks the same lines again: selected only
    /// after, a selection of part of a line stays as it is when the lines are the same.
    /// </summary>
    private void OnPicksChanged(object? sender, SelectionModelSelectionChangedEventArgs<DiffRow>? e)
    {
        if (_picksPending)
            return;
        _picksPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _picksPending = false;
            Diff.ShowPicks(_vm?.Selection.SelectedIndexes.ToList() ?? []);
        }, DispatcherPriority.Background);
    }

    /// <summary>A file's row in the list: ListBoxItem.lines (grey) while some of its lines are picked.</summary>
    private void OnFileContainer(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ListBoxItem item || item.DataContext is not DiffFileItem file)
            return;
        item.Classes.Set("lines", file.HasPickedLines);
        // Each list is made again with new files on a refresh, so a row holds one file for its life.
        file.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(DiffFileItem.HasPickedLines) && item.DataContext == file)
                item.Classes.Set("lines", file.HasPickedLines);
        };
    }

    private void OnFilePicked(object? sender, SelectionChangedEventArgs e)
    {
        // To the file just clicked in: the last one added, Shift+click adding a run.
        if (_byHand && e.AddedItems.OfType<DiffFileItem>().LastOrDefault() is { } file && _vm is { Reselecting: false })
            _vm.GoToCommand.Execute(file);
    }

    /// <summary>
    /// A right-click on a file, in the list or on its header over its lines: every file's menu (Open in
    /// Inspector, Open in Code editor, Copy file or Copy image, see <see cref="FileMenu"/>) with Show in
    /// folder before Copy. A deleted file has nothing to open, show or copy, so all are greyed out.
    /// </summary>
    private void OnFileMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source
            || ((source as StyledElement)?.FindLogicalAncestorOfType<ListBoxItem>(includeSelf: true) as Control ?? source) is not { } item
            || item.DataContext switch { DiffFileItem f => f, DiffRow { IsHeader: true } row => row.File, _ => null } is not { } file)
            return;
        e.Handled = true;
        var show = new MenuItem { Header = "Show in folder" };
        show.Click += (_, _) => FilePreview.ShowInFolder(file.FullPath);
        var exists = File.Exists(file.FullPath);
        var menu = new ContextMenu();
        foreach (var entry in (List<Control>)[.. FileMenu.Open(item, file.FullPath), show, .. FileMenu.Copy(item, null, null, file.FullPath)])
        {
            entry.IsEnabled = exists;
            menu.Items.Add(entry);
        }
        menu.Open(item);
    }
}
