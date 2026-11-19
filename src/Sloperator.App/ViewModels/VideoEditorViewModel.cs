using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sloperator.Core.Video;
using Sloperator.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sloperator.App.ViewModels;

/// <summary>
/// Video editor, one of the chat window's side panels, in a panel on the left of the chat: a viewer,
/// and the video on one timeline under one horizontal scroll, where all work on it is done (the
/// user's call, 2026-10-24: "work on the film should be done entirely in the timeline"): the
/// References row (image files and generated images), the clips on the Video clips row, the lines, and the
/// sound lines. What is picked on it goes into the chat's box, where the user says what to do with
/// it: the agent writes the prompts, picks the models and makes each piece with the video tool in the
/// chat, in view. The video is its folder (References and Generated, each file with its .json), and
/// what the agent changes and makes there shows here as it happens.
/// </summary>
public sealed partial class VideoEditorViewModel : ObservableObject, ISidePanel
{
    private readonly PreviewServices _previews;
    private readonly MainWindowViewModel _chat;
    private Storyboard _board = new();
    private readonly Dictionary<string, FrameItem> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chatTimer;
    private FileSystemWatcher? _watcher;
    private bool _loading;

    /// <summary>Where videos go, a folder each: Sloperator's SloperatorData\videos, next to the exe like the rest of its data.</summary>
    private readonly string _videosRoot;

