using Sloperator.Core.Chats;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: whether a model goes on with a task or wraps it up, played again from a saved chat. The chat's
/// history up to BB_REPLAY_CUT messages (a point where the model answered with work it hadn't done) is sent
/// as it was, with the system prompt with and without the line that the whole request is the task, each
/// BB_REPLAY_TRIES times (3). Counted: replies that call a tool (it goes on) against ones that answer.
/// BB_REPLAY_CHAT is the chat's file in a SloperatorData\chats folder; BB_LIVE_URL, BB_LIVE_KEY, BB_LIVE_MODEL
/// and BB_LIVE_EFFORT pick the server, as in PriceLookupLiveTests. Run with
/// `-- xUnit.DiagnosticMessages=true` to see the replies, or set BB_REPLAY_LOG to a file to have them written there.
/// </summary>
public class StopReplayLiveTests
{
    private static readonly string? Chat = Environment.GetEnvironmentVariable("BB_REPLAY_CHAT");
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_LIVE_URL");

    [Fact]
    public async Task The_whole_request_line_keeps_the_model_going()
    {
        Assert.SkipWhen(Chat is null || Url is null, "Set BB_REPLAY_CHAT and BB_LIVE_URL to run.");
        var ct = TestContext.Current.CancellationToken;
        var saved = new ChatStore(Path.GetDirectoryName(Chat)!).Load(Path.GetFileNameWithoutExtension(Chat));
        Assert.NotNull(saved);
        var cut = int.Parse(Environment.GetEnvironmentVariable("BB_REPLAY_CUT") ?? saved.History.Count.ToString());
        var tries = int.Parse(Environment.GetEnvironmentVariable("BB_REPLAY_TRIES") ?? "3");
        var history = saved.History.Take(cut).ToList();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var server = new ServerSettings
        {
            BaseUrl = Url!, ApiKey = Environment.GetEnvironmentVariable("BB_LIVE_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("BB_LIVE_MODEL") ?? "", Effort = Environment.GetEnvironmentVariable("BB_LIVE_EFFORT") ?? "xhigh",
        };
        var probe = await ServerProbe.CheckAsync(http, server, ct);
        Assert.True(probe.Reachable, probe.Message);
        (server.BaseUrl, server.Model, server.EffortLevels) = (probe.BaseUrl!, probe.Model!, probe.Efforts ?? []);
        var connection = BackendConnection.Create(http, server);
        var tools = ToolRegistry.CreateDefault(includeScreenTools: true).Definitions();

        var prompt = Agent.SystemPrompt.Build("Windows", null, Path.GetTempPath(), "", webSearch: true);
        var line = prompt.IndexOf("All of the user's request is the task", StringComparison.Ordinal);
        Assert.True(line >= 0);
        var end = prompt.IndexOf("running that test is part of the check. ", line, StringComparison.Ordinal) + "running that test is part of the check. ".Length;
        var log = Environment.GetEnvironmentVariable("BB_REPLAY_LOG");
        var say = (string text) =>
        {
            TestContext.Current.SendDiagnosticMessage(text);
            if (log is not null) File.AppendAllText(log, text + Environment.NewLine);
        };
        foreach (var (name, system) in new[] { ("old", prompt.Remove(line, end - line)), ("new", prompt) })
        {
            var goesOn = 0;
            for (var i = 0; i < tries; i++)
            {
                var request = new List<ChatMessage> { ChatMessage.System(system) };
                request.AddRange(history);
                var reply = await connection.Client.CompleteAsync(request, tools, connection.Options, null, ct);
                var calls = reply.ToolCalls ?? [];
                if (calls.Count > 0)
                    goesOn++;
                say($"{name} {i + 1}: {(calls.Count > 0 ? "calls " + string.Join(", ", calls.Select(c => c.Name)) : "answers")}: " +
                    (reply.Content ?? "").ReplaceLineEndings(" / ")[..Math.Min(400, (reply.Content ?? "").Length)]);
            }
            say($"{name}: goes on {goesOn} of {tries}");
        }
    }
}
