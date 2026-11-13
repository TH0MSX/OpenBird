using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenBird.App.Pages;
using OpenBird.Windows;

namespace OpenBird.App;

internal interface IPage
{
    /// <summary>Called every time the page becomes visible.</summary>
    void OnShown();
}

internal sealed class MainWindow : Window
{
    private readonly OpenBirdEngine _engine;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly TitleBar _titleBar = new();
    private readonly NavigationView _nav = new();
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(12, 0, 12, 4) };
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly string _iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "openbird.ico");
    private readonly string _recordingIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "openbird-recording.ico");
    private TrayIcon? _tray;
    private Notifier? _toasts;
    private bool _trayShowsRecording;
    private bool _exiting;
    private bool _toldAboutTray;

    public MainWindow(OpenBirdEngine engine)
    {
        _engine = engine;
        _toasts = new Notifier(Path.Combine(AppContext.BaseDirectory, "Assets", "openbird-toast.png"),
            args => _ui.TryEnqueue(() => HandleToast(args)), (title, body) => _tray?.Notify(title, body), _engine.Log);
        _toasts.Register();
        Title = "OpenBird";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1180, 800));
        if (File.Exists(_iconPath)) AppWindow.SetIcon(_iconPath);

        // Title bar: the system TitleBar control gives the correct drag area, caption-button insets and focus behaviour.
        _titleBar.Title = "OpenBird";
        _titleBar.IsBackButtonVisible = false;
        _titleBar.IsPaneToggleButtonVisible = true;
        if (File.Exists(_iconPath)) _titleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(_iconPath)) };
        _titleBar.PaneToggleRequested += (_, _) => _nav.IsPaneOpen = !_nav.IsPaneOpen;
        SetTitleBar(_titleBar);

        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = true;
        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _nav.OpenPaneLength = 240;
        _nav.IsPaneToggleButtonVisible = false;           // the toggle is in the title bar
        _nav.IsTitleBarAutoPaddingEnabled = false;
        foreach (var (tag, label, glyph) in new[]
        {
            ("home", "Home", ""), ("meetings", "Meetings", "\uE787"), ("ask", "Ask", ""),
            ("chat", "Chat", "\uE8BD"),
            ("timeline", "Timeline", ""), ("tasks", "Tasks and digest", ""),
        })
            _nav.MenuItems.Add(new NavigationViewItem { Content = label, Tag = tag, Icon = Ui.Icon(glyph) });
        _nav.SelectionChanged += (_, e) => Show(e.IsSettingsSelected ? "settings" : (string)((NavigationViewItem)e.SelectedItem).Tag);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_notice, 1);
        Grid.SetRow(_nav, 2);
        root.Children.Add(_titleBar);
        root.Children.Add(_notice);
        root.Children.Add(_nav);
        Content = root;

        AppWindow.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;                 // closing the window keeps OpenBird running in the notification area
            AppWindow.Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                Notify("OpenBird is still running", "It keeps recording from the notification area. Right-click its icon to open the menu or exit.", "tray");
            }
        };

        _engine.MeetingProcessed += m => _ui.TryEnqueue(() =>
        {
            Notify(m.Warning is null ? "Notes ready" : "Saved with a warning", m.Warning ?? m.Title, "notes-" + m.MeetingId,
                new ToastAction("Open notes", "open-meeting", m.MeetingId));
            if (m.Warning is not null) ShowNotice(m.Warning, isError: false);
            if (_pages.TryGetValue("meetings", out var page)) ((MeetingsPage)page).Reload(select: m.MeetingId);
        });
        _engine.MeetingStarted += (_, title) => _ui.TryEnqueue(() =>
            Notify("Recording started", title, "recording", new ToastAction("Stop recording", "stop")));
        _engine.Notice += text => _ui.TryEnqueue(() => ShowNotice(text, isError: false));
        _engine.CardsChanged += () => _ui.TryEnqueue(() =>
        {
            Notify("New suggestions", "OpenBird has follow-up suggestions from your last meeting.", "cards", new ToastAction("Review", "home"));
            if (_pages.TryGetValue("home", out var home)) ((HomePage)home).LoadCards();
        });
        _engine.DigestReady += d => _ui.TryEnqueue(() =>
            Notify("Daily digest ready", $"{d.Meetings} meetings, {d.Pending} open action items.", "digest", new ToastAction("Open digest", "tasks")));

        root.ActualThemeChanged += (_, _) =>
        {
            var current = _pages.FirstOrDefault(p => ReferenceEquals(p.Value, _nav.Content)).Key ?? "home";
            _pages.Clear();
            Show(current);
        };

        _timer.Tick += (_, _) => Tick();
        RefreshNavigation();
        _nav.SelectedItem = _nav.MenuItems[0];
    }

    /// <summary>Tray icon and first notices need the engine running (hotkey result, model state).</summary>
    public void AfterEngineStart()
    {
        try
        {
            _tray = new TrayIcon("OpenBird", _iconPath, BringToFront, BuildTrayMenu);
        }
        catch (InvalidOperationException e)
        {
            _engine.Log("Tray icon unavailable: " + e.Message);
            ShowNotice("The notification-area icon could not be created, so closing this window will exit OpenBird.", isError: false);
            _exiting = true;    // without a tray icon, closing must really close
        }
        if (_engine.HotkeyProblem is not null) ShowNotice(_engine.HotkeyProblem, isError: false);
        _timer.Start();
    }

    public async Task RunOnboardingAsync()
    {
        BringToFront();
        // The dialog needs the window's content to be loaded first.
        if (Content is FrameworkElement { IsLoaded: false } root)
        {
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
        }
        await Onboarding.RunAsync(_engine, Content.XamlRoot);
        if (_nav.Content is IPage page) page.OnShown();
    }

    private void Notify(string title, string body, string tag, params ToastAction[] actions)
    {
        if (!_engine.Settings.NotificationsEnabled) return;
        if (_toasts is not null) _toasts.Show(title, body, tag, actions);
        else _tray?.Notify(title, body);
    }

    /// <summary>What a pressed notification button (or the notification itself) does.</summary>
    private void HandleToast(IDictionary<string, string> args)
    {
        args.TryGetValue("action", out var action);
        switch (action)
        {
            case "open-meeting":
                Open("meetings");
                if (args.TryGetValue("meeting", out var id) && _pages.TryGetValue("meetings", out var page)) ((MeetingsPage)page).Reload(select: id);
                break;
            case "stop":
                if (_engine.Status().IsRecording) _engine.ToggleRecording();
                break;
            case "home": Open("home"); break;
            case "tasks": Open("tasks"); break;
            default: BringToFront(); break;
        }
    }

    /// <summary>Applies the sidebar choices in Settings (currently: whether "Tasks and digest" is shown).</summary>
    public void RefreshNavigation()
    {
        var showTasks = _engine.Settings.ShowTasksTab;
        foreach (var item in _nav.MenuItems.OfType<NavigationViewItem>().Where(i => (string)i.Tag == "tasks"))
            item.Visibility = showTasks ? Visibility.Visible : Visibility.Collapsed;
        _pages.Remove("tasks");
    }

    /// <summary>Brings the window forward on the given page ("home", "meetings", "ask", "chat", "timeline", "tasks", "settings").</summary>
    public void Open(string tag)
    {
        BringToFront();
        if (tag == "settings") { _nav.SelectedItem = _nav.SettingsItem; return; }
        var item = _nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag && i.Visibility == Visibility.Visible)
                   ?? _nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == "home");
        if (item is null) return;
        tag = (string)item.Tag;
        if (ReferenceEquals(_nav.SelectedItem, item)) Show(tag); else _nav.SelectedItem = item;
    }

    private static string FormatHotkey(string? spec) => string.IsNullOrWhiteSpace(spec) ? ""
        : string.Join("+", spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));

    /// <summary>Built each time the tray icon is right-clicked, so it always shows the current state.</summary>
    private IReadOnlyList<TrayIcon.MenuItem> BuildTrayMenu()
    {
        var s = _engine.Status();
        var settings = _engine.Settings;
        var state = s.IsRecording ? $"Recording {Ui.Duration(s.ElapsedSeconds)}" + (string.IsNullOrEmpty(s.MeetingTitle) ? "" : $" · {s.MeetingTitle}")
            : s.Processing > 0 ? "Writing notes…" : "Ready";
        var separator = new TrayIcon.MenuItem("", () => { }, Separator: true);
        return new[]
        {
            new TrayIcon.MenuItem("OpenBird · " + state, () => { }, Enabled: false),
            separator,
            new TrayIcon.MenuItem("Open OpenBird", () => Open("home"), Default: true),
            new TrayIcon.MenuItem("Ask your memory…", () => Open("ask")),
            new TrayIcon.MenuItem("Chat…", () => Open("chat")),
            separator,
            new TrayIcon.MenuItem(s.IsRecording ? "Stop recording" : "Start recording", () => _engine.ToggleRecording(), Shortcut: FormatHotkey(s.HotkeyInUse)),
            new TrayIcon.MenuItem("Capture screen now", () => _ = _engine.CaptureNowAsync()),
            separator,
            new TrayIcon.MenuItem("Listen for conversations", () => _engine.SetAutoListen(!s.AutoListen), Checked: s.AutoListen),
            new TrayIcon.MenuItem("Screen memory", () => _engine.SetScreenMemory(s.ScreenPaused), Checked: !s.ScreenPaused),
            new TrayIcon.MenuItem("Windows notifications", ToggleNotifications, Checked: settings.NotificationsEnabled),
            separator,
            new TrayIcon.MenuItem("Open folder", () => { }, Children: new[]
            {
                new TrayIcon.MenuItem("Notes", () => Ui.Open(settings.StorageDir)),
                new TrayIcon.MenuItem("Recordings", () => Ui.Open(settings.RecordingsDir)),
                new TrayIcon.MenuItem("Screenshots", () => Ui.Open(settings.ScreenshotsDir)),
                new TrayIcon.MenuItem("Log file", () => Ui.Open(_engine.LogPath)),
            }),
            new TrayIcon.MenuItem("Settings…", () => Open("settings")),
            separator,
            new TrayIcon.MenuItem("Exit OpenBird", Exit),
        };
    }

    private void ToggleNotifications()
    {
        var updated = System.Text.Json.JsonSerializer.Deserialize<OpenBird.Core.AppSettings>(System.Text.Json.JsonSerializer.Serialize(_engine.Settings))!;
        updated.NotificationsEnabled = !_engine.Settings.NotificationsEnabled;
        _engine.ApplySettings(updated);
    }

    public void BringToFront()
    {
        AppWindow.Show();
        Activate();
    }

    public void ShowNotice(string message, bool isError)
    {
        _notice.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private void Show(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "home" => new HomePage(_engine, this),
                "meetings" => new MeetingsPage(_engine, this),
                "ask" => new AskPage(_engine),
                "chat" => new ChatPage(_engine),
                "timeline" => new TimelinePage(_engine),
                "tasks" => new TasksPage(_engine, this),
                _ => new SettingsPage(_engine, this),
            };
            _pages[tag] = page;
        }
        _nav.Content = page;
        (page as IPage)?.OnShown();
    }

    private void Tick()
    {
        var status = _engine.Status();
        if (_pages.TryGetValue("home", out var home) && ReferenceEquals(_nav.Content, home)) ((HomePage)home).Update(status);
        _tray?.SetTooltip(status.IsRecording ? $"OpenBird - recording {Ui.Duration(status.ElapsedSeconds)}"
            : status.Processing > 0 ? "OpenBird - writing notes" : "OpenBird");
        if (status.IsRecording != _trayShowsRecording)
        {
            _trayShowsRecording = status.IsRecording;      // a red dot on the icon shows recording at a glance
            _tray?.SetIcon(status.IsRecording ? _recordingIconPath : _iconPath);
        }
    }

    private void Exit()
    {
        _exiting = true;
        _timer.Stop();
        _tray?.Dispose();
        _toasts?.Dispose();
        AppWindow.Hide();
        _engine.Dispose();
        Application.Current.Exit();
    }
}
