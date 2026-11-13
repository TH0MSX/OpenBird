using Microsoft.Win32;

namespace OpenBird.Windows;

/// <summary>
/// "Start OpenBird when I sign in": one value under the current user's Run key.
/// No administrator rights, nothing machine-wide, removed again when switched off.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenBird";
    public const string BackgroundArgument = "--background";

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    /// <summary>True when sign-in start is on and points at this copy of the program.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>True when this process was started by the sign-in entry and should stay in the notification area.</summary>
    public static bool StartedInBackground =>
        Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, BackgroundArgument, StringComparison.OrdinalIgnoreCase));
}
