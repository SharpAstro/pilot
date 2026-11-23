using System.Collections.Immutable;
using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// A 300-character test filter with no space stays inside the Notifications page at 2880x1814, DPI 1.5 (pilot #1286), by the overlap check
/// the live TextCheck reads: an open card whose summary is the filter and whose detail is the check command holding it, and a history line
/// (a job-ended event, drawn by DIR.Lib's NotificationsLayout.Wrapped) whose text is that command. Each PNG goes to PILOT_JOB_ARTIFACTS when set.
/// </summary>
public sealed class NotificationsLongWordTests
{
    private const int Width = 2880, Height = 1814;

    private static readonly DateTimeOffset T0 = PageData.Now;

    // The filter's terms, each drawn as a piece of its own ending in "FullyQualifiedName~" (WrappedProse.Break cuts after every ~).
    private static readonly int Terms = WrappedProseBreakTests.Filter().Split('|').Length;

    [Fact]
    public void ACardHoldingA300CharacterFilterStaysInsideThePage()
    {
        WrappedProseBreakTests.Filter().Length.ShouldBeGreaterThan(300);
        // A check-failed card says what failed in its summary and carries the command in its detail. The page draws a detail only where it
        // differs from the summary (CardsView.Card), so the two differ here and both reach the page: the detail whole and uncapped (FullCard).
        var card = new CardRow
        {
            Id = "c1286", Bin = CardBin.Ring, Kind = "check-failed", Subject = "flight/PIL1286", Title = "PIL1286: a check command is one long word",
            Summary = WrappedProseBreakTests.Filter(), Detail = PageData.LongFilterCommand, At = T0.AddMilliseconds(500), State = CardState.Open,
            Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/PIL1286/wrap-filter-tokens" }],
        };

        var arranged = Capture("notifications-long-word-2880x1814.png", [card], []);

        var texts = Texts(arranged);
        texts.ShouldContain("--filter ", "the card's detail, the command, is drawn");
        texts.Count(t => t.Contains("FullyQualifiedName~")).ShouldBeGreaterThanOrEqualTo(2 * Terms, "the summary and the detail each draw every term of the filter");
        TextOverlap.Faults(arranged).ShouldBeEmpty("no text of the card is drawn over other text or past the page");
    }

    [Fact]
    public void AHistoryLineHoldingA300CharacterFilterStaysInsideThePage()
    {
        var ended = new PilotEvent(PilotEventKind.JobEnded, "j1286", "Job j1286 finished", PageData.LongFilterCommand, DIR.Lib.Notifications.NotificationSeverity.Success, "DE1");

        var arranged = Capture("notifications-history-long-filter-2880x1814.png", [], [ended]);

        Texts(arranged).Count(t => t.Contains("FullyQualifiedName~")).ShouldBeGreaterThan(2, "the filter is drawn as pieces");
        TextOverlap.Faults(arranged).ShouldBeEmpty("no text of the history line is drawn over other text or past the page");
    }

    // The Notifications page with these open cards and these events in its history (posted with the bar open, as PageData.PostEvents does),
    // a frame on Jobs first as the window has it: the page's arranged nodes. The PNG goes to PILOT_JOB_ARTIFACTS when set.
    private static ImmutableArray<Layout.ArrangedNode<float>> Capture(string file, CardRow[] cards, PilotEvent[] events)
    {
        PageTime.Zone = TimeZoneInfo.Utc;
        using var renderer = new RgbaImageRenderer((uint)Width, (uint)Height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1.5f };
        chrome.Notifications.Open = true;
        for (var i = 0; i < events.Length; i++)
        {
            chrome.Notifications.PostEvent(events[i], null, T0.AddMinutes(i - events.Length));
        }
        chrome.Notifications.Open = false;
        chrome.Show(PilotPage.Jobs);
        var arranged = ImmutableArray<Layout.ArrangedNode<float>>.Empty;
        chrome.NotificationsPage.Arranged = nodes => arranged = nodes;
        var data = PageData.For("sample");
        var screen = CardsScreen.From(cards);
        void Frame(DateTimeOffset at) =>
            chrome.Render(data.Sessions with { AsOf = at }, data.Flights, screen, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, Width, Height), data.Machines);
        Frame(T0);
        chrome.Show(PilotPage.Notifications);
        Frame(T0.AddSeconds(1));
        Frame(T0.AddSeconds(2));

        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), PngWriter.Encode(renderer.Surface.Pixels, Width, Height));

        arranged.IsDefaultOrEmpty.ShouldBeFalse("the page was arranged");
        return arranged;
    }

    private static string[] Texts(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } ? text.Value : null).OfType<string>()];
}
