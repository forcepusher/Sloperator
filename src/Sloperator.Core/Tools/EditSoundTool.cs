using System.Globalization;
using System.Text.Json.Nodes;
using Sloperator.Core.Media;

namespace Sloperator.Core.Tools;

/// <summary>
/// The Audio editor's edits for the agent (<see cref="SoundEdit"/>): what the panel's buttons do, and a
/// stretch replaced by another sound, so "take out the cough at 3 s" or "redo that bit" goes as the user
/// asks it, without a script of the model's own. The file edited stays as it was; the edit is a new file
/// beside it, in a sound of the panel its next version.
/// </summary>
public sealed class EditSoundTool : Tool
{
    public override string Name => "edit_sound";

    public override string Description =>
        "Edit a sound file (WAV, MP3, FLAC, OGG, M4A…) without changing it: the edit is saved beside it as a new WAV, which the result names " +
        "(for a sound of Sloperator's Audio editor panel, its next version there). " +
        "edit: cut (take out from-to, the sound either side joined), keep (only from-to), fade_in or fade_out (over from-to; without them the first or last second), " +
        "volume (db louder, or quieter when negative; over from-to, else all of it), replace (the sound file \"with\" put in place of from-to, such as a new take made with " +
        "the comfyui tool, fitted to the sound's sample rate and channels). from and to are seconds. Joins are crossfaded over 10 ms, so nothing clicks.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("path", Str("The sound file to edit"), true),
        ("edit", Str("What to do", "cut", "keep", "fade_in", "fade_out", "volume", "replace"), true),
        ("from", Num("Where the stretch starts, in seconds"), false),
        ("to", Num("Where it ends, in seconds"), false),
        ("db", Num("volume: how many dB louder (3) or quieter (-3)"), false),
        ("with", Str("replace: the sound file to put in the stretch's place"), false));

    public override string Describe(JsonObject args)
    {
        var name = Path.GetFileName(GetString(args, "path") ?? "");
        var (from, to) = (GetNumber(args, "from"), GetNumber(args, "to"));
        var range = from is { } f && to is { } t ? string.Create(CultureInfo.InvariantCulture, $" {f:0.00}-{t:0.00} s of") : "";
        return Kind(GetString(args, "edit")) switch
        {
            SoundEditKind.Cut => $"Cut{range} {name}",
            SoundEditKind.Keep => $"Keep only{range} {name}",
            SoundEditKind.FadeIn => $"Fade in{range} {name}",
            SoundEditKind.FadeOut => $"Fade out{range} {name}",
            SoundEditKind.Replace => $"Replace{range} {name}",
            SoundEditKind.Volume => string.Create(CultureInfo.InvariantCulture, $"{GetNumber(args, "db") ?? 0:+0.#;-0.#} dB{range} {name}"),
            _ => $"Edit {name}",
        };
    }

    /// <summary>The edit as the model named it, forgiving of case, spaces and dashes; null for none of them.</summary>
    private static SoundEditKind? Kind(string? edit) => edit?.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "") switch
    {
        "cut" or "delete" or "remove" => SoundEditKind.Cut,
        "keep" or "keeponly" or "trim" or "crop" => SoundEditKind.Keep,
        "fadein" => SoundEditKind.FadeIn,
        "fadeout" => SoundEditKind.FadeOut,
        "volume" or "gain" => SoundEditKind.Volume,
        "replace" => SoundEditKind.Replace,
        _ => null,
    };

    public override Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var path = ctx.ResolvePath(RequireString(args, "path"));
        var kind = Kind(RequireString(args, "edit")) ?? throw new ToolArgumentException("edit is one of cut, keep, fade_in, fade_out, volume, replace.");
        if (ctx.Media is not { } decoder)
            return Task.FromResult(ToolResult.Error("Nothing on this PC reads sound here."));
        var with = GetString(args, "with") is { Length: > 0 } other ? ctx.ResolvePath(other) : null;
        var request = new SoundEditRequest(kind, GetNumber(args, "from"), GetNumber(args, "to"), GetNumber(args, "db") ?? 0, with);
        return Task.Run(() =>
        {
            try
            {
                var edited = SoundEdit.Save(path, request, decoder, ctx.SoundsDirectory);
                var version = SoundFolder.IsVersion(edited.Path, ctx.SoundsDirectory)
                    ? $" It is the sound's newest version in the Audio editor; {Path.GetFileName(path)} stays as it was." : "";
                return new ToolResult(string.Create(CultureInfo.InvariantCulture,
                    $"{edited.Label}: saved {edited.Path}, {edited.After:0.000} s long ({edited.Before:0.000} s before), {edited.Rate / 1000.0:0.#} kHz {MediaPreview.Channels(edited.Channels)}.{version}"),
                    Files: [edited.Path]);
            }
            catch (SoundEditException ex)
            {
                return ToolResult.Error(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ToolResult.Error($"The edit could not be saved: {ex.Message}");
            }
        }, ct);
    }
}
