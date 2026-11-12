using System.Globalization;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Documents;

/// <summary>
/// How cells or text are to look, as a format line of edit_file says it in words: "bold, yellow fill", "red text,
/// centered", "size 20, dark blue, not bold". What it doesn't say stays as it is. One reading for a workbook's cells
/// (<see cref="SheetEdit"/>) and the text of a Word document or a slide (<see cref="TextEdit"/>).
/// </summary>
/// <param name="Fill">The background as RRGGBB, or "" for none.</param>
/// <param name="Colour">The text's colour as RRGGBB.</param>
/// <param name="Size">The text's size in points.</param>
/// <param name="Align">"left", "center" or "right".</param>
internal sealed partial record DocumentLook(bool? Bold, bool? Italic, bool? Underline, string? Fill, string? Colour, double? Size, string? Align, bool? Wrap, bool Border)
{
    public bool Any => Bold is not null || Italic is not null || Underline is not null || Fill is not null || Colour is not null || Size is not null || Align is not null || Wrap is not null || Border;

    /// <summary>"bold, fill #FFFF00": what was done, for the tool's answer.</summary>
    public string Said => string.Join(", ", new[]
    {
        Bold switch { true => "bold", false => "not bold", _ => null },
        Italic switch { true => "italic", false => "not italic", _ => null },
        Underline switch { true => "underlined", false => "not underlined", _ => null },
        Size is { } size ? $"size {size.ToString("0.#", CultureInfo.InvariantCulture)}" : null,
        Fill switch { null => null, "" => "no fill", var f => $"fill #{f}" },
        Colour is null ? null : $"text #{Colour}",
        Align,
        Wrap switch { true => "wrapped", false => "not wrapped", _ => null },
        Border ? "border" : null,
    }.OfType<string>());

    /// <summary>What can be asked for, for the error that says nothing was.</summary>
    public const string Words = "bold, italic, underlined, size 14, a fill or text colour by name or #RRGGBB (yellow fill, red text), left, center or right";

