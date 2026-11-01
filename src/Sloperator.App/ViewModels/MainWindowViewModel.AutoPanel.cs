using Sloperator.Core.Llm;
using Sloperator.Core.Tools;
using Sloperator.Core.Web;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- Auto: the side panel follows the agent's work ------------------------------------------

    /// <summary>Auto is picked in the Side panel dropdown: the panel shown follows what the agent does (<see cref="AutoRule"/>).</summary>
    internal bool IsAuto => SelectedSidePanel?.Name == SidePanelNames.Auto;

    /// <summary>The panel Auto shows now; null before the first (a new window, until the agent does something a panel shows) and after its ×.</summary>
    private SidePanelOption? _autoShown;

    private SidePanelOption? AutoShown
    {
        get => _autoShown;
        set
        {
            if (ReferenceEquals(_autoShown, value))
                return;
            _autoShown = value;
            OnPropertyChanged(nameof(SidePanelCaption));
            OnPropertyChanged(nameof(SidePanelName));
        }
    }

    /// <summary>
    /// What the Side panel dropdown says: under Auto, the panel it shows, "Auto (Terminal)", or "Auto" with
    /// none (the user, 2026-11-01: so the panel open can be told); else the pick's own word.
    /// </summary>
    public string SidePanelCaption => IsAuto ? AutoShown is { } shown ? $"{SidePanelNames.Auto} ({shown.Name})" : SidePanelNames.Auto : SelectedSidePanel?.Label ?? "Side panel";

    /// <summary>Every caption Auto can show, so the dropdown is as wide for all of them: it doesn't change width mid-task as Auto goes from panel to panel.</summary>
    private static readonly IReadOnlyList<string> AutoCaptions = [SidePanelNames.Auto, .. AutoRule.Panels.Select(p => $"{SidePanelNames.Auto} ({p})")];

    /// <summary>The captions the dropdown is as wide as: Auto's all, while Auto is picked; else the pick's own.</summary>
    public IReadOnlyList<string> SidePanelCaptionSlot => IsAuto ? AutoCaptions : [SidePanelCaption];

    /// <summary>
    /// The × under Auto: the panel closes and Auto stays picked (the user, 2026-11-01: × "should not switch
    /// the side panel to None mode"); it opens one again for the agent's next work a panel shows. What the
    /// panel put in the box goes with it, as when any panel closes.
    /// </summary>
    private void CloseAutoPanel()
    {
        if (SidePanel is not { } old)
            return;
        foreach (var picked in Attachments.Where(a => a.Picked).ToList())
            RemoveAttachment(picked);
        AutoShown = null;
        SidePanel = null;
        if (!ReferenceEquals(old, _autoChanges))
            old.Dispose();
    }

    /// <summary>
    /// The panel on screen: the one picked in the dropdown, or under Auto the one Auto shows (null
    /// while it waits for the agent), which is what a message sent with it open is about.
    /// </summary>
    internal SidePanelOption? ShownSidePanel => IsAuto ? _autoShown : SelectedSidePanel;

    /// <summary>
    /// File changes under Auto, kept while Auto is picked, shown or not: it reads each change as it comes,
    /// so the one that brings it up is lit and scrolled to as with the panel open all along (opened only
    /// then, its first read has no "just now", and nothing lit up).
    /// </summary>
    private FileChangesViewModel? _autoChanges;

    /// <summary>
    /// Auto picked: File changes starts reading, and the panel of the chat's latest call that has one
    /// opens; with none, no panel (the user, 2026-11-01: an empty panel as the window opened "looks
    /// clunky"), until the agent does something a panel shows.
    /// </summary>
    private ISidePanel? StartAuto()
    {
        _autoChanges = new FileChangesViewModel(this);
        var target = FromChat();
        AutoShown = OptionOf(target);
        if (AutoShown is null)
            return null;
        var panel = OpenForAuto(AutoShown);
        ShowIn(panel, target?.Path, opened: true);
        return panel;
    }

    /// <summary>Auto left (another panel picked, the window closed): its File changes goes, unless it is the panel going anyway.</summary>
    private void EndAuto(ISidePanel? leaving)
    {
        if (_autoChanges is { } changes && !ReferenceEquals(changes, leaving))
            changes.Dispose();
        _autoChanges = null;
        AutoShown = null;
    }

    /// <summary>A call started: under Auto, the panel that shows it as it goes (a command's lines, a shot being made).</summary>
    private void AutoStarted(ToolCall call)
    {
        if (IsAuto && AutoRule.AtStart(call.Name, AutoRule.Args(call.Arguments), new AutoPlaces(_paths.PicturesDir, _paths.VideosDir)) is { } target)
            Follow(target);
    }

    /// <summary>A call ended: under Auto, the panel that shows what it left (the file it changed, what it made).</summary>
    private void AutoEnded(ToolCall call, ToolResult result)
    {
        // The file the call wrote, by the path it was given. Not the list's newest change, which may be the
        // next call's already: a command then a file, the window's thread still opening the Terminal when
        // both had ended, and the file was taken for the command's, Auto staying on the Terminal (release
        // check, 2026-11-01).
        var args = AutoRule.Args(call.Arguments);
        if (IsAuto && AutoRule.AtEnd(call.Name, args, result.IsError, Written(call.Name, args), result.Files, Places()) is { } target)
            Follow(target);
    }

    /// <summary>Where Auto's panels look: the pictures and videos folders, the chat's last web page, the Code editor's folder (as it picks it).</summary>
    private AutoPlaces Places() => new(_paths.PicturesDir, _paths.VideosDir, Changes.Last(PreviewPage.IsPage), EditorFolder ?? Changes.Project());

    /// <summary>
    /// Another chat on screen (opened, new, its first message undone): under Auto, the panel of its latest
    /// call that has one; with none (New chat), the panel open stays, showing the new chat, as a panel picked
    /// by hand does, and the window keeps its size.
    /// </summary>
    private void AutoChatChanged()
    {
        if (IsAuto)
            Follow(FromChat());
    }

    /// <summary>
    /// Auto shows <paramref name="target"/>'s panel; with none (a call no panel shows) the panel stays as
    /// it is. The panel open already shows the file, page or video in it; else the new one takes its place,
    /// in the same room, so the window and the bars of the chat stay as they are. With no panel yet (a new
    /// window), the first opens as a panel picked by hand does, the window growing to the left, once: the
    /// room then stays. It never puts anything in the box nor tells the model anything: a message goes as
    /// typed, as with every panel. While something picked in the panel waits in the box, the panel stays,
    /// for the message about it; Auto goes on after.
    /// </summary>
    private void Follow(AutoTarget? target)
    {
        if (!IsAuto || _closed || target is null || Attachments.Any(a => a.Picked) || OptionOf(target) is not { } option)
            return;
        var old = SidePanel;
        if (ReferenceEquals(option, _autoShown) && old is not null)
        {
            ShowIn(old, target.Path);
            return;
        }
        var panel = OpenForAuto(option);
        ShowIn(panel, target.Path, opened: true);
        AutoShown = option;
        SidePanel = panel;
        if (old is not null && !ReferenceEquals(old, _autoChanges))
            old.Dispose();
    }

    private SidePanelOption? OptionOf(AutoTarget? target) =>
        target is null ? null : SidePanelOptions.FirstOrDefault(o => o.Name == target.Panel && o.Open is not null && o.Name != SidePanelNames.Auto);

    /// <summary>A panel for Auto to show: File changes the one kept reading, any other new.</summary>
    private ISidePanel OpenForAuto(SidePanelOption option) =>
        option.Name == SidePanelNames.FileChanges ? _autoChanges ??= new FileChangesViewModel(this) : option.Open!(this)!;

    /// <summary>
    /// The file, page, picture or video Auto shows in the panel, unless it shows it already. The Code
    /// editor opens the agent's file in the chat's folder when Auto brings it up (<paramref name="opened"/>);
    /// shown already, its Watch follows the agent's writes itself.
    /// </summary>
    private static void ShowIn(ISidePanel panel, string? path, bool opened = false)
    {
        if (path is null)
            return;
        switch (panel)
        {
            case CodeEditorViewModel editor:
                if (opened)
                    editor.OpenLit(path);
                break;
            case IFilePanel file when !string.Equals(file.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase):
                file.Show(path);
                break;
            case VideoEditorViewModel video when !string.Equals(Path.TrimEndingDirectorySeparator(video.Folder), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase):
                video.Open(path);
                break;
            case ImageEditorViewModel image:
                image.Show(path);
                break;
        }
    }

    /// <summary>
    /// What Auto shows for the chat as it is: the panel of its latest call that has one (a command, a
    /// file changed, a video worked on), as it would have shown it then; null with none.
    /// </summary>
    private AutoTarget? FromChat()
    {
        // A chat opened from History keeps its calls' arguments in the conversation, not on their cards.
        Dictionary<string, string>? given = null;
        var places = Places();
        foreach (var call in Items.OfType<ToolCallItem>().Reverse())
        {
            if (call.State == ToolItemState.Denied)
                continue;
            var arguments = call.Arguments;
            if (arguments.Length == 0)
            {
                given ??= _history.Where(m => m.ToolCalls is not null).SelectMany(m => m.ToolCalls!)
                    .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last().Arguments);
                arguments = given.GetValueOrDefault(call.CallId, "");
            }
            var args = AutoRule.Args(arguments);
            var target = AutoRule.AtEnd(call.Name, args, call.State == ToolItemState.Error, Written(call.Name, args), call.SavedFiles, places)
                         ?? AutoRule.AtStart(call.Name, args, places);
            if (target is not null)
                return target;
        }
        return null;
    }

    /// <summary>
    /// The file a write_file or edit_file call changed, as the chat's list of changes has it; null when it has
    /// none (a file on a server, Sloperator's own, one the call didn't get to write).
    /// </summary>
    private AutoWritten? Written(string tool, System.Text.Json.Nodes.JsonObject args) =>
        WrittenPath(tool, args) is { } path && Changes.Last(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) is not null
            ? new AutoWritten(path, Changes.IsNew(path))
            : null;

    private string? WrittenPath(string tool, System.Text.Json.Nodes.JsonObject args)
    {
        // On a server (host given) the file isn't in the list.
        if (tool is not ("write_file" or "edit_file") || AutoRule.Arg(args, "host") is not null || AutoRule.Arg(args, "path") is not { } path)
            return null;
        try
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        // Relative to the folder the agent worked in: the changed file it names.
        var tail = Path.DirectorySeparatorChar + path.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
        return Changes.Last(p => p.EndsWith(tail, StringComparison.OrdinalIgnoreCase));
    }
}
