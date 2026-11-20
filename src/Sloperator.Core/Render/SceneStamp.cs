using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sloperator.Core.Render;

/// <summary>The scene a picture was drawn from: its file, whether it is an SVG or a page, and its text as it was then.</summary>
/// <param name="File">The scene's full path when it was drawn (it may have changed or gone since).</param>
/// <param name="Svg">An SVG; else an HTML page (a Three.js scene).</param>
/// <param name="Text">The scene's own text as it was drawn; empty when it was too long to keep.</param>
/// <param name="At">The moment of a moving scene the picture shows, in seconds.</param>
public sealed record SceneSource(string File, bool Svg, string Text, double At = 0)
{
    /// <summary>"SVG" or "Three.js page".</summary>
    public string Kind => Svg ? "SVG" : "Three.js page";

    /// <summary>".svg" or ".html".</summary>
    public string Extension => Svg ? ".svg" : ".html";
}

/// <summary>
/// A PNG <see cref="SceneRenderer"/> draws says what it was drawn from, as ComfyUI's pictures keep their
/// workflow: the scene's path and its text in a text chunk of the file itself ("sloperator_scene"), so a
/// version in the Image editor can be changed by changing its scene, long after the scene's file was
/// written over or cleared from the scratch folder.
/// </summary>
public static class SceneStamp
{
    /// <summary>The text chunk's keyword.</summary>
    public const string Key = "sloperator_scene";

    /// <summary>The longest scene kept in its picture: an SVG or a page is kilobytes; one with a picture inlined isn't kept.</summary>
    public const int MaxText = 512 * 1024;

    /// <summary>The PNG with the scene written into it, in a text chunk after its header; the PNG as it is when it isn't one.</summary>
    public static byte[] Stamp(byte[] png, SceneSource scene)
    {
        // The signature, then IHDR: 8 + (4 length + 4 type + 13 data + 4 CRC).
        const int afterHeader = 8 + 25;
        if (png.Length < afterHeader || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
            return png;
        var json = new JsonObject
        {
            ["file"] = scene.File,
            ["kind"] = scene.Svg ? "svg" : "page",
            ["at"] = scene.At,
            ["text"] = scene.Text.Length <= MaxText ? scene.Text : "",
        }.ToJsonString();
        // iTXt: keyword, 0, not compressed (0, 0), no language (0), no translated keyword (0), then UTF-8.
        var data = (byte[])[.. Encoding.Latin1.GetBytes(Key), 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(json)];
        var type = "iTXt"u8;
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        type.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc(chunk.AsSpan(4, 4 + data.Length)));
        return [.. png.AsSpan(0, afterHeader), .. chunk, .. png.AsSpan(afterHeader)];
    }

    /// <summary>The scene a PNG's text chunks name; null when they name none.</summary>
    public static SceneSource? Read(IReadOnlyDictionary<string, string> texts)
    {
        if (!texts.TryGetValue(Key, out var json))
            return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o || (string?)o["file"] is not { Length: > 0 } file)
                return null;
            return new SceneSource(file, (string?)o["kind"] != "page", (string?)o["text"] ?? "", o["at"] is JsonValue at && at.TryGetValue<double>(out var t) ? t : 0);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The scene the picture at <paramref name="path"/> was drawn from; null for one that says none, or that can't be read.</summary>
    public static SceneSource? Of(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The chunk is right after the header: the scene's text and a little more is all that is read.
            var head = new byte[(int)Math.Min(file.Length, 4 << 20)];
            file.ReadExactly(head);
            return Read(Media.MediaRecipe.PngTexts(head));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>A PNG chunk's CRC-32, over its type and data.</summary>
    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFF;
        foreach (var b in bytes)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
