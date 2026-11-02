using System.Text.Json.Nodes;
using Avalonia.Threading;
using Sloperator.Core.Agent;
using Sloperator.Core.Diffs;
using Sloperator.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sloperator.App.ViewModels;

/// <summary>
/// A panel that opens on the left of the chat window, picked in the Side panel dropdown at the top of
/// it (Video editor, File changes). It works through the chat: what is picked in it goes into the
/// chat's box, and what the user sends from there is done by the agent in the chat, in view.
/// </summary>
public interface ISidePanel : IDisposable
{
    /// <summary>
    /// What a message sent while the panel is open carries: the line over its bubble, and what the
    /// model is told with it (the panel's work, what of it the files picked into the box are); null
    /// when the panel has nothing to say about it.
    /// </summary>
    (string Via, string Context)? About(IReadOnlyList<string> files);

    /// <summary>A message went: what was picked into the box went with it.</summary>
    void Sent();

    /// <summary>Files dropped on the panel; false when the panel doesn't take them (they then go with the next message).</summary>
    bool Drop(IReadOnlyList<string> paths);
}

/// <summary>A side panel that follows the agent's work, with Wrap and Watch in its top bar (File changes, Code editor, Terminal; see WatchToggles).</summary>
public interface IWatchPanel : ISidePanel
{
    /// <summary>Long lines wrap inside the panel; kept in settings.</summary>
    bool Wrap { get; set; }

    /// <summary>The panel goes to what the agent changes as it changes it; kept in settings.</summary>
    bool Watch { get; set; }

    /// <summary>
    /// Shown by Auto: the panel follows the agent whatever Watch says, as Auto is what follows it (the user,
    /// 2026-11-01). Its Watch button shows on, greyed, with why; Watch keeps the user's own setting, for the
    /// panel picked by hand.
    /// </summary>
    bool ByAuto { get; }

    /// <summary>What the Watch button shows: on under Auto, else Watch; set by its click.</summary>
    bool WatchShown { get; set; }

    /// <summary>The Watch button's tooltip, which says why it is greyed under Auto.</summary>
    string WatchTip { get; }
}

/// <summary>The Side panel dropdown's entries by name, as it shows them and as Auto picks among them (<see cref="AutoRule"/>).</summary>
public static class SidePanelNames
{
    public const string None = "None", Auto = "Auto", FileChanges = "File changes", FileInspector = "File inspector", CodeEditor = "Code editor",
        ImageEditor = "Image editor", VideoEditor = "Video editor", WebPreview = "Web preview", Terminal = "Terminal", CostEstimates = "Cost estimates";
}

/// <summary>The Watch button's words under Auto, for every panel that has one.</summary>
public static class AutoWatch
{
    public const string Tip = "Auto follows the agent. Pick this panel in the dropdown to turn Watch off.";
}

/// <summary>A side panel that shows one file, which a file's right-click menu opens in it (File inspector, Code editor).</summary>
public interface IFilePanel : ISidePanel
{
    /// <summary>The file shown; null before one is.</summary>
    string? Path { get; }

    /// <summary>Shows <paramref name="path"/> in the panel, in place of the file shown.</summary>
    void Show(string path);
}

/// <summary>
/// An entry of the Side panel dropdown: None, or a panel with how narrow it may get with its top
/// bar still in one row, splitter included, and how short the window may get with it open (0: as
/// short as the chat may). Every panel opens as wide (<see cref="SidePanelColumns.PanelWidth"/>).
/// </summary>
/// <param name="OpenIn">
/// For an <see cref="IFilePanel"/>: what every file's right-click menu calls it, "Open in Inspector"
/// (see <see cref="Views.FileMenu"/>); null for a panel no file opens in.
/// </param>
/// <param name="Opens">Which files it opens from that menu, by their path.</param>
/// <param name="Open">The panel, made for the window; Auto's gives none while nothing a panel shows has happened yet.</param>
public sealed record SidePanelOption(string Name, string Description, double MinWidth, double MinHeight, Func<MainWindowViewModel, ISidePanel?>? Open,
    string? OpenIn = null, Func<string, bool>? Opens = null)
{
    /// <summary>How wide the panel opens, splitter included; 0 for None.</summary>
    public double Width => Open is null ? 0 : SidePanelColumns.PanelWidth;

    /// <summary>What the dropdown says while it is picked: "Side panel" with none, else the panel's name.</summary>
    public string Label => Open is null ? "Side panel" : Name;

    public override string ToString() => Name;
}

public sealed partial class MainWindowViewModel
{
    // ---- the side panel, on the left of the chat ------------------------------------------------

