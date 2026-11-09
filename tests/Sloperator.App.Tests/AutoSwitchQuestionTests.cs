using Sloperator.App.ViewModels;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>Auto's question over the message box before it moves up to a dearer model (the user, 2026-11-09).</summary>
public sealed class AutoSwitchQuestionTests
{
    [Fact]
    public void It_names_both_models_and_the_effort()
    {
        var question = new AutoSwitchQuestion("claude-opus-5-5", "xhigh", "claude-sonnet-5-5");
        Assert.Equal("Switch to claude-opus-5-5 at Extra high for this? It costs more than claude-sonnet-5-5.", question.Text);
        Assert.Equal("Stay on claude-sonnet-5-5", question.StayText);
        Assert.Equal("Switches in 10 s", question.Countdown);
    }

    [Fact]
    public async Task No_answer_takes_Autos_pick_once_the_wait_runs_out()
    {
        var question = new AutoSwitchQuestion("claude-opus-5-5", null, "claude-sonnet-5-5");
        question.Tick(TimeSpan.FromSeconds(2.5));
        Assert.Equal(("Switches in 8 s", 0.75), (question.Countdown, question.Left));
        Assert.False(question.Answer.IsCompleted);
        question.Tick(AutoSwitchQuestion.Wait);
        Assert.True(await question.Answer);
    }

    [Fact]
    public async Task Stay_keeps_the_model_and_Stop_ends_the_question()
    {
        var stay = new AutoSwitchQuestion("claude-opus-5-5", null, "claude-sonnet-5-5");
        stay.StayCommand.Execute(null);
        Assert.False(await stay.Answer);

        var stopped = new AutoSwitchQuestion("claude-opus-5-5", null, "claude-sonnet-5-5");
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.Answer);
    }
}
