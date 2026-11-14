namespace Sloperator.Core.Documents;

/// <summary>
/// How edit_file changes a Word, Excel or PowerPoint file beyond replacing its text: the lines it takes for
/// looks, rows, charts, pages and slides. They were 500 tokens of edit_file's description, in every request of
/// every chat, most of which never touch such a file (2026-11-14). The description now says what can be done,
/// and how to write it comes with a chat's first read_file, write_file or edit_file of a file of that kind,
/// as the comfyui and video tools' guides come with their first call.
/// </summary>
internal static class DocumentGuide
{
    /// <summary>How a kind's guide starts; null for a kind edit_file doesn't change.</summary>
    private static string? Head(string? kind) => kind switch
    {
        "docx" => "[How edit_file changes a Word document, beyond replacing text",
        "xlsx" => "[How edit_file changes a workbook, beyond replacing text in its cells",
        "pptx" => "[How edit_file changes a presentation, beyond replacing text",
        _ => null,
    };

    /// <summary>The guide for a kind (<see cref="DocumentFiles.EditKind"/>): each form as models first tried it, and as the description gave it before.</summary>
    public static string? Text(string? kind) => kind switch
    {
        "docx" => Head(kind) + ":\n" +
            "- Text: old_text as read_file shows it; new_text may use **bold** and *italic*, a line break in it starts a new paragraph, and \"# \" at a line's start makes a heading. With old_text empty, new_text goes at the document's end.\n" +
            "- Looks: new_text \"format: centered, dark blue, size 20\" changes how old_text looks instead of what it says (also bold, italic, underlined, a fill; a table's rows take the fill); old_text \"headings\" is every heading.\n" +
            "- A comment: new_text \"comment: …\" comments on old_text.\n" +
            "- Every page, with old_text empty: \"page numbers at the bottom\", \"footer left: Confidential\", \"header right: Page {page} of {pages}\", \"no footer\"; \"table of contents\" puts one after the title.\n" +
            "- A picture: a line \"![Chart](C:\\chart.png)\" after old_text's paragraph in new_text puts it there.]",
        "xlsx" => Head(kind) + ":\n" +
            "- Cells as read_file shows them: old_text the cells to change (\"B7 =SUM(B2:B6)\") or empty, new_text the cells to set, one a line " +
            "(\"B7 =SUM(B2:B7)\", \"A8 Total\", \"B2 $1,200.00\" to show it in dollars, \"Budget!C9 12.5\" on another sheet).\n" +
            "- Rows and columns, with old_text empty: new_text starting with a line \"insert row 7\" adds a row between others (above a total), its cells on the lines after: " +
            "the rows below move down with every formula pointing at them, and the new row is formatted like the one above; \"delete rows 3-4\"; \"insert column C\", \"delete columns B-C\".\n" +
            "- Looks: \"format A1:E1 bold, yellow fill\" (also text colour, center, wrap, border), \"format E2:F73 as dollars\" for how numbers show, " +
            "\"column A width 20\", \"autofit columns\", \"merge A1:F1\".\n" +
            "- Order and view: \"sort A2:F73 by F descending\", \"freeze row 1\", \"filter A1:F73\", \"highlight A2:F73 red where F > 3000\", \"dropdown C2:C73: Yes, No\".\n" +
            "- Charts: \"add chart line A1:A13,E1:E13 Monthly totals\" (bar, line, pie or area): the first column the labels, each other a series named by its top cell, " +
            "or by rows (\"A1:D1,A6:D6\"). To change a chart give it as old_text (\"chart 1\" or its title) and its new line as new_text (\"chart: line Totals\"), empty to delete it.]",
        "pptx" => Head(kind) + ":\n" +
            "- Text: old_text as read_file shows it, after the slide's \"--- slide 2 of 5 ---\" line when other slides have the same words; " +
            "new_text may use **bold** and *italic*, and a line break in it starts a new paragraph. Lines after a slide's title or chart become the slide's text.\n" +
            "- Looks: new_text \"format: centered, dark blue, size 20\" changes how old_text looks instead of what it says (also bold, italic, underlined); old_text \"all slide titles\" is every title.\n" +
            "- Slides, with old_text empty: \"delete slide 3\", \"move slide 4 to 1\" (or \"to the end\", \"after slide 2\"), \"duplicate slide 2\", " +
            "\"add slide after 2\" with the new slide's title and points on the lines under it.]",
        _ => null,
    };
}
