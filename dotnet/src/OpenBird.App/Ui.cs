using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace OpenBird.App;

/// <summary>Small builders so pages read as layout, and everything uses the system's Fluent resources.</summary>
internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static CornerRadius Radius(string key) => (CornerRadius)Application.Current.Resources[key];

    public static TextBlock Text(string text, string? style = null, bool secondary = false, bool wrap = true)
    {
        var t = new TextBlock { Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (style is not null) t.Style = Style(style);
        if (secondary) t.Foreground = Brush("TextFillColorSecondaryBrush");
        return t;
    }

    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    public static Button Button(string text, string? glyph, Action onClick, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph is not null) content.Children.Add(Icon(glyph));
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = content };
        if (accent) b.Style = Style("AccentButtonStyle");
        b.Click += (_, _) => onClick();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>A rounded card on the layer colour, as used across Windows 11 settings pages.</summary>
    public static Border Card(UIElement content, double padding = 16) => new()
    {
        Child = content,
        Padding = new Thickness(padding),
        CornerRadius = Radius("OverlayCornerRadius"),
        Background = Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
    };

    public static StackPanel Stack(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A settings-style row: label and description on the left, a control on the right.</summary>
    public static Border SettingRow(string title, string? description, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = Stack(2, Text(title));
        if (description is not null) labels.Children.Add(Text(description, "CaptionTextBlockStyle", secondary: true));
        labels.VerticalAlignment = VerticalAlignment.Center;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(labels);
        grid.Children.Add(control);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title);
        return Card(grid, 14);
    }

    public static ScrollViewer Page(string title, params UIElement[] sections)
    {
        var heading = Text(title, "TitleTextBlockStyle");
        heading.Margin = new Thickness(0, 0, 0, 4);
        return PageWith(heading, sections);
    }

    /// <summary>Same page layout with a custom header in place of the plain title (used by Home).</summary>
    public static ScrollViewer PageWith(UIElement header, params UIElement[] sections)
    {
        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(40, 24, 40, 40), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(header);
        foreach (var s in sections)
        {
            // Section headings get extra space above so groups read as groups (Windows settings pages do the same).
            if (s is TextBlock { Style: { } st } t && ReferenceEquals(st, Style("SubtitleTextBlockStyle"))) t.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(s);
        }
        return new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false };
    }

    /// <summary>An InfoBar for messages inside a page. Closed until <see cref="Show"/> is called.</summary>
    public static InfoBar Notice(InfoBarSeverity severity = InfoBarSeverity.Warning) => new() { IsOpen = false, IsClosable = true, Severity = severity };

    public static void Show(this InfoBar bar, string message, InfoBarSeverity? severity = null)
    {
        if (severity is not null) bar.Severity = severity.Value;
        bar.Message = message;
        bar.IsOpen = true;
    }

    /// <summary>What a page shows before it has content: a glyph, a heading, one line of help and optional actions.</summary>
    public static StackPanel EmptyState(string glyph, string title, string body, params UIElement[] actions)
    {
        var icon = Icon(glyph, 40);
        icon.Foreground = Brush("AccentTextFillColorPrimaryBrush");
        var panel = Stack(8, icon, Text(title, "SubtitleTextBlockStyle"), Text(body, secondary: true));
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Padding = new Thickness(0, 24, 0, 8);
        foreach (var a in actions) panel.Children.Add(a);
        foreach (var t in panel.Children.OfType<TextBlock>()) t.TextAlignment = TextAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        return panel;
    }

    /// <summary>A rounded suggestion button, used for example questions.</summary>
    public static Button Chip(string text, Action onClick)
    {
        var b = new Button { Content = text, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 6, 14, 6) };
        b.Click += (_, _) => onClick();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>Chips stacked in a centred column, which also works in a narrow window.</summary>
    public static StackPanel ChipColumn(params Button[] chips)
    {
        var panel = Stack(8, chips);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var c in chips) c.HorizontalAlignment = HorizontalAlignment.Stretch;
        return panel;
    }

    public static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    /// <summary>Opens a file or folder with its default program.</summary>
    public static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

}
