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

    /// <summary>The card content, top to bottom: the smallest node taller than twice the kind line holding it (a card's padded stack inside its frame; the left edge and the gap are the frame's, offset by one constant).</summary>
    private static List<(Rect<float> Frame, string Head, Rect<float> Border)> Frames(PageWindow w)
    {
        var heads = w.Arranged.Where(a => TextOf(a) is var t && (t.StartsWith("RING", StringComparison.Ordinal) || t.StartsWith("for you", StringComparison.Ordinal) || t.StartsWith("event", StringComparison.Ordinal)))
            .OrderBy(a => a.Bounds.Y).ToList();
        var frames = new List<(Rect<float>, string, Rect<float>)>();
        foreach (var head in heads)
        {
            var frame = w.Arranged.Where(a => a.Bounds.X <= head.Bounds.X && a.Bounds.Y <= head.Bounds.Y
                    && a.Bounds.X + a.Bounds.Width >= head.Bounds.X + head.Bounds.Width && a.Bounds.Y + a.Bounds.Height >= head.Bounds.Y + head.Bounds.Height
                    && a.Bounds.Height > head.Bounds.Height * 2f && a.Bounds.Width < w.Width * 0.9f)
                .OrderBy(a => a.Bounds.Width * a.Bounds.Height).First();
            // The card's drawn frame, the smallest bordered node around the head: what the cap is held against (the content inside it is narrower by the padding).
            var border = w.Arranged.Where(a => a.Node.BorderStyle is not null && a.Bounds.X <= head.Bounds.X && a.Bounds.Y <= head.Bounds.Y
                    && a.Bounds.X + a.Bounds.Width >= head.Bounds.X + head.Bounds.Width && a.Bounds.Y + a.Bounds.Height >= head.Bounds.Y + head.Bounds.Height)
                .OrderBy(a => a.Bounds.Width * a.Bounds.Height).First();
            frames.Add((frame.Bounds, TextOf(head), border.Bounds));
        }
        return frames;
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
        var cap = NotificationsView<RgbaImage>.ColumnWidth * dpi;
        frames.Select(f => MathF.Round(f.Frame.X)).Distinct().Count().ShouldBe(1, "every card has the same left edge");
        frames.ShouldAllBe(f => f.Border.Width <= cap + 0.5f, "no card, border included, is wider than the cap");
        frames.ShouldAllBe(f => f.Frame.Width > 200f * dpi, "and none is squeezed");

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

        w.Arranged.Count(a => TextOf(a).Trim() == "Dismiss").ShouldBe(3, "one Dismiss per open card, none from the library's row and none on a history card");
        var frames = Frames(w);
        frames.Count(f => f.Head.Contains("closed", StringComparison.Ordinal)).ShouldBe(2, "the history cards say closed in their kind line");
        frames.Take(3).ShouldAllBe(f => !f.Head.Contains("closed", StringComparison.Ordinal));
        frames.Skip(3).ShouldAllBe(f => f.Head.StartsWith("event", StringComparison.Ordinal), "a pilot event is drawn by the same component");
        TextOverlap.Faults(w.Arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Fact]
    public void AnEventCardOffersNothingToChatAbout()
    {
        using var w = new PageWindow(1280, 800, 1f);
        w.Fill();
        w.Chrome.Cards.OnChatAbout = _ => { };
        w.Frame(T0.AddMinutes(4), Cards());

        var events = Frames(w).Where(f => f.Head.StartsWith("event", StringComparison.Ordinal)).ToList();
        events.Count.ShouldBe(2);
        foreach (var e in events)
        {
            w.Arranged.Where(a => TextOf(a).Contains("Chat about", StringComparison.Ordinal)
                    && a.Bounds.Y >= e.Frame.Y && a.Bounds.Y <= e.Frame.Y + e.Frame.Height).ShouldBeEmpty("an event is not a card");
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
        // An event's head ink is held too: its kind line reads at AA on its fill, and the two severities do not share one ink.
        var (okHead, failedHead) = (InkOf("event"), w.Arranged.Where(a => TextOf(a).StartsWith("event", StringComparison.Ordinal)).Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Color).Last());
        Contrast.Ratio(okHead, ok.Background!.Value).ShouldBeGreaterThanOrEqualTo(Contrast.AA, "the first event's head on its fill");
        Contrast.Ratio(failedHead, failed.Background!.Value).ShouldBeGreaterThanOrEqualTo(Contrast.AA, "the second event's head on its fill");
    }
}
