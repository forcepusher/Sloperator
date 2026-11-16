// Draws the Auto card mockups as SVG at 1x (the app at 100%), with text measured by HarfBuzz in the app's own
// Inter, so wrapping and widths are what Avalonia would lay out. One board per concept.
using System.Globalization;
using System.Security;
using System.Text;

static class C
{
    public const string Frame = "#101010", Bg = "#161616", Bar = "#191919", Notice = "#1B1B1B", Surface = "#1E1E1E",
        Chip = "#2A2A2A", Button = "#252525", Line = "#232323", Border = "#2A2A2A", CtrlBorder = "#323232",
        Text = "#F0F0F0", Secondary = "#BBBBBB", Muted = "#A2A2A2", Faint = "#808080", Hint = "#606060", Code = "#E8E8E8",
        Accent = "#FCC21B", OnAccent = "#1A1A1A", Error = "#E06C6C", List = "#2B2B2B", ListBorder = "#0E0E0E", Picked = "#453E29";
}

sealed class Face
{
    private readonly HarfBuzzSharp.Font _font;
    private readonly int _upem;
    public readonly double Asc, Desc, Gap;
    public readonly string Family;
    public readonly int Weight;

    public Face(string path, string family, int weight)
    {
        Family = family;
        Weight = weight;
        var blob = HarfBuzzSharp.Blob.FromFile(path);
        var face = new HarfBuzzSharp.Face(blob, 0);
        _upem = face.UnitsPerEm;
        _font = new HarfBuzzSharp.Font(face);
        _font.SetScale(_upem, _upem);
        _font.TryGetHorizontalFontExtents(out var e);
        Asc = e.Ascender / (double)_upem;
        Desc = -e.Descender / (double)_upem;
        Gap = e.LineGap / (double)_upem;
    }

    public double Width(string s, double size)
    {
        if (s.Length == 0)
            return 0;
        using var buf = new HarfBuzzSharp.Buffer();
        buf.AddUtf16(s);
        buf.GuessSegmentProperties();
        _font.Shape(buf);
        double w = 0;
        foreach (var p in buf.GlyphPositions)
            w += p.XAdvance;
        return w / _upem * size;
    }

    /// <summary>A text block's line at this size, as Avalonia measures it (ascent + descent + gap).</summary>
    public double LineH(double size) => (Asc + Desc + Gap) * size;
}

sealed record St(Face F, double Size, string Color, bool Underline = false);

sealed record Run(string Text, St S);

sealed class Doc
{
    public readonly StringBuilder B = new();
    static string N(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
    public static string E(string s) => SecurityElement.Escape(s);

    public void Rect(double x, double y, double w, double h, string fill, double rx = 0, string? stroke = null, double opacity = 1, string? dash = null)
    {
        // A border of 1 is drawn inside the box, as Avalonia's.
        if (stroke is not null)
            B.Append($"<rect x='{N(x + 0.5)}' y='{N(y + 0.5)}' width='{N(w - 1)}' height='{N(h - 1)}' rx='{N(Math.Max(0, rx - 0.5))}' fill='{fill}' stroke='{stroke}' stroke-width='1'{(dash is null ? "" : $" stroke-dasharray='{dash}'")}{(opacity < 1 ? $" fill-opacity='{N(opacity)}'" : "")}/>");
        else
            B.Append($"<rect x='{N(x)}' y='{N(y)}' width='{N(w)}' height='{N(h)}' rx='{N(rx)}' fill='{fill}'{(opacity < 1 ? $" fill-opacity='{N(opacity)}'" : "")}/>");
    }

    /// <summary>A box rounded on its top corners only (an open foldout's header).</summary>
    public void TopRounded(double x, double y, double w, double h, double r, string fill, string stroke)
    {
        x += 0.5; y += 0.5; w -= 1; h -= 1; r -= 0.5;
        B.Append($"<path d='M{N(x)} {N(y + h)} V{N(y + r)} Q{N(x)} {N(y)} {N(x + r)} {N(y)} H{N(x + w - r)} Q{N(x + w)} {N(y)} {N(x + w)} {N(y + r)} V{N(y + h)} Z' fill='{fill}' stroke='{stroke}'/>");
    }

    public void BottomRounded(double x, double y, double w, double h, double r, string fill, string stroke)
    {
        x += 0.5; y += 0.5; w -= 1; h -= 1; r -= 0.5;
        B.Append($"<path d='M{N(x)} {N(y)} V{N(y + h - r)} Q{N(x)} {N(y + h)} {N(x + r)} {N(y + h)} H{N(x + w - r)} Q{N(x + w)} {N(y + h)} {N(x + w)} {N(y + h - r)} V{N(y)}' fill='{fill}' stroke='{stroke}'/>");
    }

    public void Text(double x, double baseline, string s, St st, string anchor = "start")
    {
        var fam = st.F.Family == "mono" ? "Consolas, monospace" : "InterMock, Inter, sans-serif";
        B.Append($"<text x='{N(x)}' y='{N(baseline)}' font-family='{fam}' font-size='{N(st.Size)}' font-weight='{st.F.Weight}' fill='{st.Color}'{(anchor == "start" ? "" : $" text-anchor='{anchor}'")}>{E(s)}</text>");
        if (st.Underline)
        {
            var w = st.F.Width(s, st.Size);
            var x0 = anchor == "end" ? x - w : anchor == "middle" ? x - w / 2 : x;
            Rect(x0, baseline + 1.5, w, 1, st.Color);
        }
    }

    public void Path(string d, string fill, double x, double y, double scale = 1, string? stroke = null, double strokeW = 0)
        => B.Append($"<path d='{d}' transform='translate({N(x)},{N(y)}) scale({N(scale)})' fill='{fill}'{(stroke is null ? "" : $" stroke='{stroke}' stroke-width='{N(strokeW)}' stroke-linecap='round' stroke-linejoin='round'")}/>");

    public void Line(double x1, double y1, double x2, double y2, string stroke, double w = 1)
        => B.Append($"<line x1='{N(x1)}' y1='{N(y1)}' x2='{N(x2)}' y2='{N(y2)}' stroke='{stroke}' stroke-width='{N(w)}'/>");

    public void Raw(string s) => B.Append(s);
}

static class Mock
{
    static Face Reg = null!, Semi = null!, Mono = null!;
    static St T14, Sec14, Muted12, Faint12, Sec12, Acc12, Mono12Acc, Code14, Link12, Link14, Title20, Body14Sec, OnAcc14, T14Semi, Label12, Muted14;
    static string Logo = "";

