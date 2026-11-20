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
                // Its waiting folder goes too, also when Ctrl+Z brought everything in it back: it stayed there empty.
                var marker = Path.DirectorySeparatorChar + AsideName + Path.DirectorySeparatorChar;
                if (aside.Now.IndexOf(marker, StringComparison.OrdinalIgnoreCase) is >= 0 and var at)
                    folders.Add(aside.Now[..(at + AsideName.Length + 1)]);
                if (!File.Exists(aside.Now))
                    continue;
                var from = aside.Now;
                if (!File.Exists(was) && Directory.Exists(Path.GetDirectoryName(was)))
                {
                    File.Move(aside.Now, was);
                    from = was;
                }
                RecycleBin.Put(from);
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

    // ---- a clip clicked: picked, and the playhead put there ----------------------------------------------------------

    /// <summary>
    /// A clip clicked on the timeline: picked into the chat's box, as before, and when it is made the playhead goes
    /// where it was clicked (its start for a click with no place: the keyboard's), so the viewer shows the video there
    /// with the clip as it is edited, and a look, a turn or a trim made from its menu is seen at once. Before, the
    /// viewer played the clip's own file, which no edit changes. A clip not made shows what it will be, as before.
    /// </summary>
    public void ShowClip(ShotItem item, double? seconds)
    {
        Show(item);
        if (FindShot(item) is not { } shot || !_board.HasClip(shot) || _previews.Decoder is null)
            return;
        var (starts, _) = _board.Timing();
        if (item.Index >= starts.Count)
            return;
        var (from, to) = (starts[item.Index], starts[item.Index] + Math.Max(0, shot.Seconds - 0.001));
        // With no place clicked: the first moment that is all this clip, past the transition into it.
        SeekStrip(Math.Clamp(seconds ?? from + (item.Index > 0 ? Storyboard.Overlap(shot) : 0), from, to));
    }

    // ---- a title picked on its row ----------------------------------------------------------------------------------

    /// <summary>The title picked on the Titles row, by its id: Delete and Ctrl+D work on it. It isn't the chat's to be told of.</summary>
    private string? _titlePicked;

    private void PickedTitle(string? id)
    {
        _titlePicked = id;
        foreach (var overlay in Overlays)
            overlay.IsSelected = overlay.Id == id;
    }

    /// <summary>
    /// A title clicked (not dragged): it is the one picked, marked on its row, and the playhead goes to a moment it is
    /// fully there, so the viewer shows it over the video as it is placed and styled. Delete then deletes it and
    /// Ctrl+D copies it; before, a title could only be reached through its right-click menu.
    /// </summary>
    public void PickTitle(OverlayItem item)
    {
        Clear();
        PickedTitle(item.Id);
        var shows = ShowSeconds(item);
        SeekStrip(item.Overlay.Start + Math.Min(shows / 2, Math.Max(0.35, item.Overlay.FadeIn ?? 0.3)));
    }

    // ---- a frame of the video as a picture -------------------------------------------------------------------------

    /// <summary>Whether there is a frame under the playhead to save: something here reads video, and a clip is made.</summary>
    private bool CanSaveFrame => _previews.Decoder is not null && (HasJoinedVideo || _board.Shots.Exists(_board.HasClip));

    /// <summary>
    /// The frame under the playhead saved as a picture of the Image editor ("Night drive at 0-03.2"): a poster, a
    /// thumbnail, a still to redraw or to start another clip from. From the joined video when there is one (the frame
    /// with its edits and titles), else from the clip under the playhead as it was made.
    /// </summary>
    private void SaveFrame()
    {
        if (_previews.Decoder is not { } decoder)
            return;
        var (starts, _) = _board.Timing();
        string source;
        double at;
        if (HasJoinedVideo)
            (source, at) = (_board.JoinedFile, PlayPosition);
        else if (Shots.LastOrDefault(s => s.Index < starts.Count && starts[s.Index] <= PlayPosition + 0.001) is { } item && FindShot(item) is { } shot && _board.HasClip(shot))
            (source, at) = (_board.ShotClip(shot), VideoPreviewer.SourceTime(shot, PlayPosition - starts[item.Index], double.MaxValue));
        else
        {
            Status = "There is no frame under the playhead yet: the clip there isn't made.";
            return;
        }
        var pictures = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_videosRoot))!, "pictures");
        var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{VideoName} at {(int)(PlayPosition / 60)}-{PlayPosition % 60:00.0}");
        Status = "Saving the frame…";
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = decoder.OpenVideo(source, 0) ?? throw new IOException($"{Path.GetFileName(source)} can't be read");
                reader.Seek(TimeSpan.FromSeconds(Math.Max(0, at)));
                var frame = reader.Next() ?? throw new IOException("there is no frame there");
                var folder = Core.Media.PictureFolder.Folders.New(pictures, name);
                Core.Documents.DocumentArea.SavePng(frame.Frame, Path.Combine(folder, "Frame.png"));
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = $"Saved the frame as the picture \"{Path.GetFileName(folder)}\": it is in the Image editor.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowError($"The frame couldn't be saved: {ex.Message}", ex));
            }
        });
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
        else if (_titlePicked is { } title && _board.Overlay(title) is not null)
        {
            Edit(() => _board.Overlays.RemoveAll(o => o.Id == title));
            PickedTitle(null);
            Status = "Deleted the title. Ctrl+Z brings it back.";
        }
        else
            return false;
        return true;
    }

    /// <summary>Ctrl+D: a copy of the clip picked right after it, as Duplicate of its menu does; false when no clip is picked.</summary>
    public bool DuplicatePicked()
    {
        // A title picked: its copy from where it stops, as Duplicate of its menu.
        if (!Busy(_board.Folder) && PickedShot is null && _titlePicked is { } title && Overlays.FirstOrDefault(o => o.Id == title) is { } picked)
        {
            DuplicateTitle(picked);
            return true;
        }
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

// The made video saved as another file from the panel itself. Before, an export was something only the agent did,
// asked for in the chat: someone who had made a video and wanted a GIF of it, or a copy for a phone, had no way to
// get one here.
public sealed partial class VideoEditorViewModel
{
    private bool _exporting;

    /// <summary>Whether the made video can be saved as another file now: it is made, nothing is exporting, and something here writes video.</summary>
    private bool CanExport => HasJoinedVideo && !_exporting && _previews.Decoder is { } decoder && (decoder.Ffmpeg is not null || VideoComposer.Joins(decoder));

    /// <summary>What Export offers: each a file in the video's Exports folder, named after the video.</summary>
    private List<PanelMenuItem?> ExportItems()
    {
        static int Even(double n) => Math.Max(2, (int)Math.Round(n / 2) * 2);
        var (w, h) = (_board.Width, _board.Height);
        var (longer, shorter) = (Math.Max(w, h), Math.Min(w, h));
        // Upright for a phone: as tall as the video's longer side, nine wide to sixteen tall, the video's middle kept.
        var (uprightWidth, uprightHeight) = (Even(longer * 9.0 / 16), Even(longer));
        return
        [
            new($"Smaller MP4 ({Even(w / 2.0)}×{Even(h / 2.0)})", true, () => Export(" small", ".mp4", Even(w / 2.0), Even(h / 2.0))),
            new($"Upright MP4 for a phone ({uprightWidth}×{uprightHeight})", h <= w, () => Export(" upright", ".mp4", uprightWidth, uprightHeight)),
            new($"Square MP4 ({Even(shorter)}×{Even(shorter)})", w != h, () => Export(" square", ".mp4", Even(shorter), Even(shorter))),
            null,
            new("GIF", true, () => Export("", ".gif", null, null)),
            new("Sound only (WAV)", true, () => Export("", ".wav", null, null)),
        ];
    }

    /// <summary>Saves the made video as another file in its Exports folder and shows it there.</summary>
    private void Export(string suffix, string extension, int? width, int? height)
    {
        if (_exporting || _previews.Decoder is not { } decoder)
            return;
        if (_board.JoinedIsStale)
        {
            Status = "The video has changed since it was made: make it again, then export it.";
            return;
        }
        SaveNow(force: true);
        var board = Storyboard.Parse(_board.ToJson());
        board.Folder = _board.Folder;
        var output = Path.Combine(_board.Folder, "Exports", Path.GetFileNameWithoutExtension(board.JoinedFile) + suffix + extension);
        _exporting = true;
        Status = "Exporting…";
        void Tell(string progress) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = progress);
        _ = Task.Run(async () =>
        {
            string said;
            try
            {
                var file = decoder.Ffmpeg is { } ffmpeg
                    ? await VideoMontage.ExportAsync(board, ffmpeg, output, width, height, Tell, CancellationToken.None).ConfigureAwait(false)
                    : await VideoComposer.ExportAsync(board, decoder, output, width, height, Tell, CancellationToken.None).ConfigureAwait(false);
                said = $"Exported {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024.0 / 1024:0.#} MB), in the video's Exports folder.";
                ShowInExplorer(file);
            }
            catch (Exception ex) when (ex is VideoException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                said = "It couldn't be exported: " + ex.Message;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _exporting = false;
                if (!_disposing)
                    Status = said;
            });
        });
    }
}
