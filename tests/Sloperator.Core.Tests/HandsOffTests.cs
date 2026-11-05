using System.Text.Json.Nodes;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// The user's own mouse and keyboard, which the agent shares with them: an action waits until they
/// leave them alone, locks them while it acts, and looks again when they changed the screen since
/// the model's screenshot.
/// </summary>
public class HandsOffTests
{
    private static (FakePlatform Platform, FakeUserInput User, ToolContext Ctx) Setup(bool screenshotAfter = false)
    {
        var user = new FakeUserInput();
        var platform = new FakePlatform { UserInput = user };
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = new AgentSettings { ScreenshotAfterActions = screenshotAfter },
            WorkingDirectory = Path.GetTempPath(),
        };
        return (platform, user, ctx);
    }

    /// <summary>The model is shown a screenshot and decides a reply on it, as the agent session sets it up.</summary>
    private static void Looked(ToolContext ctx)
    {
        ScreenshotTool.Take(ctx);
        ctx.Seen = ctx.LastShot;
        ctx.UserPressesSeen = ctx.LastShot!.UserPresses;
        ctx.ScreenStale = false;
    }

    [Fact]
    public async Task An_action_waits_until_the_user_leaves_the_mouse_and_keyboard_alone()
    {
        var (platform, user, ctx) = Setup();
        var progress = new List<string>();
        ctx.Progress = progress.Add;
        user.Busy = true;

        var acting = new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Empty(platform.Actions);
        Assert.Contains(progress, p => p.StartsWith("Waiting for you to leave the mouse and keyboard"));

        user.Busy = false;
        user.HoldingDown = true; // a key still held down holds it too
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(platform.Actions);

        user.HoldingDown = false;
        await acting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["keys enter"], platform.Actions);
    }

    [Fact]
    public async Task The_user_is_locked_out_while_it_acts_and_not_after()
    {
        var (platform, user, ctx) = Setup();
        var lockedAtClick = false;
        platform.OnClick = _ => lockedAtClick = user.Locked;

        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 20, ["y"] = 20 }, ctx, CancellationToken.None);

        Assert.True(lockedAtClick);
        Assert.False(user.Locked);
    }

    [Fact]
    public async Task Resting_on_Sloperators_window_is_not_leaving_the_mouse_alone()
    {
        var (platform, user, ctx) = Setup();
        var progress = new List<string>();
        ctx.Progress = progress.Add;
        user.Since = TimeSpan.FromSeconds(2); // past the second that does elsewhere
        user.Pointer = true; // on Stop, about to click it

        var acting = new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 20, ["y"] = 20 }, ctx, CancellationToken.None);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(platform.Actions);
        Assert.False(user.Locked);

        user.Pointer = false;
        await acting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("click Left x1", platform.Actions);
        // The line doesn't stay up all through the action it waited for.
        Assert.Equal("", progress[^1]);
    }

    [Fact]
    public async Task A_pointer_left_on_Sloperators_window_holds_nothing_up_for_long()
    {
        var (platform, user, ctx) = Setup();
        user.Since = TimeSpan.FromSeconds(6);
        user.Pointer = true;

        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 20, ["y"] = 20 }, ctx, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains("click Left x1", platform.Actions);
    }

    [Fact]
    public async Task Actions_that_only_press_keys_leave_the_mouse_free()
    {
        var (_, user, ctx) = Setup();

        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        Assert.False(user.LockedMouse);
        await new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = "John" }, ctx, CancellationToken.None);
        Assert.False(user.LockedMouse);
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 20, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.True(user.LockedMouse);
    }

    [Fact]
    public async Task Keys_wait_for_a_new_look_after_the_user_pressed_anything()
    {
        var (platform, user, ctx) = Setup();
        Looked(ctx);
        user.Press(); // a click in the same window moves the focus too

        var result = await new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = "John" }, ctx, CancellationToken.None);

        Assert.Empty(platform.Actions);
        Assert.StartsWith("Nothing was done: the user clicked, typed or scrolled since your last screenshot", result.Text);
        Assert.NotNull(result.Image);
        // The rest of the reply was decided on the same old screen.
        result = await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        Assert.Empty(platform.Actions);
        Assert.StartsWith("Nothing was done: an action before this one in your reply was held back", result.Text);

        // The next reply, decided on the screen it was shown, acts.
        Looked(ctx);
        await new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = "John" }, ctx, CancellationToken.None);
        Assert.Equal(["type John"], platform.Actions);
    }

    [Fact]
    public async Task A_click_goes_ahead_when_the_user_left_the_screen_where_it_lands_as_it_was()
    {
        var (platform, user, ctx) = Setup();
        platform.Desktop = Picture(200, 100);
        Looked(ctx);
        user.Press(); // somewhere else

        var result = await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 150, ["y"] = 50 }, ctx, CancellationToken.None);

        Assert.Equal("Clicked.", result.Text);
        Assert.Contains("click Left x1", platform.Actions);
        // A later action of the reply can't be compared with what the model saw (the click changes
        // the screen): it waits for a new look.
        result = await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 150, ["y"] = 80 }, ctx, CancellationToken.None);
        Assert.StartsWith("Nothing was done: the user used the mouse or keyboard since your last screenshot", result.Text);
        Assert.Single(platform.Actions, a => a.StartsWith("click"));
    }

    [Fact]
    public async Task A_click_waits_for_a_new_look_when_the_user_changed_the_screen_where_it_lands()
    {
        var (platform, user, ctx) = Setup();
        platform.Desktop = Picture(200, 100);
        Looked(ctx);
        user.Press();
        // A window the user opened, over where the model aims.
        Paint(platform.Desktop, 130, 30, 60, 40, 250);

        var result = await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 150, ["y"] = 50 }, ctx, CancellationToken.None);

        Assert.StartsWith("Nothing was done: the screen changed where you aimed", result.Text);
        Assert.NotNull(result.Image);
        Assert.DoesNotContain(platform.Actions, a => a.StartsWith("click"));
    }

    [Fact]
    public async Task Long_typing_stops_when_the_user_presses_something_meanwhile()
    {
        var (platform, user, ctx) = Setup();
        platform.OnType = _ => user.Press(); // a key of the user's, swallowed while the first piece was typed

        var result = await new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = new string('a', 450) }, ctx, CancellationToken.None);

        Assert.Equal($"Typed the first {TypeTextTool.Chunk} of 450 characters; the rest was not typed, as the user pressed a key or clicked meanwhile.", result.Text);
        Assert.Single(platform.Actions);
    }

    [Fact]
    public async Task The_run_watches_the_user_from_its_first_look_and_frames_the_screen_from_its_first_action()
    {
        var (platform, user, ctx) = Setup();
        var overlay = new FrameOverlay();
        platform.InputOverlay = overlay;

        await new ScreenshotTool().ExecuteAsync(new JsonObject(), ctx, CancellationToken.None);
        Assert.Equal(1, user.Watching);
        Assert.Empty(overlay.Frames);

        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        Assert.Equal(1, user.Watching);
        Assert.Equal(platform.ScreenBounds, overlay.Frames[^1]);

        await ctx.FinishRunAsync();
        Assert.Equal(0, user.Watching);
        Assert.Null(overlay.Frames[^1]);
    }

    private static RawImage Picture(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (40, 40, 40, 255);
        return new(width, height, bgra);
    }

    private static void Paint(RawImage image, int x, int y, int width, int height, byte value)
    {
        for (var row = y; row < y + height; row++)
            for (var col = x; col < x + width; col++)
            {
                var i = row * image.Stride + col * 4;
                (image.Bgra[i], image.Bgra[i + 1], image.Bgra[i + 2]) = (value, value, value);
            }
    }
}
