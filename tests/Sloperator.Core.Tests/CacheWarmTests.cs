using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// The server's prompt cache kept from lapsing while a step's tool calls run: in the user's Unity chat two waits
/// of 420 and 480 seconds each had the next step write over 100k tokens to Anthropic's cache anew (2026-11-12).
/// </summary>
public sealed class CacheWarmTests
{
    /// <summary>A reply with the tokens its server counted: the prompt, how much of it came from the cache and went into it.</summary>
    private static string Counted(string sse, int prompt, int read, int written) =>
        sse.Replace("data: [DONE]\n\n", "") + "data: " + new JsonObject
        {
            ["choices"] = new JsonArray(),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = 5,
                ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = read, ["cache_write_tokens"] = written },
            },
        }.ToJsonString() + "\n\ndata: [DONE]\n\n";

    /// <summary>Whether the request comes after the wait: its result is in it (a note of Sloperator's may follow it).</summary>
    private static bool EndsWithATool(JsonObject request) => request["messages"]!.AsArray().Any(m => m!["role"]!.GetValue<string>() == "tool");

    private static (AgentSession Session, OpenAiChatClient Client, RecordingObserver Observer) Session(ScriptedServer server)
    {
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null) { Spending = new Spending() };
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(client, new ChatRequestOptions(), ToolRegistry.CreateDefault(true), ctx, "sys") { KeepWarmEvery = TimeSpan.FromMilliseconds(300) };
        return (session, client, new RecordingObserver());
    }

    [Fact]
    public async Task While_a_long_call_runs_the_prompt_is_read_again_so_the_cache_holds_for_the_next_step()
    {
        // The first request writes the prompt to the cache; the model then waits longer than the cache lasts.
        var asked = 0;
        var server = new ScriptedServer
        {
            Responder = request => EndsWithATool(request) ? Counted(Sse.Text("Done."), 1050, 1000, 50)
                : Interlocked.Increment(ref asked) == 1 ? Counted(Sse.ToolCall("w1", "wait", """{"seconds":1.4}"""), 1000, 0, 1000)
                // Sent again: dropped as soon as its answer begins, which is after the server read its cache.
                : Sse.ThinkingOn("Wait for the download."),
        };
        var (session, client, observer) = Session(server);

        await session.RunAsync("Wait for the download, then say so.", observer, CancellationToken.None);

        // The same request went out again while the wait ran, more than once, and the step after it as ever.
        var first = server.Requests[0]["messages"]!.ToJsonString();
        var again = server.Requests.Skip(1).Count(r => r["messages"]!.ToJsonString() == first);
        Assert.InRange(again, 2, 5);
        Assert.Equal(again + 2, server.Requests.Count);
        Assert.Equal("Done.", session.History[^1].Content);
        // Said once a step, and what was read is counted as read from the cache: 1000 by the last step, 1000 each time.
        Assert.Single(observer.Notices, n => n.StartsWith("Kept this chat in the server's prompt cache"));
        var spent = Assert.Single(client.Spending!.Models);
        Assert.Equal(1000 * (again + 1), spent.CacheRead);
        Assert.Equal(1050, spent.CacheWrite + spent.Input);
    }

    [Fact]
    public async Task The_session_says_when_the_server_last_had_the_prompt_and_a_read_that_kept_it_warm_counts()
    {
        // Auto counts the cache's lifetime from it (AutoPick.CacheLapsed): a run stopped in a long wait was read a moment ago.
        AgentSession? running = null;
        var seen = new List<DateTime?>();
        var asked = 0;
        var server = new ScriptedServer
        {
            Responder = request =>
            {
                if (EndsWithATool(request))
                    return Counted(Sse.Text("Done."), 1050, 1000, 50);
                if (Interlocked.Increment(ref asked) == 1)
                    return Counted(Sse.ToolCall("w1", "wait", """{"seconds":1.4}"""), 1000, 0, 1000);
                lock (seen) seen.Add(running!.CacheUsedAt);
                return Sse.ThinkingOn("Wait for the download.");
            },
        };
        var (session, _, observer) = Session(server);
        running = session;
        Assert.Null(session.CacheUsedAt);

        await session.RunAsync("Wait for the download, then say so.", observer, CancellationToken.None);

        // At the first read it is the step's reply; at the second, the first read, some 300 ms on; then the last reply.
        Assert.True(seen.Count >= 2);
        Assert.NotNull(seen[0]);
        Assert.True(seen[1] - seen[0] >= TimeSpan.FromMilliseconds(200), $"{seen[1] - seen[0]}");
        Assert.True(session.CacheUsedAt > seen[^1]);
    }

    [Fact]
    public async Task A_server_that_writes_to_no_cache_is_left_alone()
    {
        // A local server: its cache doesn't lapse, and reading it again would only keep it busy.
        var server = new ScriptedServer { Responder = request => EndsWithATool(request) ? Sse.Text("Done.") : Counted(Sse.ToolCall("w1", "wait", """{"seconds":1}"""), 1000, 900, 0) };
        var (session, _, observer) = Session(server);

        await session.RunAsync("Wait a second.", observer, CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
        Assert.DoesNotContain(observer.Notices, n => n.Contains("prompt cache"));
    }

    [Fact]
    public async Task A_call_that_is_over_before_the_cache_would_lapse_reads_nothing_again()
    {
        var server = new ScriptedServer { Responder = request => EndsWithATool(request) ? Sse.Text("Done.") : Counted(Sse.ToolCall("w1", "wait", """{"seconds":0.1}"""), 1000, 0, 1000) };
        var (session, _, observer) = Session(server);
        session.KeepWarmEvery = TimeSpan.FromSeconds(30);

        await session.RunAsync("Wait a moment.", observer, CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
    }
}
