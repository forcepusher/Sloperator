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
        // The cells of each sheet a range is on, by their place.
        var read = new Dictionary<string, Dictionary<string, XElement>>(StringComparer.OrdinalIgnoreCase);
        SheetChart.Value Cell(string name, int row, int column)
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
            return SheetChart.ValueOf(cells.GetValueOrDefault(XlsxWriter.Reference(row, column)), shared);
        }
        var series = SheetChart.SeriesOf(target.Name, ask.Ranges, Cell);

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
        return $"added a {ask.Kind} chart{(ask.Title is null ? "" : $" \"{ask.Title}\"")} to sheet \"{target.Name}\" at {XlsxWriter.Reference(row, right)}: " +
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
