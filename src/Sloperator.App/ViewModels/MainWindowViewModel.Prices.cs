using System.Collections.Concurrent;
using Avalonia.Threading;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- Auto pricing lookup ------------------------------------------------------------------

    /// <summary>The models the agent was asked to look up the prices of since Sloperator started, in any window, by server and model: each once.</summary>
    private static readonly ConcurrentDictionary<string, bool> PricesAsked = new();

    /// <summary>
    /// The run's <see cref="Core.Agent.AgentSession.PriceNudge"/>, for <paramref name="baseUrl"/> and
    /// <paramref name="model"/>: with Auto pricing lookup on (Settings > Advanced, the user 2026-11-02),
    /// a model that replied with no cost from its server and has no prices has the agent asked to look
    /// them up and keep them with set_api_pricing, once while Sloperator runs. Called on the agent's thread.
    /// </summary>
    private Func<string?> PriceNudgeFor(string baseUrl, string model) => () =>
    {
        if (!_settings.Cost.AutoLookup || model.Length == 0)
            return null;
        var url = OpenAiChatClient.NormalizeBaseUrl(baseUrl);
        var count = _spent.Copy().Models.Find(m => m.Model == model && OpenAiChatClient.NormalizeBaseUrl(m.BaseUrl) == url);
        if (count is null || count.Costed > 0 || _settings.Cost.For(baseUrl, model) is not null || !PricesAsked.TryAdd(url + " " + model, true))
            return null;
        return SetApiPricingTool.LookupRequest(model, baseUrl);
    };

    /// <summary>set_api_pricing's prices, from the agent's thread: kept in the settings on the window's, and the costs shown again.</summary>
    private void KeepPricesFromAgent(ModelPrices prices) =>
        Dispatcher.UIThread.Post(() => { if (!_closed) SetPrices(prices); });
}
