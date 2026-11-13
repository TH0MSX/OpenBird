using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBird.Windows;

namespace OpenBird.App.Pages;

internal sealed class HomePage : UserControl, IPage
{
    private readonly OpenBirdEngine _engine;
    private readonly MainWindow _window;
    private readonly TextBlock _greeting = Ui.Text("", secondary: true, wrap: false);
    private readonly Button _record;
    private readonly TextBlock _recordLabel = new();
    private readonly FontIcon _recordIcon = Ui.Icon("\uE720", 18);
    private readonly TextBlock _timer = Ui.Text("00:00", "TitleLargeTextBlockStyle", wrap: false);
    private readonly TextBlock _meeting = Ui.Text("Ready when you are", secondary: true);
    private readonly TextBlock _stateLabel = Ui.Text("READY", "CaptionTextBlockStyle", secondary: true, wrap: false);
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _stateDot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _mic = new() { Maximum = 100, MinWidth = 220 };
    private readonly ProgressBar _system = new() { Maximum = 100, MinWidth = 220 };
    private readonly ToggleSwitch _autoListen = new();
    private readonly ToggleSwitch _screen = new();
    private readonly TextBlock _screenValue = Ui.Text("", "BodyStrongTextBlockStyle", wrap: false);
    private readonly TextBlock _screenNote = Ui.Text("", "CaptionTextBlockStyle", secondary: true, wrap: false);
    private readonly TextBlock _audioValue = Ui.Text("", "BodyStrongTextBlockStyle", wrap: false);
    private readonly TextBlock _audioNote = Ui.Text("", "CaptionTextBlockStyle", secondary: true, wrap: false);
    private readonly TextBlock _aiValue = Ui.Text("", "BodyStrongTextBlockStyle", wrap: false);
    private readonly TextBlock _aiNote = Ui.Text("", "CaptionTextBlockStyle", secondary: true, wrap: false);
    private readonly TextBlock _keyValue = Ui.Text("", "BodyStrongTextBlockStyle", wrap: false);
    private readonly TextBlock _keyNote = Ui.Text("", "CaptionTextBlockStyle", secondary: true, wrap: false);
    private readonly Border _modelCard;
    private readonly ProgressBar _modelProgress = new() { Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly TextBlock _modelText = Ui.Text("The speech model is not installed yet. Meetings are recorded, but not transcribed, until it is.");
    private readonly Button _modelButton;
    private readonly StackPanel _cards = new() { Spacing = 8 };
    private bool _syncing;

    public HomePage(OpenBirdEngine engine, MainWindow window)
    {
        _engine = engine;
        _window = window;

        _record = new Button { Content = Ui.Row(10, _recordIcon, _recordLabel), Style = Ui.Style("AccentButtonStyle"), MinWidth = 200, Height = 44 };
        _record.Click += (_, _) => _engine.ToggleRecording();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_mic, "Microphone level");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_system, "System audio level");

        // Hero: what is happening right now, with the one primary action.
        var status = Ui.Row(8, _stateDot, _stateLabel);
        var left = Ui.Stack(6, status, _timer, _meeting);
        left.Children.Add(new Border { Height = 8 });
        left.Children.Add(_record);
        var levels = Ui.Stack(10,
            Ui.Text("Input levels", "CaptionTextBlockStyle", secondary: true),
            Ui.Stack(2, Ui.Text("Microphone", "CaptionTextBlockStyle"), _mic),
            Ui.Stack(2, Ui.Text("System audio", "CaptionTextBlockStyle"), _system));
        levels.VerticalAlignment = VerticalAlignment.Center;
        var heroGrid = new Grid { ColumnSpacing = 48 };
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(levels, 1);
        heroGrid.Children.Add(left);
        heroGrid.Children.Add(levels);
        var hero = Ui.Card(heroGrid, 24);

