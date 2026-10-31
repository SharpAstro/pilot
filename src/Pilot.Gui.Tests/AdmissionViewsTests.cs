using System.Diagnostics;
using DIR.Lib;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The queue and the Opus count where the person looks (PIL437): <c>pilot status</c> says <c>Opus n/cap</c> and each queued flight with its
/// why, the Flights row and the flight's page carry the queued reason and a priority mark, and Home says the count and links the queue.
/// Rendered at a narrow and a wide size with long content (captures), with no text over text, the colours held to the contrast floor on the
/// palette, and the time of a frame measured with many flights queued.
/// </summary>
public sealed class AdmissionViewsTests
{
    private const string LongWhy = "waiting for an Opus slot: 3 of 3 flying: pil437 admission-views worker, pil461 chat-say-close worker, pil501 fix-waits reviewer on LE1; peers not counted: SE1 did not answer within 3 s";

    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightRow Flight(int i, string? queued = null, bool priority = false) => new()
    {
        Code = $"PIL{900 + i}", Repo = "pilot", RepoPath = "/r/pilot", Issue = 900 + i,
        Title = $"Flight {i}: admission decides whether a worker may start, and says why it waits when it may not",
        Branch = $"flight/pil{900 + i}", Worktree = $"/w/pil{900 + i}", Base = "origin/main", State = FlightState.Flying,
        CreatedAt = Live.At.AddMinutes(i), Queued = queued, GitHubLabel = priority ? "pilot: priority" : null,
    };

    private static LegRow Leg(string flight, string? queued = null, LegState state = LegState.Planned) => new()
    {
        Flight = flight, Name = "admission-views", Order = 1, Goal = "g", DoneWhen = "d", State = state, Queued = queued,
    };

    private static AdmissionRow Admission(string? peers = null, params string[] priority) => new()
    {
        OpusCount = 3, OpusCap = 3, OneFlightPerRepo = true, Counted = ["pil437 worker", "pil461 worker", "pil501 reviewer on LE1"],
        Priority = priority, PeersNotCounted = peers,
    };

    private static SnapshotEvent Snapshot(FlightRow[] flights, LegRow[] legs, AdmissionRow? admission) => new()
    {
        Seq = 1, At = Now, Stream = "s", Sessions = [], Notes = [], Flights = flights, Legs = legs, Admission = admission,
    };

    /// <summary>A node with <paramref name="queued"/> flights waiting on top of two flying, the first of them a priority one.</summary>
    private static (FlightRow[] Flights, LegRow[] Legs, AdmissionRow Admission) Queue(int queued, bool longWhy = true, bool newestPriority = false)
    {
        var flights = new List<FlightRow> { Flight(0, priority: true), Flight(1) };
        var legs = new List<LegRow>();
        for (var i = 0; i < queued; i++)
        {
            var flight = Flight(i + 2, priority: i == 0 || newestPriority && i == queued - 1);
            flights.Add(flight);
            legs.Add(Leg(flight.Code, longWhy ? LongWhy : "waiting for an Opus slot: 3 of 3 flying"));
        }
        return ([.. flights], [.. legs], Admission(longWhy ? "peers not counted: LE1 unreachable" : null, newestPriority ? ["PIL900", "PIL902", $"PIL{900 + queued + 1}"] : ["PIL900", "PIL902"]));
    }

    [Fact]
    public void StatusPrintsTheOpusLineAndEachQueuedFlightWithItsWhy()
    {
        var (flights, legs, admission) = Queue(2);
        var text = PilotCommand.Status(Snapshot(flights, legs, admission));

        text.ShouldContain("Opus 3/3, peers not counted: pil437 worker, pil461 worker, pil501 reviewer on LE1; one flight per repo");
        text.ShouldContain($"queued  PIL902 [priority]  {flights[2].Title}: {LongWhy}");
        text.ShouldContain($"queued  PIL903  {flights[3].Title}: {LongWhy}");
        text.IndexOf("[priority]", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("PIL903  ", StringComparison.Ordinal), "the priority flight is first in the queue");
    }

    [Fact]
    public void StatusSaysNothingOfTheOpusCountFromANodeThatSaidNone()
    {
        var text = PilotCommand.Status(Snapshot([Flight(0)], [], null));

        text.ShouldNotContain("Opus");
        text.ShouldNotContain("queued  ");
    }

    [Fact]
    public void StatusWithNoCapSaysSoAndWithPeersReadSaysNothingOfThem()
    {
        var text = PilotCommand.Status(Snapshot([Flight(0)], [], new AdmissionRow { OpusCount = 1, OpusCap = 0 }));

        text.ShouldContain("Opus 1 (no cap)");
        text.ShouldNotContain("peers not counted");
    }

