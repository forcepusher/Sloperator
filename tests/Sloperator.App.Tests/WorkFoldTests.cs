using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// A finished run's work folded under a card: its tool calls and thinking out of sight, its reply
/// and its stop in sight, Show steps to see them again.
/// </summary>
public sealed class WorkFoldTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 14, 2, 11, DateTimeKind.Utc);

    private static UserMessageItem Message(string text, int second) => new(text) { At = T0.AddSeconds(second) };

    private static AssistantMessageItem Step(int second, string text = "", string thoughts = "") =>
        new() { At = T0.AddSeconds(second), Text = text, Reasoning = thoughts, IsStreaming = false };

    private static ToolCallItem Call(string name, int second) => new($"call_{name}_{second}", name, name) { At = T0.AddSeconds(second), State = ToolItemState.Done };

    [Fact]
    public void A_finished_run_folds_its_tool_calls_and_thinking_and_keeps_its_reply()
    {
        var step = Step(1, thoughts: "Read it first.");
        var read = Call("read_file", 2);
        var edit = Call("edit_file", 3);
        var again = Call("read_file", 4);
        var reply = Step(5, "Done.", "All done.");
        reply.RunTook = TimeSpan.FromSeconds(192);
        ChatItemViewModel[] chat = [Message("Make it darker", 0), step, read, edit, again, reply];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        var fold = step.Card!;
        Assert.Equal("Worked for 3 min 12 s · 3 tool calls", fold.Title);
        Assert.Equal(["read_file", "edit_file"], fold.Tools);
        Assert.All(new ChatItemViewModel[] { step, read, edit, again }, l => Assert.False(l.IsShown));
        Assert.All(new ChatItemViewModel[] { read, edit, again }, l => Assert.False(l.InChat));
        Assert.True(step.InChat); // the card shows over it
        Assert.True(reply.IsShown);
        Assert.False(reply.ShowsThoughts);
        Assert.False(reply.ShowsRunTook); // the card says it
        Assert.Null(chat[0].Fold);

        fold.ToggleCommand.Execute(null);
        Assert.Equal("Hide steps", fold.ToggleText);
        Assert.All(chat, l => Assert.True(l.IsShown));
        Assert.True(reply.ShowsThoughts);
    }

    [Fact]
    public void The_card_says_which_model_Auto_picked_once_its_card_is_folded_away()
    {
        // Auto's card is no tool call, and its pick, folded away, is said on the card: nothing else said who answered.
        var auto = new ToolCallItem("auto-1", "Auto",
            "claude-sonnet-5-5 at High (you stayed off claude-opus-5-5) · A change across the settings code.") { At = T0.AddSeconds(1), State = ToolItemState.Done };
        var reply = Step(5, "Done.");
        reply.RunTook = TimeSpan.FromSeconds(24);
        ChatItemViewModel[] chat = [Message("Move the settings into a view model", 0), auto, Call("read_file", 3), reply];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.Equal("Worked for 24 s · 1 tool call · claude-sonnet-5-5 at High", auto.Card!.Title);
        Assert.Equal(["read_file"], auto.Card.Tools);
        Assert.Equal("claude-haiku-5-5", new ToolCallItem("auto-3", "Auto", "claude-haiku-5-5: the evaluation failed") { State = ToolItemState.Error }.AutoPicked);
        Assert.Equal("claude-haiku-5-5 at Low", new ToolCallItem("auto-4", "Auto", "claude-haiku-5-5 at Low") { State = ToolItemState.Done }.AutoPicked);
        // Stopped while it picked: no pick to say.
        Assert.Null(new ToolCallItem("auto-2", "Auto", "claude-haiku-5-5 picks the model for this message…") { State = ToolItemState.Stopped }.AutoPicked);
    }

    [Fact]
    public void A_message_sent_during_a_run_stays_and_each_stretch_folds_on_its_own()
    {
        var first = Call("screenshot", 2);
        var steer = Message("Use the blue one", 10);
        var second = Call("mouse_click", 12);
        var reply = Step(14, "Clicked the blue one.");
        reply.RunTook = TimeSpan.FromSeconds(14);
        ChatItemViewModel[] chat = [Message("Pick a colour", 0), Step(1), first, steer, Step(11), second, reply];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.Null(steer.Fold);
        Assert.NotSame(first.Fold, second.Fold);
        Assert.Equal("Worked for 9.0 s · 1 tool call", chat[1].Card!.Title); // from its first line to the message
        Assert.Equal("Worked for 14 s · 1 tool call", chat[4].Card!.Title);
        Assert.True(reply.IsShown);
    }

    [Fact]
    public void A_stop_stays_in_sight_with_Continue()
    {
        var stop = new NoticeItem("Stopped with Pause/Break.", isError: false, carryOn: () => { }) { At = T0.AddSeconds(9), RunTook = TimeSpan.FromSeconds(9) };
        var call = Call("press_keys", 3);
        ChatItemViewModel[] chat = [Message("Type it", 0), Step(1), call, stop];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.False(call.IsShown);
        Assert.True(stop.IsShown);
        Assert.True(stop.CanContinue);
        Assert.Equal("Worked for 9.0 s · 1 tool call", chat[1].Card!.Title);
    }

    [Fact]
    public void A_plain_answer_has_nothing_to_fold()
    {
        var reply = Step(2, "Hello!");
        reply.RunTook = TimeSpan.FromSeconds(2);
        ChatItemViewModel[] chat = [Message("Hi", 0), reply];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.Null(reply.Fold);
        Assert.True(reply.ShowsRunTook);
    }

    [Fact]
    public void An_answer_that_only_thought_folds_its_thoughts_under_a_card_over_it()
    {
        var reply = Step(4, "Paris.", "The capital of France.");
        reply.RunTook = TimeSpan.FromSeconds(4);
        ChatItemViewModel[] chat = [Message("Capital of France?", 0), reply];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.Equal("Worked for 4.0 s", reply.Card!.Title);
        Assert.False(reply.Card.HasTools);
        Assert.True(reply.IsShown);
        Assert.False(reply.ShowsThoughts);
    }

    [Fact]
    public void A_run_still_going_stays_open_and_an_old_chat_folds_to_its_end()
    {
        var done = Step(3, "Done.");
        done.RunTook = TimeSpan.FromSeconds(3);
        var going = Call("wait", 6);
        ChatItemViewModel[] chat = [Message("First", 0), Call("read_file", 1), done, Message("Second", 5), Step(6), going];

        WorkFold.FoldFinished(chat, toTheEnd: false);
        Assert.NotNull(chat[1].Fold);
        Assert.Null(going.Fold);

        // Saved before runs kept their times: no line ends a run, the chat's end does; the reply stays.
        var reply = Step(4, "Done.");
        var call = Call("read_file", 2);
        ChatItemViewModel[] old = [Message("First", 0), Step(1), call, reply];
        foreach (var line in old)
            line.At = null;
        WorkFold.FoldFinished(old, toTheEnd: true);
        Assert.False(call.IsShown);
        Assert.True(reply.IsShown);
        Assert.Equal("1 tool call", old[1].Card!.Title);
    }

    [Fact]
    public void A_folded_run_is_folded_once()
    {
        var reply = Step(3, "Done.");
        reply.RunTook = TimeSpan.FromSeconds(3);
        var call = Call("read_file", 1);
        ChatItemViewModel[] chat = [Message("First", 0), call, reply];
        WorkFold.FoldFinished(chat, toTheEnd: false);
        var fold = call.Fold;
        fold!.IsOpen = true;

        WorkFold.FoldFinished(chat, toTheEnd: true);

        Assert.Same(fold, call.Fold);
        Assert.True(fold.IsOpen);
    }

    [Fact]
    public void The_card_moves_to_the_next_line_when_a_long_run_drops_the_first()
    {
        var first = Call("screenshot", 1);
        var next = Call("mouse_click", 2);
        var reply = Step(3, "Done.");
        reply.RunTook = TimeSpan.FromSeconds(3);
        WorkFold.FoldFinished([Message("Go", 0), first, next, reply], toTheEnd: false);
        var fold = first.Card!;

        fold.Drop(first);

        Assert.Same(fold, next.Card);
        Assert.True(next.InChat);
    }

    [Fact]
    public void Folded_lines_take_no_room_in_the_chat_and_Show_steps_brings_them_back()
    {
        var (folded, places, gapless, open, cardsFolded, cardsOpen, title) = HeadlessApp.Run(() =>
        {
            var reply = Step(9, "Done. The title screen is darker.", "Done.");
            reply.RunTook = TimeSpan.FromSeconds(9);
            var chat = new ChatItemViewModel[] { Message("Make it darker", 0), Step(1, thoughts: "Read it."), Call("read_file", 2), Step(3), Call("edit_file", 4), reply };
            WorkFold.FoldFinished(chat, toTheEnd: false);
            return Shown(chat, (scroll, items) =>
            {
                int ToolCards() => scroll.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("card") && b.DataContext is ToolCallItem && b.IsEffectivelyVisible);
                var height = items.Bounds.Height;
                // The message, the card (over the first step, which is folded) and the reply, 12 between them.
                var shown = items.GetRealizedContainers().Where(c => c.IsVisible).ToList();
                var gapless = height - shown.Sum(c => c.Bounds.Height) - 12 * (shown.Count - 1);
                var title = scroll.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Worked for 9.0 s · 2 tool calls").IsEffectivelyVisible;
                var folded = ToolCards();
                var button = scroll.GetVisualDescendants().OfType<Button>().Single(b => b.Command == chat[1].Card!.ToggleCommand);
                button.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                scroll.UpdateLayout();
                return (height, shown.Count, gapless, items.Bounds.Height, folded, ToolCards(), title);
            });
        });
        Assert.True(title);
        Assert.Equal(3, places);
        Assert.InRange(gapless, -0.5, 0.5);
        Assert.Equal((0, 2), (cardsFolded, cardsOpen));
        Assert.True(open > folded + 100, $"{folded} folded, {open} open");
    }

    /// <summary>The chat's own templates, out of a main window, showing these lines.</summary>
    private static T Shown<T>(IReadOnlyList<ChatItemViewModel> chat, Func<ScrollViewer, ItemsControl, T> read)
    {
        var main = new MainWindow();
        var scroll = main.FindControl<ScrollViewer>("ChatScroll")!;
        ((Panel)scroll.Parent!).Children.Remove(scroll);
        var items = scroll.GetLogicalDescendantsOfType<ItemsControl>().First();
        items.ItemsSource = chat;
        var window = new Window { Width = 900, Height = 900, Content = scroll };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            return read(scroll, items);
        }
        finally
        {
            window.Close();
        }
    }
}
