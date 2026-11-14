using System.Text.Json.Nodes;
using Sloperator.Core.Platform;

namespace Sloperator.Core.Tools;

/// <summary>Shared behaviour for tools that drive the mouse and keyboard.</summary>
public abstract class DesktopActionTool : Tool
{
    public override ToolRisk Risk => ToolRisk.Desktop;

    /// <summary>The longest an action waits for the user to finish typing a message to the agent.</summary>
    internal static TimeSpan MaxUserPause = TimeSpan.FromSeconds(60);

    /// <summary>The longest an action waits for another Sloperator window's agent to be done with the mouse and keyboard.</summary>
    internal static TimeSpan MaxTurnWait = TimeSpan.FromMinutes(5);

    /// <summary>How long the user leaves the mouse and keyboard alone before an action goes ahead.</summary>
    private static readonly TimeSpan HandsOffTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The same with the pointer on Sloperator's own window: resting on Stop for a second, before the
    /// click, is not leaving it alone, and the action took the pointer away from under the click.
    /// </summary>
    private static readonly TimeSpan HandsOffSloperatorTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Half the side of the square around a click (screen pixels) that is looked at again when the
    /// user pressed something since the model's screenshot: changed, the click waits for a new look.
    /// </summary>
    private const int AimRadius = 64;

