namespace OpenBird.Core;

/// <summary>Decides which foreground windows must never be captured, read or stored.</summary>
public static class Privacy
{
    public static readonly IReadOnlyList<string> DefaultExclusions = new[]
    {
        // Password managers (matched against window title and process name)
        "1password", "bitwarden", "keepass", "lastpass", "dashlane", "nordpass",
        "proton pass", "enpass", "roboform", "credential manager",
        // Private browsing (Edge and Firefox put this in the window title)
        "inprivate", "private browsing", "incognito",
        // Sign-in and security surfaces
        "password", "windows security", "sign in to your account",
        // Banking and payments
        "bank", "paypal", "revolut", "wise.com",
    };

    public static bool IsExcluded(string? windowTitle, string? processName, IEnumerable<string>? patterns = null)
    {
        var haystack = ((windowTitle ?? "") + "\n" + (processName ?? "")).ToLowerInvariant();
        foreach (var raw in patterns ?? DefaultExclusions)
        {
            var p = raw?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(p) && haystack.Contains(p, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>Parses hotkey text such as "win+alt+r" into Win32 RegisterHotKey values.</summary>
public static class Hotkey
{
    public const uint ModAlt = 0x0001, ModCtrl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    public static (uint Modifiers, uint VirtualKey) Parse(string? spec)
    {
        uint mods = 0;
        uint? vk = null;
        foreach (var part in (spec ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.ToLowerInvariant();
            switch (p)
            {
                case "alt": mods |= ModAlt; continue;
                case "ctrl" or "control": mods |= ModCtrl; continue;
                case "shift": mods |= ModShift; continue;
                case "win": mods |= ModWin; continue;
            }
            if (vk is not null) throw new FormatException($"Hotkey '{spec}' names more than one key.");
            if (p.Length == 1 && char.IsAsciiLetterOrDigit(p[0])) vk = char.ToUpperInvariant(p[0]);
            else if (p == "space") vk = 0x20;
            else if (p == "pause") vk = 0x13;
            else if (p == "insert") vk = 0x2D;
            else if (p == "home") vk = 0x24;
            else if (p == "end") vk = 0x23;
            else if (p.Length >= 2 && p[0] == 'f' && int.TryParse(p[1..], out var n) && n is >= 1 and <= 24) vk = (uint)(0x70 + n - 1);
            else throw new FormatException($"Hotkey '{spec}' has an unknown key '{part}'.");
        }
        if (vk is null || mods == 0) throw new FormatException($"Hotkey '{spec}' needs at least one modifier and one key.");
        return (mods, vk.Value);
    }
}
