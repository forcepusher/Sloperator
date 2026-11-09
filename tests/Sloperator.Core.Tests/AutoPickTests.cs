using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Auto model and effort (the user, 2026-11-09): the three models in Settings found on a server by their words,
/// and the evaluation before a message, a side task on the evaluation model that picks the model and effort.
/// </summary>
public class AutoPickTests
{
    private static readonly string[] Anthropic =
        ["claude-haiku-4-5-20251001", "claude-haiku-5-5", "claude-sonnet-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1"];

    private static AutoModelSet Set(params string[] models) =>
        AutoModelSet.Resolve(new AutoModels(), models.Select(ModelChoice.FromName).ToList(), models.LastOrDefault(), _ => Efforts.Levels)!;

    [Fact]
    public void The_defaults_find_the_newest_Haiku_Sonnet_and_Opus()
    {
        var set = Set(Anthropic);

        Assert.Equal(("claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5"), (set.Evaluation.Id, set.Daily.Id, set.Complex.Id));
        Assert.True(set.Evaluation.Named && set.Daily.Named && set.Complex.Named);
        Assert.True(set.Differ);
        // OpenRouter writes them its own way.
        var openRouter = Set("anthropic/claude-haiku-5.5", "anthropic/claude-sonnet-5.5", "anthropic/claude-opus-5.5", "openai/gpt-6.1-sol");
        Assert.Equal("anthropic/claude-opus-5.5", openRouter.Complex.Id);
    }

    [Fact]
    public void A_server_without_them_stands_its_own_pick_in_and_has_nothing_to_choose()
    {
        var set = AutoModelSet.Resolve(new AutoModels(), [ModelChoice.FromName("qwen3.8-27b"), ModelChoice.FromName("gemma-4-31b")], "qwen3.8-27b", _ => [])!;

        Assert.Equal(["qwen3.8-27b", "qwen3.8-27b", "qwen3.8-27b"], [set.Evaluation.Id, set.Daily.Id, set.Complex.Id]);
        Assert.False(set.Daily.Named);
        Assert.False(set.Differ);
        // Words of the user's own find any model, the same way for all.
        var mine = AutoModelSet.Resolve(new AutoModels { Evaluation = "gemma", Daily = "qwen", Complex = "qwen" },
            [ModelChoice.FromName("qwen3.8-27b"), ModelChoice.FromName("gemma-4-31b")], "qwen3.8-27b", _ => [])!;
        Assert.Equal("gemma-4-31b", mine.Evaluation.Id);
        Assert.True(mine.Differ);
    }

    [Fact]
    public void The_effort_on_Auto_is_the_one_picked_or_medium_and_the_nearest_the_model_takes()
    {
        var server = new ServerSettings { Effort = Efforts.Auto, EffortLevels = ["low", "medium", "high"] };

        Assert.Equal("medium", server.EffortInUse);
        server.AutoEffort = "max";
        Assert.Equal("high", server.EffortInUse);
        server.EffortLevels = [];
        Assert.Null(server.EffortInUse);
    }

    [Theory]
    [InlineData("model: complex\neffort: high\nwhy: A refactor across 14 files.\nfound:\n- Settings live in SettingsWindow.axaml.cs\n- 3 tests", AutoTier.Complex, "high")]
    [InlineData("**Model:** daily\n**Effort:** Extra high\n**Why:** Writing code.\n**Found:** nothing", AutoTier.Daily, "xhigh")]
    [InlineData("model: claude-haiku-5-5\neffort: low\nwhy: A question.\nfound: none", AutoTier.Evaluation, "low")]
    [InlineData("model: evaluation\neffort: low\nwhy: A greeting.\nfound: nothing; I looked at nothing, since the message is self-contained.", AutoTier.Evaluation, "low")]
    public void The_answer_is_read_however_it_is_written(string answer, AutoTier tier, string effort)
    {
        var pick = AutoPick.Read(answer, Set(Anthropic), pickModel: true, pickEffort: true, AutoTier.Daily, "claude-haiku-5-5")!;

        Assert.Equal((tier, effort), (pick.Tier, pick.Effort));
        Assert.NotEmpty(pick.Why);
        if (answer.Contains("SettingsWindow", StringComparison.Ordinal))
        {
            Assert.Contains("SettingsWindow.axaml.cs", pick.Found);
            Assert.Contains("claude-haiku-5-5 took a first look", pick.Notes);
        }
        else
            Assert.Null(pick.Notes);
    }

