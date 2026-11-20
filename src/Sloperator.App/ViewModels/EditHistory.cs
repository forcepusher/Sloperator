namespace Sloperator.App.ViewModels;

/// <summary>
/// The Video editor's Ctrl+Z and Ctrl+Y: the storyboard before each edit made in the panel (as many as
/// <paramref name="most"/>), the latest last, and what Ctrl+Z took back. Each is the storyboard's JSON with its clips'
/// .json files as they were then: a split stamps the halves made from their footage, and taken back, the clip is made
/// as before. What Ctrl+Z took back is made again only while the storyboard is as the last Ctrl+Z or Ctrl+Y left it:
/// an edit since, made here or by the agent, drops it, as in any editor.
/// </summary>
public sealed class EditHistory(int most = 50)
{
    /// <summary>The storyboard at one moment: its JSON, and each clip's .json file by its path.</summary>
    public sealed record State(string Json, IReadOnlyDictionary<string, string> Files);

    private readonly List<State> _back = [], _again = [];

    /// <summary>The storyboard as the last Ctrl+Z or Ctrl+Y left it, as Ctrl+Y needs it to be still.</summary>
    private string? _left;

    /// <summary>Before an edit: the storyboard as it is now, to go back to; kept once, however often it is asked.</summary>
    /// <param name="files">Its clips' .json files, read only when it is kept.</param>
    public void Keep(string json, Func<IReadOnlyDictionary<string, string>> files)
    {
        if (_back.Count > 0 && _back[^1].Json == json)
            return;
        Add(_back, new State(json, files()));
    }

    /// <summary>Whether Ctrl+Z has something to take back from the storyboard as it is.</summary>
    public bool CanUndo(string json) => _back.Exists(s => s.Json != json);

    /// <summary>Whether Ctrl+Y has something to make again: only while the storyboard is as Ctrl+Z left it.</summary>
    public bool CanRedo(string json) => _again.Count > 0 && json == _left;

    /// <summary>Ctrl+Z: the storyboard to go back to, null when there is none. The storyboard as it is is kept for Ctrl+Y.</summary>
    public State? Undo(string json, Func<IReadOnlyDictionary<string, string>> files)
    {
        // Kept at a press that changed nothing (a title clicked, not dragged): nothing there to take back.
        while (_back.Count > 0 && _back[^1].Json == json)
            _back.RemoveAt(_back.Count - 1);
        if (_back.Count == 0)
            return null;
        if (json != _left)
            _again.Clear();
        Add(_again, new State(json, files()));
        var to = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        return to;
    }

    /// <summary>Ctrl+Y: the storyboard as it was before the last Ctrl+Z, null when there is none or it changed since.</summary>
    public State? Redo(string json, Func<IReadOnlyDictionary<string, string>> files)
    {
        if (!CanRedo(json))
            return null;
        var to = _again[^1];
        _again.RemoveAt(_again.Count - 1);
        Add(_back, new State(json, files()));
        return to;
    }

    /// <summary>The storyboard as Ctrl+Z or Ctrl+Y left it, once it is put back.</summary>
    public void Left(string json) => _left = json;

    /// <summary>Another video is open: what was done to the one before can't be taken back in it.</summary>
    public void Clear()
    {
        _back.Clear();
        _again.Clear();
        _left = null;
    }

    private void Add(List<State> states, State state)
    {
        states.Add(state);
        if (states.Count > most)
            states.RemoveAt(0);
    }
}
