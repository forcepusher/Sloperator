using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- Auto pricing lookup ------------------------------------------------------------------

    /// <summary>The models whose prices were looked up since Sloperator started, in any window, by server and model: each once.</summary>
    private static readonly ConcurrentDictionary<string, bool> PricesLookedUp = new();

    /// <summary>
    /// After a reply: when the model in use replied with no cost from its server and has no prices,
    /// and Auto pricing lookup is on (Settings > Advanced, the user 2026-11-02), its prices are looked
    /// up on the web in the background (<see cref="PriceLookup"/>) through the subagent tool, on the same
    /// server and model, shown in the chat as that tool's card as it goes: its task as the line, its prompt
    /// as Input, its final answer as Output. What it finds is kept as the model's prices, unless some were
    /// typed meanwhile; its tokens count in the chat.
    /// </summary>
    private void LookUpPricesIfNeeded()
    {
        if (!_settings.Cost.AutoLookup || _closed || _runDetached)
            return;
        var server = _settings.Server;
        var (url, model) = (server.BaseUrl, server.ModelInUse);
        if (model.Length == 0)
            return;
        var normalized = OpenAiChatClient.NormalizeBaseUrl(url);
        var count = _spent.Copy().Models.Find(m => m.Model == model && OpenAiChatClient.NormalizeBaseUrl(m.BaseUrl) == normalized);
        if (count is null || count.Costed > 0 || _settings.Cost.For(url, model) is not null || !PricesLookedUp.TryAdd(normalized + " " + model, true))
            return;

        // The subagent tool's card, as when the agent calls it: its task as the line, its prompt as Input.
        var call = new ToolCall
        {
            Id = "prices-" + Guid.NewGuid().ToString("N")[..8],
            Name = "subagent",
            Arguments = PriceLookup.Arguments(model, url).ToJsonString(),
        };
        var item = FindOrAddTool(call, PriceLookup.TaskName(model, url));
        item.At = DateTime.UtcNow;
        item.State = ToolItemState.Running;
        item.Progress = "The server doesn't report what replies cost: searching the web for its prices…";

        var client = BackendConnection.Create(_http, server).Client;
        client.Spending = _spent;
        var ctx = new ToolContext
        {
            Platform = _platform,
            Settings = _settings.Agent,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Browser = Core.Web.HeadlessBrowser.Find(_paths.BrowserProfileDir),
            Client = client,
            Options = BackendConnection.Create(_http, server).Options,
            Progress = step => Dispatcher.UIThread.Post(() => item.Progress = step),
            SetPrices = KeepPricesFromAgent,
        };
        _ = LookUpPricesAsync(url, model, ctx, item);
    }

    private async Task LookUpPricesAsync(string url, string model, ToolContext ctx, ToolCallItem item)
    {
        string text;
        var failed = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var (found, answer) = await Task.Run(() => PriceLookup.RunAsync(ctx, model, timeout.Token));
            if (found is { IsSet: true } prices)
            {
                text = answer;
                prices.Model = model;
                // Typed in the Cost estimates panel meanwhile: those stand, and nothing is set.
                if (_settings.Cost.For(url, model) is null)
                    Dispatcher.UIThread.Post(() => SetPricesCard(prices, ctx), DispatcherPriority.Background);
            }
            else
            {
                failed = true;
                // Its answer all the same: it may say why.
                text = answer.Length > 0 ? answer : "The subagent gave no answer.";
            }
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException or OperationCanceledException)
        {
            failed = true;
            text = $"The lookup stopped: {(ex is OperationCanceledException ? "it took more than 5 minutes" : ex.Message)}. Type the prices in the Cost estimates panel.";
        }
        item.ResultText = text.Trim();
        item.Progress = "";
        item.Ended = DateTime.UtcNow;
        item.State = failed ? ToolItemState.Error : ToolItemState.Done;
    }

    /// <summary>
    /// After the subagent's card, set_api_pricing's: the prices it found put in the Cost estimates, as the
    /// tool does when the agent calls it (the user, 2026-11-02), its arguments the prices, its output what was kept.
    /// </summary>
    private void SetPricesCard(ModelPrices prices, ToolContext ctx)
    {
        var args = SetApiPricingTool.Arguments(prices);
        var call = new ToolCall { Id = "prices-" + Guid.NewGuid().ToString("N")[..8], Name = "set_api_pricing", Arguments = args.ToJsonString() };
        var tool = new SetApiPricingTool();
        var item = FindOrAddTool(call, tool.Describe(args));
        item.At = DateTime.UtcNow;
        var result = tool.ExecuteAsync(args, ctx, CancellationToken.None).Result;
        item.ResultText = result.Text;
        item.Ended = DateTime.UtcNow;
        item.State = result.IsError ? ToolItemState.Error : ToolItemState.Done;
    }

    /// <summary>set_api_pricing's prices, from any thread: kept in the settings on the window's, and the costs shown again.</summary>
    private void KeepPricesFromAgent(ModelPrices prices) =>
        Dispatcher.UIThread.Post(() => { if (!_closed) SetPrices(prices); });
}