    /// <summary>The look <paramref name="what"/> asks for.</summary>
    /// <param name="text">
    /// What a colour with nothing said of it is: the text's (a title made "dark blue"), or (false) the fill, as for
    /// a workbook's cells and a table's rows ("format A1:E1 yellow").
    /// </param>
    public static DocumentLook Parse(string what, bool text = false)
    {
        var lone = text;
        what = " " + what.ToLowerInvariant().Replace(',', ' ').Replace(';', ' ').Replace(" and ", " ") + " ";
        bool? Flag(string word) => Regex.IsMatch(what, $@"\b(?:not|no|un)[\s-]?(?:{word})\b") ? false : Regex.IsMatch(what, $@"\b(?:{word})\b") ? true : null;
        string? fill = null, colour = null;
        if (NoFill().IsMatch(what))
            fill = "";
        // "yellow fill", "fill yellow", "background: #FFF2CC"; "red text", "text red", "font colour blue".
        foreach (Match c in ColourWord().Matches(what))
        {
            if (ColourOf(c.Groups["c"].Value) is not { } hex)
                continue;
            var before = what[..c.Index];
            var after = what[(c.Index + c.Length)..];
            var ofText = TextBefore().IsMatch(before) || TextAfter().IsMatch(after);
            var back = FillBefore().IsMatch(before) || FillAfter().IsMatch(after);
            if (ofText && !back || lone && !ofText && !back)
                colour ??= hex;
            else
                fill ??= hex;
        }
        double? size = SizeWord().Match(what) is { Success: true } s
            && double.TryParse(s.Groups["n"].Success ? s.Groups["n"].Value : s.Groups["m"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var points) && points is >= 1 and <= 400
            ? points : null;
        return new DocumentLook(Flag("bold"), Flag("italics?"), Flag("underlined?"), fill, colour, size,
            Regex.IsMatch(what, @"\bcent(?:er|re)(?:d|ed)?\b") ? "center" : Regex.IsMatch(what, @"\bright\b") ? "right" : Regex.IsMatch(what, @"\bleft\b") ? "left" : null,
            Flag("wrap(?:ped)?"), Regex.IsMatch(what, @"\bborders?\b") && !Regex.IsMatch(what, @"\bno\s+borders?\b"));
    }

    /// <summary>Whether <paramref name="what"/> is words of a look and nothing else: what tells "A1:B1 bold, light blue fill" from cells and their text.</summary>
    public static bool OnlyLook(string what) =>
        Parse(what).Any && LookWords().Replace(" " + what.ToLowerInvariant().Replace(',', ' ').Replace(';', ' ') + " ", " ").Trim().Length == 0;

    [GeneratedRegex(@"#[0-9a-f]{6}\b|\b(?:not|no|un|without|bold|italics?|underlined?|light|pale|dark|yellow|orange|red|green|blue|purple|pink|teal|brown|gr[ae]y|white|black|gold|navy|fill|background|bg|highlight(?:ed)?|shad(?:e|ed|ing)|text|font|letters?|colou?r|size|pt|points?|cent(?:er|re)(?:d|ed)?|left|right|align(?:ed)?|wrap(?:ped)?|borders?|none|and|with|in|of|a|the|[0-9]+(?:\.[0-9]+)?)\b|[:=-]")]
    private static partial Regex LookWords();

    /// <summary>A colour by its name, as Office's palette has it, or as #RRGGBB; null for a word that is none.</summary>
    public static string? ColourOf(string word)
    {
        var w = Regex.Replace(word.Trim().ToLowerInvariant(), @"\s+", " ").Replace("gray", "grey");
        if (Regex.Match(w, "^#?([0-9a-f]{6})$") is { Success: true } hex)
            return hex.Groups[1].Value.ToUpperInvariant();
        return w switch
        {
            "yellow" => "FFFF00", "light yellow" or "pale yellow" => "FFF2CC", "gold" => "FFD966",
            "orange" => "FFC000", "light orange" => "F8CBAD",
            "red" => "FF0000", "light red" => "FFC7CE", "dark red" => "C00000",
            "green" => "00B050", "light green" => "C6EFCE", "dark green" => "006100",
            "blue" => "0070C0", "light blue" => "BDD7EE", "dark blue" or "navy" => "002060",
            "purple" => "7030A0", "pink" => "FF99CC", "teal" => "00B0A0", "brown" => "7B3F00",
            "grey" => "BFBFBF", "light grey" => "D9D9D9", "dark grey" => "595959",
            "white" => "FFFFFF", "black" => "000000",
            _ => null,
        };
    }

    [GeneratedRegex(@"(?<![\w#])(?<c>#[0-9a-f]{6}|(?:(?:light|pale|dark)\s+)?(?:yellow|orange|red|green|blue|purple|pink|teal|brown|gr[ae]y|white|black)|gold|navy)(?![\w])")]
    private static partial Regex ColourWord();

    [GeneratedRegex(@"\b(?:no|without)\s+(?:fill|background|highlight|shading)\b|\b(?:fill|background|highlight|shading)\s*(?:colou?r\s*)?[:=]?\s*none\b")]
    private static partial Regex NoFill();

    [GeneratedRegex(@"\b(?:text|font|letters?)\s*(?:colou?r\s*)?(?:of\s+|in\s+|[:=]\s*)?$")]
    private static partial Regex TextBefore();

    [GeneratedRegex(@"^\s*(?:text|font|letters?)\b")]
    private static partial Regex TextAfter();

    [GeneratedRegex(@"\b(?:fill|background|bg|highlight(?:ed)?|shad(?:e|ed|ing))\s*(?:colou?r\s*)?(?:of\s+|in\s+|[:=]\s*)?$")]
    private static partial Regex FillBefore();

    [GeneratedRegex(@"^\s*(?:fill|background|bg|highlight|shading)\b")]
    private static partial Regex FillAfter();

    // "size 14", "font size: 20", "14 pt", "18 points"
    [GeneratedRegex(@"\b(?:font\s+)?size\s*(?:of\s+|[:=]\s*)?(?<n>[0-9]+(?:\.[0-9]+)?)\b|(?<![\w.#])(?<m>[0-9]+(?:\.[0-9]+)?)\s*(?:pt|points?)\b")]
    private static partial Regex SizeWord();
}
