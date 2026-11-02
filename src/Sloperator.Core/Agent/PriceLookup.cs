using System.Text.Json;
using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

/// <summary>
/// What a model costs on a server, found on the web by the model itself: the Cost estimates panel's
/// Look up prices asks the agent in the chat, and Auto pricing lookup (<see cref="RunAsync"/>) runs the
/// subagent tool in the background, with web_search and fetch_page only, on the same server and model.
/// Both end on a line the prices are read from (<see cref="Read"/>).
/// </summary>
public static class PriceLookup
{
    public const string LinePrefix = "PRICES";

    /// <summary>What the model is asked: where to look, and the line to end on.</summary>
    public static string Instructions(string model, string baseUrl) =>
        $"Look up on the web (web_search, then fetch_page) what the model {model} costs on the server at {baseUrl}, " +
        "per million tokens: input, cached input (cache read), cache write and output, in the currency that server bills in. " +
        "Go by that server's own price page: a service that resells another maker's model charges its own prices. " +
        "A model the user runs on their own PC or network costs nothing: give 0 for all four. " +
        "End your reply with this one line, not in a code block, a number for each price found and null for one not found:\n" +
        $"{LinePrefix} {{\"input\": 3, \"cachedInput\": 0.3, \"cacheWrite\": 3.75, \"output\": 15, \"currency\": \"$\"}}";

    /// <summary>The task's name, as its card shows it: "Look up the prices of qwen3 on localhost:1234".</summary>
    public static string TaskName(string model, string baseUrl) =>
        $"Look up the prices of {model} on {(Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Authority : baseUrl)}";

    /// <summary>The subagent tool's arguments for a lookup: its task and its prompt.</summary>
    public static JsonObject Arguments(string model, string baseUrl) => new() { ["task"] = TaskName(model, baseUrl), ["prompt"] = Instructions(model, baseUrl) };

    /// <summary>
    /// Runs the lookup through the subagent tool, with web_search and fetch_page only, on the server and
    /// model of <paramref name="ctx"/>; <paramref name="ctx"/>'s Progress hears each step. Returns the
    /// prices found (null when the answer had no line that reads) and the whole answer.
    /// </summary>
    public static async Task<(ModelPrices? Prices, string Answer)> RunAsync(ToolContext ctx, string model, CancellationToken ct)
    {
        var answer = await SubagentTool.RunAsync(ctx.Client!, ctx.Options ?? new ChatRequestOptions { Model = model }, Instructions(model, ctx.Client!.BaseUrl),
            [new WebSearchTool(), new FetchPageTool()], ctx, ctx.Progress, ct).ConfigureAwait(false);
        return (Read(answer), answer);
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
