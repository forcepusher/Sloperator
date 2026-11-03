using System.Net;
using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>A model's effort levels as its server's refusals say them, not as its name suggests.</summary>
public class EffortRefusalTests
{
    [Theory]
    // proxyapi.ru's openai/gpt-6-sol, 2026-11-03: chat/completions, then /v1/responses.
    [InlineData("""{"error": {"message": "Unsupported value: 'reasoning_effort' does not support 'max' with this model. Supported values are: 'none', 'low', 'medium', 'high', and 'xhigh'.", "type": "invalid_request_error", "param": null, "code": null}}""", "max", "low medium high xhigh")]
    [InlineData("""{"error": {"message": "Unsupported value: 'minimal' is not supported with the 'gpt-6-sol' model. Supported values are: 'none', 'low', 'medium', 'high', 'xhigh', and 'max'.", "type": "invalid_request_error", "param": null, "code": null}}""", "minimal", "low medium high xhigh max")]
    [InlineData("""{"type":"error","error":{"type":"invalid_request_error","message":"output_config.effort: Input should be 'low', 'medium' or 'high'"}}""", "max", "low medium high")]
    // Only the refused one named: all the others, until another refusal narrows them.
    [InlineData("""{"error":{"message":"'max' is not a valid reasoning_effort."}}""", "max", "low medium high xhigh")]
    // No effort at all.
    [InlineData("""{"error":{"message":"reasoning_effort is not supported for this model."}}""", "high", "")]
    // Not about the effort.
    [InlineData("""{"error":{"message":"chat_template_kwargs: Extra inputs are not permitted"}}""", "high", null)]
    [InlineData("""{"error":{"message":"Invalid value for 'temperature': 'high' is not a number."}}""", "high", null)]
    public void A_refusal_of_the_effort_says_which_levels_the_model_takes(string err, string sent, string? taken)
    {
        var levels = OpenAiChatClient.EffortRefusal(err, sent);
        Assert.Equal(taken, levels is null ? null : string.Join(" ", levels));
    }

    [Fact]
    public async Task The_server_check_and_later_requests_go_by_what_the_refusal_said()
    {
        var server = new ScriptedServer(Sse.Text("ok"), Sse.Text("ok"));
        server.Failures.Enqueue((HttpStatusCode.BadRequest,
            """{"error": {"message": "Unsupported value: 'reasoning_effort' does not support 'max' with this model. Supported values are: 'none', 'low', 'medium', 'high', and 'xhigh'."}}"""));
        var url = $"http://effort-{Guid.NewGuid():N}/v1";
        var client = new OpenAiChatClient(new HttpClient(server), url, "key");
        var options = new ChatRequestOptions { Model = "m", Effort = "max" };

        Assert.Equal("ok", (await client.CompleteAsync([ChatMessage.User("hi")], [], options, null, CancellationToken.None)).Content);
        Assert.Equal(["max", "xhigh"], server.Requests.Select(r => (string)r["reasoning_effort"]!));
        Assert.Equal("max", options.Effort);
        Assert.Equal(["low", "medium", "high", "xhigh"], OpenAiChatClient.EffortsTaken(url, "m"));

        // A run of its own asks for the nearest it takes straight away.
        await new OpenAiChatClient(new HttpClient(server), url, "key").CompleteAsync([ChatMessage.User("hi")], [], options, null, CancellationToken.None);
        Assert.Equal("xhigh", (string)server.Requests[2]["reasoning_effort"]!);
    }

    [Fact]
    public async Task A_bare_model_list_offers_every_level()
    {
        var probe = await ServerProbe.CheckAsync(new HttpClient(new BareList()), new ServerSettings { BaseUrl = $"http://bare-{Guid.NewGuid():N}/v1", Model = "openai/gpt-6-sol" }, CancellationToken.None);
        Assert.True(probe.Reachable, probe.Message);
        Assert.Equal(Efforts.Levels, probe.Efforts);
    }

    /// <summary>
    /// Opt-in, with ResponsesLiveTests' server (BB_RESPONSES_KEY, BB_RESPONSES_URL,
    /// BB_RESPONSES_MODEL): proxyapi.ru's openai/gpt-6-sol refuses max on chat/completions and
    /// lists what it takes, so the reply comes at the nearest, and the server check offers those.
    /// </summary>
    [Fact]
    public async Task A_live_server_refusing_an_effort_gets_the_nearest_it_takes()
    {
        var key = Environment.GetEnvironmentVariable("BB_RESPONSES_KEY");
        Assert.SkipWhen(key is null, "Set BB_RESPONSES_KEY to run.");
        var url = Environment.GetEnvironmentVariable("BB_RESPONSES_URL") ?? "https://api.proxyapi.ru/v1";
        var model = Environment.GetEnvironmentVariable("BB_RESPONSES_MODEL") ?? "openai/gpt-6-sol";
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(5));
        var reply = await new OpenAiChatClient(http, url, key).CompleteAsync([ChatMessage.User("Say hi in one word.")], [],
            new ChatRequestOptions { Model = model, Effort = "max" }, null, TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(reply.Content));
        var taken = OpenAiChatClient.EffortsTaken(url, model);
        TestContext.Current.SendDiagnosticMessage("takes " + (taken is null ? "(no refusal)" : string.Join(", ", taken)));
        Assert.NotNull(taken);
        Assert.DoesNotContain("max", taken);
        var probe = await ServerProbe.CheckAsync(http, new ServerSettings { BaseUrl = url, ApiKey = key!, Model = model }, TestContext.Current.CancellationToken);
        Assert.Equal(taken, probe.Efforts);
    }

    /// <summary>A server whose model list is ids alone, as proxyapi.ru's; everything else is not found.</summary>
    private sealed class BareList : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/v1/models"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"object":"list","data":[{"id":"openai/gpt-6-sol","object":"model","owned_by":"openai"}]}""") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
