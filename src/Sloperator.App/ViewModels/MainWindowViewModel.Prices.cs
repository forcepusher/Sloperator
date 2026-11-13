using System.Collections.Concurrent;
using Avalonia.Threading;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- Auto pricing lookup ------------------------------------------------------------------

    /// <summary>The models whose prices were looked up since Sloperator started, in any window, by server and model: each once.</summary>
    private static readonly ConcurrentDictionary<string, bool> PricesAsked = new();

    /// <summary>
    /// The run's <see cref="Core.Agent.AgentSession.PriceLookup"/>, for <paramref name="baseUrl"/> and
    /// <paramref name="model"/>: with Auto pricing lookup on (Settings > Advanced, the user 2026-11-02),
    /// a model that replied with no cost from its server and has no prices, or none for the cache it uses, has them looked up and kept with
    /// set_api_pricing in a side task, once while Sloperator runs. Called on the agent's thread.
    /// </summary>
    private Func<string?> PriceLookupFor(string baseUrl, string model) => () =>
        _settings.Cost.AutoLookup && model.Length > 0
        && ModelToPrice(_spent, _settings.Cost, baseUrl, model, key => PricesAsked.TryAdd(key, true)) is { } lacking
            ? SetApiPricingTool.LookupRequest(lacking, baseUrl)
            : null;

    /// <summary>
    /// The model whose prices a run's end looks up: the run's own, <paramref name="model"/>, or else another the
    /// chat used on the same server, one a run. On Auto a chat's cost is of up to three models: the helper's look
    /// at each message, and the model the chat was on before it moved up. Only the run's model was looked up, and
    /// no run ends on the helper, so on a server that doesn't say what replies cost (Anthropic's own) the total
    /// kept its "+" with the looks left out until the user typed the helper's prices (the user asked, 2026-11-14).
    /// </summary>
    /// <param name="firstAsk">Whether this server and model is asked for the first time: a model is looked up once.</param>
    public static string? ModelToPrice(Spending spent, CostSettings cost, string baseUrl, string model, Func<string, bool> firstAsk)
    {
        var url = OpenAiChatClient.NormalizeBaseUrl(baseUrl);
        foreach (var count in spent.Copy().Models.Where(m => OpenAiChatClient.NormalizeBaseUrl(m.BaseUrl) == url).OrderBy(m => m.Model == model ? 0 : 1))
            if (count.Costed == 0 && (cost.For(baseUrl, count.Model) is not { } kept || kept.LacksCache(count)) && firstAsk(url + " " + count.Model))
                return count.Model;
        return null;
    }

    /// <summary>set_api_pricing's prices, from the agent's thread: kept in the settings on the window's, over the prices there, and the costs shown again.</summary>
    private void KeepPricesFromAgent(ModelPrices prices) =>
        Dispatcher.UIThread.Post(() => { if (!_closed) SetPrices(prices.Over(_settings.Cost.For(prices.BaseUrl, prices.Model))); });
}
