using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sloperator.Core.Chats;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Sloperator.Core.Platform;

namespace Sloperator.App.ViewModels;

/// <summary>A sound in the Audio editor panel's list: its folder, its name, and the shape of its newest version.</summary>
public sealed partial class AudioItem(string folder) : MediaFolderItem(folder)
{
    /// <summary>How many columns the list's small waveform has.</summary>
    public const int ThumbColumns = 160;

    /// <summary>Its newest version's waveform, each column's lowest and highest (as loud as the drawing allows); null before it has one.</summary>
    [ObservableProperty]
    private (float Min, float Max)[]? _peaks;

    private string? _peaksOf;

    /// <summary>The small waveform read again when the newest version is another file.</summary>
    internal void LoadPeaks(IMediaDecoder? decoder)
    {
        if (Versions.Count == 0 || Versions[^1] == _peaksOf || decoder is null)
            return;
        var path = _peaksOf = Versions[^1];
        Task.Run(() =>
        {
            try
            {
                using var reader = decoder.OpenAudio(path);
                if (reader is null)
                    return;
                var (mono, _) = MediaPreview.ReadMono(reader, SoundEdit.Longest);
                var peaks = new (float Min, float Max)[ThumbColumns];
                var loudest = 0f;
                for (var x = 0; x < ThumbColumns && mono.Length > 0; x++)
                {
                    var (a, b) = ((int)((long)x * mono.Length / ThumbColumns), (int)Math.Max((long)x * mono.Length / ThumbColumns + 1, (long)(x + 1) * mono.Length / ThumbColumns));
                    float lo = 0, hi = 0;
                    for (var i = a; i < b && i < mono.Length; i++)
                    {
                        lo = Math.Min(lo, mono[i]);
                        hi = Math.Max(hi, mono[i]);
                    }
                    peaks[x] = (lo, hi);
                    loudest = Math.Max(loudest, Math.Max(-lo, hi));
                }
                // As the big one: up to ten times louder, so a quiet sound still shows its shape.
                var gain = loudest > 0 ? Math.Min(10f, 0.95f / loudest) : 1f;
                for (var x = 0; x < peaks.Length; x++)
                    peaks[x] = (peaks[x].Min * gain, peaks[x].Max * gain);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_peaksOf == path)
                        Peaks = peaks;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
            }
        });
    }
}


/// <summary>
/// The Audio editor panel, beside the chat, a sibling of the Image editor for sounds: the sounds in
/// SloperatorData\sounds (a folder each, its versions the sound files in it), the picked one's waveform big
/// with its versions under it, its edits and how it was made beside it. The agent makes them with the
/// comfyui tool, saving each result into the sound's folder; Cut, Keep only, Fade in, Fade out, Louder and
/// Quieter (<see cref="SoundEdit"/>) are done here at once, each a new version, so the earlier ones stay
/// (the user's pick of three mockups, 2026-11-08). A click on a sound or a version picks it into the
/// chat's box; a stretch dragged over the waveform picks that stretch, which the edits then work on. What it
/// shares with the Image editor is <see cref="MediaEditorViewModel{TItem}"/>'s.
/// </summary>
public sealed partial class AudioEditorViewModel : MediaEditorViewModel<AudioItem>
{
    private readonly PreviewServices _previews;

    /// <param name="chat">The chat the panel is beside, where picks go and its messages are sent; null in tests.</param>
    public AudioEditorViewModel(MainWindowViewModel? chat, PreviewServices previews, string root, string picksDir)
        : base(chat, root, picksDir, SoundFolder.Folders, "sound")
    {
        _previews = previews;
        Refresh();
    }

    // ---- the sounds ---------------------------------------------------------------------------------

    public override string Title => Selected is not { } s ? "Click a sound or a version to ask about it, or drag over a stretch of it to edit that stretch."
        : Shown is { } v ? $"{s.Name} · version {v.Number} of {s.Versions.Count}" : $"{s.Name} · not made yet";

    /// <summary>What the waveform's room says with no sound to show.</summary>
    public override string EmptyText => Selected is { IsMade: false } ? "Not made yet. Say in the chat what it is, and the agent makes it here."
        : Shown is not null ? _unreadable ? "Nothing on this PC can play this sound." : "Loading…"
        : HasItems ? "Pick a sound" : "No sounds yet. New sound starts one; say in the chat what it is.";

    /// <summary>The version shown can't be read: said in its place.</summary>
    private bool _unreadable;

    /// <summary>The version shown, read: its waveform, playhead and stretch picked, and its player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSound))]
    private InspectorSoundViewModel? _sound;

