using System.Net;
using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The agent connecting Sloperator itself to another model and setting its effort (list_models, switch_model, set_effort).</summary>
public sealed class ModelToolsTests
{
    /// <summary>The top bar: what it shows, and what the tool switched it to.</summary>
    private sealed class TopBar : IModelServerSwitch
    {
        public ServerSettings Current { get; } = new() { BaseUrl = KiloGateway.Address, AutoModel = KiloGateway.Model };
        public List<(string Url, string Key, string Model)> Switched { get; } = [];

        public Task SwitchAsync(string baseUrl, string apiKey, string model)
        {
            Switched.Add((baseUrl, apiKey, model));
            Current.BaseUrl = baseUrl;
            Current.ApiKey = apiKey;
            Current.Model = model;
            Current.AutoModel = null;
            return Task.CompletedTask;
        }

        public Task SetEffortAsync(string level)
        {
            Current.Effort = level;
            return Task.CompletedTask;
        }
    }

    /// <summary>An LM Studio-like server on plain http: its model list, and 401 for any other key than <paramref name="key"/> when it wants one.</summary>
    private sealed class LocalServer(string[] models, string? key = null) : HttpMessageHandler
    {
        public List<string?> KeysSeen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Scheme != "http")
                throw new HttpRequestException("The SSL connection could not be established");
            var sent = request.Headers.Authorization?.Parameter;
            KeysSeen.Add(sent);
            if (key is not null && sent != key)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Invalid API key"}}""") });
            if (request.RequestUri.AbsolutePath == "/v1/models")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(new JsonObject { ["data"] = new JsonArray(models.Select(m => (JsonNode)new JsonObject { ["id"] = m }).ToArray()) }.ToJsonString()) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NobodyThere : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    private static ToolContext Context(TopBar bar) =>
        new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), ModelServer = bar };

    private static Task<ToolResult> Switch(HttpMessageHandler server, TopBar bar, JsonObject args) =>
        new SwitchModelTool(() => new HttpClient(server)).ExecuteAsync(args, Context(bar), CancellationToken.None);

    private static Task<ToolResult> List(HttpMessageHandler server, TopBar bar) =>
        new ListModelsTool(() => new HttpClient(server)).ExecuteAsync([], Context(bar), CancellationToken.None);

    private static Task<ToolResult> Effort(TopBar bar, string level) =>
        new SetEffortTool().ExecuteAsync(new JsonObject { ["level"] = level }, Context(bar), CancellationToken.None);

    [Fact]
    public async Task Connect_switches_the_top_bar_to_a_server_that_answers()
    {
        var bar = new TopBar();
        var result = await Switch(new LocalServer(["qwen3.8-27b", "gemma-4-12b-it"]), bar, new JsonObject { ["address"] = "localhost:1234" });
        Assert.False(result.IsError, result.Text);
        // The address that answered, no key, and no model picked: the strongest one is Sloperator's pick.
        Assert.Equal([("http://localhost:1234/v1", "", "")], bar.Switched);
        Assert.Contains("uses qwen3.8-27b (Auto, the cheapest of the strongest models on the server", result.Text);
        Assert.Contains("the user's next message goes to the new one", result.Text);
    }

    /// <summary>An OpenRouter-like service: its priced list, and scores at /benchmarks, as on 2026-11-03.</summary>
    private sealed class ScoredServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/models" => """
                    {"data":[
                    {"id":"anthropic/claude-opus-5.5","canonical_slug":"anthropic/claude-opus-5.5-20260921","pricing":{"prompt":"0.000004","completion":"0.00002"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"]},
                    {"id":"anthropic/claude-sonnet-5.5","canonical_slug":"anthropic/claude-sonnet-5.5-20260928","pricing":{"prompt":"0.000002","completion":"0.00001"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"]},
                    {"id":"anthropic/claude-haiku-5.5","canonical_slug":"anthropic/claude-haiku-5.5-20261007","pricing":{"prompt":"0.0000001","completion":"0.0000005"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"]},
                    {"id":"qwen/qwen3.8-27b","canonical_slug":"qwen/qwen3.8-27b-20260814","pricing":{"prompt":"0.00000015","completion":"0.000001875"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"]}
                    ]}
                    """,
                "/api/v1/benchmarks" => """
                    {"data":[
                    {"model_permaslug":"anthropic/claude-opus-5.5-20260921","intelligence_index":58.4},
                    {"model_permaslug":"anthropic/claude-sonnet-5.5-20260928","intelligence_index":54.2},
                    {"model_permaslug":"anthropic/claude-haiku-5.5-20261007","intelligence_index":36.2},
                    {"model_permaslug":"qwen/qwen3.8-27b-20260814","intelligence_index":20.1}
                    ]}
                    """,
                _ => null,
            };
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Switched_to_another_server_without_a_model_the_agent_gets_Auto_there()
    {
        // "Switch to OpenRouter" with no model named: Auto, on the daily driver, as the top bar picks.
        var bar = new TopBar();
        var result = await Switch(new ScoredServer(), bar, new JsonObject { ["address"] = "https://switch-scored.test/api/v1", ["api_key"] = "sk-test" });
        Assert.False(result.IsError, result.Text);
        Assert.Equal([("https://switch-scored.test/api/v1", "sk-test", "")], bar.Switched);
        Assert.Contains("uses anthropic/claude-sonnet-5.5 (Auto, your daily driver model: " +
                        "54.2 on the Artificial Analysis Intelligence Index, $2 in and $10 out a million tokens;", result.Text);
    }

    [Fact]
    public async Task A_model_is_picked_by_its_listed_name_or_a_unique_part_of_it()
    {
        var server = new LocalServer(["qwen3.8-27b", "gemma-4-12b-it", "gemma-4-31b-it"]);
        var bar = new TopBar();
        var picked = await Switch(server, bar, new JsonObject { ["address"] = "http://localhost:1234/v1", ["model"] = "QWEN3.8" });
        Assert.False(picked.IsError, picked.Text);
        Assert.Equal("qwen3.8-27b", bar.Switched.Single().Model);

        // "gemma" could be either: the stronger, and the result names the other.
        var vague = await Switch(server, bar, new JsonObject { ["address"] = "http://localhost:1234/v1", ["model"] = "gemma" });
        Assert.False(vague.IsError, vague.Text);
        Assert.Equal("gemma-4-31b-it", bar.Switched[^1].Model);
        Assert.Contains("of 2 like \"gemma\": gemma-4-12b-it;", vague.Text);

        var none = await Switch(server, bar, new JsonObject { ["address"] = "http://localhost:1234/v1", ["model"] = "llama" });
        Assert.True(none.IsError);
        Assert.Equal(2, bar.Switched.Count);
        Assert.Contains("qwen3.8-27b, gemma-4-12b-it, gemma-4-31b-it", none.Text);
    }

    [Theory]
    [InlineData("haiku 5.5", "claude-haiku-5-5")]
    [InlineData("Opus 5.5", "claude-opus-5-5")]
    [InlineData("opus", "claude-opus-5-5")]
    [InlineData("haiku", "claude-haiku-5-5")]
    [InlineData("opus 5", "claude-opus-5")]
    [InlineData("opus 4.5", "claude-opus-4-5-20251101")]
    [InlineData("sonnet 4", "claude-sonnet-4-20250514")]
    [InlineData("claude-opus-4-1", "claude-opus-4-1")]
    public void Claude_models_are_named_in_plain_words_and_the_newest_wins(string wanted, string picked)
    {
        string[] anthropic = ["claude-opus-5-5", "claude-opus-5", "claude-opus-4-5-20251101", "claude-opus-4-1", "claude-sonnet-4-20250514",
                              "claude-haiku-5-5", "claude-haiku-5-5-20261001", "claude-haiku-4-5-20251001", "claude-3-5-haiku-20241022"];
        var like = ModelTools.Like(wanted, anthropic);
        Assert.Equal(picked, like.Count == 1 ? like[0] : ModelTools.Best(like));
    }

    [Fact]
    public void OpenRouter_names_with_dots_and_a_maker_match_too()
    {
        string[] openRouter = ["anthropic/claude-opus-5.5", "anthropic/claude-haiku-5.5", "anthropic/claude-haiku-4.5", "openai/gpt-5.2"];
        Assert.Equal(["anthropic/claude-opus-5.5"], ModelTools.Like("opus 5.5", openRouter));
        Assert.Equal("anthropic/claude-haiku-5.5", ModelTools.Best(ModelTools.Like("haiku", openRouter)));
        Assert.Equal(["openai/gpt-5.2"], ModelTools.Like("GPT 5.2", openRouter));
    }

    [Fact]
    public void A_size_in_a_name_is_no_version()
    {
        // "gpt" in Settings' Auto model found openai/gpt-oss-120b, its 120b read as version 120.
        string[] openRouter = ["openai/gpt-oss-120b", "openai/gpt-oss-20b", "openai/gpt-5.6-luna", "openai/gpt-6-luna", "openai/gpt-6.1-sol"];
        Assert.Equal("openai/gpt-6.1-sol", ModelChoice.Named("gpt", openRouter));
        Assert.Equal("gemma-4-31b-it", ModelChoice.Named("gemma", ["gemma-4-12b-it", "gemma-4-31b-it", "gemma-3-270m"]));
    }

    [Fact]
    public async Task Nothing_changes_when_the_server_does_not_answer_or_refuses_the_key()
    {
        var bar = new TopBar();
        var offline = await Switch(new NobodyThere(), bar, new JsonObject { ["address"] = "localhost:1234" });
        Assert.True(offline.IsError);
        Assert.StartsWith("Nothing changed:", offline.Text);
        Assert.Contains("LM Studio, its server has to be switched on", offline.Text);

        var wrongKey = await Switch(new LocalServer(["m"], key: "right"), bar, new JsonObject { ["address"] = "http://box:8080/v1", ["api_key"] = "wrong" });
        Assert.True(wrongKey.IsError);
        Assert.Contains("Ask the user for the right API key", wrongKey.Text);
        Assert.Empty(bar.Switched);
        Assert.Equal(KiloGateway.Address, bar.Current.BaseUrl);
    }

    [Fact]
    public async Task The_key_goes_to_the_server_and_stays_with_its_address_but_never_in_the_card_or_result()
    {
        var server = new LocalServer(["a-model", "b-model"], key: "sk-secret-123");
        var bar = new TopBar();
        var args = new JsonObject { ["address"] = "http://box:8080/v1", ["api_key"] = "sk-secret-123" };
        var result = await Switch(server, bar, args);
        Assert.False(result.IsError, result.Text);
        Assert.Equal("sk-secret-123", bar.Current.ApiKey);
        Assert.DoesNotContain("sk-secret", result.Text);
        Assert.DoesNotContain("sk-secret", new SwitchModelTool().Describe(args));

        // Another model on the same server keeps the key the user gave before.
        var other = await Switch(server, bar, new JsonObject { ["address"] = "box:8080", ["model"] = "b-model" });
        Assert.False(other.IsError, other.Text);
        Assert.Equal(("http://box:8080/v1", "sk-secret-123", "b-model"), bar.Switched[^1]);

        // A key isn't carried to another server.
        await Switch(new LocalServer(["c"]), bar, new JsonObject { ["address"] = "http://other:1234/v1" });
        Assert.Equal("", bar.Switched[^1].Key);

        // Back to a server saved in the top bar: its key comes with it.
        bar.Current.Saved.Add(new SavedServer { BaseUrl = "http://box:8080/v1", ApiKey = "sk-secret-123" });
        var back = await Switch(server, bar, new JsonObject { ["address"] = "box:8080" });
        Assert.False(back.IsError, back.Text);
        Assert.Equal("sk-secret-123", bar.Switched[^1].Key);
    }

    [Fact]
    public async Task Show_says_the_server_and_its_models_without_the_key()
    {
        var bar = new TopBar();
        bar.Current.BaseUrl = "http://box:8080/v1";
        bar.Current.ApiKey = "sk-secret-123";
        bar.Current.Model = "a-model";
        bar.Current.AutoModel = null;
        var result = await List(new LocalServer(["a-model", "b-model"], key: "sk-secret-123"), bar);
        Assert.Equal("Sloperator runs on a-model at http://box:8080/v1, with an API key. The model has no effort setting.\nModels it offers (2): a-model, b-model", result.Text);

        bar.Current.EffortLevels = ["low", "medium", "high"];
        var withEffort = await List(new LocalServer(["a-model"], key: "sk-secret-123"), bar);
        Assert.Contains(" Effort: medium (it takes low, medium, high).", withEffort.Text);
    }

    [Fact]
    public async Task Effort_is_set_as_the_dropdown_would_or_the_nearest_the_model_takes()
    {
        var bar = new TopBar();
        bar.Current.EffortLevels = ["low", "medium", "high"];
        var high = await Effort(bar, "High");
        Assert.False(high.IsError, high.Text);
        Assert.Equal("high", bar.Current.Effort);
        Assert.StartsWith("Sloperator's effort is now high.", high.Text);
        Assert.Contains("the user's next message is answered with the new one", high.Text);

        // Max is kept for a model that takes it, and this one gets the nearest.
        var max = await Effort(bar, "max");
        Assert.Equal("max", bar.Current.Effort);
        Assert.Contains("doesn't take max, so it gets high", max.Text);

        await Assert.ThrowsAsync<ToolArgumentException>(() => Effort(bar, "turbo"));
    }

    [Fact]
    public async Task Nothing_changes_for_a_model_without_an_effort_setting()
    {
        var bar = new TopBar();
        var result = await Effort(bar, "low");
        Assert.True(result.IsError);
        Assert.StartsWith("Nothing changed:", result.Text);
        Assert.Equal(Efforts.Default, bar.Current.Effort);
    }

    [Fact]
    public async Task Without_the_app_there_is_no_tool_and_nothing_to_switch()
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        foreach (Tool tool in new Tool[] { new ListModelsTool(), new SwitchModelTool(), new SetEffortTool() })
            Assert.True((await tool.ExecuteAsync(new JsonObject { ["address"] = "localhost:1234", ["level"] = "high" }, ctx, CancellationToken.None)).IsError);

        Assert.Contains("use switch_model; to change how hard you think (effort), set_effort", SystemPrompt.Build("Test OS", null, "C:\\", "", modelServer: true));
        Assert.DoesNotContain("switch_model", SystemPrompt.Build("Test OS", null, "C:\\", ""));
    }
}