    [Fact]
    public void A_found_field_that_starts_with_none_of_is_still_a_note()
    {
        var pick = AutoPick.Read("model: daily\nwhy: Tests.\nfound: None of the tests cover -Json.", Set(Anthropic), true, false, AutoTier.Daily, "h")!;
        Assert.Equal("None of the tests cover -Json.", pick.Found);
    }

    [Fact]
    public void An_answer_with_no_pick_is_none_and_one_it_was_not_to_make_is_left_out()
    {
        Assert.Null(AutoPick.Read("I think this is fine.", Set(Anthropic), true, true, AutoTier.Daily, "h"));
        // The effort alone was asked: a model named isn't taken.
        var pick = AutoPick.Read("model: complex\neffort: low", Set(Anthropic), pickModel: false, pickEffort: true, AutoTier.Daily, "h")!;
        Assert.Equal((AutoTier.Daily, "low"), (pick.Tier, pick.Effort));
    }

    [Fact]
    public async Task The_evaluation_runs_on_the_evaluation_model_with_tools_that_only_read_and_stays_out_of_the_chat()
    {
        var folder = Directory.CreateTempSubdirectory("bb-auto-").FullName;
        File.WriteAllText(Path.Combine(folder, "notes.md"), "The settings code is in SettingsWindow.axaml.cs.");
        var server = new ScriptedServer(
            Sse.ToolCall("r1", "read_file", new JsonObject { ["path"] = Path.Combine(folder, "notes.md") }.ToJsonString()),
            Sse.Text("model: complex\neffort: high\nwhy: A refactor across many files.\nfound: The settings code is in SettingsWindow.axaml.cs."));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.anthropic.example/v1", null);
        var context = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = folder };
        var session = new AgentSession(client, new ChatRequestOptions { Model = "claude-sonnet-5-5", Effort = "medium" },
            ToolRegistry.CreateDefault(false, [new SetApiPricingTool()]), context, "sys");
        var observer = new RecordingObserver();
        var progress = new List<string>();

        var pick = await session.EvaluateAsync(new UserInput("Move the Settings dialog to a view model, in " + folder), Set(Anthropic),
            pickModel: true, pickEffort: true, AutoTier.Daily, progress.Add, observer, CancellationToken.None);

        Assert.Null(pick.Problem);
        Assert.Equal((AutoTier.Complex, "high", "claude-haiku-5-5"), (pick.Tier, pick.Effort, pick.By));
        Assert.Contains("SettingsWindow.axaml.cs", pick.Found);
        Assert.Equal(2, server.Requests.Count);
        foreach (var request in server.Requests)
        {
            Assert.Equal("claude-haiku-5-5", (string?)request["model"]);
            Assert.Equal("low", (string?)request["reasoning_effort"]);
            Assert.Equal(["fetch_page", "index_folder", "list_directory", "read_file", "web_search"],
                request["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).Order());
        }
        var asked = server.Requests[0].ToJsonString();
        Assert.Contains("side task for Sloperator", asked);
        Assert.Contains("complex: claude-opus-5-5", asked);
        Assert.Contains("Move the Settings dialog", asked);
        Assert.Single(progress);
        Assert.Empty(session.History);
    }

    [Fact]
    public async Task A_server_that_fails_the_evaluation_leaves_the_model_as_it_was()
    {
        var server = new ScriptedServer();
        server.Failures.Enqueue((System.Net.HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"model not found\"}}"));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.anthropic.example/v1", null);
        var context = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(client, new ChatRequestOptions { Model = "claude-sonnet-5-5" }, ToolRegistry.CreateDefault(false), context, "sys");

        var pick = await session.EvaluateAsync(new UserInput("Hi"), Set(Anthropic), true, false, AutoTier.Complex, null, new RecordingObserver(), CancellationToken.None);

        Assert.NotNull(pick.Problem);
        Assert.Equal(AutoTier.Complex, pick.Tier);
        Assert.Null(pick.Effort);
    }
}
