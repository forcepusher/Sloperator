using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Auto pricing lookup's small agent (PriceLookup): it searches, then ends on the prices line.</summary>
public class PriceLookupTests
{
    [Fact]
    public async Task The_lookup_steps_are_heard_and_its_prices_line_is_read()
    {
        // A tool it doesn't have is answered with the two it has; then it answers.
        var server = new ScriptedServer(
            Sse.ToolCall("c1", "screenshot", "{}"),
            Sse.Text("proxyapi.ru lists Haiku 5.5 at 30 ₽ in, 150 ₽ out.\nPRICES {\"input\": 30, \"cachedInput\": null, \"cacheWrite\": null, \"output\": 150, \"currency\": \"₽\"}"));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.proxyapi.ru/v1", null);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };

        var (prices, answer) = await PriceLookup.RunAsync(client, "anthropic/claude-haiku-5-5", ctx, _ => { }, CancellationToken.None);

        Assert.Equal(30m, prices!.Input);
        Assert.Null(prices.CachedInput);
        Assert.Equal("₽", prices.Currency);
        Assert.Equal("proxyapi.ru lists Haiku 5.5 at 30 ₽ in, 150 ₽ out.", answer);
        Assert.Contains("https://api.proxyapi.ru/v1", server.Requests[0].ToJsonString());
        Assert.Equal(["web_search", "fetch_page"], server.Requests[0]["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        Assert.Contains("There is no tool screenshot", server.Requests[1].ToJsonString());
    }
}
