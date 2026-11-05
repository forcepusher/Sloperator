using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.Views;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The plan bar: a word per point when they fit, those next to the blob when they don't.</summary>
public sealed class PlanBarTests
{
    private static AgentPlan Plan(int steps, int at) =>
        new([.. Enumerable.Range(1, steps).Select(i => new PlanStep("Step" + i, "What step " + i + " does"))], at);

    private static List<TextBlock> Shown(PlanBar bar) =>
        [.. bar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Opacity > 0)];

    private static T WithBar<T>(int width, AgentPlan plan, Func<PlanBar, Window, T> check)
    {
        var bar = new PlanBar { Plan = plan };
        var window = new Window { Width = width, Height = 200, Content = bar };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var result = check(bar, window);
        window.Close();
        return result;
    }

    [Fact]
    public void Six_steps_show_all_their_words() => HeadlessApp.Run(() => WithBar(860, Plan(6, 2), (bar, _) =>
    {
        Assert.Equal(6, Shown(bar).Count);
        return 0;
    }));

    [Fact]
    public void A_crowded_bar_shows_the_words_next_to_the_blob() => HeadlessApp.Run(() => WithBar(556, Plan(14, 5), (bar, _) =>
    {
        Assert.Equal(["Step5", "Step6", "Step7"], Shown(bar).Select(t => t.Text));
        return 0;
    }));

    [Fact]
    public void Moving_on_keeps_the_points_and_a_new_plan_replaces_them() => HeadlessApp.Run(() => WithBar(860, Plan(3, 0), (bar, _) =>
    {
        var first = bar.GetVisualDescendants().OfType<TextBlock>().First();
        bar.Plan = bar.Plan! with { At = 1 };
        Dispatcher.UIThread.RunJobs();
        Assert.Same(first, bar.GetVisualDescendants().OfType<TextBlock>().First());
        Assert.Contains("done", first.Classes);

        bar.Plan = Plan(4, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, bar.GetVisualDescendants().OfType<TextBlock>().Count());
        return 0;
    }));

    [Fact]
    public void The_step_shows_when_the_run_waits_or_failed() => HeadlessApp.Run(() => WithBar(860, Plan(3, 1), (bar, _) =>
    {
        var word = bar.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Step2");
        Assert.Contains("now", word.Classes);
        bar.Waiting = true;
        Assert.Contains("wait", word.Classes);
        bar.Waiting = false;
        bar.Failed = true;
        Assert.Contains("fail", word.Classes);
        Assert.DoesNotContain("now", word.Classes);
        return 0;
    }));

    [Fact]
    public void Every_point_says_what_its_step_does_on_hover() => HeadlessApp.Run(() => WithBar(860, Plan(3, 0), (bar, _) =>
    {
        var tips = bar.GetVisualDescendants().OfType<Control>().Select(ToolTip.GetTip).OfType<string>().ToList();
        Assert.Equal(["What step 1 does", "What step 2 does", "What step 3 does"], tips);
        return 0;
    }));
}