    public static void Run(string outDir, string fonts, string logoPath)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Reg = new Face(Path.Combine(fonts, "Inter-Regular.ttf"), "inter", 400);
        Semi = new Face(Path.Combine(fonts, "Inter-SemiBold.ttf"), "inter", 600);
        Mono = new Face(@"C:\Windows\Fonts\consola.ttf", "mono", 400);
        T14 = new(Reg, 14, C.Text); Sec14 = new(Reg, 14, C.Secondary); Muted12 = new(Reg, 12, C.Muted); Faint12 = new(Reg, 12, C.Faint);
        Sec12 = new(Reg, 12, C.Secondary); Acc12 = new(Reg, 12, C.Accent); Mono12Acc = new(Mono, 12, C.Accent); Code14 = new(Mono, 14, C.Code);
        Link12 = new(Reg, 12, C.Accent, true); Link14 = new(Reg, 14, C.Accent, true); Title20 = new(Semi, 20, C.Text); Body14Sec = new(Reg, 14, C.Secondary);
        OnAcc14 = new(Semi, 14, C.OnAccent); T14Semi = new(Semi, 14, C.Text); Label12 = new(Reg, 12, C.Faint); Muted14 = new(Reg, 14, C.Muted);
        Logo = Convert.ToBase64String(File.ReadAllBytes(logoPath));
        var fontCss = $"@font-face{{font-family:'InterMock';font-weight:400;src:url(data:font/ttf;base64,{Convert.ToBase64String(File.ReadAllBytes(Path.Combine(fonts, "Inter-Regular.ttf")))}) format('truetype');}}"
            + $"@font-face{{font-family:'InterMock';font-weight:600;src:url(data:font/ttf;base64,{Convert.ToBase64String(File.ReadAllBytes(Path.Combine(fonts, "Inter-SemiBold.ttf")))}) format('truetype');}}";
        Directory.CreateDirectory(outDir);
        foreach (var concept in new[] { 'A', 'B', 'C', 'D' })
        {
            var (svg, w, h) = Board(concept);
            var doc = $"<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='{w}' height='{h}' viewBox='0 0 {w} {h}'>"
                + $"<defs><style>{fontCss}</style><symbol id='blob' viewBox='0 0 256 256'><image width='256' height='256' href='data:image/png;base64,{Logo}'/></symbol></defs>"
                + svg + "</svg>";
            var file = Path.Combine(outDir, $"auto-card-{char.ToLowerInvariant(concept)}.svg");
            File.WriteAllText(file, doc, new UTF8Encoding(false));
            Console.WriteLine($"{file} {w}x{h}");
        }
    }

    // ---- Text layout ---------------------------------------------------------------------------

    static double W(Run r) => r.S.F.Width(r.Text, r.S.Size);
    static double W(string s, St st) => st.F.Width(s, st.Size);

