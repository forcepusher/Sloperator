using System.Globalization;
using System.Text.Json.Nodes;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Tools;

/// <summary>
/// Keeps what a model costs on the server the run is on, per million tokens, as the prices the Cost
/// estimates panel prices its tokens with (a cost the server reports still wins). The model calls it
/// after looking the prices up, in Auto pricing lookup's side task (see <see cref="Agent.AgentSession.PriceLookup"/>)
/// or the panel's Look up prices asks (the user, 2026-11-02). Offered where the app keeps prices.
/// </summary>
public sealed class SetApiPricingTool : Tool
{
    public override string Name => "set_api_pricing";
    public override string Description =>
        "Keep what a model costs on the server Sloperator runs on, per million tokens, for Sloperator's estimate of what chats cost. " +
        "Use it when asked to look up or set those prices; give each price you found, and leave out one you didn't.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("model", Str("The model's id as the server names it; the model in use when left out"), false),
        ("input", Num("Price of a million prompt tokens"), false),
        ("cached_input", Num("Price of a million prompt tokens read from the server's cache"), false),
        ("cache_write", Num("Price of a million prompt tokens written to the server's cache"), false),
        ("output", Num("Price of a million reply tokens"), false),
        ("currency", Str("The currency the prices are in, e.g. '$', '€', '₽' (default '$')"), false));

    public override string Describe(JsonObject args) => $"Set the prices of {GetString(args, "model") ?? "the model in use"}";

    /// <summary>
    /// What the model is asked to do, by the side task and by the panel: find the prices of
    /// <paramref name="model"/> on <paramref name="baseUrl"/> on the web, then keep them with this tool.
    /// </summary>
    public static string LookupRequest(string model, string baseUrl) =>
        $"Sloperator has no prices for the model {model} on the server at {baseUrl}, which doesn't report what replies cost. " +
        "Look them up on the web (web_search, then fetch_page): that server's own prices per million tokens for input, cached input (cache read), " +
        "cache write and output, in the currency it bills in. Where they depend on the prompt's length (one rate up to some number of tokens, " +
        "another above it), give the rates for long prompts, the higher ones: an agent's prompts grow past such limits. " +
        "A service that resells another maker's model charges its own prices; " +
        "a model the user runs on their own PC or network, or one the server gives for free (\"free\" in its id), costs nothing: " +
        "call set_api_pricing with 0 for all four, without searching. " +
        "Otherwise call set_api_pricing with what you found, leaving out a price you didn't find, and look no further.";

    /// <summary>The prices in the arguments; no server, and no model where they name none (see <see cref="ModelPrices.With"/>).</summary>
    public static ModelPrices From(JsonObject args) => new()
    {
        Model = GetString(args, "model")?.Trim() ?? "",
        Input = Price(args, "input"),
        CachedInput = Price(args, "cached_input"),
        CacheWrite = Price(args, "cache_write"),
        Output = Price(args, "output"),
        Currency = GetString(args, "currency") is { Length: > 0 } unit ? unit.Trim() : "$",
    };

    private static decimal? Price(JsonObject args, string key) =>
        args[key] is JsonValue v ? v.TryGetValue<decimal>(out var d) ? d >= 0 ? d : null : v.TryGetValue<string>(out var s) ? ModelPrices.Parse(s) : null : null;

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.SetPrices is not { } keep)
            return Task.FromResult(ToolResult.Error("Prices can't be kept here."));
        var prices = From(args);
        if (!prices.IsSet)
            return Task.FromResult(ToolResult.Error("Give at least one price."));
        keep(prices);
        string Show(decimal? p) => p is { } v ? $"{v.ToString(CultureInfo.InvariantCulture)} {prices.Currency}" : "not given";
        return Task.FromResult(new ToolResult(
            $"Kept for {(prices.Model.Length > 0 ? prices.Model : "the model in use")}, per million tokens: input {Show(prices.Input)}, " +
            $"cached input {Show(prices.CachedInput)}, cache write {Show(prices.CacheWrite)}, output {Show(prices.Output)}."));
    }
}
