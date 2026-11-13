using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBird.Core;
using OpenBird.Windows;

namespace OpenBird.App.Pages;

internal sealed class MeetingsPage : UserControl, IPage
{
    private readonly OpenBirdEngine _engine;
    private readonly MainWindow _window;
    private readonly TextBox _search = new() { PlaceholderText = "Filter by title or tag" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly MarkdownView _note = new();
    private readonly StackPanel _toolbar = Ui.Row(8);
    private readonly TextBlock _empty = Ui.Text("Select a meeting to read its notes.", secondary: true);
    private List<MeetingRecord> _meetings = new();
    private MeetingRecord? _current;

    public MeetingsPage(OpenBirdEngine engine, MainWindow window)
    {
        _engine = engine;
        _window = window;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_list, "Meetings");
        _search.TextChanged += (_, _) => Fill();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListViewItem { Tag: string id }) Select(id); };
        _note.CheckChanged += OnCheck;

        _toolbar.Children.Add(Ui.Button("Copy", "", () => { if (Markdown() is { } md) Ui.Copy(md); }));
        _toolbar.Children.Add(Ui.Button("Word", "", ExportWord));
        _toolbar.Children.Add(Ui.Button("Open file", "", () => Ui.Open(_current?.MarkdownPath)));
        _toolbar.Children.Add(Ui.Button("Tags", "", EditTags));
        _toolbar.Visibility = Visibility.Collapsed;

        var left = new Grid { RowSpacing = 8, Padding = new Thickness(16, 20, 8, 16) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var title = Ui.Text("Meetings", "TitleTextBlockStyle");
        Grid.SetRow(_search, 1);
        Grid.SetRow(_list, 2);
        left.Children.Add(title);
        left.Children.Add(_search);
        left.Children.Add(_list);

        var right = new Grid { RowSpacing = 12, Padding = new Thickness(16, 20, 28, 16) };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var scroller = new ScrollViewer { Content = Ui.Stack(8, _empty, _note), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        right.Children.Add(_toolbar);
        right.Children.Add(scroller);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;
    }

    public void OnShown() => Reload(_current?.Id);

    public void Reload(string? select = null)
    {
        _meetings = _engine.Database.ListMeetings(limit: 300);
        Fill(select);
    }

    private void Fill(string? select = null)
    {
        select ??= _current?.Id;
        var filter = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var m in _meetings)
        {
            if (filter.Length > 0 && !m.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) && !m.Tags.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var when = DateTime.TryParse(m.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToLocalTime().ToString("ddd d MMM, HH:mm") : m.CreatedAt;
            var meta = $"{when} · {Ui.Duration(m.DurationSec)}{(string.IsNullOrEmpty(m.Tags) ? "" : " · " + m.Tags)}";
            var body = Ui.Stack(2, Ui.Text(m.Title, "BodyStrongTextBlockStyle", wrap: false), Ui.Text(meta, "CaptionTextBlockStyle", secondary: true, wrap: false));
            body.Margin = new Thickness(0, 6, 0, 6);
            var item = new ListViewItem { Content = body, Tag = m.Id };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, $"{m.Title}, {meta}");
            _list.Items.Add(item);
            if (m.Id == select) _list.SelectedItem = item;
        }
        if (_list.Items.Count == 0) _empty.Text = _meetings.Count == 0 ? "No meetings yet. Start one from Home, or just start talking." : "No meeting matches the filter.";
    }

    private string? Markdown() => _current?.MarkdownPath is { } p && File.Exists(p) ? File.ReadAllText(p) : null;

    private void Select(string id)
    {
        _current = _engine.Database.GetMeeting(id);
        var md = Markdown();
        _toolbar.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = md is null ? Visibility.Visible : Visibility.Collapsed;
        if (md is null) _empty.Text = "The note file for this meeting is missing.";
        _note.Show(md ?? "");
    }

    private void OnCheck(string task, bool done)
    {
        var action = _current?.ActionItems.FirstOrDefault(a => a.Task == task) ?? _current?.ActionItems.FirstOrDefault(a => task.Contains(a.Task, StringComparison.Ordinal));
        if (action is null) return;
        _engine.Database.SetActionCompleted(action.Id, done);
    }

    private void ExportWord()
    {
        if (_current is null || Markdown() is not { } md) return;
        try
        {
            var path = Path.ChangeExtension(_current.MarkdownPath!, ".docx");
            WordExporter.Export(md, path);
            Ui.Open(path);
        }
        catch (IOException e)
        {
            _window.ShowNotice($"The Word file could not be written ({e.Message}). Close it in Word if it is open.", isError: true);
        }
    }

    private async void EditTags()
    {
        if (_current is null) return;
        var box = new TextBox { Text = _current.Tags, PlaceholderText = "Comma-separated, for example: Client-Acme, 1on1" };
        var dialog = new ContentDialog
        {
            Title = "Tags", Content = box, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _engine.Database.UpdateTags(_current.Id, box.Text.Trim());
        Reload(_current.Id);
    }
}
