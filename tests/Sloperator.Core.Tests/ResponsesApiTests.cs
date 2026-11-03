using System.Net;
using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>A model whose server says to use its Responses API (/v1/responses) goes there, thinking and tools both.</summary>
public class ResponsesApiTests
{
    // As proxyapi.ru refused openai/gpt-6-sol with tools on 2026-11-03, with or without reasoning_effort.
    private const string SolRefusal = """{"error": {"message": "Function tools with reasoning_effort are not supported for gpt-6-sol in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.", "type": "invalid_request_error", "param": null, "code": null}}""";

    private static readonly List<ToolDefinition> Tools =
    [
        new() { Name = "get_time", Description = "Says the time.", Parameters = JsonNode.Parse("""{"type":"object","properties":{"zone":{"type":"string"}}}""")!.AsObject() },
    ];

    private static string Events(params JsonObject[] events) =>
        string.Concat(events.Select(e => $"event: {e["type"]}\ndata: {e.ToJsonString()}\n\n"));

    private static JsonObject Ev(string type, object? fields = null)
    {
        var e = new JsonObject { ["type"] = type };
        if (fields is not null)
            foreach (var (k, v) in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(fields))!.AsObject().ToList())
                e[k] = v?.DeepClone();
        return e;
    }

    /// <summary>A reply that thinks (a two-part summary), says a word and calls get_time, as OpenAI streams it.</summary>
    private static string ThinkSayCall(string callId = "call_7") => Events(
        Ev("response.created", new { response = new { status = "in_progress" } }),
        Ev("response.output_item.added", new { output_index = 0, item = new { type = "reasoning", id = "rs_1" } }),
        Ev("response.reasoning_summary_part.added", new { output_index = 0, summary_index = 0 }),
        Ev("response.reasoning_summary_text.delta", new { output_index = 0, delta = "Need the " }),
        Ev("response.reasoning_summary_text.delta", new { output_index = 0, delta = "time." }),
        Ev("response.reasoning_summary_part.added", new { output_index = 0, summary_index = 1 }),
        Ev("response.reasoning_summary_text.delta", new { output_index = 0, delta = "Call the tool." }),
        Ev("response.output_item.done", new { output_index = 0, item = new { type = "reasoning", id = "rs_1", status = "completed", encrypted_content = "ZW5jcnlwdGVk", summary = new[] { new { type = "summary_text", text = "Need the time." } } } }),
        Ev("response.output_item.added", new { output_index = 1, item = new { type = "message", role = "assistant" } }),
        Ev("response.output_text.delta", new { output_index = 1, delta = "Checking." }),
        Ev("response.output_item.added", new { output_index = 2, item = new { type = "function_call", call_id = callId, name = "get_time", arguments = "" } }),
        Ev("response.function_call_arguments.delta", new { output_index = 2, delta = "{\"zone\":" }),
        Ev("response.function_call_arguments.delta", new { output_index = 2, delta = "\"UTC\"}" }),
        Ev("response.output_item.done", new { output_index = 2, item = new { type = "function_call", call_id = callId, name = "get_time", arguments = "{\"zone\":\"UTC\"}" } }),
        Ev("response.completed", new { response = new { status = "completed", usage = new { input_tokens = 120, output_tokens = 40, input_tokens_details = new { cached_tokens = 100 }, output_tokens_details = new { reasoning_tokens = 30 } } } }));

    private static string Says(string text) => Events(
        Ev("response.output_text.delta", new { output_index = 0, delta = text }),
        Ev("response.completed", new { response = new { status = "completed" } }));

    [Theory]
    [InlineData("Function tools with reasoning_effort are not supported for gpt-6-sol in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.", true)]
    [InlineData("This model is only supported in /responses.", true)]
    [InlineData("Too many responses in flight, try again.", false)]
    [InlineData("See https://example.com/docs/responses/limits for limits.", false)]
    public void An_error_naming_the_responses_endpoint_sends_the_request_there(string said, bool asks) =>
        Assert.Equal(asks, OpenAiChatClient.AsksForResponses(said));

    [Fact]
    public async Task A_refusal_naming_responses_switches_the_model_there_for_good()
    {
        var server = new ScriptedServer(ThinkSayCall(), Says("It is noon."));
        server.Failures.Enqueue((HttpStatusCode.BadRequest, SolRefusal));
        var client = new OpenAiChatClient(new HttpClient(server), $"http://sol-{Guid.NewGuid():N}", "key");
        var options = new ChatRequestOptions { Model = "openai/gpt-6-sol", SendReasoning = true };
        var deltas = new List<StreamDelta>();

        List<ChatMessage> history = [ChatMessage.System("Be brief."), ChatMessage.User("What time is it?")];
        var first = await client.CompleteAsync(history, Tools, options, deltas.Add, CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
        Assert.NotNull(server.Requests[0]["messages"]);
        Assert.NotNull(server.Requests[1]["input"]);
        Assert.Equal("Checking.", first.Content);
        Assert.Equal("Need the time.\n\nCall the tool.", first.Reasoning);
        Assert.Equal("Need the time.\n\nCall the tool.", string.Concat(deltas.Where(d => d.Channel == StreamChannel.Reasoning).Select(d => d.Text)));
        var call = Assert.Single(first.ToolCalls!);
        Assert.Equal(("call_7", "get_time", """{"zone":"UTC"}"""), (call.Id, call.Name, call.Arguments));
        Assert.Equal("tool_calls", first.FinishReason);
        Assert.Equal((120, 40, 100), (first.PromptTokens, first.CompletionTokens, first.CachedTokens));
        var item = Assert.Single(first.ReasoningItems!)!;
        Assert.Equal("ZW5jcnlwdGVk", (string)item["encrypted_content"]!);
        Assert.Null(item["status"]);

        // The next step goes straight to /v1/responses, the thinking sent back ahead of the call.
        history.Add(first);
        history.Add(ChatMessage.ToolResult("call_7", "12:00"));
        var second = await client.CompleteAsync(history, Tools, options, null, CancellationToken.None);
        Assert.Equal("It is noon.", second.Content);
        Assert.Equal("stop", second.FinishReason);
        Assert.Equal(3, server.Requests.Count);
        var input = server.Requests[2]["input"]!.AsArray();
        Assert.Equal(["message", "message", "reasoning", "message", "function_call", "function_call_output"], input.Select(i => (string?)i!["type"]));
        Assert.Equal("rs_1", (string)input[2]!["id"]!);
        Assert.Equal("12:00", (string)input[5]!["output"]!);
        Assert.Equal("call_7", (string)input[5]!["call_id"]!);
    }

    [Fact]
    public void The_request_carries_pictures_calls_and_thinking_settings()
    {
        var shot = new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("Here:"), ContentPart.FromImage([1, 2, 3], "image/png")] };
        var reply = new ChatMessage
        {
            Role = ChatRole.Assistant, Content = "",
            ToolCalls = [new ToolCall { Id = "c1", Name = "get_time", Arguments = "" }],
            ReasoningItems = new JsonArray(new JsonObject { ["type"] = "reasoning", ["id"] = "rs_9", ["encrypted_content"] = "eA==" }),
        };
        var req = OpenAiChatClient.BuildResponsesRequest([ChatMessage.System("Sys"), shot, reply, ChatMessage.ToolResult("c1", "noon")], Tools,
            new ChatRequestOptions { Model = "m", Effort = "high", MaxTokens = 4000 }, sendReasoning: true);

        Assert.False((bool)req["store"]!);
        Assert.Equal("reasoning.encrypted_content", (string)req["include"]![0]!);
        Assert.Equal("high", (string)req["reasoning"]!["effort"]!);
        Assert.Equal("auto", (string)req["reasoning"]!["summary"]!);
        Assert.Equal(4000, (int)req["max_output_tokens"]!);
        var tool = req["tools"]![0]!;
        Assert.Equal(("function", "get_time", false), ((string)tool["type"]!, (string)tool["name"]!, (bool)tool["strict"]!));
        var input = req["input"]!.AsArray();
        Assert.Equal("system", (string)input[0]!["role"]!);
        Assert.Equal("input_image", (string)input[1]!["content"]![1]!["type"]!);
        Assert.Equal("data:image/png;base64,AQID", (string)input[1]!["content"]![1]!["image_url"]!);
        // A turn of calls alone has no message item; its arguments are never empty.
        Assert.Equal(["message", "message", "reasoning", "function_call", "function_call_output"], input.Select(i => (string?)i!["type"]));
        Assert.Equal("{}", (string)input[3]!["arguments"]!);

        // Without SendReasoning, or once the server refused the encrypted thinking, none goes back.
        Assert.DoesNotContain(OpenAiChatClient.BuildResponsesRequest([reply], [], new ChatRequestOptions())["input"]!.AsArray(), i => (string?)i!["type"] == "reasoning");
        var plain = OpenAiChatClient.BuildResponsesRequest([reply], [], new ChatRequestOptions(), sendReasoning: true, refused: new HashSet<string> { "include", "summary" });
        Assert.Null(plain["include"]);
        Assert.Null(plain["reasoning"]);
        Assert.DoesNotContain(plain["input"]!.AsArray(), i => (string?)i!["type"] == "reasoning");
    }

    [Theory]
    [InlineData("Unsupported parameter: 'reasoning.summary' is not supported with this model.", "summary")]
    [InlineData("Unknown parameter: 'include'.", "include")]
    [InlineData("reasoning.encrypted_content is only available with store=false", "include")]
    [InlineData("Unsupported parameter: 'store'.", "store")]
    [InlineData("Unsupported parameter: 'reasoning.effort' is not supported with this model.", "reasoning")]
    [InlineData("Invalid value for 'input'.", null)]
    public void A_part_the_server_refuses_by_name_is_found(string said, string? field)
    {
        var req = OpenAiChatClient.BuildResponsesRequest([ChatMessage.User("hi")], [], new ChatRequestOptions { Effort = "low" });
        Assert.Equal(field, OpenAiChatClient.RefusedResponsesField(req, said));
    }

    [Fact]
    public async Task Refused_parts_and_refused_thinking_are_left_out_and_sent_again()
    {
        var server = new ScriptedServer(Says("ok"), Says("ok"));
        server.Failures.Enqueue((HttpStatusCode.BadRequest, SolRefusal));
        server.Failures.Enqueue((HttpStatusCode.BadRequest, """{"error":{"message":"Unsupported parameter: 'reasoning.summary' is not supported with this model."}}"""));
        server.Failures.Enqueue((HttpStatusCode.BadRequest, """{"error":{"message":"The encrypted content for item rs_9 could not be verified."}}"""));
        var client = new OpenAiChatClient(new HttpClient(server), $"http://parts-{Guid.NewGuid():N}", "key");
        var reply = new ChatMessage
        {
            Role = ChatRole.Assistant, Content = "Hi.",
            ReasoningItems = new JsonArray(new JsonObject { ["type"] = "reasoning", ["id"] = "rs_9", ["encrypted_content"] = "eA==" }),
        };
        var options = new ChatRequestOptions { Model = "m", SendReasoning = true };

        Assert.Equal("ok", (await client.CompleteAsync([ChatMessage.User("hi"), reply, ChatMessage.User("again")], [], options, null, CancellationToken.None)).Content);

        Assert.Equal(4, server.Requests.Count);
        Assert.Equal("auto", (string)server.Requests[1]["reasoning"]!["summary"]!);
        Assert.Null(server.Requests[2]["reasoning"]?["summary"]);
        Assert.Contains(server.Requests[2]["input"]!.AsArray(), i => (string?)i!["type"] == "reasoning");
        Assert.DoesNotContain(server.Requests[3]["input"]!.AsArray(), i => (string?)i!["type"] == "reasoning");
        // The summary stays left out for the next step of the run, and the next run.
        await new OpenAiChatClient(new HttpClient(server), client.BaseUrl, "key").CompleteAsync([ChatMessage.User("hi")], [], options, null, CancellationToken.None);
        Assert.Null(server.Requests[4]["reasoning"]?["summary"]);
        Assert.NotNull(server.Requests[4]["input"]);
    }

    [Fact]
    public async Task A_reply_out_of_room_says_so_and_a_cut_stream_is_a_drop()
    {
        var cut = await OpenAiChatClient.ReadResponsesStreamAsync(new StringReader(Events(
            Ev("response.output_text.delta", new { output_index = 0, delta = "Half a" }),
            Ev("response.incomplete", new { response = new { status = "incomplete", incomplete_details = new { reason = "max_output_tokens" } } }))), null, CancellationToken.None);
        Assert.Equal(("Half a", "length"), (cut.Content, cut.FinishReason));

        await Assert.ThrowsAsync<IOException>(() => OpenAiChatClient.ReadResponsesStreamAsync(new StringReader(Events(
            Ev("response.output_text.delta", new { output_index = 0, delta = "Half a" }))), null, CancellationToken.None));

        var failed = Events(Ev("response.failed", new { response = new { status = "failed", error = new { code = "server_error", message = "The server had an error while processing your request." } } }));
        await Assert.ThrowsAsync<ConnectionLostException>(() => OpenAiChatClient.ReadResponsesStreamAsync(new StringReader(failed), null, CancellationToken.None));

        var overflow = Events(Ev("error", new { code = "context_length_exceeded", message = "Your input exceeds the context window of this model." }));
        await Assert.ThrowsAsync<ContextOverflowException>(() => OpenAiChatClient.ReadResponsesStreamAsync(new StringReader(overflow), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_server_that_sends_only_the_whole_reply_is_read_from_it()
    {
        var whole = Events(Ev("response.completed", new
        {
            response = new
            {
                status = "completed",
                output = new object[]
                {
                    new { type = "reasoning", id = "rs_2", encrypted_content = "eQ==", summary = new[] { new { type = "summary_text", text = "Short." } } },
                    new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Done." } } },
                    new { type = "function_call", call_id = "c3", name = "get_time", arguments = "{}" },
                },
            },
        }));
        var msg = await OpenAiChatClient.ReadResponsesStreamAsync(new StringReader(whole), null, CancellationToken.None);
        Assert.Equal(("Done.", "Short."), (msg.Content, msg.Reasoning));
        Assert.Equal("c3", Assert.Single(msg.ToolCalls!).Id);
        Assert.Equal("rs_2", (string)Assert.Single(msg.ReasoningItems!)!["id"]!);
    }
}
