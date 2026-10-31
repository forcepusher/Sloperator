using Sloperator.Core.Llm;
using Sloperator.Core.Tools;
using Sloperator.Core.Web;

namespace Sloperator.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // ---- Auto: the side panel follows the agent's work ------------------------------------------

    private const string AutoName = "Auto";

    /// <summary>Auto is picked in the Side panel dropdown: the panel shown follows what the agent does (<see cref="AutoRule"/>).</summary>
    internal bool IsAuto => SelectedSidePanel?.Name == AutoName;

    /// <summary>The panel Auto shows now; null while it shows what it will (<see cref="AutoPanelViewModel"/>).</summary>
    private SidePanelOption? _autoShown;

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

    /// <summary>The newest change of the chat's list (<see cref="Core.Diffs.FileChanges.LastChange"/>) that Auto has seen.</summary>
    private int _autoSeen;

    /// <summary>Auto picked: File changes starts reading, and the panel of the chat's latest call that has one opens, else what Auto will show.</summary>
    private ISidePanel StartAuto()
    {
        _autoChanges = new FileChangesViewModel(this);
        _autoSeen = Changes.LastChange?.Number ?? 0;
        var target = FromChat();
        _autoShown = OptionOf(target);
        var panel = OpenForAuto(_autoShown);
        ShowIn(panel, target?.Path);
        return panel;
    }

    /// <summary>Auto left (another panel picked, the window closed): its File changes goes, unless it is the panel going anyway.</summary>
    private void EndAuto(ISidePanel? leaving)
    {
        if (_autoChanges is { } changes && !ReferenceEquals(changes, leaving))
            changes.Dispose();
        _autoChanges = null;
        _autoShown = null;
    }

    /// <summary>A call started: under Auto, the panel that shows it as it goes (a command's lines, a shot being made).</summary>
    private void AutoStarted(ToolCall call)
    {
        if (IsAuto && AutoRule.AtStart(call.Name, AutoRule.Args(call.Arguments), _paths.PicturesDir, _paths.VideosDir) is { } target)
            Follow(target);
    }

    /// <summary>A call ended: under Auto, the panel that shows what it left (the file it changed, what it made).</summary>
    private void AutoEnded(ToolCall call, ToolResult result)
    {
        // The file a write_file or edit_file call changed is the list's newest change, new since the
        // last call (its list was kept before the call's end was told).
        string? written = null;
        if (Changes.LastChange is { } last && last.Number != _autoSeen)
            (written, _autoSeen) = (last.Path, last.Number);
        if (IsAuto && AutoRule.AtEnd(call.Name, AutoRule.Args(call.Arguments), result.IsError, written, result.Files,
                Changes.Last(PreviewPage.IsPage), _paths.PicturesDir, _paths.VideosDir) is { } target)
            Follow(target);
    }

    /// <summary>Another chat on screen (opened, new, its first message undone): under Auto, the panel of its latest call that has one.</summary>
    private void AutoChatChanged()
    {
        _autoSeen = Changes.LastChange?.Number ?? 0;
        if (IsAuto && SidePanel is not null)
            Follow(FromChat(), always: true);
    }

    /// <summary>
    /// Auto shows <paramref name="target"/>'s panel (what Auto will show with none): the panel open
    /// already shows the file, page or video in it; else the new one takes its place, in the same room,
    /// so the window and the bars of the chat stay as they are. It never puts anything in the box nor
    /// tells the model anything: a message goes as typed, as with every panel. While something picked
    /// in the panel waits in the box, the panel stays, for the message about it; Auto goes on after.
    /// </summary>
    /// <param name="always">Even to no panel (a chat with nothing a panel shows); a call with none keeps the panel as it is.</param>
    private void Follow(AutoTarget? target, bool always = false)
    {
        if (!IsAuto || _closed || SidePanel is not { } old || target is null && !always || Attachments.Any(a => a.Picked))
            return;
        var option = OptionOf(target);
        if (ReferenceEquals(option, _autoShown))
        {
            ShowIn(old, target?.Path);
            return;
        }
        var panel = OpenForAuto(option);
        ShowIn(panel, target?.Path);
        _autoShown = option;
        SidePanel = panel;
        if (!ReferenceEquals(old, _autoChanges))
            old.Dispose();
    }

    private SidePanelOption? OptionOf(AutoTarget? target) =>
        target is null ? null : SidePanelOptions.FirstOrDefault(o => o.Name == target.Panel && o.Open is not null && o.Name != AutoName);

    /// <summary>A panel for Auto to show: File changes the one kept reading, any other new.</summary>
    private ISidePanel OpenForAuto(SidePanelOption? option) =>
        option is null ? new AutoPanelViewModel()
        : option.Name == FileChangesName ? _autoChanges ??= new FileChangesViewModel(this)
        : option.Open!(this);

    /// <summary>The file, page, picture or video Auto shows in the panel, unless it shows it already.</summary>
    private static void ShowIn(ISidePanel panel, string? path)
    {
        if (path is null)
            return;
        switch (panel)
        {
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
        var lastPage = Changes.Last(PreviewPage.IsPage);
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
            var target = AutoRule.AtEnd(call.Name, args, call.State == ToolItemState.Error, Written(call.Name, args), call.SavedFiles, lastPage,
                             _paths.PicturesDir, _paths.VideosDir)
                         ?? AutoRule.AtStart(call.Name, args, _paths.PicturesDir, _paths.VideosDir);
            if (target is not null)
                return target;
        }
        return null;
    }

    /// <summary>The file an earlier write_file or edit_file call changed, as the chat's list of changes has it; null when it has none.</summary>
    private string? Written(string tool, System.Text.Json.Nodes.JsonObject args)
    {
        // On a server (host given) the file isn't in the list.
        if (tool is not ("write_file" or "edit_file") || Arg("host") is not null || Arg("path") is not { } path)
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

        string? Arg(string name) => args[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Trim('"').Length > 0 ? s.Trim().Trim('"') : null;
    }
}
