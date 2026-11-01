using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>A flight's own page lists its decisions, oldest first: when, kind, the choice among the options, who and as what, and why.</summary>
public sealed class FlightDetailDecisionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 41, 9, TimeSpan.Zero);

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Now.AddDays(-1),
    };

    private static FlightLine Line() => FlightLine.Build([Flight()], [], Now, []).Single();

    private static DecisionRow[] Sample() =>
    [
        new() { Subject = "flight/TIA1611", Kind = DecisionKinds.Merge, Question = "Merge the PR?", Options = ["merge", "wait"], Choice = "merge", By = "autopilot", ByKind = DecisionByKinds.Autopilot,
            Reason = "checks are green and the review passed, so the queue may take it and nothing else is waiting on the branch", At = Now.AddHours(-1), Seq = 12 },
        new() { Subject = "flight/TIA1611", Kind = DecisionKinds.Design, Question = "Which ephemeris source?", Options = ["VSOP87", "SOFA", "JPL"], Choice = "SOFA", By = "sebastian", ByKind = DecisionByKinds.Person,
            Reason = "already a dependency", At = Now.AddHours(-5), Seq = 7 },
    ];

    private static List<string> Texts(DecisionRow[]? decisions, int width = 1600)
    {
        using var vessel = new RgbaImageRenderer((uint)width, 900);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath }.Flights;
        if (decisions is not null)
        {
            flights.Decisions = new Dictionary<string, DecisionRow[]> { ["TIA1611"] = decisions };
        }
        var screen = new FlightsScreen([Line()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, width, 900));
        flights.OpenDetail("TIA1611");
        flights.Render(screen, new RectF32(0, 0, width, 900));
        return [.. Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];
    }

    [Fact]
    public void ThePageListsItsDecisionsOldestFirstWithChoiceOptionsWhoAndWhy()
    {
        var texts = Texts(Sample());

        texts.ShouldContain("decisions");
        var first = "2026-10-18 04:41 design: SOFA (of VSOP87 / SOFA / JPL), by sebastian (person): already a dependency";
        var second = "2026-10-18 08:41 merge: merge (of merge / wait), by autopilot (autopilot): checks are green and the review passed, so the queue may take it and nothing else is waiting on the branch";
        // Prose is laid out a word to a leaf, so the line is read back as the words after the label, in order.
        var said = string.Join(' ', texts.Skip(texts.IndexOf("decisions") + 1));
        said.ShouldContain(first);
        said.ShouldContain(second);
        said.IndexOf(first, StringComparison.Ordinal).ShouldBeLessThan(said.IndexOf(second, StringComparison.Ordinal));
    }

    [Fact]
    public void AFlightWithNoDecisionsShowsNoBlock()
    {
        Texts(null).ShouldNotContain("decisions");
        Texts([]).ShouldNotContain("decisions");
    }

    [Fact]
    public void TheDumpSaysThemToo()
    {
        var dump = FlightDetail.From(Line(), Now, null, Sample()).ToDump();

        dump.ShouldContain("decision: 2026-10-18 04:41 design: SOFA");
        dump.IndexOf("04:41", StringComparison.Ordinal).ShouldBeLessThan(dump.IndexOf("08:41", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(700, 1.5f)]
    [InlineData(1300, 1f)]
    [InlineData(1900, 1.25f)]
    public void NoTextOverlapsOnTheFlightPageWithDecisions(int width, float dpi)
    {
        using var vessel = new RgbaImageRenderer((uint)width, 900);
        var flights = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi }.Flights;
        flights.Decisions = new Dictionary<string, DecisionRow[]> { ["TIA1611"] = Sample() };
        var screen = new FlightsScreen([Line()], "test", Now);
        flights.Render(screen, new RectF32(0, 0, width, 900));
        flights.OpenDetail("TIA1611");
        flights.Render(screen, new RectF32(0, 0, width, 900));

        var faults = TextOverlap.Faults(Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(vessel, SessionsCapture.FontPath, dpi)));

        faults.Select(f => f.Line).ShouldBeEmpty();
    }
}
