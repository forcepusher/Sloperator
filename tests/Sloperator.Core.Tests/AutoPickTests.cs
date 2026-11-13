using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Auto model and effort (the user, 2026-11-09): the three models in Settings found on a server by their words,
/// and the evaluation before a message, a side task on the helper model that picks the model and effort.
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

        Assert.Equal(("claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5"), (set.Helper.Id, set.Daily.Id, set.Complex.Id));
        Assert.True(set.Helper.Named && set.Daily.Named && set.Complex.Named);
        Assert.True(set.Differ);
        // OpenRouter writes them its own way.
        var openRouter = Set("anthropic/claude-haiku-5.5", "anthropic/claude-sonnet-5.5", "anthropic/claude-opus-5.5", "openai/gpt-6.1-sol");
        Assert.Equal("anthropic/claude-opus-5.5", openRouter.Complex.Id);
    }

    [Fact]
    public void A_server_without_them_stands_its_own_pick_in_and_has_nothing_to_choose()
    {
        var set = AutoModelSet.Resolve(new AutoModels(), [ModelChoice.FromName("qwen3.8-27b"), ModelChoice.FromName("gemma-4-31b")], "qwen3.8-27b", _ => [])!;

        Assert.Equal(["qwen3.8-27b", "qwen3.8-27b", "qwen3.8-27b"], [set.Helper.Id, set.Daily.Id, set.Complex.Id]);
        Assert.False(set.Daily.Named);
        Assert.False(set.Differ);
        // Words of the user's own find any model, the same way for all.
        var mine = AutoModelSet.Resolve(new AutoModels { Helper = "gemma", Daily = "qwen", Complex = "qwen" },
            [ModelChoice.FromName("qwen3.8-27b"), ModelChoice.FromName("gemma-4-31b")], "qwen3.8-27b", _ => [])!;
        Assert.Equal("gemma-4-31b", mine.Helper.Id);
        Assert.True(mine.Differ);
    }

    [Fact]
    public void Model_and_effort_are_on_Auto_until_one_is_picked()
    {
        var fresh = new AppSettings().Server;
        Assert.True(fresh.ModelOnAuto);
        Assert.True(fresh.EffortOnAuto);

        // A settings.json without them (an older one, or written by hand) is on Auto too; a level saved stays.
        var dir = Directory.CreateTempSubdirectory("sloperator-auto-default-");
        try
        {
            var paths = new AppPaths(dir.FullName);
            Directory.CreateDirectory(paths.DataDir);
            File.WriteAllText(paths.SettingsFile, """{ "server": { "baseUrl": "http://localhost:1234/v1" } }""");
            var loaded = new SettingsStore(paths).Load().Server;
            Assert.True(loaded.ModelOnAuto && loaded.EffortOnAuto);
            File.WriteAllText(paths.SettingsFile, """{ "server": { "model": "qwen3.8-27b", "effort": "high" } }""");
            loaded = new SettingsStore(paths).Load().Server;
            Assert.Equal(("qwen3.8-27b", "high"), (loaded.Model, loaded.Effort));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_evaluation_models_words_saved_before_the_rename_are_the_helper_models()
    {
        AutoModels Auto(string json) =>
            System.Text.Json.JsonSerializer.Deserialize(SettingsStore.Migrate(json), SettingsJson.Default.AppSettings)!.Server.Auto;

        Assert.Equal("gemma", Auto("""{ "server": { "auto": { "evaluation": "gemma", "daily": "qwen", "complex": "qwen" } } }""").Helper);
        Assert.Equal("mistral", Auto("""{ "server": { "auto": { "evaluation": "gemma", "helper": "mistral" } } }""").Helper);
        Assert.Equal("Claude Haiku", Auto("""{ "server": { "baseUrl": "http://localhost:1234/v1" } }""").Helper);
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

    private static AutoPick Picked(AutoTier tier, string? effort) => new(tier, effort, "why", "", "claude-haiku-5-5");

    [Fact]
    public void A_chat_under_way_only_moves_up_in_model_and_in_effort()
    {
        var set = Set(Anthropic);

        // A first message takes what was picked: down to the helper model, another effort.
        Assert.Equal((AutoTier.Helper, "medium"), Picked(AutoTier.Helper, "medium").Next(set, AutoTier.Daily, null, upOnly: false, stayed: false));
        Assert.Equal((AutoTier.Daily, "high"), Picked(AutoTier.Complex, "high").Next(set, AutoTier.Daily, null, upOnly: false, stayed: true));
        // Under way (the user's Sonnet to Haiku, and medium to low, 2026-11-11): never down, in either.
        Assert.Equal((AutoTier.Daily, "high"), Picked(AutoTier.Helper, "medium").Next(set, AutoTier.Daily, "high", upOnly: true, stayed: false));
        Assert.Equal((AutoTier.Daily, "high"), Picked(AutoTier.Daily, "medium").Next(set, AutoTier.Daily, "high", upOnly: true, stayed: false));
        // Up to a stronger model, with the effort picked for it, a lower one too; Stay keeps both.
        Assert.Equal((AutoTier.Complex, "high"), Picked(AutoTier.Complex, "high").Next(set, AutoTier.Daily, "medium", upOnly: true, stayed: false));
        Assert.Equal((AutoTier.Complex, "medium"), Picked(AutoTier.Complex, "medium").Next(set, AutoTier.Daily, "high", upOnly: true, stayed: false));
        Assert.Equal((AutoTier.Daily, "medium"), Picked(AutoTier.Complex, "high").Next(set, AutoTier.Daily, "medium", upOnly: true, stayed: true));
        // Up in effort on the model in use, whenever the task needs it (the user, 2026-11-13): on the dearest one too,
        // and from the medium nothing has picked yet.
        Assert.Equal((AutoTier.Daily, "high"), Picked(AutoTier.Daily, "high").Next(set, AutoTier.Daily, "medium", upOnly: true, stayed: false));
        Assert.Equal((AutoTier.Complex, "max"), Picked(AutoTier.Complex, "max").Next(set, AutoTier.Complex, "high", upOnly: true, stayed: false));
        Assert.Equal((AutoTier.Daily, "xhigh"), Picked(AutoTier.Daily, "xhigh").Next(set, AutoTier.Daily, null, upOnly: true, stayed: false));
        Assert.Equal((AutoTier.Daily, "high"), Picked(AutoTier.Helper, "high").Next(set, AutoTier.Daily, "medium", upOnly: true, stayed: false));
        // A failed evaluation changes nothing.
        Assert.Equal((AutoTier.Daily, "medium"), (Picked(AutoTier.Complex, "high") with { Problem = "refused" }).Next(set, AutoTier.Daily, "medium", true, false));
        // A tier above that names the same model is no move: its effort rises as on the model in use, and never falls.
        var same = AutoModelSet.Resolve(new AutoModels { Daily = "sonnet", Complex = "sonnet" }, Anthropic.Select(ModelChoice.FromName).ToList(), null, _ => Efforts.Levels)!;
        Assert.Equal((AutoTier.Complex, "high"), Picked(AutoTier.Complex, "high").Next(same, AutoTier.Daily, "medium", upOnly: true, stayed: false));
        Assert.Equal((AutoTier.Complex, "high"), Picked(AutoTier.Complex, "medium").Next(same, AutoTier.Daily, "high", upOnly: true, stayed: false));
    }

    [Fact]
    public void A_chat_on_the_dearest_model_at_its_most_effort_has_nothing_left_to_pick()
    {
        var set = Set(Anthropic);
        Assert.True(AutoPick.CanMoveUp(set, AutoTier.Helper));
        Assert.True(AutoPick.CanMoveUp(set, AutoTier.Daily));
        Assert.False(AutoPick.CanMoveUp(set, AutoTier.Complex));
        var same = AutoModelSet.Resolve(new AutoModels { Daily = "sonnet", Complex = "sonnet" }, Anthropic.Select(ModelChoice.FromName).ToList(), null, _ => Efforts.Levels)!;
        Assert.False(AutoPick.CanMoveUp(same, AutoTier.Daily));

        // The effort has somewhere to go while the model takes more of it.
        Assert.True(AutoPick.CanRaise("medium", Efforts.Levels));
        Assert.True(AutoPick.CanRaise(null, ["low", "medium", "high"]));
        Assert.False(AutoPick.CanRaise("high", ["low", "medium", "high"]));
        Assert.False(AutoPick.CanRaise("max", Efforts.Levels));
        Assert.False(AutoPick.CanRaise(null, []));
    }

    [Fact]
    public void Auto_never_picks_an_effort_below_medium()
    {
        // The user, 2026-11-13: "never pick an effort below medium". Low isn't offered, on a first message or later.
        var set = Set(Anthropic);
        var first = AutoPick.Request("What time is it?", set, true, true, "claude-sonnet-5-5", 0, null, null);
        Assert.Contains("Efforts, least to most: medium, high, xhigh, max. ", first);
        Assert.Contains("Most tasks need medium.\n", first);
        Assert.Contains("effort: medium, high, xhigh or max\n", first);
        Assert.DoesNotMatch(@"\blow\b", first);

        // Said all the same, it is medium.
        var pick = AutoPick.Read("model: helper\neffort: low\nwhy: A question.\nfound: nothing", set, true, true, AutoTier.Daily, "h")!;
        Assert.Equal((AutoTier.Helper, "medium"), (pick.Tier, pick.Effort));
        Assert.Equal("high", AutoPick.Read("effort: high", set, false, true, AutoTier.Daily, "h")!.Effort);
    }

    [Fact]
    public void With_no_model_above_a_warm_chat_is_still_asked_whether_the_task_needs_more_effort()
    {
        // On the dearest model, or one picked by hand: the levels from the one in use up.
        var asked = AutoPick.Request("Now find the race in the scheduler.", Set(Anthropic), false, true, "claude-opus-5-5", 190_000, "claude-opus-5-5", "Done.", AutoTier.Complex, "high");
        Assert.Contains("The model is claude-opus-5-5.\n", asked);
        Assert.Contains("Efforts, least to most: high, xhigh, max. ", asked);
        Assert.Contains("The chat's effort, high now, stays or goes up, never down.\n", asked);
        Assert.Contains("Another effort reads all of it anew at full price, so raise it only when the task needs it.", asked);
        Assert.Contains("effort: high, xhigh or max\n", asked);
        Assert.DoesNotContain("model: ", asked);
    }

    [Fact]
    public void Under_way_the_helper_is_offered_the_model_in_use_and_the_ones_above()
    {
        var set = Set(Anthropic);
        var first = AutoPick.Request("How do I start it?", set, true, true, "claude-sonnet-5-5", 0, null, null);
        Assert.Contains("- helper: claude-haiku-5-5 (you).", first);
        Assert.Contains("model: helper, daily or complex\n", first);
        Assert.DoesNotContain("never moves back down", first);

        var later = AutoPick.Request("How do I start it?", set, true, true, "claude-sonnet-5-5", 190_000, "claude-sonnet-5-5", "Done.", AutoTier.Daily, "medium");
        Assert.DoesNotContain("claude-haiku-5-5", later);
        Assert.Contains("- daily: claude-sonnet-5-5 (in use).", later);
        Assert.Contains("- complex: claude-opus-5-5.", later);
        Assert.Contains("it never moves back down", later);
        Assert.Contains("The chat's effort, medium now, stays or goes up on the model in use, never down", later);
        Assert.Contains("Efforts, least to most: medium, high, xhigh, max.", later);
        Assert.Contains("effort: medium, high, xhigh or max\n", later);
        Assert.Contains("model: daily or complex\n", later);
        Assert.Contains("about 190k tokens", later);
        Assert.Contains("Another model, or another effort, reads all of it anew", later);
    }

    [Fact]
    public void Once_the_servers_cache_has_lapsed_a_chat_under_way_picks_as_freely_as_a_first_message()
    {
        // The user, 2026-11-13: up only while there is a cache to miss, five minutes from the chat's last request.
        var last = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(AutoPick.CacheLapsed(last, last.AddSeconds(20)));
        Assert.False(AutoPick.CacheLapsed(last, last.AddSeconds(299)));
        Assert.True(AutoPick.CacheLapsed(last, last.AddMinutes(5)));
        Assert.True(AutoPick.CacheLapsed(last, last.AddHours(3)));
        // Nobody knows when it was last read: nothing to hold on to.
        Assert.True(AutoPick.CacheLapsed(null, last));

        // Then the pick is taken whole: down a model, or another effort on the same one.
        var set = Set(Anthropic);
        Assert.Equal((AutoTier.Helper, "medium"), Picked(AutoTier.Helper, "medium").Next(set, AutoTier.Daily, "high", upOnly: false, stayed: false));
        Assert.Equal((AutoTier.Daily, "medium"), Picked(AutoTier.Daily, "medium").Next(set, AutoTier.Complex, "high", upOnly: false, stayed: false));
        Assert.Equal((AutoTier.Daily, "high"), Picked(AutoTier.Daily, "high").Next(set, AutoTier.Daily, "medium", upOnly: false, stayed: false));

        // And the helper is offered all three again, told that staying costs what moving does.
        var asked = AutoPick.Request("How do I start it?", set, true, true, "claude-sonnet-5-5", 190_000, "claude-sonnet-5-5", "Done.");
        Assert.Contains("- helper: claude-haiku-5-5 (you).", asked);
        Assert.Contains("model: helper, daily or complex\n", asked);
        Assert.DoesNotContain("never moves back down", asked);
        Assert.DoesNotContain("never down", asked);
        Assert.DoesNotContain("(in use)", asked);
        Assert.Contains("about 190k tokens and ran on claude-sonnet-5-5. The server no longer holds it in its cache", asked);
        Assert.DoesNotContain("reads all of it anew", asked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_evaluation_of_a_chat_under_way_is_offered_the_cheaper_model_only_once_the_cache_has_lapsed(bool cacheWarm)
    {
        var server = new ScriptedServer(Sse.Text("model: daily\neffort: low\nwhy: A question.\nfound: nothing"));
        var client = new OpenAiChatClient(new HttpClient(server), "https://api.anthropic.example/v1", null);
        var context = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(client, new ChatRequestOptions { Model = "claude-sonnet-5-5", Effort = "medium" }, ToolRegistry.CreateDefault(false), context, "sys");
        session.History.Add(ChatMessage.User("Build the importer."));
        session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "Built: importer.py reads the CSV." });

        var pick = await session.EvaluateAsync(new UserInput("And how do I start it from the command line?"), Set(Anthropic),
            pickModel: true, pickEffort: true, AutoTier.Daily, cacheWarm, null, new RecordingObserver(), CancellationToken.None);

        Assert.Equal((AutoTier.Daily, "medium"), (pick.Tier, pick.Effort));
        var asked = Assert.Single(server.Requests).ToJsonString();
        Assert.Contains("Built: importer.py reads the CSV.", asked);
        Assert.Equal(cacheWarm, asked.Contains("it never moves back down"));
        Assert.Equal(!cacheWarm, asked.Contains("helper: claude-haiku-5-5 (you)"));
    }

    [Theory]
    [InlineData("model: complex\neffort: high\nwhy: A refactor across 14 files.\nfound:\n- Settings live in SettingsWindow.axaml.cs\n- 3 tests", AutoTier.Complex, "high")]
    [InlineData("**Model:** daily\n**Effort:** Extra high\n**Why:** Writing code.\n**Found:** nothing", AutoTier.Daily, "xhigh")]
    [InlineData("model: claude-haiku-5-5\neffort: low\nwhy: A question.\nfound: none", AutoTier.Helper, "medium")]
    [InlineData("model: helper\neffort: low\nwhy: A greeting.\nfound: nothing; I looked at nothing, since the message is self-contained.", AutoTier.Helper, "medium")]
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
    public void A_short_reply_in_a_chat_under_way_goes_on_without_a_look()
    {
        Assert.True(AutoPick.IsShortReply(new UserInput("yes, go on"), chatUnderWay: true));
        Assert.True(AutoPick.IsShortReply(new UserInput("Thanks! That works now."), chatUnderWay: true));
        // A first message, however short; a longer one; one with a file or a panel's pick: looked at.
        Assert.False(AutoPick.IsShortReply(new UserInput("Fix the login bug"), chatUnderWay: false));
        Assert.False(AutoPick.IsShortReply(new UserInput("Now move the settings code into a view model"), chatUnderWay: true));
        Assert.False(AutoPick.IsShortReply(new UserInput("look at this", [@"C:\shot.png"]), chatUnderWay: true));
        Assert.False(AutoPick.IsShortReply(new UserInput("redo this bit") { Context = "Audio editor · rain" }, chatUnderWay: true));
    }

    [Fact]
    public void A_found_field_that_starts_with_none_of_is_still_a_note()
    {
        var pick = AutoPick.Read("model: daily\nwhy: Tests.\nfound: None of the tests cover -Json.", Set(Anthropic), true, false, AutoTier.Daily, "h")!;
        Assert.Equal("None of the tests cover -Json.", pick.Found);
    }

    [Fact]
    public void The_reason_is_its_sentence_and_an_answer_started_over_counts_as_given_last()
    {
        // Haiku 5.5 thought on under its reason, and all of it stood on the card under "claude-haiku-5-5 at Low".
        var pick = AutoPick.Read("model: helper\neffort: low\nwhy: The task is small, so the cheapest model should be enough.\n\n" +
            "Hmm, but I'm changing my answer, so let me be precise: daily fits better, at medium effort.\nfound: stats.ps1 has no -Top yet.",
            Set(Anthropic), true, true, AutoTier.Daily, "h")!;
        Assert.Equal("The task is small, so the cheapest model should be enough.", pick.Why);
        Assert.Equal((AutoTier.Helper, "medium", "stats.ps1 has no -Top yet."), (pick.Tier, pick.Effort, pick.Found));

        // The whole answer given again is the correction.
        var again = AutoPick.Read("model: helper\neffort: low\nwhy: Small.\nfound: nothing\n\nOn second thought:\nmodel: daily\neffort: medium\nwhy: It runs code.\nfound: tests.ps1 is missing.",
            Set(Anthropic), true, true, AutoTier.Helper, "h")!;
        Assert.Equal((AutoTier.Daily, "medium", "It runs code.", "tests.ps1 is missing."), (again.Tier, again.Effort, again.Why, again.Found));

        // A line of the notes that begins like a field stays a note.
        var notes = AutoPick.Read("model: daily\neffort: medium\nwhy: Code.\nfound:\n- Model: the data model is in models.py\n- Effort: none needed for the tests\n- why: nobody knows",
            Set(Anthropic), true, true, AutoTier.Helper, "h")!;
        Assert.Equal((AutoTier.Daily, "medium", "Code."), (notes.Tier, notes.Effort, notes.Why));
        Assert.Contains("the data model is in models.py", notes.Found);
        Assert.Contains("nobody knows", notes.Found);

        // Nor does one in the reason change what was picked.
        var prose = AutoPick.Read("model: daily\neffort: high\nwhy: A hard edit.\nEffort: medium would be too little here.\nModel: the script is long.\nfound: nothing",
            Set(Anthropic), true, true, AutoTier.Helper, "h")!;
        Assert.Equal((AutoTier.Daily, "high"), (prose.Tier, prose.Effort));
    }

    [Fact]
    public void An_answer_with_no_pick_is_none_and_one_it_was_not_to_make_is_left_out()
    {
        Assert.Null(AutoPick.Read("I think this is fine.", Set(Anthropic), true, true, AutoTier.Daily, "h"));
        // The effort alone was asked: a model named isn't taken.
        var pick = AutoPick.Read("model: complex\neffort: low", Set(Anthropic), pickModel: false, pickEffort: true, AutoTier.Daily, "h")!;
        Assert.Equal((AutoTier.Daily, "medium"), (pick.Tier, pick.Effort));
    }

    [Fact]
    public async Task The_evaluation_runs_on_the_helper_model_with_tools_that_only_read_and_stays_out_of_the_chat()
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
            pickModel: true, pickEffort: true, AutoTier.Daily, cacheWarm: true, progress.Add, observer, CancellationToken.None);

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

        var pick = await session.EvaluateAsync(new UserInput("Hi"), Set(Anthropic), true, false, AutoTier.Complex, true, null, new RecordingObserver(), CancellationToken.None);

        Assert.NotNull(pick.Problem);
        Assert.Equal(AutoTier.Complex, pick.Tier);
        Assert.Null(pick.Effort);
    }
}