    public sealed override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ScreenStale)
            return new ToolResult("Nothing was done: an action before this one in your reply was held back, as the screen may have changed " +
                                  "since you last saw it. Look at the screen as it is now and do it again if it still applies.");
        if (ctx.DesktopTurn is not { } desktop)
            return await ActAndLookAsync(args, ctx, screenChanged: false, ct).ConfigureAwait(false);
        // Another window's agent may be using the mouse and keyboard: one at a time.
        var turn = await desktop.TakeAsync(ctx, MaxTurnWait,
            waited => ctx.Progress?.Invoke($"Waiting for another Sloperator window to finish with the mouse and keyboard · {Chats.ChatTimes.Stopwatch(waited)}"), ct).ConfigureAwait(false);
        if (turn is null)
            return new ToolResult($"Nothing was done: another Sloperator window's agent has been using the mouse and keyboard for {MaxTurnWait.TotalMinutes:0} minutes. " +
                                  "Do what doesn't need them meanwhile (files, commands), or try again.");
        ctx.AfterRun("desktop turn", () =>
        {
            desktop.Release(ctx);
            return Task.CompletedTask;
        });
        // Not handed to another window while the action goes on, however long it waits for the user's typing.
        var acted = turn == false;
        try { return await ActAndLookAsync(args, ctx, screenChanged: turn == true, ct).ConfigureAwait(false); }
        // Arguments it couldn't use: nothing was pressed, for the other windows' agents to look again for.
        catch (ToolArgumentException)
        {
            acted = false;
            throw;
        }
        finally { desktop.Done(ctx, acted); }
    }

    private async Task<ToolResult> ActAndLookAsync(JsonObject args, ToolContext ctx, bool screenChanged, CancellationToken ct)
    {
        WatchUser(ctx, acting: true);
        if (screenChanged)
        {
            // What the model aimed at may have moved, been covered or closed meanwhile; and the
            // rest of its reply was decided on the same old screen.
            ctx.Seen = null;
            ctx.ScreenStale = true;
            var now = ScreenshotTool.Take(ctx);
            return now with
            {
                Text = "Nothing was done: another Sloperator window's agent has used the mouse and keyboard, so the screen may have changed since you last saw it. " +
                       "This is the screen now; do it again if it still applies.\n" + now.Text,
            };
        }

        // The user is typing to the agent in its window: a click or key press now would take the
        // focus from under their typing, or their keys would land in the agent's window.
        var paused = System.Diagnostics.Stopwatch.StartNew();
        while (ctx.UserComposing?.Invoke() == true && paused.Elapsed < MaxUserPause)
            await Task.Delay(200, ct).ConfigureAwait(false);
        await WaitForHandsOffAsync(ctx, ct).ConfigureAwait(false);
        string text;
        ClickCheck? check;
        // Nothing the user does gets in the way of the action, nor between its looking again and acting.
        using (var locked = ctx.Platform.UserInput.Lock(UsesMouse))
        {
            if (UserChangedScreen(args, ctx) is { } why)
            {
                locked.Dispose();
                // What it aimed at may have moved, and the rest of the reply was decided on the same old screen.
                ctx.Seen = null;
                ctx.ScreenStale = true;
                var now = ScreenshotTool.Take(ctx);
                return now with { Text = $"Nothing was done: {why}. This is the screen now; do it again if it still applies.\n" + now.Text };
            }
            ctx.UserPressesAtAct = ctx.Platform.UserInput.Presses;
            (text, check) = await ActAsync(args, ctx, ct).ConfigureAwait(false);
            if (SetsFocus)
                ctx.ClickedAtPresses = ctx.UserPressesAtAct;
        }
        // The screen now changes in ways the model hasn't seen; a later click in the same reply
        // is aimed at what it expects, not at what it saw.
        ctx.Seen = null;
        if (!ctx.Settings.ScreenshotAfterActions)
            return new ToolResult(text);

        // Give the UI a moment to react before looking at it.
        await Task.Delay(500, ct).ConfigureAwait(false);
        // A click on what is already selected changes nothing either: Paint's oval tool, picked by
        // one of the first clicks, was clicked some twenty times more, each "probably missed".
        if (check is not null && await check.MissedAsync(ctx, ct).ConfigureAwait(false))
            text += "\n" + ClickCheck.MissedNote + ", so it probably missed. Zoom in on what you meant and click it again, aiming at its middle. " +
                    "Stop without clicking again only if the zoom shows it is already selected or switched on (a tool, a tab, a checkbox), " +
                    "which a click doesn't change, and say so in your reply. If it only needs more time to respond, wait instead; " +
                    "if you clicked an empty spot only to focus a window, ignore this.";
        var shot = ScreenshotTool.Take(ctx);
        return shot with { Text = text + "\n" + shot.Text };
    }

    /// <summary>
    /// What an action did; for a click, also the screen just before it, to tell a miss by (see <see cref="ClickCheck"/>).
    /// </summary>
    private protected readonly record struct Acted(string Text, ClickCheck? Check = null)
    {
        public static implicit operator Acted(string text) => new(text);
    }

    private protected abstract Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct);

    /// <summary>Whether the action moves the mouse: one that doesn't leaves the user's free, however long it takes.</summary>
    private protected virtual bool UsesMouse => true;

    /// <summary>Whether the action puts the keyboard focus where it lands, as a click does (see <see cref="ToolContext.ClickedAtPresses"/>).</summary>
    private protected virtual bool SetsFocus => false;

    /// <summary>
    /// Where on the screen the action lands, to look at again when the user pressed something since
    /// the model's screenshot; null for keys, which go wherever the focus is now.
    /// </summary>
    private protected virtual (int X, int Y)[]? Aims(JsonObject args, ToolContext ctx) => null;

    /// <summary>
    /// From a run's first look at the screen, the user's input is watched; from its first action, a
    /// frame round the screen tells the user an agent is at work there. Both until the run ends.
    /// </summary>
    internal static void WatchUser(ToolContext ctx, bool acting)
    {
        var owner = ctx.DesktopSeat ?? ctx;
        if (ctx.UserWatch is null)
        {
            ctx.UserWatch = ctx.Platform.UserInput.Watch();
            ctx.AfterRun("user input", () =>
            {
                ctx.UserWatch.Dispose();
                ctx.Platform.InputOverlay.Frame(owner, null);
                return Task.CompletedTask;
            });
        }
        if (acting)
            ctx.Platform.InputOverlay.Frame(owner, ctx.GetScreenSpace().Screen);
    }

    /// <summary>
    /// Waits until the user has left the mouse and keyboard alone for <see cref="HandsOffTime"/>,
    /// holding nothing down, for as long as it takes: an action in the middle of what they are doing
    /// took the focus from under their typing, or their mouse from under their hand.
    /// </summary>
    private static async Task WaitForHandsOffAsync(ToolContext ctx, CancellationToken ct)
    {
        var user = ctx.Platform.UserInput;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var said = false;
        while (user.Holding || user.SinceLastInput < (user.OnSloperator ? HandsOffSloperatorTime : HandsOffTime))
        {
            ctx.Progress?.Invoke($"Waiting for you to leave the mouse and keyboard · {Chats.ChatTimes.Stopwatch(waited.Elapsed)}");
            said = true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        // The action goes ahead now: the line stayed up all through it.
        if (said)
            ctx.Progress?.Invoke("");
    }

    /// <summary>
    /// What the user did since the screenshot this reply was decided on that the action must not go
    /// ahead on, in words for the model; null when nothing. Keys wait for a new look after any press
    /// of the user's: a click in the same window moves the focus too. A click waits only when the screen
    /// changed around where it lands, and can't be compared but for the reply's first action.
    /// Keys right after a click of the same reply go ahead when the user pressed nothing since that click,
    /// which put the focus where the model aimed: the user wrote to the agent, its next reply triple-clicked a
    /// search box and typed, and the typing was held back as "the focus may have moved" (2026-11-12); it
    /// clicked and typed again, into the text the first click had selected no more.
    /// </summary>
    private string? UserChangedScreen(JsonObject args, ToolContext ctx)
    {
        if (ctx.UserPressesSeen is not { } seen || ctx.Platform.UserInput.Presses == seen)
            return null;
        if (Aims(args, ctx) is not { } aims)
            return ctx.ClickedAtPresses == ctx.Platform.UserInput.Presses
                ? null
                : "the user clicked, typed or scrolled since your last screenshot, so the focus may have moved";
        if (ctx.Seen is not { } look)
            return "the user used the mouse or keyboard since your last screenshot";
        var now = ctx.Platform.Screen.Capture(look.Screen, includeCursor: false);
        return aims.Any(p => ClickCheck.ChangedAround(look, now, p, AimRadius))
            ? "the screen changed where you aimed, as the user used the mouse or keyboard since your last screenshot"
            : null;
    }

    /// <summary>Whether the user pressed something since the action started: one that takes a while stops there.</summary>
    private protected static bool UserCameIn(ToolContext ctx) => ctx.Platform.UserInput.Presses != ctx.UserPressesAtAct;

    /// <summary>Waits <paramref name="time"/>, or less when the user presses something meanwhile; returns whether they did.</summary>
    private protected static async Task<bool> PauseAsync(TimeSpan time, ToolContext ctx, CancellationToken ct)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < time)
        {
            if (UserCameIn(ctx))
                return true;
            var left = time - waited.Elapsed;
            await Task.Delay(left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
        return UserCameIn(ctx);
    }

    /// <summary>How an action that stopped part way for the user says so.</summary>
    private protected const string UserStopped = "as the user pressed a key or clicked meanwhile";

    /// <summary>
    /// The wait <see cref="ExecuteAsync"/> makes before an action, made again inside one that takes
    /// a while (a series of presses, long text):
    /// typing to the agent that starts meanwhile must hold the rest too. Returns whether it waited:
    /// the keyboard is then in Sloperator's window, and keys meant for another need it given back.
    /// </summary>
    private protected static async Task<bool> WaitWhileUserTypesAsync(ToolContext ctx, CancellationToken ct)
    {
        if (ctx.UserComposing?.Invoke() != true)
            return false;
        var paused = System.Diagnostics.Stopwatch.StartNew();
        while (ctx.UserComposing() && paused.Elapsed < MaxUserPause)
            await Task.Delay(200, ct).ConfigureAwait(false);
        return true;
    }

    protected static (int X, int Y) Point(JsonObject args, ToolContext ctx, string xKey = "x", string yKey = "y") =>
        ctx.GetScreenSpace().ToScreen(RequireNumber(args, xKey), RequireNumber(args, yKey));

    /// <summary>Moves Sloperator's own window out of the way if the action lands on it.</summary>
    protected static IDisposable Aside(ToolContext ctx, params (int X, int Y)[] points) =>
        ctx.Platform.HostWindow.StepAside(points);

    // Invariant: with a German PC's culture an x of 512.5 read "(512,5, 300)", to the model as well.
    protected static string Fmt(JsonObject args, string xKey = "x", string yKey = "y") =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({GetNumber(args, xKey)}, {GetNumber(args, yKey)})");

    /// <summary>
    /// " at (512, 300)" for a card's line, with another <paramref name="word"/> where it fits; nothing when a
    /// number is missing: a call the model sent without them, refused for it, read "Double-click at (, )".
    /// </summary>
    protected static string At(JsonObject args, string word = "at", string xKey = "x", string yKey = "y") =>
        GetNumber(args, xKey) is not null && GetNumber(args, yKey) is not null ? $" {word} {Fmt(args, xKey, yKey)}" : "";

    /// <summary>The schema of the keys a click or drag can hold, as a person does to select several things.</summary>
    protected static JsonObject ModifiersParameter() =>
        StrArray("Keys to hold down during it, e.g. [\"ctrl\"] to add to a selection, [\"shift\"] to select a range");

    /// <summary>The keys in modifiers ("ctrl", "shift+alt"…); an unknown name is an argument error, found before anything moves.</summary>
    protected static List<string> Modifiers(JsonObject args, ToolContext? ctx = null)
    {
        var keys = (GetStringList(args, "modifiers") ?? []).SelectMany(k => k.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (ctx is not null && keys.Where(k => !ctx.Platform.Input.IsKnownKey(k)).ToList() is { Count: > 0 } unknown)
            throw new ToolArgumentException($"Unknown key name(s) in modifiers: {string.Join(", ", unknown)}.");
        return keys;
    }

    /// <summary>Holds the keys until disposed; with none, nothing is pressed.</summary>
    protected static IDisposable? Hold(ToolContext ctx, List<string> keys) => keys.Count > 0 ? ctx.Platform.Input.HoldKeys(keys) : null;

    /// <summary>"Ctrl+Shift" for a description.</summary>
    protected static string Held(List<string> keys) => string.Join("+", keys.Select(OverlayInputController.KeyName));
}

public sealed class ScreenshotTool : Tool
{
    public override string Name => "screenshot";
    public override string Description =>
        "Capture a screen. Returns an image; the mouse tools' x/y point into it. " +
        "With several monitors, pass screen to look at another one; from then on x/y refer to that screen.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("screen", Int("Which screen to capture: 1 = primary, 2 = the next one... Default: the screen shown last."), false));
    public override string Describe(JsonObject args) =>
        GetNumber(args, "screen") is { } n ? $"Take a screenshot of screen {n}" : "Take a screenshot";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (GetNumber(args, "screen") is { } n)
        {
            var count = ctx.Screens.Count;
            if (n < 1 || n > count)
                throw new ToolArgumentException(count == 1 ? "There is only one screen." : $"There are {count} screens, numbered 1 to {count}.");
            ctx.CurrentScreen = (int)n - 1;
        }
        return Task.FromResult(Take(ctx));
    }

    internal static ToolResult Take(ToolContext ctx)
    {
        DesktopActionTool.WatchUser(ctx, acting: false);
        var space = ctx.GetScreenSpace();
        var presses = ctx.Platform.UserInput.Presses;
        var cursor = ctx.Platform.Input.GetCursorPosition();
        var frame = ctx.Platform.Screen.Capture(space.Screen, includeCursor: true);
        ctx.LastShot = new(space.Screen, frame, cursor, presses);
        ctx.DesktopTurn?.Looked(ctx);
        var jpeg = space.Encode(frame, ctx.Settings.ScreenshotQuality);
        var count = ctx.Screens.Count;
        var which = (count > 1 ? $"Screenshot of screen {ctx.CurrentScreen + 1} of {count}, " : "Screenshot ") + $"{space.ImageWidth}x{space.ImageHeight}.";
        var cursorScreen = ctx.ScreenAt(cursor.X, cursor.Y);
        var (cx, cy) = space.ToModel(cursor.X, cursor.Y);
        var where = cursorScreen == ctx.CurrentScreen ? $"Mouse cursor at ({cx}, {cy})." : $"Mouse cursor is on screen {cursorScreen + 1}.";
        return new ToolResult(which + " " + where, jpeg);
    }
}

