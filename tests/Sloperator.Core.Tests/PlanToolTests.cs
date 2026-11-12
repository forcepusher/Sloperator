using System.Text.Json;
using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Chats;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

public sealed class PlanToolTests
{
    private const string Two = """{"steps":[{"word":"Read","detail":"Read the files"},{"word":"Build","detail":"Write the page"}]}""";

    private static ToolContext NewContext() =>
        new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };

    private static Task<ToolResult> Call(ToolContext ctx, string args) =>
        new PlanTool().ExecuteAsync(JsonNode.Parse(args)!.AsObject(), ctx, CancellationToken.None);

    [Fact]
    public async Task Steps_set_the_plan_and_now_moves_it_along_them()
    {
        var ctx = NewContext();
        var told = new List<AgentPlan?>();
        ctx.PlanChanged = told.Add;

        var set = await Call(ctx, Two);
        Assert.Contains("2. Build: Write the page", set.Text);
        Assert.Equal(0, ctx.Plan!.At);

        var moved = await Call(ctx, """{"now":2}""");
        Assert.Equal("Step 2 of 2: Build.", moved.Text);
        Assert.Equal(1, ctx.Plan!.At);
        Assert.Equal(2, ctx.Plan.Steps.Count);

        await Call(ctx, """{"now":3}""");
        Assert.True(ctx.Plan!.Finished);
        Assert.Equal(3, told.Count);

        // A step written as a string moves it too.
        Assert.Equal("Step 1 of 2: Read. As you start step 2, \"Build\", call plan with just now=2, along with that step's first call.", (await Call(ctx, """{"now":"1"}""")).Text);
    }

    [Fact]
    public async Task No_arguments_read_the_plan_and_an_empty_list_takes_it_away()
    {
        var ctx = NewContext();
        Assert.True((await Call(ctx, "{}")).IsError);

        await Call(ctx, Two);
        Assert.Contains("1. Read", (await Call(ctx, "{}")).Text);

        await Call(ctx, """{"steps":[]}""");
        Assert.Null(ctx.Plan);
    }

    [Fact]
    public async Task A_step_past_the_end_is_an_error()
    {
        var ctx = NewContext();
        await Call(ctx, Two);
        Assert.True((await Call(ctx, """{"now":4}""")).IsError);
        Assert.Equal(0, ctx.Plan!.At);
    }

    [Fact]
    public async Task Marking_the_last_step_done_with_finished_words_still_runs()
    {
        // A reply that says it is done and only looks loses its calls; this one is not a look.
        var saysDone = Sse.Text("All done.").Replace("data: [DONE]\n\n", "") + Sse.ToolCall("p2", "plan", """{"now":3}""");
        var server = new ScriptedServer(Sse.ToolCall("p1", "plan", Two), saysDone, Sse.Text("All done."));
        var ctx = NewContext();
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");

        await session.RunAsync("go", new RecordingObserver(), CancellationToken.None);

        Assert.True(ctx.Plan!.Finished);
    }

    [Fact]
    public async Task A_plan_left_behind_its_work_is_brought_up_to_date_aside()
    {
        // Sonnet 5.5 set a plan, did the work without moving it, and answered: the bar said step 1 for good.
        var folder = Path.GetTempPath();
        var server = new ScriptedServer(Sse.ToolCall("p1", "plan", Two),
            Sse.ToolCall("l1", "list_directory", new JsonObject { ["path"] = folder }.ToJsonString()),
            Sse.Text("Built the page."),
            Sse.ToolCall("p2", "plan", """{"now":3}"""));
        var ctx = NewContext();
        var observer = new RecordingObserver();
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.True(ctx.Plan!.Finished);
        Assert.Equal(4, server.Requests.Count);
        Assert.Contains("still says step 1 of 2", server.Requests[3].ToJsonString());
        // Aside: the answer stays the run's last word.
        Assert.DoesNotContain(session.History, m => m.Content?.Contains("still says step") == true);
        Assert.Equal("Built the page.", session.History[^1].Content);
        Assert.Contains(observer.Notices, n => n.Contains("left at step 1 of 2") && n.EndsWith("all done."));
    }

    private static string Folder(string root, string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private static string List(string id, string folder) => Sse.ToolCall(id, "list_directory", new JsonObject { ["path"] = folder }.ToJsonString());

    [Fact]
    public async Task A_task_begun_with_no_plan_gets_the_model_asked_for_one_once()
    {
        // The user never saw a plan: 79 replies of a Unity task on Sonnet 5.5 went by without one plan call (2026-11-12).
        var root = Directory.CreateTempSubdirectory("plan-nudge").FullName;
        var server = new ScriptedServer(List("l1", Folder(root, "a")), Sse.ToolCall("p1", "plan", Two), List("l2", Folder(root, "b")),
            List("l3", Folder(root, "c")), List("l4", Folder(root, "d")), Sse.ToolCall("p2", "plan", """{"now":3}"""), Sse.Text("Done."));
        var ctx = NewContext();
        var observer = new RecordingObserver();
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");

        await session.RunAsync("go", observer, CancellationToken.None);

        // With the first step's results, and never again in the run.
        Assert.DoesNotContain("plan bar is empty", server.Requests[0].ToJsonString());
        Assert.Contains("You have started on this task", server.Requests[1].ToJsonString());
        Assert.Contains("plan bar is empty", server.Requests[1].ToJsonString());
        Assert.Single(session.History, m => m.Content?.Contains("plan bar is empty") == true);
        Assert.Single(observer.Notices, n => n == "Asked the model to show its plan on the bar above the message box.");
        Assert.True(ctx.Plan!.Finished);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task A_run_that_stops_to_announce_the_plan_it_was_asked_for_is_told_to_carry_on_once()
    {
        // Haiku 5.5, live: the note, a plan of four steps, then "The plan bar is now set… Next I'll add the
        // parameters", and the run ended with the task not begun (2026-11-12).
        var root = Directory.CreateTempSubdirectory("plan-stop").FullName;
        var server = new ScriptedServer(List("l1", Folder(root, "a")),
            Sse.ToolCall("p1", "plan", Two), Sse.Text("The plan bar is now set with two steps. Next I'll build the page."),
            List("l2", Folder(root, "b")), Sse.ToolCall("p2", "plan", """{"now":3}"""), Sse.Text("Built the page."));
        var ctx = NewContext();
        var observer = new RecordingObserver();
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(6, server.Requests.Count);
        Assert.Contains("you ended your turn with that step not done", server.Requests[3].ToJsonString());
        Assert.Single(observer.Notices, n => n == "The model stopped after setting its plan; asked it to carry on with the task.");
        Assert.Equal("Built the page.", session.History[^1].Content);
        Assert.True(ctx.Plan!.Finished);

        // A question to the user after the plan stands: the run ends there.
        var asking = new ScriptedServer(List("l1", Folder(root, "a")),
            Sse.ToolCall("p1", "plan", Two), Sse.Text("Two ways to build it: a table or cards. **Which do you want?**"));
        var asked = new RecordingObserver();
        await new AgentSession(new OpenAiChatClient(new HttpClient(asking), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), NewContext(), "sys").RunAsync("go", asked, CancellationToken.None);
        Assert.Equal(3, asking.Requests.Count);
        Assert.DoesNotContain(asked.Notices, n => n.Contains("carry on"));

        // Nor is a run pushed on that worked after the note and then answered: Haiku 5.5 moved its plan to the
        // last step, answered, was told to carry on, and answered the finished task a second time.
        var done = new ScriptedServer(List("l1", Folder(root, "a")), Sse.ToolCall("p1", "plan", Two), List("l2", Folder(root, "c")),
            Sse.ToolCall("p2", "plan", """{"now":2}"""), Sse.Text("Built the page."));
        var ended = new RecordingObserver();
        var last = NewContext();
        await new AgentSession(new OpenAiChatClient(new HttpClient(done), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), last, "sys").RunAsync("go", ended, CancellationToken.None);
        Assert.Equal(5, done.Requests.Count);
        Assert.DoesNotContain(ended.Notices, n => n.Contains("carry on") || n.Contains("left at step"));
        // Its last step was the answer: the bar is done, rather than left on that step as a plan the run stopped short of.
        Assert.True(last.Plan!.Finished);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task A_run_that_keeps_a_plan_or_goes_on_with_one_is_not_asked_for_it()
    {
        var root = Directory.CreateTempSubdirectory("plan-kept").FullName;
        string[] work() => [List("l1", Folder(root, "a")), List("l2", Folder(root, "b")), List("l3", Folder(root, "c")), List("l4", Folder(root, "d"))];

        // It set one itself as it started.
        var planned = new ScriptedServer([Sse.ToolCall("p1", "plan", Two), .. work(), Sse.ToolCall("p2", "plan", """{"now":3}"""), Sse.Text("Done.")]);
        var observer = new RecordingObserver();
        await new AgentSession(new OpenAiChatClient(new HttpClient(planned), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), NewContext(), "sys").RunAsync("go", observer, CancellationToken.None);
        Assert.DoesNotContain(observer.Notices, n => n.Contains("show its plan"));

        // The chat's plan from the message before is still under way.
        var ctx = NewContext();
        ctx.Plan = new AgentPlan([new PlanStep("Read", "Read the files"), new PlanStep("Build", "Write the page")], 0);
        var carried = new ScriptedServer([.. work(), Sse.Text("Done."), Sse.ToolCall("p2", "plan", """{"now":3}""")]);
        var later = new RecordingObserver();
        await new AgentSession(new OpenAiChatClient(new HttpClient(carried), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys").RunAsync("carry on", later, CancellationToken.None);
        Assert.DoesNotContain(later.Notices, n => n.Contains("show its plan"));
        Assert.DoesNotContain(carried.Requests, r => r.ToJsonString().Contains("plan bar is empty"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task A_plan_asked_about_before_any_work_stays_as_it_is()
    {
        // "Here is my plan: does it suit you?" The plan waits for the user's answer.
        var server = new ScriptedServer(Sse.ToolCall("p1", "plan", Two), Sse.Text("1. Read 2. Build. Does this suit you?"));
        var ctx = NewContext();
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys");

        await session.RunAsync("go", new RecordingObserver(), CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(0, ctx.Plan!.At);
    }

    [Fact]
    public void A_chat_keeps_its_plan()
    {
        var chat = new SavedChat { Plan = new AgentPlan([new PlanStep("Read", "Read the files")], 1) };
        var again = JsonSerializer.Deserialize(JsonSerializer.Serialize(chat, ChatJson.Default.SavedChat), ChatJson.Default.SavedChat)!;
        Assert.Equal("Read", again.Plan!.Steps[0].Word);
        Assert.True(again.Plan.Finished);
    }

    [Fact]
    public void Side_tasks_have_no_plan()
    {
        var names = typeof(AgentSession).GetField("NotInSideTasks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as string[];
        Assert.Contains("plan", names!);
    }
}
