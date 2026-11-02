using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

public sealed partial class AgentSession
{
    /// <summary>The name of a side task's card in the chat.</summary>
    public const string SideTaskName = "side_task";

    /// <summary>At most this many replies in a side task.</summary>
    internal const int MaxSideTaskSteps = 12;

    internal const string SideTaskPrompt =
        "You are doing a side task for Sloperator, a desktop agent: a small job of the app's own, apart from the user's chat, which you don't see. " +
        "Do it with your tools and nothing else; keep searches few and pages short (fetch_page find=).";

    /// <summary>
    /// A side task (the user, 2026-11-02): a temporary job of the app's, run after the run's answer and before
    /// the run ends, not beside it, on the same server and model in a conversation of its own with only
    /// <paramref name="toolNames"/>. Nothing of it joins the chat's context, so the answer stays the model's
    /// last word. The chat shows it as one <see cref="SideTaskName"/> card: <paramref name="title"/> as its line,
    /// <paramref name="task"/> as its Input, each call as its progress. One that is only to do something (keep
    /// prices) is <paramref name="done"/> when a call of that tool worked, and has no Output (the user,
    /// 2026-11-02); without <paramref name="done"/>, the model's answer is its Output. A failure's reason is
    /// its Output either way. It ends after <see cref="MaxSideTaskSteps"/> replies at most. A server's failure
    /// ends it, not the run; the user's Stop ends both.
    /// </summary>
    private async Task RunSideTaskAsync(string title, string task, IReadOnlyList<string> toolNames, Func<Tool, bool>? done,
        IAgentObserver observer, CancellationToken ct)
    {
        var taskTools = toolNames.Select(tools.Find).OfType<Tool>().ToList();
        var definitions = tools.Definitions().Where(d => taskTools.Any(t => t.Name == d.Name)).ToList();
        var card = new ToolCall
        {
            Id = "side-" + Guid.NewGuid().ToString("N")[..8],
            Name = SideTaskName,
            Arguments = new JsonObject { ["task"] = title, ["prompt"] = task }.ToJsonString(),
        };
        observer.OnToolStarted(card, title);
        List<ChatMessage> conversation = [ChatMessage.System(SideTaskPrompt), ChatMessage.User(task)];
        var outcome = ToolResult.Error($"The side task ended after {MaxSideTaskSteps} replies without finishing.");
        try
        {
            for (var step = 0; step < MaxSideTaskSteps; step++)
            {
                var reply = await CompleteWithReconnectAsync(conversation, definitions, StepOptions(), observer, null, ct).ConfigureAwait(false);
                TextToolCallParser.TryExtract(reply, tools);
                conversation.Add(reply);
                if (!reply.HasToolCalls)
                {
                    var said = (reply.Content ?? "").Trim();
                    outcome = done is null && said.Length > 0 ? new ToolResult(said)
                        : ToolResult.Error(said.Length > 0 ? said : "The model ended the side task without finishing it.");
                    break;
                }
                var finished = false;
                foreach (var call in reply.ToolCalls!)
                {
                    var tool = ToolCallRepair.ResolveTool(call.Name, tools) is { } named && taskTools.Contains(named) ? named : null;
                    ToolResult result;
                    if (tool is null)
                        result = ToolResult.Error($"There is no tool {call.Name} in this side task: you have {string.Join(", ", taskTools.Select(t => t.Name))}.");
                    else
                    {
                        var args = ToolCallRepair.AsObject(call.Arguments) ?? [];
                        observer.OnToolProgress(card, tool.Describe(args));
                        try
                        {
                            result = await tool.ExecuteAsync(args, toolContext, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            result = ToolResult.Error("Error: " + ex.Message);
                        }
                    }
                    conversation.Add(ChatMessage.ToolResult(call.Id, result.Text));
                    if (tool is not null && !result.IsError && done?.Invoke(tool) == true)
                        (outcome, finished) = (new ToolResult(""), true);
                }
                if (finished)
                    break;
            }
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException)
        {
            outcome = ToolResult.Error("The side task stopped: " + ex.Message);
        }
        observer.OnToolFinished(card, outcome with { Image = null });
    }
}
