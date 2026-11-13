using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenBird.Core;
using OpenBird.Windows;

namespace OpenBird.App.Pages;

internal sealed class TimelinePage : UserControl, IPage
{
    private readonly OpenBirdEngine _engine;
    private readonly CalendarDatePicker _date = new() { Date = DateTimeOffset.Now };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, StepFrequency = 1, MinWidth = 420 };
    private readonly TextBlock _caption = Ui.Text("", "BodyStrongTextBlockStyle");
    private readonly TextBlock _position = Ui.Text("", "CaptionTextBlockStyle", secondary: true);
    private readonly Image _image = new() { MaxHeight = 420, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _empty = Ui.Text("", secondary: true);
    private List<ScreenFrame> _frames = new();

    public TimelinePage(OpenBirdEngine engine)
    {
        _engine = engine;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_slider, "Position in the day");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_date, "Day");
        _date.DateChanged += (_, _) => Load();
        _slider.ValueChanged += (_, _) => ShowFrame();
        _slider.ThumbToolTipValueConverter = null;

        var previous = Ui.Button("Earlier", "", () => _slider.Value = Math.Max(0, _slider.Value - 1));
        var next = Ui.Button("Later", "", () => _slider.Value = Math.Min(_slider.Maximum, _slider.Value + 1));
        var open = Ui.Button("Open image", "", () => Ui.Open(Current()?.ImagePath));
        var copy = Ui.Button("Copy text", "", () => { if (Current() is { } f) Ui.Copy(f.OcrText); });

        Content = Ui.Page("Timeline",
            Ui.Text("Step through what was on your screen during a day.", secondary: true),
            Ui.Row(12, _date, previous, _slider, next),
            _position,
            _empty,
            _caption,
            Ui.Card(_image, 8),
            Ui.Row(8, open, copy),
            Ui.Card(_text));
    }

    public void OnShown() => Load();

    private ScreenFrame? Current() => _frames.Count == 0 ? null : _frames[(int)Math.Clamp(_slider.Value, 0, _frames.Count - 1)];

    private void Load()
    {
        var day = (_date.Date ?? DateTimeOffset.Now).LocalDateTime.Date;
        var from = new DateTimeOffset(day).ToUnixTimeSeconds();
        _frames = _engine.Database.GetScreenFrames(from, from + 86400 - 0.001, 20000);
        _slider.Maximum = Math.Max(0, _frames.Count - 1);
        _slider.Value = _slider.Maximum;
        _empty.Text = _frames.Count == 0 ? "Nothing was captured on this day." : "";
        ShowFrame();
    }

    private void ShowFrame()
    {
        var frame = Current();
        if (frame is null)
        {
            _caption.Text = _position.Text = _text.Text = "";
            _image.Source = null;
            return;
        }
        _caption.Text = $"{frame.TimeStr} · {frame.WindowTitle}";
        _position.Text = $"Capture {(int)_slider.Value + 1} of {_frames.Count}";
        _text.Text = string.IsNullOrWhiteSpace(frame.OcrText) ? "(no text was read from this capture)" : frame.OcrText;
        _image.Source = !string.IsNullOrEmpty(frame.ImagePath) && File.Exists(frame.ImagePath)
            ? new BitmapImage(new Uri(frame.ImagePath)) { DecodePixelWidth = 1200 }
            : null;   // images are deleted after the retention period; the text stays
    }
}
