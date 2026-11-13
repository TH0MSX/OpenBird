using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenBird.Core;

/// <summary>One grab of the screen: where the image was saved, and a small grayscale thumbnail for comparing frames.</summary>
public sealed record CapturedScreen(string ImagePath, int Width, int Height, byte[] Thumbnail8x8);

public sealed record ForegroundWindow(string Title, string ProcessName);

/// <summary>Screen access. The Windows implementation grabs the desktop and runs Windows OCR.</summary>
public interface IScreenSource
{
    ForegroundWindow? GetForegroundWindow();
    /// <summary>Saves a capture to `imagePath`. Returns null when the screen cannot be read (locked, asleep).</summary>
    CapturedScreen? Capture(string imagePath, bool allMonitors);
    Task<string> ReadTextAsync(string imagePath, CancellationToken ct);
}

public static class FrameHash
{
    /// <summary>64-bit average hash of an 8x8 grayscale thumbnail: similar screens give similar bits.</summary>
    public static ulong Compute(ReadOnlySpan<byte> gray8x8)
    {
        if (gray8x8.Length != 64) throw new ArgumentException("Thumbnail must be 8x8 grayscale (64 bytes).");
        var sum = 0;
        foreach (var v in gray8x8) sum += v;
        var mean = sum / 64.0;
        ulong hash = 0;
        for (var i = 0; i < 64; i++) if (gray8x8[i] > mean) hash |= 1UL << i;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

public static class WindowTitles
{
    private static readonly string[] NoiseSuffixes =
    {
        " - Google Chrome", " - Microsoft​ Edge", " - Microsoft Edge", " - Mozilla Firefox", " - Brave",
        " - Visual Studio Code", " - Notepad", " - Word", " - Excel", " - PowerPoint",
    };

    /// <summary>Strips browser and editor suffixes, unsaved markers and notification counts. Port of `clean_window_title`.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Desktop / Inactive";
        var t = raw.Trim();
        if (t.StartsWith("● ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) t = t[2..].Trim();
        if (t.StartsWith('('))
        {
            var close = t.IndexOf(')');
            if (close > 1 && t[1..close].All(char.IsDigit)) t = t[(close + 1)..].TrimStart();
        }
        foreach (var suffix in NoiseSuffixes)
            if (t.EndsWith(suffix, StringComparison.Ordinal)) { t = t[..^suffix.Length].Trim(); break; }
        return t.Length == 0 ? "Desktop / Inactive" : t;
    }
}

public enum ScreenTick { Stored, Paused, Excluded, Inactive, Unavailable, Unchanged }

/// <summary>
/// The continuous screen memory: every tick, look at the foreground window and
/// decide whether to capture, read and store the screen. All decisions are
/// here; grabbing pixels and OCR are behind <see cref="IScreenSource"/>.
///
/// Changes from the Python version: an excluded window is never captured at
/// all; an unchanged screen is detected from a thumbnail BEFORE OCR runs and
/// its image is deleted, instead of running OCR on every frame and keeping
/// every image; and deleting old screenshots happens daily, not only at start.
/// </summary>
public sealed class ScreenWatcher
{
    private static readonly HashSet<string> InactiveTitles = new(StringComparer.Ordinal)
        { "Desktop / Inactive", "Unknown", "LockApp.exe", "Windows Default Lock Screen", "" };

    private readonly Database _db;
    private readonly IScreenSource _screen;
    private readonly AppSettings _settings;
    private readonly string _timelineDir;
    private string _lastTitle = "";
    private ulong _lastHash;
    private bool _hasLast;
    private double _lastStored;
    private DateOnly _lastPrune;

    public ScreenWatcher(Database db, IScreenSource screen, AppSettings settings)
    {
        _db = db;
        _screen = screen;
        _settings = settings;
        _timelineDir = Path.Combine(settings.StorageDir, "timeline");
    }

    public bool Paused { get; set; }
    public int StoredCount { get; private set; }
    public int ExcludedCount { get; private set; }
    public string LastWindow { get; private set; } = "None";

    /// <summary>Unchanged screens are still stored this often, so the timeline has no long holes.</summary>
    public double KeepAliveSeconds { get; init; } = 300;

    public async Task<ScreenTick> TickAsync(DateTime now, bool force = false, CancellationToken ct = default)
    {
        if (Paused && !force) return ScreenTick.Paused;
        PruneIfDue(now);

        var window = _screen.GetForegroundWindow();
        if (window is not null && Privacy.IsExcluded(window.Title, window.ProcessName, _settings.CaptureExclusions))
        {
            ExcludedCount++;
            LastWindow = "Excluded (private)";
            return ScreenTick.Excluded;                   // applies to forced captures too
        }
        var title = WindowTitles.Clean(window?.Title);
        if (InactiveTitles.Contains(title) && !force) return ScreenTick.Inactive;

        Directory.CreateDirectory(_settings.ScreenshotsDir);
        var imagePath = Path.Combine(_settings.ScreenshotsDir, $"screen_{now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.jpg");
        var shot = _screen.Capture(imagePath, _settings.CaptureAllMonitors);
        if (shot is null) return ScreenTick.Unavailable;

        // The foreground window can change between the check and the grab.
        var after = _screen.GetForegroundWindow();
        if (after is not null && Privacy.IsExcluded(after.Title, after.ProcessName, _settings.CaptureExclusions))
        {
            TryDelete(shot.ImagePath);
            ExcludedCount++;
            LastWindow = "Excluded (private)";
            return ScreenTick.Excluded;
        }

        var unix = new DateTimeOffset(now).ToUnixTimeMilliseconds() / 1000.0;
        var hash = FrameHash.Compute(shot.Thumbnail8x8);
        var unchanged = _hasLast && title == _lastTitle && FrameHash.Distance(hash, _lastHash) <= 2;
        if (unchanged && !force && unix - _lastStored < KeepAliveSeconds)
        {
            TryDelete(shot.ImagePath);
            return ScreenTick.Unchanged;
        }

        var text = _settings.ScreenOcrEnabled ? (await _screen.ReadTextAsync(shot.ImagePath, ct)).Trim() : "";
        var timeText = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _db.AddScreenFrame(new ScreenFrame(unix, timeText, title, shot.ImagePath, text));
        AppendJournal(now, title, text, Path.GetFileName(shot.ImagePath));

        _lastTitle = title;
        _lastHash = hash;
        _hasLast = true;
        _lastStored = unix;
        StoredCount++;
        LastWindow = title;
        return ScreenTick.Stored;
    }

    private void AppendJournal(DateTime now, string title, string text, string imageName)
    {
        Directory.CreateDirectory(_timelineDir);
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(_timelineDir, $"{day}_daily_context.md");
        var sb = new StringBuilder();
        if (!File.Exists(path)) sb.Append($"# OpenBird screen journal: {day}\n\n");
        sb.Append($"### {now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {title}\n\n");
        if (text.Length > 0)
        {
            var excerpt = (text.Length > 600 ? text[..600] + "…" : text).Replace("\r", "").Replace("\n", "\n> ");
            sb.Append($"> {excerpt}\n\n");
        }
        sb.Append($"*Image: {imageName}*\n\n---\n\n");
        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Deletes screenshot files older than the retention setting, once per day.</summary>
    private void PruneIfDue(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (today == _lastPrune || !Directory.Exists(_settings.ScreenshotsDir)) return;
        _lastPrune = today;
        var cutoff = now.AddDays(-Math.Max(1, _settings.ScreenshotRetentionDays));
        foreach (var file in Directory.EnumerateFiles(_settings.ScreenshotsDir, "screen_*.*"))
            if (File.GetLastWriteTime(file) < cutoff) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
