using System.Text.Json.Nodes;

namespace Sloperator.Core.Tools;

/// <summary>
/// Opens a panel of the app's side panel, left of the chat, to a page, file or folder, for the user to see
/// there. Asked to show a page "in the Web preview" with the panel closed, Haiku 5.5 opened it in Edge and
/// said it couldn't open Sloperator's panel (2026-11-10): nothing let it, and on Auto the panel opens by
/// itself only to what is written, run or made. Offered in the app only (see <see cref="ToolContext.ShowPanel"/>).
/// </summary>
public sealed class ShowPanelTool : Tool
{
    /// <summary>The panels it opens, by their names in the app's Side panel dropdown.</summary>
    public static readonly string[] Panels =
        ["Web preview", "Code editor", "File inspector", "File changes", "Terminal", "Image editor", "Audio editor", "Video editor", "Cost estimates"];

    public override string Name => "show_panel";
    public override string Description =>
        "Open Sloperator's side panel to show the user something there when they ask: a web page in the Web preview, " +
        "a file in the Code editor or File inspector, a picture's, sound's or video's folder in its editor.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("panel", Str("The panel", Panels), true),
        ("path", Str("The page, file or folder to show in it"), false));

    public override string Describe(JsonObject args) =>
        GetString(args, "path") is { Length: > 0 } path ? $"Show {Path.GetFileName(path.TrimEnd('\\', '/'))} in the {GetString(args, "panel")}" : $"Show the {GetString(args, "panel")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ShowPanel is not { } show)
            return ToolResult.Error("There is no side panel here.");
        var name = RequireString(args, "panel").Trim();
        var panel = Panels.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (panel is null)
            return ToolResult.Error($"There is no panel \"{name}\". The panels: {string.Join(", ", Panels)}.");
        string? path = null;
        if (GetString(args, "path") is { Length: > 0 } given)
        {
            path = ctx.ResolvePath(given.Trim().Trim('"'));
            if (!File.Exists(path) && !Directory.Exists(path))
                return ToolResult.Error($"{path} isn't there.");
        }
        var shown = await show(panel, path).ConfigureAwait(false);
        if (shown.Problem is { } problem)
            return ToolResult.Error(problem);
        var text = path is null ? $"The {panel} is open beside the chat." : $"The {panel} beside the chat shows {path}.";
        return new ToolResult(shown.Note is { } note ? $"{text} {note}" : text);
    }
}

/// <summary>
/// What opening a side panel came to (<see cref="ToolContext.ShowPanel"/>): why it couldn't, or else what the
/// panel does with what it shows, such as a page reloading as its files change. Told only that it opened, Haiku 5.5
/// said after each change it couldn't see the Web preview, and to reload the page there if it hadn't (2026-11-10).
/// </summary>
public readonly record struct PanelShown(string? Problem = null, string? Note = null);
