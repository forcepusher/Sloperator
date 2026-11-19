using Sloperator.Core.Video;

namespace Sloperator.App.ViewModels;

public sealed partial class VideoEditorViewModel
{
    // ---- what Delete took out, kept while Ctrl+Z can bring it back -------------------------------------------------
    // A clip or a sound deleted went straight to the Recycle Bin, and Ctrl+Z after it put the storyboard back with a
    // clip that had no file: made by a model, it was to be made again. Its files are set aside in the video's folder
    // instead, come back with Ctrl+Z, go again with Ctrl+Y, and reach the Recycle Bin once the edits here can't be
    // taken back any more (another video opened, the chat's change, the panel closed).

    /// <summary>The folder in a video's folder where deleted pieces' files wait.</summary>
    internal const string AsideName = ".deleted";

    /// <summary>A file set aside: where it waits, and the piece's own file it goes with (a clip's .mp4 for its .json, its line and its versions).</summary>
    private sealed record Aside(string Now, string Owner);

    /// <summary>The files set aside, by where they were.</summary>
    private readonly Dictionary<string, Aside> _aside = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A deleted piece's file out of the way, to come back with Ctrl+Z; one outside the video's folder goes to the Recycle Bin as before.</summary>
    /// <param name="owner">The piece's own file, by which Ctrl+Z knows the piece is back.</param>
    private void SetAside(string file, string owner)
    {
        if (!File.Exists(file))
            return;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_board.Folder));
        var relative = Path.GetRelativePath(folder, Path.GetFullPath(file));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            RecycleBin.Put(file);
            return;
        }
        var to = Path.Combine(folder, AsideName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        // One of that name set aside before (deleted, made anew, deleted again) is past bringing back.
        if (File.Exists(to))
            RecycleBin.Put(to);
        File.Move(file, to);
        _aside[file] = new Aside(to, owner);
    }

    /// <summary>The own file of every piece the storyboard has now: a clip's video, a frame's picture, a sound's file.</summary>
    private HashSet<string> PieceFiles() =>
        _board.Shots.Select(_board.ShotVideo).Concat(_board.Frames.Select(_board.FramePicture)).Concat(_board.Sounds.Select(_board.SoundFile))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// After Ctrl+Z or Ctrl+Y put a storyboard back: the files of the pieces it has come back from where they wait,
    /// and those of the pieces it hasn't go there again.
    /// </summary>
    private void BringBack()
    {
        if (_aside.Count == 0)
            return;
        var pieces = PieceFiles();
        foreach (var (was, aside) in _aside.ToList())
            try
            {
                if (pieces.Contains(aside.Owner))
                {
                    if (File.Exists(aside.Now) && !File.Exists(was))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(was)!);
                        File.Move(aside.Now, was);
                    }
                }
                else if (File.Exists(was))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(aside.Now)!);
                    File.Move(was, aside.Now, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// What was set aside goes to the Recycle Bin, from where it was (so Restore there puts it back in its place):
    /// the edits made here can't be taken back any more.
    /// </summary>
    private void EmptyAside()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (was, aside) in _aside)
            try
            {
                if (!File.Exists(aside.Now))
                    continue;
                var from = aside.Now;
                if (!File.Exists(was) && Directory.Exists(Path.GetDirectoryName(was)))
                {
                    File.Move(aside.Now, was);
                    from = was;
                }
                RecycleBin.Put(from);
                folders.Add(aside.Now[..(aside.Now.IndexOf(Path.DirectorySeparatorChar + AsideName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) + AsideName.Length + 1)]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        _aside.Clear();
        foreach (var folder in folders)
            ClearAsideFolder(folder);
    }

    /// <summary>A video's waiting folder, once nothing waits in it; what a Sloperator that ended suddenly left there goes to the Recycle Bin.</summary>
    private static void ClearAsideFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
                RecycleBin.Put(folder);
            else
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
    }

    /// <summary>The edits made here can't be taken back any more: none are kept, and what Delete set aside goes to the Recycle Bin.</summary>
    private void ForgetEdits()
    {
        _history.Clear();
        EmptyAside();
    }

    /// <summary>Every file that goes with a piece's own file: beside it under its name ("s3.mp4", "s3.json", "s3.line.flac"), and its versions.</summary>
    private static List<string> FilesWith(string own)
    {
        var files = new List<string>();
        try
        {
            var (folder, stem) = (Path.GetDirectoryName(own)!, Path.GetFileNameWithoutExtension(own) + ".");
            if (Directory.Exists(folder))
                files.AddRange(Directory.EnumerateFiles(folder).Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)));
            foreach (var version in Storyboard.FilesOf(own))
                if (File.Exists(version) && !files.Contains(version, StringComparer.OrdinalIgnoreCase))
                    files.Add(version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return files;
    }

    // ---- Delete, Ctrl+D, Ctrl+C and Ctrl+V on what is picked --------------------------------------------------------

    /// <summary>The clip picked on the timeline, as the rows have it now; null when what is picked is no clip.</summary>
    private ShotItem? PickedShot => Viewing is { Version: null, Piece: ShotItem picked } ? Shots.FirstOrDefault(s => s.Id == picked.Id) : null;

    /// <summary>The sound picked on the timeline, as the rows have it now; null when what is picked is no sound.</summary>
    private SoundItem? PickedSound => Viewing is { Version: null, Piece: SoundItem picked } ? Sounds.FirstOrDefault(s => s.Id == picked.Id) : null;

    /// <summary>Delete: the clip or sound picked goes, as Delete of its menu does; false when neither is picked or the video is being worked on.</summary>
    public bool DeletePicked()
    {
        if (Busy(_board.Folder))
            return false;
        if (PickedShot is { } shot)
            DeleteShot(shot);
        else if (PickedSound is { } sound)
            DeleteSound(sound);
        else
            return false;
        return true;
    }

    /// <summary>Ctrl+D: a copy of the clip picked right after it, as Duplicate of its menu does; false when no clip is picked.</summary>
    public bool DuplicatePicked()
    {
        if (Busy(_board.Folder) || PickedShot is not { } shot)
            return false;
        Duplicate(shot);
        return true;
    }

    /// <summary>The clip Ctrl+C took, by its id: Ctrl+V puts a copy of it after the clip under the playhead.</summary>
    private string? _copied;

    /// <summary>Whether Ctrl+V has a clip to paste: one was copied, and is still in the video.</summary>
    public bool CanPaste => _copied is not null && Shots.Any(s => s.Id == _copied);

    /// <summary>Copy of a clip's menu: it is the one Ctrl+V pastes.</summary>
    private void Copy(ShotItem shot)
    {
        _copied = shot.Id;
        Status = $"Copied {shot.Title}. Ctrl+V puts a copy after the clip under the playhead.";
    }

    /// <summary>Ctrl+C: the clip picked is the one Ctrl+V pastes; false when no clip is picked.</summary>
    public bool CopyPicked()
    {
        if (PickedShot is not { } shot)
            return false;
        Copy(shot);
        return true;
    }

    /// <summary>
    /// Ctrl+V: a copy of the clip Ctrl+C took, with its edits, after the clip under the playhead (after the last one
    /// with the playhead at the end); false when none was copied or it is gone.
    /// </summary>
    public bool Paste()
    {
        if (Busy(_board.Folder) || _copied is null || Shots.FirstOrDefault(s => s.Id == _copied) is not { } source)
            return false;
        var (starts, _) = _board.Timing();
        var under = Shots.LastOrDefault(s => s.Index < starts.Count && starts[s.Index] <= PlayPosition + 0.001) ?? Shots.LastOrDefault();
        Duplicate(source, after: under);
        return true;
    }
}
