using System.Globalization;
using System.Text.Json.Serialization;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Llm;

/// <summary>The tokens a chat sent to one model on one server, and what that server said they cost.</summary>
public sealed class TokenCount
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>Prompt tokens read anew: neither from the prompt cache nor written to it.</summary>
    public long Input { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public long Output { get; set; }

    public int Replies { get; set; }

    /// <summary>Of <see cref="Replies"/>, those whose server said what they cost; <see cref="Cost"/> is their sum, in dollars.</summary>
    public int Costed { get; set; }
    public decimal Cost { get; set; }

    public TokenCount Copy() => (TokenCount)MemberwiseClone();
}

/// <summary>What one model's tokens cost, in <see cref="Currency"/>. <c>CacheMissing</c>: its cache tokens have no price, so they aren't in <see cref="Amount"/>.</summary>
public readonly record struct ModelCost(decimal Amount, string Currency, bool Estimate, bool CacheMissing = false);

/// <summary>A chat's total in one currency. <c>Unpriced</c> counts the models with no cost at all, which it can't include.</summary>
public sealed record ChatTotal(decimal Amount, string Currency, bool Estimate, bool CacheMissing, int Unpriced)
{
    /// <summary>More was spent than <see cref="Amount"/> says.</summary>
    public bool Incomplete => CacheMissing || Unpriced > 0;

    public string Shown => Spending.Format(Amount, Currency, Estimate, Incomplete);

    /// <summary>What the total lacks, in a sentence or two; empty when nothing.</summary>
    public string Note => string.Join(" ", new[]
    {
        CacheMissing ? "Cache prices missing: cache tokens aren't counted." : "",
        Unpriced > 0 ? $"{Unpriced} {(Unpriced == 1 ? "model has" : "models have")} no prices." : "",
    }.Where(line => line.Length > 0));
}

/// <summary>
/// What a chat has spent: every reply's tokens (the agent's steps, handoff notes, second looks),
/// by server and model, saved with the chat. Kept apart from the conversation, which a context
/// reset replaces. Added to on the agent's thread and read on the window's.
/// </summary>
public sealed class Spending
{
    private readonly Lock _gate = new();

    public List<TokenCount> Models { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty { get { lock (_gate) return Models.Count == 0; } }

    /// <summary>Counts a reply. Servers that speak OpenAI's format count cached tokens in the prompt; Anthropic's replies are given the same shape (see <see cref="AnthropicMessages"/>).</summary>
    public void Add(string baseUrl, string model, ChatMessage reply)
    {
        if (reply.PromptTokens is null && reply.CompletionTokens is null && reply.Cost is null)
            return;
        lock (_gate)
        {
            var count = Models.Find(m => m.BaseUrl == baseUrl && m.Model == model) ?? new TokenCount { BaseUrl = baseUrl, Model = model };
            // The model used last stays last: the total goes by its currency.
            Models.Remove(count);
            Models.Add(count);
            var read = reply.CachedTokens ?? 0;
            var written = reply.CacheWriteTokens ?? 0;
            count.Input += Math.Max(0, (reply.PromptTokens ?? 0) - read - written);
            count.CacheRead += read;
            count.CacheWrite += written;
            count.Output += reply.CompletionTokens ?? 0;
            count.Replies++;
            if (reply.Cost is { } cost)
            {
                count.Costed++;
                count.Cost += cost;
            }
        }
    }

    public Spending Copy()
    {
        lock (_gate)
            return new Spending { Models = Models.Select(m => m.Copy()).ToList() };
    }

    /// <summary>
    /// The models that count: those in <paramref name="currency"/>, by default that of the last used
    /// model with a cost, and those with no cost yet. Spending in any other currency is dropped
    /// (the user, 2026-11-03: sums don't cross currencies, and what came before a change doesn't matter).
    /// </summary>
    public List<TokenCount> Counted(CostSettings settings, string? currency = null)
    {
        lock (_gate)
        {
            currency ??= Models.Select(count => CostOf(count, settings)).LastOrDefault(cost => cost is not null)?.Currency;
            return Models.Where(count => CostOf(count, settings) is not { } cost || cost.Currency == currency).ToList();
        }
    }

    /// <summary>
    /// What the chat cost in the currency of <see cref="Counted"/>: by what its server said where it
    /// says (OpenRouter, Kilo), else by the prices typed for the model in the Cost estimates panel.
    /// Null when no model is in it. <c>Estimate</c> when it comes from typed prices or not every reply had a cost.
    /// </summary>
    public ChatTotal? Total(CostSettings settings, string? currency = null)
    {
        var costs = Counted(settings, currency).Select(count => CostOf(count, settings)).ToList();
        var priced = costs.Where(cost => cost is not null).Select(cost => cost!.Value).ToList();
        return priced.Count == 0 ? null
            : new ChatTotal(priced.Sum(c => c.Amount), priced[0].Currency, priced.Any(c => c.Estimate), priced.Any(c => c.CacheMissing), costs.Count - priced.Count);
    }

    /// <summary>What one model's tokens cost: what its server said where it says, else by the prices typed for it; null with neither.</summary>
    public static ModelCost? CostOf(TokenCount count, CostSettings settings) =>
        count.Costed > 0 ? new ModelCost(count.Cost, "$", count.Costed < count.Replies)
        : settings.For(count.BaseUrl, count.Model) is { } prices ? new ModelCost(prices.Of(count), prices.Currency, true, prices.LacksCache(count))
        : null;

    /// <summary>"$0.08", "~$0.0042", "~12.50 ₽", and "~12.50 ₽+" when more was spent than it says: a cent's fraction is shown while the total is under a dollar.</summary>
    public static string Format(decimal amount, string currency, bool estimate, bool incomplete = false)
    {
        var number = amount.ToString(amount < 1 ? "0.00##" : "0.00", CultureInfo.InvariantCulture);
        var text = currency.Trim() is { Length: 1 } sign && "$€£¥".Contains(sign[0]) ? sign + number : $"{number} {currency.Trim()}".Trim();
        return (estimate ? "~" : "") + text + (incomplete ? "+" : "");
    }

    /// <summary>The tokens behind the total, for its tooltip.</summary>
    public string Describe(CostSettings settings) =>
        string.Join("\n", Counted(settings).Select(m =>
            $"{m.Model}: {Tokens(m.Input)} in, {Tokens(m.CacheRead)} from the cache, {Tokens(m.CacheWrite)} into it, {Tokens(m.Output)} out ({m.Replies} {(m.Replies == 1 ? "reply" : "replies")})"));

    private static string Tokens(long n) => n < 1000 ? n.ToString(CultureInfo.InvariantCulture) : (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
}
