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
    public void A_runs_end_looks_up_the_prices_of_every_model_the_chat_used_one_a_run()
    {
        // Auto on a server that doesn't say what replies cost: the helper looks at each message, sonnet does the task.
        const string anthropic = "https://api.anthropic.com/v1";
        var spent = new Spending();
        spent.Add(anthropic, "haiku", new ChatMessage { PromptTokens = 3_000, CompletionTokens = 100 });
        spent.Add(anthropic, "sonnet", new ChatMessage { PromptTokens = 60_000, CachedTokens = 50_000, CompletionTokens = 900 });
        spent.Add(Router, "opus", new ChatMessage { PromptTokens = 10, CompletionTokens = 1 }); // another server's: not this run's to ask
        var settings = new CostSettings();
        var asked = new HashSet<string>();

        // The run's own model first, then the helper's, which no run ends on; each once.
        Assert.Equal("sonnet", MainWindowViewModel.ModelToPrice(spent, settings, anthropic, "sonnet", asked.Add));
        Assert.Equal("haiku", MainWindowViewModel.ModelToPrice(spent, settings, anthropic + "/", "sonnet", asked.Add));
        Assert.Null(MainWindowViewModel.ModelToPrice(spent, settings, anthropic, "sonnet", asked.Add));

        // A model with prices isn't asked, unless they lack the cache it uses; one whose server says the cost never is.
        asked.Clear();
        settings.Set(new ModelPrices { BaseUrl = anthropic, Model = "haiku", Input = 1, Output = 5 });
        settings.Set(new ModelPrices { BaseUrl = anthropic, Model = "sonnet", Input = 3, Output = 15 });
        Assert.Equal("sonnet", MainWindowViewModel.ModelToPrice(spent, settings, anthropic, "sonnet", asked.Add));
        Assert.Null(MainWindowViewModel.ModelToPrice(spent, settings, anthropic, "sonnet", asked.Add));
        var costed = new Spending();
        costed.Add(Router, "haiku", new ChatMessage { PromptTokens = 10, CompletionTokens = 1, Cost = 0.01m });
        costed.Add(Router, "sonnet", new ChatMessage { PromptTokens = 10, CompletionTokens = 1, Cost = 0.05m });
        Assert.Null(MainWindowViewModel.ModelToPrice(costed, new CostSettings(), Router, "sonnet", new HashSet<string>().Add));
    }

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

        // qwen has no prices yet: haiku's cost shows, marked as less than was spent.
        Assert.Equal("$0.50+", vm.Total);
        Assert.Contains("1 model has no prices", vm.TotalNote);
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
    public void Only_the_last_used_currency_shows_here_and_across_chats()
    {
        const string Proxy = "https://api.proxyapi.ru/v1";
        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = Proxy, Model = "sonnet", Input = 500, Output = 2500, Currency = "₽" });
        settings.Set(new ModelPrices { BaseUrl = Lm, Model = "qwen", Input = 2, Output = 10 });

        var mixed = new Spending();
        mixed.Add(Proxy, "sonnet", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        mixed.Add(Lm, "qwen", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        var roubles = new Spending();
        roubles.Add(Proxy, "sonnet", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        var chats = new List<ChatHeader>
        {
            new() { Id = "old", Title = "Old", Updated = DateTime.UtcNow.AddDays(-1), Spent = roubles },
            new() { Id = "new", Title = "New", Updated = DateTime.UtcNow, Spent = mixed },
        };
        var vm = new CostEstimatesViewModel(null, () => mixed, () => settings, () => (Lm, "qwen"), () => chats, settings.Set);

        Assert.Equal("~$2.00", vm.Total);
        Assert.Equal("Estimated from your prices (~).", vm.TotalNote);
        Assert.Equal("qwen", Assert.Single(vm.Models).Model);

        Assert.Equal("~$2.00", vm.AllChats);
        Assert.Equal(["New"], vm.TopChats.Select(c => c.Title));
        Assert.Contains("1 chat with a cost.", vm.AllChatsNote);
    }

    [Fact]
    public void A_row_picked_in_the_table_takes_its_own_prices()
    {
        // On Auto the helper model answers too, and is never the model in use when the panel is looked at.
        const string Api = "https://api.anthropic.com/v1";
        var spent = new Spending();
        spent.Add(Api, "claude-sonnet-5-5", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        spent.Add(Api, "claude-haiku-5-5", new ChatMessage { PromptTokens = 1_000_000, CompletionTokens = 0 });
        var settings = new CostSettings();
        var vm = new CostEstimatesViewModel(null, () => spent, () => settings, () => (Api, "claude-sonnet-5-5"), () => [], settings.Set);

        Assert.Equal("claude-sonnet-5-5", vm.PricedRow!.Model);
        Assert.Equal("Prices of the model in use", vm.PricesTitle);
        Assert.Contains("pick its row", vm.TotalNote);

        vm.PricedRow = vm.Models.Single(m => m.Model == "claude-haiku-5-5");
        Assert.Equal("claude-haiku-5-5 on api.anthropic.com", vm.PricesFor);
        Assert.Equal("Prices of a model this chat used", vm.PricesTitle);
        vm.InputPrice = "1";
        Assert.Equal(1m, settings.For(Api, "claude-haiku-5-5")!.Input);
        Assert.Null(settings.For(Api, "claude-sonnet-5-5"));
        // The table is read again after each reply: the row stays picked.
        Assert.Equal("claude-haiku-5-5", vm.PricedRow!.Model);
        Assert.Equal("~$1.00+", vm.Total);
    }

    [Fact]
    public void A_missing_cache_price_shows_on_the_model_and_the_total()
    {
        var spent = new Spending();
        spent.Add(Lm, "qwen", new ChatMessage { PromptTokens = 1_000_000, CachedTokens = 900_000, CompletionTokens = 0 });
        var settings = new CostSettings();
        settings.Set(new ModelPrices { BaseUrl = Lm, Model = "qwen", Input = 500, Currency = "₽" });
        var vm = new CostEstimatesViewModel(null, () => spent, () => settings, () => (Lm, "qwen"), () => [], settings.Set);

        Assert.Equal("~50.00 ₽+", vm.Total);
        Assert.Contains("Cache prices missing", vm.TotalNote);
        Assert.Contains("cache prices missing", Assert.Single(vm.Models).Source);
    }

    [Fact]
    public void Look_up_prices_asks_the_agent_and_what_it_keeps_fills_the_fields()
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
        Assert.Contains("set_api_pricing", context);
        Assert.False(vm.CanLookUp);
        Assert.Null(vm.About([])); // the lookup's words go with its own message only

        vm.Refresh(); // a reply before it keeps them
        Assert.True(vm.IsLookingUp);
        // set_api_pricing, through the window
        settings.Set(new ModelPrices { BaseUrl = "https://api.proxyapi.ru/v1", Model = "anthropic/claude-haiku-5-5", Input = 30, Output = 150, Currency = "₽" });
        vm.Refresh();
        Assert.False(vm.IsLookingUp);
        Assert.Equal("30", vm.InputPrice);
        Assert.Equal("", vm.CachedInputPrice);
        Assert.Equal("150", vm.OutputPrice);
        Assert.Equal("₽", vm.PriceCurrency);
        Assert.Equal(150m, settings.For("https://api.proxyapi.ru/v1", "anthropic/claude-haiku-5-5")!.Output);

        // Looking up again with prices kept waits for different ones.
        vm.LookUpPricesCommand.Execute(null);
        vm.Refresh();
        Assert.True(vm.IsLookingUp);
        settings.Set(new ModelPrices { BaseUrl = "https://api.proxyapi.ru/v1", Model = "anthropic/claude-haiku-5-5", Input = 30, CachedInput = 3, Output = 150, Currency = "₽" });
        vm.Refresh();
        Assert.False(vm.IsLookingUp);
        Assert.Equal("3", vm.CachedInputPrice);

        // A run that ends without the line gives the button back and says so.
        vm.LookUpPricesCommand.Execute(null);
        vm.RunEnded();
        Assert.True(vm.CanLookUp);
        Assert.Contains("kept no prices", vm.LookupNote);
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
