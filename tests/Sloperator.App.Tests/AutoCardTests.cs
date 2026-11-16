using Sloperator.App.ViewModels;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// Auto's card says what it did on its line and why under it, whole (design A, the user, 2026-11-16): at the end of a
/// one-line card the helper's reason was cut off after a few words, and "Auto … Done in 1.3 s" didn't say what the card was.
/// </summary>
public sealed class AutoCardTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 11, 5, 40, DateTimeKind.Utc);

    private const string Why = "The user is asking a planning question about how to split the settings work into steps, which needs some "
        + "reasoning across several files but no deep architecture changes, so the daily driver at medium effort fits.";

    private static ToolCallItem Card(string summary, ToolItemState state = ToolItemState.Done, string result = "") =>
        new("auto-1", "Auto", summary) { At = T0, Ended = T0.AddMilliseconds(1300), State = state, ResultText = result };

    [Fact]
    public void A_pick_says_what_it_did_on_the_line_and_why_under_it()
    {
        var card = Card($"Picked claude-sonnet-5-5 at Medium for this message\n{Why}", result: "- Settings live in `SettingsStore.cs`.");

        Assert.Equal("Picked claude-sonnet-5-5 at Medium for this message", card.Line);
        Assert.Equal(Why, card.Reason);
        Assert.True(card.HasReason);
        Assert.Equal("Picked in 1.3 s", card.StatusText);
        Assert.Equal("Notes it passed to claude-sonnet-5-5", card.OutputHeader);
        Assert.Equal("- Settings live in `SettingsStore.cs`.", card.FoundText);
    }

    [Fact]
    public void Staying_off_a_dearer_pick_names_the_model_kept_and_the_one_turned_down()
    {
        var card = Card($"Stayed on claude-sonnet-5-5 at Extra high for this message, not claude-opus-5-5\nA redesign across many files.");

        Assert.Equal("Stayed on claude-sonnet-5-5 at Extra high for this message, not claude-opus-5-5", card.Line);
        Assert.Equal("A redesign across many files.", card.Reason);
        Assert.Equal("Notes it passed to claude-sonnet-5-5", card.OutputHeader);
    }

    [Fact]
    public void While_it_asks_to_move_up_the_card_waits_for_the_answer()
    {
        var card = Card("Wants claude-opus-5-5 at High for this message\nA redesign across many files.", ToolItemState.Running);
        card.Progress = "Waiting for your answer";

        Assert.Equal("Wants claude-opus-5-5 at High for this message", card.Line);
        Assert.Equal("A redesign across many files.", card.Reason);
        Assert.Equal("Waiting for your answer", card.StatusText);
    }

    [Fact]
    public void A_failed_pick_keeps_the_model_and_has_no_reason()
    {
        var card = Card("Kept claude-sonnet-5-5 at Medium for this message: the pick failed", ToolItemState.Error, "claude-haiku-5-5 couldn't pick: 500");

        Assert.Equal("Kept claude-sonnet-5-5 at Medium for this message: the pick failed", card.Line);
        Assert.False(card.HasReason);
        Assert.Equal("Output", card.OutputHeader);
        Assert.Equal("Failed after 1.3 s", card.StatusText);
    }

    [Fact]
    public void A_line_alone_has_no_reason_under_it()
    {
        var card = Card("Picked claude-haiku-5-5 at Medium for this message");

        Assert.Equal("", card.Reason);
        Assert.False(card.HasReason);
        Assert.Equal("Notes it passed to claude-haiku-5-5", card.OutputHeader);
        // A model that takes no effort: the model alone.
        Assert.Equal("Notes it passed to qwen/qwen3-32b", Card("Picked qwen/qwen3-32b for this message").OutputHeader);
    }

    [Fact]
    public void A_chat_saved_before_shows_its_reason_under_the_line_too()
    {
        // One line, the reason after " · ", and the notes signed at their end.
        var card = Card($"claude-sonnet-5-5 at High (you stayed off claude-opus-5-5) · {Why}",
            result: "- Settings live in `SettingsStore.cs`.\n\nFound by claude-haiku-5-5. These notes go to claude-sonnet-5-5 with your message.");

        Assert.Equal("claude-sonnet-5-5 at High (you stayed off claude-opus-5-5)", card.Line);
        Assert.Equal(Why, card.Reason);
        Assert.Equal("Notes it passed to claude-sonnet-5-5", card.OutputHeader);
        Assert.Equal("- Settings live in `SettingsStore.cs`.", card.FoundText);
        Assert.Equal("claude-haiku-5-5: the evaluation failed", Card("claude-haiku-5-5: the evaluation failed", ToolItemState.Error).Line);
        Assert.Equal("Notes it passed to claude-haiku-5-5", Card("claude-haiku-5-5 at Low").OutputHeader);
    }

    [Fact]
    public void Other_cards_keep_their_line_and_done()
    {
        var read = new ToolCallItem("call_1", "read_file", "Read notes.md · the settings part") { At = T0, Ended = T0.AddMilliseconds(1300), State = ToolItemState.Done };

        Assert.Equal("Read notes.md · the settings part", read.Line);
        Assert.False(read.HasReason);
        Assert.Equal("Done in 1.3 s", read.StatusText);
        Assert.Equal("Output", read.OutputHeader);
    }
}
