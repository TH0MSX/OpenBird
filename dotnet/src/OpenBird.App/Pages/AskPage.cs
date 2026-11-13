using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenBird.Core;
using OpenBird.Windows;

namespace OpenBird.App.Pages;

internal sealed class AskPage : UserControl, IPage
{
    private readonly OpenBirdEngine _engine;
    private readonly AutoSuggestBox _question = new() { PlaceholderText = "What did we agree about the audit?", QueryIcon = new SymbolIcon(Symbol.Find), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20 };
    private readonly InfoBar _notice = Ui.Notice();
    private readonly MarkdownView _answer = new();
    private readonly Border _answerCard;
    private readonly StackPanel _empty;
    private readonly StackPanel _sources = new() { Spacing = 8 };
    private readonly StackPanel _history = new() { Spacing = 8 };
    private string? _lastQuestion;
    private string? _lastAnswer;
    private readonly TextBlock _mode = Ui.Text("", "CaptionTextBlockStyle", secondary: true);

    public AskPage(OpenBirdEngine engine)
    {
        _engine = engine;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_question, "Question");
        _question.QuerySubmitted += (_, _) => Run();

        var search = new Grid { ColumnSpacing = 12 };
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        search.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_busy, 1);
        search.Children.Add(_question);
        search.Children.Add(_busy);

        _answerCard = Ui.Card(_answer, 20);
        _answerCard.Visibility = Visibility.Collapsed;
        _empty = Ui.EmptyState("\uE721", "Ask your memory",
            "Search what was said in meetings and what was on your screen, in plain words.",
            Ui.ChipColumn(
                Ui.Chip("What did we decide in my last meeting?", () => Ask("What did we decide in my last meeting?")),
                Ui.Chip("What action items are still open?", () => Ask("What action items are still open?")),
                Ui.Chip("What was I working on this morning?", () => Ask("What was I working on this morning?"))));

        Content = Ui.Page("Ask",
            Ui.Text("Search your meetings and screen history in plain words.", secondary: true),
            search,
            _mode,
            _notice,
            _empty,
            _answerCard,
            _sources,
            _history);
    }

    private void Ask(string text)
    {
        _question.Text = text;
        Run();
    }

    public void OnShown()
    {
        _mode.Text = (_engine.Settings.EffectiveAiMode, _engine.Models().Count > 0) switch
        {
            ("cloud", true) => "AI: cloud. The matching excerpts are sent to your provider, which also suggests related search words.",
            ("local", true) => "AI: on this PC. A local model widens the search and writes the answer. Nothing leaves this PC.",
            ("cloud", false) => "AI is set to cloud, but no key is entered in Settings. Answers are the matching excerpts.",
            ("local", false) => "AI is set to this PC, but the address in Settings is not valid. Answers are the matching excerpts.",
            _ => "AI is off: answers are the matching excerpts from this PC. Nothing is sent anywhere.",
        };
        _question.Focus(FocusState.Programmatic);
    }

    private async void Run()
    {
        var text = _question.Text.Trim();
        if (text.Length == 0 || _busy.IsActive) return;
        _busy.IsActive = true;
        try
        {
            var ask = _engine.Ask;
            var models = _engine.Models();
            var (previousQuestion, previousAnswer) = (_lastQuestion, _lastAnswer);
            var result = await Task.Run(() => ask.AskAsync(text, models, previousQuestion: previousQuestion, previousAnswer: previousAnswer));
            if (_lastQuestion is not null && _lastAnswer is not null)
            {
                // Earlier questions stay on the page, newest first, collapsed to keep the current answer in view.
                if (_history.Children.Count == 0) _history.Children.Add(Ui.Text("Earlier", "BodyStrongTextBlockStyle"));
                var previous = new MarkdownView();
                previous.Show(_lastAnswer);
                _history.Children.Insert(1, new Expander { Header = _lastQuestion, Content = previous, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left });
            }
            _lastQuestion = text;
            _lastAnswer = result.Answer;
            _question.Text = "";
            if (string.IsNullOrEmpty(result.Notice)) _notice.IsOpen = false; else _notice.Show(result.Notice, InfoBarSeverity.Warning);
            _empty.Visibility = Visibility.Collapsed;
            _answerCard.Visibility = Visibility.Visible;
            _answer.Show(result.Answer);
            _sources.Children.Clear();
            if (result.Sources.Count > 0) _sources.Children.Add(Ui.Text("Sources", "SubtitleTextBlockStyle"));
            foreach (var s in result.Sources.Take(12)) _sources.Children.Add(Source(s));
        }
        finally { _busy.IsActive = false; }
    }

    private static UIElement Source(SearchHit s)
    {
        var kind = s.Kind switch { "meeting" => "Meeting", "screen" => "Screen", _ => "Action" };
        var body = Ui.Stack(2, Ui.Text($"{kind} · {s.Title} · {s.When}", "CaptionTextBlockStyle", secondary: true, wrap: false), Ui.Text(s.Snippet));
        var card = Ui.Card(body, 12);
        if (!string.IsNullOrEmpty(s.Path) && File.Exists(s.Path))
        {
            var open = new HyperlinkButton { Content = s.Kind == "screen" ? "Open screenshot" : "Open notes", Padding = new Thickness(0) };
            open.Click += (_, _) => Ui.Open(s.Path);
            body.Children.Add(open);
        }
        return card;
    }
}
