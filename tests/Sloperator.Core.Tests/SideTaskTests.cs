using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Side tasks: the model's side_task calls, and Auto pricing lookup's after a run's answer, which keeps the model's prices with set_api_pricing, out of the chat's context.</summary>
public class SideTaskTests
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
        Assert.Contains("Sloperator lacks prices for the model anthropic/claude-haiku-5-5", side);
        Assert.DoesNotContain("Tidy the folder", side);
        Assert.Equal(["fetch_page", "set_api_pricing", "web_search"],
            server.Requests[1]["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).Order());
        // It ends once the prices are kept, with no reply after it; the chat's last word is the answer,
        // and the conversation holds nothing of the lookup.
        Assert.Equal(["reply Done: the folder is tidy."], observer.Events.Where(e => e.StartsWith("reply", StringComparison.Ordinal)));
        Assert.Equal(["user", "assistant"], session.History.Select(m => m.Role));
        Assert.Equal([SideTaskTool.ToolName], observer.ToolsRun);
        Assert.Equal(["Set the prices of the model in use"], observer.Progress);
        // Only to keep the prices: nothing to return (the window shows no Output for it).
        Assert.Equal(AgentSession.NoOutputText, observer.Results.Single().Text);
    }

    [Fact]
    public async Task The_prices_are_looked_up_as_soon_as_a_reply_came_without_a_cost_while_the_chat_waits()
    {
        var kept = new List<ModelPrices>();
        var file = Path.Combine(Directory.CreateTempSubdirectory("bb-side-").FullName, "notes.txt");
        File.WriteAllText(file, "PELICAN");
        var server = new ScriptedServer(
            Sse.ToolCall("r1", "read_file", new JsonObject { ["path"] = file }.ToJsonString()),
            Sse.ToolCall("c1", "set_api_pricing", new JsonObject { ["input"] = 1, ["output"] = 2 }.ToJsonString()),
            Sse.Text("The word is PELICAN."));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.proxyapi.ru/v1", null);
        var asked = 0;
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(false, [new SetApiPricingTool()]), Context(kept), "sys")
        {
            PriceLookup = () => asked++ == 0 ? SetApiPricingTool.LookupRequest("anthropic/claude-haiku-5-5", "https://api.proxyapi.ru/v1") : null,
        };
        var observer = new RecordingObserver();

        await session.RunAsync("Read notes.txt", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Single(kept);
        // After the first step's call, before the next step: the side task's request comes second.
        Assert.Equal(3, server.Requests.Count);
        Assert.Contains("side task for Sloperator", server.Requests[1].ToJsonString());
        Assert.Contains("PELICAN", server.Requests[2].ToJsonString());
        Assert.Equal(["read_file", "side_task"], observer.ToolsRun);
        Assert.Equal("The word is PELICAN.", session.History[^1].Content);
        Assert.Equal(1, asked);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_side_task_the_model_calls_runs_apart_and_returns_its_answer_only_with_output_on(bool output)
    {
        var args = new JsonObject { ["task"] = "Count the files", ["prompt"] = "Count the files in C:/work.", ["output"] = output }.ToJsonString();
        var server = new ScriptedServer(
            Sse.ToolCall("s1", "side_task", args),
            Sse.Text("3 files."),
            Sse.Text("Done."));
        var client = new OpenAiChatClient(new HttpClient(server), "http://localhost:1234/v1", null);
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(false), Context([]), "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("Tidy the folder", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(3, server.Requests.Count);
        // Its own conversation: the prompt, not the chat; the run's tools but another side task and those of the chat.
        var side = server.Requests[1].ToJsonString();
        Assert.Contains("Count the files in C:/work.", side);
        Assert.DoesNotContain("Tidy the folder", side);
        Assert.Contains(output ? "answer with what the task asks for" : "Nothing you write goes back", side);
        var tools = server.Requests[1]["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).ToList();
        Assert.Contains("read_file", tools);
        Assert.DoesNotContain("side_task", tools);
        Assert.DoesNotContain("reset_context", tools);
        // The chat waited for it, and got its answer only with output on.
        var result = session.History.Single(m => m.Role == ChatRole.Tool).Content;
        Assert.Equal(output ? "3 files." : AgentSession.NoOutputText, result);
        Assert.DoesNotContain(session.History, m => m.Content == "3 files." && m.Role == ChatRole.Assistant);
        Assert.Equal(["side_task"], observer.ToolsRun);
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
