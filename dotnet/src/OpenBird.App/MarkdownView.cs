using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace OpenBird.App;

/// <summary>
/// Shows the Markdown OpenBird writes (headings, bullets, check items, quotes,
/// tables, bold, italic, code) with native text controls. No browser engine.
/// </summary>
internal sealed partial class MarkdownView : UserControl
{
    private readonly StackPanel _panel = new() { Spacing = 6 };

    /// <summary>Raised when a "- [ ]" item is ticked or unticked: (task text, completed).</summary>
    public event Action<string, bool>? CheckChanged;

    public MarkdownView() => Content = _panel;

    [GeneratedRegex(@"(\*\*.+?\*\*|`[^`]+`|\*[^*\s][^*]*\*)")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableRuleRegex();

    public void Show(string markdown)
    {
        _panel.Children.Clear();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line == "---") { _panel.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), Background = Ui.Brush("DividerStrokeColorDefaultBrush") }); continue; }

            if (line.StartsWith('|'))
            {
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    var row = lines[i].Trim();
                    if (!TableRuleRegex().IsMatch(row)) rows.Add(row.Trim('|').Split('|').Select(c => c.Trim()).ToArray());
                }
                i--;
                _panel.Children.Add(Table(rows));
            }
            else if (line.StartsWith("#### ")) _panel.Children.Add(Block(line[5..], "BodyStrongTextBlockStyle", top: 6));
            else if (line.StartsWith("### ")) _panel.Children.Add(Block(line[4..], "BodyStrongTextBlockStyle", top: 8));
            else if (line.StartsWith("## ")) _panel.Children.Add(Block(line[3..], "SubtitleTextBlockStyle", top: 14));
            else if (line.StartsWith("# ")) _panel.Children.Add(Block(line[2..], "TitleTextBlockStyle"));
            else if (line.StartsWith("> ")) _panel.Children.Add(Quote(line[2..]));
            else if (line.StartsWith("- [ ] ") || line.StartsWith("- [x] ") || line.StartsWith("- [X] ")) _panel.Children.Add(Check(line[6..], line[3] != ' '));
            else if (line.StartsWith("- ") || line.StartsWith("* ")) _panel.Children.Add(Bullet(line[2..]));
            else _panel.Children.Add(Block(line, "BodyTextBlockStyle"));
        }
    }

    private static TextBlock Block(string text, string style, double top = 0)
    {
        var t = new TextBlock { Style = Ui.Style(style), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(0, top, 0, 0) };
        AddInlines(t.Inlines, text);
        return t;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        foreach (var part in InlineRegex().Split(text))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**") && part.EndsWith("**")) inlines.Add(new Run { Text = part[2..^2], FontWeight = FontWeights.SemiBold });
            else if (part.Length > 2 && part[0] == '`' && part[^1] == '`') inlines.Add(new Run { Text = part[1..^1], FontFamily = new FontFamily("Cascadia Code, Consolas") });
            else if (part.Length > 2 && part[0] == '*' && part[^1] == '*') inlines.Add(new Run { Text = part[1..^1], FontStyle = global::Windows.UI.Text.FontStyle.Italic });
            else inlines.Add(new Run { Text = part });
        }
    }

    private static UIElement Quote(string text)
    {
        var body = Block(text, "BodyTextBlockStyle");
        body.Foreground = Ui.Brush("TextFillColorSecondaryBrush");
        return new Border
        {
            Child = body, Padding = new Thickness(12, 6, 8, 6), CornerRadius = new CornerRadius(4),
            BorderBrush = Ui.Brush("AccentFillColorDefaultBrush"), BorderThickness = new Thickness(3, 0, 0, 0),
            Background = Ui.Brush("SubtleFillColorSecondaryBrush"),
        };
    }

    private static UIElement Bullet(string text)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(8, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = "•" });
        var body = Block(text, "BodyTextBlockStyle");
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private UIElement Check(string text, bool done)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AddInlines(label.Inlines, text);
        var box = new CheckBox { Content = label, IsChecked = done, Margin = new Thickness(4, 0, 0, 0) };
        // The task is the text after "**Owner**: " and before a trailing "*(from ...)*" note.
        var task = Regex.Replace(Regex.Replace(text, @"^\*\*.+?\*\*:\s*", ""), @"\s*\*\(.*\)\*\s*$", "");
        box.Click += (_, _) => CheckChanged?.Invoke(task, box.IsChecked == true);
        return box;
    }

    private static UIElement Table(List<string[]> rows)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = Block(rows[r][c].Replace("\\|", "|"), r == 0 ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle");
                cell.MaxWidth = 420;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return Ui.Card(grid, 12);
    }
}
