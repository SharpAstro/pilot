using System.Collections.Immutable;
using DIR.Lib;
using DIR.Lib.Notifications;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// A card is read whole on the Notifications page and at a glance in its toast (the owner on tianwen#2435's design card, 2026-11-22: "in the
/// notification tab it should always show the full thing with all options, when in the toaster it should say that some parts are cropped off
/// and provide a link to the notification tab", and "this is like a list of options but there's no linebreaks"). The page's row is the whole
/// card: every choice, its detail open and uncut. A toast that leaves part of the card out ends with a link to the page. A design card's
/// question, options and why it rings are lines of their own, on the page and on the For you bar. Captures at 1920x1080 go to
/// PILOT_JOB_ARTIFACTS.
/// </summary>
public sealed class CardWholeInNotificationsTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    // c26885 as the node raised it for tianwen#2435: three options too long for a button, so each is in the summary by its tag.
    private const string Summary = "remove-stars can no longer show a model's own overshoot,... A: clamp by default and give remove-stars and the split a switch"
        + " that leaves the plate as the remover wrote it (the scorers pass it, the four help texts name it) (recommended). B: keep the clamp"
        + " unconditional, reword the help texts and the scorers' comments, and read the 'added' row on an unclamped plate some other way (a plate"
        + " handed over with --plates). C: move the clamp out of the split into the stars layer only (stars = max(0, plane - plate), the plate stays raw).";

    private const string Detail = "src/TianWen.Lib/Imaging/NarrowbandCombination.cs:115 remove-stars can no longer show a model's own overshoot, and the scorer relies on it.\n"
        + "A. clamp by default and give remove-stars and the split a switch that leaves the plate as the remover wrote it (recommended)\n"
        + "B. keep the clamp unconditional, reword the help texts and the scorers' comments\n"
        + "C. move the clamp out of the split into the stars layer only, the plate stays raw\n"
        + "No fixer started: this is the author's to decide.";

    private static CardRow Design(string id = "c26885") => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "pr-design", Subject = "pr/SharpAstro/tianwen#2435", Title = "SharpAstro/tianwen#2435: the review asks a question of design",
        Summary = Summary, Detail = Detail, At = Live.At, State = CardState.Open,
        Choices =
        [
            new CardChoice { Label = "Pick option A", Verb = "answer", Target = "pr/SharpAstro/tianwen#2435" },
            new CardChoice { Label = "Pick option B", Verb = "answer", Target = "pr/SharpAstro/tianwen#2435" },
            new CardChoice { Label = "Pick option C", Verb = "answer", Target = "pr/SharpAstro/tianwen#2435" },
            new CardChoice { Label = "Answer in your own words", Verb = "answer", Target = "pr/SharpAstro/tianwen#2435", AsksFor = "Your answer" },
        ],
    };

    private static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class Window : IDisposable
    {
        private readonly int _width, _height;
        private readonly PageData _data = PageData.For("sample");
        private ImmutableArray<Layout.ArrangedNode<float>> _arranged = [];
        public RgbaImageRenderer Renderer { get; }
        public PilotChrome<RgbaImage> Chrome { get; }

        public Window(int width, int height)
        {
            _width = width;
            _height = height;
            Renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(Renderer, new SessionsView<RgbaImage>(Renderer)) { FontPath = SessionsCapture.FontPath };
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Jobs);
            var seen = Chrome.NotificationsPage.Arranged;
            Chrome.NotificationsPage.Arranged = nodes =>
            {
                _arranged = nodes;
                seen?.Invoke(nodes);
            };
        }

        public NotificationsHost Bar => Chrome.Notifications;

        public void Frame(DateTimeOffset at, CardsScreen cards) =>
            Chrome.Render(_data.Sessions with { AsOf = at }, _data.Flights, cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, _width, _height), _data.Machines);

        /// <summary>The page's text leaves as the last frame arranged them, with where each was put.</summary>
        public List<(string Text, Rect<float> Bounds)> Leaves() => [.. _arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => (((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, a.Bounds))];

        public List<ClickableRegion> ToastRegions()
        {
            var regions = new List<ClickableRegion>();
            Chrome.NotificationsLayer.CollectPaintedRegions(regions);
            return regions;
        }

        public void Save(string name) => File.WriteAllBytes(Path.Combine(ArtifactDir(), name + ".png"), PngWriter.Encode(Renderer.Surface.Pixels, _width, _height));

        public void Dispose() => Renderer.Dispose();
    }

    // Every text under a node: its leaves, read through whatever property holds a child or children (a stack, a wrap, a grid, a decorated node).
    private static IEnumerable<string> Texts(Layout.Node node) => node is Layout.Node.Leaf { Content: Layout.Content.Text text }
        ? [text.Value]
        : node.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0).SelectMany(p => p.GetValue(node) switch
        {
            Layout.Node child => Texts(child),
            IEnumerable<Layout.Node> children => children.SelectMany(Texts),
            _ => [],
        });

    // The text with no whitespace, so a label that is one leaf and one made of word leaves compare alike.
    private static string Squeezed(string text) => string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)));

    private static string Joined(Layout.Node node) => Squeezed(string.Concat(Texts(node)));

    [Fact]
    public void TheToastOfACardItCutsShortSaysSoAndItsLinkOpensTheNotificationsPage()
    {
        using var w = new Window(1920, 1080);
        w.Frame(T0, CardsScreen.Empty);
        w.Frame(T0.AddSeconds(1), CardsScreen.From([Design()]));

        var toast = w.Bar.Toasts.Toasts.ShouldHaveSingleItem().Notification;
        var body = Joined(toast.Row.ShouldNotBeNull().Invoke());
        // The plain choices sit in Dismiss's row as the library's buttons (Notification.Buttons), not in the body.
        // A button label is cut at ToastLabelChars (the whole on hover), so both options read as their cut label.
        var buttons = toast.Buttons.ShouldNotBeNull().Invoke();
        buttons.Count.ShouldBe(2, "a toast carries two plain choices");
        var choices = Squeezed(string.Concat(buttons.SelectMany(Texts)));
        choices.ShouldContain(Squeezed(PilotChrome<RgbaImage>.CutToastLabel("Pick option A")));
        choices.ShouldNotContain(Squeezed("Pick option C"), Case.Sensitive);
        body.ShouldContain(Squeezed(PilotChrome<RgbaImage>.ToastCutLabel), Case.Sensitive);
        w.Save("card-whole-toast-1920x1080");

        var link = w.ToastRegions().Single(r => r.Result is HitResult.ButtonHit { Action: "toast:c26885:whole" });
        link.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);
        w.Chrome.Page.ShouldBe(PilotPage.Notifications);
    }

    [Fact]
    public void AToastThatShowsTheWholeCardSaysNothingOfACut()
    {
        var whole = Design() with { Summary = "TIA1818's PR #63 is ready.", Detail = null, Choices = [Design().Choices[0]] };
        PilotChrome<RgbaImage>.ToastCuts(whole, shownChoices: 1).ShouldBeFalse();
        PilotChrome<RgbaImage>.ToastCuts(whole with { Detail = "more to read" }, shownChoices: 1).ShouldBeTrue("a detail the toast never shows");
        PilotChrome<RgbaImage>.ToastCuts(Design() with { Detail = null }, shownChoices: 2).ShouldBeTrue("a summary past three lines, a third option");
    }

    [Fact]
    public void TheNotificationsPageListsTheWholeCardEveryChoiceAndEachOptionOnALineOfItsOwn()
    {
        using var w = new Window(1920, 1080);
        w.Frame(T0, CardsScreen.From([Design()]));
        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(T0.AddSeconds(1), CardsScreen.From([Design()]));
        w.Save("card-whole-notifications-page-1920x1080");

        var row = Joined(w.Bar.Feed.NeedsYou.ShouldHaveSingleItem().Row.ShouldNotBeNull().Invoke());
        foreach (var choice in new[] { "Pick option A", "Pick option B", "Pick option C", "Answer in your own words", "Dismiss" })
        {
            row.ShouldContain(Squeezed(choice), Case.Sensitive, $"the page carries {choice}");
        }
        row.ShouldContain("Nofixerstarted", Case.Sensitive, "the detail is open and whole, its last line too");
        row.ShouldNotContain("Detail", Case.Sensitive, "no fold to open on the page");
        row.ShouldNotContain("closed", Case.Sensitive, "an open card is drawn open, though the collapsed bar never drew it");
        var regions = new List<ClickableRegion>();
        w.Chrome.PageWidget.CollectPaintedRegions(regions);
        regions.ShouldContain(r => r.Result.Equals(new HitResult.ButtonHit("card:c26885:3")), "Pick option C answers from the page");

        // Each option starts a line of its own, at the summary's left edge, one under the other.
        var leaves = w.Leaves();
        Rect<float> At(string word) => leaves.First(l => l.Text == word + " ").Bounds;
        var (a, b, c) = (At("A:"), At("B:"), At("C:"));
        b.Y.ShouldBeGreaterThan(a.Y);
        c.Y.ShouldBeGreaterThan(b.Y);
        b.X.ShouldBe(a.X, 0.5f);
        c.X.ShouldBe(a.X, 0.5f);
        At("remove-stars").X.ShouldBe(a.X, 0.5f, "the question is a line of its own, and the first option starts the next");
    }

    [Fact]
    public void ACardGoneFromThePanelIsListedInTheHistoryWithItsButtonsDead()
    {
        using var w = new Window(1920, 1080);
        w.Frame(T0, CardsScreen.From([Design()]));
        w.Frame(T0.AddSeconds(1), CardsScreen.Empty);
        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(T0.AddSeconds(2), CardsScreen.Empty);

        var gone = w.Bar.History.ShouldHaveSingleItem();
        gone.Row.ShouldNotBeNull().Invoke();
        var regions = new List<ClickableRegion>();
        w.Chrome.PageWidget.CollectPaintedRegions(regions);
        regions.Where(r => r.Result is HitResult.ButtonHit { Action: var action } && action.StartsWith("card:c26885:", StringComparison.Ordinal) && r.OnClick != null)
            .ShouldBeEmpty("a card no longer open answers nothing");
    }

    [Fact]
    public void TheForYouBarDrawsTheSummaryWholeEachOptionOnALineOfItsOwn()
    {
        using var w = new Window(1920, 1080);
        w.Bar.Open = true;
        w.Frame(T0, CardsScreen.From([Design()]));
        w.Save("card-whole-for-you-bar-1920x1080");

        // The summary is no longer cut with the rest on hover: every summary the node keeps (at most 600 characters) is drawn whole.
        CardsView<RgbaImage>.SummaryChars.ShouldBeGreaterThan(Summary.Length);
        var panel = Squeezed(string.Concat(Texts(w.Chrome.Cards.Build(CardsScreen.From([Design()])))));
        panel.ShouldContain(Squeezed("C: move the clamp out of the split"), Case.Sensitive);
    }
}
