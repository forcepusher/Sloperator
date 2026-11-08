using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sloperator.App.ViewModels;
using Sloperator.Core.Agent;

namespace Sloperator.App.Views;

/// <summary>The system prompt every model gets, to read and change; opened from Settings' Advanced.</summary>
public partial class SystemPromptWindow : Window
{
    private SystemPromptViewModel? _vm;

    /// <summary>Set while the editor is given the view model's text, which is then not the user's typing.</summary>
    private bool _showing;

    public SystemPromptWindow()
    {
        InitializeComponent();
        Editor.Dress();
        Editor.TextArea.TextView.LineTransformers.Add(new MarkerColours());
        Editor.TextChanged += (_, _) =>
        {
            if (!_showing && _vm is not null)
                _vm.Text = Editor.Text;
        };
        Opened += (_, _) => Editor.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as SystemPromptViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnVmChanged;
        _showing = true;
        try { Editor.Text = _vm.Text; }
        finally { _showing = false; }
    }

    /// <summary>Reset to default: Sloperator's text in the editor, as one step Ctrl+Z takes back.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemPromptViewModel.Text) || _vm is null || Editor.Document is not { } document || document.Text == _vm.Text)
            return;
        _showing = true;
        try { document.Replace(0, document.TextLength, _vm.Text); }
        finally { _showing = false; }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>The words in braces Sloperator fills in for each chat (<see cref="SystemPrompt.Markers"/>), in the accent.</summary>
    private sealed class MarkerColours : DocumentColorizingTransformer
    {
        private static readonly Regex Markers = new(string.Join("|", SystemPrompt.Markers.Select(Regex.Escape)), RegexOptions.IgnoreCase);

        protected override void ColorizeLine(DocumentLine line)
        {
            if (Application.Current?.FindResource("BbAccent") is not IBrush accent)
                return;
            foreach (Match marker in Markers.Matches(CurrentContext.Document.GetText(line)))
                ChangeLinePart(line.Offset + marker.Index, line.Offset + marker.Index + marker.Length,
                    element => element.TextRunProperties.SetForegroundBrush(accent));
        }
    }
}