public sealed class MouseClickTool : DesktopActionTool
{
    public override string Name => "mouse_click";
    public override string Description =>
        "Click at (x, y) in screenshot coordinates. When something is small or close to things like it (a row of a list, a menu item, a tab), " +
        "zoom in on it first and aim from the zoomed picture.";
    public override JsonObject Parameters => Schema(
        ("x", Num("X in screenshot coordinates"), true),
        ("y", Num("Y in screenshot coordinates"), true),
        ("button", Str("Mouse button, default left", "left", "right", "middle"), false),
        ("clicks", Int("1 = single click (default), 2 = double click, 3 = triple click"), false),
        ("modifiers", ModifiersParameter(), false));

    public override string Describe(JsonObject args)
    {
        var clicks = (int)(GetNumber(args, "clicks") ?? 1);
        var kind = clicks switch { 2 => "Double-click", 3 => "Triple-click", _ => "Click" };
        if (Modifiers(args) is { Count: > 0 } held)
            kind = Held(held) + "+" + kind.ToLowerInvariant();
        var button = GetString(args, "button") is { } b && b != "left" ? $" ({b})" : "";
        return $"{kind}{button}{At(args)}";
    }

    private protected override (int X, int Y)[] Aims(JsonObject args, ToolContext ctx) => [Point(args, ctx)];

