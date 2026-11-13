using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using OpenBird.Windows;

namespace OpenBird.App;

public partial class App : Application
{
    private MainWindow? _window;
    private OpenBirdEngine? _engine;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Keep running and leave a trace: a background recorder must not vanish silently.
            _engine?.Log("Unhandled UI error: " + e.Exception);
            e.Handled = true;
            _window?.ShowNotice("Something went wrong: " + e.Message, isError: true);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One OpenBird at a time: a second launch just brings the first window forward.
        var main = AppInstance.FindOrRegisterForKey("OpenBird.SingleInstance");
        if (!main.IsCurrent)
        {
            await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Environment.Exit(0);
            return;
        }
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        main.Activated += (_, _) => dispatcher.TryEnqueue(() => _window?.BringToFront());

        try
        {
            _engine = new OpenBirdEngine();
            _window = new MainWindow(_engine);
            // Started by Windows at sign-in: stay in the notification area until asked for.
            var background = StartupRegistration.StartedInBackground && _engine.Settings.OnboardingDone;
            if (!background) _window.Activate();
            // Audio devices, the screen loop and the hotkey start off the UI thread so the window appears at once.
            await Task.Run(_engine.Start);
            _window.AfterEngineStart();
            if (!_engine.Settings.OnboardingDone) await _window.RunOnboardingAsync();
        }
        catch (Exception e)
        {
            ShowStartupFailure(e);
        }
    }

    /// <summary>A failed start must be visible: write it to a file and show it in a plain window.</summary>
    private void ShowStartupFailure(Exception e)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenBird", "logs");
        var logPath = Path.Combine(logDir, "openbird-app-startup.log");
        try
        {
            Directory.CreateDirectory(logDir);
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}");
        }
        catch (IOException) { }

        var text = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = $"OpenBird could not start.\n\n{e.GetType().Name}: {e.Message}\n\nDetails were written to:\n{logPath}",
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(24),
        };
        var window = new Window { Title = "OpenBird could not start", Content = text };
        window.Closed += (_, _) => Exit();
        window.Activate();
    }
}
