using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sloperator.App.ViewModels;

namespace Sloperator.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTabKey, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Tab round the dialog through the page's tab: Avalonia's tab control keeps a page's controls in its tabs' one
    /// tab stop, so Tab from Save came back to the page's last control focused (Add a language), and Shift+Tab from the
    /// page's first control stayed on it (checked by UI Automation, 2026-11-10; TabNavigation didn't change that).
    /// </summary>
    private void OnTabKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Tabs.ContainerFromIndex(Tabs.SelectedIndex) is not TabItem page || FocusManager?.GetFocusedElement() is not { } focused)
            return;
        if (e.KeyModifiers == KeyModifiers.None && ReferenceEquals(focused, SaveButton))
        {
            page.Focus(NavigationMethod.Tab);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.Shift)
        {
            // Where Shift+Tab goes nowhere, the page's tab is the place before it.
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(FocusManager?.GetFocusedElement(), focused) && !ReferenceEquals(focused, page))
                    page.Focus(NavigationMethod.Tab);
            });
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // A broken MCP server list is not saved: the error is shown under the box instead, on
        // Advanced, once that page is laid out.
        if (DataContext is SettingsViewModel { HasMcpError: true })
        {
            Tabs.SelectedIndex = 1;
            // The error under the box in sight too: brought to the box alone, it stayed below the window's edge.
            Dispatcher.UIThread.Post(() =>
            {
                McpBox.BringIntoView();
                McpErrorText.BringIntoView();
                McpBox.Focus();
            }, DispatcherPriority.Loaded);
            return;
        }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>The System prompt window; what it saves is kept when this dialog is saved, with the rest.</summary>
    private async void OnEditPrompt(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings)
            return;
        var prompt = settings.EditPrompt();
        if (await new SystemPromptWindow { DataContext = prompt }.ShowDialog<bool>(this))
            settings.SetPrompt(prompt);
    }
}
