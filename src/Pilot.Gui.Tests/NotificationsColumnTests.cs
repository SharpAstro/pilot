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
/// The Notifications page lists its cards as toasts do (pilot #1292): the one card component in a left-aligned column no wider than a cap near the
/// toast's, every card's left edge on one line with one gap between cards, one Dismiss per open card, and a history card (an answered card or a
/// pilot event) drawn by the same component, dimmed and marked closed.
/// </summary>
public sealed class NotificationsColumnTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    /// <summary>A window on the Notifications page with three open cards and two history entries, CPU-rendered; the capture tests draw their own window, so the before run stays self-contained.</summary>
    private sealed class PageWindow : IDisposable
    {
        private ImmutableArray<Layout.ArrangedNode<float>> _arranged = [];

        public PageWindow(int width, int height, float dpi)
        {
            Width = width;
            Height = height;
            Dpi = dpi;
            Renderer = new RgbaImageRenderer((uint)width, (uint)height);
            Chrome = new PilotChrome<RgbaImage>(Renderer, new SessionsView<RgbaImage>(Renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Jobs);
            var seen = Chrome.NotificationsPage.Arranged;
            Chrome.NotificationsPage.Arranged = nodes =>
            {
                _arranged = nodes;
                seen?.Invoke(nodes);
            };
        }

        public int Width { get; }
        public int Height { get; }
        public float Dpi { get; }
        public RgbaImageRenderer Renderer { get; }
        public PilotChrome<RgbaImage> Chrome { get; }
        public ImmutableArray<Layout.ArrangedNode<float>> Arranged => _arranged;

        public void Frame(DateTimeOffset at, CardsScreen cards)
        {
            var data = PageData.For("sample");
            Chrome.Render(data.Sessions with { AsOf = at }, data.Flights, cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, Width, Height), data.Machines);
        }

        /// <summary>Three open cards and two history entries, with the page shown.</summary>
        public void Fill()
        {
            Frame(T0, CardsScreen.Empty);
            Chrome.Notifications.Open = true;
            Chrome.Notifications.PostEvent(new PilotEvent(PilotEventKind.JobEnded, "j1", "Job one finished", "took 4 min", NotificationSeverity.Success, "DE1"), null, T0.AddMinutes(1));
            Chrome.Notifications.PostEvent(new PilotEvent(PilotEventKind.JobEnded, "j2", "Job two failed", "1 failed, 437 passed", NotificationSeverity.Error, "DE1"), null, T0.AddMinutes(2));
            Chrome.Notifications.Open = false;
            Chrome.Show(PilotPage.Notifications);
            Frame(T0.AddMinutes(3), Cards());
        }

        public void Save(string name) => File.WriteAllBytes(Path.Combine(ArtifactDir(), name + ".png"), PngWriter.Encode(Renderer.Surface.Pixels, Width, Height));

        public void Dispose() => Renderer.Dispose();
    }

    private static string ArtifactDir()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CardsScreen Cards() => CardsScreen.From(
    [
        Card("c1", CardBin.Ring, "TIA1818's PR #63 is ready to merge"),
        Card("c2", CardBin.ForYou, "PIL795: 5 legs planned"),
        Card("c3", CardBin.ForYou, "PIL1293 is stuck: its worker stopped with the leg unfinished"),
    ]);

    private static CardRow Card(string id, CardBin bin, string title) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB" + id, Title = title, Summary = "A short line on what happened and what the choices do.", At = T0, State = CardState.Open,
        Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/TIA1818/web-header" }],
    };

    private static string TextOf(Layout.ArrangedNode<float> a) => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } ? v : "";

    /// <summary>The page's rows, top to bottom: the toast-framed nodes (the library's corner radius) as wide as most of the page (DSN-5 v1: the frame is the row).</summary>
    private static List<(Rect<float> Frame, string Head, Rect<float> Border)> Frames(PageWindow w)
    {
        var radius = Pilot.Ui.NotificationsHost.Colors().CornerRadius;
        return [.. w.Arranged.Where(a => a.Node.BorderStyle is { } border && border.Radius == radius && a.Bounds.Width > w.Width * 0.5f * w.Dpi / w.Dpi && a.Bounds.Height > 20f)
            .OrderBy(a => a.Bounds.Y).Select(a => (a.Bounds, "", a.Bounds))];
    }

    [Theory]
    [InlineData(2880, 1814, 1.5f)]
    [InlineData(1280, 800, 1f)]
    public void TheCardsFormOneAlignedColumnNoWiderThanTheCapWithOneGap(int width, int height, float dpi)
    {
        using var w = new PageWindow(width, height, dpi);
        w.Fill();

        var frames = Frames(w);
        frames.Count.ShouldBe(5, "three open cards and two history entries, all drawn");
        // DSN-5 v1 removed the 440 column cap: a row spans the page less its gutters (20 units each side).
        frames.Select(f => MathF.Round(f.Frame.X)).Distinct().Count().ShouldBe(1, "every card has the same left edge");
        frames.ShouldAllBe(f => f.Border.Width >= width - 2f * 20f * dpi - 1f, "each row fills the page less its gutters");
        frames.ShouldAllBe(f => f.Border.Width <= width, "and none runs past the page");

        var gaps = frames.Zip(frames.Skip(1), (a, b) => b.Frame.Y - (a.Frame.Y + a.Frame.Height)).ToList();
        // Between the three open cards and between the two history cards the gap is one value; a section's heading sits between the groups.
        MathF.Abs(gaps[0] - gaps[1]).ShouldBeLessThan(1.5f, "the open cards are one gap apart");
        MathF.Abs(gaps[0] - gaps[3]).ShouldBeLessThan(1.5f, "and so are the history cards");
        gaps[0].ShouldBeGreaterThan(0f);
        gaps[2].ShouldBeGreaterThan(gaps[0], "a section heading sits between the groups");
    }

    [Theory]
    [InlineData(2880, 1814, 1.5f)]
    [InlineData(1280, 800, 1f)]
    public void EachOpenCardHasExactlyOneDismissAndTheHistoryIsMarkedClosed(int width, int height, float dpi)
    {
        using var w = new PageWindow(width, height, dpi);
        w.Fill();

        // DSN-5 v1: an open row is dismissed by the toast's Close cross (an icon, no Dismiss word); a closed row is one quiet line with neither.
        w.Arranged.Count(a => TextOf(a).Trim() == "Dismiss").ShouldBe(0);
        Func<Layout.ArrangedNode<float>, bool> IsClose = a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Icon { Kind: Layout.IconKind.Close } };
        var frames = Frames(w);
        frames.Count.ShouldBe(5);
        w.Arranged.Count(IsClose).ShouldBe(3, "one Close cross per open card, none on a history row");
        frames.Skip(3).ShouldAllBe(f => !w.Arranged.Any(a => IsClose(a) && a.Bounds.Y >= f.Frame.Y && a.Bounds.Y <= f.Frame.Y + f.Frame.Height));
        TextOverlap.Faults(w.Arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void AnEventCardOffersNothingToChatAbout()
    {
        using var w = new PageWindow(1280, 800, 1f);
        w.Fill();
        w.Chrome.Cards.OnChatAbout = _ => { };
        w.Frame(T0.AddMinutes(4), Cards());

        var events = Frames(w).Skip(3).ToList();
        events.Count.ShouldBe(2);
        foreach (var e in events)
        {
            w.Arranged.Where(a => TextOf(a).Contains("Chat about", StringComparison.Ordinal)
                    && a.Bounds.Y >= e.Frame.Y && a.Bounds.Y <= e.Frame.Y + e.Frame.Height).ShouldBeEmpty("an event is not a card");
        }
    }

    [Fact]
    public void InNightAHistoryCardIsNotBrighterThanAnOpenOneAndStillDiffers()
    {
        using var w = new PageWindow(1280, 800, 1f);
        w.Chrome.Night.On = true;
        w.Fill();

        RGBAColor32 InkOf(string word) => ((Layout.Content.Text)((Layout.Node.Leaf)w.Arranged.First(a => TextOf(a).StartsWith(word, StringComparison.Ordinal)).Node).Content).Color;
        // Night's red on black has no ink that reaches AA on the toned fill, so a closed card keeps Dim rather than Bright.
        foreach (var (closed, open, what) in new[] { ("Job", "TIA1818's", "title"), ("437", "short", "summary") })
        {
            Contrast.Luminance(InkOf(closed)).ShouldBeLessThanOrEqualTo(Contrast.Luminance(InkOf(open)), $"a history card's {what} is not brighter than an open card's in Night");
            InkOf(closed).ShouldNotBe(InkOf(open), $"a history card's {what} is still marked apart from an open card's (Night keeps Dim, not Bright)");
        }
    }

    [Fact]
    public void AHistoryCardIsDimmedAndAnEventKeepsItsSeverity()
    {
        using var w = new PageWindow(1280, 800, 1f);
        w.Fill();

        RGBAColor32 InkOf(string word) => ((Layout.Content.Text)((Layout.Node.Leaf)w.Arranged.First(a => TextOf(a).StartsWith(word, StringComparison.Ordinal)).Node).Content).Color;
        // The frame of the card holding this word: the smallest node with a border around it.
        Layout.Node FrameOf(string word)
        {
            var leaf = w.Arranged.First(a => TextOf(a).StartsWith(word, StringComparison.Ordinal)).Bounds;
            return w.Arranged.Where(a => a.Node.BorderStyle is not null && a.Bounds.X <= leaf.X && a.Bounds.Y <= leaf.Y
                    && a.Bounds.X + a.Bounds.Width >= leaf.X + leaf.Width && a.Bounds.Y + a.Bounds.Height >= leaf.Y + leaf.Height)
                .OrderBy(a => a.Bounds.Width * a.Bounds.Height).First().Node;
        }

        // An open card's title ("TIA1818's PR ...") against a history card's ("Job two failed"), and the summaries likewise.
        InkOf("Job").ShouldNotBe(InkOf("TIA1818's"), "a history card's title is dimmed");
        InkOf("437").ShouldNotBe(InkOf("short"), "a history card's summary is dimmed");
        FrameOf("Job").BorderStyle.ShouldNotBe(FrameOf("TIA1818's").BorderStyle, "a history card's edge is toned");
        // Two events of different severity (a success and an error) keep their hues, toned down.
        var (ok, failed) = (FrameOf("took"), FrameOf("437"));
        ok.Background.ShouldNotBe(failed.Background, "an event's fill keeps its severity");
        ok.BorderStyle.ShouldNotBe(failed.BorderStyle, "and so does its edge");

        // Every dimmed word reads at AA on the toned fill it is drawn on (the success tone read 4.49:1 with the plain Dim).
        foreach (var (word, frame) in new[] { ("Job", FrameOf("Job")), ("437", FrameOf("437")), ("took", ok) })
        {
            Contrast.Ratio(InkOf(word), frame.Background!.Value).ShouldBeGreaterThanOrEqualTo(Contrast.AA, $"'{word}' on its history card's fill");
        }
    }
}
