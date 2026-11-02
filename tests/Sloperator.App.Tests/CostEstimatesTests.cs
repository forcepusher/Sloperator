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
