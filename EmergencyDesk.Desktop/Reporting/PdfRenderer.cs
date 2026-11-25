using System.Globalization;
using System.Text;

namespace EmergencyDesk.Reporting;

/// <summary>
/// Minimal PDF writer (no NuGet packages needed): A4 pages, Helvetica text, wrapped paragraphs and tables
/// with a repeating header row, and "Page x of y" footers. Only Western-European characters are supported
/// (WinAnsi); anything else is printed as "?".
/// </summary>
public static class PdfRenderer
{
    private const double PageW = 595, PageH = 842;
    private const double MarginX = 48, MarginTop = 52, MarginBottom = 56;
    private const double ContentW = PageW - 2 * MarginX;
    private const int MaxCellLines = 30;

    // Helvetica advance widths (1/1000 em) for ASCII 32..126.
    private static readonly int[] Helv =
    {
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,   // space ! " # $ % & ' ( ) * + , - . /
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556,                                  // 0-9
        278, 278, 584, 584, 584, 556, 1015,                                                // : ; < = > ? @
        667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667,    // A-P
        778, 722, 667, 611, 722, 667, 944, 667, 667, 611,                                  // Q-Z
        278, 278, 278, 469, 556, 333,                                                      // [ \ ] ^ _ `
        556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556,    // a-p
        556, 333, 500, 278, 556, 500, 722, 500, 500, 500,                                  // q-z
        334, 260, 334, 584                                                                 // { | } ~
    };

