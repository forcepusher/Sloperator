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
    private readonly Debounce _pick;

    /// <summary>Set while the view lets go of its selection itself (sent, unpicked, another command).</summary>
    private bool _lettingGo;

    public TerminalPanel()
    {
        InitializeComponent();
        _pick = new Debounce(TimeSpan.FromMilliseconds(150), PickSelection);
        Output.TextArea.SelectionChanged += (_, _) =>
        {
            if (!_lettingGo)
                _pick.Restart();
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
        if (_vm is not { Following: true } || Output.Document is not { LineCount: > 0 } document)
            return;
        Dispatcher.UIThread.Post(() => Output.ScrollToLine(document.LineCount), DispatcherPriority.Background);
    }

    /// <summary>The lines the selection is in (<see cref="CodeView.SelectedLines"/>), into the box; nothing selected takes them out.</summary>
    private void PickSelection()
    {
        var (from, to) = Output.SelectedLines() ?? (0, 0);
        _vm?.PickLines(from, to);
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
