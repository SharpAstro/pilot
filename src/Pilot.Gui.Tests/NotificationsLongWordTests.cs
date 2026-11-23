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
/// A card whose summary and detail are one 300-character test filter with no space stays inside the Notifications page at 2880x1814, DPI 1.5
/// (pilot #1286), by the overlap check the live TextCheck reads. The PNG goes to PILOT_JOB_ARTIFACTS when set.
/// </summary>
public sealed class NotificationsLongWordTests
{
    private static readonly DateTimeOffset T0 = PageData.Now;

    [Fact]
    public void ACardHoldingA300CharacterFilterStaysInsideThePage()
    {
        const int width = 2880, height = 1814;
        var word = "dotnet test --filter " + WrappedProseBreakTests.Filter();
        var card = new CardRow
        {
            Id = "c1286", Bin = CardBin.Ring, Kind = "check-failed", Subject = "flight/PIL1286", Title = "PIL1286: a check command is one long word",
            Summary = WrappedProseBreakTests.Filter(), Detail = WrappedProseBreakTests.Filter(), At = T0.AddMilliseconds(500), State = CardState.Open,
            Choices = [new CardChoice { Label = "Start web-header", Verb = Verbs.StartLeg, Target = "flight/PIL1286/wrap-filter-tokens" }],
        };
        word.Length.ShouldBeGreaterThan(300);

        PageTime.Zone = TimeZoneInfo.Utc;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1.5f };
        chrome.Notifications.Open = false;
        chrome.Show(PilotPage.Jobs);
        var arranged = ImmutableArray<Layout.ArrangedNode<float>>.Empty;
        chrome.NotificationsPage.Arranged = nodes => arranged = nodes;
        var data = PageData.For("sample");
        void Frame(DateTimeOffset at, CardsScreen cards) =>
            chrome.Render(data.Sessions with { AsOf = at }, data.Flights, cards, data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines);
        Frame(T0, CardsScreen.From([card]));
        chrome.Show(PilotPage.Notifications);
        Frame(T0.AddSeconds(1), CardsScreen.From([card]));
        Frame(T0.AddSeconds(2), CardsScreen.From([card]));

        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "notifications-long-word-2880x1814.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));

        arranged.IsDefaultOrEmpty.ShouldBeFalse("the page was arranged");
        var pieces = arranged.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && text.Value.Contains("FullyQualifiedName~"));
        pieces.ShouldBeGreaterThan(2, "the filter is drawn as pieces");
        TextOverlap.Faults(arranged).ShouldBeEmpty("no text of the card is drawn over other text or past the page");
    }

    [Fact]
    public void AHistoryLineHoldingA300CharacterFilterStaysInsideThePage()
    {
        const int width = 2880, height = 1814;
        PageTime.Zone = TimeZoneInfo.Utc;
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = 1.5f };
        chrome.Notifications.Open = true;
        var command = "dotnet test Pilot.Gui.Tests -c Release --filter '" + WrappedProseBreakTests.Filter() + "'";
        chrome.Notifications.PostEvent(new PilotEvent(PilotEventKind.JobEnded, "j1286", "Job j1286 finished", command, DIR.Lib.Notifications.NotificationSeverity.Success, "DE1"), null, T0.AddSeconds(-60));
        chrome.Notifications.Open = false;
        chrome.Show(PilotPage.Jobs);
        var arranged = ImmutableArray<Layout.ArrangedNode<float>>.Empty;
        chrome.NotificationsPage.Arranged = nodes => arranged = nodes;
        var data = PageData.For("sample");
        void Frame(DateTimeOffset at) =>
            chrome.Render(data.Sessions with { AsOf = at }, data.Flights, CardsScreen.From([]), data.Jobs, data.Fleet, data.Repos, new RectF32(0, 0, width, height), data.Machines);
        Frame(T0);
        chrome.Show(PilotPage.Notifications);
        Frame(T0.AddSeconds(1));
        Frame(T0.AddSeconds(2));

        var dir = Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } given ? given : Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "notifications-history-long-filter-2880x1814.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));

        arranged.IsDefaultOrEmpty.ShouldBeFalse("the page was arranged");
        var pieces = arranged.Count(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text text } && text.Value.Contains("FullyQualifiedName~"));
        pieces.ShouldBeGreaterThan(2, "the filter is drawn as pieces");
        TextOverlap.Faults(arranged).ShouldBeEmpty("no text of the history line is drawn over other text or past the page");
    }
}