    [Fact]
    public void TheMarkdownStatusSaysTheCountAndTheQueueToo()
    {
        var (flights, legs, admission) = Queue(1);
        var text = StatusMarkdown.Build(Snapshot(flights, legs, admission));

        text.ShouldContain("**Opus 3/3, peers not counted**");
        text.ShouldContain("**queued** PIL902 (priority)");
        text.ShouldContain("waiting for an Opus slot");
    }

    [Fact]
    public void ALegsWaitIsTheFlightsQueuedReasonAndItsOwnWaitComesFirst()
    {
        var flight = Flight(0);
        var legs = new[] { Leg(flight.Code, "second", LegState.Planned) with { Order = 2 }, Leg(flight.Code, "first") };
        QueuedFlight.Of([flight], legs, null).Single().Why.ShouldBe("first");
        QueuedFlight.Of([flight with { Queued = "its own" }], legs, null).Single().Why.ShouldBe("its own");
        QueuedFlight.Of([flight], [Leg(flight.Code, "flying now", LegState.Flying)], null).ShouldBeEmpty();
        QueuedFlight.Of([flight with { State = FlightState.Landed, Queued = "x" }], [], null).ShouldBeEmpty();
    }

    [Fact]
    public void TheFlightsRowAndPageSayWhyItWaitsAndMarkAPriorityFlight()
    {
        var (flights, legs, admission) = Queue(2);
        var lines = FlightLine.Build(flights, [], Now, legs, admission);

        var waiting = lines.Single(l => l.Code == "PIL903");
        waiting.Queued.ShouldBe(LongWhy);
        waiting.StateDetail.ShouldBe("queued: " + LongWhy);
        waiting.PriorityMark.ShouldBeNull();
        waiting.ToDumpLine().ShouldContain("queued: waiting for an Opus slot");

        var priority = lines.Single(l => l.Code == "PIL902");
        priority.PriorityMark.ShouldBe(FlightMarks.PriorityMark);
        // The mark comes from the node's set and from the PR's own label, so either says it.
        lines.Single(l => l.Code == "PIL900").PriorityMark.ShouldBe(FlightMarks.PriorityMark);
        lines.Single(l => l.Code == "PIL901").PriorityMark.ShouldBeNull();
        FlightMarks.IsPriority(Flight(7), Admission(null, "PIL907")).ShouldBeTrue();
    }

    [Fact]
    public void HomeSaysTheCountAndListsTheQueueWithPriorityFirst()
    {
        var (flights, legs, admission) = Queue(3);
        var screen = Screens(flights, legs, admission);
        var home = new HomeScreen(screen, new SessionsScreen([], "t", Now), new JobsScreen([], "t", Now), CardsScreen.Empty);

        home.OpusLine.ShouldBe("Opus 3/3, peers not counted");
        home.QueueLines.Select(q => q.Line.Code).ShouldBe(["PIL902", "PIL903", "PIL904"]);
        home.QueueLines[0].Line.PriorityMark.ShouldNotBeNull();
        new HomeScreen(Screens([Flight(0)], [], null), new SessionsScreen([], "t", Now), new JobsScreen([], "t", Now), CardsScreen.Empty).OpusLine.ShouldBeNull();
    }

    private static FlightsScreen Screens(FlightRow[] flights, LegRow[] legs, AdmissionRow? admission) =>
        new(FlightLine.Build(flights, [], Now, legs, admission), "test", Now) { Admission = admission };

    [Fact]
    public void TheMarksAndTheQueuedWordsKeepTheContrastFloorOnEveryBackgroundTheyAreDrawnOn()
    {
        foreach (var (paletteName, palette) in new[] { ("Standard", Palette.Standard), ("Night", Palette.Night) })
        {
            // Every colour the new text is drawn in: the priority mark on a row, card, page and Home (Held); the queued reason on a row
            // and Home (Body); the queued state word on a row, card and page (Held, as StateColor gives a queued flight).
            // Well is the Home page ground, where the queue is Body (the mark rides inside its text); Held is drawn on the rows, cards and page.
            foreach (var (name, fg) in new[] { ("Held", palette.Held), ("Body", palette.Body) })
            {
                foreach (var (bgName, bg) in new[] { ("Well", palette.Well), ("Ground", palette.Ground), ("PanelBg", palette.PanelBg), ("RowHoverBg", palette.RowHoverBg), ("RowFocusBg", palette.RowFocusBg), ("RowAltBg", palette.RowAltBg) })
                {
                    if (name == "Held" && bgName == "Well")
                    {
                        continue;
                    }
                    Contrast(fg, bg).ShouldBeGreaterThanOrEqualTo(4.5, $"{paletteName}: {name} on {bgName}");
                }
            }
        }
    }

