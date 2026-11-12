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

        Assert.Equal("Worked for 24 s · 1 tool call · " + WorkFold.Unbroken("claude-sonnet-5-5 at High"), auto.Card!.Title); // held together for the wrap
        Assert.Equal(["read_file"], auto.Card.Tools);
        Assert.Equal("claude-haiku-5-5", new ToolCallItem("auto-3", "Auto", "claude-haiku-5-5: the evaluation failed") { State = ToolItemState.Error }.AutoPicked);
        Assert.Equal("claude-haiku-5-5 at Low", new ToolCallItem("auto-4", "Auto", "claude-haiku-5-5 at Low") { State = ToolItemState.Done }.AutoPicked);
        // Stopped while it picked: no pick to say.
        Assert.Null(new ToolCallItem("auto-2", "Auto", "claude-haiku-5-5 picks the model for this message…") { State = ToolItemState.Stopped }.AutoPicked);
    }

    [Fact]
    public void The_price_lookup_after_the_reply_folds_with_the_work_and_the_reply_stays()
    {
        // A long chat's answer is followed by Sloperator's own side task looking up the model's prices: no call
        // of the model's. Taken for one, the reply before it folded away with the work (2026-11-12).
        var auto = new ToolCallItem("auto-1", "Auto", "claude-haiku-5-5 at Low") { At = T0.AddSeconds(1), State = ToolItemState.Done };
        var reply = Step(4, "Start the stack from Tools > Test Backend.", "How the stack starts.");
        var lookup = new ToolCallItem(Sloperator.Core.Tools.SideTaskTool.AfterAnswerId + "3d86397e", "side_task", "Look up the prices of claude-haiku-5-5")
        { At = T0.AddSeconds(15), State = ToolItemState.Done, RunTook = TimeSpan.FromSeconds(25) };
        ChatItemViewModel[] chat = [Message("How do I start it?", 0), auto, reply, lookup];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        var fold = auto.Card!;
        // Nor does the card count it as one: "1 tool call" over a "side_task" chip, for a question answered in one step.
        Assert.Equal("Worked for 25 s · price lookup · " + WorkFold.Unbroken("claude-haiku-5-5 at Low"), fold.Title);
        Assert.Empty(fold.Tools);
        Assert.True(reply.IsShown);
        Assert.False(reply.ShowsThoughts);
        Assert.False(lookup.IsShown);
        Assert.False(lookup.InChat);
        Assert.False(lookup.ShowsRunTook); // the card says it

        fold.ToggleCommand.Execute(null);
        Assert.All(chat, l => Assert.True(l.IsShown));
        fold.ToggleCommand.Execute(null); // Hide steps
        Assert.True(reply.IsShown);
        Assert.False(lookup.IsShown);

        // The user wrote during the lookup: their message ends the stretch, and the reply still stays.
        var work = Call("mouse_click", 2);
        var checkedReply = Step(5, "I checked the new layout in the editor.");
        var lookedUp = new ToolCallItem(Sloperator.Core.Tools.SideTaskTool.AfterAnswerId + "08591956", "side_task", "Look up the prices of claude-sonnet-5-5")
        { At = T0.AddSeconds(6), State = ToolItemState.Done };
        ChatItemViewModel[] steered = [Message("Verify it", 0), Step(1), work, checkedReply, lookedUp, Message("By playing it", 30)];
        WorkFold.FoldFinished(steered, toTheEnd: false);
        Assert.True(checkedReply.IsShown);
        Assert.False(work.IsShown);
        Assert.False(lookedUp.IsShown);
        Assert.Equal("Worked for 29 s · 1 tool call · price lookup", work.Fold!.Title);
        Assert.Equal(["mouse_click"], work.Fold.Tools);

        // A side task the model called itself after its words is more work: stopped there, those words were no reply.
        var words = Step(3, "Now the prices.");
        var own = new ToolCallItem("toolu_01", "side_task", "Look up the prices") { At = T0.AddSeconds(4), State = ToolItemState.Stopped };
        var stop = new NoticeItem("Stopped with Pause/Break.", isError: false, carryOn: () => { }) { At = T0.AddSeconds(9), RunTook = TimeSpan.FromSeconds(9) };
        ChatItemViewModel[] called = [Message("Find the prices", 0), Call("read_file", 1), words, own, stop];
        WorkFold.FoldFinished(called, toTheEnd: false);
        Assert.False(words.IsShown);
        Assert.False(own.IsShown);
        Assert.True(stop.IsShown);
    }

    [Fact]
    public void The_plan_brought_up_to_date_after_the_answer_folds_with_the_work_unless_steps_are_left()
    {
        // With a plan in every task this line stood after the answer whenever the model did several steps in one go.
        var work = Call("edit_file", 2);
        var reply = Step(5, "Added the parameter.");
        var settled = new NoticeItem("The plan was left at step 2 of 4; asked the model where it stands: all done.", isError: false)
        { At = T0.AddSeconds(7), RunTook = TimeSpan.FromSeconds(7) };
        ChatItemViewModel[] chat = [Message("Add it", 0), Step(1), work, reply, settled];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.True(reply.IsShown);
        Assert.False(work.IsShown);
        Assert.False(settled.IsShown);
        Assert.Equal("Worked for 7.0 s · 1 tool call", reply.Fold!.Title);
        reply.Fold.ToggleCommand.Execute(null); // Show steps: it is there, after the reply
        Assert.True(settled.IsShown);

        // With steps left the line says why the bar isn't done: it stays in sight.
        var answer = Step(5, "Added the parameter.");
        var left = new NoticeItem("The plan was left at step 2 of 4; asked the model where it stands: it left it there.", isError: false)
        { At = T0.AddSeconds(7), RunTook = TimeSpan.FromSeconds(7) };
        ChatItemViewModel[] open = [Message("Add it", 0), Step(1), Call("edit_file", 2), answer, left];
        WorkFold.FoldFinished(open, toTheEnd: false);
        Assert.True(answer.IsShown);
        Assert.True(left.IsShown);
    }

    [Fact]
    public void An_answer_with_a_call_after_it_stays_when_the_last_step_said_nothing()
    {
        // The answer and a capture_result for the picture of the work in one message; the step after it said nothing
        // (an empty step leaves no line, a thinking one does). The answer was the last words, and folded with the call.
        var answer = Step(3, "The frames fit now; here is the chat.");
        var capture = Call("capture_result", 4);
        var last = Step(6, thoughts: "Done.");
        last.RunTook = TimeSpan.FromSeconds(8);
        ChatItemViewModel[] chat = [Message("Fix the frames", 0), Step(1, thoughts: "Look first."), Call("screenshot", 2), answer, capture, last];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.True(answer.IsShown);
        Assert.False(capture.IsShown);
        Assert.False(last.IsShown);
        Assert.Equal("Worked for 8.0 s · 2 tool calls", chat[1].Card!.Title);

        // The same in a stretch the user's message ended mid-run: words before a call are mid-work.
        var midWork = Step(3, "Let me click Save.");
        var click = Call("mouse_click", 4);
        ChatItemViewModel[] steered = [Message("Save it", 0), Call("screenshot", 1), midWork, click, Message("Wait", 5)];
        WorkFold.FoldFinished(steered, toTheEnd: false);
        Assert.False(midWork.IsShown);
    }

    [Fact]
    public void A_handoff_note_after_the_answer_folds_and_the_answer_stays()
    {
        // Compact context pressed while the model answered: the note comes after the answer, between its notices.
        var answer = Step(4, "Done: both buttons are in the scene.");
        var compacting = new NoticeItem("Compacting the context: asking the model for a handoff note, then starting fresh…", isError: false) { At = T0.AddSeconds(5) };
        var note = new AssistantMessageItem { At = T0.AddSeconds(6), Text = "Task: darken the title screen. Done so far: …", IsStreaming = false, IsHandoffNote = true };
        var reset = new NoticeItem("Context reset: about 90k tokens replaced by a handoff note; the context is now about 12k.", isError: false)
        { At = T0.AddSeconds(20), RunTook = TimeSpan.FromSeconds(20) };
        ChatItemViewModel[] chat = [Message("Darken it", 0), Call("edit_file", 2), answer, compacting, note, reset];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.True(answer.IsShown);
        Assert.False(note.IsShown);
        Assert.True(compacting.IsShown);
        Assert.True(reset.IsShown);

        // Compact context with no run going: the note is all there is, and it stays.
        var alone = new AssistantMessageItem { At = T0.AddSeconds(31), Text = "Task: …", Reasoning = "Sum it up.", IsStreaming = false, IsHandoffNote = true };
        var done = new NoticeItem("Context reset: …", isError: false) { At = T0.AddSeconds(40), RunTook = TimeSpan.FromSeconds(10) };
        ChatItemViewModel[] compacted = [new NoticeItem("Compacting the context: …", isError: false) { At = T0.AddSeconds(30) }, alone, done];
        WorkFold.FoldFinished(compacted, toTheEnd: false);
        Assert.True(alone.IsShown);
        Assert.False(alone.ShowsThoughts);
    }

    [Fact]
    public void A_handoff_note_is_kept_with_the_chat()
    {
        var note = new AssistantMessageItem { Text = "Task: …", IsStreaming = false, IsHandoffNote = true };
        var reply = new AssistantMessageItem { Text = "Done.", IsStreaming = false };

        Assert.True(MainWindowViewModel.ToEntry(note).Handoff);
        Assert.Null(MainWindowViewModel.ToEntry(reply).Handoff); // left out of the file
        Assert.True(MainWindowViewModel.ReplyFrom(MainWindowViewModel.ToEntry(note), new PreviewServices(null, () => null)).IsHandoffNote);
        Assert.False(MainWindowViewModel.ReplyFrom(MainWindowViewModel.ToEntry(reply), new PreviewServices(null, () => null)).IsHandoffNote);
    }

    [Fact]
    public void A_plain_answer_with_a_price_lookup_after_it_keeps_the_answer_and_folds_the_lookup_under_it()
    {
        var reply = Step(2, "Hello!");
        var lookup = new ToolCallItem(Sloperator.Core.Tools.SideTaskTool.AfterAnswerId + "1", "side_task", "Look up the prices of qwen3")
        { At = T0.AddSeconds(3), State = ToolItemState.Done, RunTook = TimeSpan.FromSeconds(12) };
        ChatItemViewModel[] chat = [Message("Hi", 0), reply, lookup];

        WorkFold.FoldFinished(chat, toTheEnd: false);

        Assert.True(reply.IsShown);
        Assert.Null(reply.Card);
        Assert.Equal("Worked for 12 s · price lookup", lookup.Card!.Title); // the card in the lookup's place, after the answer
        Assert.False(lookup.Card.HasTools);
        Assert.False(lookup.IsShown);
        Assert.True(lookup.InChat);
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