    /// <param name="chat">The chat the panel is open in, where what is said about the video runs.</param>
    /// <param name="videosRoot">Where videos go (Sloperator's SloperatorData\videos).</param>
    /// <param name="folder">The video to open; the latest changed one when null.</param>
    public VideoEditorViewModel(PreviewServices previews, MainWindowViewModel chat, string videosRoot, string? folder)
    {
        _videosRoot = videosRoot;
        _previews = previews;
        _chat = chat;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow());
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            OnFolderChanged();
        });
        // What the chat is doing, shown here: on the piece the video tool is making.
        _chatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => WatchChat());
        _chatTimer.Start();
        var open = folder ?? Storyboard.Recent(_videosRoot).FirstOrDefault();
        if (open is not null && Storyboard.IsVideo(open))
            Open(open);
        else
            Untitled();
        Watch();
    }

    // ---- the video --------------------------------------------------------------------------------------

    public string Folder => _board.Folder;

    /// <summary>The video's name, on the button at the top that opens the videos' menu.</summary>
    public string VideoName => Path.GetFileName(Path.TrimEndingDirectorySeparator(_board.Folder));

    /// <summary>Opens the video in a folder (or of a file in it).</summary>
    public void Open(string path)
    {
        SaveNow();
        StopPlaying(); // the video left: its sounds played on over the new one's frames
        try
        {
            _board = Storyboard.Load(path);
            _savedJson = _board.ToJson();
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VideoException)
        {
            ShowError($"That video can't be opened: {ex.Message}", ex, path);
            return;
        }
        Opened();
    }

    /// <summary>
    /// A new video in Sloperator's SloperatorData\videos, its name typed as a new folder's is in Explorer. Its folder
    /// is made with the first change: opening the panel to look leaves nothing behind. The user says
    /// in the chat what it is to be.
    /// </summary>
    [RelayCommand]
    private void NewVideo()
    {
        Untitled();
        RenameVideoAsked?.Invoke();
    }

    /// <summary>The video's name is to be typed anew (a new video, or Rename in its menu): the panel shows the box.</summary>
    public event Action? RenameVideoAsked;

    /// <summary>A new video, named "Untitled video" (or "Untitled video 2"), not saved yet. Not "New
    /// video": that is the menu's item that makes it (the user, 2026-10-25).</summary>
    private void Untitled()
    {
        SaveNow();
        StopPlaying();
        var root = _videosRoot;
        var name = "Untitled video";
        for (var n = 2; Directory.Exists(Path.Combine(root, name)); n++)
            name = $"Untitled video {n}";
        _board = Storyboard.New(Path.Combine(root, name), name);
        _savedJson = null;
        _dirty = false;
        Opened();
    }

    /// <summary>Another video is shown: what was picked of the one before goes from the chat's box.</summary>
    private void Opened()
    {
        // The edits of the video before aren't taken back here: Ctrl+Z wrote its storyboard into this video's folder.
        ForgetEdits();
        Scrubbed = false;
        ResetPreview();
        _soundMix = null;
        // What a Sloperator that ended suddenly left set aside in this video's folder.
        ClearAsideFolder(Path.Combine(_board.Folder, AsideName));
        Status = "";
        Rebuild();
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(VideoName));
        _chat.Pick([]);
        Clear();
    }

    public decimal? FrameWidth
    {
        get => _board.Width;
        set
        {
            if (value is not { } v) return;
            _board.Width = Math.Clamp((int)v / 2 * 2, 64, 4096);
            OnPropertyChanged();
            Changed();
        }
    }

    public decimal? FrameHeight
    {
        get => _board.Height;
        set
        {
            if (value is not { } v) return;
            _board.Height = Math.Clamp((int)v / 2 * 2, 64, 4096);
            OnPropertyChanged();
            Changed();
        }
    }

    public decimal? Fps
    {
        get => _board.Fps;
        set
        {
            if (value is not { } v) return;
            _board.Fps = Math.Clamp((int)v, 1, 120);
            OnPropertyChanged();
            Changed();
        }
    }

    /// <summary>How long the video is, and in how many clips (its size and frame rate are at the top).</summary>
    public string LengthText
    {
        get
        {
            var (_, length) = _board.Timing();
            var made = _board.Shots.Count(s => VideoMaker.IsCurrent(_board, s));
            return $"{length:0.#} s in {_board.Shots.Count} clip{(_board.Shots.Count == 1 ? "" : "s")}" +
                   (_board.Shots.Count == 0 ? "" : made < _board.Shots.Count ? $" · {made} made" : _board.JoinedIsStale ? " · all made, not joined since" : " · all made");
        }
    }

    // ---- the timeline ------------------------------------------------------------------------------------

    /// <summary>Where the video's time starts on each row, in pixels.</summary>
    public const double Lead = 12;

    /// <summary>A card on the References row, and the room between two.</summary>
    public const double CardWidth = 96, CardGap = 6;

    /// <summary>How wide the column of how the piece shown was made is, right of the viewer: the viewer is short of height, not width.</summary>
    public const double DetailsWidth = 280;

    /// <summary>How tall a sound line is.</summary>
    public const double SoundLineHeight = 48;

    /// <summary>How many pixels a second takes on the timeline: the zoom.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText), nameof(ZoomInTip), nameof(ZoomOutTip), nameof(ZoomResetTip))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(ZoomResetCommand))]
    private double _pps = DefaultPps;

    partial void OnPpsChanged(double value) => Relayout();

    public ObservableCollection<ReferenceCard> References { get; } = [];
    public ObservableCollection<ShotItem> Shots { get; } = [];
    public ObservableCollection<SoundItem> Sounds { get; } = [];
    public ObservableCollection<SoundLineItem> SoundLines { get; } = [];

    /// <summary>How tall the sound lines are together.</summary>
    public double SoundLinesHeight => Math.Max(1, SoundLines.Count) * SoundLineHeight;

    /// <summary>How wide the rows are: the video, the sounds and cards past it, and room to make the last clip longer.</summary>
    [ObservableProperty]
    private double _timelineWidth = 800;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndX), nameof(HasLength))]
    private double _length;

    /// <summary>Where the video ends on the timeline: a light grey line down every row, its length on the ruler.</summary>
    public double EndX => Lead + Length * Pps;

    public bool HasLength => Length > 0;

    /// <summary>Where the next clip's place is on the Video clips row, after the last: a click there adds it.</summary>
    [ObservableProperty]
    private double _addShotX = Lead;

    /// <summary>Where the next card's place is on the References row: a click there makes one.</summary>
    [ObservableProperty]
    private double _addReferenceX = Lead;

    public double PlayheadX => Lead + Math.Max(0, PlayPosition) * Pps;

    /// <summary>Lays the timeline out again: each card, clip and sound where it is, the width.</summary>
    public void Relayout()
    {
        var (starts, length) = _board.Timing();
        Length = length;
        for (var i = 0; i < References.Count; i++)
            References[i].X = Lead + i * (CardWidth + CardGap);
        AddReferenceX = Lead + References.Count * (CardWidth + CardGap);
        for (var i = 0; i < Shots.Count && i < starts.Count; i++)
        {
            Shots[i].X = Lead + starts[i] * Pps;
            Shots[i].BlockWidth = Math.Max(8, Shots[i].Shot.Seconds * Pps - 2);
        }
        var end = length;
        foreach (var sound in Sounds)
        {
            var seconds = sound.PlaySeconds(length);
            sound.X = Lead + sound.Sound.Start * Pps;
            sound.Y = sound.Sound.SoundLine * SoundLineHeight + 4;
            sound.BlockWidth = Math.Max(8, seconds * Pps - 2);
            end = Math.Max(end, sound.Sound.Start + seconds);
            sound.Peaks = PeaksOf(sound);
            sound.Beats = BeatsOf(sound);
        }
        // Each sound line's +: after its last sound.
        foreach (var line in SoundLines)
        {
            line.AddSeconds = Math.Round(Sounds.Where(s => s.Sound.SoundLine == line.Index).Select(s => s.Sound.Start + s.PlaySeconds(length)).DefaultIfEmpty(0).Max() * 10) / 10;
            line.AddX = Lead + line.AddSeconds * Pps + 4;
            end = Math.Max(end, line.AddSeconds + 56 / Pps);
        }
        AddShotX = Lead + length * Pps + 4;
        LayoutOverlays(length);
        TimelineWidth = Math.Max(Lead + end * Pps, AddReferenceX + CardWidth) + 240;
        OnPropertyChanged(nameof(PlayheadX));
        OnPropertyChanged(nameof(EndX));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(SoundLinesHeight));
        // What the bars' buttons can do follows the clips and sound lines: greyed, never hidden.
        MakeVideoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(MakeVideoTip));
        OnPropertyChanged(nameof(CanAddSoundLine));
        OnPropertyChanged(nameof(AddSoundLineTip));
    }

    /// <summary>The usual zoom (100% on the button between − and +), and how far it goes either way.</summary>
    public const double DefaultPps = 64, MinPps = 12, MaxPps = 240;

    /// <summary>How close the timeline is against its usual zoom, as the Code editor's zoom says it.</summary>
    public string ZoomText => $"{Pps / DefaultPps * 100:0}%";

    /// <summary>The zoom's widest text (19% to 375%, its figures all as wide): the button between − and + is as wide at every zoom.</summary>
    public static IReadOnlyList<string> ZoomSlot { get; } = ["100%"];

    private bool CanZoomIn() => Pps < MaxPps;

    private bool CanZoomOut() => Pps > MinPps;

    private bool CanZoomReset() => Pps != DefaultPps;

    public string ZoomInTip => CanZoomIn() ? "See it closer (Ctrl + wheel)" : "As close as it goes";

    public string ZoomOutTip => CanZoomOut() ? "See more of the video (Ctrl + wheel)" : "As far out as it goes";

    public string ZoomResetTip => CanZoomReset() ? "Back to 100%" : "At 100% already";

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => Pps = Math.Min(MaxPps, Pps * 1.25);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => Pps = Math.Max(MinPps, Pps / 1.25);

    /// <summary>The percentage between − and +: back to the usual zoom.</summary>
    [RelayCommand(CanExecute = nameof(CanZoomReset))]
    private void ZoomReset() => Pps = DefaultPps;

    /// <summary>The time under a point of the timeline, in seconds.</summary>
    public double SecondsAt(double x) => Math.Max(0, (x - Lead) / Math.Max(1, Pps));

    // ---- the sounds' audio, read once: for their waveforms and for playing the video ---------------------------

    /// <summary>Each sound file's samples, mono, with their rate, by its path and time.</summary>
    private readonly Dictionary<string, (DateTime Stamp, float[] Samples, int Rate)> _audio = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _reading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A sound's samples, once read; null while they are read (it is laid out again then) or can't be.</summary>
    private (float[] Samples, int Rate)? AudioOf(string file)
    {
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (stamp == DateTime.MinValue)
            return null;
        if (_audio.TryGetValue(file, out var known) && known.Stamp == stamp)
            return (known.Samples, known.Rate);
        if (_previews.Decoder is not { } decoder || !_reading.Add(file))
            return null;
        var music = _board.Sounds.Any(s => s.Source is VideoSound.Song or VideoSound.FromFile && string.Equals(_board.SoundFile(s), file, StringComparison.OrdinalIgnoreCase));
        _ = Task.Run(() =>
        {
            (float[], int)? read = null;
            try
            {
                using var reader = decoder.OpenAudio(file);
                if (reader is not null)
                    read = MediaPreview.ReadMono(reader, TimeSpan.FromMinutes(20));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
            // Its beat too, for music: the ticks under its waveform, and what a clip's end or a title snaps to.
            BeatGrid? beat = null;
            if (music && read is var (heard, at) && heard.Length >= at * 8)
            {
                try { beat = SoundBeats.Find(new SoundSamples(heard, at, 1)); }
                catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { }
            }
            Dispatcher.UIThread.Post(() =>
            {
                _reading.Remove(file);
                if (_disposing)
                    return;
                _beats[file] = beat;
                _audio[file] = read is var (samples, rate) ? (stamp, samples, rate) : (stamp, [], 1);
                foreach (var sound in Sounds.Where(s => string.Equals(_board.SoundFile(s.Sound), file, StringComparison.OrdinalIgnoreCase)))
                    sound.FileSeconds = read is var (all, r) ? all.Length / (double)Math.Max(1, r) : 0;
                Relayout();
            });
        });
        return null;
    }

    /// <summary>The beat of each music file read, by its path; null for one with no steady beat.</summary>
    private readonly Dictionary<string, BeatGrid?> _beats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the music's beats fall in the video: each song's and sound file's, where it plays.</summary>
    internal IEnumerable<double> BeatMarks()
    {
        foreach (var sound in Sounds)
            if (_beats.GetValueOrDefault(_board.SoundFile(sound.Sound)) is { } grid)
            {
                var plays = sound.PlaySeconds(Length);
                foreach (var time in grid.Times)
                    if (time >= sound.Sound.Trim && time <= sound.Sound.Trim + plays)
                        yield return sound.Sound.Start + time - sound.Sound.Trim;
            }
    }

    /// <summary>Where a sound's beats fall along its block, 0 at its left edge to 1 at its right; null for one with no beat found.</summary>
    private double[]? BeatsOf(SoundItem sound)
    {
        if (_beats.GetValueOrDefault(_board.SoundFile(sound.Sound)) is not { } grid)
            return null;
        var plays = sound.PlaySeconds(Length);
        return plays <= 0 ? null : [.. grid.Times.Where(t => t >= sound.Sound.Trim && t <= sound.Sound.Trim + plays).Select(t => (t - sound.Sound.Trim) / plays)];
    }

    /// <summary>
    /// A clip's length while its right edge is dragged: to the beat its end is near (within <see cref="SnapPixels"/>),
    /// so a cut falls on the music; null when no beat is near, and the length goes by half seconds as before.
    /// </summary>
    internal double? BeatLength(ShotItem item, double seconds)
    {
        var index = _board.Shots.IndexOf(item.Shot);
        var (starts, _) = _board.Timing();
        if (index < 0 || index >= starts.Count)
            return null;
        var (end, reach) = (starts[index] + seconds, SnapPixels / Math.Max(1, Pps));
        double? nearest = null;
        foreach (var beat in BeatMarks())
            if (Math.Abs(beat - end) <= reach && (nearest is null || Math.Abs(beat - end) < Math.Abs(nearest.Value - end)))
                nearest = beat;
        return nearest is { } on && on - starts[index] >= 0.5 ? Math.Round(on - starts[index], 2) : null;
    }

    /// <summary>The waveform of the part of a sound that plays, a peak each tenth of a second, 0 to 1.</summary>
    private float[]? PeaksOf(SoundItem sound)
    {
        if (AudioOf(_board.SoundFile(sound.Sound)) is not var (samples, rate) || samples.Length == 0)
            return null;
        sound.FileSeconds = samples.Length / (double)rate;
        var seconds = sound.PlaySeconds(Length);
        // Worked out once for each trim and length: the timeline is laid out at every step of a drag.
        var key = (samples, sound.Sound.Trim, seconds);
        if (_peaks.TryGetValue(sound.Id, out var known) && known.Key == key)
            return known.Peaks;
        var peaks = new float[Math.Max(1, (int)Math.Ceiling(seconds * 10))];
        var per = Math.Max(1, rate / 10);
        var from = (long)(sound.Sound.Trim * rate);
        for (var i = 0; i < peaks.Length; i++)
        {
            var peak = 0f;
            for (long j = from + (long)i * per; j < Math.Min(samples.Length, from + (long)(i + 1) * per); j++)
                peak = Math.Max(peak, Math.Abs(samples[j]));
            peaks[i] = peak;
        }
        var top = peaks.Max();
        if (top > 0)
            for (var i = 0; i < peaks.Length; i++)
                peaks[i] /= top;
        _peaks[sound.Id] = (key, peaks);
        return peaks;
    }

    private readonly Dictionary<string, ((float[] Samples, double Trim, double Seconds) Key, float[] Peaks)> _peaks = new(StringComparer.OrdinalIgnoreCase);

    // ---- playing the video: its sounds mixed, with each clip's frames -------------------------------------

    /// <summary>Where the playhead is, in seconds from the video's start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadX), nameof(PlayTime))]
    private double _playPosition;

    public string PlayTime => $"{TimeSpan.FromSeconds(Math.Max(0, PlayPosition)):m\\:ss\\.f}";

    /// <summary>The storyboard frame under the playhead: a clip's first frame, its last for the second half.</summary>
    [ObservableProperty]
    private Bitmap? _playFrame;

    [ObservableProperty]
    private string _playLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayIcon), nameof(ShowsPlayhead))]
    private bool _isPlaying;

    public Avalonia.Media.Geometry PlayIcon => PlayerIcons.Play(IsPlaying);

    private IAudioOutput? _playOutput;
    private DispatcherTimer? _playTimer;
    private Stopwatch _playClock = new();
    private double _playFrom;

    /// <summary>Plays the sound lines mixed from the playhead with the frames switching as the clips do, or pauses it.</summary>
    [RelayCommand]
    private void PlayStrip()
    {
        if (IsPlaying)
        {
            StopPlaying();
            return;
        }
        var end = Math.Max(Length, Sounds.Select(s => s.Sound.Start + s.PlaySeconds(Length)).DefaultIfEmpty(0).Max());
        StartPlaying(PlayPosition >= 0 && PlayPosition < end - 0.05 ? PlayPosition : 0);
    }

    /// <summary>
    /// The arrow keys: the playhead a frame back or on (by <paramref name="frames"/>), or with Shift a second, kept
    /// between the video's start and end, for a split or a trim just there.
    /// </summary>
    public void StepPlayhead(int frames, bool seconds = false) =>
        SeekStrip(Math.Clamp(PlayPosition + (seconds ? frames : frames / (double)Math.Max(1, _board.Fps)), 0, Math.Max(0, Length)));

    /// <summary>Home and End: the playhead to the video's start or its end.</summary>
    public void PlayheadTo(bool end) => SeekStrip(end ? Math.Max(0, Length) : 0);

    /// <summary>The timeline was clicked: the playhead goes there (and plays on from there if it was playing).</summary>
    public void SeekStrip(double seconds)
    {
        var playing = IsPlaying;
        StopPlaying();
        PlayPosition = Math.Max(0, seconds);
        Scrubbed = true;
        // The clips' sound is read, and the video's sound mixed, by the time Play is pressed.
        foreach (var made in _board.Shots.Where(s => !s.Reverse && (s.Volume ?? 1) > 0))
            AudioOf(_board.ShotVideo(made));
        WarmMix();
        ShowFrameAt(PlayPosition);
        if (playing)
            StartPlaying(PlayPosition);
    }

    private void StartPlaying(double from)
    {
        StopPlaying();
        // The video's sound as joining it mixes it, when that mix is ready for the video as it is now: a clip's sound
        // through a transition, backwards or at another speed with its pitch kept, the music under a voice, the end's fade.
        // Before, Play mixed the clips' and lines' sounds plainly, and an edit was heard as it is only once the video was made.
        var json = MixKey();
        if (_soundMix is { } ready && ready.Json == json && _previews.NewAudioOutput() is { } speakers)
        {
            _playEnd = Math.Max(Length, 0);
            var joined = new JoinedSound(ready.Mix);
            joined.Seek(TimeSpan.FromSeconds(from));
            try
            {
                speakers.Play(joined);
                _playOutput = speakers;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                speakers.Dispose(); // no speakers to open
            }
            BeginPlaying(from);
            return;
        }
        WarmMix();
        // The sounds that are read, mixed as the video joins them; with none, the frames go by the clock.
        var parts = new List<VideoMix.Part>();
        foreach (var sound in Sounds)
            if (AudioOf(_board.SoundFile(sound.Sound)) is var (samples, rate) && samples.Length > 0)
                parts.Add(new VideoMix.Part(samples, rate, sound.Sound.Start, sound.Sound.Trim, sound.PlaySeconds(Length), sound.Sound.Volume * _board.LineVolume(sound.Sound.SoundLine)));
        // The clips' own sound too, each from where its clip starts, as the joined video has it: its start cut off, at its
        // speed (higher or lower with it here, where the joined video keeps its pitch), at its volume, half under a song.
        // Not a clip played backwards, nor one whose sound isn't read yet (it is by the next time).
        var (starts, _) = _board.Timing();
        var under = _board.Sounds.Any(s => s.Source == VideoSound.Song) ? 0.5 : 1.0;
        for (var i = 0; i < _board.Shots.Count && i < starts.Count; i++)
        {
            var shot = _board.Shots[i];
            if (shot.Reverse || (shot.Volume ?? 1) <= 0 || AudioOf(_board.ShotVideo(shot)) is not var (samples, rate) || samples.Length == 0)
                continue;
            var plays = shot.Freeze is { } freeze ? Math.Min(freeze, shot.Seconds) : shot.Seconds;
            parts.Add(new VideoMix.Part(samples, rate, starts[i], shot.In, plays, (shot.Volume ?? 1) * under, shot.Speed ?? 1));
        }
        _playEnd = Math.Max(Length, parts.Select(p => p.Start + p.Seconds).DefaultIfEmpty(0).Max());
        if (parts.Count > 0 && _previews.NewAudioOutput() is { } output)
        {
            var mix = new VideoMix(parts, _playEnd);
            mix.Seek(TimeSpan.FromSeconds(from));
            try
            {
                output.Play(mix);
                _playOutput = output;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                output.Dispose(); // no speakers to open
            }
        }
        BeginPlaying(from);
    }

    /// <summary>The playhead set going from a moment, by the sound's clock when there is sound.</summary>
    private void BeginPlaying(double from)
    {
        _playFrom = from;
        _playClock = Stopwatch.StartNew();
        IsPlaying = true;
        PlayPosition = from;
        ShowFrameAt(from);
        _playTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => PlayTick());
        _playTimer.Start();
    }

    private double _playEnd;

    /// <summary>The video's sound mixed as joining it does (<see cref="VideoSoundMix"/>), for the video as its JSON was then.</summary>
    private (string Json, float[] Mix)? _soundMix;
    private bool _mixing;

    /// <summary>The longest video whose whole sound is mixed ahead for Play (23 MB a minute while the panel is open).</summary>
    private const double LongestMixed = 300;

    /// <summary>What a mix is of: the video as written, and when each clip and sound it is made of was written (a clip made again is another sound).</summary>
    private string MixKey()
    {
        static long Stamp(string file)
        {
            try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
        return _board.ToJson() + "|" + string.Join(',', _board.Shots.Select(s => Stamp(_board.ShotVideo(s))).Concat(_board.Sounds.Select(s => Stamp(_board.SoundFile(s)))));
    }

    /// <summary>Mixes the video's sound in the background when the video changed since the last mix, so Play has it.</summary>
    private void WarmMix()
    {
        if (_mixing || _disposing || _previews.Decoder is not { } decoder || _board.Shots.Count == 0 || Length > LongestMixed)
            return;
        var json = MixKey();
        if (_soundMix?.Json == json)
            return;
        var board = Storyboard.Parse(_board.ToJson());
        board.Folder = _board.Folder;
        _mixing = true;
        _ = Task.Run(() =>
        {
            float[]? mix = null;
            try { mix = VideoSoundMix.Mix(board, decoder, []); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or SoundEditException) { }
            Dispatcher.UIThread.Post(() =>
            {
                _mixing = false;
                if (_disposing)
                    return;
                _soundMix = mix is null ? null : (json, mix);
                // Changed again meanwhile: once more for the video as it is now.
                if (MixKey() != json)
                    WarmMix();
            });
        });
    }

    private void PlayTick()
    {
        var at = _playFrom + (_playOutput?.Played ?? _playClock.Elapsed).TotalSeconds;
        if (at >= _playEnd)
        {
            StopPlaying();
            PlayPosition = _playEnd;
            return;
        }
        PlayPosition = at;
        ShowFrameAt(at);
    }

    private void StopPlaying()
    {
        _playTimer?.Stop();
        _playTimer = null;
        Interlocked.Exchange(ref _playOutput, null)?.Dispose();
        IsPlaying = false;
    }

    /// <summary>
    /// The video under the playhead, in the viewer: the clip there at its own frame, as it is edited, with the titles
    /// over it (<see cref="VideoPreviewer"/>), drawn off the window's thread, the latest moment asked for first. Until
    /// its frame is there, and for a clip not made yet, the clip's start or end picture as before.
    /// </summary>
    private void ShowFrameAt(double seconds)
    {
        var (starts, _) = _board.Timing();
        var at = -1;
        for (var i = 0; i < Shots.Count && i < starts.Count; i++)
            if (starts[i] <= seconds + 1e-6)
                at = i;
        if (at < 0)
        {
            _frameEpoch++;
            SetPlayFrame(null, owned: false);
            return;
        }
        var shot = Shots[at];
        PlayLabel = $"{shot.Title} · {TimeSpan.FromSeconds(Math.Max(0, seconds)):m\\:ss\\.f}";
        if (_previews.Decoder is not null && File.Exists(_board.ShotVideo(shot.Shot)))
        {
            AskFrame(seconds);
            return;
        }
        var late = seconds - starts[at] >= shot.Shot.Seconds / 2 && shot.Last is not null && !shot.Shot.LipSync;
        var frame = late ? shot.Last : shot.First ?? shot.Last;
        _frameEpoch++; // a frame still being drawn for a moment before isn't shown over this
        SetPlayFrame(frame?.Thumbnail, owned: false);
    }

    /// <summary>The longer side the playhead's look is drawn at: as wide as the viewer is on most screens.</summary>
    private const int PreviewSide = 960;

    private VideoPreviewer? _previewer;

    /// <summary>The storyboard the previewer draws from, a copy of its own (it is read off the window's thread), and the JSON it was copied at.</summary>
    private (string Json, Storyboard Board)? _previewBoard;

    /// <summary>The moment asked for last, and its number: a frame drawn for an earlier one is dropped.</summary>
    private double _frameWanted;
    private int _frameAsked;
    private int _frameEpoch;
    private bool _drawing;

    /// <summary>Whether <see cref="PlayFrame"/> is a bitmap of the previewer's, to let go when another is shown (a clip's thumbnail is its clip's).</summary>
    private bool _ownsPlayFrame;

    private void SetPlayFrame(Bitmap? frame, bool owned)
    {
        var old = _ownsPlayFrame ? PlayFrame : null;
        (PlayFrame, _ownsPlayFrame) = (frame, owned);
        if (!ReferenceEquals(old, frame))
            old?.Dispose();
    }

    /// <summary>Asks for the video's frame at a moment; one is drawn at a time, and then the latest asked for meanwhile.</summary>
    private void AskFrame(double seconds)
    {
        _frameWanted = seconds;
        var asked = Interlocked.Increment(ref _frameAsked);
        var epoch = _frameEpoch;
        if (_drawing || _previews.Decoder is not { } decoder)
            return;
        var json = _board.ToJson();
        if (_previewBoard?.Json != json)
        {
            var copy = Storyboard.Parse(json);
            copy.Folder = _board.Folder;
            _previewBoard = (json, copy);
        }
        var board = _previewBoard.Value.Board;
        var previewer = _previewer ??= new VideoPreviewer(decoder, PreviewSide);
        _drawing = true;
        _ = Task.Run(() =>
        {
            Core.Platform.RawImage? frame = null;
            try { frame = previewer.FrameAt(board, seconds); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { }
            Dispatcher.UIThread.Post(() =>
            {
                _drawing = false;
                if (_disposing)
                    return;
                // Shown unless a clip not made, or nothing, took the viewer meanwhile; then on to the latest moment, if another was asked for.
                if (frame is not null && epoch == _frameEpoch)
                    SetPlayFrame(ToBitmap(frame), owned: true);
                if (asked != _frameAsked && ShowsPlayhead)
                    AskFrame(_frameWanted);
                else
                    RestPreview();
            });
        });
    }

    private static Bitmap ToBitmap(Core.Platform.RawImage frame)
    {
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(frame.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul, pinned.AddrOfPinnedObject(),
                new Avalonia.PixelSize(frame.Width, frame.Height), new Avalonia.Vector(96, 96), frame.Stride);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// The viewer shows the video under the playhead: while it plays, and once the playhead was moved (a click on the
    /// ruler, the arrows, Home and End), until something is picked or let go of. Before, moving the playhead showed
    /// nothing: a split or a trim "at the playhead" was made blind.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlayhead))]
    private bool _scrubbed;

    public bool ShowsPlayhead => IsPlaying || Scrubbed;

    /// <summary>
    /// Nothing asked of the previewer for a few seconds, and nothing playing: its readers are closed, so the clips'
    /// files aren't held open while the panel only stands there.
    /// </summary>
    private void RestPreview()
    {
        _restTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _restTimer!.Stop();
            if (!IsPlaying && !_drawing)
                _previewer?.Reset();
        });
        _restTimer.Stop();
        _restTimer.Start();
    }

    private DispatcherTimer? _restTimer;

    /// <summary>The previewer's readers closed (a clip is about to be written over, another video is open).</summary>
    private void ResetPreview()
    {
        _previewer?.Reset();
        _previewBoard = null;
    }
}

