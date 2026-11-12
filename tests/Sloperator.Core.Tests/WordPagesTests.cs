using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>edit_file's lines for every page of a Word document (page numbers, header and footer), and "headings" as old_text.</summary>
public class WordPagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-pages-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content }, Ctx(), CancellationToken.None);

    private Task<ToolResult> Edit(string file, string oldText, string newText) =>
        new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = oldText, ["new_text"] = newText }, Ctx(), CancellationToken.None);

    private async Task<string> Read(string file) => System.Text.RegularExpressions.Regex.Replace(
        (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = file }, Ctx(), CancellationToken.None)).Text.ReplaceLineEndings("\n"), @"(?m)^\d+\t", "");

    private XDocument Part(string file, string part)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, file));
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    private const string Report = "# Harbour Cycles: third quarter\n\nSales rose in July.\n\n## Sales\n\nJuly was the best month.\n\n## Outlook\n\nSteady.";

    [Fact]
    public async Task Page_numbers_and_a_footer_are_put_on_every_page_and_kept_as_each_is_changed()
    {
        Assert.False((await Write("report.docx", Report)).IsError);
        // As asked of Haiku and Sonnet (2026-11-13): page numbers at the bottom, then Confidential on the left of the footer.
        var numbers = await Edit("report.docx", "", "page numbers at the bottom");
        Assert.False(numbers.IsError, numbers.Text);
        Assert.Contains("Set the footer of every page to: \"{page}\" on the centre.", numbers.Text);
        var document = Part("report.docx", "word/document.xml");
        var reference = document.Descendants(W + "footerReference").Single();
        Assert.Equal("default", (string?)reference.Attribute(W + "type"));
        Assert.Same(reference, document.Descendants(W + "sectPr").Single().Elements().First());
        Assert.Contains("footer1.xml", Part("report.docx", "word/_rels/document.xml.rels").ToString());
        Assert.Contains("/word/footer1.xml", Part("report.docx", "[Content_Types].xml").ToString());
        var field = Part("report.docx", "word/footer1.xml").Descendants(W + "fldSimple").Single();
        Assert.Equal(" PAGE ", (string?)field.Attribute(W + "instr"));
        Assert.Contains("Footer: \t{page}", await Read("report.docx"));
        // The body is as it was.
        Assert.Contains("## Sales\nJuly was the best month.", await Read("report.docx"));

        var left = await Edit("report.docx", "", "footer left: Confidential");
        Assert.False(left.IsError, left.Text);
        Assert.Contains("\"Confidential\" on the left, \"{page}\" on the centre", left.Text);
        Assert.Contains("Footer: Confidential\t{page}", await Read("report.docx"));
        Assert.Single(Part("report.docx", "word/document.xml").Descendants(W + "footerReference"));

        // A header with both fields on the right; then the footer taken off.
        Assert.False((await Edit("report.docx", "", "header right: Page {page} of {pages}")).IsError);
        Assert.Contains("Header: \t\tPage {page} of {pages}", await Read("report.docx"));
        var tabs = Part("report.docx", "word/header1.xml").Descendants(W + "tabs").Single().Elements(W + "tab").Select(t => ((string?)t.Attribute(W + "val"), (string?)t.Attribute(W + "pos"))).ToList();
        Assert.Equal([("center", "4680"), ("right", "9360")], tabs);
        var gone = await Edit("report.docx", "", "no footer");
        Assert.Contains("Took the footer off every page.", gone.Text);
        Assert.DoesNotContain("Footer:", await Read("report.docx"));
        Assert.Contains("Header:", await Read("report.docx"));
        // Mixed with other lines they are refused, and nothing is changed: as Haiku gave them, they went in as text.
        var mixed = await Edit("report.docx", "", "page numbers at the bottom\nheadings: format: dark blue");
        Assert.True(mixed.IsError);
        Assert.Contains("old_text is the text (\"headings\" for every heading) and new_text \"format: dark blue\"", mixed.Text);
        Assert.DoesNotContain("format: dark blue", await Read("report.docx"));
        // Text that isn't such lines still goes at the end of the body.
        Assert.Contains("Added the text at the end", (await Edit("report.docx", "", "Page numbers are on every page.")).Text);
    }

    [Fact]
    public async Task A_footer_word_wrote_keeps_its_page_number_where_it_was()
    {
        Assert.False((await Write("report.docx", Report)).IsError);
        // Word's own page number in the middle of the footer: a field in runs, centred.
        using (var zip = ZipFile.Open(Path.Combine(_dir, "report.docx"), ZipArchiveMode.Update))
        {
            using (var footer = new StreamWriter(zip.CreateEntry("word/footer7.xml").Open()))
                footer.Write($"""<w:ftr xmlns:w="{W.NamespaceName}"><w:p><w:pPr><w:pStyle w:val="Footer"/><w:jc w:val="center"/></w:pPr><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE   \* MERGEFORMAT </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>2</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p></w:ftr>""");
            XDocument rels;
            using (var read = zip.GetEntry("word/_rels/document.xml.rels")!.Open())
                rels = XDocument.Load(read);
            rels.Root!.Add(new XElement(rels.Root.Name.Namespace + "Relationship", new XAttribute("Id", "rIdFoot"), new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer"), new XAttribute("Target", "footer7.xml")));
            zip.GetEntry("word/_rels/document.xml.rels")!.Delete();
            using (var write = zip.CreateEntry("word/_rels/document.xml.rels").Open())
                rels.Save(write);
            XDocument document;
            using (var read = zip.GetEntry("word/document.xml")!.Open())
                document = XDocument.Load(read);
            document.Descendants(W + "sectPr").Single().AddFirst(new XElement(W + "footerReference", new XAttribute(W + "type", "default"), new XAttribute(R + "id", "rIdFoot")));
            zip.GetEntry("word/document.xml")!.Delete();
            using (var write = zip.CreateEntry("word/document.xml").Open())
                document.Save(write);
        }
        Assert.Contains("Footer: {page}", await Read("report.docx"));
        var result = await Edit("report.docx", "", "footer: Confidential, on the left");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("\"Confidential\" on the left, \"{page}\" on the centre", result.Text);
        Assert.Contains("Footer: Confidential\t{page}", await Read("report.docx"));
        // The same part, changed: no second footer.
        Assert.Equal(" PAGE   \\* MERGEFORMAT ", (string?)Part("report.docx", "word/footer7.xml").Descendants(W + "fldSimple").Single().Attribute(W + "instr"));
    }

    [Fact]
    public async Task A_picture_goes_after_the_paragraph_given_or_at_the_end()
    {
        Assert.False((await Write("report.docx", Report)).IsError);
        var png = Path.Combine(_dir, "chart.png");
        using (var bitmap = new SkiaSharp.SKBitmap(800, 400))
        {
            bitmap.Erase(SkiaSharp.SKColors.SteelBlue);
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(png, data.ToArray());
        }
        // After a paragraph, as the paragraph with the picture's line under it.
        var after = await Edit("report.docx", "July was the best month.", "July was the best month.\n![Sales by month](chart.png)");
        Assert.False(after.IsError, after.Text);
        Assert.Contains("Put the picture chart.png (", after.Text);
        Assert.Contains("after the paragraph given", after.Text);
        var document = Part("report.docx", "word/document.xml");
        var paragraphs = document.Root!.Element(W + "body")!.Elements(W + "p").ToList();
        var at = paragraphs.FindIndex(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)) == "July was the best month.");
        Assert.NotNull(paragraphs[at + 1].Descendants().FirstOrDefault(e => e.Name.LocalName == "drawing"));
        Assert.Equal("Sales by month", (string?)document.Descendants().First(e => e.Name.LocalName == "docPr").Attribute("descr"));
        using (var zip = ZipFile.OpenRead(Path.Combine(_dir, "report.docx")))
        {
            Assert.NotNull(zip.GetEntry("word/media/image1.png"));
            using var types = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open());
            Assert.Contains("Extension=\"png\"", types.ReadToEnd());
        }
        // After a table, given by its last row as read_file shows it.
        Assert.False((await Write("table.docx", "# Sales\n\n| Month | Bikes |\n|---|---|\n| July | 41 |\n| August | 47 |\n\nSteady.")).IsError);
        var table = await Edit("table.docx", "| August | 47 |", "| August | 47 |\n![Bikes](chart.png)");
        Assert.False(table.IsError, table.Text);
        Assert.Contains("after the table given", table.Text);
        var tbl = Part("table.docx", "word/document.xml").Descendants(W + "tbl").Single();
        Assert.NotNull(tbl.ElementsAfterSelf().First().Descendants().FirstOrDefault(e => e.Name.LocalName == "drawing"));

        // At the end, old_text empty; a path that is no picture is said and nothing changes.
        Assert.Contains("at the end of the document", (await Edit("report.docx", "", "![Again](chart.png)")).Text);
        Assert.Equal(2, Part("report.docx", "word/document.xml").Descendants().Count(e => e.Name.LocalName == "drawing"));
        var missing = await Edit("report.docx", "", "![Gone](nowhere.png)");
        Assert.True(missing.IsError);
        Assert.Contains("can't be put in", missing.Text);
        var changed = await Edit("report.docx", "Steady.", "Steady and calm.\n![x](chart.png)");
        Assert.True(changed.IsError);
        Assert.Contains("change the text in an edit of its own", changed.Text);
    }

    [Fact]
    public async Task A_comment_goes_on_the_words_given_and_read_file_shows_it()
    {
        Assert.False((await Write("report.docx", Report)).IsError);
        // As asked of Haiku and Sonnet (2026-11-13): a comment on one word.
        var result = await Edit("report.docx", "best month", "comment: check the date with the council");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Put a comment on \"best month\": \"check the date with the council\", by Sloperator.", result.Text);
        Assert.Contains("Comments:\n- on \"best month\": check the date with the council", await Read("report.docx"));
        var document = Part("report.docx", "word/document.xml");
        Assert.Single(document.Descendants(W + "commentRangeStart"));
        Assert.Single(document.Descendants(W + "commentReference"));
        // The words are as they were, and a second comment gets an id of its own.
        Assert.Contains("July was the best month.", await Read("report.docx"));
        Assert.False((await Edit("report.docx", "Steady.", "comment: Why steady?")).IsError);
        Assert.Equal(["0", "1"], Part("report.docx", "word/comments.xml").Descendants(W + "comment").Select(c => (string?)c.Attribute(W + "id")));
        Assert.Contains("not found", (await Edit("report.docx", "nowhere at all", "comment: x")).Text);
    }

    [Fact]
    public async Task Headings_as_old_text_are_every_heading()
    {
        Assert.False((await Write("report.docx", Report)).IsError);
        var result = await Edit("report.docx", "headings", "format: dark blue");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Formatted all 3 headings (text #002060)", result.Text);
        var paragraphs = Part("report.docx", "word/document.xml").Descendants(W + "p").ToList();
        static string? Colour(XElement p) => (string?)p.Descendants(W + "color").FirstOrDefault()?.Attribute(W + "val");
        Assert.Equal(["002060", null, "002060", null, "002060", null], paragraphs.Where(p => p.Descendants(W + "t").Any()).Select(Colour));

        // A deck's slide titles the same way.
        Assert.False((await Write("deck.pptx", "# Q3\nBy region\n\n---\n\n# Sales\n\n- East leads\n\n---\n\n# Thank you")).IsError);
        var titles = await Edit("deck.pptx", "all slide titles", "format: dark blue, bold");
        Assert.False(titles.IsError, titles.Text);
        Assert.Contains("Formatted all 3 slide titles (bold, text #002060)", titles.Text);
    }
}