    private protected override bool SetsFocus => true;

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var modifiers = Modifiers(args, ctx);
        var point = Point(args, ctx);
        var button = ParseButton(GetString(args, "button"));
        var clicks = Math.Clamp((int)(GetNumber(args, "clicks") ?? 1), 1, 3);
        var check = ClickCheck.Enabled && ctx.Settings.ScreenshotAfterActions ? ClickCheck.Start(ctx) : null;
        using var aside = Aside(ctx, point);
        ctx.Platform.Input.MoveMouse(point.X, point.Y);
        using (Hold(ctx, modifiers))
        {
            await Task.Delay(60, ct).ConfigureAwait(false);
            ctx.Platform.Input.Click(button, clicks);
        }
        return new(clicks switch { 2 => "Double-clicked.", 3 => "Triple-clicked.", _ => "Clicked." }, check);
    }

    internal static MouseButton ParseButton(string? s) => s?.ToLowerInvariant() switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };
}

public sealed class MouseMoveTool : DesktopActionTool
{
    public override string Name => "mouse_move";
    public override string Description => "Move the mouse to (x, y) in screenshot coordinates without clicking (e.g. to hover).";
    public override JsonObject Parameters => Schema(("x", Num("X in screenshot coordinates"), true), ("y", Num("Y in screenshot coordinates"), true));
    public override string Describe(JsonObject args) => $"Move mouse{At(args, "to")}";

    private protected override (int X, int Y)[] Aims(JsonObject args, ToolContext ctx) => [Point(args, ctx)];

    private protected override Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var (x, y) = Point(args, ctx);
        using var aside = Aside(ctx, (x, y));
        ctx.Platform.Input.MoveMouse(x, y);
        return Task.FromResult<Acted>("Moved the mouse.");
    }
}

