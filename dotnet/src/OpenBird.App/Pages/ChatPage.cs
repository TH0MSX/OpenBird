using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBird.Core;
using OpenBird.Windows;

namespace OpenBird.App.Pages;

/// <summary>A general chat with the model chosen in Settings. It does not read meetings or screen history; Ask does that.</summary>
internal sealed class ChatPage : UserControl, IPage
{
    private readonly OpenBirdEngine _engine;
    private readonly ChatSession _session;
    private readonly StackPanel _messages = new() { Spacing = 12 };
    private readonly TextBox _input = new()
    {
        PlaceholderText = "Message OpenBird",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 140,
        BorderThickness = new Thickness(0),
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private readonly Button _send;
    private readonly Button _newChat;
    private readonly InfoBar _notice = Ui.Notice();
    private readonly TextBlock _mode = Ui.Text("", "CaptionTextBlockStyle", secondary: true);
    private readonly StackPanel _empty;
    private readonly ScrollViewer _scroll;
    private bool _busy;

    public ChatPage(OpenBirdEngine engine)
    {
        _engine = engine;
        _session = new ChatSession(engine.Settings.UserName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_input, "Message");
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) return;
            e.Handled = true;
            Send();
        };

        _send = Ui.Button("Send", "", Send, accent: true);
        _newChat = Ui.Button("New chat", "", NewChat);
        _empty = Ui.EmptyState("", "How can I help?",
            "Ask anything. This chat runs on the model chosen in Settings and does not read your meetings (use Ask for those).",
            Ui.ChipColumn(
                Ui.Chip("Draft a short, friendly follow-up email", () => Prefill("Draft a short, friendly follow-up email after a meeting about a delayed delivery.")),
                Ui.Chip("Explain something in simple terms", () => Prefill("Explain how a heat pump works in simple terms.")),
                Ui.Chip("Help me plan my day", () => Prefill("Help me plan a focused workday with three priorities and two breaks."))));

        // Header: title and status on the left, New chat on the right.
        var title = Ui.Stack(2, Ui.Text("Chat", "TitleTextBlockStyle"), _mode);
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _newChat.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_newChat, 1);
        header.Children.Add(title);
        header.Children.Add(_newChat);

        // Messages scroll; the composer stays docked at the bottom, as in a messaging app.
        var messageArea = Ui.Stack(0, _empty, _messages);
        messageArea.Padding = new Thickness(0, 8, 12, 8);
        _scroll = new ScrollViewer { Content = messageArea, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false };

        var composer = new Grid { ColumnSpacing = 8 };
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_send, 1);
        composer.Children.Add(_input);
        composer.Children.Add(_send);
        var composerCard = Ui.Card(composer, 8);

        var layout = new Grid { RowSpacing = 12, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(40, 24, 40, 24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(_notice, 2);
        Grid.SetRow(composerCard, 3);
        layout.Children.Add(header);
        layout.Children.Add(_scroll);
        layout.Children.Add(_notice);
        layout.Children.Add(composerCard);
        Content = layout;
    }

    public void OnShown()
    {
        var models = _engine.Models();
        _mode.Text = (_engine.Settings.EffectiveAiMode, models.Count > 0) switch
        {
            ("local", true) => $"{models[0].Name} · runs on this PC, nothing leaves it",
            ("cloud", true) => "Cloud AI · what you type is sent to your provider",
            ("local", false) => "AI is set to this PC, but the address in Settings is not valid.",
            ("cloud", false) => "AI is set to cloud, but no key is entered in Settings.",
            _ => "AI is off. Choose this PC or cloud in Settings to chat.",
        };
        _input.Focus(FocusState.Programmatic);
    }

    private void Prefill(string text)
    {
        _input.Text = text;
        Send();
    }

    private void NewChat()
    {
        if (_busy) return;
        _session.Clear();
        _messages.Children.Clear();
        _empty.Visibility = Visibility.Visible;
        _notice.IsOpen = false;
        _input.Focus(FocusState.Programmatic);
    }

    private async void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _busy) return;
        SetBusy(true);
        _notice.IsOpen = false;
        _empty.Visibility = Visibility.Collapsed;
        var pending = UserBubble(text);
        var thinking = Thinking();
        _messages.Children.Add(pending);
        _messages.Children.Add(thinking);
        _input.Text = "";
        ScrollToEnd();
        try
        {
            var models = _engine.Models();
            var reply = await Task.Run(() => _session.SendAsync(models, text, CancellationToken.None));
            _messages.Children.Remove(thinking);
            _messages.Children.Add(AssistantBubble(reply));
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        {
            // Not kept in the conversation: put the text back so it can be sent again.
            _messages.Children.Remove(thinking);
            _messages.Children.Remove(pending);
            if (_messages.Children.Count == 0) _empty.Visibility = Visibility.Visible;
            _input.Text = text;
            _notice.Show(e.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            SetBusy(false);
            ScrollToEnd();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.IsEnabled = !busy;
        _newChat.IsEnabled = !busy;
    }

    /// <summary>Your message: right-aligned on the accent colour.</summary>
    private static UIElement UserBubble(string text)
    {
        var label = Ui.Text(text);
        label.Foreground = Ui.Brush("TextOnAccentFillColorPrimaryBrush");
        label.IsTextSelectionEnabled = true;
        return new Border
        {
            Child = label,
            Background = Ui.Brush("AccentFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
    }

    /// <summary>The model's reply: a card on the left with a Copy action.</summary>
    private static UIElement AssistantBubble(string reply)
    {
        var view = new MarkdownView();
        view.Show(reply);
        var copy = new HyperlinkButton { Content = "Copy", Padding = new Thickness(0) };
        copy.Click += (_, _) => Ui.Copy(reply);
        var card = Ui.Card(Ui.Stack(6, Ui.Text("OpenBird", "CaptionTextBlockStyle", secondary: true), view, copy), 14);
        card.MaxWidth = 760;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        return card;
    }

    private static UIElement Thinking()
    {
        var row = Ui.Row(10, new ProgressRing { IsActive = true, Width = 18, Height = 18 }, Ui.Text("Thinking…", secondary: true));
        row.HorizontalAlignment = HorizontalAlignment.Left;
        row.Padding = new Thickness(4);
        return row;
    }

    private void ScrollToEnd() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null));
}
