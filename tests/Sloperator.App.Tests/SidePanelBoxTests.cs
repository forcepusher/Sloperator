using System.Reflection;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// The Side panel dropdown in the chat's bar keeps one width whatever it says (the user, 2026-11-02: it
/// changed width with its label): every caption it can show fits the slot Auto's captions make, so none
/// widens it.
/// </summary>
public sealed class SidePanelBoxTests
{
    /// <summary>Every word the dropdown can say: each panel's name, Auto's captions, and "Side panel" with none open.</summary>
    private static IEnumerable<string> Captions() =>
        typeof(SidePanelNames).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!)
            .Append("Side panel")
            .Concat(MainWindowViewModel.SidePanelCaptionSlot).Distinct();

    [Fact]
    public void Every_caption_is_as_wide_as_the_slot() =>
        HeadlessApp.Run(() =>
        {
            var widths = Captions().ToDictionary(c => c, Width);
            Assert.True(widths.Values.Distinct().Count() == 1, string.Join(", ", widths.Select(w => $"{w.Key} {w.Value:F1}")));
            return 0;
        });

    private static double Width(string caption)
    {
        var text = new SlotText { Text = caption, Slot = MainWindowViewModel.SidePanelCaptionSlot };
        TextElement.SetFontWeight(text, FontWeight.SemiBold);
        text.Measure(Size.Infinity);
        return text.DesiredSize.Width;
    }
}
