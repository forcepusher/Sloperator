using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The subagent tool, and Auto pricing lookup through it (PriceLookup).</summary>
public class PriceLookupTests
{
    private static ToolContext Context(ScriptedServer server, string baseUrl, List<string>? steps = null) => new()
    {
        Platform = new FakePlatform(),
        Settings = new AgentSettings(),
        WorkingDirectory = Path.GetTempPath(),
        Client = new OpenAiChatClient(new HttpClient(server), baseUrl, null),
        Options = new ChatRequestOptions { Model = "helper-model" },
        Progress = steps is null ? null : steps.Add,
    };

    [Fact]
    public async Task A_subagent_works_in_a_fresh_context_and_returns_its_final_answer()
    {
        var dir = Directory.CreateTempSubdirectory("bb-subagent-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "The answer is 42.");
            var server = new ScriptedServer(
                Sse.ToolCall("c1", "list_directory", new JsonObject { ["path"] = dir }.ToJsonString()),
                Sse.ToolCall("c2", "screenshot", "{}"),
                Sse.Text("<think>Read it.</think>The folder's notes say the answer is 42."));
            var steps = new List<string>();
            var tool = new SubagentTool();
            var args = new JsonObject { ["task"] = "Find the answer", ["prompt"] = "What does the notes file in " + dir + " say?" };

            var result = await tool.ExecuteAsync(args, Context(server, "http://test", steps), CancellationToken.None);

            Assert.Equal("Find the answer", tool.Describe(args));
            Assert.Equal("The folder's notes say the answer is 42.", result.Text);
            Assert.Single(steps);
            // Only the prompt, not the chat; only tools that look.
            var first = server.Requests[0];
            Assert.Equal(2, first["messages"]!.AsArray().Count);
            Assert.Equal(["web_search", "fetch_page", "read_file", "list_directory"], first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
            Assert.Equal("helper-model", first["model"]!.GetValue<string>());
            Assert.Contains("There is no tool screenshot", server.Requests[2].ToJsonString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_price_lookup_reads_the_prices_line_of_the_subagents_answer()
    {
        var server = new ScriptedServer(
            Sse.Text("proxyapi.ru lists Haiku 5.5 at 30 ₽ in, 150 ₽ out.\nPRICES {\"input\": 30, \"cachedInput\": null, \"cacheWrite\": null, \"output\": 150, \"currency\": \"₽\"}"));

        var (prices, answer) = await PriceLookup.RunAsync(Context(server, "https://api.proxyapi.ru/v1"), "anthropic/claude-haiku-5-5", CancellationToken.None);

        Assert.Equal(30m, prices!.Input);
        Assert.Null(prices.CachedInput);
        Assert.Equal("₽", prices.Currency);
        Assert.StartsWith("proxyapi.ru lists", answer);
        Assert.Contains("https://api.proxyapi.ru/v1", server.Requests[0].ToJsonString());
        Assert.Equal(["web_search", "fetch_page"], server.Requests[0]["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        Assert.Equal("Look up the prices of anthropic/claude-haiku-5-5 on api.proxyapi.ru", PriceLookup.TaskName("anthropic/claude-haiku-5-5", "https://api.proxyapi.ru/v1"));
    }

    [Fact]
    public async Task Set_api_pricing_keeps_the_prices_for_the_model_in_use()
    {
        var kept = new List<ModelPrices>();
        var ctx = new ToolContext
        {
            Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(),
            Client = new OpenAiChatClient(new HttpClient(new ScriptedServer()), "https://api.proxyapi.ru/v1", null),
            Options = new ChatRequestOptions { Model = "anthropic/claude-haiku-5-5" }, SetPrices = kept.Add,
        };
        var tool = new SetApiPricingTool();
        var args = SetApiPricingTool.Arguments(new ModelPrices { Model = "anthropic/claude-haiku-5-5", Input = 30, Output = 150, Currency = "₽" });

        var result = await tool.ExecuteAsync(args, ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Set the prices of anthropic/claude-haiku-5-5", tool.Describe(args));
        var prices = Assert.Single(kept);
        Assert.Equal(("https://api.proxyapi.ru/v1", 30m, null, 150m, "₽"), (prices.BaseUrl, prices.Input, prices.CachedInput, prices.Output, prices.Currency));
        Assert.Contains("input 30 ₽", result.Text);
        // Nothing to keep: refused.
        Assert.True((await tool.ExecuteAsync(new JsonObject(), ctx, CancellationToken.None)).IsError);
    }
}
