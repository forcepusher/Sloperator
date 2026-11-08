using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Agent;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// The System prompt window (Settings, Advanced): Sloperator's own prompt until the user changes it, Reset to
/// default taking a change back, and only a change kept, so an untouched prompt follows the settings and new versions.
/// </summary>
public sealed class SystemPromptTests
{
    private static readonly string Default = AgentSetup.DefaultPrompt(new AgentSettings());

    [Fact]
    public void Sloperators_own_prompt_is_not_kept_as_the_users()
    {
        var prompt = new SystemPromptViewModel(Default, Default);
        Assert.False(prompt.IsEdited);
        Assert.False(prompt.ResetCommand.CanExecute(null));
        Assert.Equal("", prompt.Saved);
        Assert.StartsWith("Default · about ", prompt.StatusText);

        // Lines ended another way, or a blank line more at the end, are still Sloperator's text.
        prompt.Text = Default.ReplaceLineEndings("\n") + "\n\n";
        Assert.False(prompt.IsEdited);
        Assert.Equal("", prompt.Saved);
    }

    [Fact]
    public void A_change_is_kept_and_Reset_to_default_takes_it_back()
    {
        var prompt = new SystemPromptViewModel(Default, Default) { Text = "Answer like a pirate. " + Default };
        Assert.True(prompt.IsEdited);
        Assert.True(prompt.ResetCommand.CanExecute(null));
        Assert.Equal(prompt.Text, prompt.Saved);
        Assert.StartsWith("Edited · about ", prompt.StatusText);

        prompt.ResetCommand.Execute(null);
        Assert.Equal(Default, prompt.Text);
        Assert.Equal("", prompt.Saved);

        // Nothing at all is no prompt of the user's: Sloperator's is sent.
        prompt.Text = "  \n";
        Assert.Equal("", prompt.Saved);
    }

    [Fact]
    public void Its_length_is_reckoned_as_a_chats_two_tokens_for_seven_characters()
    {
        Assert.Equal("Edited · about 2,000 tokens", SystemPromptViewModel.Describe(new string('a', 7_000), edited: true));
        Assert.Equal("Default · about 0 tokens", SystemPromptViewModel.Describe("abc", edited: false));
    }

    [Fact]
    public void The_window_shows_the_text_takes_typing_and_puts_Sloperators_back_as_one_undo()
    {
        var (shown, typed, reset, undone, edited) = HeadlessApp.Run(() =>
        {
            var prompt = new SystemPromptViewModel(Default, Default);
            var window = new SystemPromptWindow { DataContext = prompt };
            window.Show();
            var editor = window.FindControl<CodeView>("Editor")!;
            var shown = editor.Text;
            editor.Document.Insert(0, "Answer like a pirate. ");
            var typed = prompt.Text;
            prompt.ResetCommand.Execute(null);
            var reset = editor.Text;
            editor.Undo();
            var undone = prompt.Text;
            var edited = prompt.IsEdited;
            window.Close();
            return (shown, typed, reset, undone, edited);
        });
        Assert.Equal(Default, shown);
        Assert.Equal("Answer like a pirate. " + Default, typed);
        Assert.Equal(Default, reset);
        Assert.Equal("Answer like a pirate. " + Default, undone);
        Assert.True(edited);
    }

    [Fact]
    public void The_words_filled_in_for_each_chat_are_in_the_accent()
    {
        var (marker, plain, accent) = HeadlessApp.Run(() =>
        {
            var window = new SystemPromptWindow { DataContext = new SystemPromptViewModel(Default, Default), Width = 900, Height = 700 };
            window.Show();
            window.UpdateLayout();
            var editor = window.FindControl<CodeView>("Editor")!;
            var view = editor.TextArea.TextView;
            view.EnsureVisualLines();
            Color? At(int offset)
            {
                var line = view.GetOrConstructVisualLine(editor.Document.GetLineByOffset(offset));
                var column = offset - line.FirstDocumentLine.Offset;
                var element = line.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);
                return (element.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color;
            }
            var date = editor.Text.IndexOf("{date}", StringComparison.Ordinal);
            var result = (At(date + 1), At(date - 3), ((ISolidColorBrush)Application.Current!.FindResource("BbAccent")!).Color);
            window.Close();
            return result;
        });
        Assert.Equal(accent, marker);
        Assert.NotEqual(accent, plain);
    }
}