    /// <summary>What the Side panel dropdown offers: nothing, Auto, or a panel that opens on the left of the chat.</summary>
    public IReadOnlyList<SidePanelOption> SidePanelOptions { get; } = WithAuto(
    [
        new(SidePanelNames.None, "", 0, 0, null),
        new(SidePanelNames.FileChanges, "Files the agent changed", 560, 0,
            vm => new FileChangesViewModel(vm)),
        new(SidePanelNames.FileInspector, "Look inside any file", 420, 0,
            vm => new FileInspectorViewModel(vm._previews, vm, vm._paths.PastedDir),
            OpenIn: "Inspector", Opens: path => !LocalFolder(path)),
        new(SidePanelNames.CodeEditor, "Browse and edit a folder's files", 800, 0,
            vm => new CodeEditorViewModel(vm),
            OpenIn: "Code editor", Opens: Core.Documents.FileInspector.IsText),
        new(SidePanelNames.ImageEditor, "Make pictures and redraw parts of them", 760, 0,
            vm => new ImageEditorViewModel(vm, vm._paths.PicturesDir, vm._paths.PastedDir)),
        new(SidePanelNames.VideoEditor, "Plan and make a video shot by shot", 770, 633,
            vm => new VideoEditorViewModel(vm._previews, vm, vm._paths.VideosDir, null)),
        new(SidePanelNames.WebPreview, "Play a web page or game as it is built", 640, 0,
            vm => new WebPreviewViewModel(vm, vm._paths.PastedDir, vm._paths.WebViewDir),
            OpenIn: "Web preview", Opens: Core.Web.PreviewPage.IsPage),
        new(SidePanelNames.Terminal, "The agent's commands and all they print", 600, 0,
            vm => new TerminalViewModel(vm, vm._paths.PastedDir)),
        new(SidePanelNames.CostEstimates, "What the chat cost, and the prices behind it", 560, 0,
            vm => new CostEstimatesViewModel(vm)),
    ]);

    /// <summary>
    /// The panels, with Auto after None (the default, the user's 2026-11-01): it shows each panel
    /// as the agent works in it (<see cref="AutoRule"/>), in a room as narrow and short as the widest
    /// and tallest of those panels needs, so going from one to another never moves the window.
    /// </summary>
    private static List<SidePanelOption> WithAuto(List<SidePanelOption> options)
    {
        var shown = options.Where(o => AutoRule.Panels.Contains(o.Name)).ToList();
        options.Insert(1, new(SidePanelNames.Auto, "Follows the agent from panel to panel", shown.Max(o => o.MinWidth), shown.Max(o => o.MinHeight),
            vm => vm.StartAuto()));
        return options;
    }

    /// <summary>A card's Inspect: the file in the File inspector panel.</summary>
    internal void Inspect(string path) => OpenIn(SidePanelOptions.First(o => o.Name == SidePanelNames.FileInspector), path);