    // Characters in the Windows-1252 block 0x80-0x9F that we know how to print.
    private static readonly Dictionary<char, byte> Cp1252 = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87,
        ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91,
        ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98,
        ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B, ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F
    };

    // ------------------------------------------------------------------ text helpers
    internal static string ToWinAnsi(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\n') { sb.Append('\n'); continue; }
            if (c == '\r') continue;
            if (c == '\t' || c < 32 || c == 127) { sb.Append(' '); continue; }
            if (c < 127 || (c >= 160 && c <= 255)) { sb.Append(c); continue; }
            if (Cp1252.TryGetValue(c, out var b)) { sb.Append((char)b); continue; }
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;   // emoji etc.
            sb.Append('?');
        }
        return sb.ToString();
    }

    private static double CharWidth(char c)
    {
        if (c >= 32 && c <= 126) return Helv[c - 32];
        return c switch
        {
            (char)0x95 => 350,                       // bullet
            (char)0x96 => 556,                       // en dash
            (char)0x97 or (char)0x85 or (char)0x99 => 1000,   // em dash, ellipsis, trademark
            (char)0x91 or (char)0x92 => 222,
            (char)0x93 or (char)0x94 => 333,
            _ => 556
        };
    }

    internal static double TextWidth(string s, double size, bool bold)
    {
        double w = 0;
        foreach (var c in s) w += CharWidth(c);
        return w * size / 1000.0 * (bold ? 1.08 : 1.0);
    }

    /// <summary>Wraps already-sanitised text (may contain '\n') into lines no wider than maxWidth.</summary>
    internal static List<string> Wrap(string text, double maxWidth, double size, bool bold)
    {
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            if (para.Trim().Length == 0) { lines.Add(""); continue; }

            var cur = new StringBuilder();
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = cur.Length == 0 ? word : cur + " " + word;
                if (TextWidth(candidate, size, bold) <= maxWidth) { cur.Clear(); cur.Append(candidate); continue; }

                if (cur.Length > 0) { lines.Add(cur.ToString()); cur.Clear(); }

                // A single word wider than the line (long URL, no spaces): cut it by characters.
                var rest = word;
                while (TextWidth(rest, size, bold) > maxWidth && rest.Length > 1)
                {
                    var n = 1;
                    while (n < rest.Length && TextWidth(rest[..(n + 1)], size, bold) <= maxWidth) n++;
                    lines.Add(rest[..n]);
                    rest = rest[n..];
                }
                cur.Append(rest);
            }
            lines.Add(cur.ToString());
        }
        return lines;
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    // ------------------------------------------------------------------ page builder
    private sealed class Builder
    {
        public readonly List<StringBuilder> Pages = new();
        public StringBuilder Cur = null!;
        public double Y;                                  // distance from the top edge of the page

        public Builder() => NewPage();

        public void NewPage()
        {
            Cur = new StringBuilder();
            Pages.Add(Cur);
            Y = MarginTop;
        }

        public void Ensure(double h)
        {
            if (Y + h > PageH - MarginBottom) NewPage();
        }

        public void Text(double x, double baselineFromTop, string s, double size, bool bold, double gray = 0, double[]? rgb = null)
        {
            if (s.Length == 0) return;
            var color = rgb is null ? $"{N(gray)} g" : $"{N(rgb[0])} {N(rgb[1])} {N(rgb[2])} rg";
            Cur.Append("BT ").Append(color).Append(" /").Append(bold ? "F2" : "F1").Append(' ').Append(N(size))
               .Append(" Tf ").Append(N(x)).Append(' ').Append(N(PageH - baselineFromTop))
               .Append(" Td (").Append(Esc(s)).Append(") Tj ET\n");
        }

        public void Rect(double x, double topY, double w, double h, double gray)
        {
            Cur.Append(N(gray)).Append(" g ").Append(N(x)).Append(' ').Append(N(PageH - topY - h)).Append(' ')
               .Append(N(w)).Append(' ').Append(N(h)).Append(" re f\n");
        }

        public void Line(double x1, double y, double x2, double gray, double width = 0.5)
        {
            Cur.Append(N(gray)).Append(" G ").Append(N(width)).Append(" w ").Append(N(x1)).Append(' ')
               .Append(N(PageH - y)).Append(" m ").Append(N(x2)).Append(' ').Append(N(PageH - y)).Append(" l S\n");
        }
    }

    // ------------------------------------------------------------------ public API
    public static byte[] Render(ReportDoc doc)
    {
        var b = new Builder();

        // Title block
        var title = ToWinAnsi(doc.Title);
        foreach (var line in Wrap(title, ContentW, 18, true))
        {
            b.Text(MarginX, b.Y + 18, line, 18, true, rgb: new[] { 0.75, 0.15, 0.18 });
            b.Y += 24;
        }
        foreach (var line in Wrap(ToWinAnsi(doc.Subtitle), ContentW, 9, false))
        {
            b.Text(MarginX, b.Y + 9, line, 9, false, 0.4);
            b.Y += 13;
        }
        b.Y += 4;
        b.Line(MarginX, b.Y, PageW - MarginX, 0.75, 1.2);
        b.Y += 14;

        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock h: DrawHeading(b, h); break;
                case ParagraphBlock p: DrawParagraph(b, p); break;
                case KeyValueBlock kv: DrawKeyValues(b, kv); break;
                case TableBlock t: DrawTable(b, t); break;
            }
        }

        return Assemble(b, ToWinAnsi(doc.Title));
    }

    private static void DrawHeading(Builder b, HeadingBlock h)
    {
        b.Y += 8;
        b.Ensure(40);               // never leave a heading alone at the bottom of a page
        b.Text(MarginX, b.Y + 12, ToWinAnsi(h.Text), 12, true);
        b.Y += 17;
        b.Line(MarginX, b.Y, PageW - MarginX, 0.8);
        b.Y += 8;
    }

    private static void DrawParagraph(Builder b, ParagraphBlock p)
    {
        const double size = 9.5, lh = 13;
        foreach (var line in Wrap(ToWinAnsi(p.Text), ContentW, size, false))
        {
            b.Ensure(lh);
            b.Text(MarginX, b.Y + size, line, size, false, p.Muted ? 0.45 : 0);
            b.Y += lh;
        }
        b.Y += 4;
    }

    private static void DrawKeyValues(Builder b, KeyValueBlock kv)
    {
        const double size = 9.5, lh = 13, keyW = 130, gap = 8;
        foreach (var (key, value) in kv.Items)
        {
            var lines = Wrap(ToWinAnsi(value.Length == 0 ? "—" : value), ContentW - keyW - gap, size, false);
            for (var i = 0; i < lines.Count; i++)
            {
                b.Ensure(lh);
                if (i == 0) b.Text(MarginX, b.Y + size, ToWinAnsi(key), size, true, 0.25);
                b.Text(MarginX + keyW + gap, b.Y + size, lines[i], size, false);
                b.Y += lh;
            }
            b.Y += 2;
        }
        b.Y += 4;
    }

    private static void DrawTable(Builder b, TableBlock t)
    {
        const double size = 8.5, lh = 11.5, padX = 4, padY = 3;
        var total = t.Widths.Sum();
        var widths = t.Widths.Select(w => w / total * ContentW).ToArray();
        var xs = new double[widths.Length];
        double acc = MarginX;
        for (var i = 0; i < widths.Length; i++) { xs[i] = acc; acc += widths[i]; }

        void Header()
        {
            b.Ensure(lh + 2 * padY);
            b.Rect(MarginX, b.Y, ContentW, lh + 2 * padY, 0.92);
            for (var c = 0; c < t.Columns.Count; c++)
                b.Text(xs[c] + padX, b.Y + padY + size, Truncate(ToWinAnsi(t.Columns[c]), widths[c] - 2 * padX, size, true), size, true, 0.2);
            b.Y += lh + 2 * padY;
        }

        Header();
        if (t.Rows.Count == 0)
        {
            b.Ensure(lh + 6);
            b.Text(MarginX + padX, b.Y + padY + size, "No data for this period.", size, false, 0.45);
            b.Y += lh + 2 * padY;
            b.Y += 6;
            return;
        }

        foreach (var row in t.Rows)
        {
            var cells = new List<List<string>>();
            for (var c = 0; c < widths.Length; c++)
            {
                var text = c < row.Length ? ToWinAnsi(row[c]) : "";
                var lines = Wrap(text, widths[c] - 2 * padX, size, false);
                if (lines.Count > MaxCellLines)
                {
                    lines = lines.GetRange(0, MaxCellLines);
                    lines[^1] += " ...";
                }
                cells.Add(lines);
            }

            var rowH = cells.Max(l => l.Count) * lh + 2 * padY;
            if (b.Y + rowH > PageH - MarginBottom)
            {
                b.NewPage();
                Header();
            }

            for (var c = 0; c < cells.Count; c++)
                for (var l = 0; l < cells[c].Count; l++)
                    b.Text(xs[c] + padX, b.Y + padY + size + l * lh, cells[c][l], size, false);

            b.Y += rowH;
            b.Line(MarginX, b.Y, PageW - MarginX, 0.88, 0.4);
        }
        b.Y += 8;
    }

    private static string Truncate(string s, double maxWidth, double size, bool bold)
    {
        if (TextWidth(s, size, bold) <= maxWidth) return s;
        while (s.Length > 1 && TextWidth(s + "...", size, bold) > maxWidth) s = s[..^1];
        return s + "...";
    }

    // ------------------------------------------------------------------ file structure
    private static byte[] Assemble(Builder b, string footerTitle)
    {
        var pageCount = b.Pages.Count;
        for (var i = 0; i < pageCount; i++)
        {
            b.Cur = b.Pages[i];
            b.Line(MarginX, PageH - 40, PageW - MarginX, 0.8, 0.5);
            b.Text(MarginX, PageH - 28, "Emergency Desk · " + Truncate(footerTitle, 330, 8, false), 8, false, 0.45);
            var label = $"Page {i + 1} of {pageCount}";
            b.Text(PageW - MarginX - TextWidth(label, 8, false), PageH - 28, label, 8, false, 0.45);
        }

        // Every char below is 0..255, so one char == one byte and string offsets are byte offsets.
        var sb = new StringBuilder();
        var offsets = new List<int>();

        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        sb.Append("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{5 + 2 * i} 0 R"));
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < pageCount; i++)
        {
            var contentId = 6 + 2 * i;
            Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageW)} {N(PageH)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
            var content = b.Pages[i].ToString();
            Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
