using Sloperator.App.ViewModels;
using Sloperator.Core.Chats;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The Cost estimates panel: the chat's total by model, prices typed for the model in use, every chat's total.</summary>
public sealed class CostEstimatesTests
{
    private const string Lm = "http://localhost:1234/v1", Router = "https://openrouter.ai/api/v1";

    [Fact]
    public void Prices_typed_for_the_model_in_use_price_its_tokens_and_the_server_cost_wins()
    {
        var spent = new Spending();
        spent.Add(Lm, "qwen", new ChatMessage { PromptTokens = 1_000_000, CachedTokens = 500_000, CompletionTokens = 100_000 });
        spent.Add(Router, "haiku", new ChatMessage { PromptTokens = 10, CompletionTokens = 1, Cost = 0.5m });
        var settings = new CostSettings();
        var chats = new List<ChatHeader>
        {
            new() { Id = "a", Title = "Cheap", Spent = Spent(Router, 0.1m) },
            new() { Id = "b", Title = "Dear", Spent = Spent(Router, 2m) },
            new() { Id = "c", Title = "Unpriced", Spent = Unpriced() },
            new() { Id = "d", Title = "Before costs" },
        };
        var vm = new CostEstimatesViewModel(null, () => spent, () => settings, () => (Lm, "qwen"), () => chats, settings.Set);

        Assert.Equal("Not known", vm.Total);
        Assert.Equal("–", vm.Models.Single(m => m.Model == "qwen").Cost);
        Assert.Equal("$0.50", vm.Models.Single(m => m.Model == "haiku").Cost);
        Assert.Equal("qwen on localhost:1234", vm.PricesFor);

        vm.InputPrice = "1";
        vm.CachedInputPrice = "0,1";
        vm.OutputPrice = "2";
        // qwen: 0.5M × 1 + 0.5M × 0.1 + 0.1M × 2 = 0.75, plus haiku's 0.50 as OpenRouter said.
        Assert.Equal("~$1.25", vm.Total);
        Assert.Contains("50%", vm.CacheNote);
        Assert.Contains("saving ~$0.45", vm.CacheNote);
        Assert.Equal(0.1m, settings.For(Lm, "qwen")!.CachedInput);

        Assert.Equal("$2.10", vm.AllChats);
        Assert.Equal(["Dear", "Cheap"], vm.TopChats.Select(c => c.Title));
        Assert.Contains("1 chat without one", vm.AllChatsNote);
    }

    [Fact]
    public void Look_up_prices_asks_the_agent_and_its_prices_line_fills_the_fields()
    {
        var settings = new CostSettings();
        var sent = new List<string>();
        CostEstimatesViewModel vm = null!;
        string? context = null;
        vm = new CostEstimatesViewModel(null, () => new Spending(), () => settings, () => ("https://api.proxyapi.ru/v1", "anthropic/claude-haiku-5-5"), () => [], settings.Set,
            text => { sent.Add(text); context = vm.About([])?.Context; });

        vm.LookUpPricesCommand.Execute(null);
        Assert.Equal("Look up the prices of anthropic/claude-haiku-5-5 on api.proxyapi.ru.", Assert.Single(sent));
        Assert.Contains("https://api.proxyapi.ru/v1", context);
        Assert.Contains("PRICES {", context);
        Assert.False(vm.CanLookUp);
        Assert.Null(vm.About([])); // the lookup's words go with its own message only

        vm.TakeReply("Searching…"); // a step before the answer
        Assert.True(vm.IsLookingUp);
        vm.TakeReply("proxyapi.ru lists Haiku 5.5 at 30 ₽ in and 150 ₽ out.\n\nPRICES {\"input\": 30, \"cachedInput\": null, \"cacheWrite\": null, \"output\": 150, \"currency\": \"₽\"}");
        Assert.False(vm.IsLookingUp);
        Assert.Equal("30", vm.InputPrice);
        Assert.Equal("", vm.CachedInputPrice);
        Assert.Equal("150", vm.OutputPrice);
        Assert.Equal("₽", vm.PriceCurrency);
        Assert.Equal(150m, settings.For("https://api.proxyapi.ru/v1", "anthropic/claude-haiku-5-5")!.Output);

        // A run that ends without the line gives the button back and says so.
        vm.LookUpPricesCommand.Execute(null);
        vm.RunEnded();
        Assert.True(vm.CanLookUp);
        Assert.Contains("no prices line", vm.LookupNote);
    }

    [Theory]
    [InlineData("PRICES {\"input\": \"0,5\", \"output\": 2}", 0.5, 2, "$")]
    [InlineData("Done.\n`PRICES {\"input\": 0, \"cachedInput\": 0, \"cacheWrite\": 0, \"output\": 0, \"currency\": \"$\"}`", 0, 0, "$")]
    public void The_prices_line_reads_numbers_and_text(string reply, double input, double output, string currency)
    {
        var p = Sloperator.Core.Agent.PriceLookup.Read(reply)!;
        Assert.Equal((decimal)input, p.Input);
        Assert.Equal((decimal)output, p.Output);
        Assert.Equal(currency, p.Currency);
    }

    private static Spending Spent(string url, decimal cost)
    {
        var s = new Spending();
        s.Add(url, "haiku", new ChatMessage { PromptTokens = 10, CompletionTokens = 1, Cost = cost });
        return s;
    }

    private static Spending Unpriced()
    {
        var s = new Spending();
        s.Add("http://elsewhere/v1", "m", new ChatMessage { PromptTokens = 10, CompletionTokens = 1 });
        return s;
    }
}
