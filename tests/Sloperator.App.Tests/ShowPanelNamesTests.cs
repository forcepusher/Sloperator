using Sloperator.App.ViewModels;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>show_panel names the panels as the Side panel dropdown does: one added to the dropdown is one the agent can open.</summary>
public sealed class ShowPanelNamesTests
{
    [Fact]
    public void Show_panel_offers_every_panel_of_the_dropdown()
    {
        var dropdown = typeof(SidePanelNames).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Except([SidePanelNames.None, SidePanelNames.Auto]);
        Assert.Equal(dropdown.Order(), ShowPanelTool.Panels.Order());
    }
}
