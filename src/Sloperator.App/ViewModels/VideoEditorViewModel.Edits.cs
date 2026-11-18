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

    /// <summary>
    /// The storyboard before each edit made here, the latest last, for Ctrl+Z (as many as 50), with its clips'
    /// .json as they were: a split stamps the halves made from their footage, and taken back, the clip is made as before.
    /// </summary>
    private readonly List<(string Json, Dictionary<string, string> Metas)> _undo = [];

    /// <summary>Keeps the storyboard as it is now, to go back to with <see cref="Undo"/>.</summary>
    public void Remember()
    {
        var now = _board.ToJson();
        if (_undo.Count > 0 && _undo[^1].Json == now)
            return;
        var metas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shot in _board.Shots)
            try
            {
                var meta = _board.ShotMeta(shot);
                if (File.Exists(meta))
                    metas[meta] = File.ReadAllText(meta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _undo.Add((now, metas));
        if (_undo.Count > 50)
            _undo.RemoveAt(0);
    }

    /// <summary>
    /// Ctrl+Z: the storyboard as it was before the last edit made here (its timing, speeds, looks, transitions,
    /// titles; a split's halves are one clip again, the footage it copied stays). False when there is none.
    /// </summary>
    public bool Undo()
    {
        if (_undo.Count == 0 || Busy(_board.Folder))
            return false;
        var (json, metas) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        var folder = _board.Folder;
        _board = Storyboard.Parse(json);
        _board.Folder = folder;
        foreach (var (meta, text) in metas)
            try { File.WriteAllText(meta, text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed();
        SaveNow();
        Rebuild();
        Status = "Took the last change back.";
        return true;
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

    /// <summary>What right-clicking a clip offers to edit it: speed, reverse, sound, look, zoom, the transition into it, and split.</summary>
    private List<PanelMenuItem?> ClipEdits(ShotItem item, bool free)
    {
        var shot = item.Shot;
        var speed = shot.Speed ?? 1;
        var items = new List<PanelMenuItem?>
        {
            new("Speed", free, () => { })
            {
                Items = new[] { 0.25, 0.5, 0.75, 1, 1.5, 2, 4 }.Select(s => (PanelMenuItem?)new PanelMenuItem($"{Mark(Math.Abs(speed - s) < 0.001)}{s:0.##}×", free,
                    () => Edit(() => FindShot(item)?.Let(x => x.Speed = s)))).ToList(),
            },
            new($"{Mark(shot.Reverse)}Reverse", free, () => Edit(() => FindShot(item)?.Let(x => x.Reverse = !x.Reverse))),
            new($"{Mark(shot.Volume == 0)}Mute its sound", free, () => Edit(() => FindShot(item)?.Let(x => x.Volume = x.Volume == 0 ? null : 0))),
            new("Look", free, () => { })
            {
                Items = Looks.Select(l => (PanelMenuItem?)new PanelMenuItem($"{Mark((shot.Color?.Look ?? "") == l.Look)}{l.Name}", free,
                    () => Edit(() => FindShot(item)?.Let(x => { x.Color ??= new VideoColor(); x.Color.Look = l.Look; })))).ToList(),
            },
            new("Zoom", free, () => { })
            {
                Items = Zooms.Select(z => (PanelMenuItem?)new PanelMenuItem($"{Mark(ZoomName(shot.Zoom) == z.Name)}{z.Name}", free,
                    () => Edit(() => FindShot(item)?.Let(x => x.Zoom = z.Make())))).ToList(),
            },
        };
        if (item.Index > 0)
            items.Add(new("Transition in", free, () => { })
            {
                Items = TransitionChoices.Select(t => (PanelMenuItem?)new PanelMenuItem($"{Mark(shot.Transition == t.Name)}{t.Label}", free,
                    () => Edit(() => FindShot(item)?.Let(x => x.Transition = t.Name)))).ToList(),
            });
        var (starts, _) = _board.Timing();
        var into = item.Index < starts.Count ? PlayPosition - starts[item.Index] : -1;
        items.Add(new("Split at the playhead", free && !shot.Reverse && into > 0.2 && into < shot.Seconds - 0.2 && File.Exists(_board.ShotVideo(shot)), () => Split(item)));
        return items;
    }

    private static readonly (string Look, string Name)[] Looks =
        [("", "As it is"), ("bw", "Black and white"), ("sepia", "Sepia"), ("warm", "Warm"), ("cool", "Cool"), ("vintage", "Vintage")];

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
        var clip = _board.ShotVideo(shot);
        if (into <= 0.2 || into >= shot.Seconds - 0.2 || !File.Exists(clip))
            return;
        SaveNow();
        StopPlaying();
        DropViewer();
        Remember();
        try
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
                File.Copy(clip, Path.Combine(_board.ReferencesFolder, name));
            }
            var speed = shot.Speed ?? 1;
            var second = new VideoShot
            {
                Id = _board.NewId("s"), Name = shot.Name, File = name, In = shot.In + into * speed, Seconds = shot.Seconds - into, Speed = shot.Speed,
                Volume = shot.Volume, Prompt = shot.Prompt, Camera = shot.Camera, Seed = Storyboard.NewSeed(),
                Crop = shot.Crop is { } c ? new VideoBox { X = c.X, Y = c.Y, Width = c.Width, Height = c.Height } : null,
                Color = shot.Color is { } k ? new VideoColor { Brightness = k.Brightness, Contrast = k.Contrast, Saturation = k.Saturation, Look = k.Look } : null,
                Freeze = shot.Freeze is { } f && f > into ? f - into : null,
            };
            shot.File = name;
            shot.Scene = null;
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

    /// <summary>What right-clicking a title offers: its words, its place on the frame, how long it shows, and Delete.</summary>
    public IReadOnlyList<PanelMenuItem?> MenuFor(OverlayItem item)
    {
        var free = !Busy(_board.Folder);
        var o = item.Overlay;
        var words = o.File is null && o.Scene is null;
        return
        [
            new("Change the words", free && words, () => RenameAsked?.Invoke(item, o.Text.Replace("\\n", "\n"))),
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
            new($"{Mark(o.Background is not null)}Box behind the words", free && words,
                () => Edit(() => _board.Overlay(item.Id)?.Let(x => x.Background = x.Background is null ? "#000000A0" : null))),
            null,
            new("Delete", free, () => Edit(() => _board.Overlays.RemoveAll(x => x.Id == item.Id))),
            null,
            new("Create", free, () => CreateTitle(PlayPosition)),
        ];
    }

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