    public bool HasSound => Sound is not null;

    protected override AudioItem NewItem(string folder) => new(folder);

    protected override void OnVersionsRead(AudioItem item) => item.LoadPeaks(_previews.Decoder);

    /// <summary>
    /// The stretch to pick again on the version an edit makes, when it is as long as the one edited (a
    /// fade, louder, quieter): the next click of Louder works on the same stretch.
    /// </summary>
    private (double From, double To, double Length)? _carry;

    /// <summary>
    /// A version into the waveform's room, read off the window's thread, its Prompt metadata beside it. The
    /// waveform shown stays (stopped) until the next is read, so an edit doesn't blink the room empty.
    /// </summary>
    protected override void ShowVersion(VersionItem? version)
    {
        foreach (var v in Versions)
            v.IsShown = ReferenceEquals(v, version);
        Shown = version;
        Sound?.Pause();
        _unreadable = false;
        OnEditsChanged();
        if (version is null)
        {
            DropSound();
            Recipe = null;
            return;
        }
        var path = version.Path;
        var carry = _carry;
        _carry = null;
        Task.Run(() =>
        {
            var sound = new InspectorSoundViewModel(path, _previews);
            var read = false;
            try { read = sound.Load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            var recipe = MediaRecipe.Read(path);
            Dispatcher.UIThread.Post(() =>
            {
                if (Closed || Shown?.Path != path)
                {
                    sound.Dispose();
                    return;
                }
                Recipe = recipe;
                DropSound();
                if (!read)
                {
                    sound.Dispose();
                    _unreadable = true;
                    OnPropertyChanged(nameof(EmptyText));
                    return;
                }
                sound.Picked += PickRange;
                sound.PropertyChanged += OnSoundChanged;
                Sound = sound;
                OnEditsChanged();
                if (carry is { } c && Math.Abs(c.Length - sound.Length) < 0.002)
                {
                    sound.Seek(c.From);
                    sound.Pick((c.From, c.To));
                }
            });
        });
    }

    /// <summary>The sound shown goes (another shown, the panel closed): its playing stops.</summary>
    private void DropSound()
    {
        if (Sound is not { } sound)
            return;
        sound.Picked -= PickRange;
        sound.PropertyChanged -= OnSoundChanged;
        Sound = null;
        sound.Dispose();
    }

    private void OnSoundChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorSoundViewModel.Range))
            OnEditsChanged();
    }

    // ---- the edits ----------------------------------------------------------------------------------

    /// <summary>An edit is being made: the buttons wait for it.</summary>
    private bool _editing;

    /// <summary>What the edits work on, beside the Edit heading: the stretch picked, else the whole sound.</summary>
    public string EditsOn => Sound is null ? "" : Sound.HasRange ? "the stretch picked" : "the whole sound";

    private bool HasStretch => Sound is { HasRange: true };

    /// <summary>The version shown is read (not the one before it, still shown while it is) and no edit is being saved.</summary>
    private bool CanEdit() => Sound is { Samples.Length: > 0 } sound && sound.Path == Shown?.Path && !_editing;

    private bool CanEditStretch() => CanEdit() && HasStretch;

    private string NoSound => _editing ? "An edit is being saved" : "Pick a sound that has been made first";

    public string CutTip => CanEditStretch() ? "Take the stretch out, the sound either side joined (Delete)" : CanEdit() ? "Drag over a stretch of the sound first" : NoSound;

    public string KeepTip => CanEditStretch() ? "Keep only the stretch" : CanEdit() ? "Drag over a stretch of the sound first" : NoSound;

    public string FadeInTip => !CanEdit() ? NoSound : HasStretch ? "Fade the stretch in from silence" : "Fade in over the first second";

    public string FadeOutTip => !CanEdit() ? NoSound : HasStretch ? "Fade the stretch out to silence" : "Fade out over the last second";

    public string LouderTip => !CanEdit() ? NoSound : $"{SoundEdit.Step:0} dB louder: {EditsOn}";

    public string QuieterTip => !CanEdit() ? NoSound : $"{SoundEdit.Step:0} dB quieter: {EditsOn}";

    private void OnEditsChanged()
    {
        foreach (var name in (string[])[nameof(EditsOn), nameof(CutTip), nameof(KeepTip), nameof(FadeInTip), nameof(FadeOutTip), nameof(LouderTip), nameof(QuieterTip)])
            OnPropertyChanged(name);
        CutCommand.NotifyCanExecuteChanged();
        KeepCommand.NotifyCanExecuteChanged();
        FadeInCommand.NotifyCanExecuteChanged();
        FadeOutCommand.NotifyCanExecuteChanged();
        LouderCommand.NotifyCanExecuteChanged();
        QuieterCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditStretch))]
    private void Cut() => Edit(SoundEditKind.Cut);

    [RelayCommand(CanExecute = nameof(CanEditStretch))]
    private void Keep() => Edit(SoundEditKind.Keep);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void FadeIn() => Edit(SoundEditKind.FadeIn);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void FadeOut() => Edit(SoundEditKind.FadeOut);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Louder() => Edit(SoundEditKind.Volume, SoundEdit.Step);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Quieter() => Edit(SoundEditKind.Volume, -SoundEdit.Step);

    /// <summary>Raised when an edit is saved, with its file (the version it makes is then shown as the folder is read).</summary>
    internal event Action<string>? Edited;

    /// <summary>
    /// The edit made on the version shown, over the stretch picked (else the whole sound, or a fade's first
    /// or last second), off the window's thread: saved as the sound's next version, which the folder's
    /// watcher then shows. A stretch an edit leaves as long is picked again on the new version.
    /// </summary>
    private void Edit(SoundEditKind kind, double db = 0)
    {
        if (Sound is not { } sound || Shown is not { } version || _previews.Decoder is not { } decoder || !CanEdit())
            return;
        var range = sound.Range;
        var request = new SoundEditRequest(kind, range?.From, range?.To, db);
        _editing = true;
        OnEditsChanged();
        // What was picked is of the version before: it leaves the box.
        Unpick();
        var path = version.Path;
        var root = Root;
        Task.Run(() =>
        {
            try
            {
                var edited = SoundEdit.Save(path, request, decoder, root);
                Dispatcher.UIThread.Post(() =>
                {
                    _editing = false;
                    if (Closed)
                        return;
                    if (range is { } r && kind is SoundEditKind.FadeIn or SoundEditKind.FadeOut or SoundEditKind.Volume)
                        _carry = (r.From, r.To, edited.After);
                    OnEditsChanged();
                    Edited?.Invoke(edited.Path);
                    Refresh();
                });
            }
            catch (Exception ex) when (ex is SoundEditException or IOException or UnauthorizedAccessException)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _editing = false;
                    OnEditsChanged();
                    Chat?.AddNotice($"The edit could not be saved: {ex.Message}", isError: ex is not SoundEditException, ex);
                });
            }
        });
    }

    // ---- what is picked, into the chat's box ------------------------------------------------------

    /// <summary>What is picked: the sound (its version shown, or none yet), or a stretch of the version with a picture of it.</summary>
    private sealed record Picked(AudioItem Sound, VersionItem? Version, string File, (double From, double To)? Stretch = null, string? Told = null);

    private Picked? _pick;

    protected override bool HasPick => _pick is not null;

    /// <summary>The sound selected, as the version shown (or the folder of one not made yet), into the box.</summary>
    protected override void PickWhole()
    {
        if (Selected is not { } sound)
            return;
        DropFile();
        if (Sound is { HasRange: true } shown)
            shown.Range = null;
        var file = Shown?.Path ?? sound.Folder;
        _pick = new Picked(sound, Shown, file);
        Box.Put([(file, Shown is { } v ? $"Version {v.Number} · {sound.Name}" : $"New · {sound.Name}")]);
    }

    /// <summary>
    /// A stretch dragged over the waveform: a picture of it (its waveform over its spectrogram, as the model
    /// hears a sound) is saved for the message and goes into the box as "3.18-5.00 s of version 2 · Rain".
    /// A click (null) lets go of it.
    /// </summary>
    private void PickRange((double From, double To)? range)
    {
        OnEditsChanged();
        if (range is not { } r || Selected is not { } item || Shown is not { } version || Sound is not { } sound || sound.Path != version.Path)
        {
            if (_pick is { Stretch: not null })
                Unpick();
            return;
        }
        var dir = PicksDir;
        Task.Run(() =>
        {
            try
            {
                using var picture = SoundRange.Picture(sound.Samples, sound.Rate, r.From, r.To);
                var bgra = new byte[picture.Width * picture.Height * 4];
                using (var copy = picture.Copy(SkiaSharp.SKColorType.Bgra8888))
                    for (var row = 0; row < copy.Height; row++)
                        System.Runtime.InteropServices.Marshal.Copy(copy.GetPixels() + row * copy.RowBytes, bgra, row * copy.Width * 4, copy.Width * 4);
                var file = DocumentArea.SaveNew(new RawImage(picture.Width, picture.Height, bgra), dir);
                var told = SoundRange.Told(sound.Samples, sound.Rate, r.From, r.To);
                Dispatcher.UIThread.Post(() =>
                {
                    // Another stretch picked meanwhile, or another version shown: it isn't wanted.
                    if (Closed || !ReferenceEquals(Sound, sound) || sound.Range != r)
                    {
                        PastedFiles.TryDelete(file);
                        return;
                    }
                    DropFile();
                    _pick = new Picked(item, version, file, r, told);
                    Box.Put([(file, $"{SoundRange.Label(r.From, r.To)} of version {version.Number} · {item.Name}")]);
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(() => Chat?.AddNotice($"The stretch picked could not be saved: {ex.Message}", isError: true, ex));
            }
        });
    }

    /// <summary>A stretch picked before goes: its picture, which wasn't sent, with it.</summary>
    private void DropFile()
    {
        if (_pick is { Stretch: not null } pick)
            PastedFiles.TryDelete(pick.File);
        _pick = null;
    }

    /// <summary>Esc, a click away: what was picked leaves the box, the stretch with it. False when nothing was.</summary>
    public bool Unpick()
    {
        if (Sound is { HasRange: true } sound)
            sound.Range = null;
        if (_pick is null)
            return false;
        DropFile();
        Box.Put([]);
        RaiseUnpicked();
        return true;
    }

    protected override void OnTakenOff()
    {
        if (_pick is null)
            return;
        DropFile();
        if (Sound is { HasRange: true } sound)
            sound.Range = null;
        RaiseUnpicked();
    }

    /// <summary>
    /// What a message sent with a pick carries: over its bubble the sound and what of it; for the model, the
    /// sound's folder and versions, how to edit it (edit_sound) and how to make the next one there (the comfyui
    /// tool's save_to), or for a stretch, where it is to the sample and how it sounds there, with a picture of it.
    /// </summary>
    public override (string Via, string Context)? About(IReadOnlyList<string> files)
    {
        if (_pick is not { } pick || !files.Contains(pick.File, StringComparer.OrdinalIgnoreCase))
            return null;
        var sound = pick.Sound;
        var save = $"make it with the comfyui tool and save_to set to the sound's folder, \"{sound.Folder}\", so the result is its next version there " +
                   "(the panel shows the newest; earlier versions stay)";
        const string opening = "[The Audio editor panel is open beside the chat. ";
        if (pick.Version is null)
            return ($"Audio editor · {sound.Name} · new",
                opening + $"They picked a new sound, \"{sound.Name}\", not made yet: what they say is what it is. {char.ToUpperInvariant(save[0])}{save[1..]}; " +
                "action=templates finds one for sound effects, music or speech.]");
        var version = pick.Version;
        var versions = string.Join(", ", sound.Versions.Select((v, i) => $"{i + 1}: {v}"));
        var edit = $"run edit_sound with path \"{version.Path}\"";
        const string edits = "(cut, keep, fade_in, fade_out, volume, replace): each edit is the sound's next version";
        if (pick.Stretch is { } r)
        {
            var inv = CultureInfo.InvariantCulture;
            return ($"Audio editor · {sound.Name} · {SoundRange.Label(r.From, r.To)} of version {version.Number}",
                opening + $"They picked a stretch of version {version.Number} of the sound \"{sound.Name}\" ({version.Path}). {pick.Told}. " +
                $"The picture {pick.File} attached below is that stretch's waveform (top) and spectrogram (bottom, low notes at the bottom, louder is brighter), its seconds marked. " +
                string.Create(inv, $"To change only that stretch, {edit}, from {r.From:0.000} and to {r.To:0.000} {edits}; to make it anew, make a take about {r.To - r.From:0.0} s long with the comfyui tool ") +
                $"(save_to a folder of its own, not the sound's) and put it in with edit=replace and with set to that file. Its versions: {versions}.]");
        }
        return ($"Audio editor · {sound.Name} · version {version.Number}",
            opening + $"They picked version {version.Number} of {sound.Versions.Count} of the sound \"{sound.Name}\" ({version.Path}, attached below). " +
            $"To edit it, {edit} {edits}; to make it again, {save}. Its versions: {versions}.]");
    }

    /// <summary>The message went with the pick: it is no longer picked (a stretch's picture went with it, and stays).</summary>
    public override void Sent()
    {
        if (_pick is null)
            return;
        _pick = null;
        if (Sound is { HasRange: true } sound)
            sound.Range = null;
        RaiseUnpicked();
    }


    public override void Dispose()
    {
        base.Dispose();
        DropFile();
        DropSound();
        Edited = null;
    }
}