public sealed class MouseDragTool : DesktopActionTool
{
    public override string Name => "mouse_drag";
    public override string Description => "Press the left button at (x, y), drag to (to_x, to_y) and release. Screenshot coordinates.";
    public override JsonObject Parameters => Schema(
        ("x", Num("Start X"), true), ("y", Num("Start Y"), true),
        ("to_x", Num("End X"), true), ("to_y", Num("End Y"), true),
        ("modifiers", ModifiersParameter(), false));
    public override string Describe(JsonObject args) =>
        $"Drag{At(args, "from")}{At(args, "to", "to_x", "to_y")}" + (Modifiers(args) is { Count: > 0 } held ? $" holding {Held(held)}" : "");

    private protected override (int X, int Y)[] Aims(JsonObject args, ToolContext ctx) => [Point(args, ctx), Point(args, ctx, "to_x", "to_y")];

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var modifiers = Modifiers(args, ctx);
        var (x1, y1) = Point(args, ctx);
        var (x2, y2) = Point(args, ctx, "to_x", "to_y");
        var input = ctx.Platform.Input;
        using var aside = Aside(ctx, (x1, y1), (x2, y2));
        input.MoveMouse(x1, y1);
        using var held = Hold(ctx, modifiers);
        await Task.Delay(80, ct).ConfigureAwait(false);
        input.MouseDown(MouseButton.Left);
        try
        {
            // Move in steps so apps that track drag motion see it.
            const int steps = 12;
            for (var i = 1; i <= steps; i++)
            {
                input.MoveMouse(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
                await Task.Delay(15, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            input.MouseUp(MouseButton.Left);
        }
        // In screen pixels, as apps measure what was drawn or selected.
        return $"Dragged {Math.Abs(x2 - x1)} x {Math.Abs(y2 - y1)} px on screen.";
    }
}

public sealed class ScrollTool : DesktopActionTool
{
    public override string Name => "scroll";
    public override string Description => "Scroll the mouse wheel, optionally after moving to (x, y). One unit is one wheel notch (about 3 lines).";
    public override JsonObject Parameters => Schema(
        ("direction", Str("Scroll direction", "up", "down", "left", "right"), true),
        ("amount", Int("Number of wheel notches, default 3"), false),
        ("x", Num("Optional X to scroll at"), false),
        ("y", Num("Optional Y to scroll at"), false));
    public override string Describe(JsonObject args) =>
        $"Scroll {GetString(args, "direction")} x{GetNumber(args, "amount") ?? 3}" + At(args);

    private protected override (int X, int Y)[] Aims(JsonObject args, ToolContext ctx) =>
        [args["x"] is not null && args["y"] is not null ? Point(args, ctx) : ctx.Platform.Input.GetCursorPosition()];

    private protected override Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var at = ctx.Platform.Input.GetCursorPosition();
        if (args["x"] is not null && args["y"] is not null)
        {
            at = Point(args, ctx);
            ctx.Platform.Input.MoveMouse(at.X, at.Y);
        }
        using var aside = Aside(ctx, at);
        var amount = Math.Clamp((int)(GetNumber(args, "amount") ?? 3), 1, 50);
        var (dx, dy) = RequireString(args, "direction").ToLowerInvariant() switch
        {
            "up" => (0, -amount),
            "down" => (0, amount),
            "left" => (-amount, 0),
            "right" => (amount, 0),
            var d => throw new ToolArgumentException($"Unknown direction '{d}'."),
        };
        ctx.Platform.Input.Scroll(dx, dy);
        return Task.FromResult<Acted>(Describe(args) + " done.");
    }
}

public sealed partial class TypeTextTool : DesktopActionTool
{
    public override string Name => "type_text";
    // Not "use \n for Enter": read as the two characters, Gemma 4 typed "gemma.txt\n" into a
    // Save As dialog's file name, backslash and all.
    public override string Description =>
        "Type text into the focused control, exactly as given; a line break in the text presses Enter. Click the target field first. " +
        "Into a Save As or Open dialog's file name box, type the whole path (C:\\folder\\name.txt), whatever folder the dialog shows.";
    public override JsonObject Parameters => Schema(("text", Str("Text to type"), true));
    public override string Describe(JsonObject args) => $"Type \"{(GetString(args, "text") ?? "").Clip(60)}\"";

    /// <summary>How much is typed at a time (about 0.2 s) before checking that the user hasn't started typing to the agent.</summary>
    internal const int Chunk = 200;

