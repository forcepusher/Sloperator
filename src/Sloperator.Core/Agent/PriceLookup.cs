using System.Text.Json;
using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

/// <summary>
/// What a model costs on a server, found on the web by the model itself: the Cost estimates panel's
/// Look up prices asks the agent in the chat, and Auto pricing lookup (<see cref="RunAsync"/>) asks a
/// small agent of its own in the background, with web_search and fetch_page only, on the same server
/// and model. Both end on a line the prices are read from (<see cref="Read"/>).
/// </summary>
public static class PriceLookup
{
    public const string LinePrefix = "PRICES";

    /// <summary>At most this many replies: searches and pages read, then the answer.</summary>
    private const int MaxSteps = 10;

    /// <summary>What the model is asked: where to look, and the line to end on.</summary>
    public static string Instructions(string model, string baseUrl) =>
        $"Look up on the web (web_search, then fetch_page) what the model {model} costs on the server at {baseUrl}, " +
        "per million tokens: input, cached input (cache read), cache write and output, in the currency that server bills in. " +
        "Go by that server's own price page: a service that resells another maker's model charges its own prices. " +
        "A model the user runs on their own PC or network costs nothing: give 0 for all four. " +
        "End your reply with this one line, not in a code block, a number for each price found and null for one not found:\n" +
        $"{LinePrefix} {{\"input\": 3, \"cachedInput\": 0.3, \"cacheWrite\": 3.75, \"output\": 15, \"currency\": \"$\"}}";

    /// <summary>
    /// Runs the lookup: the model searches and reads pages until it answers, <paramref name="progress"/>
    /// hearing each step ("Search the web: …"). Returns the prices found (null when its answer had no
    /// line that reads) and its answer without the line, for the card.
    /// </summary>
    public static async Task<(ModelPrices? Prices, string Answer)> RunAsync(OpenAiChatClient client, string model, ToolContext ctx,
        Action<string> progress, CancellationToken ct)
    {
        Tool[] tools = [new WebSearchTool(), new FetchPageTool()];
        var definitions = tools.Select(t => new ToolDefinition { Name = t.Name, Description = t.Description, Parameters = t.Parameters }).ToList();
        var options = new ChatRequestOptions { Model = model, MaxTokens = 8192 };
        List<ChatMessage> conversation =
        [
            ChatMessage.System("You find prices for Sloperator, a desktop agent, which keeps what each chat costs. Keep searches few and pages short (find=)."),
            ChatMessage.User(Instructions(model, client.BaseUrl)),
        ];
        for (var step = 0; step < MaxSteps; step++)
        {
            // The last step gets no tools: it answers with what it found.
            var reply = await client.CompleteAsync(conversation, step < MaxSteps - 1 ? definitions : [], options, null, ct).ConfigureAwait(false);
            conversation.Add(reply);
            if (reply.ToolCalls is not { Count: > 0 } calls)
            {
                var text = reply.Content ?? "";
                if (text.LastIndexOf("</think>", StringComparison.Ordinal) is var end and >= 0)
                    text = text[(end + "</think>".Length)..];
                return (Read(text), string.Join("\n", text.Split('\n').Where(l => !l.Contains(LinePrefix + " {", StringComparison.Ordinal))).Trim());
            }
            foreach (var call in calls)
            {
                var tool = tools.FirstOrDefault(t => t.Name == call.Name);
                string result;
                try
                {
                    var args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? [];
                    if (tool is null)
                        result = $"There is no tool {call.Name}: use web_search or fetch_page.";
                    else
                    {
                        progress(tool.Describe(args));
                        result = (await tool.ExecuteAsync(args, ctx, ct).ConfigureAwait(false)).Text;
                    }
                }
                catch (Exception ex) when (ex is JsonException or ToolArgumentException)
                {
                    result = "Error: " + ex.Message;
                }
                conversation.Add(ChatMessage.ToolResult(call.Id, result));
            }
        }
        return (null, "");
    }

    /// <summary>The prices in the text's last "PRICES {…}" line; null without one that reads.</summary>
    public static ModelPrices? Read(string text)
    {
        foreach (var line in text.Split('\n').Reverse())
        {
            var at = line.IndexOf(LinePrefix + " {", StringComparison.Ordinal);
            if (at < 0)
                continue;
            try
            {
                if (JsonNode.Parse(line[(at + LinePrefix.Length)..].Trim().Trim('`')) is not JsonObject json)
                    return null;
                decimal? Price(string key) => json[key] is JsonValue v
                    ? v.TryGetValue<decimal>(out var d) ? d : v.TryGetValue<string>(out var s) ? ModelPrices.Parse(s) : null
                    : null;
                var currency = json["currency"] is JsonValue c && c.TryGetValue<string>(out var unit) && unit.Trim().Length > 0 ? unit.Trim() : "$";
                return new ModelPrices { Input = Price("input"), CachedInput = Price("cachedInput"), CacheWrite = Price("cacheWrite"), Output = Price("output"), Currency = currency };
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }
}
