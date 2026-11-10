using Sloperator.App.ViewModels;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>A long message the user sent folds to its first lines in the chat, all of it going to the model.</summary>
public sealed class LongMessageTests
{
    [Fact]
    public void A_pasted_log_shows_its_first_lines_until_show_all()
    {
        var text = "How many lines?\n" + string.Join('\n', Enumerable.Range(1, 3000).Select(i => $"line {i}"));
        var message = new UserMessageItem(text);

        Assert.True(message.IsLong);
        Assert.Equal(UserMessageItem.FoldedLines, message.ShownText.Split('\n').Length);
        Assert.EndsWith("line 19…", message.ShownText);
        Assert.Equal("Show all 3,001 lines", message.FoldText);
        Assert.Equal(text, message.Input.Text);

        message.ToggleAllCommand.Execute(null);
        Assert.Equal(text, message.ShownText);
        Assert.Equal("Show less", message.FoldText);
    }

    [Fact]
    public void Short_messages_and_ones_a_little_over_stay_whole()
    {
        Assert.False(new UserMessageItem("hello").IsLong);
        var thirty = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        Assert.False(new UserMessageItem(thirty).IsLong);
        Assert.Equal(thirty, new UserMessageItem(thirty).ShownText);

        // One long line folds by its length.
        var paragraph = new UserMessageItem(new string('x', 5_000));
        Assert.True(paragraph.IsLong);
        Assert.Equal(UserMessageItem.FoldedCharacters + 1, paragraph.ShownText.Length);
        Assert.Equal("Show all", paragraph.FoldText);
    }
}
