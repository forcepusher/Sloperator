using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

/// <summary>
/// The System prompt window, opened from Settings' Advanced: the prompt every model gets, Sloperator's own
/// until the user changes it. Its words in braces (<see cref="Core.Agent.SystemPrompt.Markers"/>) are
/// filled in for each chat.
/// </summary>
/// <param name="text">The user's own prompt, or Sloperator's when they have none.</param>
/// <param name="defaultText">Sloperator's own, as their settings make it (<see cref="Core.Agent.AgentSetup.DefaultPrompt"/>).</param>
public sealed partial class SystemPromptViewModel(string text, string defaultText) : ObservableObject
{
    public string DefaultText { get; } = defaultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private string _text = text;

    /// <summary>Whether the text is the user's own rather than Sloperator's.</summary>
    public bool IsEdited => !Same(Text, DefaultText);

    public string StatusText => $"{Describe(Text, IsEdited)} · words in yellow are filled in for each chat";

    /// <summary>Sloperator's own prompt back in the box; Save keeps it.</summary>
    [RelayCommand(CanExecute = nameof(IsEdited))]
    private void Reset() => Text = DefaultText;

    /// <summary>What Settings keeps: empty for Sloperator's own text (or none), so it follows the settings and later versions.</summary>
    public string Saved => IsEdited && !string.IsNullOrWhiteSpace(Text) ? Text : "";

    /// <summary>"Default" or "Edited", and about how many tokens it takes, as Sloperator reckons a chat's (two for every seven characters).</summary>
    public static string Describe(string text, bool edited) =>
        $"{(edited ? "Edited" : "Default")} · about {(long)text.Length * 2 / 7:N0} tokens";

    /// <summary>The same text, but for how its lines end and the blanks at its end.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(a.ReplaceLineEndings("\n").TrimEnd(), b.ReplaceLineEndings("\n").TrimEnd(), StringComparison.Ordinal);
}
