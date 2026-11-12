using System.Text.Json.Nodes;

namespace Sloperator.Core.Tools;

/// <summary>
/// Hands a self-contained job to a side task (the user, 2026-11-02): the same model in a conversation of its
/// own with the agent's tools, whose steps stay out of the agent's context. The agent waits for it: it never
/// runs beside the chat. Output is off by default: nothing comes back but that it is done, unless the call
/// enables output, when the side task's final answer is the result. The session runs it
/// (<see cref="ToolContext.SideTask"/>); the chat shows its card with the prompt as Input and, only with
/// output on, the answer as Output.
/// </summary>
public sealed class SideTaskTool : Tool
{
    public const string ToolName = "side_task";

    /// <summary>
    /// How the id of a side task Sloperator runs itself once the model has answered begins (Auto pricing lookup's,
    /// see <see cref="Agent.AgentSession.PriceLookup"/>): its card is no call of the model's, and comes after the reply.
    /// </summary>
    public const string AfterAnswerId = "side-";

    public override string Name => ToolName;
    public override string Description =>
        "Hand a self-contained job to a side task: you, in a fresh conversation of its own with your tools, whose steps stay out of your context " +
        "(e.g. summarize a folder or a script, look something up, tidy files). You wait until it finishes. It knows nothing of this chat: put " +
        "everything it needs in the prompt. Output is disabled by default and then nothing comes back but that it is done, so set output to true " +
        "whenever you want something returned from it (a summary, an answer, what it found), and say in the prompt what that should contain.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("task", Str("A few words naming the job, shown to the user, e.g. 'Summarize the build scripts'"), true),
        ("prompt", Str("The whole job: what to do, where, and, with output on, what to answer with"), true),
        ("output", Bool("True to get the side task's final answer back. Default false: nothing is returned"), false));
    public override string Describe(JsonObject args) => GetString(args, "task") is { Length: > 0 } task ? task : "Run a side task";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var prompt = RequireString(args, "prompt");
        if (ctx.SideTask is not { } run)
            return ToolResult.Error("Side tasks can't run here.");
        return await run(prompt, GetBool(args, "output"), ct).ConfigureAwait(false);
    }

    /// <summary>Whether a side_task call's arguments (JSON) enable output; false for anything else.</summary>
    public static bool OutputIn(string arguments) => ArgumentsOf(arguments) is { } args && GetBool(args, "output");

    /// <summary>A side_task call's prompt in its arguments (JSON), as its card's Input shows it; empty when there is none.</summary>
    public static string PromptIn(string arguments) => ArgumentIn(arguments, "prompt") is JsonValue v && v.TryGetValue<string>(out var prompt) ? prompt : "";

    private static JsonNode? ArgumentIn(string arguments, string key) => ArgumentsOf(arguments)?[key];

    private static JsonObject? ArgumentsOf(string arguments)
    {
        try { return JsonNode.Parse(arguments) as JsonObject; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
}
