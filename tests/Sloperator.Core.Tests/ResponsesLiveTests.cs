using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: a model whose server refuses tools with thinking on chat/completions and says to use
/// /v1/responses thinks, calls a tool and gets its thinking back on the next step. Set
/// BB_RESPONSES_KEY; BB_RESPONSES_URL picks the server (https://api.proxyapi.ru/v1),
/// BB_RESPONSES_MODEL the model (openai/gpt-6-sol). Costs well under a cent.
/// </summary>
public class ResponsesLiveTests
{
    private static readonly string? Key = Environment.GetEnvironmentVariable("BB_RESPONSES_KEY");
    private static readonly string Url = Environment.GetEnvironmentVariable("BB_RESPONSES_URL") ?? "https://api.proxyapi.ru/v1";
    private static readonly string Model = Environment.GetEnvironmentVariable("BB_RESPONSES_MODEL") ?? "openai/gpt-6-sol";

    [Fact]
    public async Task The_model_thinks_calls_a_tool_and_keeps_its_thinking()
    {
        Assert.SkipWhen(Key is null, "Set BB_RESPONSES_KEY to run.");
        var server = new ServerSettings { BaseUrl = Url, Model = Model, ApiKey = Key! };
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(5));
        var connection = BackendConnection.Create(http, server);
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "record", Description = "Records a number and says the time.",
                Parameters = JsonNode.Parse("""{"type":"object","properties":{"n":{"type":"integer"},"note":{"type":"string"}},"required":["n"]}""")!.AsObject(),
            },
        };
        List<ChatMessage> history = [ChatMessage.User("Work out carefully, step by step in your head, how many positive integers below 10000 have digits summing to 25, then call record with n = that number. After that tell me the time.")];
        var first = await connection.Client.CompleteAsync(history, tools, connection.Options, null, TestContext.Current.CancellationToken);
        Assert.True(first.HasToolCalls, first.Content);
        Assert.Contains("348", first.ToolCalls![0].Arguments);
        Assert.NotNull(first.ReasoningItems);
        TestContext.Current.SendDiagnosticMessage($"thinking shown: {first.Reasoning}; {first.CompletionTokens} tokens out");
        history.Add(first);
        history.Add(ChatMessage.ToolResult(first.ToolCalls[0].Id, "14:52"));

        var second = await connection.Client.CompleteAsync(history, tools, connection.Options, null, TestContext.Current.CancellationToken);
        Assert.Contains("14:52", second.Content);

        // The thinking went back: the prompt is longer by it than one without it.
        var bare = history.Select(m => m.Role != ChatRole.Assistant ? m : new ChatMessage { Role = m.Role, Content = m.Content, ToolCalls = m.ToolCalls }).ToList();
        var without = await connection.Client.CompleteAsync(bare, tools, connection.Options, null, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"prompt with thinking {second.PromptTokens}, without {without.PromptTokens}");
        Assert.True(second.PromptTokens > without.PromptTokens, $"{second.PromptTokens} vs {without.PromptTokens}");
    }
}