/// <summary>
/// The video's sounds mixed into one, as joining the video lays them over each other: each from where
/// it starts, its trim cut off, as long as it plays, at its volume; mono at 48 kHz.
/// </summary>
internal sealed class VideoMix(IReadOnlyList<VideoMix.Part> parts, double length) : IAudioReader
{
    /// <param name="Speed">How fast it plays: 2 is twice as fast (and higher), as a clip sped up.</param>
    public sealed record Part(float[] Samples, int Rate, double Start, double Trim, double Seconds, double Volume, double Speed = 1);

    private const int Rate = 48000;
    private long _at;

    public int SampleRate => Rate;
    public int Channels => 1;
    public TimeSpan Duration => TimeSpan.FromSeconds(length);

    public int Read(float[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, (long)(length * Rate) - _at);
        if (n <= 0)
            return 0;
        Array.Clear(buffer, offset, n);
        foreach (var part in parts)
            for (var i = 0; i < n; i++)
            {
                var local = (_at + i) / (double)Rate - part.Start;
                if (local < 0 || local >= part.Seconds)
                    continue;
                var pos = (local * part.Speed + part.Trim) * part.Rate;
                var j = (long)pos;
                if (j + 1 >= part.Samples.Length)
                    continue;
                var f = (float)(pos - j);
                buffer[offset + i] += (part.Samples[j] * (1 - f) + part.Samples[j + 1] * f) * (float)part.Volume;
            }
        for (var i = 0; i < n; i++)
            buffer[offset + i] = Math.Clamp(buffer[offset + i], -1f, 1f);
        _at += n;
        return n;
    }

    public void Seek(TimeSpan time) => _at = Math.Max(0, (long)(time.TotalSeconds * Rate));

    public void Dispose() { }
}

/// <summary>The video's sound already mixed (stereo at 48 kHz), played from a moment.</summary>
internal sealed class JoinedSound(float[] mix) : IAudioReader
{
    private int _at;

    public int SampleRate => VideoSoundMix.Rate;
    public int Channels => VideoSoundMix.Channels;
    public TimeSpan Duration => TimeSpan.FromSeconds(mix.Length / VideoSoundMix.Channels / (double)VideoSoundMix.Rate);

    public int Read(float[] buffer, int offset, int count)
    {
        var n = Math.Min(count, mix.Length - _at);
        if (n <= 0)
            return 0;
        Array.Copy(mix, _at, buffer, offset, n);
        _at += n;
        return n;
    }

    public void Seek(TimeSpan time) => _at = (int)Math.Clamp((long)(time.TotalSeconds * VideoSoundMix.Rate) * VideoSoundMix.Channels, 0, mix.Length);

    public void Dispose() { }
}
