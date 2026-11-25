using System.Text;
using System.Text.RegularExpressions;

namespace EmergencyDesk.Reporting;

/// <summary>Turns a <see cref="ReportDoc"/> into PDF bytes, CSV bytes or plain text (for e-mail bodies).</summary>
public static class ReportExporter
{
    public static byte[] ToPdf(ReportDoc doc) => PdfRenderer.Render(doc);

    // ------------------------------------------------------------------ CSV
    /// <summary>
    /// CSV in sections: title, then each block in turn (key/value pairs as two columns, tables with a header row),
    /// separated by blank rows. UTF-8 with a byte-order mark so Excel shows Filipino letters correctly.
    /// </summary>
    public static byte[] ToCsvBytes(ReportDoc doc)
    {
        var enc = new UTF8Encoding(true);
        return enc.GetPreamble().Concat(enc.GetBytes(ToCsv(doc))).ToArray();
    }

    public static string ToCsv(ReportDoc doc)
    {
        var sb = new StringBuilder();
        void Row(params string[] cells) => sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");

        Row(doc.Title);
        if (doc.Subtitle.Length > 0) Row(doc.Subtitle);

        foreach (var block in doc.Blocks)
        {
            Row();   // blank separator row
            switch (block)
            {
                case HeadingBlock h: Row(h.Text); break;
                case ParagraphBlock p: Row(p.Text); break;
                case KeyValueBlock kv:
                    foreach (var (k, v) in kv.Items) Row(k, v);
                    break;
                case TableBlock t:
                    Row(t.Columns.ToArray());
                    foreach (var r in t.Rows) Row(r);
                    break;
            }
        }
        return sb.ToString();
    }

    private static readonly Regex NumberLike = new(@"^[+-]?[0-9][0-9 ()\-.,:]*$", RegexOptions.Compiled);

    /// <summary>
    /// Quotes a CSV cell and neutralises spreadsheet formulas. Descriptions and names come from the public app,
    /// so a report written as "=HYPERLINK(...)" must not run when the file is opened in Excel.
    /// </summary>
    internal static string Cell(string? value)
    {
        var s = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (s.Length > 0)
        {
            var c = s[0];
            var risky = c is '=' or '@' or '\t' || (c is '+' or '-' && !NumberLike.IsMatch(s));
            if (risky) s = "'" + s;
        }
        if (s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ------------------------------------------------------------------ plain text
    public static string ToPlainText(ReportDoc doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine(doc.Title);
        sb.AppendLine(new string('=', Math.Min(72, Math.Max(doc.Title.Length, 8))));
        if (doc.Subtitle.Length > 0) sb.AppendLine(doc.Subtitle);

        foreach (var block in doc.Blocks)
        {
            sb.AppendLine();
            switch (block)
            {
                case HeadingBlock h:
                    sb.AppendLine(h.Text);
                    sb.AppendLine(new string('-', Math.Min(72, Math.Max(h.Text.Length, 4))));
                    break;
                case ParagraphBlock p:
                    sb.AppendLine(p.Text);
                    break;
                case KeyValueBlock kv:
                    var w = kv.Items.Count == 0 ? 0 : kv.Items.Max(i => i.Key.Length);
                    foreach (var (k, v) in kv.Items) sb.AppendLine($"{k.PadRight(w)}  {v}");
                    break;
                case TableBlock t:
                    sb.AppendLine(string.Join(" | ", t.Columns));
                    foreach (var r in t.Rows) sb.AppendLine(string.Join(" | ", r));
                    if (t.Rows.Count == 0) sb.AppendLine("(no data)");
                    break;
            }
        }
        return sb.ToString();
    }

    public static string SafeFileName(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "report" : cleaned;
    }
}
