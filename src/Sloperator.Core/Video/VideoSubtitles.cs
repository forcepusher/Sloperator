using System.Globalization;
using System.Text;
using Sloperator.Core.Media;

namespace Sloperator.Core.Video;

/// <summary>
/// What is said in the video as a subtitle file beside it (<c>Title.srt</c>, next to <c>Title.mp4</c>): each shot's
/// line while its shot speaks it, and each voice-over while it plays. A player that finds the file shows the words,
/// and a site the video is put on takes it as its subtitles, where before the words were only in the storyboard or
/// burnt into the picture as captions.
/// </summary>
public static class VideoSubtitles
{
    /// <summary>The file a video's subtitles are in: named as the video, beside it.</summary>
    public static string FileOf(Storyboard board) => Path.ChangeExtension(board.VideoFile, ".srt");

    /// <summary>What is said and when, in order: (from, to, words), in the video's seconds.</summary>
    public static List<(double From, double To, string Words)> Lines(Storyboard board, IMediaDecoder? decoder)
    {
        var (starts, length) = board.Timing();
        var lines = new List<(double From, double To, string Words)>();
        for (var i = 0; i < board.Shots.Count; i++)
            if (board.Shots[i].Line.Trim() is { Length: > 0 } line)
                lines.Add((starts[i] + VideoMaker.LineLead, starts[i] + board.Shots[i].Seconds, line));
        foreach (var voice in board.Sounds.Where(s => s.Source == VideoSound.Speech && s.Text.Trim().Length > 0))
        {
            // As long as it plays: its own length when it says, else its file's, else a guess from its words (150 a minute).
            var plays = voice.Seconds > 0 ? voice.Seconds
                : decoder is not null && File.Exists(board.SoundFile(voice)) && decoder.Probe(board.SoundFile(voice))?.Duration.TotalSeconds is > 0 and var heard ? Math.Max(0.5, heard - voice.Trim)
                : Math.Max(1.5, voice.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 0.4);
            lines.Add((voice.Start, voice.Start + plays, voice.Text.Trim()));
        }
        return [.. lines.Where(l => l.From < length - 0.05).Select(l => (l.From, Math.Min(l.To, length), l.Words)).Where(l => l.Item2 - l.From >= 0.2).OrderBy(l => l.From)];
    }

    /// <summary>
    /// Writes the subtitles beside the video, or takes away the file of an earlier join when nothing is said any
    /// more; returns the file written, null for none.
    /// </summary>
    public static string? Write(Storyboard board, IMediaDecoder? decoder)
    {
        var file = FileOf(board);
        var lines = Lines(board, decoder);
        try
        {
            if (lines.Count == 0)
            {
                File.Delete(file);
                return null;
            }
            File.WriteAllText(file, Srt(lines), new UTF8Encoding(false));
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The lines as SubRip: a number, "00:00:01,500 --> 00:00:04,000", the words, an empty line.</summary>
    internal static string Srt(IReadOnlyList<(double From, double To, string Words)> lines)
    {
        static string Time(double seconds)
        {
            var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, seconds) * 1000));
            return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
        }
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
            text.Append(i + 1).Append("\r\n").Append(Time(lines[i].From)).Append(" --> ").Append(Time(lines[i].To)).Append("\r\n")
                .Append(lines[i].Words.ReplaceLineEndings("\r\n")).Append("\r\n\r\n");
        return text.ToString();
    }
}