    /// <summary>Breaks runs into lines no wider than <paramref name="max"/>, word by word, as a wrapping TextBlock does.</summary>
    static List<List<Run>> Wrap(IList<Run> runs, double max)
    {
        var words = new List<Run>();
        foreach (var r in runs)
        {
            var parts = r.Text.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                var t = parts[i] + (i < parts.Length - 1 ? " " : "");
                if (t.Length > 0)
                    words.Add(r with { Text = t });
            }
        }
        var lines = new List<List<Run>> { new() };
        double x = 0;
        foreach (var w in words)
        {
            var ww = W(w with { Text = w.Text.TrimEnd() });
            if (x > 0 && x + ww > max + 0.01)
            {
                lines.Add(new());
                x = 0;
            }
            lines[^1].Add(w);
            x += W(w);
        }
        return lines;
    }

    static double LineWidth(List<Run> line)
    {
        double w = 0;
        for (var i = 0; i < line.Count; i++)
            w += i == line.Count - 1 ? W(line[i] with { Text = line[i].Text.TrimEnd() }) : W(line[i]);
        return w;
    }

    /// <summary>
    /// Draws a wrapping paragraph from <paramref name="y"/> (its top); returns its height, whole pixels as
    /// Avalonia rounds a block. <paramref name="lh"/> null is the font's own line; a set one shares its spare room
    /// over and under the words.
    /// </summary>
    static double Para(Doc s, double x, double y, double max, IList<Run> runs, double? lh = null, string align = "start")
    {
        var lines = Wrap(runs, max);
        // Runs of other fonts share one baseline: the line is the tallest ascent over the deepest descent.
        var asc = runs.Max(r => r.S.F.Asc * r.S.Size);
        var natural = asc + runs.Max(r => (r.S.F.Desc + r.S.F.Gap) * r.S.Size);
        var step = lh ?? natural;
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = y + i * step + (step - natural) / 2 + asc;
            var lw = LineWidth(lines[i]);
            var cx = align == "end" ? x + max - lw : align == "middle" ? x + (max - lw) / 2 : x;
            foreach (var r in lines[i])
            {
                // Mono runs share the line's baseline.
                s.Text(cx, baseline, r.Text.TrimEnd(), r.S);
                cx += W(r);
            }
        }
        return Math.Ceiling(lines.Count * step - 0.01);
    }

    static double Para(Doc s, double x, double y, double max, string text, St st, double? lh = null, string align = "start")
        => Para(s, x, y, max, [new Run(text, st)], lh, align);

    static double Lines(IList<Run> runs, double max) => Wrap(runs, max).Count;

    /// <summary>One line of text in a box of the font's line height at <paramref name="top"/>; the baseline it took.</summary>
    static double One(Doc s, double x, double top, string text, St st, string anchor = "start")
    {
        var baseline = top + st.F.Asc * st.Size;
        s.Text(x, baseline, text, st, anchor);
        return baseline;
    }

    static double LH(St st) => Math.Ceiling(st.F.LineH(st.Size) - 0.01);

    // ---- Pieces of the app ----------------------------------------------------------------------

    const string Sparkle = "M9 1.5l1.9 4.6 4.6 1.9-4.6 1.9L9 14.5l-1.9-4.6L2.5 8l4.6-1.9z";

    /// <summary>The Switch card's sparkle as a PathIcon of <paramref name="size"/> draws it (its 13-wide shape stretched to fill).</summary>
    static void SparkleIcon(Doc s, double x, double y, double size, string fill = C.Accent)
    {
        var k = size / 13.0;
        s.Raw($"<path d='{Sparkle}' transform='translate({x - 2.5 * k:0.##},{y - 1.5 * k:0.##}) scale({k:0.####})' fill='{fill}'/>");
    }

    static void Chevron(Doc s, double cx, double cy, bool up, string color = C.Secondary)
    {
        var d = up ? $"M{cx - 5:0.##} {cy + 2.5:0.##} L{cx:0.##} {cy - 2.5:0.##} L{cx + 5:0.##} {cy + 2.5:0.##}" : $"M{cx - 5:0.##} {cy - 2.5:0.##} L{cx:0.##} {cy + 2.5:0.##} L{cx + 5:0.##} {cy - 2.5:0.##}";
        s.Raw($"<path d='{d}' fill='none' stroke='{color}' stroke-width='1.25' stroke-linecap='round' stroke-linejoin='round'/>");
    }

    /// <summary>A menu button's arrow (M7 10l5 5 5-5z, 10 wide), down or up.</summary>
    static void Arrow(Doc s, double x, double cy, bool up, string color = C.Text)
    {
        var d = up ? $"M{x} {cy + 2.5} l5 -5 l5 5 z" : $"M{x} {cy - 2.5} l5 5 l5 -5 z";
        s.Raw($"<path d='{d}' fill='{color}'/>");
    }

    static double Chip(Doc s, double x, double y, string text)
    {
        var w = 16 + W(text, Mono12Acc);
        var h = 2 + LH(Mono12Acc);
        s.Rect(x, y, w, h, C.Chip, 6);
        One(s, x + 8, y + 2, text, Mono12Acc);
        return w;
    }

    /// <summary>A button 32 high (24 small), its words centred; returns its width.</summary>
    static double Btn(Doc s, double x, double y, string text, string kind, bool small = false, bool arrow = false, bool up = false, double? width = null)
    {
        var st = kind == "primary" ? OnAcc14 : kind == "danger" ? T14 : T14;
        var h = small ? 24 : 32;
        var w = width ?? 24 + W(text, st) + (arrow ? 31 : 0) + 2;
        if (kind == "primary")
            s.Rect(x, y, w, h, C.Accent, 8);
        else if (kind == "danger")
            s.Rect(x, y, w, h, C.Error, 8);
        else
            s.Rect(x, y, w, h, C.Button, 8, C.CtrlBorder);
        var top = y + (h - LH(st)) / 2;
        if (arrow)
        {
            One(s, x + 13, top, text, st);
            Arrow(s, x + w - 1 - 9 - 10, y + h / 2.0, up);
        }
        else
            One(s, x + w / 2, top, text, st, "middle");
        return w;
    }

    static double Card(Doc s, double x, double y, double w, double h)
    {
        s.Rect(x, y, w, h, C.Surface, 12, C.Border);
        return h;
    }

    static double UserMsg(Doc s, double x, double y, double w, string time, string text)
    {
        var right = x + w;
        One(s, right, y, time, Muted12, "end");
        var top = y + LH(Muted12) + 3;
        var max = 640 - 24;
        var runs = new List<Run> { new(text, T14) };
        var lines = Wrap(runs, Math.Min(max, w - 60 - 24));
        var tw = lines.Max(LineWidth);
        var bw = Math.Ceiling(tw) + 24;
        var bh = 16 + lines.Count * LH(T14);
        s.Rect(right - bw, top, bw, bh, "#252525", 12);
        Para(s, right - bw + 12, top + 8, bw - 24 + 1, runs);
        return top + bh - y;
    }

    static double Reply(Doc s, double x, double y, double w, string time, string text)
    {
        One(s, x, y, time, Muted12);
        var top = y + LH(Muted12) + 6;
        var h = Para(s, x, top, w - 40, [new Run(text, T14)], 21);
        return top + h - y;
    }

    /// <summary>A finished run's fold card (WorkFold), its steps shown or not.</summary>
    static double Fold(Doc s, double x, double y, double w, string title, bool open)
    {
        var cw = w - 40;
        Card(s, x, y, cw, 58);
        var bw = 24 + Math.Max(W("Show steps", T14), W("Hide steps", T14)) + 31 + 2;
        Btn(s, x + cw - 13 - bw, y + 13, open ? "Hide steps" : "Show steps", "ghost", arrow: true, up: open, width: bw);
        One(s, x + 13, y + 13 + (32 - LH(T14)) / 2 - 2 + 1, title, T14);
        return 58;
    }

    /// <summary>A foldout as the cards have it: header 34 high (words 17 in, secondary), open with a body under it.</summary>
    static double Foldout(Doc s, double x, double y, double w, string header, bool open, Func<Doc, double, double, double, double>? body = null, string? right = null)
    {
        const double hh = 34;
        if (!open)
        {
            s.Rect(x, y, w, hh, C.Bg, 8, C.Border);
            One(s, x + 17, y + (hh - LH(Sec14)) / 2, header, Sec14);
            if (right is not null)
                One(s, x + w - 44, y + (hh - LH(Muted12)) / 2, right, Muted12, "end");
            Chevron(s, x + w - 24, y + hh / 2, false);
            return hh;
        }
        // The body first, so the header's border lies over its top line.
        var bodyTop = y + hh - 1;
        var probe = new Doc();
        var bh = body!(probe, x + 9, bodyTop + 5, w - 18) + 10;
        s.BottomRounded(x, bodyTop, w, bh, 8, C.Bg, C.Border);
        s.B.Append(probe.B);
        s.TopRounded(x, y, w, hh, 8, C.Bg, C.Border);
        One(s, x + 17, y + (hh - LH(Sec14)) / 2, header, Sec14);
        if (right is not null)
            One(s, x + w - 44, y + (hh - LH(Muted12)) / 2, right, Muted12, "end");
        Chevron(s, x + w - 24, y + hh / 2, true);
        return hh - 1 + bh;
    }

    static readonly string Why1 = "The user is asking a planning question about how to split the settings work into steps, which needs some reasoning across several files but no deep architecture changes, so the daily driver at medium effort fits.";
    static readonly string Why2 = "This asks for a redesign across the settings store, view model and tests, with a migration to keep old files working, which is the deep, many-file kind of change the complex task model is for.";

    static List<Run>[] Notes() =>
    [
        [new("Settings live in ", T14), new("src/Settings/SettingsStore.cs", Code14), new("; the view model is ", T14), new("SettingsViewModel.cs", Code14), new(" (about 900 lines).", T14)],
        [new("Tests for them are in ", T14), new("tests/SettingsTests.cs", Code14), new(", 42 of them.", T14)],
        [new("docs/notes.md", Code14), new(" has a section on how settings are saved.", T14)],
    ];

    /// <summary>The helper's notes as a Markdown list, 20 a line; returns the height.</summary>
    static double NoteList(Doc s, double x, double y, double w)
    {
        double h = 0;
        foreach (var item in Notes())
        {
            One(s, x, y + h + 1.5, "•", T14);
            h += Para(s, x + 11, y + h, w - 11, item, 20);
        }
        return h;
    }

    // ---- The switch question over the message box, and the box -------------------------------------

    static double SwitchCard(Doc s, double x, double y, double w, string? why = null)
    {
        var text = "Switch to claude-opus-5-5 at High for this? It costs more than claude-sonnet-5-5.";
        var inner = w - 28 - 2;
        var tx = x + 15 + 18 + 10;
        var tw = inner - 28;
        var probe = new Doc();
        var th = Math.Max(18, Para(probe, tx, 0, tw, text, T14));
        double whyH = 0;
        if (why is not null)
            whyH = 4 + Para(probe, tx, 0, tw, why, Sec12, 16);
        var h = 1 + 12 + th + whyH + 10 + 32 + 10 + 2 + 1;
        Card(s, x, y, w, h);
        var top = y + 13;
        SparkleIcon(s, x + 15, top + 1, 18);
        Para(s, tx, top + (th > 18 ? 0 : (18 - LH(T14)) / 2), tw, text, T14);
        if (why is not null)
            Para(s, tx, top + th + 4, tw, why, Sec12, 16);
        var row = top + th + whyH + 10;
        var sw = W("Switch", OnAcc14) + 26;
        Btn(s, x + w - 15 - sw, row, "Switch", "primary", width: sw);
        var stay = "Stay on claude-sonnet-5-5";
        var stw = W(stay, T14) + 26;
        Btn(s, x + w - 15 - sw - 8 - stw, row, stay, "ghost", width: stw);
        One(s, x + 15 + 28, row + (32 - LH(Muted12)) / 2, "Switches in 7 s", Muted12);
        var bar = row + 32 + 10;
        s.Rect(x + 1, bar, w - 2, 2, C.Chip);
        s.Rect(x + 1, bar, (w - 2) * 0.7, 2, C.Accent);
        return h;
    }

    static double Composer(Doc s, double x, double y, double w, bool busy)
    {
        s.Rect(x, y, w, 50, C.Surface, 12, C.CtrlBorder);
        // The paperclip (flat icon button), the words, then Stop, the blob and Send from the right.
        s.Raw($"<path d='M16.5 6v11.5c0 2.21-1.79 4-4 4s-4-1.79-4-4V5c0-1.38 1.12-2.5 2.5-2.5s2.5 1.12 2.5 2.5v10.5c0 .55-.45 1-1 1s-1-.45-1-1V6H10v9.5c0 1.38 1.12 2.5 2.5 2.5s2.5-1.12 2.5-2.5V5c0-2.21-1.79-4-4-4S7 2.79 7 5v12.5c0 3.04 2.46 5.5 5.5 5.5s5.5-2.46 5.5-5.5V6h-1.5z' transform='translate({x + 15},{y + 15}) scale(0.8333)' fill='{C.Muted}'/>");
        One(s, x + 55, y + (50 - LH(T14)) / 2, "Type a task, paste a screenshot or drop files (Shift+Enter: new line)", new St(Reg, 14, C.Faint));
        var sendW = W("Send", OnAcc14) + 26;
        var sx = x + w - 9 - sendW;
        s.Rect(sx, y + 9, sendW, 32, C.Button, 8, C.CtrlBorder);
        One(s, sx + sendW / 2, y + 9 + (32 - LH(T14)) / 2, "Send", new St(Semi, 14, C.Faint), "middle");
        var bx = sx - 8 - 32;
        s.Raw($"<use href='#blob' x='{bx}' y='{y + 9}' width='32' height='32'/>");
        if (busy)
        {
            var stopW = W("Stop", T14) + 26;
            var stx = bx - 8 - stopW;
            s.Rect(stx, y + 9, stopW, 32, C.Error, 8);
            One(s, stx + stopW / 2, y + 9 + (32 - LH(T14)) / 2, "Stop", T14, "middle");
        }
        return 50;
    }

    // ---- The concepts ------------------------------------------------------------------------------

    enum State { Picking, Picked, Wants }

    /// <summary>A: today's card, said plainly. The line says what happened; the reason wraps under it.</summary>
    static double CardA(Doc s, double x, double y, double w, State state)
    {
        var cw = w - 40;
        var ix = x + 13;
        var iw = cw - 26;
        var probe = new Doc();
        double Inside(Doc t)
        {
            var top = y + 13;
            var chip = Chip(t, ix, top, "Auto");
            var status = state switch { State.Picking => "Reading SettingsStore.cs", State.Wants => "Waiting for your answer", _ => "Picked in 1.3 s" };
            var line = state switch
            {
                State.Picking => "claude-haiku-5-5 is picking the model for this message…",
                State.Wants => "Wants claude-opus-5-5 at High for this message",
                _ => "Picked claude-sonnet-5-5 at Medium for this message",
            };
            One(t, ix + iw, top, status, Muted12, "end");
            One(t, ix + chip + 8, top, line, T14);
            var h = LH(T14);
            if (state == State.Picking)
                return h;
            h += 8;
            h += Para(t, ix, top + h, iw, state == State.Wants ? Why2 : Why1, Sec14, 20);
            if (state == State.Wants)
                return h;
            h += 8;
            h += Foldout(t, ix, top + h, iw, "Notes it passed to claude-sonnet-5-5", true, (b, bx, by, bw) =>
            {
                return NoteList(b, bx + 2, by, bw - 2);
            });
            return h;
        }
        var ih = Inside(probe);
        var ch = 26 + ih;
        Card(s, x, y, cw, ch);
        Inside(s);
        return ch;
    }

    /// <summary>B: the card shows the three Auto models, the picked one lit.</summary>
    static double CardB(Doc s, double x, double y, double w, State state)
    {
        var cw = w - 40;
        var ix = x + 13;
        var iw = cw - 26;
        double Inside(Doc t)
        {
            var top = y + 13;
            SparkleIcon(t, ix, top + 0.5, 16);
            var status = state switch { State.Picking => "Reading SettingsStore.cs", State.Wants => "Waiting for your answer", _ => "Picked in 1.3 s" };
            var line = state switch
            {
                State.Picking => "claude-haiku-5-5 is picking one of your three models…",
                State.Wants => "Auto wants a stronger model for this message",
                _ => "Auto picked the daily driver for this message",
            };
            One(t, ix + iw, top, status, Muted12, "end");
            One(t, ix + 16 + 10, top, line, T14);
            var h = LH(T14) + 12;
            // The three models, as Settings names them.
            var bw = (iw - 16) / 3;
            var boxes = new[] { ("Helper", "claude-haiku-5-5"), ("Daily driver", "claude-sonnet-5-5"), ("Complex task", "claude-opus-5-5") };
            var bh = 8 + LH(Muted12) + 2 + LH(T14) + 2 + LH(Muted12) + 8;
            for (var i = 0; i < 3; i++)
            {
                var bx = ix + i * (bw + 8);
                var by = top + h;
                var picked = state == State.Picked && i == 1 || state == State.Wants && i == 2;
                var inUse = state == State.Wants && i == 1;
                var below = state == State.Wants && i == 0;
                if (picked)
                {
                    t.Rect(bx, by, bw, bh, C.Accent, 8, null, 0.10);
                    t.Rect(bx, by, bw, bh, "none", 8, C.Accent, 1, state == State.Wants ? "4 3" : null);
                }
                else
                    t.Rect(bx, by, bw, bh, C.Bar, 8, C.Border);
                var label = boxes[i].Item1;
                var labelSt = picked ? Acc12 : below ? new St(Reg, 12, C.Hint) : Faint12;
                One(t, bx + 13, by + 8, label, labelSt);
                if (picked)
                    SparkleIcon(t, bx + bw - 13 - 12, by + 9, 12);
                var nameSt = picked ? T14 : inUse ? Sec14 : below ? new St(Reg, 14, C.Faint) : Muted14;
                One(t, bx + 13, by + 8 + LH(Muted12) + 2, boxes[i].Item2, nameSt);
                var third = picked ? (state == State.Wants ? "High effort" : "Medium effort") : inUse ? "In use now, Medium effort" : null;
                if (third is not null)
                    One(t, bx + 13, by + 8 + LH(Muted12) + 2 + LH(T14) + 2, third, picked ? Sec12 : below ? new St(Reg, 12, C.Hint) : Muted12);
            }
            h += bh;
            if (state == State.Picking)
                return h;
            h += 12;
            h += Para(t, ix, top + h, iw, [new Run("Why: ", Muted14), new Run(state == State.Wants ? Why2 : Why1, Sec14)], 20);
            if (state == State.Wants)
                return h;
            h += 12;
            h += Foldout(t, ix, top + h, iw, "Notes it passed to claude-sonnet-5-5", false, right: "3 notes");
            return h;
        }
        var ih = Inside(new Doc());
        var ch = 26 + ih;
        Card(s, x, y, cw, ch);
        Inside(s);
        return ch;
    }

    /// <summary>C: no card; a line under the user's message names the pick, Why opens the rest.</summary>
    static double LineC(Doc s, double x, double y, double w, State state)
    {
        var right = x + w;
        var top = y;
        var runs = state switch
        {
            State.Picking => new List<Run> { new("Auto is picking a model for this message…", Muted12) },
            State.Wants => [new("Auto wants ", Muted12), new("claude-opus-5-5 at High", Sec12), new(" for this", Muted12)],
            _ => [new("Auto: ", Muted12), new("claude-sonnet-5-5 at Medium", Sec12)],
        };
        double lw = runs.Sum(W);
        var why = state == State.Picking ? null : "Why?";
        var ww = why is null ? 0 : W(" · ", Muted12) + W(why, Link12);
        var lx = right - ww - lw;
        SparkleIcon(s, lx - 6 - 12, top + 1, 12);
        var cx = lx;
        foreach (var r in runs)
        {
            One(s, cx, top, r.Text, r.S);
            cx += W(r);
        }
        if (why is not null)
        {
            One(s, cx, top, " · ", Muted12);
            cx += W(" · ", Muted12);
            One(s, cx, top, why, Link12);
        }
        return LH(Muted12);
    }

    /// <summary>C's popup under the line: the pick, why, the notes, and what Auto is.</summary>
    static double PopupC(Doc s, double right, double y, double w)
    {
        var x = right - w;
        var iw = w - 2 - 24;
        double Inside(Doc t)
        {
            var ix = x + 13;
            var top = y + 13;
            double h = 0;
            h += Para(t, ix, top + h, iw, [new Run("claude-sonnet-5-5 at Medium", T14Semi)]);
            h += 6;
            h += Para(t, ix, top + h, iw, Why1, Sec14, 20);
            h += 12;
            t.Rect(x + 1, top + h, w - 2, 1, C.CtrlBorder);
            h += 1 + 12;
            h += Para(t, ix, top + h, iw, "Notes it passed on with your message", Muted12);
            h += 6;
            h += NoteList(t, ix, top + h, iw);
            h += 12;
            t.Rect(x + 1, top + h, w - 2, 1, C.CtrlBorder);
            h += 1 + 12;
            h += Para(t, ix, top + h, iw, "Picked by claude-haiku-5-5 in 1.3 s. On Auto, each message gets one of the three models in Settings > Advanced.", Muted12, 16);
            return h;
        }
        var ih = Inside(new Doc());
        var ph = 26 + ih;
        // A soft shadow as Fluent's popups have, then the list's colours (the model dropdown's).
        s.Raw($"<rect x='{x}' y='{y + 4}' width='{w}' height='{ph}' rx='8' fill='#000000' fill-opacity='0.35' filter='url(#soft)'/>");
        s.Rect(x, y, w, ph, C.List, 8, C.ListBorder);
        Inside(s);
        return ph;
    }

    /// <summary>D: a note in Sloperator's voice, as its other notes in the chat (Border.notice), quoting the helper.</summary>
    static double NoteD(Doc s, double x, double y, double w, State state, bool open)
    {
        var timeW = W("18:08:57", Muted12);
        List<Run> runs = state switch
        {
            State.Picking => [new("claude-haiku-5-5 is looking at your message to pick the model for it…", Sec12)],
            State.Wants => [new("Auto would move this message up to ", Sec12), new("claude-opus-5-5 at High", new St(Semi, 12, C.Text)), new(". claude-haiku-5-5, which picks for Auto, said: “" + Why2 + "”", Sec12)],
            _ => [new("Auto gave this message to ", Sec12), new("claude-sonnet-5-5 at Medium", new St(Semi, 12, C.Text)), new(". claude-haiku-5-5, which picks for Auto, said: “" + Why1 + "”", Sec12)],
        };
        var hasButton = state == State.Picked;
        var bw = hasButton ? 24 + W("Its notes", T14) + 31 + 2 : 0;
        // Laid out as the notice: 2,6 padding, time 8 in, words with 8,4 of their own, the button 4 left of 8 right.
        var textMax = w - 4 - 8 - timeW - 16 - (hasButton ? bw + 12 : 0);
        var lines = Wrap(runs, textMax);
        var textW = lines.Max(LineWidth);
        var full = lines.Count > 1;
        var nw = full ? w : 4 + 8 + timeW + 16 + Math.Ceiling(textW) + (hasButton ? bw + 12 : 0);
        var nx = x + (w - nw) / 2;
        var th = lines.Count * 16;
        double notesH = 0;
        var probe = new Doc();
        if (open)
            notesH = 8 + NoteList(probe, 0, 0, nw - 4 - 32) + 4;
        var nh = 12 + 8 + Math.Max(th, hasButton ? 24 : 0) + notesH;
        s.Rect(nx, y, nw, nh, C.Notice, 8, C.Border);
        var top = y + 6 + 4 + (hasButton && th < 24 ? (24 - th) / 2 : 0);
        One(s, nx + 2 + 8, top, "18:08:57", Muted12);
        Para(s, nx + 2 + 8 + timeW + 8, top, textMax, runs, 16);
        if (hasButton)
        {
            var bx = nx + nw - 2 - 8 - bw;
            Btn(s, bx, y + 6 + 4 + (Math.Max(th, 24) - 24) / 2, "Its notes", "ghost", small: true, arrow: true, up: open, width: bw);
        }
        if (open)
        {
            var ny = y + 10 + Math.Max(th, 24) + 8;
            s.Rect(nx + 1, ny - 4, nw - 2, 1, C.Border);
            NoteList(s, nx + 2 + 8 + timeW + 8, ny + 4, nw - (2 + 8 + timeW + 8) - 16);
        }
        return nh;
    }

    // ---- Boards ---------------------------------------------------------------------------------------

    const double Pad = 40, WideW = 900, NarrowW = 660, Gap = 40;

    static (string Doc, int W, int H) Board(char concept)
    {
        var s = new Doc();
        var bw = Pad + WideW + Gap + NarrowW + Pad;
        var (title, about) = concept switch
        {
            'A' => ("A. Say it plainly", "Today's card, with a line that says what happened and the helper's reason on lines of its own under it, so none of it is cut. Its notes keep their foldout, named for the model they go to. The least change."),
            'B' => ("B. Show the three models", "The card shows your three Auto models, as Settings names them, with the picked one lit and its effort under it, so the card itself explains what Auto chooses between. The reason wraps under them; the notes fold away."),
            'C' => ("C. A line under your message", "No card: one short line under your message names the model it went to, and stays when the run's steps are folded. Why opens a popup with the reason, the notes and what Auto is. Moving up, the reason goes on the Switch card."),
            _ => ("D. Sloperator says it", "A note in Sloperator's own voice, as its other notes in the chat are, that names the pick and quotes the helper, so the helper's \"The user is asking…\" reads as a quote. Its notes open under it."),
        };
        var body = new Doc();
        double y = 36;
        y += Para(body, Pad, y, bw - 2 * Pad, [new Run(title, Title20)]);
        y += 8;
        y += Para(body, Pad, y, 1180, about, Body14Sec, 20);
        y += 28;
        var colTop = y;

        // Left column: the window at its default size.
        double ly = colTop;
        ly += Slice(body, Pad, ly, WideW, concept == 'C' ? "Picked, Why opened" : "Picked, its steps shown", concept, State.Picked, false);
        ly += 28;
        ly += Slice(body, Pad, ly, WideW, "While it picks", concept, State.Picking, false);
        ly += 28;
        ly += Slice(body, Pad, ly, WideW, "Later in the chat, moving up a model (the Switch card over the message box)", concept, State.Wants, false);

        // Right column: the narrowest window.
        double ry = colTop;
        ry += Slice(body, Pad + WideW + Gap, ry, NarrowW, concept == 'C' ? "Why opened, in the narrowest window (660)" : "Picked, in the narrowest window (660)", concept, State.Picked, true);
        if (concept is 'C' or 'D')
        {
            ry += 28;
            ry += Slice(body, Pad + WideW + Gap, ry, NarrowW, concept == 'C' ? "Picked, the run's steps folded: the line stays" : "Its notes opened", concept, State.Picked, true, alt: true);
        }

        var h = Math.Max(ly, ry) + Pad;
        s.Rect(0, 0, bw, h, C.Frame);
        s.Raw("<defs><filter id='soft' x='-10%' y='-10%' width='120%' height='130%'><feGaussianBlur stdDeviation='6'/></filter></defs>");
        s.B.Append(body.B);
        return (s.B.ToString(), (int)bw, (int)Math.Ceiling(h));
    }

    /// <summary>A slice of the chat as the window shows it: its label over it; returns its height with the label.</summary>
    static double Slice(Doc s, double x, double y, double sw, string label, char concept, State state, bool narrow, bool alt = false)
    {
        One(s, x, y, label, Faint12);
        var top = y + LH(Faint12) + 8;
        var chatW = Math.Min(860, sw - 32);
        var cx = x + (sw - chatW) / 2;
        var c = new Doc();
        double cy = top + 12;
        double over = 0;
        if (state == State.Wants)
        {
            cy += UserMsg(c, cx, cy, chatW, "18:12:40", "Now redo the whole settings architecture carefully, with a migration for old files.");
            cy += concept == 'C' ? 6 : 12;
            cy += Element(c, cx, cy, chatW, concept, state, alt);
            // The box at the window's foot, the question over it.
            var probe = new Doc();
            var qh = SwitchCard(probe, cx, 0, chatW, concept == 'C' ? Why2 : null);
            var foot = cy + 40;
            SwitchCard(c, cx, foot, chatW, concept == 'C' ? Why2 : null);
            Composer(c, cx, foot + qh + 8, chatW, true);
            cy = foot + qh + 8 + 50 + 16;
        }
        else
        {
            cy += UserMsg(c, cx, cy, chatW, "18:08:57", "How should I split the settings rework into steps?");
            if (concept == 'C')
            {
                cy += 6;
                var lineTop = cy;
                cy += Element(c, cx, cy, chatW, concept, state, alt);
                if (state == State.Picked)
                {
                    cy += 12;
                    cy += Fold(c, cx, cy, chatW, "Worked for 2.1 s · claude-sonnet-5-5 at Medium", false);
                    cy += 12;
                    cy += Reply(c, cx, cy, chatW, "18:08:58 · model 941 ms", "I'd split it into three steps: the store first, then the view model, then the tests. Want me to start on the store?");
                    if (!alt)
                    {
                        // The popup Why opened, under the line, over the chat.
                        var pw = Math.Min(460, chatW - 40);
                        over = PopupC(new Doc(), cx + chatW, lineTop + LH(Muted12) + 6, pw);
                        var end = lineTop + LH(Muted12) + 6 + over;
                        var popup = new Doc();
                        PopupC(popup, cx + chatW, lineTop + LH(Muted12) + 6, pw);
                        cy = Math.Max(cy, end);
                        c.B.Append(popup.B);
                    }
                }
            }
            else
            {
                cy += 12;
                if (state == State.Picked)
                {
                    cy += Fold(c, cx, cy, chatW, "Worked for 2.1 s · claude-sonnet-5-5 at Medium", true);
                    cy += 12;
                }
                cy += Element(c, cx, cy, chatW, concept, state, alt);
                if (state == State.Picked)
                {
                    cy += 12;
                    cy += Reply(c, cx, cy, chatW, "18:08:58 · model 941 ms", "I'd split it into three steps: the store first, then the view model, then the tests. Want me to start on the store?");
                }
            }
            cy += 16;
        }
        var sh = cy - top;
        s.Rect(x, top, sw, sh, C.Bg);
        s.Rect(x - 1, top - 1, sw + 2, sh + 2, "none", 0, C.Line);
        s.B.Append(c.B);
        return top + sh - y;
    }

    static double Element(Doc s, double x, double y, double w, char concept, State state, bool alt) => concept switch
    {
        'A' => CardA(s, x, y, w, state),
        'B' => CardB(s, x, y, w, state),
        'C' => LineC(s, x, y, w, state),
        _ => NoteD(s, x, y, w, state, alt),
    };
}
