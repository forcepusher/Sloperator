using System.Text.Json.Nodes;
using Sloperator.Core.Diffs;
using Sloperator.Core.Web;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The Live preview panel's page: where it is served, what its errors say, what reloads it.</summary>
public sealed class LivePageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-live-page-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_page_is_served_from_its_project_or_its_own_folder()
    {
        var alone = Path.Combine(_dir, "alone", "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(alone)!);
        Assert.Equal(Path.GetDirectoryName(alone), LivePage.Folder(alone));

        // In a project (a package.json above it), the project: a page in src reaches ../assets.
        var project = Path.Combine(_dir, "game");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "package.json"), "{}");
        var page = Path.Combine(project, "src", "index.html");
        Assert.Equal(project, LivePage.Folder(page));
    }

    [Fact]
    public void The_address_and_the_file_go_both_ways()
    {
        var folder = Path.Combine(_dir, "My game");
        var page = Path.Combine(folder, "levels", "level 1.html");
        var address = LivePage.Address(page, folder);
        Assert.Equal("https://page.sloperator.example/levels/level%201.html", address.AbsoluteUri);
        Assert.Equal(page, LivePage.PathOf(address, folder));
        // Another site, or a way out of the folder, is no file of the page's.
        Assert.Null(LivePage.PathOf(new Uri("https://example.com/levels/level%201.html"), folder));
        Assert.Null(LivePage.PathOf(new Uri("https://page.sloperator.example/..%5C..%5Csecret.txt"), folder));
        Assert.True(LivePage.IsPage("index.HTM"));
        Assert.False(LivePage.IsPage("game.js"));
    }

    [Fact]
    public void An_error_the_page_posted_reads_with_its_paths_in_the_folder()
    {
        var folder = Path.Combine(_dir, "game");
        var thrown = new JsonObject
        {
            ["text"] = "ReferenceError: level is not defined\n    at https://page.sloperator.example/src/game.js:30:29",
            ["source"] = "https://page.sloperator.example/src/game.js",
            ["line"] = 30,
            ["column"] = 29,
        }.ToJsonString();
        var error = LivePage.Parse(thrown, folder)!;
        Assert.Equal("ReferenceError: level is not defined", error.Summary);
        Assert.Equal("src/game.js:30:29", error.Where);
        Assert.Contains("at src/game.js:30:29", error.Text);
        // Its stack says where already; one without says it after.
        Assert.Equal(error.Text, error.Told);
        Assert.Equal("Script error\n    at src/game.js:3", new ConsoleError("Script error", "src/game.js", 3, 0).Told);

        // A file that failed to load, posted as a string the web view hands over in quotes.
        var missing = new JsonObject { ["text"] = "Failed to load https://page.sloperator.example/sprites/basket.png" }.ToJsonString();
        var loaded = LivePage.Parse(JsonValue.Create(missing).ToJsonString(), folder)!;
        Assert.Equal("Failed to load sprites/basket.png", loaded.Summary);
        Assert.Equal("", loaded.Where);
        Assert.Equal("Failed to load sprites/basket.png", loaded.Told);

        // Anything else the page posts is no error.
        Assert.Null(LivePage.Parse("hello", folder));
        Assert.Null(LivePage.Parse("""{"score":3}""", folder));
        Assert.Null(LivePage.Parse(null, folder));
    }

    [Fact]
    public void A_commit_or_an_install_in_the_folder_doesnt_reload_the_page()
    {
        var folder = Path.Combine(_dir, "game");
        Assert.True(LivePage.Reloads(Path.Combine(folder, "game.js"), folder));
        Assert.True(LivePage.Reloads(Path.Combine(folder, "assets", "star.png"), folder));
        Assert.False(LivePage.Reloads(Path.Combine(folder, ".git", "index"), folder));
        Assert.False(LivePage.Reloads(Path.Combine(folder, "node_modules", "three", "build", "three.module.js"), folder));
    }

    [Fact]
    public async Task The_page_the_agent_wrote_last_is_found_among_its_changes()
    {
        var changes = new FileChanges();
        var ctx = new Tools.ToolContext { Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = _dir, Changes = changes };
        Assert.Null(changes.Last(LivePage.IsPage));
        var page = Path.Combine(_dir, "index.html");
        var script = Path.Combine(_dir, "game.js");
        foreach (var (path, text) in new[] { (page, "<p>1</p>"), (script, "1"), (Path.Combine(_dir, "about.html"), "<p>a</p>"), (page, "<p>2</p>") })
        {
            var tool = new Tools.WriteFileTool();
            var args = new JsonObject { ["path"] = path, ["content"] = text };
            var call = changes.Starting(tool, args, ctx)!;
            await tool.ExecuteAsync(args, ctx, CancellationToken.None);
            await call.EndAsync();
        }
        // The last change, a page: that one.
        Assert.Equal(page, changes.Last(LivePage.IsPage));
        Assert.Equal(script, changes.Last(p => p.EndsWith(".js")));
    }
}
