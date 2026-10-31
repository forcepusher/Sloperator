using System.Collections.ObjectModel;
using System.Globalization;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Sloperator.Core.Tools;

namespace Sloperator.App.ViewModels;

/// <summary>A command in the Terminal panel's list: its command line, where it ran, how it ended and how long it took.</summary>
public sealed partial class CommandItem(CommandRun run) : ObservableObject
{
    public CommandRun Run { get; } = run;

    /// <summary>The command's first line, as the list shows it.</summary>
    public string Command { get; } = run.Command.Trim().Split('\n')[0].TrimEnd('\r');

    /// <summary>"This PC · exit 0 · 1.2 s", "admin@web1 · running · 0:12".</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>It ended other than with exit code 0: the status is in the error colour.</summary>
    [ObservableProperty]
    private bool _failed;

    public string Tip => $"{Run.Command.Trim()}\n{Run.Where}{(Run.Folder is { } f ? " · " + f : "")} · started {Run.Started.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}";

    /// <summary>Its status read again (it ended, or a second went by while it runs).</summary>
    public void Update()
    {
        Status = $"{Run.Where} · {TerminalViewModel.Ended(Run)}";
        Failed = Run.HasEnded && Run.ExitCode != 0;
    }

    public override string ToString() => Command;
}

/// <summary>
/// The Terminal panel, beside the chat: the commands the agent runs (run_shell on this PC, ssh on a
/// server), newest last, each with its command line, where it ran, its exit code and how long it took,
/// and the one picked shown whole beside the list as it prints, every line of it (the model gets only
/// the head and tail of a long one). Read-only: the commands take no typing. Lines selected in the
/// output go into the chat's box as "Lines 3-9 · npm test", to ask about them.
/// </summary>
public sealed partial class TerminalViewModel : ObservableObject, IWatchPanel
{
    private readonly MainWindowViewModel? _chat;
    private readonly string? _picksDir;
    private bool _picking;

    /// <summary>Characters of the lines picked the model is given at most, with the count of the rest (as the other panels' picks).</summary>
    private const int MaxPickedChars = 20_000;