    private protected override bool UsesMouse => false;

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var text = EnterForEscapedBreaks(RequireString(args, "text"));
        ctx.Platform.HostWindow.YieldFocus();
        // Long text takes seconds: typing to the agent that starts meanwhile holds the rest, which
        // then goes back to the window it was meant for, not into the user's message.
        for (var at = 0; at < text.Length; at += Chunk)
        {
            // Stop ends long text between pieces: it went on typing the rest, Enter for every
            // line break, into whatever window had the focus.
            ct.ThrowIfCancellationRequested();
            // The user's keys and clicks go nowhere while it types, but they want the keyboard back.
            if (at > 0 && UserCameIn(ctx))
                return $"Typed the first {at} of {text.Length} characters; the rest was not typed, {UserStopped}.";
            if (at > 0 && await WaitWhileUserTypesAsync(ctx, ct).ConfigureAwait(false))
                ctx.Platform.HostWindow.YieldFocus();
            ctx.Platform.Input.TypeText(text.Substring(at, Math.Min(Chunk, text.Length - at)));
        }
        return $"Typed {text.Length} characters.";
    }

    /// <summary>
    /// A "\n" written out as a backslash and an n at the very end of the text means Enter
    /// ("Hello from Gemma\n"), when nothing else in it is a backslash or a line break: code
    /// (printf("hi\n");) and paths are typed as written.
    /// </summary>
    internal static string EnterForEscapedBreaks(string text) =>
        EscapedBreaksAtEnd().Match(text) is { Success: true } m && !text.AsSpan(0, m.Index).ContainsAny('\\', '\n')
            ? text[..m.Index] + new string('\n', m.Length / 2)
            : text;

    [System.Text.RegularExpressions.GeneratedRegex(@"(\\n)+$")]
    private static partial System.Text.RegularExpressions.Regex EscapedBreaksAtEnd();
}

public sealed class PressKeysTool : DesktopActionTool
{
    /// <summary>Pause between the presses of a sequence, so apps (and game animations) keep up.</summary>
    private const int DefaultGapMs = 150;

    /// <summary>Longest a press may be held.</summary>
    private const int MaxHoldSeconds = 60;

    private protected override bool UsesMouse => false;

    public override string Name => "press_keys";
    public override string Description =>
        "Press a key or shortcut, or a whole series of them in one go. For one chord give its keys in order, e.g. [\"ctrl\",\"c\"], [\"alt\",\"tab\"], [\"enter\"]. " +
        "For several presses one after another, give sequence instead, e.g. [\"left\",\"down\",\"left\",\"tab\",\"ctrl+s\"]: much faster than one call per key. " +
        "A sequence can also time its steps: \"wait 0.5\" pauses that many seconds, \"hold w\" keeps w down while the next steps run until \"release w\" " +
        "(what is still held is let go at the end), e.g. [\"hold w\",\"wait 2\",\"space\",\"wait 1\",\"release w\"] to walk, jump and walk on; " +
        "\"type Reports\" types that text, e.g. [\"ctrl+a\",\"type Reports\",\"enter\"] to rename. " +
        "Names: ctrl, alt, shift, win, enter, esc, tab, backspace, delete, space, up, down, left, right, home, end, pageup, pagedown, f1-f12, a-z, 0-9.";
    public override JsonObject Parameters => Schema(
        ("keys", StrArray("Keys of one chord, modifiers first"), false),
        ("sequence", StrArray("Steps one after another, instead of keys: a key, a chord like ctrl+s, \"wait 1.5\", \"hold shift\", \"release shift\" or \"type some text\""), false),
        ("repeat", Int("How many times to press the chord or run the sequence, default 1"), false),
        ("gap_ms", Int($"Pause between steps in milliseconds, default {DefaultGapMs}; none next to a wait step"), false),
        ("hold_seconds", Num($"Keep each press down this long (walking in a game, a long press), e.g. [\"w\"] for 2 seconds to walk forward (max {MaxHoldSeconds}); default: a quick press"), false));

    public override string Describe(JsonObject args)
    {
        var repeat = GetNumber(args, "repeat") is > 1 and var r ? $" x{r}" : "";
        var hold = GetNumber(args, "hold_seconds") is > 0 and var h ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $", held {h} s") : "";
        var sequence = (GetStringList(args, "sequence") ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return sequence.Count > 0
            ? $"Keys: {string.Join(", ", sequence)}{repeat}{hold}"
            : $"Press {string.Join("+", Keys(args))}{repeat}{hold}";
    }

    private static List<string> Keys(JsonObject args)
    {
        var keys = GetStringList(args, "keys") ?? [];
        // Accept "ctrl+c" as a single string as well.
        return keys.SelectMany(Split).ToList();
    }

    private enum StepKind { Press, Wait, Hold, Release, Type }

    /// <summary>One step of a sequence: a press, a wait, keys held down or let go, or text typed.</summary>
    private sealed record Step(StepKind Kind, List<string> Keys, double Seconds = 0, string Text = "");

    private static List<Step> Steps(JsonObject args) =>
        (GetStringList(args, "sequence") ?? []).Select(ParseStep).Where(s => s.Kind is StepKind.Wait or StepKind.Type || s.Keys.Count > 0).ToList();

