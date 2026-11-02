using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Auto pricing lookup: the model is asked before a run ends to look up its prices and keep them with set_api_pricing.</summary>
public class PriceNudgeTests
{
    private static ToolContext Context(List<ModelPrices> kept) => new()
    {
        Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(),
        SetPrices = p => kept.Add(p.With("https://api.proxyapi.ru/v1", "anthropic/claude-haiku-5-5")),
    };

    [Fact]
    public async Task Before_the_end_the_model_is_asked_once_and_keeps_the_prices()
    {
        var kept = new List<ModelPrices>();
        var args = new JsonObject { ["input"] = 30, ["output"] = 150, ["currency"] = "₽" }.ToJsonString();
        var server = new ScriptedServer(
            Sse.Text("Done: the folder is tidy."),
            Sse.ToolCall("c1", "set_api_pricing", args),
            Sse.Text("Prices kept. The folder is tidy."));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.proxyapi.ru/v1", null);
        var asked = 0;
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(false, [new SetApiPricingTool()]), Context(kept), "sys")
        {
            PriceNudge = () => asked++ == 0 ? SetApiPricingTool.LookupRequest("anthropic/claude-haiku-5-5", "https://api.proxyapi.ru/v1") : null,
        };
        var observer = new RecordingObserver();

        await session.RunAsync("Tidy the folder", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Contains(observer.Notices, n => n.Contains("look up its prices", StringComparison.Ordinal));
        Assert.Equal(3, server.Requests.Count);
        Assert.Contains("stays their answer. One more thing, for Sloperator itself: Sloperator has no prices for the model anthropic/claude-haiku-5-5", server.Requests[1].ToJsonString());
        var prices = Assert.Single(kept);
        Assert.Equal(("https://api.proxyapi.ru/v1", "anthropic/claude-haiku-5-5", 30m, 150m, "₽"), (prices.BaseUrl, prices.Model, prices.Input, prices.Output, prices.Currency));
        Assert.Equal(1, asked); // once a run: the second end stands without asking again
        // The answer the user is shown is the one before the lookup: what the model wrote after it
        // (Haiku 5.5 rewrote its answer with the prices in it) neither streams nor shows.
        Assert.Equal(["reply Done: the folder is tidy.", "reply ", "reply "], observer.Events.Where(e => e.StartsWith("reply", StringComparison.Ordinal)));
        Assert.Equal("Done: the folder is tidy.", observer.Streamed);
        // The model's conversation keeps what it wrote.
        Assert.Equal("Prices kept. The folder is tidy.", session.History[^1].Content);
    }

    [Fact]
    public async Task A_message_of_the_users_during_the_lookup_is_answered_where_they_see_it()
    {
        var args = new JsonObject { ["input"] = 30, ["output"] = 150 }.ToJsonString();
        AgentSession? session = null;
        var server = new ScriptedServer(
            Sse.Text("Done: the folder is tidy."),
            Sse.ToolCall("c1", "set_api_pricing", args),
            Sse.Text("It holds 3 files."));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.proxyapi.ru/v1", null);
        var context = new ToolContext
        {
            Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(),
            SetPrices = _ => session!.Steer("How many files are left?"),
        };
        var asked = 0;
        session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(false, [new SetApiPricingTool()]), context, "sys")
        {
            PriceNudge = () => asked++ == 0 ? SetApiPricingTool.LookupRequest("anthropic/claude-haiku-5-5", "https://api.proxyapi.ru/v1") : null,
        };
        var observer = new RecordingObserver();

        await session.RunAsync("Tidy the folder", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Single(observer.Delivered);
        Assert.Equal("reply It holds 3 files.", observer.Events.Last(e => e.StartsWith("reply", StringComparison.Ordinal)));
        Assert.EndsWith("It holds 3 files.", observer.Streamed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_api_pricing_needs_a_price_and_a_place_to_keep_it()
    {
        var tool = new SetApiPricingTool();
        Assert.True((await tool.ExecuteAsync(new JsonObject(), Context([]), CancellationToken.None)).IsError);
        var nowhere = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        Assert.True((await tool.ExecuteAsync(new JsonObject { ["input"] = 1 }, nowhere, CancellationToken.None)).IsError);
        Assert.Equal("Set the prices of the model in use", tool.Describe(new JsonObject()));
        // Text prices read too; a negative one counts as none.
        var p = SetApiPricingTool.From(new JsonObject { ["input"] = "0,5", ["output"] = -1 });
        Assert.Equal((0.5m, (decimal?)null, "$"), (p.Input, p.Output, p.Currency));
    }
}