    // WCAG 2.x relative luminance and contrast ratio.
    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("flights table")]
    [InlineData("flights canvas")]
    [InlineData("flight page")]
    public async Task TheQueueIsInThePageDataTheOverlapCheckRunsOverAtNarrowAndWideSizes(string page)
    {
        var data = PageData.For("long");
        data.Flights.Admission.ShouldNotBeNull();
        data.Flights.Lines.ShouldContain(l => l.Queued != null && l.Queued.Length > 100);
        data.Flights.Lines.ShouldContain(l => l.PriorityMark != null);
        foreach (var width in new[] { 700, 1900 })
        {
            var faults = await PageTextOverlapTests.FaultsOf(page, "long", width, 1f);
            faults.Select(f => f.Line).ShouldBeEmpty($"{page} at {width}");
        }
    }

    [Fact]
    public void EachChangedPageIsCapturedAtANarrowAndAWideSizeWithLongContent()
    {
        // The newest flight is a priority one too, so a mark is on the first row and card of the list and canvas captures.
        var (flights, legs, admission) = Queue(8, newestPriority: true);
        var screen = Screens(flights, legs, admission);
        // Where the captures go (docs/captures, for the PR) when a person asks for them; else a folder that goes with the test.
        var given = Environment.GetEnvironmentVariable("PILOT_ADMISSION_CAPTURES");
        var folder = given is { Length: > 0 } ? Directory.CreateDirectory(given) : Directory.CreateTempSubdirectory("pilot-admission-");
        try
        {
            foreach (var (size, width, height) in new[] { ("narrow", 760, 640), ("wide", 1600, 900) })
            {
                foreach (var (label, page, mode, open) in new (string, PilotPage, FlightsViewMode, string?)[]
                {
                    ("home", PilotPage.Home, FlightsViewMode.Canvas, null),
                    ("flights-list", PilotPage.Flights, FlightsViewMode.List, null),
                    ("flights-canvas", PilotPage.Flights, FlightsViewMode.Canvas, null),
                    ("flight-page", PilotPage.Flights, FlightsViewMode.Canvas, "PIL902"),
                })
                {
                    using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                    var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                    chrome.Play.ShowEverything();
                    chrome.Flights.CanCommand = true;
                    chrome.Flights.Mode = mode;
                    chrome.Show(page);
                    if (open is not null)
                    {
                        chrome.Flights.OpenDetail(open);
                    }
                    chrome.Render(new SessionsScreen([], "test", Now), screen, CardsScreen.Empty, new JobsScreen([], "test", Now), null, null, new RectF32(0, 0, width, height));
                    var png = Path.Combine(folder.FullName, $"admission-{label}-{size}.png");
                    File.WriteAllBytes(png, PngWriter.Encode(renderer.Surface.Pixels, width, height));
                    File.ReadAllBytes(png).Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
                }
            }
        }
        finally
        {
            if (given is not { Length: > 0 })
            {
                folder.Delete(recursive: true);
            }
        }
    }

    /// <summary>The longest a frame of a page with many flights queued takes to lay out and draw (median of the frames), in milliseconds.</summary>
    private const double FrameBudgetMs = 250;

    [Theory]
    [InlineData("home")]
    [InlineData("list")]
    [InlineData("canvas")]
    public void AFrameWithFiftyFlightsQueuedIsWithinTheBudgetAndNoSlowerThanOneWithNoneQueued(string which)
    {
        var (page, mode) = which switch { "home" => (PilotPage.Home, FlightsViewMode.Canvas), "list" => (PilotPage.Flights, FlightsViewMode.List), _ => (PilotPage.Flights, FlightsViewMode.Canvas) };
        var (flights, legs, admission) = Queue(60);
        var queued = Screens(flights, legs, admission);
        var none = Screens([.. flights.Select(f => f with { Queued = null })], [], null);

        var withQueue = MedianFrameMs(page, mode, queued);
        var without = MedianFrameMs(page, mode, none);
        TestContext.Current.SendDiagnosticMessage($"{which}: 60 queued {withQueue:F1} ms, none queued {without:F1} ms");

        withQueue.ShouldBeLessThan(FrameBudgetMs);
        // The queue costs a row of text and a mark a flight, not a new order of work: well inside twice the page without it.
        withQueue.ShouldBeLessThan(without * 2 + 30);
    }

    private static double MedianFrameMs(PilotPage page, FlightsViewMode mode, FlightsScreen screen)
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        chrome.Flights.Mode = mode;
        chrome.Show(page);
        void Frame() => chrome.Render(new SessionsScreen([], "test", Now), screen, CardsScreen.Empty, new JobsScreen([], "test", Now), null, null, new RectF32(0, 0, 1600, 900));
        // The first frames load the font and fill the caches; the page's steady state is what a person waits on.
        Frame();
        Frame();
        var times = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var watch = Stopwatch.StartNew();
            Frame();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }
}
