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
            var count = Models.Find(m => m.BaseUrl == baseUrl && m.Model == model);
            if (count is null)
                Models.Add(count = new TokenCount { BaseUrl = baseUrl, Model = model });
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
    /// What the chat cost: by what its server said where it says (OpenRouter, Kilo), else by
    /// the prices typed in Settings for the model. Null when a model has neither, or the prices are in more than
    /// one currency. <c>Estimate</c> when it comes from typed prices or not every reply had a cost.
    /// </summary>
    public (decimal Amount, string Currency, bool Estimate)? Total(CostSettings settings)
    {
        lock (_gate)
        {
            if (Models.Count == 0)
                return null;
            decimal amount = 0;
            string? currency = null;
            var estimate = false;
            foreach (var count in Models)
            {
                string each;
                if (count.Costed > 0)
                {
                    amount += count.Cost;
                    each = "$";
                    estimate |= count.Costed < count.Replies;
                }
                else if (settings.For(count.BaseUrl, count.Model) is { } prices)
                {
                    amount += prices.Of(count);
                    each = prices.Currency;
                    estimate = true;
                }
                else
                    return null;
                if (currency is not null && currency != each)
                    return null;
                currency = each;
            }
            return (amount, currency!, estimate);
        }
    }

    /// <summary>"$0.08", "~$0.0042", "~12.50 ₽": a cent's fraction is shown while the total is under a dollar.</summary>
    public static string Format(decimal amount, string currency, bool estimate)
    {
        var number = amount.ToString(amount < 1 ? "0.00##" : "0.00", CultureInfo.InvariantCulture);
        var text = currency.Trim() is { Length: 1 } sign && "$€£¥".Contains(sign[0]) ? sign + number : $"{number} {currency.Trim()}".Trim();
        return estimate ? "~" + text : text;
    }

    /// <summary>The tokens behind the total, for its tooltip.</summary>
    public string Describe()
    {
        lock (_gate)
            return string.Join("\n", Models.Select(m =>
                $"{m.Model}: {Tokens(m.Input)} in, {Tokens(m.CacheRead)} from the cache, {Tokens(m.CacheWrite)} into it, {Tokens(m.Output)} out ({m.Replies} {(m.Replies == 1 ? "reply" : "replies")})"));
    }

    private static string Tokens(long n) => n < 1000 ? n.ToString(CultureInfo.InvariantCulture) : (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
}
