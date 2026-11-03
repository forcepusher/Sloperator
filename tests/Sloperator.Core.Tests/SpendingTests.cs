using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>A chat's tokens and what they cost (Spending), by the server's word or by prices typed in Settings.</summary>
public class SpendingTests
{
    private const string Server = "https://openrouter.ai/api/v1";

    [Fact]
    public async Task A_reply_brings_its_cache_writes_and_cost()
    {
        // OpenRouter's and Kilo's last chunk, as Kilo sent it (2026-11-02), with a cost and cache counts.
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"content":"Hi"}}]}

            data: {"choices":[{"index":0,"delta":{"content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":1200,"completion_tokens":20,"cost":0.0042,"prompt_tokens_details":{"cached_tokens":800,"cache_write_tokens":300}}}

            data: [DONE]

            """;
        var reply = await OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None);
        Assert.Equal(1200, reply.PromptTokens);
        Assert.Equal(800, reply.CachedTokens);
        Assert.Equal(300, reply.CacheWriteTokens);
        Assert.Equal(0.0042m, reply.Cost);
    }

    [Fact]
    public void Cached_tokens_are_taken_out_of_the_prompt_and_priced_apart()
    {
        var spent = new Spending();
        spent.Add(Server, "m", new ChatMessage { PromptTokens = 1_000_000, CachedTokens = 600_000, CacheWriteTokens = 100_000, CompletionTokens = 10_000 });
        var count = Assert.Single(spent.Models);
        Assert.Equal(300_000, count.Input);

        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = Server + "/", Model = "m", Input = 3, CachedInput = 0.3m, CacheWrite = 3.75m, Output = 15 });
        // 0.3M × 3 + 0.6M × 0.3 + 0.1M × 3.75 + 0.01M × 15
        var total = spent.Total(settings)!;
        Assert.Equal((0.9m + 0.18m + 0.375m + 0.15m, "$", true), (total.Amount, total.Currency, total.Estimate));
        Assert.False(total.Incomplete);
        Assert.Equal("", total.Note);
    }

    [Fact]
    public void Tokens_of_an_empty_cache_price_are_left_out_and_marked()
    {
        // What a proxyapi chat came to: 9.6M read from the cache with input and output prices only.
        var spent = new Spending();
        spent.Add(Server, "m", new ChatMessage { PromptTokens = 9_700_000, CachedTokens = 9_600_000, CompletionTokens = 100_000 });
        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = Server, Model = "m", Input = 500, Output = 2500, Currency = "₽" });

        var total = spent.Total(settings)!;
        Assert.Equal(300m, total.Amount); // 0.1M × 500 + 0.1M × 2500, not 9.6M × 500 on top
        Assert.True(total.CacheMissing);
        Assert.Equal("~300.00 ₽+", total.Shown);
        Assert.Contains("Cache prices missing", total.Note);

        // Only the price the tokens need counts: this model wrote nothing to the cache.
        settings.Set(new ModelPrices { BaseUrl = Server, Model = "m", Input = 500, CachedInput = 50, Output = 2500, Currency = "₽" });
        total = spent.Total(settings)!;
        Assert.Equal(780m, total.Amount);
        Assert.Equal("~780.00 ₽", total.Shown);
    }

    [Fact]
    public void Spending_in_other_currencies_than_the_last_used_is_dropped()
    {
        const string Proxy = "https://api.proxyapi.ru/v1", Anthropic = "https://api.anthropic.com/v1";
        var spent = new Spending();
        spent.Add(Proxy, "sonnet", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        spent.Add(Anthropic, "sonnet", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = Proxy, Model = "sonnet", Input = 500, Currency = "₽" });
        settings.Set(new ModelPrices { BaseUrl = Anthropic, Model = "sonnet", Input = 2 });

        var total = spent.Total(settings)!;
        Assert.Equal("~$2.00", total.Shown);
        Assert.Equal("", total.Note);
        Assert.Equal(Anthropic, Assert.Single(spent.Counted(settings)).BaseUrl);
        Assert.DoesNotContain("\n", spent.Describe(settings));

        // The proxy again: its reply makes it the last, whatever the order the models came in.
        spent.Add(Proxy, "sonnet", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        Assert.Equal("~1000.00 ₽", spent.Total(settings)!.Shown);
        Assert.Equal("~$2.00", spent.Total(settings, "$")!.Shown);
        Assert.Null(spent.Total(settings, "€"));
    }

    [Fact]
    public void The_server_cost_counts_where_nothing_is_typed_and_unpriced_models_give_none()
    {
        var spent = new Spending();
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 100, CompletionTokens = 5, Cost = 0.01m });
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 200, CompletionTokens = 5, Cost = 0.02m });
        Assert.Equal("$0.03", spent.Total(new CostSettings())!.Shown);

        // A reply without a cost makes it an estimate.
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 300, CompletionTokens = 5 });
        Assert.True(spent.Total(new CostSettings())!.Estimate);

        // A model with neither cost nor prices can't be counted, and the total says it isn't whole.
        spent.Add("http://localhost:1234/v1", "qwen", new ChatMessage { PromptTokens = 300, CompletionTokens = 5 });
        var total = spent.Total(new CostSettings())!;
        Assert.Equal("~$0.03+", total.Shown);
        Assert.Contains("1 model has no prices", total.Note);

        var none = new Spending();
        none.Add("http://localhost:1234/v1", "qwen", new ChatMessage { PromptTokens = 300, CompletionTokens = 5 });
        Assert.Null(none.Total(new CostSettings()));
    }

    [Fact]
    public void The_server_cost_wins_over_typed_prices()
    {
        var spent = new Spending();
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0, Cost = 0.5m });
        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = Server, Model = "a", Input = 3, Currency = "₽" });
        Assert.Equal("$0.50", spent.Total(settings)!.Shown);
    }

    [Fact]
    public void Prices_with_nothing_typed_are_dropped()
    {
        var settings = new CostSettings();
        var prices = new ModelPrices { BaseUrl = Server, Model = "m", Input = 1 };
        settings.Set(prices);
        Assert.Same(prices, settings.For(Server, "m"));
        prices.Input = null;
        settings.Set(prices);
        Assert.Empty(settings.Prices);
    }

    [Fact]
    public void Prices_kept_over_others_fill_in_what_they_leave_out()
    {
        var kept = new ModelPrices { BaseUrl = Server, Model = "m", Input = 500, Output = 2500, Currency = "₽" };

        var filled = new ModelPrices { BaseUrl = Server, Model = "m", CachedInput = 50, CacheWrite = 625, Currency = "₽" }.Over(kept);
        Assert.Equal((500m, 50m, 625m, 2500m), (filled.Input, filled.CachedInput, filled.CacheWrite, filled.Output));

        var dollars = new ModelPrices { BaseUrl = Server, Model = "m", CachedInput = 0.2m }.Over(kept);
        Assert.Null(dollars.Input);
    }

    [Theory]
    [InlineData(0.08, "$", false, "$0.08")]
    [InlineData(0.0042, "$", true, "~$0.0042")]
    [InlineData(12.5, "₽", true, "~12.50 ₽")]
    [InlineData(3, "EUR", false, "3.00 EUR")]
    public void Totals_read_as_money(decimal amount, string currency, bool estimate, string shown) =>
        Assert.Equal(shown, Spending.Format(amount, currency, estimate));
}

/// <summary>What a chat spent, read from the start of its file for the Cost estimates panel's all-chats total.</summary>
public sealed class ChatSpendingStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-spent-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_history_list_reads_what_each_chat_spent()
    {
        var store = new Chats.ChatStore(_dir);
        var spent = new Spending();
        spent.Add("https://openrouter.ai/api/v1", "a", new ChatMessage { PromptTokens = 100, CompletionTokens = 5, Cost = 0.25m });
        // A long chat: the spending is near the start, before the megabytes of conversation.
        var history = Enumerable.Range(0, 400).Select(i => ChatMessage.User(new string('x', 1000))).ToList();
        store.Save(new Chats.SavedChat { Id = "c1", Title = "Long", Updated = DateTime.UtcNow, History = history, Spent = spent });
        store.Save(new Chats.SavedChat { Id = "c2", Title = "Old", Updated = DateTime.UtcNow.AddDays(-1), History = history });

        var list = store.List();
        Assert.Equal(0.25m, Assert.Single(list, h => h.Id == "c1").Spent!.Models[0].Cost);
        Assert.Null(Assert.Single(list, h => h.Id == "c2").Spent);
    }
}
