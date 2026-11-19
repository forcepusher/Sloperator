using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sloperator.Core.Chats;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;

namespace Sloperator.App.ViewModels;

/// <summary>A picture in the Image editor panel's list: its folder, its name, and a small copy of its newest version.</summary>
public sealed partial class PictureItem(string folder) : MediaFolderItem(folder)
{
    /// <summary>Its newest version, 160 wide; null before it has one.</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;

    private string? _thumbnailOf;

    /// <summary>The thumbnail read again when the newest version is another file.</summary>
    internal void LoadThumbnail()
    {
        if (Versions.Count == 0 || Versions[^1] == _thumbnailOf)
            return;
        var path = _thumbnailOf = Versions[^1];
        Task.Run(() =>
        {
            try
            {
                using var stream = MediaFolders.ReadShared(path);
                var bitmap = UprightPicture.ToWidth(stream, 160);
                Dispatcher.UIThread.Post(() =>
                {
                    var old = Thumbnail;
                    Thumbnail = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
            }
        });
    }
}

/// <summary>A version of the picture or sound shown: its number from 1, and whether it is the one in the big view.</summary>
public sealed partial class VersionItem(int number, string path) : ObservableObject
{
    public int Number { get; } = number;
    public string Path { get; } = path;

    /// <summary>A picture's version small, for its button in the strip; null for a sound's, and until it is read.</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;
    public string Label => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Tip => $"Version {Number} · {System.IO.Path.GetFileName(Path)}";

    [ObservableProperty]
    private bool _isShown;

    public override string ToString() => $"Version {Number}";
}

/// <summary>
/// The Image editor panel, beside the chat, a sibling of the Video editor for single pictures:
/// the pictures in SloperatorData\pictures (a folder each, its versions the picture files in it), the picked one big
/// with its versions under it, and how the version shown was made under it when Prompt metadata is on (as the
/// File inspector shows it). The agent makes them one of the three ways (a raster picture with the comfyui tool, a
/// vector one as an SVG and a 3D one as a Three.js page, drawn by render), saving each result into the picture's
/// folder, so a redo is a new version and the earlier ones stay; one drawn from a scene keeps its scene, and is
/// changed there. A click on a picture or a
/// version picks it into the chat's box; a box dragged over the big picture picks that part, with a mask
/// of it, for the agent to redraw only that part (sloperator/redraw_area). What it shares with the Audio
/// editor is <see cref="MediaEditorViewModel{TItem}"/>'s.
/// </summary>
public sealed partial class ImageEditorViewModel : MediaEditorViewModel<PictureItem>
{
    /// <param name="chat">The chat the panel is beside, where picks go and its messages are sent; null in tests.</param>
    public ImageEditorViewModel(MainWindowViewModel? chat, string root, string picksDir)
        : base(chat, root, picksDir, PictureFolder.Folders, "picture")
    {
        Refresh();
    }

    // ---- the pictures -------------------------------------------------------------------------------

    public override string Title => Selected is not { } p ? "Pictures the agent makes and changes for you, each with its versions"
        : Shown is { } v ? $"{p.Name} · version {v.Number} of {p.Versions.Count}" : $"{p.Name} · not made yet";

    public override string EmptyText => Selected is { IsMade: false } ? "Not made yet. Say in the chat what it shows, and the agent makes it here."
        : HasItems ? "Pick a picture" : "No pictures yet. New picture starts one; say in the chat what it shows.";

    /// <summary>The version shown, decoded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private Bitmap? _picture;

    public bool HasPicture => Picture is not null && Shown is not null;

    protected override void ShownChanged() => OnPropertyChanged(nameof(HasPicture));

    /// <summary>The version's size in pixels.</summary>
    private (int Width, int Height) _size;

    /// <summary>The zoom of the big view (1 fits it).</summary>
    [ObservableProperty]
    private double _zoom = 1;

    /// <summary>The part marked on the big view; null with none.</summary>
    [ObservableProperty]
    private DocumentArea? _mark;

    /// <summary>
    /// A left drag paints with the brush; without it, a drag marks a box. The two ways to mark a part to redraw, in
    /// the bar as the File inspector's Area and Text are (the user, 2026-11-12, "Do we support inpainting mask?":
    /// only a box, which takes in whatever is around the thing meant).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArea))]
    private bool _isBrush;

    /// <summary>A drag marks a box: on whenever the brush is off. Pressed while on, it stays on: one of the two always is.</summary>
    public bool IsArea
    {
        get => !IsBrush;
        set
        {
            if (value)
                IsBrush = false;
            else
                OnPropertyChanged();
        }
    }

    /// <summary>How wide the brush is, in parts of the picture's width.</summary>
    [ObservableProperty]
    private double _brushSize = BrushStroke.DefaultWidth;

    /// <summary>What was painted on the big view, each stroke in parts of the picture; null with nothing painted.</summary>
    [ObservableProperty]
    private IReadOnlyList<BrushStroke>? _strokes;

    protected override PictureItem NewItem(string folder) => new(folder);

    protected override void OnGone(PictureItem item) => item.Thumbnail?.Dispose();

    protected override void OnVersionsRead(PictureItem item) => item.LoadThumbnail();

    /// <summary>How tall a version's small copy is read, in pixels: its button shows it 40 high, at up to 200%.</summary>
    private const int ThumbHeight = 80;

    /// <summary>
    /// Each version's small copy for its button (the user's go-ahead, 2026-11-12): the strip was numbers, "1 2 3",
    /// and which version was which took a click on each. Read one after another off the window's thread.
    /// </summary>
    protected override void VersionsListed(IReadOnlyList<VersionItem> versions)
    {
        if (versions.Count == 0)
            return;
        Task.Run(() =>
        {
            foreach (var version in versions)
            {
                Bitmap? bitmap = null;
                try
                {
                    using var stream = MediaFolders.ReadShared(version.Path);
                    bitmap = UprightPicture.ToHeight(stream, ThumbHeight);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    continue;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    // Another picture is shown by now, or the panel has closed: its strip is gone.
                    if (Closed || !Versions.Contains(version))
                        bitmap.Dispose();
                    else
                        version.Thumbnail = bitmap;
                });
            }
        });
    }

    protected override void VersionsGone(IReadOnlyList<VersionItem> versions)
    {
        foreach (var version in versions)
        {
            var old = version.Thumbnail;
            version.Thumbnail = null;
            old?.Dispose();
        }
    }

    /// <summary>A version into the big view, its Prompt metadata beside it.</summary>
    protected override void ShowVersion(VersionItem? version)
    {
        foreach (var v in Versions)
            v.IsShown = ReferenceEquals(v, version);
        Shown = version;
        Mark = null;
        Strokes = null;
        Zoom = 1;
        var old = Picture;
        Picture = null;
        old?.Dispose();
        Recipe = null;
        if (version is null)
            return;
        var path = version.Path;
        Task.Run(() =>
        {
            Bitmap? bitmap = null;
            try
            {
                using var stream = MediaFolders.ReadShared(path);
                bitmap = UprightPicture.Load(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            var recipe = MediaRecipe.Read(path);
            Dispatcher.UIThread.Post(() =>
            {
                if (Closed || Shown?.Path != path)
                {
                    bitmap?.Dispose();
                    return;
                }
                Picture = bitmap;
                _size = bitmap is null ? default : (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                Recipe = recipe;
            });
        });
    }

    // ---- edits made here, each a new version --------------------------------------------------------
    // (the Audio editor has had its Edit line since it was made; a picture took a model, or another program, to crop.)

    private bool _editing;

    /// <summary>The version shown is read, and no edit is being saved.</summary>
    private bool CanEdit() => HasPicture && !_editing;

    private bool CanCrop() => CanEdit() && Mark is not null;

    private string NoPicture => _editing ? "An edit is being saved" : "Pick a picture that has been made first";

    public string CropTip => CanCrop() ? "Keep only the box marked, as a new version" : CanEdit() ? "Drag a box over the picture first (with Area on)" : NoPicture;

    public string TurnLeftTip => CanEdit() ? "A quarter turn to the left, as a new version" : NoPicture;

    public string TurnRightTip => CanEdit() ? "A quarter turn to the right, as a new version" : NoPicture;

    public string MirrorTip => CanEdit() ? "Its left and right swapped, as a new version" : NoPicture;

    public string FlipTip => CanEdit() ? "Upside down, as a new version" : NoPicture;

    private void OnEditsChanged()
    {
        foreach (var name in (string[])[nameof(CropTip), nameof(TurnLeftTip), nameof(TurnRightTip), nameof(MirrorTip), nameof(FlipTip)])
            OnPropertyChanged(name);
        CropCommand.NotifyCanExecuteChanged();
        TurnLeftCommand.NotifyCanExecuteChanged();
        TurnRightCommand.NotifyCanExecuteChanged();
        MirrorCommand.NotifyCanExecuteChanged();
        FlipCommand.NotifyCanExecuteChanged();
    }

    partial void OnMarkChanged(DocumentArea? value) => OnEditsChanged();

    partial void OnPictureChanged(Bitmap? value) => OnEditsChanged();

    [RelayCommand(CanExecute = nameof(CanCrop))]
    private void Crop() => Edit(new PictureEditRequest(PictureEditKind.Crop) { Area = Mark });

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void TurnLeft() => Edit(new PictureEditRequest(PictureEditKind.Rotate) { Degrees = 270 });

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void TurnRight() => Edit(new PictureEditRequest(PictureEditKind.Rotate) { Degrees = 90 });

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Mirror() => Edit(new PictureEditRequest(PictureEditKind.Flip));

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Flip() => Edit(new PictureEditRequest(PictureEditKind.Flip) { Vertical = true });

    /// <summary>
    /// The edit made on the version shown, off the window's thread: saved as the picture's next version
    /// (<see cref="PictureEdit"/>), which is then shown; the version before stays.
    /// </summary>
    private void Edit(PictureEditRequest request)
    {
        if (Shown is not { } version || !CanEdit())
            return;
        _editing = true;
        OnEditsChanged();
        // What was picked is of the version before: it leaves the box.
        Unpick();
        var (path, root) = (version.Path, Root);
        Task.Run(() =>
        {
            try
            {
                PictureEdit.Save(path, request, root);
                Dispatcher.UIThread.Post(() =>
                {
                    _editing = false;
                    if (Closed)
                        return;
                    OnEditsChanged();
                    Refresh();
                });
            }
            catch (Exception ex) when (ex is PictureEditException or IOException or UnauthorizedAccessException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _editing = false;
                    OnEditsChanged();
                    Chat?.AddNotice($"The edit could not be saved: {ex.Message}", isError: ex is not PictureEditException, ex);
                });
            }
        });
    }

    // ---- what is picked, into the chat's box ------------------------------------------------------

    /// <summary>What is picked: the picture (its version shown, or none yet), or a part of the version with its mask.</summary>
    /// <param name="Painted">The part was painted with the brush: the mask is its shape, and <paramref name="Place"/> the box around it.</param>
    private sealed record Picked(PictureItem Picture, VersionItem? Version, string File, string? Mask = null, string? Place = null, bool Painted = false);

    private Picked? _pick;

    protected override bool HasPick => _pick is not null;

    /// <summary>The picture selected, as the version shown (or the folder of one not made yet), into the box.</summary>
    protected override void PickWhole()
    {
        if (Selected is not { } picture)
            return;
        Mark = null;
        Strokes = null;
        DropFiles();
        var file = Shown?.Path ?? picture.Folder;
        _pick = new Picked(picture, Shown, file);
        Box.Put([(file, Shown is { } v ? $"Version {v.Number} · {picture.Name}" : $"New · {picture.Name}")]);
    }

    /// <summary>
    /// A box dragged over the big picture: a picture of that part and a mask of it (white in the box, soft at
    /// its edge) are saved for the message, and the part goes into the box as "Area of version 2 · Lighthouse";
    /// the agent redraws only it. A click (null) picks the version shown whole.
    /// </summary>
    public void PickArea(DocumentArea? area)
    {
        if (area is null)
        {
            PickWhole();
            return;
        }
        if (Selected is not { } picture || Shown is not { } version || _size.Width == 0)
            return;
        Mark = area;
        Strokes = null;
        var (width, height) = _size;
        var dir = PicksDir;
        Task.Run(() =>
        {
            try
            {
                var pixels = DocumentArea.Decode(version.Path) ?? throw new IOException("the picture can't be read");
                var crop = DocumentArea.SaveNew(area.Crop(pixels), dir);
                var mask = Path.Combine(dir, Path.GetFileNameWithoutExtension(crop).Replace("Area", "Mask", StringComparison.Ordinal) + ".png");
                PictureFolder.SaveMask(area, pixels.Width, pixels.Height, mask);
                Dispatcher.UIThread.Post(() =>
                {
                    if (Closed || !Equals(Mark, area) || !ReferenceEquals(Shown, version))
                    {
                        PastedFiles.TryDelete(crop);
                        PastedFiles.TryDelete(mask);
                        return;
                    }
                    DropFiles();
                    _pick = new Picked(picture, version, crop, mask, area.Place(pixels.Width, pixels.Height, "pixels"));
                    Box.Put([(crop, $"Area of version {version.Number} · {picture.Name}")]);
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(() => Chat?.AddNotice($"The marked part could not be saved: {ex.Message}", isError: true, ex));
            }
        });
    }

    /// <summary>
    /// Strokes painted over the big picture with the brush, the new one last: a mask of what they cover (white
    /// there, soft at its edge) and a picture of the box around them are saved for the message, and the part goes
    /// into the box as "Painted part of version 2 · Lighthouse"; the agent redraws only what was painted. Each
    /// stroke more makes them again, the ones before deleted.
    /// </summary>
    public void PickPainted(IReadOnlyList<BrushStroke> strokes)
    {
        if (Selected is not { } picture || Shown is not { } version || _size.Width == 0 || strokes.Count == 0)
            return;
        Mark = null;
        Strokes = strokes;
        var dir = PicksDir;
        Task.Run(() =>
        {
            try
            {
                var pixels = DocumentArea.Decode(version.Path) ?? throw new IOException("the picture can't be read");
                var area = PictureFolder.AreaOf(strokes, pixels.Width, pixels.Height) ?? throw new IOException("nothing of the picture was painted");
                var crop = DocumentArea.SaveNew(area.Crop(pixels), dir);
                var mask = Path.Combine(dir, Path.GetFileNameWithoutExtension(crop).Replace("Area", "Mask", StringComparison.Ordinal) + ".png");
                PictureFolder.SaveMask(strokes, pixels.Width, pixels.Height, mask);
                Dispatcher.UIThread.Post(() =>
                {
                    // Another stroke, another version or nothing painted any more by now: these aren't the part.
                    if (Closed || !ReferenceEquals(Strokes, strokes) || !ReferenceEquals(Shown, version))
                    {
                        PastedFiles.TryDelete(crop);
                        PastedFiles.TryDelete(mask);
                        return;
                    }
                    DropFiles();
                    _pick = new Picked(picture, version, crop, mask, area.Place(pixels.Width, pixels.Height, "pixels"), Painted: true);
                    Box.Put([(crop, $"Painted part of version {version.Number} · {picture.Name}")]);
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(() => Chat?.AddNotice($"The painted part could not be saved: {ex.Message}", isError: true, ex));
            }
        });
    }

    /// <summary>A part picked before goes: its picture and mask, which weren't sent, with it.</summary>
    private void DropFiles()
    {
        if (_pick is { Mask: { } mask } pick)
        {
            PastedFiles.TryDelete(pick.File);
            PastedFiles.TryDelete(mask);
        }
        _pick = null;
    }

    /// <summary>
    /// Ctrl+Z: the stroke painted last is taken back, and the part is the ones before it; with none left, nothing is
    /// painted or picked. False when nothing was painted.
    /// </summary>
    public bool UndoStroke()
    {
        if (Strokes is not { Count: > 0 } strokes)
            return false;
        if (strokes.Count == 1)
            Unpick();
        else
            PickPainted([.. strokes.Take(strokes.Count - 1)]);
        return true;
    }

    /// <summary>Esc, a click away: what was picked leaves the box. False when nothing was.</summary>
    public bool Unpick()
    {
        Mark = null;
        Strokes = null;
        if (_pick is null)
            return false;
        DropFiles();
        Box.Put([]);
        RaiseUnpicked();
        return true;
    }

    protected override void OnTakenOff()
    {
        if (_pick is null)
            return;
        DropFiles();
        Mark = null;
        Strokes = null;
        RaiseUnpicked();
    }

    protected override void UnpickOf(PictureItem item)
    {
        if (ReferenceEquals(_pick?.Picture, item))
            Unpick();
    }

    /// <summary>
    /// What a message sent with a pick carries: over its bubble the picture and what of it; for the model, the
    /// picture's folder and versions, and how to make the next one there (the comfyui tool's or render's save_to:
    /// a picture is made one of the three ways, a raster one by ComfyUI, a vector one as an SVG, a 3D one as a
    /// Three.js page), or for a part, the picture, the box in its pixels and the mask, with how to redraw only that
    /// part. A version drawn from a scene is changed in its scene, which the version itself keeps.
    /// </summary>
    public override (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (_pick is not { } pick || !files.Contains(pick.File, StringComparer.OrdinalIgnoreCase))
            return null;
        var picture = pick.Picture;
        var next = "so the result is its next version there (the panel shows the newest; earlier versions stay)";
        var opening = $"[The Image editor panel is open beside the chat. ";
        if (pick.Version is null)
            return ($"Image editor · {picture.Name} · new",
                opening + $"They picked a new picture, \"{picture.Name}\", not made yet: what they say is what it shows. Make it the way that suits it, " +
                $"into the picture's folder, \"{picture.Folder}\", {next}: a raster picture (a photo, a painting, an illustration) with the comfyui tool and save_to " +
                "set to that folder (sloperator/text_to_image suits a PC without a graphics card); a vector one (an icon, a logo, a diagram) as an SVG, or a 3D one " +
                "as a Three.js page, drawn with render and save_to set to that folder.]");
        var version = pick.Version;
        var versions = string.Join(", ", picture.Versions.Select((v, i) => $"{i + 1}: {v}"));
        // Drawn from a scene, it is changed there: the scene as this version has it, in a file to edit.
        var scene = SceneOf(version.Path);
        var drawn = scene is null ? null
            : $"This version was drawn from {(scene.Value.Source.Svg ? "an SVG" : "a Three.js page")}, \"{scene.Value.File}\": ";
        if (pick.Mask is { } mask)
        {
            var part = pick.Painted
                // The box says where; the mask is the shape, and only what it covers is theirs to change.
                ? $"They painted with a brush over a part of version {version.Number} of the picture \"{picture.Name}\" ({version.Path}) to redraw. " +
                  $"The mask has the shape they painted; the box around it is {pick.Place}, and the picture {pick.File} attached below is that box. "
                : $"They marked a part of version {version.Number} of the picture \"{picture.Name}\" ({version.Path}) to redraw: {pick.Place}. " +
                  $"The picture {pick.File} attached below is that part. ";
            var title = $"Image editor · {picture.Name} · {(pick.Painted ? "painted part" : "area")} of version {version.Number}";
            if (drawn is not null)
                return (title, opening + part + drawn + $"change only what is in that part as they say, in the scene (edit_file), the rest kept, " +
                    $"and draw it again with render and save_to set to the picture's folder, \"{picture.Folder}\", so it becomes the picture's next version. " +
                    $"Its versions: {versions}.]");
            return (title, opening + part +
                $"Redraw only {(pick.Painted ? "what they painted" : "that part")} as they say, the rest kept: run the comfyui tool with " +
                $"name=sloperator/redraw_area, set 4.image to \"{version.Path}\", 5.image to the mask \"{mask}\" (white where to redraw), 2.text to what goes there " +
                $"in the picture's style (and 1.ckpt_name to the checkpoint it was made with, if its metadata says), and save_to \"{picture.Folder}\", " +
                $"so it becomes the picture's next version. Its versions: {versions}.]");
        }
        var picked = opening + $"They picked version {version.Number} of {picture.Versions.Count} of the picture \"{picture.Name}\" ({version.Path}, attached below). ";
        // What needs no model: to the pixel, at once, each its next version.
        var exact = $"An exact edit of it as it is (a crop, another size, a turn, a mirror, its colours, another kind of file) is edit_picture with path \"{version.Path}\", " +
                    "which saves the picture's next version. ";
        if (drawn is not null)
            return ($"Image editor · {picture.Name} · version {version.Number}",
                picked + drawn + $"to change what it shows, change that scene (edit_file) and draw it again with render and save_to set to the picture's folder, " +
                $"\"{picture.Folder}\", {next}. {exact}Its versions: {versions}.]");
        return ($"Image editor · {picture.Name} · version {version.Number}",
            picked + $"To change what it shows or make it again, make it with the comfyui tool and save_to set to the picture's folder, \"{picture.Folder}\", {next}; " +
            "image_to_image keeps its layout, text_to_image starts afresh from the prompt in its metadata. A vector or 3D picture of it instead is an SVG or a " +
            $"Three.js page drawn with render and the same save_to. {exact}Its versions: {versions}.]");
    }

    /// <summary>
    /// A version drawn from a scene offers the scene itself: an icon or a logo is wanted as its SVG as often as its
    /// PNG, and the scene was written wherever the agent wrote it (the scratch folder, as a rule).
    /// </summary>
    protected override IEnumerable<PanelMenuItem?> MoreFor(VersionItem version)
    {
        if (Core.Render.SceneStamp.Of(version.Path) is not { } source)
            yield break;
        yield return new(source.Svg ? "Show its SVG in folder" : "Show its Three.js page in folder", true, () =>
        {
            if (SceneOf(version.Path) is { } scene)
                FilePreview.ShowInFolder(scene.File);
            else
                Chat?.AddNotice("This version's scene was too long to keep in the picture, and its file is gone.", isError: true);
        });
    }

    /// <summary>
    /// The scene a version was drawn from, in a file to change: the scene's own file while it is still as the version
    /// has it, else the version's copy of it written beside the version ("plane 2.svg" for "plane 2.png": the scene's
    /// file was changed since, or cleared with the scratch folder). Null for a version no scene drew, or whose scene
    /// was too long to keep and is gone.
    /// </summary>
    internal static (string File, Core.Render.SceneSource Source)? SceneOf(string version)
    {
        if (Core.Render.SceneStamp.Of(version) is not { } source)
            return null;
        try
        {
            if (File.Exists(source.File) && (source.Text.Length == 0 || Same(File.ReadAllText(source.File), source.Text)))
                return (source.File, source);
            if (source.Text.Length == 0)
                return null;
            var copy = Path.ChangeExtension(version, source.Extension);
            if (!File.Exists(copy) || !Same(File.ReadAllText(copy), source.Text))
                File.WriteAllText(copy, source.Text);
            return (copy, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        static bool Same(string a, string b) => string.Equals(a.ReplaceLineEndings("\n"), b.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>The message went with the pick: it is no longer picked (its files went with it, and stay).</summary>
    public override void Sent()
    {
        if (_pick is null)
            return;
        AskedFor(_pick.Picture);
        _pick = null;
        Mark = null;
        Strokes = null;
        RaiseUnpicked();
    }

    public override void Dispose()
    {
        base.Dispose();
        DropFiles();
        Picture?.Dispose();
        VersionsGone(Versions.ToList());
        foreach (var picture in Items)
            picture.Thumbnail?.Dispose();
    }
}
