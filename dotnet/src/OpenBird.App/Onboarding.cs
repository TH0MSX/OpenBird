using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBird.Windows;

namespace OpenBird.App;

/// <summary>First-run setup: five short steps in one dialog. Can be reopened from Settings.</summary>
internal static class Onboarding
{
    public static async Task RunAsync(OpenBirdEngine engine, XamlRoot root)
    {
        var mic = new ProgressBar { Maximum = 100, Width = 320 };
        var system = new ProgressBar { Maximum = 100, Width = 320 };
        var devices = Ui.Text("", "CaptionTextBlockStyle", secondary: true);
        var meter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        meter.Tick += (_, _) =>
        {
            var s = engine.Status();
            mic.Value = Math.Min(100, Math.Sqrt(s.MicLevel) * 220);
            system.Value = Math.Min(100, Math.Sqrt(s.SystemLevel) * 220);
            devices.Text = $"Microphone {(s.MicrophoneActive ? "connected" : "not found")} · System audio {(s.SystemAudioActive ? "connected" : "not found")}";
        };

        var modelText = Ui.Text("");
        var modelBar = new ProgressBar { Maximum = 100, Visibility = Visibility.Collapsed };
        Button modelButton = null!;
        modelButton = Ui.Button("Download now (about 200 MB)", "", async () =>
        {
            modelButton.IsEnabled = false;
            modelBar.Visibility = Visibility.Visible;
            modelBar.IsIndeterminate = true;
            var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var progress = new Progress<(long Done, long Total)>(p => ui.TryEnqueue(() =>
            {
                if (p.Total <= 0) return;
                modelBar.IsIndeterminate = false;
                modelBar.Value = p.Done * 100.0 / p.Total;
                modelText.Text = $"Downloading: {p.Done / 1048576} of {p.Total / 1048576} MB";
            }));
            try
            {
                await Task.Run(() => engine.InstallModelAsync(progress, CancellationToken.None));
                modelText.Text = "The speech model is installed.";
                modelBar.Visibility = Visibility.Collapsed;
                modelButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                engine.Log("Model download failed during setup: " + e);
                modelText.Text = $"The download failed ({e.Message}). You can retry here or later from Home.";
                modelBar.Visibility = Visibility.Collapsed;
                modelButton.IsEnabled = true;
            }
        }, accent: true);

        var screenOn = new ToggleSwitch { IsOn = engine.Settings.ScreenMemoryEnabled, Header = "Screen memory" };
        var allMonitors = new ToggleSwitch { IsOn = engine.Settings.CaptureAllMonitors, Header = "Capture all monitors" };
        var autoListen = new ToggleSwitch { IsOn = engine.Settings.AutoTranscribe, Header = "Start recording when a conversation begins" };
        var startup = new ToggleSwitch { IsOn = StartupRegistration.IsEnabled, Header = "Start OpenBird when I sign in to Windows" };

        var steps = new (string Title, Func<UIElement> Build)[]
        {
            ("Welcome to OpenBird", () => Ui.Stack(12,
                Ui.Text("OpenBird records your meetings, writes the notes, and keeps a searchable memory of what was on your screen."),
                Ui.Text("Everything stays on this PC. Nothing is sent anywhere unless you set AI to Cloud in Settings."),
                Ui.Text($"Notes are saved in {engine.Settings.StorageDir}", "CaptionTextBlockStyle", secondary: true))),

            ("Check your audio", () =>
            {
                var s = engine.Status();
                return Ui.Stack(12,
                    Ui.Text("Say something, and play any sound. Both bars should move."),
                    Ui.Text("Microphone", "CaptionTextBlockStyle", secondary: true), mic,
                    Ui.Text("System audio (the other side of a call)", "CaptionTextBlockStyle", secondary: true), system,
                    devices,
                    Ui.Text("If a bar stays still, check the default input and output devices in Windows Sound settings.", "CaptionTextBlockStyle", secondary: true));
            }),

            ("Speech model", () =>
            {
                var installed = engine.Status().ModelInstalled;
                modelText.Text = installed
                    ? "The speech model is installed. Transcription runs on this PC, offline."
                    : "Transcription runs on this PC with an offline speech model. It is not installed yet. Without it, meetings are recorded but not transcribed.";
                modelButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
                return Ui.Stack(12, modelText, modelBar, modelButton);
            }),

            ("Screen memory and privacy", () => Ui.Stack(12,
                Ui.Text("Every few seconds OpenBird can read the text on your screen so you can search it later."),
                screenOn, allMonitors,
                Ui.Text($"{engine.Settings.CaptureExclusions.Count} privacy rules are active: password managers, private browsing, sign-in pages and banking windows are never captured. Chrome Incognito cannot be detected, so add your own rules in Settings for anything sensitive there.", "CaptionTextBlockStyle", secondary: true),
                Ui.Text("Screenshots and the text read from them are stored unencrypted on this PC.", "CaptionTextBlockStyle", secondary: true))),

            ("Recording", () => Ui.Stack(12,
                autoListen,
                Ui.Text(engine.ActiveHotkey is { } key
                    ? $"Press {key} anywhere to start or stop a recording."
                    : "No record hotkey is active. Set one in Settings."),
                engine.HotkeyProblem is { } problem ? Ui.Text(problem, "CaptionTextBlockStyle", secondary: true) : new Border(),
                startup,
                Ui.Text("Closing the window keeps OpenBird running in the notification area. Right-click its icon to exit.", "CaptionTextBlockStyle", secondary: true))),
        };

        var index = 0;
        var host = new ContentControl { MinWidth = 440, MinHeight = 250, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { XamlRoot = root, Content = host, DefaultButton = ContentDialogButton.Primary, CloseButtonText = "Skip setup" };
        void ShowStep()
        {
            dialog.Title = $"{steps[index].Title}  ({index + 1} of {steps.Length})";
            host.Content = steps[index].Build();
            dialog.PrimaryButtonText = index == steps.Length - 1 ? "Finish" : "Next";
            dialog.SecondaryButtonText = index == 0 ? "" : "Back";
        }
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (index == steps.Length - 1) return;      // Finish closes the dialog
            e.Cancel = true;
            index++;
            ShowStep();
        };
        dialog.SecondaryButtonClick += (_, e) =>
        {
            e.Cancel = true;
            index = Math.Max(0, index - 1);
            ShowStep();
        };

        ShowStep();
        meter.Start();
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { meter.Stop(); }

        // Whatever was chosen so far is kept, and setup is not shown again.
        var updated = System.Text.Json.JsonSerializer.Deserialize<OpenBird.Core.AppSettings>(System.Text.Json.JsonSerializer.Serialize(engine.Settings))!;
        updated.OnboardingDone = true;
        if (result == ContentDialogResult.Primary)
        {
            updated.ScreenMemoryEnabled = screenOn.IsOn;
            updated.CaptureAllMonitors = allMonitors.IsOn;
            updated.AutoTranscribe = autoListen.IsOn;
            try { StartupRegistration.Set(startup.IsOn); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                engine.Log("Could not change sign-in start: " + e.Message);
            }
        }
        engine.ApplySettings(updated);
    }
}