        // At-a-glance tiles replace the old line of small print.
        var tiles = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 4; i++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Tile(int col, string glyph, string label, TextBlock value, TextBlock note)
        {
            var icon = Ui.Icon(glyph, 16);
            icon.Foreground = Ui.Brush("AccentTextFillColorPrimaryBrush");
            var card = Ui.Card(Ui.Stack(6, Ui.Row(8, icon, Ui.Text(label, "CaptionTextBlockStyle", secondary: true, wrap: false)), value, note), 16);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(card, col);
            tiles.Children.Add(card);
        }
        Tile(0, "\uE7F4", "Screen memory", _screenValue, _screenNote);
        Tile(1, "\uE720", "Audio", _audioValue, _audioNote);
        Tile(2, "\uE945", "AI", _aiValue, _aiNote);
        Tile(3, "\uE765", "Hotkey", _keyValue, _keyNote);

        _autoListen.Toggled += (_, _) => { if (!_syncing) _engine.SetAutoListen(_autoListen.IsOn); };
        _screen.Toggled += (_, _) => { if (!_syncing) _engine.SetScreenMemory(_screen.IsOn); };
        var capture = Ui.Button("Capture now", "\uE722", async () =>
        {
            var result = await _engine.CaptureNowAsync();
            if (result == ScreenTickText.Excluded) _window.ShowNotice("The window in front is on your privacy list, so nothing was captured.", isError: false);
        });

        _modelButton = Ui.Button("Download speech model (about 200 MB)", "\uE896", DownloadModel, accent: true);
        _modelCard = Ui.Card(Ui.Stack(10, Ui.Text("Speech model needed", "BodyStrongTextBlockStyle"), _modelText, _modelProgress, _modelButton));

