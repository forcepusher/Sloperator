using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Llm;

// OpenAI's Responses API (/v1/responses), for a model whose server says to use it: the same calls
// as for any server, so the agent, fresh looks and model questions go through it unchanged.
public sealed partial class OpenAiChatClient
{
    /// <summary>
    /// The models whose server said, refusing a chat/completions request, that it goes to its
    /// Responses API, by server and model, for as long as Sloperator runs: proxyapi.ru's
    /// openai/gpt-6-sol takes tools there only without thinking ("Function tools with
    /// reasoning_effort are not supported for gpt-6-sol in /v1/chat/completions. To use function
    /// tools, use /v1/responses or set reasoning_effort to 'none'.", even with no effort sent,
    /// since it thinks by default), and on /v1/responses with both (tried 2026-11-03).
    /// </summary>
    private static readonly ConcurrentDictionary<(string Server, string Model), bool> ResponsesRoutes = new();

    /// <summary>An error that sends the request to the Responses API: one naming <c>/responses</c> or <c>/v1/responses</c>.</summary>
    internal static bool AsksForResponses(string said) => ResponsesNamed().IsMatch(said);

    [GeneratedRegex(@"/responses(?![\w/])", RegexOptions.IgnoreCase)]
    private static partial Regex ResponsesNamed();

