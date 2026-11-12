using System.Globalization;
using System.Xml.Linq;

namespace Sloperator.Core.Documents;

internal static partial class SheetEdit
{
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>A chart asked for with a line of new_text ("add chart bar A1:D13 Sales by month"), on the sheet named before it if any.</summary>
    private sealed record ChartLine(string? Sheet, SheetChart.Ask Ask);

    /// <summary>
    /// A chart added to a sheet of the workbook, drawn from its cells (<see cref="SheetChart"/>): its part, the
    /// sheet's drawing that holds it (made when the sheet has none; under the charts it has already), and their
    /// relationships and content types. To the right of the sheet's cells. What was done, for the tool's answer.
    /// </summary>
    private static string AddChart(PartSet parts, List<(string Name, string Part)> sheets, (string Name, string Part) target, SheetChart.Ask ask, List<string> shared)
    {
        if (ask.Ranges.Count == 0)
            throw new InvalidOperationException($"Say which cells the chart draws, their headings and labels with them: add chart {ask.Kind} A1:D13 {ask.Title ?? "Title"}");
        var series = SheetChart.SeriesOf(target.Name, ask.Ranges, CellReader(parts, sheets, shared));

        var sheet = parts.Get(target.Part);
        var types = parts.Get("[Content_Types].xml");
        string Unused(string pattern)
        {
            for (var n = 1; ; n++)
                if (string.Format(CultureInfo.InvariantCulture, pattern, n) is var name && !parts.Exists(name))
                    return name;
        }
        void Typed(string part, string type) =>
            types.Root!.Add(new XElement(Types + "Override", new XAttribute("PartName", "/" + part), new XAttribute("ContentType", type)));

        // The sheet's drawing: the one it has, or a new one.
        var drawingPart = (string?)sheet.Root!.Element(S + "drawing")?.Attribute(R + "id") is { } has && Targets(parts, target.Part).TryGetValue(has, out var there) && parts.Exists(there) ? there : null;
        XDocument drawing;
        if (drawingPart is null)
        {
            drawingPart = Unused("xl/drawings/drawing{0}.xml");
            parts.Add(drawingPart, drawing = SheetChart.Drawing([]));
            Typed(drawingPart, SheetChart.DrawingType);
            var id = Relate(parts, target.Part, SheetChart.DrawingRel, drawingPart);
            sheet.Root.Element(S + "drawing")?.Remove();
            var element = new XElement(S + "drawing", new XAttribute(R + "id", id));
            // In the order a sheet's parts go in: before these, else last.
            if (sheet.Root.Elements().FirstOrDefault(e => SheetChart.AfterDrawing.Contains(e.Name.LocalName)) is { } later)
                later.AddBeforeSelf(element);
            else
                sheet.Root.Add(element);
            if (sheet.Root.GetPrefixOfNamespace(R) is null)
                sheet.Root.SetAttributeValue(XNamespace.Xmlns + "r", R.NamespaceName);
            parts.Changed(target.Part);
        }
        else
            drawing = parts.Get(drawingPart);

        var chartPart = Unused("xl/charts/chart{0}.xml");
        parts.Add(chartPart, SheetChart.Chart(ask.Kind, ask.Title, series));
        Typed(chartPart, SheetChart.ChartType);
        parts.Changed("[Content_Types].xml");
        var relation = Relate(parts, drawingPart, SheetChart.ChartRel, chartPart);
        var right = (sheet.Root.Element(S + "sheetData")?.Elements(S + "row").Elements(S + "c")
            .Select(c => (string?)c.Attribute("r")).OfType<string>().Select(r => Parse(r).Column).DefaultIfEmpty(1).Max() ?? 1) + 2;
        var row = Math.Max(2, SheetChart.RowUnder(drawing));
        drawing.Root!.Add(SheetChart.Anchor(SheetChart.Count(drawing) + 1, relation, row, right));
        parts.Changed(drawingPart);
        return $"added {A(ask.Kind)} {ask.Kind} chart{(ask.Title is null ? "" : $" \"{ask.Title}\"")} to sheet \"{target.Name}\" at {XlsxWriter.Reference(row, right)}: " +
               string.Join(", ", series.Take(pieOnly(ask) ? 1 : 8).Select(s => $"{s.Name} ({s.ValuesRef.Replace("$", "")})")) +
               (series[0].LabelsRef is { } labels ? $" over {labels.Replace("$", "")}" : "");

        static bool pieOnly(SheetChart.Ask a) => a.Kind == "pie";
    }

    /// <summary>A relationship from <paramref name="part"/> to <paramref name="to"/>, added to its relationships (made when it has none); its id.</summary>
    private static string Relate(PartSet parts, string part, string type, string to)
    {
        var folder = Path.GetDirectoryName(part)!.Replace('\\', '/');
        var name = $"{folder}/_rels/{Path.GetFileName(part)}.rels";
        XDocument rels;
        if (parts.Exists(name))
            rels = parts.Get(name);
        else
            parts.Add(name, rels = new XDocument(new XElement(Rel + "Relationships")));
        var taken = rels.Root!.Elements(Rel + "Relationship").Select(r => (string?)r.Attribute("Id")).ToHashSet();
        var id = Enumerable.Range(1, int.MaxValue).Select(n => $"rId{n}").First(i => !taken.Contains(i));
        // From the package's root: right wherever the part itself is.
        rels.Root.Add(new XElement(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", "/" + to)));
        parts.Changed(name);
        return id;
    }
}

internal static partial class SheetEdit
{
    // ---- a chart that is there: changed or deleted ---------------------------------------------------------------
    // Asked to "make that chart a line chart instead", Haiku gave edit_file the chart's line as old_text and the new
    // one as new_text, was told the text wasn't in the cells, and wrote the whole workbook anew; Sonnet deleted the
    // file and built it again (2026-11-13). Neither can be done to a workbook that isn't the model's own.

    /// <summary>Which chart is meant: by its place on its sheet (from 1), its title, its kind, or any of them; on a named sheet or any.</summary>
    private sealed record ChartTarget(string? Sheet, string? Kind, string? Title, int? Number);

    /// <summary>A chart of the workbook: its sheet, the drawing that holds it and its frame there, and its own part.</summary>
    private sealed record ChartFound((string Name, string Part) Sheet, string DrawingPart, XElement Anchor, string Relation, string ChartPart, int Number, string Kind, string? Title)
    {
        public string Said => $"{Kind} chart{(Title is null ? "" : $" \"{Title}\"")}";
    }

    private static readonly XNamespace ChartNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";

    /// <summary>"a" or "an" before a chart's kind.</summary>
    private static string A(string kind) => kind.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(kind[0])) ? "an" : "a";

    /// <summary>Every chart of the workbook, sheet by sheet in the order they were put there.</summary>
    private static List<ChartFound> Charts(PartSet parts, List<(string Name, string Part)> sheets)
    {
        var found = new List<ChartFound>();
        foreach (var sheet in sheets)
        {
            if ((string?)parts.Get(sheet.Part).Root!.Element(S + "drawing")?.Attribute(R + "id") is not { } id
                || !Targets(parts, sheet.Part).TryGetValue(id, out var drawingPart) || !parts.Exists(drawingPart))
                continue;
            var relations = Targets(parts, drawingPart);
            var number = 0;
            foreach (var anchor in parts.Get(drawingPart).Root!.Elements().ToList())
            {
                if ((string?)anchor.Descendants(ChartNs + "chart").FirstOrDefault()?.Attribute(R + "id") is not { } relation
                    || !relations.TryGetValue(relation, out var chartPart) || !parts.Exists(chartPart))
                    continue;
                var (kind, title) = SheetChart.About(parts.Get(chartPart));
                found.Add(new ChartFound(sheet, drawingPart, anchor, relation, chartPart, ++number, kind, title));
            }
        }
        return found;
    }

    /// <summary>The one chart <paramref name="target"/> means; an error that lists the charts when it means none, or more than one.</summary>
    private static ChartFound Find(List<ChartFound> charts, ChartTarget target)
    {
        string List() => charts.Count == 0 ? "The workbook has no charts."
            : "The charts are: " + string.Join("; ", charts.Select(c => $"chart {c.Number} on sheet \"{c.Sheet.Name}\", {A(c.Kind)} {c.Said}")) + ".";
        IEnumerable<ChartFound> some = charts;
        if (target.Sheet is { } sheet)
            some = some.Where(c => c.Sheet.Name.Equals(sheet, StringComparison.OrdinalIgnoreCase));
        if (target.Number is { } number)
            some = some.Where(c => c.Number == number);
        if (target.Title is { } title)
            some = some.Where(c => string.Equals(c.Title?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)) is var exact && exact.Any()
                ? exact : some.Where(c => c.Title?.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase) == true);
        // Its kind only tells charts apart: one named by its title or number is that one, whatever was said of its kind.
        if (target.Kind is { } kind && some.Count() > 1)
            some = some.Where(c => c.Kind == kind);
        var left = some.ToList();
        return left.Count switch
        {
            1 => left[0],
            0 => throw new InvalidOperationException($"There is no such chart. {List()}"),
            _ => throw new InvalidOperationException($"More than one chart fits that: say which by its number or title, such as chart 2 or chart \"{left[0].Title ?? "Title"}\". {List()}"),
        };
    }

    /// <summary>
    /// A line that names a chart that is there, or null: "chart 2", "chart \"Monthly totals\"", "the bar chart",
    /// "chart: bar Monthly totals" as write_file took it, "(chart: bar chart \"Monthly totals\": Sales!B2:B13 over
    /// Sales!A2:A13)" as read_file shows it.
    /// </summary>
    private static ChartTarget? TargetOf(string line, string? sheet)
    {
        var text = line.Trim();
        if (text.StartsWith('(') && text.EndsWith(')'))
            text = text[1..^1].Trim();
        if (ChartNamed().Match(text) is not { Success: true } m)
            return null;
        var rest = m.Groups["rest"].Value.Trim();
        if (ChartNumber().Match(rest) is { Success: true } number)
            return new ChartTarget(sheet, null, null, int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture));
        var kind = m.Groups["kind"].Success ? SheetChart.KindOf(m.Groups["kind"].Value) : null;
        string? title = null;
        if (QuotedTitle().Match(rest) is { Success: true } quoted)
            title = (quoted.Groups[1].Success ? quoted.Groups[1] : quoted.Groups[2]).Value;
        else
        {
            // "bar Monthly totals", "bar chart": its kind first, then its title up to where its cells are said.
            var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (kind is null && words.Count > 0 && SheetChart.KindOf(words[0]) is { } first)
            {
                kind = first;
                words.RemoveAt(0);
            }
            if (words.Count > 0 && words[0].TrimEnd(':').Equals("chart", StringComparison.OrdinalIgnoreCase))
                words.RemoveAt(0);
            var cut = words.FindIndex(w => w.Contains(':') || w.Contains('!') || w.Equals("over", StringComparison.OrdinalIgnoreCase));
            title = string.Join(' ', cut < 0 ? words : words.Take(cut)).Trim(' ', ',', '.', ':');
        }
        return new ChartTarget(sheet, kind, string.IsNullOrWhiteSpace(title) ? null : title, null);
    }

    /// <summary>The charts <paramref name="text"/> names, when every line of it names one (a line # Sheet "Name" says where); else null.</summary>
    private static List<ChartTarget>? ChartTargets(string text)
    {
        var targets = new List<ChartTarget>();
        string? sheet = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (TargetOf(line, sheet) is { } target)
                targets.Add(target);
            else if (SheetHeader().Match(line) is { Success: true } header)
                sheet = header.Groups[1].Value.Trim();
            else
                return null;
        }
        return targets.Count > 0 ? targets : null;
    }

    /// <summary>What new_text makes of a chart named in old_text: a chart line in any of its forms; null when the text is something else.</summary>
    private static List<SheetChart.Ask>? ChartAsks(string text)
    {
        var asks = new List<SheetChart.Ask>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var bare = line.StartsWith('(') && line.EndsWith(')') ? line[1..^1].Trim() : line;
            if ((SheetChart.Parse(bare, colon: true) ?? SheetChart.Parse(bare, colon: false)) is { } ask)
                asks.Add(ask);
            else if (TargetOf(bare, null) is { Kind: { } kind } named)
                asks.Add(new SheetChart.Ask(kind, named.Title, []));
            else
                return null;
        }
        return asks;
    }

    /// <summary>
    /// edit_file with a chart as old_text: with no new_text the chart is deleted; with a chart line it becomes
    /// that, in its place on the sheet: another kind, another title, other cells, each kept as it was when not said.
    /// </summary>
    private static string ChangeCharts(PartSet parts, List<(string Name, string Part)> sheets, List<ChartTarget> targets, List<SheetChart.Ask> asks)
    {
        if (asks.Count > 0 && asks.Count != targets.Count)
            throw new InvalidOperationException("Give one chart line in new_text for each chart named in old_text, or none to delete them.");
        var shared = parts.Exists("xl/sharedStrings.xml") ? parts.Get("xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList() : [];
        var done = new List<string>();
        // Found first, all of them: deleting one moves the numbers of those after it.
        var found = targets.Select(t => Find(Charts(parts, sheets), t)).ToList();
        if (found.Select(f => f.ChartPart).Distinct().Count() != found.Count)
            throw new InvalidOperationException("old_text names the same chart twice.");
        for (var i = 0; i < found.Count; i++)
            done.Add(asks.Count == 0 ? RemoveChart(parts, found[i]) : ChangeChart(parts, sheets, found[i], asks[i], shared));
        var said = string.Join(", then ", done);
        return char.ToUpperInvariant(said[0]) + said[1..] + ".";
    }

    /// <summary>A chart off its sheet: its frame, its part and what belongs to it alone.</summary>
    private static string RemoveChart(PartSet parts, ChartFound chart)
    {
        chart.Anchor.Remove();
        parts.Changed(chart.DrawingPart);
        var folder = Path.GetDirectoryName(chart.DrawingPart)!.Replace('\\', '/');
        var drawingRels = $"{folder}/_rels/{Path.GetFileName(chart.DrawingPart)}.rels";
        if (parts.Exists(drawingRels))
        {
            parts.Get(drawingRels).Root!.Elements(Rel + "Relationship").Where(r => (string?)r.Attribute("Id") == chart.Relation).Remove();
            parts.Changed(drawingRels);
        }
        // The chart's own parts go with it: its style and colours, as Excel keeps them beside it.
        var chartFolder = Path.GetDirectoryName(chart.ChartPart)!.Replace('\\', '/');
        var chartRels = $"{chartFolder}/_rels/{Path.GetFileName(chart.ChartPart)}.rels";
        var gone = new List<string> { chart.ChartPart };
        if (parts.Exists(chartRels))
        {
            gone.AddRange(Targets(parts, chart.ChartPart).Values.Where(t => t.StartsWith(chartFolder + "/", StringComparison.Ordinal) && parts.Exists(t)));
            gone.Add(chartRels);
        }
        var types = parts.Get("[Content_Types].xml");
        foreach (var part in gone)
        {
            parts.Delete(part);
            types.Root!.Elements().Where(e => string.Equals((string?)e.Attribute("PartName"), "/" + part, StringComparison.OrdinalIgnoreCase)).Remove();
        }
        parts.Changed("[Content_Types].xml");
        return $"deleted the {chart.Said} from sheet \"{chart.Sheet.Name}\"";
    }

    /// <summary>A chart made anew in its place: what <paramref name="ask"/> says of its kind, title and cells, the rest as it was.</summary>
    private static string ChangeChart(PartSet parts, List<(string Name, string Part)> sheets, ChartFound chart, SheetChart.Ask ask, List<string> shared)
    {
        var series = ask.Ranges.Count > 0
            ? SheetChart.SeriesOf(chart.Sheet.Name, ask.Ranges, CellReader(parts, sheets, shared))
            : SheetChart.SeriesIn(parts.Get(chart.ChartPart));
        if (series.Count == 0)
            throw new InvalidOperationException($"The {chart.Said} names no cells that can be kept: give the cells it should draw (chart: {ask.Kind} A1:D13 {ask.Title ?? chart.Title ?? "Title"}).");
        var title = ask.Title ?? chart.Title;
        parts.Add(chart.ChartPart, SheetChart.Chart(ask.Kind, title, series));
        return $"changed the {chart.Said} on sheet \"{chart.Sheet.Name}\" to {A(ask.Kind)} {ask.Kind} chart{(title is null ? "" : $" \"{title}\"")}: " +
               string.Join(", ", series.Take(ask.Kind == "pie" ? 1 : 8).Select(s => $"{s.Name} ({s.ValuesRef.Replace("$", "")})")) +
               (series[0].LabelsRef is { } labels ? $" over {labels.Replace("$", "")}" : "");
    }

    /// <summary>What a cell of a sheet of the workbook holds, for a chart: each sheet's cells read once.</summary>
    private static Func<string, int, int, SheetChart.Value> CellReader(PartSet parts, List<(string Name, string Part)> sheets, List<string> shared)
    {
        var read = new Dictionary<string, Dictionary<string, XElement>>(StringComparer.OrdinalIgnoreCase);
        // Which cell formats are bold, for headings that are numbers (see SheetChart.SeriesOf).
        var styles = parts.Exists("xl/styles.xml") ? parts.Get("xl/styles.xml").Root : null;
        var fonts = styles?.Element(S + "fonts")?.Elements(S + "font").Select(f => f.Element(S + "b") is { } b && (string?)b.Attribute("val") is not ("0" or "false")).ToList() ?? [];
        var bold = styles?.Element(S + "cellXfs")?.Elements(S + "xf").Select(xf => (int?)xf.Attribute("fontId") is { } font && font >= 0 && font < fonts.Count && fonts[font]).ToList() ?? [];
        return (name, row, column) =>
        {
            if (!read.TryGetValue(name, out var cells))
            {
                var on = sheets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (on.Part is null)
                    throw new InvalidOperationException($"There is no sheet \"{name}\"; the sheets are {string.Join(", ", sheets.Select(s => $"\"{s.Name}\""))}.");
                read[name] = cells = [];
                foreach (var c in parts.Get(on.Part).Root!.Element(S + "sheetData")?.Elements(S + "row").Elements(S + "c") ?? [])
                    if ((string?)c.Attribute("r") is { } at)
                        cells[at] = c;
            }
            if (!cells.TryGetValue(XlsxWriter.Reference(row, column), out var cell))
                return default;
            return SheetChart.ValueOf(cell, shared) with { Bold = (int?)cell.Attribute("s") is { } style && style >= 0 && style < bold.Count && bold[style] };
        };
    }

    // "chart 2", "the bar chart", "chart: bar Monthly totals", "charts"
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:the\s+)?(?:(?<kind>bar|column|line|pie|area)\s+)?chart\b\s*:?\s*(?<rest>.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChartNamed();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:number\s+|no\.?\s*|#)?([0-9]{1,3})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChartNumber();

    [System.Text.RegularExpressions.GeneratedRegex("\"([^\"]*)\"|“([^”]*)”")]
    private static partial System.Text.RegularExpressions.Regex QuotedTitle();

    // "delete chart 2", "remove the bar chart", "delete chart "Monthly totals""
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:delete|remove)\s+(?<what>(?:the\s+)?(?:(?:bar|column|line|pie|area)\s+)?chart\b.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChartRemoval();
}