    /// <summary>
    /// The panels a file's right-click menu opens it in: each that opens its kind, but the one open
    /// with it in it already.
    /// </summary>
    internal IEnumerable<SidePanelOption> PanelsFor(string path) => SidePanelOptions.Where(o => o.OpenIn is not null && o.Opens?.Invoke(path) == true
        && !(ReferenceEquals(o, SelectedSidePanel) && SidePanel is IFilePanel { Path: { } shown } && string.Equals(shown, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The file in that panel (a card's Inspect, Open in on its right-click menu), which opens in place
    /// of the panel open (if another). Under Auto too: the user's own pick, it stays until they pick
    /// Auto again, where Auto would take it away at the agent's next call.
    /// </summary>
    internal void OpenIn(SidePanelOption panel, string path)
    {
        if (!ReferenceEquals(SelectedSidePanel, panel))
            SelectedSidePanel = panel;
        (SidePanel as IFilePanel)?.Show(path);
    }

    /// <summary>The × on the open panel: None in the dropdown; under Auto the panel closes and Auto stays.</summary>
    internal void CloseSidePanel()
    {
        if (IsAuto)
            CloseAutoPanel();
        else
            SelectedSidePanel = SidePanelOptions[0];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidePanelCaption))]
    private SidePanelOption? _selectedSidePanel;

    /// <summary>The open panel, on the left of the chat; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidePanel))]
    [NotifyPropertyChangedFor(nameof(SidePanelName))]
    private ISidePanel? _sidePanel;

    public bool HasSidePanel => SidePanel is not null;

    /// <summary>The open panel's name, in the title bar over it (under Auto, the panel Auto shows); empty with none.</summary>
    public string SidePanelName => HasSidePanel ? ShownSidePanel?.Name ?? "" : "";

    partial void OnSelectedSidePanelChanged(SidePanelOption? oldValue, SidePanelOption? newValue)
    {
        var old = SidePanel;
        // What it put in the box goes with it: about a video no longer open, it would be sent bare.
        foreach (var picked in Attachments.Where(a => a.Picked).ToList())
            RemoveAttachment(picked);
        if (oldValue?.Name == SidePanelNames.Auto)
            EndAuto(old);
        // One panel for another in one change, so the window goes from the one to the other
        // without closing between.
        SidePanel = newValue?.Open is { } open && !_closed ? open(this) : null;
        old?.Dispose();
    }

    /// <summary>
    /// The panel picked when the window opens (Auto), before it is first shown: Auto starts (its File
    /// changes reading), with no panel in a new window; one picked by hand would open with the window.
    /// </summary>
    internal void OpenSidePanel()
    {
        if (SidePanel is null && SelectedSidePanel?.Open is { } open && !_closed)
            SidePanel = open(this);
    }

    /// <summary>
    /// What is picked in the side panel, into the box (one picked there before taken out): the
    /// user then says what to do with it. A piece not made yet has no file: it goes as its place.
    /// One picked again under another caption (more lines of a file in the File changes panel) is put back with it.
    /// </summary>
    internal void Pick(IReadOnlyList<(string Path, string Caption)> picked)
    {
        foreach (var old in Attachments.Where(a => a.Picked && !picked.Any(p => string.Equals(p.Path, a.Path, StringComparison.OrdinalIgnoreCase) && p.Caption == a.Caption)).ToList())
            RemoveAttachment(old);
        foreach (var (path, caption) in picked)
        {
            if (Attachments.Any(a => a.Picked && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            // The same file put in the box by the user goes as the pick: left there, the pick wasn't
            // added and the message lost what was picked in it (Sloperator's own review of the File changes panel).
            foreach (var same in Attachments.Where(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
                RemoveAttachment(same);
            // After the other picks, before the user's own files: what comes from the panel goes first (the user, 2026-10-26).
            Attachments.Insert(Attachments.Count(a => a.Picked), new AttachmentItem(path, RemoveAttachment, caption));
        }
    }

    /// <summary>The paths picked in the side panel that are in the box now.</summary>
    internal IReadOnlyList<string> Picked => Attachments.Where(a => a.Picked).Select(a => a.Path).ToList();

    /// <summary>
    /// A message from the side panel (Make the video): into this chat as if typed in the box, with
    /// what the panel says about it. With <paramref name="withPicks"/> (Generate on a piece), about
    /// what is picked in the box, which goes with it as when the user sends: "Make it." sent bare was
    /// about the whole video, and the model was asked to make all of it.
    /// </summary>
    internal void SendFromSidePanel(string text, bool withPicks = false)
    {
        if (_closed)
            return;
        if (!withPicks)
        {
            _ = SendAsync(WithSidePanel(new UserInput(text), [], fromPanel: true));
            return;
        }
        var picked = Attachments.Where(a => a.Picked).ToList();
        var input = WithSidePanel(new UserInput(text, picked.Where(a => File.Exists(a.Path) || Directory.Exists(a.Path)).Select(a => a.Path).ToList()), Picked, fromPanel: true);
        foreach (var pick in picked)
            RemoveAttachment(pick);
        _ = SendAsync(input);
    }

    /// <summary>
    /// A message as the model gets it while a panel is open: with what the panel says about it and what
    /// was picked in it, which then goes with it (none for a message of the panel's own: what is picked
    /// stays in the box for the next), and the cards of those as the box showed them. A message the
    /// user typed with nothing picked goes as it is: no card, no word of the panel (the user,
    /// 2026-10-28: a card of what they never picked, with no × to take it off, was context they
    /// didn't choose). The panel's own messages (Make the video) always say what the panel shows.
    /// </summary>
    private UserInput WithSidePanel(UserInput input, IReadOnlyList<string> picked, bool fromPanel = false)
    {
        if (SidePanel is not { } sidePanel || picked.Count == 0 && !fromPanel)
            return input;
        var cards = Attachments.Where(a => a.Picked && picked.Contains(a.Path, StringComparer.OrdinalIgnoreCase))
            .Select(a => new SidePanelCard(a.Title, Path: a.Path)).ToList();
        var name = ShownSidePanel?.Name ?? "Side panel";
        var about = sidePanel.About(picked) ?? (name, $"[The {name} panel is open beside the chat, with nothing in it yet.]");
        if (picked.Count > 0)
            sidePanel.Sent();
        return input with { Via = about.Via, Context = about.Context, Cards = cards };
    }

    /// <summary>
    /// A file about to be written over (a shot made again): the chat's players of it, and the
    /// side panel's, stop; their playbacks, still holding it, are handed back to be ended.
    /// </summary>
    internal List<IDisposable> LetGo(string path)
    {
        var playing = new List<IDisposable>();
        foreach (var preview in Items.SelectMany(i => i.Previews).OfType<PlayerPreview>()
                     .Where(p => string.Equals(Path.GetFullPath(p.Path), path, StringComparison.OrdinalIgnoreCase)))
            if (preview.LetGo() is { } playback)
                playing.Add(playback);
        if (SidePanel is VideoEditorViewModel video && video.LetGo(path) is { } shown)
            playing.Add(shown);
        return playing;
    }

    // ---- the files the agent changed in this chat (the File changes panel) -------------------------

    /// <summary>The files the agent changed in the chat on screen, each with what it held before.</summary>
    internal FileChanges Changes { get; private set; } = null!;

    /// <summary>How the side panels show things (File changes' Wrap and Watch), saved as the user leaves them.</summary>
    internal PanelSettings Panels => _settings.Panels;

    internal void SavePanels() => SaveSettings();

    /// <summary>On the window's thread: the list changed, or another chat's took its place.</summary>
    internal event Action? ChangesUpdated;

    /// <param name="editorFolder">The folder the chat had open in the Code editor panel (see <see cref="EditorFolder"/>).</param>
    /// <param name="commands">The chat's commands as the Terminal panel shows them; a new log when it has none yet.</param>
    private void UseChanges(FileChanges changes, string? editorFolder = null, Core.Tools.CommandLog? commands = null)
    {
        UseCommands(commands ?? new Core.Tools.CommandLog());
        EditorFolder = editorFolder;
        if (Changes is not null)
            Changes.Changed -= OnChanges;
        Changes = changes;
        changes.Changed += OnChanges;
        ChangesUpdated?.Invoke();
    }

    // A run stopped by leaving the chat still writes into the chat's own list, never into this one.
    // File changes never opens by itself (the user, 2026-10-31: it widened the window mid-task): it
    // shows the changes once picked in the Side panel dropdown.
    private void OnChanges(FileChanges changes) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(changes, Changes))
            ChangesUpdated?.Invoke();
    });

    // ---- the commands the agent ran in this chat (the Terminal panel) ----------------------------

    /// <summary>The commands the agent ran in the chat on screen, with all they printed.</summary>
    internal Core.Tools.CommandLog Commands { get; private set; } = null!;

    /// <summary>On the window's thread: commands started, printed or ended, or another chat's took their place.</summary>
    internal event Action? CommandsUpdated;

    /// <summary>A note to the window's thread is on its way: lines come by the thousand, one note covers them.</summary>
    private int _commandsPosted;

    private void UseCommands(Core.Tools.CommandLog commands)
    {
        if (Commands is not null)
            Commands.Changed -= OnCommands;
        Commands = commands;
        commands.Changed += OnCommands;
        CommandsUpdated?.Invoke();
    }

    // On the tool's thread; a run stopped by leaving the chat still writes into the chat's own log.
    private void OnCommands(Core.Tools.CommandLog commands)
    {
        if (Interlocked.Exchange(ref _commandsPosted, 1) == 1)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _commandsPosted, 0);
            if (ReferenceEquals(commands, Commands))
                CommandsUpdated?.Invoke();
        }, DispatcherPriority.Background);
    }

