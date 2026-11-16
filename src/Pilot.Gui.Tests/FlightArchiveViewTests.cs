using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The GUI and the TUI follow the flight archive (PIL1083): In play counts only the landed flights the snapshot carries; Everything names the
/// archived ones once, quietly (<c>520 archived</c>), as a click to the archived list read through the archive read verb; a landed flight has an
/// Archive button, an archived one Bring back, and Everything offers Archive landed, each flight sent to its own node.
/// </summary>
public sealed class FlightArchiveViewTests
{
    private static readonly NodeMark Here = new("node.sock", "node.sock", "DE1", true);
    private static readonly NodeMark Other = new("10.0.0.9:47731", "10.0.0.9:47731", "SE1", false);

    private static FlightRow Flight(string code, FlightState state, int minutes = 0) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = $"Title of {code}", Branch = "flight/" + code.ToLowerInvariant(),
        Worktree = "/r/tianwen/.claude/worktrees/" + code.ToLowerInvariant(), Base = "origin/main", State = state, CreatedAt = Live.At.AddMinutes(-minutes),
    };

    // Two flying and two landed here, one landed on SE1; 500 archived on DE1 and 20 on SE1, and the snapshot carries none of them.
    private static FlightsScreen Screen() => new(
        FlightLine.Build([Flight("FLY1", FlightState.Flying, 1), Flight("FLY2", FlightState.Flying, 2), Flight("LAND1", FlightState.Landed, 4), Flight("LAND2", FlightState.Landed, 5)], [], Live.At)
            .Append(FlightLine.Build([Flight("LAND9", FlightState.Landed, 6)], [], Live.At)[0] with { Node = Other }).ToList(),
        "test", Live.At)
    {
        Archived = [new ArchivedNode(Here, 500), new ArchivedNode(Other, 20)],
    };

    private static ArchivedFlightsPage Page(int from, int count, int total) => new()
    {
        Total = total,
        Rows = [.. Enumerable.Range(from, count).Select(i => new ArchivedFlightRow
        {
            Code = $"OLD{i}", Repo = "tianwen", Title = $"Old flight {i}", Issue = i, PullRequest = 1000 + i, LandedAt = Live.At.AddDays(-30 - i), ArchivedAt = Live.At.AddDays(-20),
        })],
    };

    private static RgbaImageRenderer NewRenderer(uint width = 1600, uint height = 900) => new(width, height);

    private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(Layout.Node tree) =>
        Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 200, 50), CellMeasureContext.PixelAuthored);

    private static string Describe(Layout.Node tree) => CellLayout.Describe(Arrange(tree));

    private static void Click(Layout.Node tree, string action)
    {
        var arranged = Arrange(tree);
        var target = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);
        CellLayout.HitTest(arranged, target.Bounds.X + 1, target.Bounds.Y);
    }

    private static FlightsView<RgbaImage> View(RgbaImageRenderer renderer, PlayFilter play, List<FlightAction>? sent = null) => new(renderer)
    {
        FontPath = SessionsCapture.FontPath, Mode = FlightsViewMode.List, Play = play, CanCommand = true,
        OnAction = Taken.By<FlightAction>(a => sent?.Add(a)),
    };

    [Fact]
    public void InPlayCountsOnlyTheLandedFlightsTheSnapshotCarriesAndNeverTheArchivedOnes()
    {
        var screen = Screen().InPlay();

        screen.Hidden.ShouldBe("3 landed hidden");
        screen.Summary.ShouldEndWith("· 3 landed hidden");

        using var renderer = NewRenderer();
        var text = Describe(View(renderer, new PlayFilter()).Tree(Screen()));
        text.ShouldContain("3 landed hidden");
        text.ShouldNotContain("archived");
    }

    [Fact]
    public void EverythingNamesTheArchivedFlightsOnceSummedOverTheNodes()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        play.ShowEverything();
        var text = Describe(View(renderer, play).Tree(Screen()));

        text.ShouldContain("520 archived");
        text.Split("520 archived").Length.ShouldBe(2);
        Screen().ArchivedCount.ShouldBe(520);
    }

    [Fact]
    public void TheQuietCountIsReadableOnTheHeaderItIsDrawnOn()
    {
        // Standard needs the usual 4.5; Night is red on near black by design, where its brightest red is the header's own text colour.
        Contrast.Ratio(FlightsView<RgbaImage>.QuietColor(Palette.Standard), Palette.Standard.HeaderBg).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast.Ratio(FlightsView<RgbaImage>.QuietColor(Palette.Night), Palette.Night.HeaderBg).ShouldBeGreaterThanOrEqualTo(3.0);
    }

    [Fact]
    public async Task ClickingTheCountOpensTheArchivedListReadFromTheArchiveNodeByNode()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        play.ShowEverything();
        // Each node's read runs on its own task, so the record is thread-safe.
        var asked = new System.Collections.Concurrent.ConcurrentBag<(string? Node, int Offset, int Count)>();
        var view = View(renderer, play);
        var changes = 0;
        var loaded = new TaskCompletionSource();
        view.Changed = () =>
        {
            if (Interlocked.Increment(ref changes) >= 3)
            {
                loaded.TrySetResult();
            }
        };
        view.OnReadArchived = (node, offset, count) =>
        {
            asked.Add((node?.Name, offset, count));
            return Task.FromResult<(ArchivedFlightsPage?, string?)>((Page(offset, 3, node == Here ? 500 : 20), null));
        };

        Click(view.Tree(Screen()), FlightsView<RgbaImage>.ArchivedHit);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        view.IsArchivedOpen.ShouldBeTrue();
        asked.Select(a => a.Node).ShouldBe(["DE1", "SE1"], ignoreOrder: true);
        asked.ShouldAllBe(a => a.Offset == 0 && a.Count == FlightsView<RgbaImage>.ArchivedPageSize);
        var text = Describe(view.Tree(Screen()));
        text.ShouldContain("OLD0");
        text.ShouldContain("Old flight 2");
        text.ShouldContain("500 archived");
        text.ShouldContain("Bring back");
        text.ShouldContain("Older →");
    }

    [Fact]
    public async Task ADownLanNodeTimeoutEndsTheReadingStateAsARefusal()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        play.ShowEverything();
        var view = View(renderer, play);
        var changes = 0;
        var loaded = new TaskCompletionSource();
        view.Changed = () =>
        {
            if (Interlocked.Increment(ref changes) >= 3)
            {
                loaded.TrySetResult();
            }
        };
        view.OnReadArchived = (_, _, _) => throw new TaskCanceledException("timed out", new TimeoutException());

        Click(view.Tree(Screen()), FlightsView<RgbaImage>.ArchivedHit);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var text = Describe(view.Tree(Screen()));
        text.ShouldContain("timed out");
        text.ShouldNotContain("reading");
    }

    [Fact]
    public async Task ArchiveOnALandedFlightAndBringBackOnAnArchivedOneSendArchiveFlightToTheFlightsOwnNode()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        play.ShowEverything();
        var sent = new List<FlightAction>();
        var view = View(renderer, play, sent);
        var screen = Screen();

        var landed = screen.Lines.First(l => l.Code == "LAND9");
        var archive = FlightAction.For(landed).ShouldHaveSingleItem();
        archive.Verb.ShouldBe(Verbs.ArchiveFlight);
        archive.Target.ShouldBe(Verbs.FlightTarget("LAND9"));
        archive.Node.ShouldBe(Other);
        FlightAction.For(screen.Lines.First(l => l.Code == "FLY1")).ShouldNotContain(a => a.Verb == Verbs.ArchiveFlight);

        view.OpenDetail("LAND1");
        Describe(view.Tree(screen)).ShouldContain("Archive");
        await view.Run(FlightAction.For(screen.Lines.First(l => l.Code == "LAND1")).Single(), "LAND1", "flight:LAND1:archive-flight");
        sent.Single().Verb.ShouldBe(Verbs.ArchiveFlight);
        sent.Single().Args.ShouldBeEmpty();

        sent.Clear();
        await view.Run(FlightAction.BringBack("OLD3", Other), "OLD3", "flight:OLD3:bring-back");
        sent.Single().Verb.ShouldBe(Verbs.ArchiveFlight);
        sent.Single().Target.ShouldBe(Verbs.FlightTarget("OLD3"));
        sent.Single().Args.ShouldBe(["undo"]);
        sent.Single().Node.ShouldBe(Other);
    }

    [Fact]
    public async Task ArchiveLandedOnEverythingSendsEachShownLandedFlightToItsOwnNodeAndSaysARefusal()
    {
        using var renderer = NewRenderer();
        var play = new PlayFilter();
        var sent = new List<FlightAction>();
        var view = View(renderer, play, sent);
        // In play shows no landed row, so it offers nothing to archive.
        Describe(view.Tree(Screen())).ShouldNotContain("Archive landed");

        play.ShowEverything();
        view.OnAction = action =>
        {
            sent.Add(action);
            return Task.FromResult<string?>(action.Target.EndsWith("LAND2", StringComparison.Ordinal) ? "LAND2 has an open card" : null);
        };
        var tree = view.Tree(Screen());
        Describe(tree).ShouldContain("Archive landed (3)");

        await view.ArchiveLanded(view.ArchivableLanded(Screen()));

        sent.Select(a => (a.Target, a.Node?.Name)).ShouldBe(
            [(Verbs.FlightTarget("LAND1"), null), (Verbs.FlightTarget("LAND2"), null), (Verbs.FlightTarget("LAND9"), "SE1")], ignoreOrder: true);
        sent.ShouldAllBe(a => a.Verb == Verbs.ArchiveFlight);
        view.Said.ShouldNotBeNull().ShouldContain("LAND2 has an open card");
    }

    [Fact]
    public async Task ArchiveCapturesAtANarrowAndAWideSizeShowNoTextOverlap()
    {
        var given = Environment.GetEnvironmentVariable("PILOT_ARCHIVE_CAPTURES");
        var phase = Environment.GetEnvironmentVariable("PILOT_ARCHIVE_PHASE") is { Length: > 0 } p ? p : "after";
        var folder = given is { Length: > 0 } ? Directory.CreateDirectory(given) : Directory.CreateTempSubdirectory("pilot-archive-");
        try
        {
            foreach (var (name, width, height) in new[] { ("narrow", 760, 640), ("wide", 1600, 900) })
            {
                foreach (var (label, everything, archived) in new[] { ("in-play", false, false), ("everything", true, false), ("archived-list", true, true) })
                {
                    using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                    var view = View(renderer, new PlayFilter());
                    if (everything)
                    {
                        view.Play!.ShowEverything();
                    }
                    var screen = Screen();
                    view.OnReadArchived = (node, offset, count) => Task.FromResult<(ArchivedFlightsPage?, string?)>((Page(offset, 6, node == Here ? 500 : 20), null));
                    view.Render(screen, new RectF32(0, 0, width, height));
                    if (archived)
                    {
                        // The list's first reads: opened, then each node's page in; a redraw is asked at each step.
                        var changes = 0;
                        var loaded = new TaskCompletionSource();
                        view.Changed = () =>
                        {
                            if (Interlocked.Increment(ref changes) >= 3)
                            {
                                loaded.TrySetResult();
                            }
                        };
                        view.OpenArchived();
                        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                        view.Render(screen, new RectF32(0, 0, width, height));
                    }

                    var png = Path.Combine(folder.FullName, $"{phase}-{label}-{name}.png");
                    File.WriteAllBytes(png, PngWriter.Encode(renderer.Surface.Pixels, width, height));
                    File.ReadAllBytes(png).Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });

                    var faults = TextOverlap.Faults(Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)));
                    faults.Select(f => f.Line).ShouldBeEmpty($"{label} at {name}");
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
}
