using Sloperator.App.ViewModels;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The Video editor's Ctrl+Z and Ctrl+Y: the storyboard before each edit, and what Ctrl+Z took back.</summary>
public sealed class EditHistoryTests
{
    private static readonly Func<IReadOnlyDictionary<string, string>> NoFiles = () => new Dictionary<string, string>();

    /// <summary>An edit as the panel makes one: the storyboard kept, then changed.</summary>
    private static string Edit(EditHistory history, string now, string to)
    {
        history.Keep(now, NoFiles);
        return to;
    }

    /// <summary>Ctrl+Z or Ctrl+Y as the panel does it: the storyboard it gives put back, and where it was left.</summary>
    private static string Put(EditHistory history, EditHistory.State? state)
    {
        Assert.NotNull(state);
        history.Left(state.Json);
        return state.Json;
    }

    [Fact]
    public void Ctrl_Z_takes_the_edits_back_one_by_one_and_Ctrl_Y_makes_them_again()
    {
        var history = new EditHistory();
        var board = Edit(history, "a", "b");
        board = Edit(history, board, "c");
        Assert.False(history.CanRedo(board));

        board = Put(history, history.Undo(board, NoFiles));
        Assert.Equal("b", board);
        board = Put(history, history.Undo(board, NoFiles));
        Assert.Equal("a", board);
        Assert.False(history.CanUndo(board));
        Assert.Null(history.Undo(board, NoFiles));

        Assert.True(history.CanRedo(board));
        board = Put(history, history.Redo(board, NoFiles));
        Assert.Equal("b", board);
        board = Put(history, history.Redo(board, NoFiles));
        Assert.Equal("c", board);
        Assert.Null(history.Redo(board, NoFiles));

        // Made again, the edits can be taken back again.
        Assert.Equal("b", Put(history, history.Undo(board, NoFiles)));
    }

    [Fact]
    public void An_edit_after_Ctrl_Z_drops_what_it_took_back()
    {
        var history = new EditHistory();
        var board = Edit(history, "a", "b");
        board = Put(history, history.Undo(board, NoFiles));

        // A new edit, here or by the agent in the chat: what Ctrl+Z took back isn't made again over it.
        board = Edit(history, board, "x");
        Assert.False(history.CanRedo(board));
        Assert.Null(history.Redo(board, NoFiles));
        Assert.False(history.CanRedo("y"));

        // Ctrl+Z takes the new edit back, and Ctrl+Y makes it, not the one dropped.
        board = Put(history, history.Undo(board, NoFiles));
        Assert.Equal("a", board);
        Assert.Equal("x", Put(history, history.Redo(board, NoFiles)));
    }

    [Fact]
    public void A_press_that_changed_nothing_is_no_step_to_take_back()
    {
        var history = new EditHistory();
        var board = Edit(history, "a", "b");
        // A title clicked, not dragged: kept at the press, and still as it was.
        history.Keep(board, NoFiles);
        history.Keep(board, NoFiles);

        Assert.True(history.CanUndo(board));
        Assert.Equal("a", Put(history, history.Undo(board, NoFiles)));
    }

    [Fact]
    public void The_clips_files_go_back_with_the_storyboard_and_come_again_with_Ctrl_Y()
    {
        var history = new EditHistory();
        var files = new Dictionary<string, string> { ["clip.json"] = "made from the clip" };
        history.Keep("one clip", () => new Dictionary<string, string>(files));
        // A split stamps the halves made from their footage.
        files["clip.json"] = "made from its footage";

        var back = history.Undo("two halves", () => new Dictionary<string, string>(files));
        Assert.Equal("made from the clip", back!.Files["clip.json"]);
        history.Left(back.Json);
        var again = history.Redo(back.Json, NoFiles);
        Assert.Equal("two halves", again!.Json);
        Assert.Equal("made from its footage", again.Files["clip.json"]);
    }

    [Fact]
    public void It_keeps_the_latest_edits_and_another_video_starts_with_none()
    {
        var history = new EditHistory(most: 3);
        var board = "0";
        for (var i = 1; i <= 5; i++)
            board = Edit(history, board, $"{i}");
        for (var i = 4; i >= 2; i--)
            Assert.Equal($"{i}", board = Put(history, history.Undo(board, NoFiles)));
        Assert.False(history.CanUndo(board));

        history.Clear();
        Assert.False(history.CanRedo(board));
        Assert.Null(history.Redo(board, NoFiles));
    }
}
