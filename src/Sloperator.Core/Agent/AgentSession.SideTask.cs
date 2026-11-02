using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

public sealed partial class AgentSession
{
    /// <summary>At most this many replies in a side task.</summary>
    internal const int MaxSideTaskSteps = 50;

    /// <summary>Tools a side task doesn't get: another side task, and those that act on the main conversation.</summary>
    private static readonly string[] NotInSideTasks = [SideTaskTool.ToolName, "reset_context", "capture_result"];

    /// <summary>What the side task is told, by whether its answer is returned (<paramref name="output"/>).</summary>
    internal static string SideTaskPrompt(bool output) =>
        "You are doing a side task for Sloperator, a desktop agent: a job handed to you in a conversation of its own, so that its steps " +
        "stay out of the main conversation. You don't see that conversation, and it waits until you finish: the task below is all you have. " +
        "Do it with your tools. " +
        (output
            ? "When it is done, answer with what the task asks for: that answer is all that goes back."
            : "Nothing you write goes back: do what the task asks, then end with a short line saying it is done.");

    /// <summary>
    /// A side task (the user, 2026-11-02): a job done on the same server and model in a conversation of its
    /// own, with <paramref name="toolNames"/> or else the run's tools but those in <see cref="NotInSideTasks"/>.
    /// It runs where it is called, never beside the main conversation, which waits for it, and nothing of it
    /// joins that conversation. The model's side_task calls run it (<see cref="ToolContext.SideTask"/>), and
    /// so does Auto pricing lookup. Each call it makes is a line of <paramref name="progress"/>; a call that
    /// needs the user's OK gets its own card to ask on. With <paramref name="output"/> its final answer is the
    /// result; without, the result only says it is done (output is off unless asked for). It ends when the
    /// model answers without calls, when a call of a tool that worked is <paramref name="done"/>, or after
    /// <see cref="MaxSideTaskSteps"/> replies. A server's failure ends it, not the run; the user's Stop ends both.
    /// </summary>
    private async Task<ToolResult> RunSideTaskAsync(string task, bool output, IReadOnlyList<string>? toolNames, Func<Tool, bool>? done,
        Action<string>? progress, IAgentObserver observer, CancellationToken ct)
    {
        var taskTools = toolNames is null
            ? tools.All.Where(t => !NotInSideTasks.Contains(t.Name)).ToList()
            : [.. toolNames.Select(tools.Find).OfType<Tool>()];
        var definitions = tools.Definitions().Where(d => taskTools.Any(t => t.Name == d.Name)).ToList();
        List<ChatMessage> conversation = [ChatMessage.System(SideTaskPrompt(output)), ChatMessage.User(task)];
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
                    if (done is not null)
                        return ToolResult.Error(said.Length > 0 ? said : "The model ended the side task without finishing it.");
                    return !output ? new ToolResult(NoOutputText)
                        : said.Length > 0 ? new ToolResult(said) : ToolResult.Error("The side task ended without an answer.");
                }
                foreach (var call in reply.ToolCalls!)
                {
                    var result = await RunSideTaskCallAsync(call, taskTools, progress, observer, ct).ConfigureAwait(false);
                    var text = result.Image is not null ? result.Text + "\n(A picture came with this, left out: a side task gets text only.)" : result.Text;
                    conversation.Add(ChatMessage.ToolResult(call.Id, text));
                    if (!result.IsError && done is not null && ToolCallRepair.ResolveTool(call.Name, tools) is { } tool && done(tool))
                        return new ToolResult(NoOutputText);
                }
            }
            return ToolResult.Error($"The side task ended after {MaxSideTaskSteps} replies without finishing.");
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or IOException)
        {
            return ToolResult.Error("The side task stopped: " + ex.Message);
        }
    }

    /// <summary>What a side task without output returns.</summary>
    internal const string NoOutputText = "The side task is done. It returns nothing: output wasn't enabled.";

    /// <summary>One call a side task makes: a line of <paramref name="progress"/>, or a card of its own when it needs the user's OK.</summary>
    private async Task<ToolResult> RunSideTaskCallAsync(ToolCall call, List<Tool> taskTools, Action<string>? progress, IAgentObserver observer, CancellationToken ct)
    {
        if (ToolCallRepair.ResolveTool(call.Name, tools) is not { } tool || !taskTools.Contains(tool))
            return ToolResult.Error($"There is no tool {call.Name} in this side task: you have {string.Join(", ", taskTools.Select(t => t.Name))}.");
        JsonObject args;
        string summary;
        try
        {
            (args, summary) = PrepareCall(call, tool);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Error(ArgumentProblem(ex, tool));
        }
        var asked = NeedsApproval(tool);
        if (asked)
        {
            if (!await observer.RequestApprovalAsync(call, tool, summary, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var denied = ToolResult.Error(DeclinedText);
                observer.OnToolFinished(call, denied);
                return denied;
            }
            observer.OnToolStarted(call, summary);
        }
        else
            progress?.Invoke(summary);
        var outer = toolContext.Progress;
        toolContext.Progress = asked ? text => observer.OnToolProgress(call, text) : progress;
        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(args, toolContext, ct).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            result = ToolResult.Error(ArgumentProblem(ex, tool));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = ToolResult.Error("Error: " + ex.Message);
        }
        finally
        {
            toolContext.Progress = outer;
        }
        if (asked)
            observer.OnToolFinished(call, result);
        return result;
    }
}
