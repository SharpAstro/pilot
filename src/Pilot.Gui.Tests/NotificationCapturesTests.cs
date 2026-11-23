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
/// The captures pilot #1292 asks for, before and after: the Notifications page at 2880x1814 DPI 1.5 and at 1280x800, the For you panel, and a staying
/// toast whose buttons wrap. Each is saved as a PNG to PILOT_JOB_ARTIFACTS (else the test's captures folder) and checked with the text overlap check.
/// Self-contained, so the same file runs on the commit before the flight to make the before captures.
/// </summary>
public sealed class NotificationCapturesTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    private static string Dir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CardRow Card(string id, CardBin bin, string title, params CardChoice[] choices) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Summary = "A short line on what happened and what the choices do.",
        At = T0.AddSeconds(0.5), State = CardState.Open,
        Choices = choices.Length > 0 ? choices : [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static CardsScreen Three() => CardsScreen.From(
    [
        Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge"),
        Card("c2", CardBin.ForYou, "PIL795: 5 legs planned"),
        Card("c3", CardBin.ForYou, "PIL1293 is stuck: its worker stopped with the leg unfinished"),
    ]);

    private static CardsScreen Stuck() => CardsScreen.From(
    [
        Card("c28040", CardBin.Ring, "PIL1293 is stuck",
            new CardChoice { Label = "Nudge the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" },
            new CardChoice { Label = "Restart the worker", Verb = Verbs.StartLeg, Target = "flight/PIL1293/gh-call-counter" }),
    ]);

    private sealed class Window : IDisposable
    {
        private readonly PageData _data = PageData.For("sample");

        public Window(int width, int height, float dpi)
        {
            Width = width;
            Height = height;
            Renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(Renderer, new SessionsView<RgbaImage>(Renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Jobs);
        }

        public int Width { get; }
        public int Height { get; }
        public RgbaImageRenderer Renderer { get; }
        public PilotChrome<RgbaImage> Chrome { get; }
        public ImmutableArray<Layout.ArrangedNode<float>> Seen { get; set; } = [];

        public void Frame(int seconds, CardsScreen cards) =>
            Chrome.Render(_data.Sessions with { AsOf = T0.AddSeconds(seconds) }, _data.Flights, cards, _data.Jobs, _data.Fleet, _data.Repos, new RectF32(0, 0, Width, Height), _data.Machines);

        public void Save(string name) => File.WriteAllBytes(Path.Combine(Dir(), name + ".png"), PngWriter.Encode(Renderer.Surface.Pixels, Width, Height));

        public void Dispose() => Renderer.Dispose();
    }

    [Theory]
    [InlineData(2880, 1814, 1.5f, "page-2880x1814")]
    [InlineData(1280, 800, 1f, "page-1280x800")]
    public void ThePageWithThreeOpenCardsAndTwoHistoryEntries(int width, int height, float dpi, string name)
    {
        using var w = new Window(width, height, dpi);
        w.Chrome.NotificationsPage.Arranged = nodes => w.Seen = nodes;
        w.Frame(0, CardsScreen.Empty);
        w.Chrome.Notifications.Open = true;
        w.Chrome.Notifications.PostEvent(new PilotEvent(PilotEventKind.JobEnded, "j1", "Job one finished", "took 4 min", NotificationSeverity.Success, "DE1"), null, T0.AddMinutes(1));
        w.Chrome.Notifications.PostEvent(new PilotEvent(PilotEventKind.JobEnded, "j2", "Job two failed", "1 failed, 437 passed", NotificationSeverity.Error, "DE1"), null, T0.AddMinutes(2));
        w.Chrome.Notifications.Open = false;
        w.Chrome.Show(PilotPage.Notifications);
        w.Frame(180, Three());

        w.Seen.IsEmpty.ShouldBeFalse();
        TextOverlap.Faults(w.Seen).Select(f => f.Line).ShouldBeEmpty();
        w.Save(name);
    }

    [Fact]
    public void ThePanelAt528()
    {
        using var w = new Window(1920, 1080, 1f);
        w.Chrome.Cards.Arranged = nodes => w.Seen = nodes;
        w.Chrome.Notifications.Open = true;
        w.Frame(0, Three());
        w.Frame(1, Three());

        w.Seen.IsEmpty.ShouldBeFalse();
        TextOverlap.Faults(w.Seen).Select(f => f.Line).ShouldBeEmpty();
        w.Save("panel");
    }

    [Fact]
    public void AStayingToastWhoseButtonsWrap()
    {
        using var w = new Window(1920, 1080, 1.5f);
        w.Chrome.NotificationsLayer.Arranged = nodes => w.Seen = nodes;
        w.Frame(0, CardsScreen.Empty);
        w.Frame(1, Stuck());
        w.Frame(2, Stuck());

        w.Seen.IsEmpty.ShouldBeFalse();
        TextOverlap.Faults(w.Seen).Select(f => f.Line).ShouldBeEmpty();
        w.Save("toast-wrapped-buttons");
    }
}
