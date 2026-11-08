using System.Net;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Mcp;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Ssh;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

public class PictureSizeTests
{
    private const string Refused = """{"error": {"message": "messages.0.content.1.image.source.base64.data: At least one of the image dimensions exceed max allowed size for many-image requests: 3000 pixels", "type": "invalid_request_error", "param": null, "code": null}}""";

    [Fact]
    public async Task A_size_the_server_refused_goes_into_the_prompt_and_the_next_run_fits_it_from_the_start()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("bb-picture-size-").FullName);
        var server = new ScriptedServer(Sse.Text("Done."), Sse.Text("Done again."));
        server.Failures.Enqueue((HttpStatusCode.BadRequest, Refused));
        var http = new HttpClient(server);
        var settings = new ServerSettings { BaseUrl = "http://picture-size.test", Model = "wide/model", SupportsVision = true };
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "go", FromUser = true },
            new() { Role = ChatRole.Assistant, Content = "Looking." },
            new() { Role = ChatRole.User, Parts = [ContentPart.FromText(AgentSession.ScreenshotText), ContentPart.FromImage([1], "image/png")] },
            new() { Role = ChatRole.Assistant, Content = "Looking again." },
            new() { Role = ChatRole.User, Parts = [ContentPart.FromText(AgentSession.ScreenshotText), ContentPart.FromImage([2], "image/png")] },
        };
        var platform = new FakePlatform { ScreenBounds = new(0, 0, 5120, 1080) };
        await using var mcp = new McpHost();
        using var ssh = new SshHosts(paths.KnownHostsFile);

        var session = AgentSetup.Create(BackendConnection.Create(http, settings), platform, new AgentSettings(), paths, mcp, ssh, history);
        var observer = new RecordingObserver();
        await session.RunAsync("what now?", observer, TestContext.Current.CancellationToken);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Contains("5120x1088", SystemPrompt(server, 0));
        Assert.Contains("2976x640", SystemPrompt(server, 1));

        session = AgentSetup.Create(BackendConnection.Create(http, settings), platform, new AgentSettings(), paths, mcp, ssh, history);
        await session.RunAsync("and now?", observer, TestContext.Current.CancellationToken);

        Assert.Equal(3, server.Requests.Count);
        Assert.Contains("2976x640", SystemPrompt(server, 2));
    }

    /// <summary>
    /// Opt-in, for Claude through a gateway (BB_LIVE_URL, BB_LIVE_KEY, BB_LIVE_MODEL, as in
    /// <see cref="ReplyRoomLiveTests"/>): 21 real pictures of 4000 px, refused, and the run goes on
    /// with the screen taken anew at the size the refusal names. One or two requests of a few
    /// thousand tokens.
    /// </summary>
    [Fact]
    public async Task A_live_server_refusing_a_picture_size_takes_the_run_on_at_that_size()
    {
        var url = Environment.GetEnvironmentVariable("BB_LIVE_URL");
        Assert.SkipWhen(url is null, "Set BB_LIVE_URL to run.");
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var server = new ServerSettings { BaseUrl = url!, ApiKey = Environment.GetEnvironmentVariable("BB_LIVE_KEY") ?? "", Model = Environment.GetEnvironmentVariable("BB_LIVE_MODEL") ?? "" };
        var probe = await ServerProbe.CheckAsync(http, server, ct);
        Assert.True(probe.Reachable, probe.Message);
        server.BaseUrl = probe.BaseUrl!;
        server.Model = probe.Model!;

        using var wide = SKImage.Create(new SKImageInfo(4000, 64));
        using var jpeg = wide.Encode(SKEncodedImageFormat.Jpeg, 80);
        var history = new List<ChatMessage> { new() { Role = ChatRole.User, Content = "Look at the screen.", FromUser = true } };
        for (var i = 0; i < 21; i++)
        {
            history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "Looking." });
            history.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(AgentSession.ScreenshotText), ContentPart.FromImage(jpeg.ToArray(), "image/jpeg")] });
        }
        var connection = BackendConnection.Create(http, server);
        var ctx = new ToolContext { Platform = new FakePlatform { ScreenBounds = new(0, 0, 5120, 1080) }, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(false), ctx, "You are a test assistant.", history, 1_000_000);
        var observer = new RecordingObserver();
        await session.RunAsync("Reply with the single word done.", observer, ct);

        foreach (var notice in observer.Notices)
            TestContext.Current.SendDiagnosticMessage("notice: " + notice);
        TestContext.Current.SendDiagnosticMessage($"{server.BaseUrl}: {observer.StopReason} {observer.Detail}, longest side {ctx.MaxScreenshotSide}, reply: {session.History[^1].Content}");
        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.NotNull(ctx.MaxScreenshotSide);
    }

    private static string SystemPrompt(ScriptedServer server, int request) =>
        server.Requests[request]["messages"]![0]!["content"]!.ToJsonString();
}