    /// <param name="chat">The chat whose commands it shows, where what is picked goes; null to show <paramref name="log"/> alone (tests).</param>
    /// <param name="picksDir">Where lines picked are saved as a text file for the message (the chat's pasted files).</param>
    public TerminalViewModel(MainWindowViewModel? chat, string? picksDir, CommandLog? log = null)
    {
        _chat = chat;
        _picksDir = picksDir;
        _watch = chat?.Panels.WatchTerminal ?? true;
        _wrap = chat?.Panels.WrapTerminal ?? true;
        _clock = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            foreach (var item in Commands.Where(c => !c.Run.HasEnded))
                item.Update();
            if (Selected is { } selected && !selected.Run.HasEnded)
                OnPropertyChanged(nameof(Details));
        });
        if (chat is not null)
        {
            chat.CommandsUpdated += Refresh;
            chat.Attachments.CollectionChanged += OnBoxChanged;
        }
        _log = log ?? chat?.Commands;
        // Without a chat to pass its notes on, the log is listened to here, on the window's thread.
        if (chat is null && log is not null)
            log.Changed += _ => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private CommandLog? _log;

    /// <summary>Ticks while a command runs, for its running time.</summary>
    private readonly Avalonia.Threading.DispatcherTimer _clock;

    /// <summary>The chat's commands, oldest first.</summary>
    public ObservableCollection<CommandItem> Commands { get; } = [];

    public bool HasCommands => Commands.Count > 0;

    /// <summary>What the bar says with no command yet.</summary>
    public string EmptyText => "The commands the agent runs show here as they run. Select lines to ask about them.";

    /// <summary>The command shown beside the list; null with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(Details), nameof(FullCommand))]
    private CommandItem? _selected;

    public bool HasSelected => Selected is not null;

    /// <summary>The command shown, whole (a script of several lines too).</summary>
    public string FullCommand => Selected?.Run.Command.Trim() ?? "";

    /// <summary>Over its output: where it ran, in which folder, how it ended, how many lines it printed.</summary>
    public string Details => Selected?.Run is not { } run ? "" :
        $"{run.Where}{(run.Folder is { } f ? " · " + f : "")} · {Ended(run)} · {Lines(run.LineCount)}";

    private static string Lines(int n) => n == 1 ? "1 line" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} lines";

    /// <summary>"exit 0 · 1.2 s", "timed out after 60 s · 1:00", "running · 0:12".</summary>
    public static string Ended(CommandRun run)
    {
        if (!run.HasEnded)
            return $"running · {Core.Chats.ChatTimes.Duration(DateTime.Now - run.Started)}";
        var how = run.ExitCode is { } code ? $"exit {code}" : run.Ending ?? "ended";
        return run.Took is { } took && took > TimeSpan.Zero ? $"{how} · {Core.Chats.ChatTimes.Duration(took)}" : how;
    }

    /// <summary>The output of the command shown, read-only; lines come in at its end as it prints.</summary>
    [ObservableProperty]
    private TextDocument? _document;

    /// <summary>How many of the command's lines the document has.</summary>
    private int _shownLines;

    partial void OnSelectedChanged(CommandItem? value)
    {
        Unpick();
        _shownLines = 0;
        Document = value is null ? null : new TextDocument { UndoStack = { SizeLimit = 0 } };
        Append();
    }

    /// <summary>Raised when lines came in at the end of the output shown, so the view follows them with Watch on.</summary>
    internal event Action? Grew;

    /// <summary>The lines the command shown printed since last read, at the end of its output.</summary>
    private void Append()
    {
        if (Selected?.Run is not { } run || Document is not { } document)
            return;
        var lines = run.LinesFrom(_shownLines);
        if (lines.Count == 0)
            return;
        var text = (_shownLines == 0 ? "" : "\n") + string.Join("\n", lines);
        _shownLines += lines.Count;
        document.Insert(document.TextLength, text);
        OnPropertyChanged(nameof(Details));
        Grew?.Invoke();
    }

    /// <summary>The log changed (a command started, printed or ended) or another chat's came: the list and the output follow it.</summary>
    private void Refresh()
    {
        if (_closed)
            return;
        var log = _chat?.Commands ?? _log;
        if (!ReferenceEquals(log, _log))
        {
            _log = log;
            Commands.Clear();
            Selected = null;
        }
        var runs = log?.Runs ?? [];
        var added = false;
        for (var i = Commands.Count; i < runs.Count; i++)
        {
            Commands.Add(new CommandItem(runs[i]));
            added = true;
        }
        foreach (var item in Commands)
            item.Update();
        OnPropertyChanged(nameof(HasCommands));
        // Watching, the newest command is shown as it starts; with none shown, the newest anyway.
        if (Commands.Count > 0 && (Selected is null || added && Watch))
            Selected = Commands[^1];
        else
            Append();
        if (Commands.Any(c => !c.Run.HasEnded))
            _clock.Start();
        else
            _clock.Stop();
        OnPropertyChanged(nameof(Details));
    }

    // ---- Wrap and Watch -----------------------------------------------------------------------

    /// <summary>Long lines wrap inside the panel; kept in settings.</summary>
    [ObservableProperty]
    private bool _wrap;

    partial void OnWrapChanged(bool value)
    {
        if (_chat is null)
            return;
        _chat.Panels.WrapTerminal = value;
        _chat.SavePanels();
    }

    /// <summary>The newest command is shown as it starts, its output kept scrolled to its end; kept in settings.</summary>
    [ObservableProperty]
    private bool _watch;

    partial void OnWatchChanged(bool value)
    {
        if (_chat is not null)
        {
            _chat.Panels.WatchTerminal = value;
            _chat.SavePanels();
        }
        // Turned on, it goes to the newest command's end.
        if (value && Commands.Count > 0)
        {
            if (Selected != Commands[^1])
                Selected = Commands[^1];
            else
                Grew?.Invoke();
        }
    }

    // ---- lines picked into the chat's box -------------------------------------------------------

    /// <summary>The lines picked: the file they were saved to for the message, the command's number, the place, the text.</summary>
    private (string File, CommandRun Run, int From, int To, string Text)? _pick;

    /// <summary>Raised to clear the output's selection: what was picked was sent, unpicked or taken off the box.</summary>
    public event Action? Unpicked;

    /// <summary>
    /// Lines <paramref name="from"/> to <paramref name="to"/> (from 1) of the output shown selected: saved as
    /// a text file and put in the box as "Lines 3-9 · npm test" ("Line 4"); none (0) takes them out.
    /// </summary>
    public void PickLines(int from, int to)
    {
        if (from < 1 || to < from || Selected?.Run is not { } run || _picksDir is null || Document is not { } document)
        {
            Unpick();
            return;
        }
        to = Math.Min(to, document.LineCount);
        var text = document.GetText(document.GetLineByNumber(from).Offset, document.GetLineByNumber(to).EndOffset - document.GetLineByNumber(from).Offset);
        if (_pick is { } same && same.Run == run && same.From == from && same.To == to)
            return;
        string file;
        try
        {
            Directory.CreateDirectory(_picksDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            file = Path.Combine(_picksDir, $"Terminal {stamp}.txt");
            for (var n = 2; File.Exists(file); n++)
                file = Path.Combine(_picksDir, $"Terminal {stamp} ({n}).txt");
            File.WriteAllText(file, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _chat?.AddNotice($"The selected lines could not be saved: {ex.Message}", isError: true, ex);
            return;
        }
        DropPick();
        _pick = (file, run, from, to, text);
        _picking = true;
        try { _chat?.Pick([(file, $"{Place(from, to)} · {Short(run.Command)}")]); }
        finally { _picking = false; }
    }

    /// <summary>"Lines 3-9", "Line 4".</summary>
    public static string Place(int from, int to) => from == to ? $"Line {from}" : $"Lines {from}-{to}";

    /// <summary>A command's first line, cut to what a card's caption holds.</summary>
    public static string Short(string command)
    {
        var line = command.Trim().Split('\n')[0].TrimEnd('\r');
        return line.Length <= 40 ? line : line[..39] + "…";
    }

    /// <summary>The lines picked before go: their file, which wasn't sent, with them.</summary>
    private void DropPick()
    {
        if (_pick is { } pick)
        {
            try { File.Delete(pick.File); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _pick = null;
    }

    /// <summary>Esc, a click that selects nothing, another command: the lines leave the box. False when there were none.</summary>
    public bool Unpick()
    {
        if (_pick is null)
            return false;
        DropPick();
        _picking = true;
        try { _chat?.Pick([]); }
        finally { _picking = false; }
        Unpicked?.Invoke();
        return true;
    }

    private void OnBoxChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_picking || _pick is null || e.OldItems is null || !e.OldItems.OfType<AttachmentItem>().Any(a => a.Picked))
            return;
        if (_chat?.Attachments.Any(a => a.Picked) == true)
            return;
        DropPick();
        Unpicked?.Invoke();
    }

    /// <summary>
    /// What a message sent with lines picked carries: over its bubble the command and the lines; for the
    /// model, the command, where it ran and how it ended, and the lines whole, numbered as the panel shows them.
    /// </summary>
    public (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (_pick is not { } pick || !files.Contains(pick.File, StringComparer.OrdinalIgnoreCase))
            return null;
        var run = pick.Run;
        var context = new System.Text.StringBuilder(
            $"[The Terminal panel is open beside the chat, showing the output of a command you ran ({run.Where}{(run.Folder is { } f ? ", in " + f : "")}, {Ended(run)}, " +
            $"{Lines(run.LineCount)} in all, which the panel keeps whole):\n{run.Command.Trim()}\n" +
            $"They selected {char.ToLowerInvariant(Place(pick.From, pick.To)[0])}{Place(pick.From, pick.To)[1..]} of its output (numbered from 1), which is what they refer to " +
            $"(also in the file {pick.File} attached below):\n");
        var numbered = string.Join("\n", pick.Text.Split('\n').Select((l, i) => $"{pick.From + i}: {l.TrimEnd('\r')}"));
        context.Append(numbered.Length <= MaxPickedChars ? numbered : numbered[..MaxPickedChars] + $"\n[... {numbered.Length - MaxPickedChars:N0} more characters]");
        context.Append("\n]");
        return ($"Terminal · {Short(run.Command)} · {char.ToLowerInvariant(Place(pick.From, pick.To)[0])}{Place(pick.From, pick.To)[1..]}", context.ToString());
    }

    /// <summary>The message went with the lines: they are no longer picked (their file went with it, and stays).</summary>
    public void Sent()
    {
        if (_pick is null)
            return;
        _pick = null;
        Unpicked?.Invoke();
    }

    /// <summary>Files dropped on the panel go with the next message: it shows only the agent's commands.</summary>
    public bool Drop(IReadOnlyList<string> paths) => false;

    private bool _closed;

    public void Dispose()
    {
        _closed = true;
        _clock.Stop();
        if (_chat is not null)
        {
            _chat.CommandsUpdated -= Refresh;
            _chat.Attachments.CollectionChanged -= OnBoxChanged;
        }
        DropPick();
    }
}
