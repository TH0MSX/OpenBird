using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBird.Core;
using OpenBird.Speech;
using OpenBird.Windows;

namespace OpenBird.App.Pages;

internal sealed class SettingsPage : UserControl, IPage
{
    private readonly OpenBirdEngine _engine;
    private readonly MainWindow _window;
    private readonly TextBox _name = new() { Width = 220 };
    private readonly TextBox _hotkey = new() { Width = 220, PlaceholderText = "ctrl+alt+r" };
    private readonly NumberBox _interval = new() { Minimum = 5, Maximum = 600, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160 };
    private readonly NumberBox _retention = new() { Minimum = 1, Maximum = 365, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160 };
    private readonly ToggleSwitch _allMonitors = new();
    private readonly ToggleSwitch _ocr = new();
    private readonly ToggleSwitch _autoListen = new();
    private readonly ToggleSwitch _startup = new();
    private readonly ToggleSwitch _systemAudioStarts = new();
    private readonly ToggleSwitch _digestOn = new();
    private readonly TimePicker _digestTime = new() { ClockIdentifier = "24HourClock", MinuteIncrement = 5 };
    private readonly ToggleSwitch _screenMemory = new();
    private readonly TextBox _exclusions = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 260 };
    private readonly ComboBox _aiMode = new() { Width = 320 };
    private readonly ToggleSwitch _notifications = new();
    private readonly ToggleSwitch _tasksTab = new();
    private readonly TextBox _localUrl = new() { Width = 320, PlaceholderText = "http://localhost:11434" };
    private readonly TextBox _localModel = new() { Width = 320, PlaceholderText = "empty: first model on the server" };
    private readonly TextBlock _localStatus = Ui.Text("", "CaptionTextBlockStyle", secondary: true);
    private readonly PasswordBox _gemini = new() { Width = 320, PlaceholderText = "Gemini API key" };
    private readonly PasswordBox _groq = new() { Width = 320, PlaceholderText = "Groq API key" };
    private readonly TextBlock _saved = Ui.Text("", "CaptionTextBlockStyle", secondary: true);
    private readonly TextBlock _about = Ui.Text("", "CaptionTextBlockStyle", secondary: true);

    public SettingsPage(OpenBirdEngine engine, MainWindow window)
    {
        _engine = engine;
        _window = window;
        foreach (var label in new[] { "Off (built-in rules only)", "On this PC (local model server)", "Cloud (Gemini or Groq)" }) _aiMode.Items.Add(label);
        ScrollViewer.SetVerticalScrollBarVisibility(_exclusions, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_exclusions, "Privacy exclusions, one per line");

        var save = Ui.Button("Save", "", Save, accent: true);
        var folders = Ui.Row(8,
            Ui.Button("Notes folder", "", () => Ui.Open(_engine.Settings.StorageDir)),
            Ui.Button("Recordings", "", () => Ui.Open(_engine.Settings.RecordingsDir)),
            Ui.Button("Screenshots", "", () => Ui.Open(_engine.Settings.ScreenshotsDir)),
            Ui.Button("Log file", "", () => Ui.Open(_engine.LogPath)),
            Ui.Button("Settings file", "", () => Ui.Open(_engine.Store.ConfigPath)));

        Content = Ui.Page("Settings",
            Ui.Text("General", "SubtitleTextBlockStyle"),
            Ui.SettingRow("Your name", "Used as the speaker label for your microphone.", _name),
            Ui.SettingRow("Record hotkey", "Starts and stops a recording from anywhere. Example: ctrl+alt+r, win+shift+f9.", _hotkey),
            Ui.SettingRow("Start recording when a conversation begins", null, _autoListen),
            Ui.SettingRow("Also start when only system audio is heard", "On: a call where you just listen is recorded, but so is any video or music. Off: only your own voice starts a recording.", _systemAudioStarts),
            Ui.SettingRow("Write a daily digest automatically", "At this time each day, a summary of meetings, action items and screen focus is written.", Ui.Row(12, _digestTime, _digestOn)),
            Ui.SettingRow("Show Tasks and digest in the sidebar", "Off hides the page (open action items, daily digest, Word export). Suggestions on Home and the scheduled digest keep working.", _tasksTab),
            Ui.SettingRow("Windows notifications", "Recording started, notes ready, new suggestions and the daily digest. Windows Focus and Do Not Disturb still apply.", _notifications),
            Ui.SettingRow("Start OpenBird when I sign in to Windows", "Starts in the notification area, without opening the window.", _startup),

            Ui.Text("Screen memory", "SubtitleTextBlockStyle"),
            Ui.SettingRow("Screen memory", "Off: the screen is never captured.", _screenMemory),
            Ui.SettingRow("Seconds between captures", null, _interval),
            Ui.SettingRow("Read text from captures", "Uses the OCR built into Windows. Off: only window titles and images are kept.", _ocr),
            Ui.SettingRow("Capture all monitors", "Off: only the main display.", _allMonitors),
            Ui.SettingRow("Keep screenshots for (days)", "Older images are deleted. The text read from them stays searchable.", _retention),

            Ui.Text("Privacy", "SubtitleTextBlockStyle"),
            Ui.Text("A window whose title or program name contains any of these is never captured, read or stored. One per line.", secondary: true),
            _exclusions,

            Ui.Text("AI", "SubtitleTextBlockStyle"),
            Ui.SettingRow("Model for summaries, answers and suggestions", "Off: built-in rules only, nothing is generated. On this PC: a model server running locally. Cloud: transcripts and screen text of a meeting are sent to the provider.", _aiMode),
            Ui.Text("On this PC", "BodyStrongTextBlockStyle"),
            Ui.Text("Needs a model server on this PC that speaks the OpenAI chat API, such as Ollama or LM Studio, with at least one model installed. Only addresses on this PC are accepted.", "CaptionTextBlockStyle", secondary: true),
            Ui.SettingRow("Server address", null, _localUrl),
            Ui.SettingRow("Model name", null, _localModel),
            Ui.Row(12, Ui.Button("Test connection", "\uE9D9", TestLocal), _localStatus),
            Ui.Text("Cloud", "BodyStrongTextBlockStyle"),
            Ui.SettingRow("Gemini", "Tried first when a key is set.", _gemini),
            Ui.SettingRow("Groq", "Tried when Gemini has no key or fails.", _groq),
            Ui.Text("Keys are stored unencrypted in the settings file on this PC.", "CaptionTextBlockStyle", secondary: true),

            Ui.Row(12, save, _saved),

            Ui.Button("Run first-time setup again", "\uE8FB", async () => await _window.RunOnboardingAsync()),

            Ui.Text("Files", "SubtitleTextBlockStyle"),
            folders,
            _about);
    }

    public void OnShown()
    {
        var s = _engine.Settings;
        _name.Text = s.UserName;
        _hotkey.Text = s.Hotkey;
        _autoListen.IsOn = s.AutoTranscribe;
        _startup.IsOn = StartupRegistration.IsEnabled;
        _systemAudioStarts.IsOn = s.AutoStartOnSystemAudio;
        _notifications.IsOn = s.NotificationsEnabled;
        _tasksTab.IsOn = s.ShowTasksTab;
        _digestOn.IsOn = DigestSchedule.TryParse(s.DigestTime, out var digestAt);
        _digestTime.Time = _digestOn.IsOn ? digestAt.ToTimeSpan() : new TimeSpan(17, 30, 0);
        _screenMemory.IsOn = s.ScreenMemoryEnabled;
        _interval.Value = s.ScreenOcrIntervalSeconds;
        _ocr.IsOn = s.ScreenOcrEnabled;
        _allMonitors.IsOn = s.CaptureAllMonitors;
        _retention.Value = s.ScreenshotRetentionDays;
        _exclusions.Text = string.Join("\r", s.CaptureExclusions);
        _aiMode.SelectedIndex = s.EffectiveAiMode switch { "local" => 1, "cloud" => 2, _ => 0 };
        _localUrl.Text = s.LocalAiUrl;
        _localModel.Text = s.LocalAiModel;
        _localStatus.Text = "";
        _gemini.Password = s.GeminiApiKey;
        _groq.Password = s.GroqApiKey;
        _saved.Text = "";

        var devices = WasapiCaptureService.DescribeDevices();
        _about.Text =
            $"OpenBird {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)} · " +
            $"Speech model {(WhisperModel.IsInstalled(s.WhisperModelDir) ? "installed" : "not installed")} · " +
            $"Microphone: {devices.Microphone ?? "none"} · Speakers: {devices.Speakers ?? "none"}\n" +
            $"Database: {s.DbPath}";
    }

    private async void TestLocal()
    {
        _localStatus.Text = "Testing: sending a short message to the model (the first one can take a while)…";
        try
        {
            var url = string.IsNullOrWhiteSpace(_localUrl.Text) ? "http://localhost:11434" : _localUrl.Text.Trim();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await _engine.TestLocalModelAsync(url, _localModel.Text.Trim(), timeout.Token);
            _localStatus.Text = result.Message;
        }
        catch (ArgumentException e) { _localStatus.Text = e.Message; }
        catch (OperationCanceledException) { _localStatus.Text = "The model did not answer within 90 seconds. Check that it is loaded, then test again."; }
    }

    private void Save()
    {
        try { Hotkey.Parse(_hotkey.Text); }
        catch (FormatException e)
        {
            _window.ShowNotice(e.Message, isError: true);
            return;
        }

        // Copy the current settings (including keys this version does not know), then apply the edits.
        var updated = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_engine.Settings))!;
        updated.UserName = string.IsNullOrWhiteSpace(_name.Text) ? _engine.Settings.UserName : _name.Text.Trim();
        updated.Hotkey = _hotkey.Text.Trim().ToLowerInvariant();
        updated.AutoTranscribe = _autoListen.IsOn;
        updated.ScreenMemoryEnabled = _screenMemory.IsOn;
        updated.AutoStartOnSystemAudio = _systemAudioStarts.IsOn;
        updated.NotificationsEnabled = _notifications.IsOn;
        updated.ShowTasksTab = _tasksTab.IsOn;
        updated.DigestTime = _digestOn.IsOn ? $"{_digestTime.Time.Hours:00}:{_digestTime.Time.Minutes:00}" : "";
        updated.ScreenOcrIntervalSeconds = double.IsNaN(_interval.Value) ? updated.ScreenOcrIntervalSeconds : (int)_interval.Value;
        updated.ScreenOcrEnabled = _ocr.IsOn;
        updated.CaptureAllMonitors = _allMonitors.IsOn;
        updated.ScreenshotRetentionDays = double.IsNaN(_retention.Value) ? updated.ScreenshotRetentionDays : (int)_retention.Value;
        updated.CaptureExclusions = _exclusions.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        updated.GeminiApiKey = _gemini.Password.Trim();
        updated.GroqApiKey = _groq.Password.Trim();
        updated.SetAiMode(_aiMode.SelectedIndex switch { 1 => "local", 2 => "cloud", _ => "off" });
        updated.LocalAiUrl = string.IsNullOrWhiteSpace(_localUrl.Text) ? "http://localhost:11434" : _localUrl.Text.Trim();
        updated.LocalAiModel = _localModel.Text.Trim();
        if (updated.EffectiveAiMode == "local")
        {
            try { LocalChatModel.ParseLoopback(updated.LocalAiUrl); }
            catch (ArgumentException e)
            {
                _window.ShowNotice(e.Message, isError: true);
                return;
            }
        }
        if (updated.EffectiveAiMode == "cloud" && updated.GeminiApiKey.Length == 0 && updated.GroqApiKey.Length == 0)
            _window.ShowNotice("AI is set to cloud but no key is entered, so the built-in rules are used until you add one.", isError: false);

        try
        {
            _engine.ApplySettings(updated);
            _window.RefreshNavigation();
            if (StartupRegistration.IsEnabled != _startup.IsOn) StartupRegistration.Set(_startup.IsOn);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _window.ShowNotice($"Sign-in start could not be changed: {e.Message}", isError: true);
            return;
        }
        catch (IOException e)
        {
            _window.ShowNotice($"Settings could not be saved: {e.Message}", isError: true);
            return;
        }
        _saved.Text = $"Saved at {DateTime.Now:HH:mm:ss}.";
        if (_engine.HotkeyProblem is not null) _window.ShowNotice(_engine.HotkeyProblem, isError: false);
        if (updated.CaptureExclusions.Count == 0)
            _window.ShowNotice("The privacy list is empty: password managers and private windows will be captured.", isError: false);
    }
}
