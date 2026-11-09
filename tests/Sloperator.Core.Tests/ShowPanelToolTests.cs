using System.Text.Json.Nodes;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The agent opening the app's side panel to what the user asks to see there (show_panel).</summary>
public sealed class ShowPanelToolTests
{
    private static ToolContext Context(Func<string, string?, Task<string?>>? show) =>
        new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), ShowPanel = show };

    private static Task<ToolResult> Run(ToolContext ctx, string panel, string? path = null)
    {
        var args = new JsonObject { ["panel"] = panel };
        if (path is not null)
            args["path"] = path;
        return new ShowPanelTool().ExecuteAsync(args, ctx, CancellationToken.None);
    }

    [Fact]
    public async Task The_panel_named_opens_with_the_page_in_it()
    {
        var page = Path.Combine(Path.GetTempPath(), $"show-panel-{Guid.NewGuid():N}.html");
        File.WriteAllText(page, "<p>hi</p>");
        try
        {
            var shown = new List<(string Panel, string? Path)>();
            var ctx = Context((panel, path) => { shown.Add((panel, path)); return Task.FromResult<string?>(null); });

            var result = await Run(ctx, "web preview", page);
            Assert.False(result.IsError);
            Assert.Equal($"The Web preview beside the chat shows {page}.", result.Text);
            // Its name as the dropdown has it, whatever case the model wrote.
            Assert.Equal(("Web preview", page), Assert.Single(shown));

            Assert.Equal("The Terminal is open beside the chat.", (await Run(ctx, "Terminal")).Text);
        }
        finally
        {
            File.Delete(page);
        }
    }

    [Fact]
    public async Task A_panel_or_file_that_isnt_there_and_a_refusal_are_said()
    {
        var ctx = Context((_, _) => Task.FromResult<string?>("The Web preview doesn't show notes.txt."));
        var unknown = await Run(ctx, "Preview");
        Assert.True(unknown.IsError);
        Assert.Contains("There is no panel \"Preview\". The panels: Web preview, Code editor", unknown.Text);
        Assert.Contains("isn't there", (await Run(ctx, "Code editor", Path.Combine(Path.GetTempPath(), "no-such-file-here.txt"))).Text);
        var refused = await Run(ctx, "Web preview", Path.GetTempPath());
        Assert.True(refused.IsError);
        Assert.Equal("The Web preview doesn't show notes.txt.", refused.Text);

        // Without a side panel (the command line) it does nothing.
        Assert.Equal("There is no side panel here.", (await Run(Context(null), "Terminal")).Text);
    }
}