    /// <summary>Where a chat's commands are kept: SloperatorData/chats/id/terminal.json, beside its pictures, so it goes with the chat.</summary>
    private string CommandsFile(string chatId) => Path.Combine(_chats.PicturesOf(chatId), "terminal.json");

    // ---- the Code editor panel's folder and unsaved files ---------------------------------------

    /// <summary>
    /// The folder the user opened in the Code editor panel in the chat on screen, saved with the chat;
    /// null while none was, and the panel shows the project the agent worked in.
    /// </summary>
    internal string? EditorFolder { get; set; }

    /// <summary>Files edited in the Code editor panel and not saved yet, by path: kept while the window is open, so closing the panel loses nothing.</summary>
    internal Dictionary<string, CodeDraft> CodeDrafts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a chat's list is kept: SloperatorData/chats/changes/id.json.</summary>
    private string ChangesFile(string chatId) => Path.Combine(_chats.Folder, "changes", Path.GetFileName(chatId) + ".json");

    /// <summary>Files dropped on the side panel: the panel takes them, or they go with the next message.</summary>
    internal bool DropOnSidePanel(IReadOnlyList<string> paths) => SidePanel?.Drop(paths) == true;

    /// <summary>The video tool's call running in this chat now: what it was given, and how it is getting on; null when none runs.</summary>
    internal (JsonObject Args, string Status)? VideoCall()
    {
        if (!IsBusy || Items.OfType<ToolCallItem>().LastOrDefault(t => t.State == ToolItemState.Running) is not { Name: "video" } call)
            return null;
        return ToolCallRepair.AsObject(call.Arguments) is { } args ? (args, call.Progress) : null;
    }
}
