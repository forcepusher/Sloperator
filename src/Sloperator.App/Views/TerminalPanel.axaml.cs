using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

/// <summary>The Terminal panel, on the left of the chat window: the agent's commands in a list, the one picked shown whole beside it.</summary>
public partial class TerminalPanel : UserControl
{
    private TerminalViewModel? _vm;

    /// <summary>Lines selected go into the box once the selection rests a moment, as the Code editor's.</summary>
    private readonly DispatcherTimer _pickTimer;

    /// <summary>Set while the view lets go of its selection itself (sent, unpicked, another command).</summary>
    private bool _lettingGo;

    public TerminalPanel()
    {
        InitializeComponent();
        _pickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _pickTimer!.Stop();
            PickSelection();
        });
        Output.TextArea.SelectionChanged += (_, _) =>
        {
            if (_lettingGo)
                return;
            _pickTimer.Stop();
            _pickTimer.Start();
        };
        Output.Dress();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Grew -= FollowEnd;
            _vm.Unpicked -= LetGoOfSelection;
        }
        _vm = DataContext as TerminalViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.Grew += FollowEnd;
            _vm.Unpicked += LetGoOfSelection;
        }
        ShowDocument();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.Document))
            ShowDocument();
    }

    private void ShowDocument()
    {
        _lettingGo = true;
        try { Output.Document = _vm?.Document ?? new AvaloniaEdit.Document.TextDocument(); }
        finally { _lettingGo = false; }
        FollowEnd();
    }

    /// <summary>Watching, the output is kept at its last line as lines come; scrolled away from it with Watch off, it stays.</summary>
    private void FollowEnd()
    {
        if (_vm is not { Watch: true } || Output.Document is not { LineCount: > 0 } document)
            return;
        Dispatcher.UIThread.Post(() => Output.ScrollToLine(document.LineCount), DispatcherPriority.Background);
    }

    /// <summary>The lines the selection is in (a part of a line means the line), into the box; nothing selected takes them out.</summary>
    private void PickSelection()
    {
        if (_vm is null)
            return;
        var selection = Output.TextArea.Selection;
        if (selection.IsEmpty || Output.Document is not { } document)
        {
            _vm.PickLines(0, 0);
            return;
        }
        var start = document.GetLineByOffset(selection.SurroundingSegment.Offset).LineNumber;
        var end = document.GetLineByOffset(selection.SurroundingSegment.EndOffset).LineNumber;
        // A selection ending at the start of a line (a whole line dragged over) doesn't take that line.
        if (end > start && document.GetLineByNumber(end).Offset == selection.SurroundingSegment.EndOffset)
            end--;
        _vm.PickLines(start, end);
    }

    private void LetGoOfSelection()
    {
        _lettingGo = true;
        try { Output.TextArea.ClearSelection(); }
        finally { _lettingGo = false; }
    }

    /// <summary>Esc: the lines picked leave the box.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm?.Unpick() == true)
            e.Handled = true;
    }
}
