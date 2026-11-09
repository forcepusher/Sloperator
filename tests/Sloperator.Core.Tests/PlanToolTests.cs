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
