using System.Text.Json.Nodes;

namespace Sloperator.Core.Tools;

/// <summary>One point of the plan: a word for the bar, and what the step does for its tooltip.</summary>
public sealed record PlanStep(string Word, string Detail);

/// <summary>The agent's plan. <paramref name="At"/> is the step it is on, from 0; the number of steps when all are done.</summary>
public sealed record AgentPlan(List<PlanStep> Steps, int At)
{
    public bool Finished => At >= Steps.Count;
}

/// <summary>
/// The plan the user follows above the message box, as Claude Code's checklist (the user, 2026-11-05).
/// The whole plan goes in once and <c>now</c> moves it on, so a step costs a few tokens. Its calls
/// have no cards in the chat: the bar shows them.
/// </summary>
public sealed class PlanTool : Tool
{
    public const string ToolName = "plan";

    public override string Name => ToolName;
    public override string Description =>
        "Show your plan to the user as a progress bar above their message box, one point per step, and move it on as you work. " +
        "Set it as you start any task of several steps, and keep it easy to read: a handful of steps, one plain word each. " +
        "steps sets or replaces the plan; now is the step you are on, from 1, or one past the last when all are done. " +
        "With neither, it gives you the plan as it stands. steps=[] takes the plan away.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("steps", new JsonObject
        {
            ["type"] = "array",
            ["description"] = "The steps in order",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["word"] = Str("One word for the step, shown on the bar"),
                    ["detail"] = Str("A short sentence on what the step does, shown on hover"),
                },
                ["required"] = new JsonArray("word", "detail"),
            },
        }, false),
        ("now", Int("The step you are on, from 1; one past the last when all are done"), false));

    public override string Describe(JsonObject args) => "Update the plan";

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var steps = Steps(args);
        // GetNumber reads "2" too: a step given as a string left the bar where it was.
        var now = GetNumber(args, "now") is { } number ? (int)number : (int?)null;
        if (steps is { Count: 0 })
        {
            ctx.Plan = null;
            ctx.PlanChanged?.Invoke(null);
            return Task.FromResult(new ToolResult("The plan is gone from the bar."));
        }
        if ((steps ?? ctx.Plan?.Steps) is not { } list)
            return Task.FromResult(ToolResult.Error("There is no plan yet: give steps."));
        if (now is < 1 || now > list.Count + 1)
            return Task.FromResult(ToolResult.Error($"now is from 1 to {list.Count + 1}."));
        if (steps is null && now is null)
            return Task.FromResult(new ToolResult(Listed(ctx.Plan!)));
        var plan = new AgentPlan(list, now - 1 ?? 0);
        ctx.Plan = plan;
        ctx.PlanChanged?.Invoke(plan);
        return Task.FromResult(new ToolResult(steps is null ? Position(plan) : Listed(plan)));
    }

    private static List<PlanStep>? Steps(JsonObject args)
    {
        if (args["steps"] is not JsonArray array)
            return null;
        var steps = new List<PlanStep>();
        foreach (var item in array)
        {
            var word = item is JsonObject o ? GetString(o, "word")?.Trim() : null;
            if (string.IsNullOrEmpty(word))
                throw new ToolArgumentException("Every step needs a word.");
            steps.Add(new PlanStep(word, GetString((JsonObject)item!, "detail")?.Trim() ?? ""));
        }
        return steps;
    }

    /// <summary>
    /// Where the plan stands, and the call that moves it on next: Sonnet 5.5, asked for a plan, set four steps and
    /// did them all with the bar on the first (2026-11-12). Said with every plan call, it costs nothing between them.
    /// Nothing more on the last step: a run that ends its turn there has the bar finished for it (see
    /// <see cref="Agent.AgentSession"/>), and a plan call with the answer would make a call of the answer's reply.
    /// </summary>
    private static string Position(AgentPlan plan) =>
        plan.Finished ? "All steps of the plan are done."
        : $"Step {plan.At + 1} of {plan.Steps.Count}: {plan.Steps[plan.At].Word}." +
          (plan.At + 1 < plan.Steps.Count
              ? $" As you start step {plan.At + 2}, \"{plan.Steps[plan.At + 1].Word}\", call plan with just now={plan.At + 2}, along with that step's first call."
              : "");

    private static string Listed(AgentPlan plan) =>
        string.Join('\n', plan.Steps.Select((s, i) => $"{i + 1}. {s.Word}: {s.Detail}")) + '\n' + Position(plan);
}
