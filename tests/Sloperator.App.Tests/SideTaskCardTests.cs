using Sloperator.App.ViewModels;
using Sloperator.Core.Agent;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>A side task's card: its prompt as the Input foldout, and an Output only when it returned something.</summary>
public sealed class SideTaskCardTests
{
    [Fact]
    public void A_side_tasks_prompt_is_its_Input_and_no_result_means_no_Output()
    {
        var card = new ToolCallItem("side-1", AgentSession.SideTaskName, "Look up the prices of m")
        {
            Arguments = """{"task":"Look up the prices of m","prompt":"Find the prices."}""",
        };
        Assert.Equal("Find the prices.", card.Input);
        Assert.True(card.HasInput);
        Assert.False(card.HasResult);
        // Other tools say it in their line.
        Assert.False(new ToolCallItem("1", "run_shell", "Run: dir") { Arguments = """{"prompt":"x"}""" }.HasInput);
        // Read back from a saved chat, whose cards keep no arguments.
        Assert.Equal("Find the prices.", new ToolCallItem("side-1", AgentSession.SideTaskName, "Look up") { SavedInput = "Find the prices." }.Input);
    }
}
