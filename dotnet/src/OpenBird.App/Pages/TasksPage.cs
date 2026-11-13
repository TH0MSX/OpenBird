using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBird.Core;
using OpenBird.Windows;

namespace OpenBird.App.Pages;

internal sealed class TasksPage : UserControl, IPage
{
    private readonly OpenBirdEngine _engine;
    private readonly MainWindow _window;
    private readonly StackPanel _tasks = new() { Spacing = 4 };
    private readonly MarkdownView _digest = new();
    private readonly CalendarDatePicker _day = new() { Date = DateTimeOffset.Now };

    public TasksPage(OpenBirdEngine engine, MainWindow window)
    {
        _engine = engine;
        _window = window;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_day, "Digest day");
        var export = Ui.Button("Save as tasks.md", "", () =>
        {
            var path = TasksExporter.Export(_engine.Database.GetActions(pendingOnly: true), Path.Combine(_engine.Settings.StorageDir, "tasks.md"), _engine.Settings.UserName);
            Ui.Open(path);
        });
        var make = Ui.Button("Create digest", "", () => MakeDigest(openWord: false), accent: true);
        var word = Ui.Button("Create and open in Word", "", () => MakeDigest(openWord: true));

        Content = Ui.Page("Tasks and digest",
            Ui.Text("Open action items", "SubtitleTextBlockStyle"),
            Ui.Card(_tasks),
            export,
            Ui.Text("Daily digest", "SubtitleTextBlockStyle"),
            Ui.Row(8, _day, make, word),
            _digest);
    }

    public void OnShown() => LoadTasks();

    private void LoadTasks()
    {
        _tasks.Children.Clear();
        var open = _engine.Database.GetActions(pendingOnly: true);
        if (open.Count == 0) _tasks.Children.Add(Ui.Text("No open action items.", secondary: true));
        foreach (var a in open)
        {
            var label = Ui.Stack(0, Ui.Text(a.Task), Ui.Text($"{a.Owner} · {a.MeetingTitle ?? "General"}", "CaptionTextBlockStyle", secondary: true));
            var box = new CheckBox { Content = label };
            var id = a.Id;
            box.Click += (_, _) => _engine.Database.SetActionCompleted(id, box.IsChecked == true);
            _tasks.Children.Add(box);
        }
    }

    private async void MakeDigest(bool openWord)
    {
        var day = DateOnly.FromDateTime((_day.Date ?? DateTimeOffset.Now).LocalDateTime);
        try
        {
            var db = _engine.Database;
            var user = _engine.Settings.UserName;
            var result = await Task.Run(() => new DigestGenerator(db, user).Generate(day));
            _digest.Show(result.Markdown);
            if (openWord) Ui.Open(result.DocxPath);
        }
        catch (IOException e)
        {
            _window.ShowNotice($"The digest could not be written ({e.Message}). Close it in Word if it is open.", isError: true);
        }
    }
}
