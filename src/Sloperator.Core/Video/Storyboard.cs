using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sloperator.Core.Video;

/// <summary>
/// A video as the user or the agent plans it: shots in order, each going from a first frame to a
/// last one in so many seconds, the pictures they start and end on, and the sounds laid over them.
/// The video is its folder (see StoryboardFolder.cs): References with the pictures frames are made
/// from, and Generated with what Sloperator makes (each shot's frames and clip, the songs), each file with a .json beside it saying what it is
/// and how it was made, and its ComfyUI workflow; the Video editor panel and the video tool both read
/// and write it. Here it is the whole video at once, as the agent writes and reads it in JSON.
/// </summary>
public sealed partial class Storyboard
{
    /// <summary>How long a dissolve or a fade between two shots takes: the two overlap that long.</summary>
    public const double DissolveSeconds = 0.5, FadeSeconds = 1.0;

    public string Title { get; set; } = "";

    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 24;

    /// <summary>The pictures: frames shots start and end on, and references (a character, a place) others are made from.</summary>
    public List<VideoFrame> Frames { get; set; } = [];

    public List<VideoShot> Shots { get; set; } = [];

    /// <summary>
    /// The sounds laid over the shots' own, each from where it starts for as long as it plays, as a
    /// video editor's sound line has them (the user's call, 2026-10-24: "make the sounds line into an
    /// overlay sound editor... kinda like in any popular video editing software"): sound files of the
    /// user's and songs Sloperator makes, overlapping as they please.
    /// </summary>
    public List<VideoSound> Sounds { get; set; } = [];

    /// <summary>How many sound lines the video has, the sounds on them by <see cref="VideoSound.SoundLine"/> (the user: "we also need to be able to create multiple sound tracks").</summary>
    public int SoundLines { get; set; } = 1;

    /// <summary>Each sound line's volume, 1 as its sounds are (the user: "Each sound line needs a volume slider"); one for each sound line.</summary>
    public List<double> SoundLineVolumes { get; set; } = [];

    /// <summary>How loud a sound line is: 1 unless it is turned up or down.</summary>
    public double LineVolume(int soundLine) => soundLine >= 0 && soundLine < SoundLineVolumes.Count ? SoundLineVolumes[soundLine] : 1;

    /// <summary>The voice the shots' lines are said in ("a man in his forties, warm and a little hoarse, deadpan").</summary>
    public string Voice { get; set; } = "";

    /// <summary>The seed the lines are said with.</summary>
    public long VoiceSeed { get; set; }

    /// <summary>Titles, captions, pictures and scenes shown over the video, each for a while.</summary>
    public List<VideoOverlay> Overlays { get; set; } = [];

    /// <summary>Seconds the video fades in from black over at its start, and out over at its end; null its usual (0.4 and 1), 0 none.</summary>
    public double? FadeIn { get; set; }

    public double? FadeOut { get; set; }

    /// <summary>The shots' lines shown as captions at the bottom while they are said.</summary>
    public bool Captions { get; set; }

    /// <summary>How the joined video is encoded: draft (quick, larger and softer), good (the usual) or best (slow, the sharpest).</summary>
    public string Quality { get; set; } = "";

