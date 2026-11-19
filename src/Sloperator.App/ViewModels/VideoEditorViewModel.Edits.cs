using System.Collections.ObjectModel;
using Sloperator.Core.Video;

namespace Sloperator.App.ViewModels;

public sealed partial class VideoEditorViewModel
{
    // ---- editing a clip and the titles over the video, as a video editor does --------------------------------
    // (the user, 2026-11-19: "Do your best to provide a fully featured video editing experience"). Each is a
    // change to the storyboard, done when the video is joined: no clip is made again for it.

    /// <summary>How tall the Titles row is at least, and each lane of titles that show at once in it.</summary>
    public const double TitlesRowHeight = 48, TitleLaneHeight = 22;

    /// <summary>How tall the Titles row is now: taller when more titles show at once than two lanes hold.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private double _titlesHeight = TitlesRowHeight;

    /// <summary>The titles, captions, pictures and scenes over the video, on the Titles row.</summary>
    public ObservableCollection<OverlayItem> Overlays { get; } = [];

    private void RebuildOverlays()
    {
        Overlays.Clear();
        foreach (var overlay in _board.Overlays)
            Overlays.Add(new OverlayItem(overlay));
    }

    private void LayoutOverlays(double length)
    {
        // Titles that show at once go one above the other, each in the first lane free at its start.
        var lanes = new List<double>();
        var lane = new Dictionary<OverlayItem, int>();
        foreach (var item in Overlays.OrderBy(o => o.Overlay.Start))
        {
            var seconds = item.Overlay.Seconds > 0 ? item.Overlay.Seconds : Math.Max(1, length - item.Overlay.Start);
            item.X = Lead + item.Overlay.Start * Pps;
            item.BlockWidth = Math.Max(8, seconds * Pps - 2);
            var free = lanes.FindIndex(end => end <= item.Overlay.Start + 0.001);
            if (free < 0)
            {
                lanes.Add(0);
                free = lanes.Count - 1;
            }
            lanes[free] = item.Overlay.Start + seconds;
            lane[item] = free;
        }
        TitlesHeight = Math.Max(TitlesRowHeight, lanes.Count * TitleLaneHeight + 8);
        var height = (TitlesHeight - 8) / Math.Max(1, lanes.Count);
        foreach (var (item, at) in lane)
        {
            item.Y = 4 + at * height;
            item.BlockHeight = Math.Max(10, height - 2);
        }
    }

    /// <summary>The edits made here, for Ctrl+Z and Ctrl+Y; another video opened, or the chat's change to this one, starts with none.</summary>
    private readonly EditHistory _history = new();

    /// <summary>While Ctrl+Z or Ctrl+Y writes its clips' .json back: the save after it finds them changed, and it isn't the chat.</summary>
    private bool _puttingBack;

    /// <summary>Keeps the storyboard as it is now, to go back to with <see cref="Undo"/>: before an edit, or at the press of a drag.</summary>
    public void Remember() => _history.Keep(_board.ToJson(), ClipFiles);