        // Brand header: the app icon with a large name, and a greeting underneath.
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "openbird-toast.png");
        var header = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (File.Exists(logoPath))
        {
            var logo = new Microsoft.UI.Xaml.Controls.Image { Width = 56, Height = 56, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logoPath)) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(logo, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            header.Children.Add(logo);
        }
        var titles = Ui.Stack(0, Ui.Text("OpenBird", "TitleLargeTextBlockStyle", wrap: false), _greeting);
        titles.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        Content = Ui.PageWith(header,
            _modelCard,
            hero,
            tiles,
            _cards,
            Ui.Text("Quick settings", "SubtitleTextBlockStyle"),
            Ui.SettingRow("Start recording when a conversation begins", "Listens for sustained speech on the microphone or in system audio.", _autoListen),
            Ui.SettingRow("Screen memory", "Reads the screen every few seconds so you can search it later. Private windows are skipped.", Ui.Row(12, capture, _screen)));
    }

    public void OnShown()
    {
        var hour = DateTime.Now.Hour;
        var part = hour < 5 ? "Good evening" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        _greeting.Text = $"{part}, {_engine.Settings.UserName} · {DateTime.Now:dddd, d MMMM}";
        Update(_engine.Status());
        LoadCards();
    }

    /// <summary>Follow-up suggestions written by the selected model after meetings.</summary>
    public void LoadCards()
    {
        _cards.Children.Clear();
        var cards = _engine.Database.GetCards(limit: 6);
        if (cards.Count == 0) return;
        _cards.Children.Add(Ui.Text("Suggestions", "SubtitleTextBlockStyle"));
        foreach (var card in cards)
        {
            var id = card.Id;
            var text = Ui.Stack(2, Ui.Text(card.Title, "BodyStrongTextBlockStyle"));
            if (card.Detail.Length > 0) text.Children.Add(Ui.Text(card.Detail));
            text.Children.Add(Ui.Text($"From {card.MeetingTitle ?? "a meeting"}", "CaptionTextBlockStyle", secondary: true));
            var buttons = Ui.Row(8,
                Ui.Button("Add to tasks", "\uE710", () => { _engine.Database.AcceptCard(id, _engine.Settings.UserName); LoadCards(); }),
                Ui.Button("Dismiss", "\uE711", () => { _engine.Database.DismissCard(id); LoadCards(); }));
            _cards.Children.Add(Ui.Card(Ui.Stack(10, text, buttons), 14));
        }
    }

    public void Update(EngineStatus s)
    {
        _syncing = true;
        _recordLabel.Text = s.IsRecording ? "Stop recording" : "Start recording";
        _recordIcon.Glyph = s.IsRecording ? "\uE15B" : "\uE720";
        _timer.Text = Ui.Duration(s.ElapsedSeconds);
        _meeting.Text = s.IsRecording ? s.MeetingTitle ?? "Recording"
            : s.Processing > 0 ? "Writing notes for the last meeting…" : "Ready when you are";
        _stateLabel.Text = s.IsRecording ? "RECORDING" : s.Processing > 0 ? "WRITING NOTES" : "READY";
        _stateDot.Fill = Ui.Brush(s.IsRecording ? "SystemFillColorCriticalBrush" : s.Processing > 0 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        // Speech sits around 0.02-0.2 RMS; a square-root scale makes quiet speech visible.
        _mic.Value = Math.Min(100, Math.Sqrt(s.MicLevel) * 220);
        _system.Value = Math.Min(100, Math.Sqrt(s.SystemLevel) * 220);
        _autoListen.IsOn = s.AutoListen;
        _screen.IsOn = !s.ScreenPaused;

        _screenValue.Text = s.ScreenPaused ? "Paused" : $"{s.ScreenStored} captured";
        _screenNote.Text = $"{s.ScreenExcluded} skipped for privacy";
        _audioValue.Text = s.MicrophoneActive && s.SystemAudioActive ? "All inputs ready" : s.MicrophoneActive || s.SystemAudioActive ? "Partly available" : "No input";
        _audioNote.Text = $"Mic {(s.MicrophoneActive ? "on" : "off")} · System {(s.SystemAudioActive ? "on" : "off")}";
        _aiValue.Text = _engine.Settings.EffectiveAiMode switch { "cloud" => "Cloud", "local" => "On this PC", _ => "Off" };
        _aiNote.Text = _engine.Settings.EffectiveAiMode switch { "local" => "Nothing leaves this PC", "cloud" => "Sent to your provider", _ => "Built-in rules only" };
        _keyValue.Text = s.HotkeyInUse ?? "None";
        _keyNote.Text = "Start or stop anywhere";
        if (_modelProgress.Visibility == Visibility.Collapsed) _modelCard.Visibility = s.ModelInstalled ? Visibility.Collapsed : Visibility.Visible;
        _syncing = false;
    }

    private async void DownloadModel()
    {
        _modelButton.IsEnabled = false;
        _modelProgress.Visibility = Visibility.Visible;
        _modelProgress.IsIndeterminate = true;
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var progress = new Progress<(long Done, long Total)>(p => ui.TryEnqueue(() =>
        {
            if (p.Total <= 0) return;
            _modelProgress.IsIndeterminate = false;
            _modelProgress.Value = p.Done * 100.0 / p.Total;
            _modelText.Text = $"Downloading the speech model: {p.Done / 1048576} of {p.Total / 1048576} MB";
        }));
        try
        {
            await Task.Run(() => _engine.InstallModelAsync(progress, CancellationToken.None));
            _modelProgress.Visibility = Visibility.Collapsed;
            _modelCard.Visibility = Visibility.Collapsed;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            _engine.Log("Model download failed: " + e);
            _modelText.Text = $"The download failed ({e.Message}). Check your connection and try again.";
            _modelProgress.Visibility = Visibility.Collapsed;
            _modelButton.IsEnabled = true;
        }
    }
}

/// <summary>Readable aliases for the screen-capture result used by the pages.</summary>
internal static class ScreenTickText
{
    public const OpenBird.Core.ScreenTick Excluded = OpenBird.Core.ScreenTick.Excluded;
}
