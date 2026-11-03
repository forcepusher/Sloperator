using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: a short run on a real server asks for reply room (max_tokens) at each step, and the
/// server takes it or says what it takes instead. BB_LIVE_URL picks the server, BB_LIVE_KEY its key
/// and BB_LIVE_MODEL the model (else the one Sloperator picks there). Two or three requests.
/// </summary>
public class ReplyRoomLiveTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_LIVE_URL");
    private static readonly string Key = Environment.GetEnvironmentVariable("BB_LIVE_KEY") ?? "";
    private static readonly string Model = Environment.GetEnvironmentVariable("BB_LIVE_MODEL") ?? "";

    [Fact]
    public async Task A_run_asks_for_reply_room_and_the_server_takes_it()
    {
        Assert.SkipWhen(Url is null, "Set BB_LIVE_URL to run.");
        var ct = TestContext.Current.CancellationToken;
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(recorder) { Timeout = TimeSpan.FromMinutes(5) };
        var server = new ServerSettings { BaseUrl = Url!, ApiKey = Key, Model = Model };
        var probe = await ServerProbe.CheckAsync(http, server, ct);
        Assert.True(probe.Reachable, probe.Message);
        server.BaseUrl = probe.BaseUrl!;
        server.Model = probe.Model!;
        server.EffortLevels = probe.Efforts ?? [];
        var facts = await ServerProbe.EnsureLoadedAsync(http, server, null, ct, known: probe.Facts);
        server.SetDetectedContext(facts.Context);
        recorder.Sent.Clear();

        var connection = BackendConnection.Create(http, server);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(false), ctx,
            "You are a test assistant.", contextTokens: connection.ContextTokens);
        var observer = new RecordingObserver();
        await session.RunAsync("Call list_directory on the current folder once, then reply with the single word done.", observer, ct);

        TestContext.Current.SendDiagnosticMessage($"{server.BaseUrl} {server.Model}, context {connection.ContextTokens}");
        foreach (var (body, status) in recorder.Sent)
            TestContext.Current.SendDiagnosticMessage($"max_tokens {body["max_tokens"]?.ToJsonString() ?? "(none)"}: {status}");
        foreach (var notice in observer.Notices)
            TestContext.Current.SendDiagnosticMessage("notice: " + notice);
        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.NotNull(recorder.Sent[0].Body["max_tokens"]);
        Assert.Equal(200, recorder.Sent[^1].Status);
        Assert.Contains("done", session.History[^1].Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opt-in, for a server whose credit check before the call counts pictures far above their price
    /// (proxyapi.ru refused 80 screenshots with 2000₽ on the account): a run holding that many goes
    /// on with only the latest. BB_LIVE_SHOTS is a folder of screenshots (a chat's .jpg files), of
    /// which BB_LIVE_SHOT_COUNT are taken (80); BB_LIVE_TEXT_K adds about that many thousand tokens of
    /// tool output, as a long run has. The refused requests cost nothing; the one that goes through
    /// carries one picture.
    /// </summary>
    [Fact]
    public async Task Screenshots_a_credit_check_counts_too_high_are_left_out_and_the_run_goes_on()
    {
        var shots = Environment.GetEnvironmentVariable("BB_LIVE_SHOTS");
        Assert.SkipWhen(Url is null || shots is null, "Set BB_LIVE_URL and BB_LIVE_SHOTS to run.");
        var count = int.TryParse(Environment.GetEnvironmentVariable("BB_LIVE_SHOT_COUNT"), out var c) ? c : 80;
        var textK = int.TryParse(Environment.GetEnvironmentVariable("BB_LIVE_TEXT_K"), out var t) ? t : 0;
        var ct = TestContext.Current.CancellationToken;
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(recorder) { Timeout = TimeSpan.FromMinutes(5) };
        var server = new ServerSettings { BaseUrl = Url!, ApiKey = Key, Model = Model };
        var connection = BackendConnection.Create(http, server);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(false), ctx,
            "You are a test assistant.", contextTokens: 1_000_000);
        session.History.Add(new ChatMessage { Role = ChatRole.User, Content = "Look at the screen a few times.", FromUser = true });
        var files = Directory.GetFiles(shots!, "*.jpg").Order().Take(count).ToList();
        // Numbered lines, about 10 tokens each, spread over the steps.
        var lines = textK * 100 / Math.Max(1, files.Count);
        var i = 0;
        foreach (var file in files)
        {
            var output = string.Join("\n", Enumerable.Range(0, lines).Select(n => $"line {i}.{n}: value {(i * 7919 + n * 104729) % 1000003} ok"));
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "screenshot", Arguments = "{}" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", lines > 0 ? output : "ok"));
            session.History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(AgentSession.ScreenshotText), ContentPart.FromImage(File.ReadAllBytes(file), "image/jpeg")] });
            i++;
        }
        var observer = new RecordingObserver();

        await session.RunAsync("Reply with the single word done, calling no tools.", observer, ct);

        static int Pictures(JsonObject body) => body["messages"]!.AsArray().Sum(m => (m!["content"] as JsonArray)?.Count(p => (string?)p!["type"] == "image_url") ?? 0);
        // BB_LIVE_LOG, if set, gets the same lines (diagnostic messages show only when turned on).
        var log = Environment.GetEnvironmentVariable("BB_LIVE_LOG");
        void Say(string line)
        {
            TestContext.Current.SendDiagnosticMessage(line);
            if (log is not null)
                File.AppendAllText(log, line + "\n");
        }
        foreach (var (body, status) in recorder.Sent)
            Say($"{Pictures(body)} pictures, {body.ToJsonString().Length / 1_000_000.0:0.0} MB, max_tokens {body["max_tokens"]}: {status}");
        Say($"reply: {session.History[^1].Content} (prompt {session.History[^1].PromptTokens} tokens), stop {observer.StopReason} {observer.Detail}");
        foreach (var notice in observer.Notices)
            Say("notice: " + notice);
        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(402, recorder.Sent[0].Status);
        Assert.Equal((1, 200), (Pictures(recorder.Sent[^1].Body), recorder.Sent[^1].Status));
    }

    /// <summary>The chat requests sent (those with messages) and the status each got.</summary>
    private sealed class Recorder : DelegatingHandler
    {
        public List<(JsonObject Body, int Status)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject;
            var response = await base.SendAsync(request, ct);
            if (body?["messages"] is not null)
                lock (Sent) Sent.Add((body, (int)response.StatusCode));
            return response;
        }
    }
}
