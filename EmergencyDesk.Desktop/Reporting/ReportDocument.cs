namespace EmergencyDesk.Reporting;

/// <summary>
/// A report described once, as plain blocks (headings, key/value lists, tables).
/// The same document is rendered to PDF, CSV or e-mail text, so the three always agree.
/// </summary>
public class ReportDoc
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<ReportBlock> Blocks { get; } = new();

    public ReportDoc Heading(string text) { Blocks.Add(new HeadingBlock(text)); return this; }
    public ReportDoc Paragraph(string text, bool muted = false) { Blocks.Add(new ParagraphBlock(text, muted)); return this; }

    public ReportDoc KeyValues(params (string Key, string Value)[] items)
    {
        Blocks.Add(new KeyValueBlock(items));
        return this;
    }

    public ReportDoc Table(string[] columns, double[] widths, IEnumerable<string[]> rows)
    {
        Blocks.Add(new TableBlock(columns, widths, rows.ToList()));
        return this;
    }
}

public abstract record ReportBlock;
public sealed record HeadingBlock(string Text) : ReportBlock;
public sealed record ParagraphBlock(string Text, bool Muted) : ReportBlock;
public sealed record KeyValueBlock(IReadOnlyList<(string Key, string Value)> Items) : ReportBlock;

/// <param name="Widths">Relative column widths (any scale; they are normalised to the page width).</param>
public sealed record TableBlock(IReadOnlyList<string> Columns, IReadOnlyList<double> Widths,
                                IReadOnlyList<string[]> Rows) : ReportBlock;