    private static Step ParseStep(string text)
    {
        var parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // "down" alone is the arrow key; "hold"/"release" don't name keys, so they can't be read as one.
        if (parts.Length == 2)
            switch (parts[0].ToLowerInvariant())
            {
                case "wait":
                    return double.TryParse(parts[1].TrimEnd('s', 'S'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
                        ? new Step(StepKind.Wait, [], seconds)
                        : throw new ToolArgumentException($"\"{text.Trim()}\": a wait step takes seconds, e.g. \"wait 0.5\".");
                case "hold":
                    return new Step(StepKind.Hold, Split(parts[1]).ToList());
                case "release":
                    return new Step(StepKind.Release, Split(parts[1]).ToList());
                // Haiku 5.5 renamed a new folder with ["wait 1","ctrl+a","type Reports","enter"], which failed as an
                // unknown key, and took another call to type it (2026-11-10).
                case "type":
                    return new Step(StepKind.Type, [], Text: text.TrimStart()[5..]);
            }
        return new Step(StepKind.Press, Split(text).ToList());
    }

    private static string[] Split(string chord) =>
        chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private protected override async Task<Acted> ActAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var steps = Steps(args);
        if (steps.Count == 0 && Keys(args) is { Count: > 0 } chord)
            steps = [new Step(StepKind.Press, chord)];
        if (steps.Count == 0)
            throw new ToolArgumentException("Give 'keys' (one chord) or 'sequence' (several presses).");
        var unknown = steps.SelectMany(s => s.Keys).Where(k => !ctx.Platform.Input.IsKnownKey(k)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new ToolArgumentException($"Unknown key name(s): {string.Join(", ", unknown)}.");

        var repeat = Math.Clamp((int)(GetNumber(args, "repeat") ?? 1), 1, 100);
        var gap = Math.Clamp((int)(GetNumber(args, "gap_ms") ?? (steps.Count > 1 ? DefaultGapMs : 40)), 0, 5000);
        var hold = TimeSpan.FromSeconds(Math.Clamp(GetNumber(args, "hold_seconds") ?? 0, 0, MaxHoldSeconds));
        // Keys a hold step put down, one by one so a release step can let go of any of them.
        var held = new List<(string Key, IDisposable Up)>();
        ctx.Platform.HostWindow.YieldFocus();
        var total = repeat * steps.Count;
        try
        {
            for (var i = 0; i < total; i++)
            {
                // The user's keys and clicks go nowhere while it presses, but they want the keyboard back.
                if (i > 0 && UserCameIn(ctx))
                    return $"Did {i} of {total} steps; the rest was not done, {UserStopped}.";
                // A series takes a while: typing to the agent that starts meanwhile holds the rest, which
                // then goes back to the window it was meant for. It went into the user's message.
                if (i > 0 && await WaitWhileUserTypesAsync(ctx, ct).ConfigureAwait(false))
                    ctx.Platform.HostWindow.YieldFocus();
                var step = steps[i % steps.Count];
                switch (step.Kind)
                {
                    case StepKind.Wait:
                        if (await PauseAsync(TimeSpan.FromSeconds(step.Seconds), ctx, ct).ConfigureAwait(false))
                            return $"Did {i + 1} of {total} steps, the last cut short; the rest was not done, {UserStopped}.";
                        break;
                    case StepKind.Hold:
                        foreach (var key in step.Keys.Where(k => !held.Exists(h => Same(h.Key, k))))
                            held.Add((key, ctx.Platform.Input.HoldKeys([key])));
                        break;
                    case StepKind.Release:
                        foreach (var key in Enumerable.Reverse(step.Keys))
                            if (held.FindLastIndex(h => Same(h.Key, key)) is >= 0 and var at)
                            {
                                held[at].Up.Dispose();
                                held.RemoveAt(at);
                            }
                        break;
                    case StepKind.Type:
                        ctx.Platform.Input.TypeText(TypeTextTool.EnterForEscapedBreaks(step.Text));
                        break;
                    case StepKind.Press when hold > TimeSpan.Zero:
                        // Released on Stop too, so no key is left held down.
                        using (ctx.Platform.Input.HoldKeys(step.Keys))
                            if (await PauseAsync(hold, ctx, ct).ConfigureAwait(false))
                                return $"Did {i + 1} of {total} steps, the last let go early; the rest was not done, {UserStopped}.";
                        break;
                    default:
                        ctx.Platform.Input.PressKeys(step.Keys);
                        break;
                }
                // A wait step is the pause there.
                if (i < total - 1 && step.Kind != StepKind.Wait && steps[(i + 1) % steps.Count].Kind != StepKind.Wait)
                    await PauseAsync(TimeSpan.FromMilliseconds(gap), ctx, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Nothing is left held down: not at the end, not when the user comes in, not on Stop.
            for (var h = held.Count - 1; h >= 0; h--)
                held[h].Up.Dispose();
        }
        return Describe(args) + " done.";
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What <see cref="WaitTool"/> and <see cref="WaitUntilChangeTool"/> share.</summary>
public abstract class WaitToolBase : Tool
{
    public override ToolRisk Risk => ToolRisk.ReadOnly;

    /// <summary>
    /// Runs <paramref name="wait"/> and gives its words, or how long it went on before the user ended
    /// it, with a message or <see cref="ToolContext.EndWait"/>: a ten-minute watch shouldn't keep them waiting.
    /// </summary>
    protected static async Task<string> UntilEndedAsync(Func<CancellationToken, Task<string>> wait, ToolContext ctx, CancellationToken ct)
    {
        using var ended = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.UserMessage, ended.Token);
        ctx.Waiting = ended;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { return await wait(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && ctx.UserMessage.IsCancellationRequested)
        {
            return $"The wait ended after {clock.Elapsed.TotalSeconds:0.#}s: the user sent you a message, which follows.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && ended.IsCancellationRequested)
        {
            return $"The wait ended after {clock.Elapsed.TotalSeconds:0.#}s: the user ended it early.";
        }
        finally { ctx.Waiting = null; }
    }

    protected static ToolResult WithScreenshot(string waited, ToolContext ctx)
    {
        var shot = ScreenshotTool.Take(ctx);
        return shot with { Text = $"{waited}\n{shot.Text}" };
    }
}

/// <summary>Waits a set time, as for a build or a download; a screenshot at the end only when asked.</summary>
public sealed class WaitTool : WaitToolBase
{
    private const int MaxSeconds = 600;

    public override string Name => "wait";
    public override string Description => $"Wait a set time, e.g. for a build or a download. Max {MaxSeconds} seconds.";
    public override JsonObject Parameters => Schema(
        ("seconds", Num($"Seconds to wait, at most {MaxSeconds}"), true),
        ("screenshot_on_end", Bool("Take a screenshot when the wait ends (default false)"), false));
    public override string Describe(JsonObject args) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Wait {GetNumber(args, "seconds")} s");

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var seconds = TimeSpan.FromSeconds(Math.Clamp(GetNumber(args, "seconds") ?? 1, 0.1, MaxSeconds));
        var waited = await UntilEndedAsync(async token =>
        {
            await Task.Delay(seconds, token).ConfigureAwait(false);
            return $"Waited {seconds.TotalSeconds:0.#}s.";
        }, ctx, ct).ConfigureAwait(false);
        return GetBool(args, "screenshot_on_end") ? WithScreenshot(waited, ctx) : new ToolResult(waited);
    }
}

/// <summary>
/// Waits until the screen changes from how it was when the wait started, then screenshots. That
/// first look is never shown to the model.
/// </summary>
public sealed class WaitUntilChangeTool : WaitToolBase
{
    /// <summary>How often the screen is looked at while waiting for a change.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    public override string Name => "wait_until_change";
    // Right after starting Notepad, Qwen 3.8 called it to see the window that had already opened, and waited
    // out the task's 15 minutes (2026-11-10): a change before the wait doesn't end it.
    public override string Description =>
        "Wait until something on the screen changes from how it is when the wait starts (a reply arrives in a chat, a window or page finishes loading), " +
        "then take a screenshot. What already changed doesn't count: to see the screen as it is, take a screenshot.";
    public override JsonObject Parameters => Schema(
        ("seconds", Num("The longest to wait; without it, it waits until something changes"), false));
    public override string Describe(JsonObject args) =>
        GetNumber(args, "seconds") is { } seconds ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Wait up to {seconds} s for the screen to change") : "Wait for the screen to change";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        TimeSpan? limit = GetNumber(args, "seconds") is { } seconds ? TimeSpan.FromSeconds(Math.Max(seconds, 0.1)) : null;
        var waited = await UntilEndedAsync(async token => await WaitForChangeAsync(ctx, limit, token).ConfigureAwait(false) switch
        {
            { } after => $"The screen changed after {after.TotalSeconds:0.#}s.",
            null => $"Waited {limit!.Value.TotalSeconds:0.#}s; nothing on the screen changed.",
        }, ctx, ct).ConfigureAwait(false);
        return WithScreenshot(waited, ctx);
    }

    /// <summary>
    /// How long it took the screen to change from how it was at the start, or null if it didn't within
    /// <paramref name="limit"/>; with no limit it waits until it does (or the user or Stop ends it).
    /// </summary>
    private static async Task<TimeSpan?> WaitForChangeAsync(ToolContext ctx, TimeSpan? limit, CancellationToken ct)
    {
        var screen = ctx.GetScreenSpace().Screen;
        var first = ctx.Platform.Screen.Capture(screen, includeCursor: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((limit ?? TimeSpan.MaxValue) - clock.Elapsed is var left && left > TimeSpan.Zero)
        {
            await Task.Delay(left < Poll ? left : Poll, ct).ConfigureAwait(false);
            if (ClickCheck.Differ(first, ctx.Platform.Screen.Capture(screen, includeCursor: false)))
                return clock.Elapsed;
        }
        return null;
    }
}
