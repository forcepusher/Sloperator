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
    /// up on the web in the background by a small agent of their own (<see cref="PriceLookup"/>) on the
    /// same server and model, shown in the chat as a tool call (look_up_prices) as it goes. What it
    /// finds is kept as the model's prices, unless some were typed meanwhile; its tokens count in the chat.
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

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;
        var call = new ToolCall
        {
            Id = "prices-" + Guid.NewGuid().ToString("N")[..8],
            Name = "look_up_prices",
            Arguments = new JsonObject { ["model"] = model, ["server"] = url }.ToJsonString(),
        };
        var item = FindOrAddTool(call, $"Look up the prices of {model} on {host}");
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
        };
        _ = LookUpPricesAsync(client, url, model, ctx, item);
    }

    private async Task LookUpPricesAsync(OpenAiChatClient client, string url, string model, ToolContext ctx, ToolCallItem item)
    {
        string text;
        var failed = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var (found, answer) = await Task.Run(() => PriceLookup.RunAsync(client, model, ctx,
                step => Dispatcher.UIThread.Post(() => item.Progress = step), timeout.Token));
            if (found is { IsSet: true } prices)
            {
                prices.BaseUrl = url;
                prices.Model = model;
                // Typed in the Cost estimates panel meanwhile: those stand.
                if (_settings.Cost.For(url, model) is null)
                    SetPrices(prices);
                text = $"Per million tokens: input {Price(prices.Input)}, cached input {Price(prices.CachedInput)}, cache write {Price(prices.CacheWrite)}, " +
                       $"output {Price(prices.Output)} ({prices.Currency}). Kept as the model's prices in the Cost estimates panel.\n\n{answer}";

                static string Price(decimal? p) => p?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not found";
            }
            else
            {
                failed = true;
                text = "No prices found. Type them in the Cost estimates panel.\n\n" + answer;
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
}
