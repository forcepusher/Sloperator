using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Auto pricing lookup: after a run's answer, a side task looks up its model's prices and keeps them with set_api_pricing, out of the chat's context.</summary>
public class PriceLookupTests
{
    private static ToolContext Context(List<ModelPrices> kept) => new()
    {
        Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(),
        SetPrices = p => kept.Add(p.With("https://api.proxyapi.ru/v1", "anthropic/claude-haiku-5-5")),
    };

    [Fact]
    public async Task After_the_answer_a_side_task_keeps_the_prices_and_the_chat_keeps_none_of_it()
    {
        var kept = new List<ModelPrices>();
        var args = new JsonObject { ["input"] = 30, ["output"] = 150, ["currency"] = "₽" }.ToJsonString();
        var server = new ScriptedServer(
            Sse.Text("Done: the folder is tidy."),
            Sse.ToolCall("c1", "set_api_pricing", args));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.proxyapi.ru/v1", null);
        var asked = 0;
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(false, [new SetApiPricingTool()]), Context(kept), "sys")
        {
            PriceLookup = () => asked++ == 0 ? SetApiPricingTool.LookupRequest("anthropic/claude-haiku-5-5", "https://api.proxyapi.ru/v1") : null,
        };
        var observer = new RecordingObserver();

        await session.RunAsync("Tidy the folder", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(1, asked);
        var prices = Assert.Single(kept);
        Assert.Equal(("https://api.proxyapi.ru/v1", "anthropic/claude-haiku-5-5", 30m, 150m, "₽"), (prices.BaseUrl, prices.Model, prices.Input, prices.Output, prices.Currency));
        // The side task is a conversation of its own: the lookup and its tools, not the chat.
        Assert.Equal(2, server.Requests.Count);
        var side = server.Requests[1].ToJsonString();
        Assert.Contains("side task for Sloperator", side);
        Assert.Contains("Sloperator has no prices for the model anthropic/claude-haiku-5-5", side);
        Assert.DoesNotContain("Tidy the folder", side);
        Assert.Equal(["fetch_page", "set_api_pricing", "web_search"],
            server.Requests[1]["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).Order());
        // It ends once the prices are kept, with no reply after it; the chat's last word is the answer,
        // and the conversation holds nothing of the lookup.
        Assert.Equal(["reply Done: the folder is tidy."], observer.Events.Where(e => e.StartsWith("reply", StringComparison.Ordinal)));
        Assert.Equal(["user", "assistant"], session.History.Select(m => m.Role));
        Assert.Equal([AgentSession.SideTaskName], observer.ToolsRun);
        Assert.Equal(["Set the prices of the model in use"], observer.Progress);
        // Only to keep the prices: no Output.
        Assert.Equal("", observer.Results.Single().Text);
    }

    [Fact]
    public async Task A_message_of_the_users_during_the_side_task_is_answered_after_it()
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
            PriceLookup = () => asked++ == 0 ? SetApiPricingTool.LookupRequest("anthropic/claude-haiku-5-5", "https://api.proxyapi.ru/v1") : null,
        };
        var observer = new RecordingObserver();

        await session.RunAsync("Tidy the folder", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Single(observer.Delivered);
        Assert.Equal(3, server.Requests.Count);
        // The chat goes on from the answer: the message is the next request, the side task not in it.
        var next = server.Requests[2].ToJsonString();
        Assert.Contains("How many files are left?", next);
        Assert.DoesNotContain("set_api_pricing\",\"arguments", next);
        Assert.Equal("It holds 3 files.", session.History[^1].Content);
        Assert.Equal(1, asked);
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
