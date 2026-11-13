using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using OpenBird.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OpenBird.Windows;

/// <summary>Grabs the desktop with GDI, saves it as JPEG and reads it with the built-in Windows OCR.</summary>
public sealed class WindowsScreenSource : IScreenSource
{
    private readonly Action<string> _log;
    private OcrEngine? _ocr;

    public WindowsScreenSource(Action<string>? log = null) => _log = log ?? (_ => { });

    /// <summary>Call once at process start so captures use real pixels on scaled displays.</summary>
    public static void EnablePerMonitorDpiAwareness() => Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

    public ForegroundWindow? GetForegroundWindow()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var length = Native.GetWindowTextLengthW(hwnd);
        var title = new StringBuilder(length + 1);
        if (length > 0) Native.GetWindowTextW(hwnd, title, title.Capacity);

        var process = "";
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0)
        {
            var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var name = new StringBuilder(1024);
                    var size = (uint)name.Capacity;
                    if (Native.QueryFullProcessImageNameW(handle, 0, name, ref size)) process = Path.GetFileName(name.ToString());
                }
                finally { Native.CloseHandle(handle); }
            }
        }
        return new ForegroundWindow(title.ToString(), process);
    }

    public CapturedScreen? Capture(string imagePath, bool allMonitors)
    {
        int x = 0, y = 0;
        var width = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        var height = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        if (allMonitors)
        {
            x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        }
        if (width <= 0 || height <= 0) return null;

        var pixels = GrabBgra(x, y, width, height);
        if (pixels is null) return null;                 // locked screen, secure desktop or display asleep

        try
        {
            SaveJpeg(pixels, width, height, imagePath);
        }
        catch (Exception e) when (e is IOException or COMException or UnauthorizedAccessException)
        {
            _log($"Could not save screenshot {imagePath}: {e.Message}");
            return null;
        }
        return new CapturedScreen(imagePath, width, height, Thumbnail(pixels, width, height));
    }

    private static byte[]? GrabBgra(int x, int y, int width, int height)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var memory = Native.CreateCompatibleDC(screen);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = width, biHeight = -height,   // negative: top-down rows
            biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var bitmap = Native.CreateDIBSection(memory, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;
            var previous = Native.SelectObject(memory, bitmap);
            var ok = Native.BitBlt(memory, 0, 0, width, height, screen, x, y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.SelectObject(memory, previous);
            if (!ok) return null;
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>8x8 grayscale thumbnail: each cell is the average of a grid of sampled pixels.</summary>
    private static byte[] Thumbnail(byte[] bgra, int width, int height)
    {
        var thumb = new byte[64];
        for (var cy = 0; cy < 8; cy++)
            for (var cx = 0; cx < 8; cx++)
            {
                long sum = 0;
                var count = 0;
                for (var sy = 0; sy < 12; sy++)
                    for (var sx = 0; sx < 12; sx++)
                    {
                        var px = (cx * 12 + sx) * width / 96;
                        var py = (cy * 12 + sy) * height / 96;
                        var i = (py * width + px) * 4;
                        sum += (bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8;   // B, G, R weights
                        count++;
                    }
                thumb[cy * 8 + cx] = (byte)(sum / count);
            }
        return thumb;
    }

    private static void SaveJpeg(byte[] bgra, int width, int height, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        using var input = stream.AsStreamForRead();
        using var output = File.Create(path);
        input.CopyTo(output);
    }

    public async Task<string> ReadTextAsync(string imagePath, CancellationToken ct)
    {
        _ocr ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (_ocr is null)
        {
            _log("Windows OCR is not available for your display languages. Install a language pack with OCR support.");
            return "";
        }

        try
        {
            using var file = File.OpenRead(imagePath);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);

            // Windows OCR rejects images larger than its limit; scale down to fit.
            var max = OcrEngine.MaxImageDimension;
            var scale = Math.Min(1.0, Math.Min(max / (double)decoder.PixelWidth, max / (double)decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
            var result = await _ocr.RecognizeAsync(bitmap).AsTask(ct);
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        catch (Exception e) when (e is IOException or COMException or UnauthorizedAccessException)
        {
            _log($"OCR failed for {imagePath}: {e.Message}");
            return "";
        }
    }
}
