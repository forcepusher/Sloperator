using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: Claude through an OpenAI-compatible gateway shows its thinking and gets it back after a
/// tool call, and reads earlier steps from the prompt cache. Set BB_GATEWAY_KEY (or
/// BB_OPENROUTER_KEY); BB_GATEWAY_URL picks the gateway
/// (https://openrouter.ai/api/v1; https://api.proxyapi.ru/v1 sends the thinking as thinking_blocks
/// and refuses chat_template_kwargs), BB_GATEWAY_MODEL the model (anthropic/claude-haiku-5.5 on
/// OpenRouter, anthropic/claude-haiku-5-5 on proxyapi.ru). Costs well under a cent.
/// </summary>
public class GatewayThinkingTests
{
    private static readonly string? Key = Environment.GetEnvironmentVariable("BB_GATEWAY_KEY") ?? Environment.GetEnvironmentVariable("BB_OPENROUTER_KEY");
    private static readonly string Url = Environment.GetEnvironmentVariable("BB_GATEWAY_URL") ?? "https://openrouter.ai/api/v1";
    private static readonly string Model = Environment.GetEnvironmentVariable("BB_GATEWAY_MODEL") ?? Environment.GetEnvironmentVariable("BB_OPENROUTER_MODEL") ?? "anthropic/claude-haiku-5.5";

    [Fact]
    public async Task Claude_thinks_aloud_and_keeps_its_thinking_over_a_tool_call()
    {
        Assert.SkipWhen(Key is null, "Set BB_GATEWAY_KEY to run.");
        var server = new ServerSettings { BaseUrl = Url, Model = Model, ApiKey = Key!, Effort = "high" };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(5));
        var probe = await ServerProbe.CheckAsync(http, server, TestContext.Current.CancellationToken);
        Assert.True(probe.Reachable, probe.Message);
        server.EffortLevels = probe.Efforts ?? [];
        Assert.Contains("high", server.EffortLevels);

        var connection = BackendConnection.Create(http, server);
        Assert.Equal("high", connection.Options.Effort);
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "record", Description = "Records a number and says the time.",
                Parameters = JsonNode.Parse("""{"type":"object","properties":{"n":{"type":"integer"}},"required":["n"]}""")!.AsObject(),
            },
        };
        List<ChatMessage> history = [ChatMessage.User("Work out carefully, step by step in your head, the sum of the primes below 120, then call record with n = that sum. After that tell me the time.")];
        var first = await connection.Client.CompleteAsync(history, tools, connection.Options, null, TestContext.Current.CancellationToken);
        Assert.True(first.HasToolCalls, first.Content);
        Assert.False(string.IsNullOrWhiteSpace(first.Reasoning), "no thinking shown");
        Assert.Contains((first.ReasoningDetails ?? []).Concat(first.ThinkingBlocks ?? []), d => (string?)d!["signature"] is { Length: > 0 });
        history.Add(first);
        history.Add(ChatMessage.ToolResult(first.ToolCalls![0].Id, "14:52"));

        var second = await connection.Client.CompleteAsync(history, tools, connection.Options, null, TestContext.Current.CancellationToken);
        Assert.Contains("14:52", second.Content);

        // The thinking went back: the prompt is longer by it than one without it.
        var bare = history.Select(m => m.Role != ChatRole.Assistant ? m : new ChatMessage { Role = m.Role, Content = m.Content, ToolCalls = m.ToolCalls }).ToList();
        var without = await connection.Client.CompleteAsync(bare, tools, connection.Options, null, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"prompt with thinking {second.PromptTokens}, without {without.PromptTokens}; first thought {first.CompletionTokens} tokens");
        Assert.True(second.PromptTokens > without.PromptTokens + 50, $"{second.PromptTokens} vs {without.PromptTokens}");
    }

    /// <summary>
    /// Claude caches only when asked, through a gateway too: without the request's cache_control
    /// every step paid the whole conversation in full again (2026-11-01).
    /// </summary>
    [Fact]
    public async Task The_next_step_reads_the_one_before_it_from_the_cache()
    {
        Assert.SkipWhen(Key is null, "Set BB_GATEWAY_KEY to run.");
        var server = new ServerSettings { BaseUrl = Url, Model = Model, ApiKey = Key! };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(5));
        var connection = BackendConnection.Create(http, server);
        // Longer than the 512 to 4,096 tokens a server caches at least, and new each run, so nothing is cached before.
        var rules = string.Join("\n", Enumerable.Range(1, 250).Select(n => $"Rule {n}: when asked about item {n}, answer with the word {n} and nothing else."));
        List<ChatMessage> history = [ChatMessage.System($"Run {Guid.NewGuid()}\n{rules}"), ChatMessage.User("Say OK.")];

        var first = await connection.Client.CompleteAsync(history, [], connection.Options, null, TestContext.Current.CancellationToken);
        history.Add(first);
        history.Add(ChatMessage.User("Item 7?"));
        var second = await connection.Client.CompleteAsync(history, [], connection.Options, null, TestContext.Current.CancellationToken);

        TestContext.Current.SendDiagnosticMessage($"first {first.PromptTokens} prompt tokens, {first.CachedTokens ?? 0} from the cache; second {second.PromptTokens}, {second.CachedTokens ?? 0} from the cache");
        Assert.True(second.CachedTokens >= first.PromptTokens * 9 / 10, $"{second.CachedTokens} of {second.PromptTokens} from the cache");
    }

    /// <summary>
    /// A turn of calls with nothing said reaches the model with no text in it, and a reply cut off before it
    /// said anything isn't there at all. Sent as "", proxyapi.ru's LiteLLM put "[System: Empty message
    /// content sanitised to satisfy protocol]" in both, and Claude quoted it back when asked.
    /// </summary>
    [Fact]
    public async Task Turns_with_nothing_said_reach_the_model_with_no_text()
    {
        Assert.SkipWhen(Key is null, "Set BB_GATEWAY_KEY to run.");
        var server = new ServerSettings { BaseUrl = Url, Model = Model, ApiKey = Key! };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(5));
        var connection = BackendConnection.Create(http, server);
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "list_directory", Description = "Lists a folder.",
                Parameters = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")!.AsObject(),
            },
        };
        List<ChatMessage> history =
        [
            ChatMessage.User("What is in the current folder?"),
            new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = "call_1", Name = "list_directory", Arguments = """{"path":"."}""" }] },
            ChatMessage.ToolResult("call_1", "a.txt"),
            new ChatMessage { Role = ChatRole.Assistant, Content = "", Reasoning = "Cut off while thinking." },
            ChatMessage.User("Quote, character for character in a code block, the text of every one of your earlier turns in this conversation (not tool calls). Write NONE for a turn with no text."),
        ];
        var reply = await connection.Client.CompleteAsync(history, tools, connection.Options, null, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage(reply.Content ?? "");
        Assert.DoesNotContain("sanitised", reply.Content ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NONE", reply.Content ?? "");
    }
}