    private async Task<ChatMessage> CompleteResponsesAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ChatRequestOptions options,
        Action<StreamDelta>? onDelta, CancellationToken ct, Action<long, long>? onSending)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sendReasoning = options.SendReasoning && !_reasoningRejected;
            var request = BuildResponsesRequest(messages, tools, WithTakenEffort(options), sendReasoning, PromptShifts.GetValueOrDefault(_baseUrl),
                RefusedByServer.GetValueOrDefault((_baseUrl, options.Model)), ReplyCaps.TryGetValue((_baseUrl, options.Model), out var cap) ? cap : null);
            var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var upload = new UploadContent(payload, stall, UploadStallLimit, onSending);
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/responses") { Content = upload };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            Authorize(req);

            using (var resp = await SendAsync(req, upload, stall, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var err = await ErrorBodyAsync(resp, ct).ConfigureAwait(false);
                    if (SizeRefusal(status, err, messages, payload.Length) is { } sized)
                        throw sized;
                    var said = ErrorText(err);
                    if (status is 400 or 422 && LearnEfforts(options.Model, request["reasoning"]?["effort"]?.GetValue<string>(), err))
                        return await CompleteAsync(messages, tools, options, onDelta, ct, onSending).ConfigureAwait(false);
                    if (status is 400 or 422 && attempt < 6)
                    {
                        // Earlier thinking the server can't read back (written for another model, or
                        // its key changed): the run goes on without it.
                        if (sendReasoning && messages.Any(m => m.ReasoningItems is { Count: > 0 }) && ReasoningItemsRefused(said))
                        {
                            _reasoningRejected = true;
                            continue;
                        }
                        // A reply limit over the model's: the one it names, else none.
                        if (request["max_output_tokens"]?.GetValue<int>() is { } asked && said.Contains("max_output_tokens", StringComparison.OrdinalIgnoreCase))
                        {
                            var most = NumberWords().Matches(said).Select(n => int.TryParse(n.Value.Replace(",", ""), out var v) ? v : 0).Where(v => v > 0 && v < asked).DefaultIfEmpty(0).Max();
                            if (most > 0)
                                ReplyCaps[(_baseUrl, options.Model)] = most;
                            else
                                Refuse(options.Model, "max_output_tokens");
                            continue;
                        }
                        // One of the parts Sloperator adds, named in the refusal: left out from then on.
                        if (RefusedResponsesField(request, said) is { } field)
                        {
                            Refuse(options.Model, field);
                            continue;
                        }
                    }
                    throw Failure(status, err, resp.Headers);
                }

                var received = false;
                string? toolCall = null;
                try
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return await ReadResponsesStreamAsync(reader, d => { received = true; onDelta?.Invoke(d); }, ct,
                        onToolCall: name => { received = true; toolCall = name; }).ConfigureAwait(false);
                }
                catch (TooManyImagesException ex) when (ex.Sent == 0)
                {
                    throw new TooManyImagesException(ex.ServerMessage, ImageCount(messages));
                }
                catch (LlmException ex) when (ex is not (RateLimitedException or ContextOverflowException or ImagesRefusedException or TooManyImagesException)
                                              && HasImages(messages) && LooksLikeImagesRefused(ex.Message))
                {
                    throw new ImagesRefusedException(ex.Message.Replace("Server error: ", "").Clip(300));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or HttpRequestException or TaskCanceledException)
                {
                    throw new ConnectionLostException($"The connection to {_baseUrl} dropped: {ex.Message}", received, ex, toolCall);
                }
            }
        }
    }

    private void Refuse(string model, string field) =>
        RefusedByServer.AddOrUpdate((_baseUrl, model),
            _ => System.Collections.Immutable.ImmutableHashSet.Create(StringComparer.Ordinal, field),
            (_, before) => before.Add(field));

    /// <summary>A refusal of the encrypted reasoning sent back: "The encrypted content … could not be verified", an unknown reasoning item "rs_…".</summary>
    private static bool ReasoningItemsRefused(string said) =>
        said.Contains("encrypted", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(said, @"(?<!\w)rs_\w+")
        || said.Contains("reasoning", StringComparison.OrdinalIgnoreCase) && said.Contains("item", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The optional part of a Responses request that a refusal names, of those in it: the thinking
    /// summary, the encrypted thinking asked for (<c>include</c>), <c>store</c>, or the thinking
    /// settings as a whole; null when it names none of them.
    /// </summary>
    internal static string? RefusedResponsesField(JsonObject request, string said)
    {
        bool Named(string word) => Regex.IsMatch(said, $@"(?<![\w]){Regex.Escape(word)}(?![\w])", RegexOptions.IgnoreCase);
        if (request["reasoning"] is JsonObject reasoning && reasoning.ContainsKey("summary") && Named("summary"))
            return "summary";
        if (request.ContainsKey("include") && (Named("include") || said.Contains("encrypted_content", StringComparison.OrdinalIgnoreCase)))
            return "include";
        if (request.ContainsKey("store") && Named("store"))
            return "store";
        if (request.ContainsKey("reasoning") && Named("reasoning"))
            return "reasoning";
        return null;
    }

    /// <summary>
    /// A Responses API request for the conversation: its messages as input items (each reply's
    /// thinking ahead of it, its calls as function_call items and their results as
    /// function_call_output), nothing stored on the server, the thinking asked back encrypted to
    /// send with the next request, and a summary of it to show.
    /// </summary>
    /// <param name="refused">Parts the server refused by name (see <see cref="RefusedResponsesField"/>), left out.</param>
    internal static JsonObject BuildResponsesRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ChatRequestOptions options,
        bool sendReasoning = false, int promptShift = 0, IReadOnlySet<string>? refused = null, int? replyCap = null)
    {
        var input = new JsonArray();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case ChatRole.Assistant:
                    // As on chat/completions, a reply that said nothing and called nothing isn't sent.
                    if (!m.HasToolCalls && m.Parts is not { Count: > 0 } && string.IsNullOrWhiteSpace(m.Content))
                        continue;
                    if (sendReasoning && m.ReasoningItems is { Count: > 0 } items)
                        foreach (var item in items.OfType<JsonObject>())
                            input.Add((JsonNode)item.DeepClone());
                    var said = m.Parts is { Count: > 0 } ? string.Join("\n", m.Parts.Where(p => !p.IsImage).Select(p => p.Text)) : m.Content;
                    if (!string.IsNullOrWhiteSpace(said))
                        input.Add((JsonNode)new JsonObject { ["type"] = "message", ["role"] = ChatRole.Assistant, ["content"] = said });
                    foreach (var c in m.ToolCalls ?? [])
                        input.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = c.Id,
                            ["name"] = c.Name,
                            ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments,
                        });
                    break;
                case ChatRole.Tool:
                    input.Add((JsonNode)new JsonObject { ["type"] = "function_call_output", ["call_id"] = m.ToolCallId ?? "", ["output"] = m.Content ?? "" });
                    break;
                default:
                    var text = promptShift > 0 && input.Count == 0 && m.Role == ChatRole.System && m.Parts is null ? new string('\n', promptShift) + m.Content : m.Content;
                    JsonNode content = m.Parts is { Count: > 0 }
                        ? new JsonArray(m.Parts.Select(p => (JsonNode)(p.IsImage
                            ? new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{p.ImageMime};base64,{Convert.ToBase64String(p.ImageBytes!)}" }
                            : new JsonObject { ["type"] = "input_text", ["text"] = p.Text ?? "" })).ToArray())
                        : JsonValue.Create(text ?? "");
                    input.Add((JsonNode)new JsonObject { ["type"] = "message", ["role"] = m.Role, ["content"] = content });
                    break;
            }
        }

        var req = new JsonObject
        {
            ["model"] = options.Model,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
        };
        if (options.MaxTokens is { } mt)
            req["max_output_tokens"] = replyCap is { } cap ? Math.Min(mt, cap) : mt;
        if (tools.Count > 0)
            // Not strict, which the Responses API takes by default: strict wants every argument
            // required, and Sloperator's tools have optional ones.
            req["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.DeepClone(),
                ["strict"] = false,
            }).ToArray());
        var reasoning = new JsonObject { ["summary"] = "auto" };
        if (options.Effort is { } effort)
            reasoning["effort"] = effort;
        req["reasoning"] = reasoning;

        foreach (var field in refused ?? Enumerable.Empty<string>())
        {
            if (field == "summary")
                reasoning.Remove("summary");
            else
                req.Remove(field);
            // The thinking asked back encrypted is for sending it back.
            if (field is "include" or "reasoning")
                foreach (var item in input.OfType<JsonObject>().Where(i => GetString(i, "type") == "reasoning").ToList())
                    input.Remove(item);
        }
        if (reasoning.Count == 0)
            req.Remove("reasoning");
        return req;
    }

    /// <summary>
    /// Puts a Responses API reply together from its stream of events: the text, the thinking
    /// summary (or the thinking itself, where a server sends it) to show, the tool calls, the
    /// reasoning items to send back, usage and why it stopped.
    /// </summary>
    internal static async Task<ChatMessage> ReadResponsesStreamAsync(TextReader reader, Action<StreamDelta>? onDelta, CancellationToken ct, Action<string>? onToolCall = null)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        var items = new List<JsonObject>();
        JsonObject? done = null;

        void Think(string text)
        {
            reasoning.Append(text);
            onDelta?.Invoke(new StreamDelta(StreamChannel.Reasoning, text));
        }

        // A new part of the summary, or another reasoning item: on a line of its own.
        void NewPart()
        {
            if (reasoning.Length > 0 && reasoning[^1] != '\n')
                Think("\n\n");
        }

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
                break;
            JsonNode? ev;
            try { ev = JsonNode.Parse(data); }
            catch (JsonException) { continue; }
            if (ev is not JsonObject e)
                continue;

            var type = GetString(e, "type");
            var index = GetInt(e, "output_index") ?? 0;
            switch (type)
            {
                case "response.output_text.delta" when GetString(e, "delta") is { Length: > 0 } text:
                    content.Append(text);
                    onDelta?.Invoke(new StreamDelta(StreamChannel.Content, text));
                    break;
                case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta" when GetString(e, "delta") is { Length: > 0 } thought:
                    Think(thought);
                    break;
                case "response.reasoning_summary_part.added":
                    NewPart();
                    break;
                case "response.output_item.added" when e["item"] is JsonObject item:
                    if (GetString(item, "type") == "reasoning")
                        NewPart();
                    else if (GetString(item, "type") == "function_call")
                    {
                        calls[index] = (GetString(item, "call_id") ?? "", GetString(item, "name") ?? "", new StringBuilder(GetString(item, "arguments") ?? ""));
                        if (calls[index].Name.Length > 0)
                            onToolCall?.Invoke(calls[index].Name);
                    }
                    break;
                case "response.function_call_arguments.delta" when GetString(e, "delta") is { } piece:
                    if (!calls.TryGetValue(index, out var open))
                        calls[index] = open = ("", "", new StringBuilder());
                    open.Args.Append(piece);
                    break;
                case "response.output_item.done" when e["item"] is JsonObject item:
                    Finished(item, index);
                    break;
                case "response.completed" or "response.incomplete" or "response.failed":
                    done = e["response"] as JsonObject ?? e;
                    break;
                case "error":
                    var said = GetString(e, "message") ?? (e["error"] as JsonObject)?["message"]?.ToString() ?? e.ToJsonString();
                    throw StreamError(said, GetString(e, "code") ?? (e["error"] as JsonObject)?["code"]?.ToString(), e.ToJsonString(),
                        content.Length > 0 || reasoning.Length > 0 || calls.Count > 0, calls.Values.LastOrDefault().Name is { Length: > 0 } calling ? calling : null);
            }
            if (done is not null)
                break;
        }

        // The whole item, once done: its arguments as the server put them together.
        void Finished(JsonObject item, int at)
        {
            switch (GetString(item, "type"))
            {
                case "function_call":
                    var known = calls.TryGetValue(at, out var had);
                    var name = GetString(item, "name") ?? (known ? had.Name : "");
                    if (!known && name.Length > 0)
                        onToolCall?.Invoke(name);
                    calls[at] = (GetString(item, "call_id") ?? (known ? had.Id : ""), name, new StringBuilder(GetString(item, "arguments") ?? (known ? had.Args.ToString() : "")));
                    break;
                // Kept to send back only with its thinking in it: with nothing stored on the server,
                // an item without it is refused.
                case "reasoning" when GetString(item, "encrypted_content") is { Length: > 0 }:
                    var kept = (JsonObject)item.DeepClone();
                    kept.Remove("status");
                    items.Add(kept);
                    break;
            }
        }

        // A stream that just stops, before the server said the reply is done, was cut by the network.
        if (done is null)
            throw new IOException("The reply stream ended before the server finished.");
        var status = GetString(done, "status");
        if (status == "failed" && done["error"] is JsonObject failure)
            throw StreamError(GetString(failure, "message") ?? failure.ToJsonString(), GetString(failure, "code"), failure.ToJsonString(),
                content.Length > 0 || reasoning.Length > 0 || calls.Count > 0, null);

        // A server that sent no events for the parts gives them with the whole reply.
        if (content.Length == 0 && calls.Count == 0 && items.Count == 0 && done["output"] is JsonArray output)
        {
            var thoughtBefore = reasoning.Length > 0;
            foreach (var (item, at) in output.OfType<JsonObject>().Select((item, at) => (item, at)))
            {
                if (GetString(item, "type") == "message")
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>().Where(p => GetString(p, "type") == "output_text"))
                        content.Append(GetString(part, "text"));
                else if (GetString(item, "type") == "reasoning" && !thoughtBefore)
                    foreach (var part in (item["summary"] as JsonArray ?? []).OfType<JsonObject>())
                        reasoning.Append(reasoning.Length > 0 ? "\n\n" : "").Append(GetString(part, "text"));
                Finished(item, at);
            }
        }

        var usage = done["usage"] as JsonObject;
        // Out of room (max_output_tokens) as "length", as on chat/completions; another reason as it is.
        var cut = (done["incomplete_details"] as JsonObject)?["reason"]?.ToString();
        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = content.ToString(),
            Reasoning = reasoning.Length > 0 ? reasoning.ToString().Trim() : null,
            FinishReason = status == "incomplete" ? cut is null or "max_output_tokens" ? "length" : cut
                : calls.Count > 0 ? "tool_calls" : "stop",
            PromptTokens = usage is null ? null : GetInt(usage, "input_tokens"),
            CompletionTokens = usage is null ? null : GetInt(usage, "output_tokens"),
            CachedTokens = usage?["input_tokens_details"] is JsonObject inputDetails ? GetInt(inputDetails, "cached_tokens") : null,
            CacheWriteTokens = usage?["input_tokens_details"] is JsonObject writeDetails ? GetInt(writeDetails, "cache_write_tokens") : null,
            Cost = usage?["cost"] is JsonValue spent && spent.TryGetValue<decimal>(out var dollars) ? dollars : null,
        };
        if (items.Count > 0)
            message.ReasoningItems = new JsonArray(items.ToArray<JsonNode?>());
        if (calls.Count > 0)
            message.ToolCalls = calls.Values
                .Where(c => c.Name.Length > 0)
                .Select((c, i) => new ToolCall
                {
                    Id = c.Id.Length > 0 ? c.Id : $"call_{Guid.NewGuid():N}"[..16] + i,
                    Name = c.Name,
                    Arguments = c.Args.ToString(),
                })
                .ToList();
        return message;
    }
}
