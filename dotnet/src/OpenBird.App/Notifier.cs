using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace OpenBird.App;

/// <summary>A button on a notification. <see cref="Action"/> is what the app does when it is pressed.</summary>
internal sealed record ToastAction(string Label, string Action, string? Meeting = null);

/// <summary>
/// Windows app notifications (the toasts in the notification centre) through the Windows App SDK.
/// If Windows refuses to register or show them, the caller's fallback (a tray balloon) is used,
/// so a notification is never silently lost.
/// </summary>
internal sealed class Notifier : IDisposable
{
    private readonly Uri? _logo;
    private readonly Action<IDictionary<string, string>> _activated;
    private readonly Action<string, string> _fallback;
    private readonly Action<string> _log;
    private bool _registered;

    public Notifier(string? logoPath, Action<IDictionary<string, string>> onActivated, Action<string, string> fallback, Action<string> log)
    {
        _logo = logoPath is not null && File.Exists(logoPath) ? new Uri(logoPath) : null;
        _activated = onActivated;
        _fallback = fallback;
        _log = log;
    }

    /// <summary>Must run once at start-up, before the first notification, so button presses reach the running app.</summary>
    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, e) => _activated(e.Arguments);
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException)
        {
            _log("Windows notifications could not be registered, using tray balloons instead: " + e.Message);
        }
    }

    public void Show(string title, string body, string tag, IReadOnlyList<ToastAction> actions)
    {
        if (!_registered) { _fallback(title, body); return; }
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddText(title)
                .AddText(body.Length > 240 ? body[..240] + "…" : body)
                .SetTimeStamp(DateTime.Now);
            if (_logo is not null) builder.SetAppLogoOverride(_logo, AppNotificationImageCrop.Default);
            if (tag == "recording") builder.MuteAudio();             // starting a recording should not beep into the call
            foreach (var a in actions)
            {
                var button = new AppNotificationButton(a.Label).AddArgument("action", a.Action);
                if (a.Meeting is not null) button.AddArgument("meeting", a.Meeting);
                builder.AddButton(button);
            }
            var notification = builder.BuildNotification();
            if (tag.Length > 0)
            {
                notification.Tag = tag;                               // a newer notification with the same tag replaces the old one
                notification.Group = "openbird";
            }
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            _log("Notification failed, using a tray balloon: " + e.Message);
            _fallback(title, body);
        }
    }

    public void Dispose()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
        _registered = false;
    }
}
