using System.Globalization;
using System.Text.Json.Nodes;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Tools;

/// <summary>
/// Keeps what a model costs on the server Sloperator runs on, per million tokens, as the prices the
/// Cost estimates panel prices its tokens with (a cost the server reports still wins). Auto pricing
/// lookup calls it with what its subagent found, as a card of its own after the subagent's (the user,
/// 2026-11-02); the agent has it too, for prices the user asks it to find or gives it.
/// </summary>
public sealed class SetApiPricingTool : Tool
{
    public override string Name => "set_api_pricing";
    public override string Description =>
        "Keep what a model costs on the server Sloperator runs on, per million tokens, for Sloperator's estimate of what chats cost. " +
        "Use it when the user asks you to find or set those prices; give each price you found, and leave out one you didn't.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("model", Str("The model's id as the server names it; the model in use when left out"), false),
        ("input", Num("Price of a million prompt tokens"), false),
        ("cached_input", Num("Price of a million prompt tokens read from the server's cache"), false),
        ("cache_write", Num("Price of a million prompt tokens written to the server's cache"), false),
        ("output", Num("Price of a million reply tokens"), false),
        ("currency", Str("The currency the prices are in, e.g. '$', '€', '₽' (default '$')"), false));

    public override string Describe(JsonObject args) => $"Set the prices of {GetString(args, "model") ?? "the model in use"}";

    /// <summary>The prices in the arguments, for the server and model given.</summary>
    public static ModelPrices From(JsonObject args, string baseUrl, string model) => new()
    {
        BaseUrl = baseUrl,
        Model = GetString(args, "model") is { Length: > 0 } named ? named : model,
        Input = Price(args, "input"),
        CachedInput = Price(args, "cached_input"),
        CacheWrite = Price(args, "cache_write"),
        Output = Price(args, "output"),
        Currency = GetString(args, "currency") is { Length: > 0 } unit ? unit.Trim() : "$",
    };

    /// <summary>The arguments that set <paramref name="prices"/>: what Auto pricing lookup calls the tool with.</summary>
    public static JsonObject Arguments(ModelPrices prices)
    {
        var args = new JsonObject { ["model"] = prices.Model };
        if (prices.Input is { } input) args["input"] = input;
        if (prices.CachedInput is { } cached) args["cached_input"] = cached;
        if (prices.CacheWrite is { } write) args["cache_write"] = write;
        if (prices.Output is { } output) args["output"] = output;
        args["currency"] = prices.Currency;
        return args;
    }

    private static decimal? Price(JsonObject args, string key) =>
        args[key] is JsonValue v ? v.TryGetValue<decimal>(out var d) && d >= 0 ? d : v.TryGetValue<string>(out var s) ? ModelPrices.Parse(s) : null : null;

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.SetPrices is not { } keep || ctx.Client is not { } client)
            return Task.FromResult(ToolResult.Error("Prices can't be kept here."));
        var prices = From(args, client.BaseUrl, ctx.Options?.Model ?? "");
        if (prices.Model.Length == 0 || !prices.IsSet)
            return Task.FromResult(ToolResult.Error("Give the model and at least one price."));
        keep(prices);
        var host = Uri.TryCreate(prices.BaseUrl, UriKind.Absolute, out var uri) ? uri.Authority : prices.BaseUrl;
        string Show(decimal? p) => p is { } v ? $"{v.ToString(CultureInfo.InvariantCulture)} {prices.Currency}" : "not given";
        return Task.FromResult(new ToolResult(
            $"Kept for {prices.Model} on {host}, per million tokens: input {Show(prices.Input)}, cached input {Show(prices.CachedInput)}, " +
            $"cache write {Show(prices.CacheWrite)}, output {Show(prices.Output)}."));
    }
}
