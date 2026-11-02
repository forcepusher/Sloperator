using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Sloperator.Core.Web;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: how well a real model does with Auto pricing lookup's side task. A small task (read a file, give its
/// secret word) runs on a real server with Sloperator's own system prompt and tools; after its answer a side
/// task has the model look up its prices and keep them with set_api_pricing. Measured: whether it searched,
/// whether it kept prices, how far they are from the truth, whether the chat's last word is still the answer
/// (the secret word, nothing of the lookup in the conversation), and how many replies and seconds it took.
/// BB_LIVE_URL, BB_LIVE_KEY and BB_LIVE_MODEL pick the server, key and model (as ReplyRoomLiveTests).
/// The truth is the server's own model list where it prices models (OpenRouter, Kilo), else
/// BB_LIVE_EXPECT, "input,output,currency" per million tokens ("30,150,₽"; "0,0,$" for a model on the
/// user's own PC). Run with `-- xUnit.DiagnosticMessages=true` to see the measures.
/// </summary>
public class PriceLookupLiveTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_LIVE_URL");
    private static readonly string Key = Environment.GetEnvironmentVariable("BB_LIVE_KEY") ?? "";
    private static readonly string Model = Environment.GetEnvironmentVariable("BB_LIVE_MODEL") ?? "";
    private static readonly string? Expect = Environment.GetEnvironmentVariable("BB_LIVE_EXPECT");

    /// <summary>How far a price kept may be from the truth and still count as right.</summary>
    private const double Tolerance = 0.15;

    [Fact]
    public async Task The_side_task_finds_the_prices_and_keeps_them()
    {
        Assert.SkipWhen(Url is null, "Set BB_LIVE_URL to run.");
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var server = new ServerSettings { BaseUrl = Url!, ApiKey = Key, Model = Model };
        var probe = await ServerProbe.CheckAsync(http, server, ct);
        Assert.True(probe.Reachable, probe.Message);
        server.BaseUrl = probe.BaseUrl!;
        server.Model = probe.Model!;
        server.EffortLevels = probe.Efforts ?? [];
        var facts = await ServerProbe.EnsureLoadedAsync(http, server, null, ct, known: probe.Facts);
        server.SetDetectedContext(facts.Context);
        var truth = await TruthAsync(http, server, ct);

        var connection = BackendConnection.Create(http, server);
        var kept = new List<ModelPrices>();
        var profile = Directory.CreateTempSubdirectory("bb-prices-browser-").FullName;
        var ctx = new ToolContext
        {
            Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(),
            Browser = HeadlessBrowser.Find(profile),
            SetPrices = p => kept.Add(p.With(connection.Client.BaseUrl, connection.Options.Model)),
        };
        var prompt = SystemPrompt.Build("Windows", null, Path.GetTempPath(), "", webSearch: true, folderIndex: false, control: false, findText: false);
        var session = new AgentSession(connection.Client, connection.Options,
            ToolRegistry.CreateDefault(false, [new SetApiPricingTool()], includeIndex: false, includeControl: false, includeFindText: false), ctx, prompt,
            contextTokens: connection.ContextTokens);
        var asked = 0;
        session.PriceLookup = () => asked++ == 0 ? SetApiPricingTool.LookupRequest(connection.Options.Model, connection.Client.BaseUrl) : null;
        var observer = new RecordingObserver();
        var clock = Stopwatch.StartNew();
        // A task of its own, whose answer must stay the chat's last word (the user: the model mustn't get
        // distracted from its goal).
        var folder = Directory.CreateTempSubdirectory("bb-prices-task-").FullName;
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "Shopping list: milk, eggs.\nThe secret word is PELICAN.\n");

        await session.RunAsync($"Read notes.txt in {folder} and tell me the secret word it holds.", observer, ct);

        clock.Stop();
        // The side task's calls are its card's progress lines; set_api_pricing's reads "Set the prices of …".
        var searches = observer.Progress.Count(p => !p.StartsWith("Set the prices", StringComparison.Ordinal));
        var keptAt = observer.Progress.FindLastIndex(p => p.StartsWith("Set the prices", StringComparison.Ordinal));
        var after = keptAt < 0 ? [] : observer.Progress.Skip(keptAt + 1).ToList();
        var last = session.History.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Content?.Trim() ?? "";
        var found = kept.LastOrDefault();
        var say = (string text) => TestContext.Current.SendDiagnosticMessage(text);
        say($"{server.BaseUrl} {server.Model}: {clock.Elapsed.TotalSeconds:0} s, {session.History.Count(m => m.Role == ChatRole.Assistant)} replies");
        say($"tools: {string.Join(", ", observer.ToolsRun)}; side task: {string.Join(" | ", observer.Progress)}");
        say($"kept: {(found is null ? "nothing" : $"input {found.Input}, cached {found.CachedInput}, write {found.CacheWrite}, output {found.Output} {found.Currency}")}");
        say($"truth: {(truth is null ? "unknown" : $"input {truth.Value.Input}, output {truth.Value.Output} {truth.Value.Currency}")}");
        say($"tools after set_api_pricing: {(after.Count == 0 ? "none" : string.Join(", ", after))}");
        say($"last reply: {last.ReplaceLineEndings(" / ")}");
        foreach (var notice in observer.Notices)
            say("notice: " + notice);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(1, asked);
        // The answer is the last word: the secret word in the last reply, nothing of the lookup in the
        // conversation, nothing run after the prices were kept.
        Assert.Contains("PELICAN", last, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.History, m => m.ToolCalls?.Any(c => c.Name is "set_api_pricing" or "web_search" or "fetch_page") == true);
        Assert.Empty(after);
        Assert.True(searches > 0 || truth is { Input: 0, Output: 0 }, "It kept prices without looking anything up.");
        Assert.NotNull(found);
        Assert.True(found.IsSet);
        if (truth is { } t)
        {
            Assert.True(Near(found.Input, t.Input), $"input {found.Input}, truth {t.Input}");
            Assert.True(Near(found.Output, t.Output), $"output {found.Output}, truth {t.Output}");
            Assert.Equal(t.Currency, found.Currency);
        }
    }

    private static bool Near(decimal? kept, decimal truth) =>
        kept is { } k && (truth == 0 ? k == 0 : Math.Abs((double)(k - truth) / (double)truth) <= Tolerance);

    /// <summary>What the model costs per million tokens: from the server's model list, else BB_LIVE_EXPECT; null when neither says.</summary>
    private static async Task<(decimal Input, decimal Output, string Currency)?> TruthAsync(HttpClient http, ServerSettings server, CancellationToken ct)
    {
        if (Expect?.Split(',') is [var input, var output, var currency])
            return (decimal.Parse(input, CultureInfo.InvariantCulture), decimal.Parse(output, CultureInfo.InvariantCulture), currency.Trim());
        using var request = new HttpRequestMessage(HttpMethod.Get, OpenAiChatClient.NormalizeBaseUrl(server.BaseUrl) + "/models");
        if (server.ApiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode || JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["data"] is not JsonArray models)
            return null;
        var pricing = models.OfType<JsonObject>().FirstOrDefault(m => m["id"]?.GetValue<string>() == server.Model)?["pricing"];
        decimal? PerMillion(string key) => pricing?[key] is JsonValue v && decimal.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken) ? perToken * 1_000_000 : null;
        return PerMillion("prompt") is { } i && PerMillion("completion") is { } o ? (i, o, "$") : null;
    }
}
