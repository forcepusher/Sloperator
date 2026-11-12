using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Sloperator.Core.Tools;

/// <summary>
/// The text of the documents people hand over most, for read_file: PDF (its text layer, page by
/// page), Word (.docx, with headings, lists, **bold**, tables, headers, footers and comments), Excel
/// (.xlsx, cell by cell with the formulas, merged cells and charts) and PowerPoint (.pptx, slide by
/// slide with the speaker notes). Without it a PDF took qwen3.8 eight minutes of inflating streams
/// and mapping glyphs by hand in PowerShell.
/// </summary>
internal static class DocumentText
{
    public static bool Handles(string path) => Kind(path) is not null;

    private static string? Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "pdf",
        ".docx" or ".docm" or ".dotx" => "docx",
        ".xlsx" or ".xlsm" or ".xltx" => "xlsx",
        ".pptx" or ".pptm" or ".potx" => "pptx",
        _ => null,
    };

    /// <summary>The document as lines of text; throws <see cref="InvalidDataException"/> when it can't be read.</summary>
    /// <param name="ct">Stop: looked at page by page (a PDF of a thousand pages takes minutes) and row by row.</param>
    public static string Read(string path, CancellationToken ct = default)
    {
        // Shared for writing too: Excel and Word keep an open file locked against plain readers.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return Kind(path) switch
            {
                "pdf" => Pdf(file, ct),
                "docx" => Docx(file, ct),
                "xlsx" => Xlsx(file, ct),
                _ => Pptx(file, ct),
            };
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            // A damaged file, one that is password-protected, or not what its name says.
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be read as a document: {ex.Message}", ex);
        }
    }

    // ---- PDF -------------------------------------------------------------------------------------

    /// <summary>A PDF's text layer, page by page; also what fetch_page shows of a PDF on the web.</summary>
    internal static string Pdf(Stream file, CancellationToken ct = default)
    {
        using var pdf = PdfDocument.Open(file);
        var sb = new StringBuilder();
        var withText = 0;
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            sb.Append("--- page ").Append(page.Number).Append(" of ").Append(pdf.NumberOfPages).AppendLine(" ---");
            if (text.Length == 0)
                sb.AppendLine("(no text on this page: a scan or a picture)");
            else
            {
                sb.AppendLine(text);
                withText++;
            }
        }
        return withText > 0 ? sb.ToString()
            : $"(This PDF has no text layer: its {pdf.NumberOfPages} page(s) are pictures, such as a scan. Look at it on screen, or use an OCR tool.)";
    }

    // ---- Word ------------------------------------------------------------------------------------

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Docx(Stream file, CancellationToken ct)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var body = Load(zip, "word/document.xml").Root?.Element(W + "body") ?? throw new InvalidDataException("no document body");
        var sb = new StringBuilder();
        foreach (var block in body.Elements())
        {
            ct.ThrowIfCancellationRequested();
            if (block.Name == W + "p")
                sb.AppendLine(Paragraph(block));
            else if (block.Name == W + "tbl")
                foreach (var row in block.Elements(W + "tr"))
                    sb.Append("| ").AppendJoin(" | ", row.Elements(W + "tc").Select(c => string.Join(" ", c.Elements(W + "p").Select(Paragraph)).Trim())).AppendLine(" |");
        }
        // Headers, footers and footnotes, which edit_file changes too: the model couldn't see the
        // footer it was asked to change.
        foreach (var (title, pattern) in new[] { ("Header", "word/header"), ("Footer", "word/footer"), ("Footnotes", "word/footnotes.xml"), ("Endnotes", "word/endnotes.xml") })
        {
            var lines = zip.Entries.Where(e => e.FullName.StartsWith(pattern, StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .SelectMany(e => Optional(() => Load(zip, e.FullName).Descendants(W + "p").Select(Paragraph).ToList(), []))
                .Where(line => line.Trim().Length > 0).Distinct().ToList();
            if (lines.Count > 0)
                sb.Append(title).Append(": ").AppendJoin(" / ", lines).AppendLine();
        }
        // The comments in the margin, with the text each is on: asked to go through a document's
        // comments, the model saw none of them.
        if (Optional(() => Comments(zip, body), []) is { Count: > 0 } comments)
        {
            sb.AppendLine("Comments:");
            foreach (var (on, text) in comments)
                sb.Append("- on \"").Append(on.Clip(100)).Append("\": ").AppendLine(text);
        }
        return sb.ToString();
    }

    /// <summary>Each comment's text and the text it is on, in the document's order.</summary>
    private static List<(string On, string Text)> Comments(ZipArchive zip, XElement body)
    {
        if (zip.GetEntry("word/comments.xml") is null)
            return [];
        var texts = Load(zip, "word/comments.xml").Root!.Elements(W + "comment")
            .Where(c => c.Attribute(W + "id") is not null)
            .ToDictionary(c => (string)c.Attribute(W + "id")!, c => string.Join(" ", c.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))).Where(t => t.Length > 0)));
        var open = new Dictionary<string, StringBuilder>();
        var found = new List<(string Id, StringBuilder On)>();
        foreach (var e in body.Descendants())
        {
            if (e.Name == W + "commentRangeStart" && (string?)e.Attribute(W + "id") is { } start && !open.ContainsKey(start))
                found.Add((start, open[start] = new StringBuilder()));
            else if (e.Name == W + "commentRangeEnd" && (string?)e.Attribute(W + "id") is { } end)
                open.Remove(end);
            else if (e.Name == W + "t")
                foreach (var on in open.Values)
                    on.Append(e.Value);
        }
        return found.Where(f => texts.ContainsKey(f.Id)).Select(f => (f.On.ToString().Trim(), texts[f.Id])).ToList();
    }

    /// <summary>A paragraph's text: "# " for a heading, "- " for a list item, bold runs in **.</summary>
    private static string Paragraph(XElement p)
    {
        var style = (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
        var prefix = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var level) ? new string('#', Math.Clamp(level, 1, 6)) + " "
            : style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? "# "
            : p.Element(W + "pPr")?.Element(W + "numPr") is not null ? "- " : "";
        var sb = new StringBuilder(prefix);
        var bold = false;
        // A page's number and the count of pages as {page} and {pages}, as edit_file writes them: their text here is
        // only what Word last showed ("1").
        static string? Placeholder(string instruction) => instruction.Trim().Split(' ')[0].ToUpperInvariant() switch { "PAGE" => "{page}", "NUMPAGES" or "SECTIONPAGES" => "{pages}", _ => null };
        var instruction = new StringBuilder();
        var state = 0; // 0 text, 1 in a field's instruction, 2 in the result of a page field
        foreach (var run in p.Descendants(W + "r"))
        {
            if (run.Parent?.Name == W + "fldSimple" && Placeholder((string?)run.Parent.Attribute(W + "instr") ?? "") is { } simple)
            {
                if (run == run.Parent.Elements(W + "r").First())
                    sb.Append(simple);
                continue;
            }
            switch ((string?)run.Element(W + "fldChar")?.Attribute(W + "fldCharType"))
            {
                case "begin": state = 1; instruction.Clear(); continue;
                case "separate":
                    if (Placeholder(instruction.ToString()) is { } field) { sb.Append(field); state = 2; }
                    else state = 0;
                    continue;
                case "end": state = 0; continue;
            }
            if (state == 1) { instruction.Append(string.Concat(run.Elements(W + "instrText").Select(t => t.Value))); continue; }
            if (state == 2) continue;
            var text = string.Concat(run.Elements().Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : e.Name == W + "br" ? "\n" : ""));
            if (text.Length == 0) continue;
            var b = run.Element(W + "rPr")?.Element(W + "b") is { } bEl && (string?)bEl.Attribute(W + "val") is null or "1" or "true";
            if (b != bold && text.Trim().Length > 0)
            {
                sb.Append("**");
                bold = b;
            }
            sb.Append(text);
        }
        if (bold) sb.Append("**");
        return sb.ToString().Replace("****", "");
    }

    // ---- Excel -----------------------------------------------------------------------------------

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// Every sheet, every filled cell by its address ("B7 =SUM(B2:B6) → $2,150.00"), a row per
    /// line, numbers as Excel shows them (in dollars, as percentages, dates as 2026-11-01), and
    /// which cells are bold. Without formats a model asked for amounts in dollars couldn't see
    /// whether they were, and unzipped the workbook to look; a date read 46327.
    /// </summary>
    private static string Xlsx(Stream file, CancellationToken ct)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? []
            : Load(zip, "xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();
        var styles = CellFormats(zip);
        var highlights = Highlights(zip);
        var targets = Relations(zip, "xl/_rels/workbook.xml.rels", "xl/");
        var sb = new StringBuilder();
        foreach (var sheet in Load(zip, "xl/workbook.xml").Root!.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            var name = (string?)sheet.Attribute("name") ?? "?";
            if ((string?)sheet.Attribute(R + "id") is not { } id || !targets.TryGetValue(id, out var target) || zip.GetEntry(target) is null)
                continue;
            var data = Load(zip, target).Root!;
            sb.Append("# Sheet \"").Append(name).Append('"');
            if ((string?)data.Element(S + "dimension")?.Attribute("ref") is { } range)
                sb.Append(" (").Append(range).Append(')');
            sb.AppendLine();
            var bold = new List<string>();
            foreach (var row in data.Element(S + "sheetData")?.Elements(S + "row") ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var cells = new List<string>();
                foreach (var c in row.Elements(S + "c"))
                {
                    var style = int.TryParse((string?)c.Attribute("s"), out var s) && s >= 0 && s < styles.Count ? styles[s] : default;
                    if (Cell(c, shared, style.Format) is not { Length: > 0 } cell)
                        continue;
                    cells.Add(cell);
                    if (style.Bold)
                        bold.Add((string?)c.Attribute("r") ?? "");
                }
                if (cells.Count > 0)
                    sb.AppendJoin(" | ", cells).AppendLine();
            }
            if (bold.Count > 0)
                sb.Append("(bold: ").AppendJoin(", ", bold).AppendLine(")");
            // What else a person sees on the sheet: a title across merged cells, its charts.
            if (data.Element(S + "mergeCells")?.Elements(S + "mergeCell").Select(m => (string?)m.Attribute("ref")).OfType<string>().ToList() is { Count: > 0 } merged)
                sb.Append("(merged: ").AppendJoin(", ", merged).AppendLine(")");
            // What stays in view, the filter and the rules that colour cells: made with edit_file, they were nowhere to
            // check them by (2026-11-13).
            if (data.Element(S + "sheetViews")?.Element(S + "sheetView")?.Element(S + "pane") is { } pane && ((string?)pane.Attribute("state"))?.StartsWith("frozen", StringComparison.Ordinal) == true)
            {
                var rows = (int?)pane.Attribute("ySplit") ?? 0;
                var columns = (int?)pane.Attribute("xSplit") ?? 0;
                static string Letters(int n) { var s = ""; for (; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s; return s; }
                sb.Append("(frozen: ").AppendJoin(" and ", new[] { rows > 0 ? (rows == 1 ? "row 1" : $"rows 1-{rows}") : null, columns > 0 ? (columns == 1 ? "column A" : $"columns A-{Letters(columns)}") : null }.OfType<string>()).AppendLine(")");
            }
            if ((string?)data.Element(S + "autoFilter")?.Attribute("ref") is { } filter)
                sb.Append("(filter: ").Append(filter).AppendLine(")");
            foreach (var rule in data.Element(S + "dataValidations")?.Elements(S + "dataValidation").Where(v => (string?)v.Attribute("type") == "list") ?? [])
                sb.Append("(dropdown: ").Append((string?)rule.Attribute("sqref")).Append(": ")
                    .Append(((string?)rule.Element(S + "formula1"))?.Trim('"').Replace(",", ", ") ?? "").AppendLine(")");
            foreach (var rule in data.Elements(S + "conditionalFormatting").SelectMany(f => f.Elements(S + "cfRule").Select(r => (Range: (string?)f.Attribute("sqref"), Rule: r))))
                sb.Append("(highlight: ").Append(rule.Range).Append(' ').Append(Rule(rule.Rule)).Append(highlights.ElementAtOrDefault((int?)rule.Rule.Attribute("dxfId") ?? -1) is { Length: > 0 } look ? ": " + look : "").AppendLine(")");
            // Each chart with what it draws ("(chart: bar chart "Sales": Budget!B2:B13 over Budget!A2:A13)"), so one
            // made is checked by reading and one of the user's is understood; one that can't be read is still counted.
            var charts = Optional(() => Charts(zip, target), []);
            foreach (var chart in charts.OfType<string>())
                sb.Append("(chart: ").Append(chart).AppendLine(")");
            if (charts.Count(c => c is null) is > 0 and var unread)
                sb.AppendLine(unread == 1 ? "(1 chart)" : $"({unread} charts)");
        }
        return sb.ToString();
    }

    /// <summary>The charts drawn on a sheet, each as <see cref="Documents.SheetChart.Describe"/> says it, or null for one whose part can't be read.</summary>
    private static List<string?> Charts(ZipArchive zip, string sheet)
    {
        var folder = sheet[..(sheet.LastIndexOf('/') + 1)];
        var rels = $"{folder}_rels/{sheet[folder.Length..]}.rels";
        if (zip.GetEntry(rels) is null)
            return [];
        XNamespace c = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        var found = new List<string?>();
        foreach (var drawing in Load(zip, rels).Root!.Elements(Pr + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/drawing", StringComparison.Ordinal) == true && r.Attribute("Target") is not null)
            .Select(r => Resolve(folder, (string)r.Attribute("Target")!))
            .Where(d => zip.GetEntry(d) is not null))
        {
            var drawingFolder = drawing[..(drawing.LastIndexOf('/') + 1)];
            var drawingRels = $"{drawingFolder}_rels/{drawing[drawingFolder.Length..]}.rels";
            var targets = zip.GetEntry(drawingRels) is null ? [] : Optional(() => Load(zip, drawingRels).Root!.Elements(Pr + "Relationship")
                .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null)
                .ToDictionary(r => (string)r.Attribute("Id")!, r => Resolve(drawingFolder, (string)r.Attribute("Target")!)), []);
            foreach (var chart in Load(zip, drawing).Descendants(c + "chart"))
                found.Add((string?)chart.Attribute(R + "id") is { } id && targets.TryGetValue(id, out var part) && zip.GetEntry(part) is not null
                    ? Optional<string?>(() => Documents.SheetChart.Describe(Load(zip, part)), null)
                    : null);
        }
        return found;
    }

    private static string Cell(XElement c, List<string> shared, string? format)
    {
        var at = (string?)c.Attribute("r") ?? "";
        var value = (string?)c.Element(S + "v");
        var shown = (string?)c.Attribute("t") switch
        {
            "s" when int.TryParse(value, out var i) && i < shared.Count => shared[i],
            "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
            "b" => value == "1" ? "TRUE" : "FALSE",
            // Numbers as the format shows them, else with the 15 digits Excel shows: the file has
            // 512.29999999999995 for 512.3.
            null or "n" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
                => Formatted(n, format) ?? n.ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
            _ => value ?? "",
        };
        if (c.Element(S + "f") is { } f)
            // A shared formula is written out only in its first cell; the others just point at it.
            shown = $"={(f.Value.Length > 0 ? f.Value : "(shared formula)")} → {shown}";
        return shown.Length == 0 ? "" : $"{at} {shown}";
    }

    /// <summary>Each cell style's number format code (null for General) and whether its font is bold, by style index.</summary>
    private static List<(string? Format, bool Bold)> CellFormats(ZipArchive zip)
    {
        if (zip.GetEntry("xl/styles.xml") is null)
            return [];
        var styles = Load(zip, "xl/styles.xml").Root!;
        var codes = styles.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => f.Attribute("numFmtId") is not null)
            .GroupBy(f => (int)f.Attribute("numFmtId")!).ToDictionary(g => g.Key, g => (string?)g.First().Attribute("formatCode")) ?? [];
        var bold = styles.Element(S + "fonts")?.Elements(S + "font").Select(f => f.Element(S + "b") is { } b && (string?)b.Attribute("val") is null or "1" or "true").ToList() ?? [];
        return styles.Element(S + "cellXfs")?.Elements(S + "xf").Select(xf =>
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            var font = (int?)xf.Attribute("fontId") ?? 0;
            return (codes.GetValueOrDefault(id) ?? BuiltInFormat(id), font >= 0 && font < bold.Count && bold[font]);
        }).ToList() ?? [];
    }

    /// <summary>What a conditional format's rule tests: "where $F2>3000", "where the cell is over 3000".</summary>
    private static string Rule(XElement rule)
    {
        var formulas = rule.Elements(S + "formula").Select(f => f.Value).ToList();
        return (string?)rule.Attribute("type") switch
        {
            "expression" => "where " + string.Join(" ", formulas),
            "cellIs" => "where the cell is " + ((string?)rule.Attribute("operator") switch
            {
                "greaterThan" => "over", "greaterThanOrEqual" => "at least", "lessThan" => "under", "lessThanOrEqual" => "at most",
                "notEqual" => "not", "between" => "between", "notBetween" => "not between", _ => "",
            }) + " " + string.Join(" and ", formulas),
            var type => $"({type ?? "a rule"})",
        };
    }

    /// <summary>What each of the workbook's highlight looks (dxf) gives a cell: "fill #FFC7CE, text #9C0006".</summary>
    private static List<string> Highlights(ZipArchive zip)
    {
        if (zip.GetEntry("xl/styles.xml") is null)
            return [];
        return Optional(() => Load(zip, "xl/styles.xml").Root!.Element(S + "dxfs")?.Elements(S + "dxf").Select(dxf =>
        {
            static string? Rgb(XElement? colour) => ((string?)colour?.Attribute("rgb")) is { Length: 8 } argb ? "#" + argb[2..] : null;
            var fill = Rgb(dxf.Element(S + "fill")?.Descendants().FirstOrDefault(e => e.Name.LocalName is "bgColor" or "fgColor"));
            var text = Rgb(dxf.Element(S + "font")?.Element(S + "color"));
            return string.Join(", ", new[] { fill is null ? null : "fill " + fill, text is null ? null : "text " + text, dxf.Element(S + "font")?.Element(S + "b") is not null ? "bold" : null }.OfType<string>());
        }).ToList() ?? [], []);
    }

    /// <summary>The number formats Excel has without writing them down, the ones that change how a number reads.</summary>
    private static string? BuiltInFormat(int id) => id switch
    {
        3 => "#,##0",
        4 => "#,##0.00",
        9 => "0%",
        10 => "0.00%",
        >= 14 and <= 17 => "yyyy-mm-dd",
        _ => null,
    };

    /// <summary>
    /// A number as its format shows it, written so that edit_file reads it back as the same number
    /// in the same format: $1,200.00, 1,200, 40%, 2026-11-01. Null for formats it doesn't know
    /// (times, fractions, custom text), which show the number as it is.
    /// </summary>
    internal static string? Formatted(double n, string? format)
    {
        if (format is null || format.Contains('h') || format.Contains('s') && !format.Contains('#'))
            return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var decimals = format.IndexOf('.') is var dot and >= 0 ? format[(dot + 1)..].TakeWhile(ch => ch == '0').Count() : 0;
        if (format.Contains('%'))
            return (n * 100).ToString("F" + decimals, inv) + "%";
        if (format.Contains('y') && format.Contains('d'))
            return n is >= 1 and < 2958466 ? DateTime.FromOADate(n).ToString("yyyy-MM-dd", inv) : null;
        if (!format.Contains('0'))
            return null;
        var number = Math.Abs(n).ToString((format.Contains(",") ? "N" : "F") + decimals, inv);
        var symbol = format.FirstOrDefault(ch => ch is '$' or '€' or '£' or '¥' or '₹');
        var sign = n < 0 ? "-" : "";
        if (symbol == default)
            return sign + number;
        return format.IndexOf(symbol) < format.IndexOf('0') ? $"{sign}{symbol}{number}" : $"{sign}{number} {symbol}";
    }

    // ---- PowerPoint ------------------------------------------------------------------------------

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static string Pptx(Stream file, CancellationToken ct)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var targets = Relations(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
        var ids = Load(zip, "ppt/presentation.xml").Root!.Element(P + "sldIdLst")?.Elements(P + "sldId").Select(s => (string?)s.Attribute(R + "id")).ToList() ?? [];
        var sb = new StringBuilder();
        var number = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (id is null || !targets.TryGetValue(id, out var target) || zip.GetEntry(target) is null)
                continue;
            sb.Append("--- slide ").Append(number).Append(" of ").Append(ids.Count).AppendLine(" ---");
            // A table as rows, as a Word table reads: its cells one a line, the model couldn't tell rows apart, and to
            // add one it typed "insert row 10" into a cell (2026-11-10).
            static string Text(XElement para) => string.Concat(para.Descendants(A + "t").Select(t => t.Value));
            foreach (var block in Load(zip, target).Descendants().Where(e => e.Name == A + "tbl" || e.Name == A + "p" && !e.Ancestors(A + "tbl").Any()))
                if (block.Name == A + "tbl")
                    foreach (var row in block.Elements(A + "tr"))
                        sb.Append("| ").AppendJoin(" | ", row.Elements(A + "tc").Select(c => string.Join(" ", c.Descendants(A + "p").Select(Text)).Trim())).AppendLine(" |");
                else if (Text(block) is { Length: > 0 } text)
                    sb.AppendLine(text);
            // Its charts, with the numbers they draw: nothing of a chart is text on the slide, and a slide read back
            // showed none of it (Haiku took its chart for missing, 2026-11-13).
            foreach (var chart in Optional(() => SlideCharts(zip, target), []))
                sb.Append("(chart: ").Append(chart).AppendLine(")");
            // The speaker notes, which went unseen: asked to change them, the model had nothing to go on.
            if (Optional(() => Notes(zip, target), []) is { Count: > 0 } notes)
            {
                sb.AppendLine("Speaker notes:");
                foreach (var line in notes)
                    sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    /// <summary>The charts on a slide, each as <see cref="Documents.SheetChart.DescribeData"/> says it.</summary>
    private static List<string> SlideCharts(ZipArchive zip, string slide)
    {
        var folder = slide[..(slide.LastIndexOf('/') + 1)];
        var rels = $"{folder}_rels/{slide[folder.Length..]}.rels";
        if (zip.GetEntry(rels) is null)
            return [];
        return Load(zip, rels).Root!.Elements(Pr + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/chart", StringComparison.Ordinal) == true && r.Attribute("Target") is not null)
            .Select(r => Resolve(folder, (string)r.Attribute("Target")!))
            .Where(part => zip.GetEntry(part) is not null)
            .Select(part => Documents.SheetChart.DescribeData(Load(zip, part)))
            .ToList();
    }

    /// <summary>The paragraphs of a slide's speaker notes (their body, not the slide number or picture); empty when it has none.</summary>
    private static List<string> Notes(ZipArchive zip, string slide)
    {
        var folder = slide[..(slide.LastIndexOf('/') + 1)];
        var rels = $"{folder}_rels/{slide[folder.Length..]}.rels";
        if (zip.GetEntry(rels) is null)
            return [];
        var target = Load(zip, rels).Root!.Elements(Pr + "Relationship")
            .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/notesSlide", StringComparison.Ordinal) == true)?.Attribute("Target")?.Value;
        if (target is null || zip.GetEntry(Resolve(folder, target)) is null)
            return [];
        var body = Load(zip, Resolve(folder, target)).Descendants(P + "sp")
            .FirstOrDefault(sp => sp.Descendants(P + "ph").Any(ph => (string?)ph.Attribute("type") == "body"));
        return body?.Descendants(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value))).Where(t => t.Length > 0).ToList() ?? [];
    }

    /// <summary>A relationship's target ("../notesSlides/notesSlide2.xml") as an entry of the package, from the folder of the part it belongs to.</summary>
    internal static string Resolve(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var path = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..") { if (path.Count > 0) path.RemoveAt(path.Count - 1); }
            else if (piece is not ("." or "")) path.Add(piece);
        }
        return string.Join('/', path);
    }

    /// <summary>What a part beside the main text says (notes, comments, footers, charts), or nothing when it can't be read: the text itself still is.</summary>
    private static T Optional<T>(Func<T> read, T none)
    {
        try { return read(); }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or KeyNotFoundException or ArgumentException) { return none; }
    }

    // ---- the zip package -------------------------------------------------------------------------

    private static XDocument Load(ZipArchive zip, string entry)
    {
        using var stream = (zip.GetEntry(entry) ?? throw new InvalidDataException($"{entry} is missing")).Open();
        return XDocument.Load(stream);
    }

    /// <summary>A part's relationships: id to the entry it points at, relative to <paramref name="folder"/>.</summary>
    private static Dictionary<string, string> Relations(ZipArchive zip, string rels, string folder) =>
        zip.GetEntry(rels) is null ? []
            : Load(zip, rels).Root!.Elements(Pr + "Relationship")
                .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null)
                .ToDictionary(r => (string)r.Attribute("Id")!, r => ((string)r.Attribute("Target")!).TrimStart('/') is var t && t.StartsWith(folder, StringComparison.Ordinal) ? t : folder + t);
}
