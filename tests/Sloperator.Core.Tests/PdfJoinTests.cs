using System.Text.Json.Nodes;
using Sloperator.Core.Documents;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using UglyToad.PdfPig;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>write_file to a .pdf with "pages:" lines: a PDF made of pages of other PDFs, joined, taken out or reordered.</summary>
public class PdfJoinTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-pdfjoin-").FullName;

    public void Dispose() => TestFolder.Delete(_dir);

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content }, Ctx(), CancellationToken.None);

    /// <summary>The first word of each page, which says which page of which file it was.</summary>
    private List<string> Pages(string file)
    {
        using var pdf = PdfDocument.Open(Path.Combine(_dir, file));
        return [.. pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Take(2).Select(w => w.Text)))];
    }

    private async Task Sources()
    {
        Assert.False((await Write("lease.pdf", "Lease one\n\nThe flat is let from 1 March.")).IsError);
        Assert.False((await Write("inventory.pdf", "Inventory one\n\nKitchen.\n\n\\newpage\n\nInventory two\n\nLiving room.\n\n\\newpage\n\nInventory three\n\nBedroom.")).IsError);
        Assert.Equal(["Inventory one", "Inventory two", "Inventory three"], Pages("inventory.pdf"));
    }

    [Fact]
    public async Task Lines_of_pages_join_other_pdfs_in_their_order()
    {
        await Sources();
        // As asked of Haiku and Sonnet (2026-11-13): the lease first, then the inventory.
        var joined = await Write("flat.pdf", $"pages: {Path.Combine(_dir, "lease.pdf")}\npages: inventory.pdf");
        Assert.False(joined.IsError, joined.Text);
        Assert.Contains("flat.pdf: a PDF of 4 pages (lease.pdf: 1 page; inventory.pdf: 3 pages).", joined.Text);
        Assert.Equal(["Lease one", "Inventory one", "Inventory two", "Inventory three"], Pages("flat.pdf"));
        // The sources are as they were, and the result gets its card in the chat as any document made does.
        Assert.Equal(["Lease one"], Pages("lease.pdf"));
        Assert.Equal([Path.Combine(_dir, "flat.pdf")], joined.Files);

        // Some pages, backwards, the same file twice: taking out, dropping and reordering are the same lines.
        var some = await Write("some.pdf", "pages: inventory.pdf 3,1\npages: \"lease.pdf\"\nPages: inventory.pdf 2-");
        Assert.False(some.IsError, some.Text);
        Assert.Contains("(inventory.pdf: pages 3,1 of 3; lease.pdf: 1 page; inventory.pdf: pages 2- of 3)", some.Text);
        Assert.Equal(["Inventory three", "Inventory one", "Lease one", "Inventory two", "Inventory three"], Pages("some.pdf"));

        // Made here, it is the model's own to make again.
        Assert.False((await Write("some.pdf", "pages: inventory.pdf 2")).IsError);
        Assert.Equal(["Inventory two"], Pages("some.pdf"));
    }

    [Fact]
    public async Task What_cannot_be_joined_is_said()
    {
        await Sources();
        var missing = await Write("out.pdf", "pages: nowhere.pdf");
        Assert.True(missing.IsError);
        Assert.Contains("There is no", missing.Text);
        var past = await Write("out.pdf", "pages: inventory.pdf 2-9");
        Assert.True(past.IsError);
        Assert.Contains("inventory.pdf has 3 pages: there is no page 9.", past.Text);
        var mixed = await Write("out.pdf", "# Cover\n\npages: inventory.pdf");
        Assert.True(mixed.IsError);
        Assert.Contains("either written from Markdown or made of pages of other PDFs", mixed.Text);
        var itself = await Write("inventory.pdf", "pages: inventory.pdf 1");
        Assert.True(itself.IsError);
        Assert.Contains("can't be made of its own pages", itself.Text);
        File.WriteAllText(Path.Combine(_dir, "notes.pdf"), "not a PDF at all");
        var broken = await Write("out.pdf", "pages: notes.pdf");
        Assert.True(broken.IsError);
        Assert.Contains("notes.pdf could not be read as a PDF", broken.Text);
        Assert.False(File.Exists(Path.Combine(_dir, "out.pdf")));
        // A line of a document that only starts alike is text: the PDF is written from it.
        Assert.False((await Write("text.pdf", "Pages: 12, as agreed with the printer.")).IsError);
        Assert.Contains("Pages:", Pages("text.pdf")[0]);
    }

    [Theory]
    [InlineData(null, "1,2,3,4,5")]
    [InlineData("2-4", "2,3,4")]
    [InlineData("4-", "4,5")]
    [InlineData("-2", "1,2")]
    [InlineData("5, 1, 3-2", "5,1,3,2")]
    public void Pages_are_read_as_they_are_said(string? said, string pages) =>
        Assert.Equal(pages, string.Join(",", PdfJoin.Numbers(said, 5, "a.pdf")));
}
