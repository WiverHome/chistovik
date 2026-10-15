using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShablonRabot.App.Controls;

/// <summary>
/// Простая разметка статей базы знаний в FlowDocument: # ## ### заголовки, абзацы, списки «- » и «1. »,
/// таблицы «| … |», врезки «> », **полужирный**, `код` и ссылки [текст](адрес). Цвета — из темы приложения.
/// </summary>
public static partial class MarkdownDocument
{
    [GeneratedRegex(@"\*\*(.+?)\*\*|`(.+?)`|\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex Inline();

    [GeneratedRegex(@"^\d+\.\s+")]
    private static partial Regex Numbered();

    public static FlowDocument Build(string markdown, Action<string> navigate)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = 14, LineHeight = 22, TextAlignment = TextAlignment.Left };
        doc.SetResourceReference(FlowDocument.FontFamilyProperty, "Font.UI");
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.Text.Primary");
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            AddInlines(p.Inlines, string.Join(" ", paragraph), navigate);
            doc.Blocks.Add(p);
            paragraph.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();
            if (line.StartsWith("# "))
            {
                FlushParagraph();
                var h = new Paragraph(new Run(line[2..])) { FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), LineHeight = 32 };
                doc.Blocks.Add(h);
            }
            else if (line.StartsWith("## "))
            {
                FlushParagraph();
                doc.Blocks.Add(new Paragraph(new Run(line[3..])) { FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8), LineHeight = 26 });
            }
            else if (line.StartsWith("### "))
            {
                FlushParagraph();
                doc.Blocks.Add(new Paragraph(new Run(line[4..])) { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
            }
            else if (line.StartsWith("> "))
            {
                FlushParagraph();
                var text = new List<string>();
                while (i < lines.Length && lines[i].StartsWith("> ")) text.Add(lines[i++][2..]);
                i--;
                var p = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(12, 8, 12, 8) };
                p.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)Application.Current.FindResource("Font.Icons") });
                AddInlines(p.Inlines, string.Join(" ", text), navigate);
                var section = new Section(p) { Margin = new Thickness(0, 4, 0, 16), BorderThickness = new Thickness(3, 0, 0, 0) };
                section.SetResourceReference(Section.BackgroundProperty, "Brush.Fill.AccentSubtle");
                section.SetResourceReference(Section.BorderBrushProperty, "Brush.Accent.Default");
                doc.Blocks.Add(section);
            }
            else if (line.StartsWith("- ") || Numbered().IsMatch(line))
            {
                FlushParagraph();
                var numbered = !line.StartsWith("- ");
                var list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(24, 0, 0, 0) };
                while (i < lines.Length && (numbered ? Numbered().IsMatch(lines[i]) : lines[i].StartsWith("- ")))
                {
                    var content = numbered ? Numbered().Replace(lines[i], "") : lines[i][2..];
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                    AddInlines(p.Inlines, content, navigate);
                    list.ListItems.Add(new ListItem(p));
                    i++;
                }
                i--;
                doc.Blocks.Add(list);
            }
            else if (line.StartsWith('|'))
            {
                FlushParagraph();
                var rows = new List<string[]>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (!cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$"))) rows.Add(cells);
                    i++;
                }
                i--;
                doc.Blocks.Add(BuildTable(rows, navigate));
            }
            else if (line.Length == 0) FlushParagraph();
            else paragraph.Add(line);
            i++;
        }
        FlushParagraph();
        return doc;
    }

    static Table BuildTable(List<string[]> rows, Action<string> navigate)
    {
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 16), BorderThickness = new Thickness(1) };
        table.SetResourceReference(Table.BorderBrushProperty, "Brush.Stroke.Card");
        for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) row.SetResourceReference(TableRow.BackgroundProperty, "Brush.Bg.CardSecondary");
            for (var c = 0; c < columns; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0), FontSize = 13, LineHeight = 19 };
                AddInlines(p.Inlines, c < rows[r].Length ? rows[r][c] : "", navigate);
                if (r == 0) p.FontWeight = FontWeights.SemiBold;
                var cell = new TableCell(p) { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, r == 0 ? 0 : 1, c == 0 ? 0 : 1, 0) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Brush.Stroke.Divider");
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    static void AddInlines(InlineCollection inlines, string text, Action<string> navigate)
    {
        var pos = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            if (m.Groups[1].Success) inlines.Add(new Bold(new Run(m.Groups[1].Value)));
            else if (m.Groups[2].Success)
            {
                var code = new Run(m.Groups[2].Value);
                code.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
                inlines.Add(code);
            }
            else
            {
                var target = m.Groups[4].Value;
                var link = new Hyperlink(new Run(m.Groups[3].Value)) { Cursor = System.Windows.Input.Cursors.Hand };
                link.SetResourceReference(TextElement.ForegroundProperty, "Brush.Accent.Text");
                link.Click += (_, _) => navigate(target);
                System.Windows.Automation.AutomationProperties.SetName(link, m.Groups[3].Value);
                inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }
}