    /// <summary>Each clip's .json as it is now, by its path: a split stamps the halves made from their footage.</summary>
    private IReadOnlyDictionary<string, string> ClipFiles()
    {
        var metas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shot in _board.Shots)
            try
            {
                var meta = _board.ShotMeta(shot);
                if (File.Exists(meta))
                    metas[meta] = File.ReadAllText(meta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return metas;
    }

    /// <summary>
    /// Ctrl+Z: the storyboard as it was before the last edit made here (its timing, speeds, looks, transitions,
    /// titles, sounds; a split's halves are one clip again, the footage it copied stays). False when there is none.
    /// </summary>
    public bool Undo() => !Busy(_board.Folder) && PutBack(_history.Undo(_board.ToJson(), ClipFiles), "Took the last change back.");

    /// <summary>Ctrl+Y: the edit Ctrl+Z took back, made again; false when there is none, or the video changed since.</summary>
    public bool Redo() => !Busy(_board.Folder) && PutBack(_history.Redo(_board.ToJson(), ClipFiles), "Made the change again.");

    private bool PutBack(EditHistory.State? state, string said)
    {
        if (state is null)
            return false;
        var folder = _board.Folder;
        _board = Storyboard.Parse(state.Json);
        _board.Folder = folder;
        _puttingBack = true;
        try
        {
            // A clip or sound Delete took out comes back with its files (and goes again with Ctrl+Y).
            BringBack();
            foreach (var (meta, text) in state.Files)
                try { File.WriteAllText(meta, text); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Changed();
            SaveNow();
        }
        finally
        {
            _puttingBack = false;
        }
        Rebuild();
        _history.Left(_board.ToJson());
        Status = said;
        return true;
    }

    /// <summary>Undo and Redo, at the end of a row's menu, with their keys.</summary>
    private PanelMenuItem?[] UndoItems(bool free)
    {
        var now = _board.ToJson();
        return
        [
            null,
            new("Undo", free && _history.CanUndo(now), () => Undo(), "Ctrl+Z"),
            new("Redo", free && _history.CanRedo(now), () => Redo(), "Ctrl+Y"),
        ];
    }

    /// <summary>A change made here: saved, and the timeline built again with it.</summary>
    private void Edit(Action change)
    {
        if (Busy(_board.Folder))
            return;
        Remember();
        change();
        _board.Normalize();
        Changed();
        SaveNow();
        Rebuild();
    }

    private static string Mark(bool on) => on ? "● " : "";

    /// <summary>What right-clicking a clip offers to edit it: speed, reverse, sound, look, zoom, a freeze, the transition into it, its place, and split.</summary>
    private List<PanelMenuItem?> ClipEdits(ShotItem item, bool free)
    {
        var shot = item.Shot;
        var speed = shot.Speed ?? 1;
        var (starts, _) = _board.Timing();
        var into = item.Index < starts.Count ? PlayPosition - starts[item.Index] : -1;
        var items = new List<PanelMenuItem?>
        {
            new("Speed", free, () => { })
            {
                Items = new[] { 0.25, 0.5, 0.75, 1, 1.5, 2, 4 }.Select(s => (PanelMenuItem?)new PanelMenuItem($"{Mark(Math.Abs(speed - s) < 0.001)}{s:0.##}×", free,
                    () => Edit(() => FindShot(item)?.Let(x => x.Speed = s)))).ToList(),
            },
            new($"{Mark(shot.Reverse)}Reverse", free, () => Edit(() => FindShot(item)?.Let(x => x.Reverse = !x.Reverse))),
            // The frame under the playhead held to the clip's end; picked again, the clip moves on as it was.
            new($"{Mark(shot.Freeze is not null)}Freeze from the playhead", free && (shot.Freeze is not null || into > 0.05 && into < shot.Seconds - 0.05),
                () => Edit(() => FindShot(item)?.Let(x => x.Freeze = x.Freeze is null ? Math.Round(into * 100) / 100 : null))),
            null,
            new($"{Mark(shot.Volume == 0)}Mute its sound", free, () => Edit(() => FindShot(item)?.Let(x => x.Volume = x.Volume == 0 ? null : 0))),
            // How loud its own sound is, as a sound's menu sets a sound's.
            new("Volume", free, () => { })
            {
                Items = new[] { 0.25, 0.5, 0.75, 1, 1.5, 2 }.Select(v => (PanelMenuItem?)new PanelMenuItem($"{Mark(Math.Abs((shot.Volume ?? 1) - v) < 0.001)}{v * 100:0}%", free,
                    () => Edit(() => FindShot(item)?.Let(x => x.Volume = v)))).ToList(),
            },
            null,
            new("Look", free, () => { })
            {
                // The looks, then its light and colour a step at a time, as an editor's colour sliders do.
                Items =
                [
                    .. Looks.Select(l => (PanelMenuItem?)new PanelMenuItem($"{Mark((shot.Color?.Look ?? "") == l.Look)}{l.Name}", free,
                        () => Edit(() => FindShot(item)?.Let(x => { x.Color ??= new VideoColor(); x.Color.Look = l.Look; })))),
                    null,
                    .. Adjustments.Select(a => (PanelMenuItem?)new PanelMenuItem(a.Name, free,
                        () => Edit(() => FindShot(item)?.Let(x => { x.Color ??= new VideoColor(); a.Change(x.Color); })))),
                ],
            },
            // Each on or off, any of them together.
            new("Effects", free, () => { })
            {
                Items =
                [
                    new PanelMenuItem($"{Mark(shot.Effects is null)}None", free, () => Edit(() => FindShot(item)?.Let(x => x.Effects = null))),
                    null,
                    .. EffectChoices.Select(e => (PanelMenuItem?)new PanelMenuItem($"{Mark(shot.Effects?.Contains(e.Effect) == true)}{e.Name}", free,
                        () => Edit(() => FindShot(item)?.Let(x =>
                        {
                            x.Effects ??= [];
                            if (!x.Effects.Remove(e.Effect))
                                x.Effects.Add(e.Effect);
                        })))),
                ],
            },
            new("Zoom", free, () => { })
            {
                Items = Zooms.Select(z => (PanelMenuItem?)new PanelMenuItem($"{Mark(ZoomName(shot.Zoom) == z.Name)}{z.Name}", free,
                    () => Edit(() => FindShot(item)?.Let(x => x.Zoom = z.Make())))).ToList(),
            },
            // Footage filmed on its side, or the wrong way round.
            new("Turn", free, () => { })
            {
                Items =
                [
                    new PanelMenuItem("Turn right", free, () => Edit(() => FindShot(item)?.Let(x => x.Rotate = (x.Rotate + 90) % 360))),
                    new PanelMenuItem("Turn left", free, () => Edit(() => FindShot(item)?.Let(x => x.Rotate = (x.Rotate + 270) % 360))),
                    null,
                    new PanelMenuItem($"{Mark(shot.Flip is "horizontal" or "both")}Mirror", free, () => Edit(() => FindShot(item)?.Let(x => x.Flip = Flipped(x.Flip, "horizontal")))),
                    new PanelMenuItem($"{Mark(shot.Flip is "vertical" or "both")}Upside down", free, () => Edit(() => FindShot(item)?.Let(x => x.Flip = Flipped(x.Flip, "vertical")))),
                    null,
                    new PanelMenuItem($"{Mark(shot.Rotate == 0 && shot.Flip is null)}As shot", free, () => Edit(() => FindShot(item)?.Let(x => (x.Rotate, x.Flip) = (0, null)))),
                ],
            },
            // A picture of another shape than the video's: its edges cut off, or all of it with something beside it.
            new("Fit", free, () => { })
            {
                Items = Fits.Select(f => (PanelMenuItem?)new PanelMenuItem($"{Mark(shot.Fit == f.Fit)}{f.Name}", free,
                    () => Edit(() => FindShot(item)?.Let(x => x.Fit = f.Fit)))).ToList(),
            },
        };
        if (item.Index > 0)
        {
            items.Add(null);
            items.Add(new("Transition in", free, () => { })
            {
                // The kinds, then how long it takes.
                Items =
                [
                    .. TransitionChoices.Select(t => (PanelMenuItem?)new PanelMenuItem($"{Mark(shot.Transition == t.Name)}{t.Label}", free,
                        () => Edit(() => FindShot(item)?.Let(x => x.Transition = t.Name)))),
                    null,
                    .. new[] { 0, 0.3, 0.5, 1, 1.5, 2 }.Select(s => (PanelMenuItem?)new PanelMenuItem(
                        $"{Mark(Math.Abs(shot.TransitionSeconds - s) < 0.001)}{(s == 0 ? "Its usual length" : $"{s:0.#} s")}", free && shot.Transition != VideoShot.Cut,
                        () => Edit(() => FindShot(item)?.Let(x => x.TransitionSeconds = s)))),
                ],
            });
        }
        items.Add(null);
        var at = FindShot(item) is { } now ? _board.Shots.IndexOf(now) : -1;
        items.Add(new("Move earlier", free && at > 0, () => MoveShot(item, -1)));
        items.Add(new("Move later", free && at >= 0 && at < _board.Shots.Count - 1, () => MoveShot(item, 1)));
        items.Add(new("Split at the playhead", free && CanSplit(shot, into), () => Split(item), "Ctrl+B"));
        items.Add(new("Trim start to the playhead", free && CanSplit(shot, into), () => Trim(item, start: true)));
        items.Add(new("Trim end to the playhead", free && CanSplit(shot, into), () => Trim(item, start: false)));
        return items;
    }

    /// <summary>A clip moved one place earlier or later in the video, with the transition into it.</summary>
    private void MoveShot(ShotItem item, int by) => Edit(() =>
    {
        if (FindShot(item) is not { } shot)
            return;
        var at = _board.Shots.IndexOf(shot);
        if (at + by < 0 || at + by >= _board.Shots.Count)
            return;
        _board.Shots.RemoveAt(at);
        _board.Shots.Insert(at + by, shot);
    });

    /// <summary>Whether a clip can be split this far into it: made, not reversed, and a fifth of a second either side.</summary>
    private bool CanSplit(VideoShot shot, double into) =>
        !shot.Reverse && into > 0.2 && into < shot.Seconds - 0.2 && File.Exists(_board.ShotVideo(shot));

    /// <summary>Ctrl+B: the clip under the playhead split there, as its menu's Split at the playhead does; false when none can be.</summary>
    public bool SplitAtPlayhead()
    {
        if (Busy(_board.Folder))
            return false;
        var (starts, _) = _board.Timing();
        // The last clip begun by then: in a transition, the one coming in.
        var item = Shots.LastOrDefault(s => s.Index < starts.Count && starts[s.Index] < PlayPosition);
        if (item is null || FindShot(item) is not { } shot || !CanSplit(shot, PlayPosition - starts[item.Index]))
            return false;
        Split(item);
        return true;
    }

    private static readonly (string Look, string Name)[] Looks =
        [("", "As it is"), ("bw", "Black and white"), ("sepia", "Sepia"), ("warm", "Warm"), ("cool", "Cool"), ("vintage", "Vintage")];

    /// <summary>A clip's light and colour a step at a time, within what its colour takes.</summary>
    private static readonly (string Name, Action<VideoColor> Change)[] Adjustments =
    [
        ("Brighter", c => c.Brightness = Math.Min(1, Math.Round(c.Brightness + 0.1, 2))),
        ("Darker", c => c.Brightness = Math.Max(-1, Math.Round(c.Brightness - 0.1, 2))),
        ("More contrast", c => c.Contrast = Math.Min(2, Math.Round(c.Contrast + 0.1, 2))),
        ("Less contrast", c => c.Contrast = Math.Max(0, Math.Round(c.Contrast - 0.1, 2))),
        ("More colour", c => c.Saturation = Math.Min(3, Math.Round(c.Saturation + 0.2, 2))),
        ("Less colour", c => c.Saturation = Math.Max(0, Math.Round(c.Saturation - 0.2, 2))),
        ("Light and colour as shot", c => (c.Brightness, c.Contrast, c.Saturation) = (0, 1, 1)),
    ];

    private static readonly (string Effect, string Name)[] EffectChoices =
        [("blur", "Blur"), ("sharpen", "Sharpen"), ("vignette", "Vignette"), ("grain", "Film grain"), ("invert", "Negative"), ("pixelate", "Pixelate")];

    private static readonly (string? Fit, string Name)[] Fits =
        [(null, "Fill the frame"), ("fit", "Whole, with black bars"), ("blur", "Whole, over a blur of itself")];

    /// <summary>A clip's mirroring with one way turned on or off: mirrored both ways is "both", neither none.</summary>
    private static string? Flipped(string? now, string way)
    {
        var (h, v) = (now is "horizontal" or "both", now is "vertical" or "both");
        (h, v) = way == "horizontal" ? (!h, v) : (h, !v);
        return h && v ? "both" : h ? "horizontal" : v ? "vertical" : null;
    }

    private static readonly (string Name, Func<VideoZoom?> Make)[] Zooms =
    [
        ("None", () => null),
        ("Slow zoom in", () => new VideoZoom { From = new VideoView { Scale = 1 }, To = new VideoView { Scale = 1.25 } }),
        ("Slow zoom out", () => new VideoZoom { From = new VideoView { Scale = 1.25 }, To = new VideoView { Scale = 1 } }),
        ("Pan left", () => new VideoZoom { From = new VideoView { Scale = 1.2, X = 0.6 }, To = new VideoView { Scale = 1.2, X = 0.4 } }),
        ("Pan right", () => new VideoZoom { From = new VideoView { Scale = 1.2, X = 0.4 }, To = new VideoView { Scale = 1.2, X = 0.6 } }),
    ];

    private static string ZoomName(VideoZoom? zoom) => zoom is null ? "None"
        : zoom.From.Scale < zoom.To.Scale ? "Slow zoom in"
        : zoom.From.Scale > zoom.To.Scale ? "Slow zoom out"
        : zoom.From.X > zoom.To.X ? "Pan left" : zoom.From.X < zoom.To.X ? "Pan right" : "";

    private static readonly (string Name, string Label)[] TransitionChoices =
    [
        (VideoShot.Cut, "Cut"), (VideoShot.Dissolve, "Dissolve"), (VideoShot.Fade, "Fade through black"), ("fadewhite", "Fade through white"),
        ("wipeleft", "Wipe"), ("slideleft", "Slide"), ("smoothleft", "Smooth wipe"), ("circleopen", "Circle"), ("zoomin", "Zoom"),
        ("radial", "Clock"), ("pixelize", "Pixelate"), ("hblur", "Blur"),
    ];

    /// <summary>The shot an item stands for in the storyboard now (the items are made anew as it is saved).</summary>
    private VideoShot? FindShot(ShotItem item) => _board.Shot(item.Id);

    /// <summary>
    /// Splits a clip in two at the playhead: both halves are the same footage, the second starting where the first
    /// stops. A clip a model made becomes footage for it (a copy in References), so neither half is made again.
    /// </summary>
    private void Split(ShotItem item)
    {
        if (Busy(_board.Folder) || FindShot(item) is not { } shot)
            return;
        var (starts, _) = _board.Timing();
        var into = PlayPosition - starts[item.Index];
        if (!CanSplit(shot, into))
            return;
        SaveNow();
        StopPlaying();
        DropViewer();
        Remember();
        try
        {
            var name = Footage(item, shot);
            var speed = shot.Speed ?? 1;
            var second = new VideoShot
            {
                Id = _board.NewId("s"), Name = shot.Name, File = name, In = shot.In + into * speed, Seconds = shot.Seconds - into, Speed = shot.Speed,
                Volume = shot.Volume, Prompt = shot.Prompt, Camera = shot.Camera, Seed = Storyboard.NewSeed(),
                Crop = shot.Crop is { } c ? new VideoBox { X = c.X, Y = c.Y, Width = c.Width, Height = c.Height } : null,
                Color = shot.Color is { } k ? new VideoColor { Brightness = k.Brightness, Contrast = k.Contrast, Saturation = k.Saturation, Look = k.Look } : null,
                Freeze = shot.Freeze is { } f && f > into ? f - into : null,
                Rotate = shot.Rotate, Flip = shot.Flip, Fit = shot.Fit, Effects = shot.Effects?.ToList(),
            };
            shot.Seconds = into;
            if (shot.Freeze >= into)
                shot.Freeze = null;
            _board.Shots.Insert(_board.Shots.IndexOf(shot) + 1, second);
            _board.Normalize();
            Changed();
            SaveNow();
            VideoMaker.TakeClip(_board, shot);
            VideoMaker.TakeClip(_board, second);
            Status = $"Split {item.Title} at {into:0.0} s.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The clip couldn't be split: {ex.Message}", ex);
            Reload();
        }
        Rebuild();
    }

    /// <summary>
    /// Cuts a clip's start or its end off at the playhead, as an editor's trim does, the clips after it moving up.
    /// A clip a model made becomes footage for it first (a copy in References), so it isn't made again.
    /// </summary>
    private void Trim(ShotItem item, bool start)
    {
        if (Busy(_board.Folder) || FindShot(item) is not { } shot)
            return;
        var (starts, _) = _board.Timing();
        var into = PlayPosition - starts[item.Index];
        if (!CanSplit(shot, into))
            return;
        SaveNow();
        StopPlaying();
        DropViewer();
        Remember();
        try
        {
            Footage(item, shot);
            if (start)
            {
                shot.In += into * (shot.Speed ?? 1);
                shot.Seconds -= into;
                shot.Freeze = shot.Freeze is { } f && f > into ? f - into : null;
            }
            else
            {
                shot.Seconds = into;
                if (shot.Freeze >= into)
                    shot.Freeze = null;
            }
            _board.Normalize();
            Changed();
            SaveNow();
            VideoMaker.TakeClip(_board, shot);
            Status = start ? $"Cut the first {into:0.0} s of {item.Title} off." : $"Cut {item.Title} off after {into:0.0} s.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The clip couldn't be trimmed: {ex.Message}", ex);
            Reload();
        }
        Rebuild();
        // On the clip's first frame now: what was under the playhead.
        if (start)
            SeekStrip(starts[item.Index]);
    }

    /// <summary>
    /// A copy of a clip right after it, with its edits: its clip and the files that go with it copied under the copy's
    /// own name, so a clip made is made in the copy too and neither is made again.
    /// </summary>
    /// <param name="after">The clip the copy goes after (Ctrl+V: the one under the playhead); null: the clip copied.</param>
    private void Duplicate(ShotItem item, ShotItem? after = null)
    {
        if (Busy(_board.Folder) || FindShot(item) is not { } shot || Storyboard.Parse(_board.ToJson()).Shot(shot.Id) is not { } copy)
            return;
        SaveNow();
        StopPlaying();
        DropViewer();
        Remember();
        copy.Id = _board.NewId("s");
        copy.Place = null;
        try
        {
            var clip = _board.ShotVideo(shot);
            if (File.Exists(clip))
            {
                var to = _board.ShotVideo(copy);
                var (from, into) = (Path.GetDirectoryName(clip)!, Path.GetDirectoryName(to)!);
                var (stem, name) = (Path.GetFileNameWithoutExtension(clip) + ".", Path.GetFileNameWithoutExtension(to) + ".");
                Directory.CreateDirectory(into);
                foreach (var file in Directory.GetFiles(from).Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
                    File.Copy(file, Path.Combine(into, name + Path.GetFileName(file)[stem.Length..]), overwrite: true);
            }
            var place = after is not null && FindShot(after) is { } afterShot ? afterShot : shot;
            _board.Shots.Insert(_board.Shots.IndexOf(place) + 1, copy);
            _board.Normalize();
            Changed();
            SaveNow();
            Status = after is null || ReferenceEquals(after, item) ? $"Put a copy of {item.Title} after it." : $"Put a copy of {item.Title} after {after.Title}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The clip couldn't be copied: {ex.Message}", ex);
            Reload();
        }
        Rebuild();
    }

    /// <summary>
    /// What a clip is cut from, so it can be cut without being made again: the MP4 of the user's it is made of, else its
    /// clip (a model's, or a scene drawn) copied into References as "&lt;clip&gt; footage.mp4". It is made of that from then on.
    /// </summary>
    private string Footage(ShotItem item, VideoShot shot)
    {
        string name;
        if (shot.File is { } own && _board.FindFile(own) is { } source && source.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            name = own;
        else
        {
            Directory.CreateDirectory(_board.ReferencesFolder);
            var stem = Storyboard.Safe($"{item.Title} footage");
            name = stem + ".mp4";
            for (var n = 2; File.Exists(Path.Combine(_board.ReferencesFolder, name)); n++)
                name = $"{stem} {n}.mp4";
            File.Copy(_board.ShotVideo(shot), Path.Combine(_board.ReferencesFolder, name));
        }
        shot.File = name;
        shot.Scene = null;
        return name;
    }

    // ---- the Titles row --------------------------------------------------------------------------------------

    /// <summary>A new title where the row was clicked: three seconds of words in the middle, typed in at once.</summary>
    private void CreateTitle(double seconds)
    {
        if (Busy(_board.Folder))
            return;
        var overlay = new VideoOverlay { Id = _board.NewId("o"), Text = "Title", Start = Math.Max(0, Math.Round(seconds * 10) / 10), Seconds = 3, Position = "center" };
        Edit(() => _board.Overlays.Add(overlay));
        if (Overlays.FirstOrDefault(o => o.Id == overlay.Id) is { } item)
            RenameAsked?.Invoke(item, overlay.Text);
    }

    /// <summary>
    /// What right-clicking a title offers: its words, its place on the frame, how long it shows and how it comes in,
    /// the words' size, colour and font, and Delete.
    /// </summary>
    public IReadOnlyList<PanelMenuItem?> MenuFor(OverlayItem item)
    {
        var free = !Busy(_board.Folder);
        var o = item.Overlay;
        var words = o.File is null && o.Scene is null;
        return
        [
            new("Change the words", free && words, () => RenameAsked?.Invoke(item, o.Text.Replace("\\n", "\n"))),
            null,
            new("Place", free, () => { })
            {
                Items = Places.Select(p => (PanelMenuItem?)new PanelMenuItem($"{Mark(o.Box is null && o.Position == p.Position)}{p.Name}", free,
                    () => Edit(() => _board.Overlay(item.Id)?.Let(x => { x.Position = p.Position; x.Box = null; })))).ToList(),
            },
            new("Shows for", free, () => { })
            {
                Items = new[] { 1.0, 2, 3, 5, 8, 0 }.Select(s => (PanelMenuItem?)new PanelMenuItem($"{Mark(Math.Abs(o.Seconds - s) < 0.01)}{(s == 0 ? "To the end" : $"{s:0} s")}", free,
                    () => Edit(() => _board.Overlay(item.Id)?.Let(x => x.Seconds = s)))).ToList(),
            },
            new("Move to the playhead", free, () => Edit(() => _board.Overlay(item.Id)?.Let(x => x.Start = Math.Max(0, Math.Round(PlayPosition * 10) / 10)))),
            Choices("Comes in", free && words, item, Motions, o.Motion, (x, m) => x.Motion = m),
            null,
            Choices("Size", free && words, item, Sizes, o.Size, (x, s) => x.Size = s),
            Choices("Colour", free && words, item, Colours, o.Color?.ToUpperInvariant(), (x, c) => x.Color = c),
            Choices("Font", free && words, item, Fonts, o.Font, (x, f) => x.Font = f),
            new($"{Mark(o.Bold != false)}Bold", free && words, () => Edit(() => _board.Overlay(item.Id)?.Let(x => x.Bold = x.Bold == false ? null : false))),
            new($"{Mark(o.Background is not null)}Box behind the words", free && words,
                () => Edit(() => _board.Overlay(item.Id)?.Let(x => x.Background = x.Background is null ? "#000000A0" : null))),
            null,
            new("Duplicate", free, () => DuplicateTitle(item)),
            new("Delete", free, () => Edit(() => _board.Overlays.RemoveAll(x => x.Id == item.Id))),
            null,
            new("Create", free, () => CreateTitle(PlayPosition)),
        ];
    }

    /// <summary>A copy of a title, showing from where it stops (one shown to the end: from the playhead).</summary>
    private void DuplicateTitle(OverlayItem item)
    {
        if (Busy(_board.Folder) || _board.Overlay(item.Id) is not { } overlay || Storyboard.Parse(_board.ToJson()).Overlay(item.Id) is not { } copy)
            return;
        copy.Id = _board.NewId("o");
        copy.Name = null;
        copy.Start = Math.Round((overlay.Seconds > 0 ? overlay.Start + overlay.Seconds : PlayPosition) * 10) / 10;
        Edit(() => _board.Overlays.Add(copy));
    }

    /// <summary>A submenu of a title's choices with the one it has marked, each an edit of it.</summary>
    private PanelMenuItem Choices<T>(string header, bool enabled, OverlayItem item, (T Value, string Name)[] choices, T now, Action<VideoOverlay, T> set) =>
        new(header, enabled, () => { })
        {
            Items = choices.Select(c => (PanelMenuItem?)new PanelMenuItem($"{Mark(EqualityComparer<T>.Default.Equals(c.Value, now))}{c.Name}", enabled,
                () => Edit(() => _board.Overlay(item.Id)?.Let(x => set(x, c.Value))))).ToList(),
        };

    private static readonly (string? Value, string Name)[] Motions =
        [(null, "Fading in"), ("slide up", "Sliding up"), ("slide down", "Sliding down"), ("slide left", "Sliding left"), ("slide right", "Sliding right")];

    /// <summary>The words' height as a fraction of the frame's; none as its place has it (a title bigger than a corner's words).</summary>
    private static readonly (double? Value, string Name)[] Sizes =
        [(null, "As its place has it"), (0.045, "Small"), (0.07, "Medium"), (0.1, "Large"), (0.14, "Huge")];

    private static readonly (string? Value, string Name)[] Colours =
        [(null, "White"), ("#000000", "Black"), ("#FFD60A", "Yellow"), ("#FF453A", "Red"), ("#0A84FF", "Blue"), ("#30D158", "Green")];

    private static readonly (string? Value, string Name)[] Fonts =
        [(null, "Segoe UI"), ("Arial", "Arial"), ("Georgia", "Georgia"), ("Times New Roman", "Times New Roman"), ("Impact", "Impact"), ("Consolas", "Consolas")];

    private static readonly (string Position, string Name)[] Places =
    [
        ("center", "Centre"), ("top", "Top"), ("bottom", "Bottom"), ("lower third", "Lower third"),
        ("top left", "Top left"), ("top right", "Top right"), ("bottom left", "Bottom left"), ("bottom right", "Bottom right"), ("full", "Whole frame"),
    ];

    /// <summary>The words typed for a title (a line break as \n).</summary>
    private void SetWords(OverlayItem item, string words) =>
        Edit(() => _board.Overlay(item.Id)?.Let(x => x.Text = words.ReplaceLineEndings("\n")));

    /// <summary>A title dragged along the row, or at an edge: where it starts and how long it shows, in tenths of a second.</summary>
    public void MoveOverlay(OverlayItem item, double start, double seconds)
    {
        var overlay = item.Overlay;
        (overlay.Start, overlay.Seconds) = (Math.Max(0, Math.Round(start * 10) / 10), Math.Max(0.2, Math.Round(seconds * 10) / 10));
        Relayout();
        Changed();
    }

    /// <summary>How long a title shows on the row: its seconds, else to the video's end.</summary>
    public double ShowSeconds(OverlayItem item) => item.Overlay.Seconds > 0 ? item.Overlay.Seconds : Math.Max(1, Length - item.Overlay.Start);

    /// <summary>A double click on a title: its words to change.</summary>
    public void AskWords(OverlayItem item)
    {
        if (!Busy(_board.Folder) && item.Overlay.File is null && item.Overlay.Scene is null)
            RenameAsked?.Invoke(item, item.Overlay.Text);
    }
}

internal static class EditExtensions
{
    /// <summary>Does <paramref name="change"/> to it: a change to something that may be gone, in one line.</summary>
    public static void Let<T>(this T target, Action<T> change) => change(target);
}
