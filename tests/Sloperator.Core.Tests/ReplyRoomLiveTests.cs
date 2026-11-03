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
    /// Opt-in, for a reseller that prices the longest reply before running a request (proxyapi.ru:
    /// 402 "Insufficient balance" with balance left): asking for far more reply room than the
    /// balance pays for ends in a reply, with the room halved until it fits. BB_LIVE_BIG is the
    /// room asked for (default 1,000,000). A tiny prompt: the refused requests cost nothing.
    /// </summary>
    [Fact]
    public async Task Reply_room_beyond_the_balance_is_halved_until_the_server_takes_it()
    {
        Assert.SkipWhen(Url is null, "Set BB_LIVE_URL to run.");
        var ct = TestContext.Current.CancellationToken;
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(recorder) { Timeout = TimeSpan.FromMinutes(5) };
        var big = int.TryParse(Environment.GetEnvironmentVariable("BB_LIVE_BIG"), out var b) ? b : 1_000_000;
        var client = new OpenAiChatClient(http, Url!, Key);

        var reply = await client.CompleteAsync([ChatMessage.User("Reply with one word: ok")], [],
            new ChatRequestOptions { Model = Model, MaxTokens = big }, null, ct);

        foreach (var (body, status) in recorder.Sent)
            TestContext.Current.SendDiagnosticMessage($"max_tokens {body["max_tokens"]}: {status}");
        Assert.Contains(402, recorder.Sent.Select(s => s.Status));
        Assert.Equal(200, recorder.Sent[^1].Status);
        Assert.False(string.IsNullOrWhiteSpace(reply.Content));
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
