using System.Text.Json;
using System.Text.Json.Nodes;
using Sloperator.Core.Llm;

namespace Sloperator.Core.Tools;

/// <summary>
/// Hands a self-contained task to a helper agent with a fresh context of its own, on the same server and
/// model: it searches the web, reads pages, files and folders, and answers with its final report, which
/// is all that comes back. The searching and reading stay out of the agent's own context. The helper
/// only looks: no screen, no changes, no subagent of its own. Auto pricing lookup runs it too (see
/// <see cref="Agent.PriceLookup"/>). The chat shows it as a "subagent" card named after its task, with the
/// prompt as its Input and the answer as its Output (the user, 2026-11-02).
/// </summary>
public sealed class SubagentTool : Tool
{
    /// <summary>At most this many replies: tool calls, then the answer (the last one gets no tools).</summary>
    internal const int MaxSteps = 30;

    public override string Name => "subagent";
    public override string Description =>
        "Hand a self-contained research task to a helper agent with a fresh context of its own: it searches the web, reads pages, files and folders, " +
        "and answers with a final report, which is all you get back, so your own context stays small. It can't see or control the screen or change anything. " +
        "It knows nothing of this chat: put everything it needs in the prompt, and say what its answer should contain.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("task", Str("A few words naming the task, shown to the user, e.g. 'Find the Blender 5.2 bevel modifier API'"), true),
        ("prompt", Str("The whole task for the helper: what to find out, where to look, and what to answer with"), true));
    public override string Describe(JsonObject args) => GetString(args, "task") is { Length: > 0 } task ? task : "Run a subagent";

    /// <summary>The tools the helper has: those that only look, without the screen.</summary>
    internal static Tool[] HelperTools() => [new WebSearchTool(), new FetchPageTool(), new ReadFileTool(), new ListDirectoryTool()];

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var prompt = RequireString(args, "prompt");
        if (ctx.Client is not { } client)
            return ToolResult.Error("No model server to run a subagent on.");
        var answer = await RunAsync(client, ctx.Options ?? new ChatRequestOptions(), prompt, HelperTools(), ctx, ctx.Progress, ct).ConfigureAwait(false);
        return answer.Length > 0 ? new ToolResult(answer) : ToolResult.Error("The subagent gave no answer.");
    }

    /// <summary>
    /// Runs the helper on <paramref name="prompt"/> with <paramref name="tools"/> until it answers;
    /// <paramref name="progress"/> hears each step ("Search the web: …"). Returns its answer, thinking left out.
    /// </summary>
    public static async Task<string> RunAsync(OpenAiChatClient client, ChatRequestOptions options, string prompt, IReadOnlyList<Tool> tools, ToolContext ctx,
        Action<string>? progress, CancellationToken ct)
    {
        var definitions = tools.Select(t => new ToolDefinition { Name = t.Name, Description = t.Description, Parameters = t.Parameters }).ToList();
        var helper = new ChatRequestOptions { Model = options.Model, Effort = options.Effort, Extra = options.Extra, SendReasoning = options.SendReasoning, MaxTokens = options.MaxTokens };
        List<ChatMessage> conversation =
        [
            ChatMessage.System("You are a helper agent of Sloperator, a desktop agent, doing one task for it. Use your tools to find out what the task asks, " +
                               "keep searches few and pages short (fetch_page find=), then answer with a final report: only what was asked, with the sources it rests on."),
            ChatMessage.User(prompt),
        ];
        for (var step = 0; step < MaxSteps; step++)
        {
            var reply = await client.CompleteAsync(conversation, step < MaxSteps - 1 ? definitions : [], helper, null, ct).ConfigureAwait(false);
            conversation.Add(reply);
            if (reply.ToolCalls is not { Count: > 0 } calls)
            {
                var text = reply.Content ?? "";
                if (text.LastIndexOf("</think>", StringComparison.Ordinal) is var end and >= 0)
                    text = text[(end + "</think>".Length)..];
                return text.Trim();
            }
            foreach (var call in calls)
            {
                var tool = tools.FirstOrDefault(t => t.Name == call.Name);
                string result;
                try
                {
                    var callArgs = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? [];
                    if (tool is null)
                        result = $"There is no tool {call.Name}: you have {string.Join(", ", tools.Select(t => t.Name))}.";
                    else
                    {
                        progress?.Invoke(tool.Describe(callArgs));
                        result = (await tool.ExecuteAsync(callArgs, ctx, ct).ConfigureAwait(false)).Text;
                    }
                }
                catch (Exception ex) when (ex is JsonException or ToolArgumentException or IOException or UnauthorizedAccessException)
                {
                    result = "Error: " + ex.Message;
                }
                conversation.Add(ChatMessage.ToolResult(call.Id, result));
            }
        }
        return "";
    }
}
