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
        Assert.Equal((0.9m + 0.18m + 0.375m + 0.15m, "$", true), spent.Total(settings));

        // A cache price left empty is the input price.
        settings.Set(new ModelPrices { BaseUrl = Server, Model = "m", Input = 1, Output = 2 });
        Assert.Equal(1.02m, spent.Total(settings)!.Value.Amount);
    }

    [Fact]
    public void The_server_cost_counts_where_nothing_is_typed_and_unpriced_models_give_none()
    {
        var spent = new Spending();
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 100, CompletionTokens = 5, Cost = 0.01m });
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 200, CompletionTokens = 5, Cost = 0.02m });
        Assert.Equal((0.03m, "$", false), spent.Total(new CostSettings()));

        // A reply without a cost makes it an estimate; a model with neither cost nor prices, unknown.
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 300, CompletionTokens = 5 });
        Assert.True(spent.Total(new CostSettings())!.Value.Estimate);
        spent.Add("http://localhost:1234/v1", "qwen", new ChatMessage { PromptTokens = 300, CompletionTokens = 5 });
        Assert.Null(spent.Total(new CostSettings()));

        // Prices in two currencies don't add up.
        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = "http://localhost:1234/v1", Model = "qwen", Input = 30, Currency = "₽" });
        Assert.Null(spent.Total(settings));
    }

    [Fact]
    public void The_server_cost_wins_over_typed_prices()
    {
        var spent = new Spending();
        spent.Add(Server, "a", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0, Cost = 0.5m });
        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = Server, Model = "a", Input = 3, Currency = "₽" });
        Assert.Equal((0.5m, "$", false), spent.Total(settings));
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