    public VideoOverlay? Overlay(string? id) => id is null ? null : Overlays.FirstOrDefault(o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The transitions there are, by the name a shot gives (ffmpeg's xfade names, and a dissolve and a fade through black).</summary>
    public static readonly IReadOnlyList<string> Transitions =
    [
        VideoShot.Cut, VideoShot.Dissolve, VideoShot.Fade, "fadewhite", "wipeleft", "wiperight", "wipeup", "wipedown", "slideleft", "slideright", "slideup", "slidedown",
        "smoothleft", "smoothright", "smoothup", "smoothdown", "circleopen", "circleclose", "circlecrop", "rectcrop", "radial", "pixelize", "zoomin", "hblur", "distance",
        "squeezeh", "squeezev", "diagtl", "diagtr", "diagbl", "diagbr", "hlslice", "hrslice", "vuslice", "vdslice", "coverleft", "coverright", "revealleft", "revealright",
    ];

    /// <summary>A transition as a person or a model may name it ("wipe", "iris", "fade to white"), as <see cref="Transitions"/> has it; cut for none it knows.</summary>
    public static string TransitionNamed(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        return n switch
        {
            "" or "cut" or "none" or "hardcut" => VideoShot.Cut,
            "dissolve" or "crossfade" or "crossdissolve" or "mix" => VideoShot.Dissolve,
            "fade" or "fadetoblack" or "fadeblack" or "black" or "dip" or "diptoblack" or "fadethroughblack" => VideoShot.Fade,
            "fadetowhite" or "white" or "diptowhite" or "flash" or "fadethroughwhite" => "fadewhite",
            "wipe" => "wipeleft",
            "slide" or "push" or "pushleft" => "slideleft",
            "pushright" => "slideright",
            "smooth" => "smoothleft",
            "circle" or "iris" or "irisopen" => "circleopen",
            "irisclose" => "circleclose",
            "zoom" => "zoomin",
            "blur" => "hblur",
            "clock" or "clockwipe" => "radial",
            "pixel" or "pixels" or "pixelate" => "pixelize",
            "squeeze" => "squeezeh",
            "cover" => "coverleft",
            "reveal" => "revealleft",
            _ => Transitions.FirstOrDefault(t => t == n) ?? VideoShot.Cut,
        };
    }

    /// <summary>The looks a shot's colours may take.</summary>
    public static readonly IReadOnlyList<string> Looks = ["", "bw", "sepia", "warm", "cool", "vintage"];

    /// <summary>The video's folder; not saved.</summary>
    [JsonIgnore]
    public string Folder { get; set; } = "";

    public VideoFrame? Frame(string? id) => id is null ? null : Frames.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public VideoShot? Shot(string? id) => id is null ? null : Shots.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public VideoSound? Sound(string? id) => id is null ? null : Sounds.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The video's folder of references (the user's call, 2026-10-24: "References (where either image
    /// file or generated image present)"): image files, the user's own pictures copied in, and
    /// generated images, the references Sloperator makes ("References/Mira.png"), which frames name in
    /// "from" to be made from.
    /// </summary>
    public const string ReferencesName = "References";

    public string ReferencesFolder => Path.Combine(Folder, ReferencesName);

    /// <summary>
    /// Where a frame's picture is once it is made (or taken from the user's file): a reference's in
    /// References, a shot's frame in the folder of the shot it is the start or end of.
    /// </summary>
    public string FramePicture(VideoFrame frame) => Full(frame.Place ?? PlaceFor(frame));

    /// <summary>What a frame is and how it was made: its settings, and the stamp, template and look of the picture made.</summary>
    public string FrameMeta(VideoFrame frame) => MetaOf(FramePicture(frame));

    /// <summary>The kinds of picture a reference can be.</summary>
    public static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <summary>The kinds of video a reference can be: a clip of the user's whose movement, look or end a shot takes.</summary>
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v"];

    public static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The video in References a shot's "motion", a frame's "of" or "from" names: by its path there
    /// ("dance.mp4", "Mira/walk"), with or without its extension, by its name alone, or a video
    /// anywhere by its full path. Null when it names none that is there.
    /// </summary>
    public string? ReferenceVideo(string? name)
    {
        name = name?.Trim().Trim('"').Trim().TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(name))
            return null;
        try
        {
            if (Path.IsPathRooted(name))
                return File.Exists(name) && IsVideoFile(name) ? Path.GetFullPath(name) : null;
            if (Folder.Length == 0)
                return null;
            var relative = name.Replace('/', Path.DirectorySeparatorChar);
            if (relative.StartsWith(ReferencesName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                relative = relative[(ReferencesName.Length + 1)..];
            var place = Path.Combine(ReferencesFolder, relative);
            if (File.Exists(place) && IsVideoFile(place))
                return Path.GetFullPath(place);
            foreach (var extension in VideoExtensions)
                if (File.Exists(place + extension))
                    return Path.GetFullPath(place + extension);
            if (!name.Contains('/') && !name.Contains('\\'))
                return ReferenceVideos().FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase) ||
                                                             string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { }
        return null;
    }

    /// <summary>The videos in the References folder (in folders of their own too), without the versions kept of pieces, by their full paths.</summary>
    public IReadOnlyList<string> ReferenceVideos()
    {
        try
        {
            return Folder.Length > 0 && Directory.Exists(ReferencesFolder)
                ? new DirectoryInfo(ReferencesFolder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .Where(f => VideoExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && !IsTake(f.Name) && !f.Name.StartsWith('.') &&
                                !string.Equals(f.Directory?.Name, VersionsName, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FullName).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The picture an entry of a frame's "from" names: a frame of the video by its id, else in
    /// References a picture by its path ("Mira/side", with or without its extension) or a
    /// reference's folder by its name ("Mira": its main picture, see <see cref="MainPicture"/>),
    /// else a picture anywhere by its full path. Null when it names none that is there, or a frame
    /// of the video (whose picture may not be made yet: see <see cref="Frame"/>).
    /// </summary>
    /// <summary>
    /// Any file a shot or an overlay names (a video, a picture, an SVG or a page): by its full path, in
    /// References or the video's folder, with or without its extension; null when there is none.
    /// </summary>
    public string? FindFile(string name)
    {
        name = name.Trim().Trim('"').Trim();
        if (name.Length == 0)
            return null;
        try
        {
            if (Path.IsPathRooted(name))
                return File.Exists(name) ? Path.GetFullPath(name) : null;
            if (Folder.Length == 0)
                return null;
            var relative = name.Replace('/', Path.DirectorySeparatorChar);
            foreach (var place in new[] { Path.Combine(ReferencesFolder, relative), Path.Combine(Folder, relative) })
                if (File.Exists(place))
                    return Path.GetFullPath(place);
            if (Directory.Exists(ReferencesFolder))
                foreach (var file in Directory.EnumerateFiles(ReferencesFolder, "*", SearchOption.AllDirectories))
                    if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        return Path.GetFullPath(file);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public string? ReferenceFile(string name)
    {
        name = name.Trim().Trim('"').Trim().TrimEnd('/', '\\');
        if (name.Length == 0 || Frame(name) is not null)
            return null;
        try
        {
            if (Path.IsPathRooted(name))
                return File.Exists(name) ? Path.GetFullPath(name) : Directory.Exists(name) ? MainPicture(name) : null;
            if (Folder.Length == 0)
                return null;
            var relative = name.Replace('/', Path.DirectorySeparatorChar);
            foreach (var place in new[] { Path.Combine(ReferencesFolder, relative), Path.Combine(Folder, relative) })
            {
                if (File.Exists(place))
                    return Path.GetFullPath(place);
                foreach (var extension in PictureExtensions)
                    if (File.Exists(place + extension))
                        return Path.GetFullPath(place + extension);
                if (Directory.Exists(place) && MainPicture(place) is { } main)
                    return main;
            }
            // A picture named without its reference's folder ("side.png" for "Mira/side.png").
            if (!name.Contains('/') && !name.Contains('\\'))
                return ReferenceFiles().FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase) ||
                                                            string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { }
        return null;
    }

    /// <summary>A picture in References as "from" names it: "Mira/side.png".</summary>
    public string InReferences(string picture) => Path.GetRelativePath(ReferencesFolder, picture).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// A picture of the user's put into a folder as it is, byte for byte (the user's call,
    /// 2026-10-24: the source picture stays as they pasted or made it, never cut, resized or made
    /// again; the references are made from it): under its own name, or "Carl (2).png" where another
    /// file has that name or a piece is planned (a .json of that name). The same picture there
    /// already is the one. Its path.
    /// </summary>
    public static string KeepPicture(string picture, string folder)
    {
        picture = Path.GetFullPath(picture);
        var (stem, extension) = (Path.GetFileNameWithoutExtension(picture), Path.GetExtension(picture));
        var target = Path.GetFullPath(Path.Combine(folder, stem + extension));
        for (var n = 2; !Same(picture, target); n++)
        {
            if (!File.Exists(target) && !File.Exists(MetaOf(target)))
            {
                Directory.CreateDirectory(folder);
                File.Copy(picture, target);
                break;
            }
            target = Path.GetFullPath(Path.Combine(folder, $"{stem} ({n}){extension}"));
        }
        return target;

        static bool Same(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length)
                return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
    }

    /// <summary>
    /// The pictures from outside the video that frames are made from, named in "from" by their full
    /// paths (one the user attached to a message), copied into References as they are (see
    /// <see cref="KeepPicture"/>), as one dropped on the Video editor panel goes; "from" names the copy
    /// then ("CarlLineup.png"). Told only that from takes pictures in References, the model
    /// cut one figure out of the user's lineup of four and saved that there as the source; and a
    /// pasted picture goes with its chat when the chat is deleted. Called once the video is saved, so
    /// the names of pieces not made yet are taken. What was copied: from where, as what in "from".
    /// </summary>
    public List<(string From, string To)> TakeInPictures()
    {
        var taken = new List<(string, string)>();
        if (Folder.Length == 0)
            return taken;
        var video = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder)) + Path.DirectorySeparatorChar;
        foreach (var frame in Frames)
            for (var i = 0; i < frame.From.Count; i++)
            {
                var name = frame.From[i].Trim().Trim('"').Trim();
                try
                {
                    if (!Path.IsPathRooted(name) || Frame(name) is not null)
                        continue;
                    var full = Path.GetFullPath(name);
                    if (full.StartsWith(video, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) ||
                        !PictureExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) && !IsVideoFile(full))
                        continue;
                    frame.From[i] = InReferences(KeepPicture(full, ReferencesFolder));
                    taken.Add((full, frame.From[i]));
                }
                // Not copied: the frame is made from it where it is.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        // A video of the user's a shot moves as, or a frame is where it ends, given by its path: copied in the same way.
        string? TakeVideo(string? name)
        {
            if (name?.Trim().Trim('"').Trim() is not { Length: > 0 } given || !Path.IsPathRooted(given))
                return null;
            var full = Path.GetFullPath(given);
            if (full.StartsWith(video, StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || !IsVideoFile(full))
                return null;
            var copy = InReferences(KeepPicture(full, ReferencesFolder));
            taken.Add((full, copy));
            return copy;
        }
        foreach (var shot in Shots)
            try { shot.Motion = TakeVideo(shot.Motion) ?? shot.Motion; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        foreach (var frame in Frames)
            try { frame.Of = TakeVideo(frame.Of) ?? frame.Of; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        // A sound of the user's given by its path goes into Sounds the same way, and is that copy from then on.
        foreach (var sound in Sounds)
            try
            {
                if (sound.File is not { Length: > 0 } file || !Path.IsPathRooted(file))
                    continue;
                var full = Path.GetFullPath(file.Trim().Trim('"'));
                if (!File.Exists(full))
                    continue;
                var copy = full.StartsWith(video, StringComparison.OrdinalIgnoreCase) ? full : KeepPicture(full, ReferencesFolder);
                sound.Place = Path.GetRelativePath(Folder, copy).Replace(Path.DirectorySeparatorChar, '/');
                sound.File = null;
                sound.Source = VideoSound.FromFile;
                if (!string.Equals(copy, full, StringComparison.OrdinalIgnoreCase))
                    taken.Add((full, Path.GetRelativePath(Folder, copy).Replace(Path.DirectorySeparatorChar, '/')));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return taken;
    }

    /// <summary>
    /// The picture a reference's folder stands for, named by the folder alone: the one named after
    /// it ("Mira/Mira.png", the reference Sloperator made), else its first picture by name.
    /// </summary>
    public static string? MainPicture(string folder)
    {
        try
        {
            var pictures = Directory.EnumerateFiles(folder)
                .Where(f => PictureExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !IsTake(Path.GetFileName(f)) && !Path.GetFileName(f).StartsWith('.'))
                .Order(NaturalOrder.Instance).ToList();
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            return pictures.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) is { } own
                ? Path.GetFullPath(own) : pictures.Select(Path.GetFullPath).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pictures in the References folder: the references' and the user's own (in folders of their
    /// own too), without the versions kept of them (Mira.v1.png), by their full paths, by name.
    /// </summary>
    public IReadOnlyList<string> ReferenceFiles()
    {
        try
        {
            return Folder.Length > 0 && Directory.Exists(ReferencesFolder)
                ? new DirectoryInfo(ReferencesFolder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .Where(f => PictureExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && !IsTake(f.Name) && !f.Name.StartsWith('.') &&
                                !string.Equals(f.Directory?.Name, VersionsName, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FullName).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>A version kept of a piece when it was made again: "f2.v1.png", "s1.v3.mp4".</summary>
    public static bool IsTake(string fileName) => TakeName().IsMatch(fileName);

    [System.Text.RegularExpressions.GeneratedRegex(@"\.v\d+\.[^.]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TakeName();

    /// <summary>A shot's clip, in its folder.</summary>
    public string ShotVideo(VideoShot shot) => Full(shot.Place ?? PlaceFor(shot));

    /// <summary>What a shot is and how its clip was made.</summary>
    public string ShotMeta(VideoShot shot) => MetaOf(ShotVideo(shot));

    /// <summary>The folder of a shot: its clip, and the frames it starts and ends on unless another shot's they are.</summary>
    public string ShotFolder(VideoShot shot) => Path.GetDirectoryName(ShotVideo(shot))!;

    /// <summary>The sound a lip-synced shot is made with: its line placed in it, as long as the shot.</summary>
    public string ShotAudio(VideoShot shot) => Path.ChangeExtension(ShotVideo(shot), ".wav");

    /// <summary>A shot's line as the voice model said it, and how it was said.</summary>
    public string ShotLine(VideoShot shot) => Path.ChangeExtension(ShotVideo(shot), ".line.flac");

    public string ShotLineMeta(VideoShot shot) => MetaOf(ShotLine(shot));

    /// <summary>Whether a shot says a line of its own, lip-synced.</summary>
    public static bool Speaks(VideoShot shot) => shot.LipSync && shot.Line.Trim().Length > 0;

    /// <summary>The most sound lines a video has.</summary>
    public const int MaxSoundLines = 12;

    /// <summary>The kinds of sound file the Sounds folder takes.</summary>
    public static readonly string[] SoundExtensions = [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus"];


    /// <summary>Where a sound's file is: a song once made, or the user's sound copied in.</summary>
    public string SoundFile(VideoSound sound) => Full(sound.Place ?? PlaceFor(sound));

    /// <summary>What a sound is, where it sits in the video, and how a song was made.</summary>
    public string SoundMeta(VideoSound sound) => MetaOf(SoundFile(sound));

    /// <summary>The joined video: named after it, in its folder.</summary>
    public string VideoFile => Path.Combine(Folder, Safe(Title.Length > 0 ? Title : "video") + ".mp4");

    /// <summary>What belongs to the whole video: its size, frame rate, the lines' voice and the order of its shots.</summary>
    public string VideoMeta => MetaOf(VideoFile);

    /// <summary>
    /// The video as last joined: <see cref="VideoFile"/>, or, when the video was named again since, the
    /// newest video beside the storyboard, joined under its old name (the window lost it, and
    /// "Look at the video" said it wasn't joined).
    /// </summary>
    public string JoinedFile
    {
        get
        {
            var file = VideoFile;
            if (File.Exists(file) || Folder.Length == 0 || !Directory.Exists(Folder))
                return file;
            // Not the video being joined, written to a hidden ".<title>.joining.mp4" first.
            try { return new DirectoryInfo(Folder).EnumerateFiles("*.mp4").Where(f => !f.Name.StartsWith('.')).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName ?? file; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return file; }
        }
    }

    /// <summary>
    /// Whether the joined video is older than a clip or sound in it (one made again, or a version put
    /// back): it then shows what was there before, until it is joined again.
    /// </summary>
    public bool JoinedIsStale
    {
        get
        {
            var video = JoinedFile;
            if (!File.Exists(video))
                return false;
            // Its pieces' .json too, as a clip's speed or a title changes there, and the video's own.
            var newest = Shots.Select(ShotVideo).Concat(Sounds.Select(SoundFile)).Concat(Shots.Select(ShotMeta)).Concat(Sounds.Select(SoundMeta)).Append(VideoMeta).Where(File.Exists)
                .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            return File.GetLastWriteTimeUtc(video) < newest;
        }
    }

    /// <summary>
    /// A name made safe as a file name: no character Windows forbids, at most 80 long, and not a
    /// device's name (a video called "Con" got the console for its folder, and nothing saved).
    /// </summary>
    public static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Trim().Select(c => bad.Contains(c) ? '_' : c)).Trim('.', ' ');
        safe = safe.Length == 0 ? "video" : safe.Length > 80 ? safe[..80].Trim() : safe;
        return Device().IsMatch(safe) ? safe + "_" : safe;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Device();

    /// <summary>
    /// When each shot starts in the video and how long the video is: shots follow each other, and
    /// a dissolve or a fade makes two overlap for its length.
    /// </summary>
    public (IReadOnlyList<double> Starts, double Length) Timing()
    {
        var starts = new List<double>();
        var at = 0.0;
        for (var i = 0; i < Shots.Count; i++)
        {
            var overlap = i == 0 ? 0 : Overlap(Shots[i]);
            at = Math.Max(0, at - overlap);
            starts.Add(at);
            at += Math.Max(0, Shots[i].Seconds);
        }
        return (starts, at);
    }

    /// <summary>How long a shot overlaps the one before it: its transition's length (a cut none).</summary>
    public static double Overlap(VideoShot shot) => shot.Transition == VideoShot.Cut ? 0 : Math.Min(
        shot.TransitionSeconds > 0 ? shot.TransitionSeconds : shot.Transition switch
        {
            VideoShot.Dissolve => DissolveSeconds,
            VideoShot.Fade => FadeSeconds,
            _ => OtherTransitionSeconds,
        }, shot.Seconds / 2);

    /// <summary>How long a wipe, slide or any transition but a dissolve or a fade takes, unless a shot says.</summary>
    public const double OtherTransitionSeconds = 0.8;

    /// <summary>
    /// The next free id with this prefix: f1, f2… for frames, s1, s2… for shots. A piece's files are
    /// where it is placed, not named by its id, so a new piece never gets the files one taken out
    /// left behind (see <see cref="PlaceFor(VideoShot)"/>).
    /// </summary>
    public string NewId(string prefix)
    {
        var taken = Frames.Select(f => f.Id).Concat(Shots.Select(s => s.Id)).Concat(Sounds.Select(s => s.Id)).Concat(Overlays.Select(o => o.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
            if (!taken.Contains(prefix + n))
                return prefix + n;
    }

    /// <summary>A seed to start from: fixed once given, so the same prompt makes the same picture again.</summary>
    public static long NewSeed() => Random.Shared.NextInt64(1, 1L << 48);

    /// <summary>
    /// Gives each shot without a first frame one of its own, from the shot's words and the references
    /// of the frames nearest it: the video model animates a picture well and draws one badly (the
    /// user's finding), so every shot starts on a picture a picture model made. The frames added.
    /// </summary>
    public IReadOnlyList<VideoFrame> AddFirstFrames()
    {
        var added = new List<VideoFrame>();
        for (var i = 0; i < Shots.Count; i++)
        {
            var shot = Shots[i];
            // A clip of the user's or a scene drawn starts on no picture a model makes.
            if (shot.First is not null && Frame(shot.First) is not null || shot.Assembled)
                continue;
            // The people and places the shots around it are made from.
            var near = Enumerable.Range(1, Shots.Count).SelectMany(d => new[] { i - d, i + d }).Where(j => j >= 0 && j < Shots.Count)
                .Select(j => Frame(Shots[j].First)).OfType<VideoFrame>().FirstOrDefault();
            var frame = new VideoFrame { Id = NewId("f"), Prompt = shot.Prompt.Trim(), Seed = NewSeed(), From = References(near) };
            Frames.Add(frame);
            shot.First = frame.Id;
            added.Add(frame);
        }
        return added;
    }

    /// <summary>The references a frame is made from, however far back.</summary>
    private List<string> References(VideoFrame? frame)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(VideoFrame f)
        {
            if (f.Of is { } of && Shot(of)?.First is { } first && seen.Add(first) && Frame(first) is { } start)
                Walk(start);
            foreach (var id in f.From)
                if (!seen.Add(id))
                    continue;
                else if (Frame(id) is { } from)
                {
                    if (from.Reference)
                        found.Add(from.Id);
                    else
                        Walk(from);
                }
                // A picture of the References folder no reference of the storyboard is for: the user's own.
                else if (ReferenceFile(id) is not null)
                    found.Add(id);
        }
        if (frame is not null)
            Walk(frame);
        return found;
    }

    // ---- reading and writing ---------------------------------------------------------------------

    public static Storyboard Parse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject root)
                throw new VideoException("The storyboard isn't valid: it must be a JSON object.");
            Tidy(root);
            var board = root.Deserialize(StoryboardJson.Default.Storyboard) ?? new Storyboard();
            board.Normalize();
            return board;
        }
        catch (JsonException ex)
        {
            throw new VideoException($"The storyboard isn't valid: {ex.Message}");
        }
    }

    /// <summary>
    /// Shapes a model writes that mean what the storyboard means: "from": "f1" for ["f1"], a
    /// frame's id as a number, the lines' voice under "sound" as it was before there was none.
    /// </summary>
    private static void Tidy(JsonObject root)
    {
        foreach (var frame in (root["frames"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (frame["from"] is JsonValue one)
                frame["from"] = new JsonArray(JsonValue.Create(one.ToString()));
            else if (frame["from"] is JsonArray many)
                frame["from"] = new JsonArray(many.Select(x => (JsonNode)JsonValue.Create(x?.ToString() ?? "")).ToArray());
            Text(frame, "id");
        }
        foreach (var shot in (root["shots"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var key in new[] { "id", "first", "last" })
                Text(shot, key);
        foreach (var sound in (root["sounds"] as JsonArray ?? []).OfType<JsonObject>())
            Text(sound, "id");
        foreach (var overlay in (root["overlays"] as JsonArray ?? []).OfType<JsonObject>())
        {
            Text(overlay, "id");
            // "position": {"x": …} for a box of its own.
            if (overlay["position"] is JsonObject box && overlay["box"] is null)
            {
                overlay["box"] = box.DeepClone();
                overlay.Remove("position");
            }
        }
        // "zoom": "in" or "out" for the usual slow push in or pull out, "left" or "right" for a pan.
        foreach (var shot in (root["shots"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // "color": "bw" for a look alone.
            if (shot["color"] is JsonValue c && c.TryGetValue<string>(out var look))
                shot["color"] = new JsonObject { ["look"] = look };
            if (shot["zoom"] is JsonValue z && z.TryGetValue<string>(out var zoom))
                shot["zoom"] = zoom.Trim().ToLowerInvariant() switch
                {
                    "in" or "push in" or "zoom in" => JsonNode.Parse("""{"from": {"scale": 1}, "to": {"scale": 1.25}}"""),
                    "out" or "pull out" or "zoom out" => JsonNode.Parse("""{"from": {"scale": 1.25}, "to": {"scale": 1}}"""),
                    "left" or "pan left" => JsonNode.Parse("""{"from": {"scale": 1.2, "x": 0.6}, "to": {"scale": 1.2, "x": 0.4}}"""),
                    "right" or "pan right" => JsonNode.Parse("""{"from": {"scale": 1.2, "x": 0.4}, "to": {"scale": 1.2, "x": 0.6}}"""),
                    _ => null,
                };
        }
        if (root["sound"] is JsonObject old && old["voice"] is JsonValue voice && root["voice"] is null)
            root["voice"] = voice.DeepClone();
        root.Remove("sound");

        static void Text(JsonObject o, string key)
        {
            if (o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
                o[key] = v.ToJsonString();
        }
    }

    /// <summary>Two parts of a prompt as one: a full stop between them unless the first ends a sentence.</summary>
    public static string JoinText(string a, string b)
    {
        a = a.Trim();
        b = b.Trim();
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a.EndsWith('.') || a.EndsWith('!') || a.EndsWith('?') ? $"{a} {b}" : $"{a}. {b}";
    }

    private static string? Clean(string? s) => s?.Trim() is { Length: > 0 } t ? t : null;

    public string ToJson() => JsonSerializer.Serialize(this, Readable.Storyboard);

    /// <summary>Written as people read it: "fox's", not "fox's" (the user may open the file).</summary>
    private static readonly StoryboardJson Readable = new(new JsonSerializerOptions(StoryboardJson.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public JsonObject ToJsonObject() => (JsonObject)JsonNode.Parse(ToJson())!;

    /// <summary>
    /// Ids filled in and made unique, seeds given, numbers kept in range: a storyboard written by
    /// hand or by a model can leave any of them out.
    /// </summary>
    public void Normalize()
    {
        Frames ??= [];
        Shots ??= [];
        Sounds ??= [];
        Voice ??= "";
        Width = Even(Math.Clamp(Width <= 0 ? 1280 : Width, 64, 4096));
        Height = Even(Math.Clamp(Height <= 0 ? 720 : Height, 64, 4096));
        Fps = Math.Clamp(Fps <= 0 ? 24 : Fps, 1, 120);
        // The ids given first, shots before frames, and new ones after them: numbered in list order,
        // a frame without one took the id a later frame in the same change was given (and a shot's
        // first frame named by it was the wrong one), and a frame given a shot's id renamed that shot
        // and every one after it, their clips then matched to the wrong ids.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (item, id) in Shots.Select(s => ((object)s, s.Id)).Concat(Frames.Select(f => ((object)f, f.Id))).Concat(Sounds.Select(a => ((object)a, a.Id))))
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim()))
                renamed.Add(item);
        foreach (var f in Frames)
        {
            f.From ??= [];
            if (renamed.Contains(f))
                f.Id = NextFree("f", seen);
            f.Id = f.Id.Trim();
            if (f.Seed <= 0)
                f.Seed = NewSeed();
            f.Prompt ??= "";
            f.Model = Clean(f.Model);
        }
        foreach (var s in Shots)
        {
            if (renamed.Contains(s))
                s.Id = NextFree("s", seen);
            s.Id = s.Id.Trim();
            if (s.Seed <= 0)
                s.Seed = NewSeed();
            s.Seconds = s.Seconds <= 0 ? 5 : Math.Clamp(s.Seconds, 0.5, 60);
            s.Prompt ??= "";
            s.Camera ??= "";
            s.Line ??= "";
            s.Model = Clean(s.Model);
            s.Motion = Clean(s.Motion);
            // A line is said lip-synced: a shot is lip-synced when it has one, and only then.
            s.LipSync = s.Line.Trim().Length > 0;
            s.Transition = TransitionNamed(s.Transition);
            s.TransitionSeconds = Math.Clamp(s.TransitionSeconds, 0, 10);
            s.File = Clean(s.File);
            s.Scene = Clean(s.Scene);
            s.In = Math.Clamp(s.In, 0, 36000);
            s.Speed = s.Speed is { } speed && Math.Abs(speed - 1) > 0.001 ? Math.Round(Math.Clamp(speed, 0.1, 10), 3) : null;
            s.Freeze = s.Freeze is >= 0 and var freeze && freeze < s.Seconds ? freeze : null;
            s.Volume = s.Volume is { } volume && Math.Abs(volume - 1) > 0.001 ? Math.Round(Math.Clamp(volume, 0, 4), 3) : null;
            if (s.Crop is { } crop)
            {
                crop.X = Math.Clamp(crop.X, 0, 0.95);
                crop.Y = Math.Clamp(crop.Y, 0, 0.95);
                crop.Width = Math.Clamp(crop.Width <= 0 ? 1 : crop.Width, 0.05, 1 - crop.X);
                crop.Height = Math.Clamp(crop.Height <= 0 ? 1 : crop.Height, 0.05, 1 - crop.Y);
                if (crop is { X: 0, Y: 0, Width: 1, Height: 1 })
                    s.Crop = null;
            }
            if (s.Zoom is { } zoom)
            {
                zoom.From ??= new VideoView();
                zoom.To ??= new VideoView();
                foreach (var view in new[] { zoom.From, zoom.To })
                {
                    view.Scale = Math.Clamp(view.Scale <= 0 ? 1 : view.Scale, 1, 8);
                    view.X = Math.Clamp(view.X, 0, 1);
                    view.Y = Math.Clamp(view.Y, 0, 1);
                }
                if (zoom.From.Scale == 1 && zoom.To.Scale == 1)
                    s.Zoom = null;
            }
            if (s.Color is { } color)
            {
                color.Brightness = Math.Clamp(color.Brightness, -1, 1);
                color.Contrast = Math.Clamp(color.Contrast, 0, 2);
                color.Saturation = Math.Clamp(color.Saturation, 0, 3);
                color.Look = color.Look?.Trim().ToLowerInvariant() switch
                {
                    "bw" or "b&w" or "black and white" or "black & white" or "mono" or "monochrome" or "grayscale" or "greyscale" => "bw",
                    "sepia" or "old" or "old photo" => "sepia",
                    "warm" or "warmer" or "golden" => "warm",
                    "cool" or "cold" or "cooler" or "blue" => "cool",
                    "vintage" or "retro" or "film" => "vintage",
                    _ => "",
                };
                if (color is { Brightness: 0, Contrast: 1, Saturation: 1, Look: "" })
                    s.Color = null;
            }
            if (Frame(s.First) is null) s.First = null;
            if (Frame(s.Last) is null) s.Last = null;
        }
        // A frame is where a shot's clip ends only for a shot there is, and not one of that shot's own
        // frames; or where a video of the user's ends, named by its file ("walk.mp4").
        foreach (var f in Frames)
        {
            f.Of = Clean(f.Of);
            if (f.Of is { } of && (Shot(of) is { } shot ? shot.First == f.Id || shot.Last == f.Id : !IsVideoFile(of) && ReferenceVideo(of) is null))
                f.Of = null;
        }
        foreach (var a in Sounds)
        {
            if (renamed.Contains(a))
                a.Id = NextFree("a", seen);
            a.Id = a.Id.Trim();
            a.Source = a.Source?.Trim().ToLowerInvariant() switch
            {
                VideoSound.FromFile or "audio" or "user" => VideoSound.FromFile,
                VideoSound.Foley or "sfx" or "sound effects" or "effects" or "video to audio" => VideoSound.Foley,
                _ => VideoSound.Song,
            };
            if (a.Source != VideoSound.FromFile && a.Seed <= 0)
                a.Seed = NewSeed();
            a.Tags ??= "";
            a.Lyrics ??= "";
            a.Model = Clean(a.Model);
            a.Name = Clean(a.Name);
            a.Start = Math.Clamp(a.Start, 0, 3600);
            a.Trim = Math.Clamp(a.Trim, 0, 3600);
            a.Seconds = Math.Clamp(a.Seconds, 0, 3600);
            a.Volume = Math.Clamp(a.Volume, 0, 2);
            a.FadeIn = Math.Clamp(a.FadeIn, 0, 60);
            a.FadeOut = Math.Clamp(a.FadeOut, 0, 60);
            a.SoundLine = Math.Clamp(a.SoundLine, 0, MaxSoundLines - 1);
        }
        Overlays ??= [];
        foreach (var o in Overlays)
        {
            if (string.IsNullOrWhiteSpace(o.Id) || !seen.Add(o.Id.Trim()))
                o.Id = NextFree("o", seen);
            o.Id = o.Id.Trim();
            o.Text ??= "";
            o.File = Clean(o.File);
            o.Scene = Clean(o.Scene);
            o.Name = Clean(o.Name);
            o.Position = (o.Position ?? "").Trim().ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');
            o.Start = Math.Clamp(o.Start, 0, 36000);
            o.Seconds = Math.Clamp(o.Seconds, 0, 36000);
            o.Opacity = o.Opacity is { } opacity && opacity < 0.999 ? Math.Clamp(opacity, 0, 1) : null;
            o.FadeIn = o.FadeIn is { } fi ? Math.Clamp(fi, 0, 10) : null;
            o.FadeOut = o.FadeOut is { } fo ? Math.Clamp(fo, 0, 10) : null;
            o.Size = o.Size is { } size ? Math.Clamp(size > 1 ? size / Height : size, 0.01, 0.5) : null;
            o.Font = Clean(o.Font);
            o.Color = Clean(o.Color);
            o.Background = Clean(o.Background);
            if (o.Box is { } box)
            {
                box.X = Math.Clamp(box.X, 0, 0.99);
                box.Y = Math.Clamp(box.Y, 0, 0.99);
                box.Width = Math.Clamp(box.Width <= 0 ? 1 : box.Width, 0.01, 1);
                box.Height = Math.Clamp(box.Height <= 0 ? 1 : box.Height, 0.01, 1);
            }
        }
        FadeIn = FadeIn is { } fadeIn ? Math.Clamp(fadeIn, 0, 10) : null;
        FadeOut = FadeOut is { } fadeOut ? Math.Clamp(fadeOut, 0, 10) : null;
        Quality = (Quality ?? "").Trim().ToLowerInvariant() switch
        {
            "draft" or "fast" or "quick" or "preview" or "low" => "draft",
            "best" or "high" or "highest" or "max" or "final" => "best",
            _ => "",
        };
        SoundLines = Math.Clamp(Math.Max(SoundLines, Sounds.Select(a => a.SoundLine + 1).DefaultIfEmpty(1).Max()), 1, MaxSoundLines);
        SoundLineVolumes = [.. Enumerable.Range(0, SoundLines).Select(i => i < (SoundLineVolumes?.Count ?? 0) ? Math.Round(Math.Clamp(SoundLineVolumes![i], 0, 2), 2) : 1)];
        if (VoiceSeed <= 0)
            VoiceSeed = NewSeed();

        static int Even(int n) => n / 2 * 2;

        static string NextFree(string prefix, HashSet<string> seen)
        {
            for (var n = 1; ; n++)
                if (seen.Add(prefix + n))
                    return prefix + n;
        }
    }

    /// <summary>
    /// A new video in <paramref name="folder"/> (made if needed), with one shot of two frames to
    /// start from. A video already there is loaded instead.
    /// </summary>
    public static Storyboard Create(string folder, string title)
    {
        var board = New(folder, title);
        if (!IsVideo(board.Folder))
            board.Save();
        return board;
    }

    /// <summary>
    /// A new video for <paramref name="folder"/>, not saved yet: one shot of two frames to start
    /// from. A video already there is loaded instead.
    /// </summary>
    public static Storyboard New(string folder, string title)
    {
        folder = Path.GetFullPath(folder);
        if (IsVideo(folder))
            return Load(folder);
        var board = new Storyboard { Title = title, Folder = folder };
        board.Frames.Add(new VideoFrame { Id = "f1", Seed = NewSeed() });
        board.Frames.Add(new VideoFrame { Id = "f2", Seed = NewSeed() });
        board.Shots.Add(new VideoShot { Id = "s1", First = "f1", Last = "f2", Seconds = 5, Seed = NewSeed() });
        board.Normalize();
        return board;
    }

    /// <summary>
    /// Merges a change into the storyboard, as the agent writes one: fields given replace those
    /// there, frames and shots are matched by id (a new id adds one, "delete": true takes one out),
    /// and anything not given stays as it is.
    /// </summary>
    public static Storyboard Merge(Storyboard board, JsonObject change)
    {
        change = Spread(change);
        var json = board.ToJsonObject();
        foreach (var (key, value) in change)
        {
            var name = JsonKey(key);
            if (name is "frames" or "shots" or "sounds" or "overlays" && value is JsonArray items)
                json[name] = MergeList(json[name] as JsonArray ?? [], items);
            // The lines' voice, as a model may still give it.
            else if (name is "sound")
            {
                if (value is JsonObject inner && inner.FirstOrDefault(p => JsonKey(p.Key) == "voice").Value is { } voice)
                    json["voice"] = voice.DeepClone();
            }
            else
                json[name] = value?.DeepClone();
        }
        // A frame given "of" is where that shot's clip ends, taken from it: the shot stops ending on
        // it as its last frame, unless the change says it does. Kept, the frame lost its "of" instead,
        // and a model that had left "last" out of the shot to take it away edited the file by hand.
        foreach (var frame in Given(change, "frames"))
            if (Text(Field(frame, "of")) is { } of && Text(Field(frame, "id")) is { } id
                && (json["shots"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => Same(s["id"], of)) is { } shot && Same(shot["last"], id)
                && !Given(change, "shots").Any(s => Same(Field(s, "id"), of) && Field(s, "last") is not null))
                shot.Remove("last");
        var merged = Parse(json.ToJsonString());
        merged.Folder = board.Folder;
        return merged;

        // A video-wide look or templates in a change go to the frames and shots that change gives,
        // never to those there before: one setting in every prompt remade everything when it changed.
        static JsonObject Spread(JsonObject change)
        {
            var keys = change.Select(p => p.Key).Where(k => JsonKey(k) is "style" or "story" or "workflows").ToList();
            if (keys.Count == 0)
                return change;
            change = (JsonObject)change.DeepClone();
            var style = keys.Where(k => JsonKey(k) == "style").Select(k => change[k]).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault();
            var workflows = keys.Where(k => JsonKey(k) == "workflows").Select(k => change[k]).OfType<JsonObject>().FirstOrDefault();
            foreach (var key in keys)
                change.Remove(key);
            string? Job(JsonObject? w, string job) =>
                w?.FirstOrDefault(p => p.Key.Equals(job, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            foreach (var (kind, item) in Items(change))
            {
                if (style is { Length: > 0 } && item["prompt"] is JsonValue p && p.TryGetValue<string>(out var prompt))
                    item["prompt"] = JoinText(prompt, style);
                if (workflows is not null && item["model"] is null)
                {
                    var job = kind == "frames"
                        ? item["from"] is JsonArray { Count: > 0 } ? "edit" : "frame"
                        : item["lipSync"] is JsonValue ls && ls.TryGetValue<bool>(out var lip) && lip ||
                          item["line"] is JsonValue lv && lv.TryGetValue<string>(out var line) && line.Trim().Length > 0 ? "lipSync"
                        : item["first"] is null ? "shotFromText" : item["last"] is null ? "shotFromFirst" : "shot";
                    if (Job(workflows, job) is { Length: > 0 } model)
                        item["model"] = model;
                }
            }
            return change;

            static IEnumerable<(string Kind, JsonObject Item)> Items(JsonObject change) =>
                change.Where(p => JsonKey(p.Key) is "frames" or "shots").SelectMany(p => (p.Value as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonKey(p.Key), o)));
        }

        static JsonArray MergeList(JsonArray existing, JsonArray changes)
        {
            var list = existing.Select(n => n?.DeepClone()).OfType<JsonObject>().ToList();
            foreach (var item in changes.OfType<JsonObject>())
            {
                var id = (string?)item["id"];
                var at = id is null ? -1 : list.FindIndex(o => string.Equals((string?)o["id"], id, StringComparison.OrdinalIgnoreCase));
                if (item["delete"] is JsonValue d && d.TryGetValue<bool>(out var delete) && delete)
                {
                    if (at >= 0)
                        list.RemoveAt(at);
                    continue;
                }
                if (at < 0)
                {
                    var added = new JsonObject();
                    foreach (var (k, v) in item)
                        added[JsonKey(k)] = v?.DeepClone();
                    list.Add(added);
                    continue;
                }
                foreach (var (k, v) in item)
                    list[at][JsonKey(k)] = v?.DeepClone();
            }
            return new JsonArray(list.Cast<JsonNode>().ToArray());
        }

        static IEnumerable<JsonObject> Given(JsonObject change, string list) =>
            change.Where(p => JsonKey(p.Key) == list).SelectMany(p => (p.Value as JsonArray ?? []).OfType<JsonObject>());

        static JsonNode? Field(JsonObject item, string key) => item.FirstOrDefault(p => JsonKey(p.Key) == key).Value;

        static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

        static bool Same(JsonNode? node, string id) => string.Equals(Text(node), id, StringComparison.OrdinalIgnoreCase);

        // Written as the file has them (camelCase), whatever case the change used.
        static string JsonKey(string key) => key.Length == 0 ? key : char.ToLowerInvariant(key[0]) + key[1..];
    }
}

public sealed class VideoFrame
{
    public string Id { get; set; } = "";

    /// <summary>
    /// What a reference is called ("Mira", "the lighthouse"), its picture in References named after
    /// it ("Mira.png"). Frames in shots may have one too.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>A character, place or thing other frames are made from, not a frame of a shot.</summary>
    public bool Reference { get; set; }

    /// <summary>What the picture shows, and how it looks.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// What it is made with: a model or workflow in the user's words ("furry nova xl", "my pony
    /// workflow"), matched against what ComfyUI has installed, or a template's name; none: the best
    /// installed one (<see cref="VideoTemplates.PickAsync"/>).
    /// </summary>
    public string? Model { get; set; }

    public long Seed { get; set; }

    /// <summary>Frames this one is made from, so the same people and places come out the same: an edit model takes them.</summary>
    public List<string> From { get; set; } = [];

    /// <summary>A picture of the user's own to use instead of making one.</summary>
    public string? File { get; set; }

    /// <summary>
    /// The shot whose clip this frame is the last picture of, taken from the clip: the shot that
    /// starts on it goes on from the moment that one ends. A picture model can't make that frame:
    /// asked to move a girl to a mailbox, an edit model left her where she stood.
    /// </summary>
    /// <remarks>Or a video in References ("walk.mp4"): the frame is where that video ends, so a shot goes on from the user's clip.</remarks>
    public string? Of { get; set; }

    /// <summary>Changes to the workflow after the storyboard's own, as the comfyui tool's set takes them: {"node id.input": value}.</summary>
    public JsonObject? Set { get; set; }

    /// <summary>Where its picture is in the video's folder ("Generated/f1.png"): where its .json was found, or where it is given one.</summary>
    public string? Place { get; set; }

    /// <summary>"Frame 3", or its name.</summary>
    public string Label => Name is { Length: > 0 } name ? name : Id;
}

public sealed class VideoShot
{
    public const string Cut = "cut", Dissolve = "dissolve", Fade = "fade";

    public string Id { get; set; } = "";

    /// <summary>A few words naming it ("intro", "the leap").</summary>
    public string? Name { get; set; }

    /// <summary>Where its clip is in the video's folder ("Generated/s1.mp4"): where its .json was found, or where it is given one.</summary>
    public string? Place { get; set; }

    /// <summary>The frame it starts on; the last frame of the shot before when the camera goes on from there.</summary>
    public string? First { get; set; }

    /// <summary>The frame it ends on, or none: then it goes where its prompt takes it.</summary>
    public string? Last { get; set; }

    public double Seconds { get; set; } = 5;

    /// <summary>What happens in it.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>What the camera does ("slow push in", "pans left", "still").</summary>
    public string Camera { get; set; } = "";

    /// <summary>What it is made with, as <see cref="VideoFrame.Model"/>: a video model or workflow.</summary>
    public string? Model { get; set; }

    public long Seed { get; set; }

    /// <summary>Lip-synced to its line: set by whether it has one.</summary>
    public bool LipSync { get; set; }

    /// <summary>
    /// A video in References ("dance.mp4") whose movement it copies: made with a template that takes
    /// a video to follow (LTX's IC-LoRA control, Wan Animate), from its first frame.
    /// </summary>
    public string? Motion { get; set; }

    /// <summary>What is said in it, in the video's voice (<see cref="Storyboard.Voice"/>), lip-synced.</summary>
    public string Line { get; set; } = "";

    /// <summary>How it follows the shot before: cut, dissolve or fade (through black).</summary>
    public string Transition { get; set; } = Cut;

    /// <summary>Changes to the workflow after the storyboard's own, as the comfyui tool's set takes them.</summary>
    public JsonObject? Set { get; set; }

    // ---- what it is made of, when no model makes it -----------------------------------------------

    /// <summary>A video or picture of the user's (in References, or by its full path, copied in) that is the clip as it is: no model makes it.</summary>
    public string? File { get; set; }

    /// <summary>An SVG (moving with SMIL) or a Three.js page (in References, or by its full path) drawn into the clip.</summary>
    public string? Scene { get; set; }

    // ---- editing: how its clip is cut and shown in the video, all done as the video is joined ------

    /// <summary>Seconds cut off the start of its clip.</summary>
    public double In { get; set; }

    /// <summary>How fast its clip plays (0.5 is half speed, 2 twice); null as it is.</summary>
    public double? Speed { get; set; }

    /// <summary>Its clip played backwards.</summary>
    public bool Reverse { get; set; }

    /// <summary>Seconds into the shot where its picture stops and holds to the end (a freeze frame); null: it doesn't.</summary>
    public double? Freeze { get; set; }

    /// <summary>How loud its clip's own sound is, 1 as it is and 0 silent; null as it is.</summary>
    public double? Volume { get; set; }

    /// <summary>The part of its picture kept, the rest cut away; null all of it.</summary>
    public VideoBox? Crop { get; set; }

    /// <summary>A slow zoom or pan over the shot (Ken Burns), from one view to another; null none.</summary>
    public VideoZoom? Zoom { get; set; }

    /// <summary>Its colours: brightness, contrast, saturation and a look; null as they are.</summary>
    public VideoColor? Color { get; set; }

    /// <summary>How long the transition into it lasts, in seconds; 0 its usual (a dissolve 0.5, a fade 1, the others 0.8).</summary>
    public double TransitionSeconds { get; set; }

    /// <summary>Whether no model makes its clip: it is a file of the user's or a scene drawn.</summary>
    [JsonIgnore]
    public bool Assembled => File is { Length: > 0 } || Scene is { Length: > 0 };
}

/// <summary>A part of a picture, in fractions of it: 0,0 is its top left, 1,1 its bottom right.</summary>
public sealed class VideoBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
}

/// <summary>A slow zoom or pan over a shot: the view at its start and at its end.</summary>
public sealed class VideoZoom
{
    public VideoView From { get; set; } = new();
    public VideoView To { get; set; } = new();
}

/// <summary>A view of a picture: how far in (1 the whole picture, 2 half of it) and the point it is centred on, in fractions.</summary>
public sealed class VideoView
{
    public double Scale { get; set; } = 1;
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
}

/// <summary>A shot's colours.</summary>
public sealed class VideoColor
{
    /// <summary>-1 to 1, 0 as it is.</summary>
    public double Brightness { get; set; }

    /// <summary>0 to 2, 1 as it is.</summary>
    public double Contrast { get; set; } = 1;

    /// <summary>0 (grey) to 3, 1 as it is.</summary>
    public double Saturation { get; set; } = 1;

    /// <summary>A look over it: bw, sepia, warm, cool, vintage, or none.</summary>
    public string Look { get; set; } = "";
}

/// <summary>
/// Something shown over the video for a while: a title, a caption or any words (drawn by Sloperator),
/// a picture or a video of the user's (a logo, picture in picture), or a scene drawn (an SVG title
/// moving with SMIL, a Three.js logo), where the video's frame puts it.
/// </summary>
public sealed class VideoOverlay
{
    public string Id { get; set; } = "";

    /// <summary>What it is called in the panel.</summary>
    public string? Name { get; set; }

    /// <summary>Words to show (a title, a caption); lines split at \n.</summary>
    public string Text { get; set; } = "";

    /// <summary>A picture or video (in References, or by its full path) to show.</summary>
    public string? File { get; set; }

    /// <summary>An SVG or Three.js page (in References, or by its full path) drawn over the video, with a clear background.</summary>
    public string? Scene { get; set; }

    /// <summary>Where it starts in the video, in seconds.</summary>
    public double Start { get; set; }

    /// <summary>How long it shows; 0 to the video's end.</summary>
    public double Seconds { get; set; }

    /// <summary>Where on the frame: center, top, bottom, lower third, top left, top right, bottom left, bottom right or full; <see cref="Box"/> instead for a place of its own.</summary>
    public string Position { get; set; } = "";

    /// <summary>Its place on the frame, in fractions; null: by <see cref="Position"/>.</summary>
    public VideoBox? Box { get; set; }

    /// <summary>0 to 1; null fully there.</summary>
    public double? Opacity { get; set; }

    /// <summary>Seconds it fades in and out over; null: words 0.3, the rest none.</summary>
    public double? FadeIn { get; set; }

    public double? FadeOut { get; set; }

    /// <summary>The words' font (a font on the PC: Segoe UI, Arial, Georgia, Impact…); null Segoe UI.</summary>
    public string? Font { get; set; }

    /// <summary>The words' height as a fraction of the frame's; null by its place (a title bigger than a caption).</summary>
    public double? Size { get; set; }

    /// <summary>The words' colour (#RRGGBB); null white.</summary>
    public string? Color { get; set; }

    /// <summary>A box behind the words (#RRGGBB or #RRGGBBAA); null none, with a shadow under the words instead.</summary>
    public string? Background { get; set; }

    /// <summary>Bold words; null bold.</summary>
    public bool? Bold { get; set; }

    /// <summary>"Title 1", its name or its words.</summary>
    [JsonIgnore]
    public string Label => Name is { Length: > 0 } name ? name : Text.Trim() is { Length: > 0 } text ? text.Split('\n')[0].Clip(30) : Id;
}

/// <summary>
/// A sound laid over the video, as a clip on a video editor's sound line: a sound file of the
/// user's (copied into Sounds), a song Sloperator makes, or foley Sloperator makes from the clips under
/// it, from where it starts in the video, trimmed at either end, at its volume.
/// </summary>
public sealed class VideoSound
{
    public const string FromFile = "file", Song = "song", Foley = "foley";

    public string Id { get; set; } = "";

    /// <summary>What it is called; a song's file in Sounds is named after it.</summary>
    public string? Name { get; set; }

    /// <summary>file (the user's), song (made with a music model) or foley (made from the clips under it with a video-to-audio model).</summary>
    public string Source { get; set; } = Song;

    /// <summary>A sound of the user's from elsewhere, by its full path: copied into Sounds as it is when the video is saved.</summary>
    public string? File { get; set; }

    /// <summary>The sound line it is on, from 0.</summary>
    public int SoundLine { get; set; }

    /// <summary>Where it starts in the video, in seconds.</summary>
    public double Start { get; set; }

    /// <summary>How much of its start is cut off, in seconds.</summary>
    public double Trim { get; set; }

    /// <summary>How long it plays, in seconds; 0: to its end.</summary>
    public double Seconds { get; set; }

    /// <summary>How loud, 1 as it is.</summary>
    public double Volume { get; set; } = 1;

    /// <summary>Seconds it fades in over from where it starts, and out over before it stops.</summary>
    public double FadeIn { get; set; }

    public double FadeOut { get; set; }

    /// <summary>A song's style: genre, instruments, voice, tempo ("dreamy synthwave, female vocals, 90 BPM"); foley's, what is heard ("footsteps on gravel, wind"), which it can do without.</summary>
    public string Tags { get; set; } = "";

    /// <summary>A song's words; none makes an instrumental.</summary>
    public string Lyrics { get; set; } = "";

    /// <summary>What a song or foley is made with, as <see cref="VideoFrame.Model"/>: a music or video-to-audio model or workflow.</summary>
    public string? Model { get; set; }

    public long Seed { get; set; }

    public JsonObject? Set { get; set; }

    /// <summary>Where its file is in the video's folder ("Sounds/rain.mp3"): where its .json was found, or where it is given one.</summary>
    public string? Place { get; set; }

    /// <summary>"rain", or its id.</summary>
    public string Label => Name is { Length: > 0 } name ? name : Id;
}

/// <summary>Something in a video that can't be done as asked, said so the user or the model can fix it.</summary>
public sealed class VideoException(string message) : Exception(message);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, IgnoreReadOnlyProperties = true, AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(Storyboard))]
[JsonSerializable(typeof(VideoFrame))]
[JsonSerializable(typeof(VideoShot))]
[JsonSerializable(typeof(VideoSound))]
internal sealed partial class StoryboardJson : JsonSerializerContext;
