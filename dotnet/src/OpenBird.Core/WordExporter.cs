using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace OpenBird.Core;

/// <summary>
/// Converts the Markdown that OpenBird writes (headings, bullets, check items,
/// quotes, tables, bold, code) into a Word document. Not a general Markdown converter.
/// </summary>
public static partial class WordExporter
{
    private const string BodyFont = "Segoe UI";
    private const string CodeFont = "Cascadia Code";

    [GeneratedRegex(@"(\*\*.+?\*\*|`[^`]+`|\*[^*\s][^*]*\*)")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableRuleRegex();

    public static string Export(string markdown, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed == "---") continue;

            if (trimmed.StartsWith('|'))
            {
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    var row = lines[i].Trim();
                    if (TableRuleRegex().IsMatch(row)) continue;
                    rows.Add(SplitRow(row));
                }
                i--;
                body.Append(BuildTable(rows));
                body.Append(new Paragraph());
                continue;
            }

            if (trimmed.StartsWith("#### ")) body.Append(Para(trimmed[5..], size: 22, bold: true, before: 160));
            else if (trimmed.StartsWith("### ")) body.Append(Para(trimmed[4..], size: 24, bold: true, before: 200));
            else if (trimmed.StartsWith("## ")) body.Append(Para(trimmed[3..], size: 30, bold: true, before: 280));
            else if (trimmed.StartsWith("# ")) body.Append(Para(trimmed[2..], size: 40, bold: true, before: 0));
            else if (trimmed.StartsWith("> ")) body.Append(Para(trimmed[2..], indent: 360, italic: true, color: "595959"));
            else if (trimmed.StartsWith("- [x] ") || trimmed.StartsWith("- [X] ")) body.Append(Para("☑ " + trimmed[6..], indent: 360));
            else if (trimmed.StartsWith("- [ ] ")) body.Append(Para("☐ " + trimmed[6..], indent: 360));
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) body.Append(Para("• " + trimmed[2..], indent: 360));
            else body.Append(Para(trimmed));
        }

        body.Append(new SectionProperties(new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134 }));
        main.Document.Save();
        return outputPath;
    }

    private static string[] SplitRow(string row)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inner = row.Trim().Trim('|');
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length && inner[i + 1] == '|') { current.Append('|'); i++; }
            else if (inner[i] == '|') { cells.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(inner[i]);
        }
        cells.Add(current.ToString().Trim());
        return cells.ToArray();
    }

    private static Table BuildTable(List<string[]> rows)
    {
        var table = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" })));
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        var grid = new TableGrid();
        for (var c = 0; c < width; c++) grid.Append(new GridColumn());
        table.Append(grid);
        for (var r = 0; r < rows.Count; r++)
        {
            var tr = new TableRow();
            for (var c = 0; c < width; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : "";
                var cell = new TableCell(Para(text, size: 18, bold: r == 0, after: 40));
                if (r == 0) cell.PrependChild(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" }));
                tr.Append(cell);
            }
            table.Append(tr);
        }
        return table;
    }

    private static Paragraph Para(string text, int size = 21, bool bold = false, bool italic = false,
        int indent = 0, int before = 0, int after = 100, string? color = null)
    {
        var p = new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines { Before = before.ToString(), After = after.ToString() },
            new Indentation { Left = indent.ToString() }));
        foreach (var part in InlineRegex().Split(text))
        {
            if (part.Length == 0) continue;
            bool b = bold, it = italic, code = false;
            var content = part;
            if (part.Length > 4 && part.StartsWith("**") && part.EndsWith("**")) { b = true; content = part[2..^2]; }
            else if (part.Length > 2 && part[0] == '`' && part[^1] == '`') { code = true; content = part[1..^1]; }
            else if (part.Length > 2 && part[0] == '*' && part[^1] == '*') { it = true; content = part[1..^1]; }

            var props = new RunProperties(new RunFonts { Ascii = code ? CodeFont : BodyFont, HighAnsi = code ? CodeFont : BodyFont });
            if (b) props.Append(new Bold());
            if (it) props.Append(new Italic());
            if (color is not null) props.Append(new Color { Val = color });
            props.Append(new FontSize { Val = (code ? size - 2 : size).ToString() });
            p.Append(new Run(props, new Text(content) { Space = SpaceProcessingModeValues.Preserve }));
        }
        return p;
    }
}
