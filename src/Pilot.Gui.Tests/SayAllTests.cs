using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// "Say to all shown": the Sessions page's header pill says the bar's text to every shown row <c>pilot say-all</c> would pick
/// with its defaults, through the host's send, and sums up what became of each.
/// </summary>
public sealed class SayAllTests
{
    private const string Pill = "sessions:say-all";

    private static SessionLine Line(string id, string name, SessionState state, SessionRole role = SessionRole.Worker, bool archived = false) =>
        SessionLine.From(Live.Row(id, name, state) with { Role = role, Archived = archived }, Live.At);

    // Two the default pick tells (working, waiting), and every kind it leaves out.
    private static SessionsScreen Screen() => new(SessionLine.Order(
    [
        Line("aaaa0001", "w-working", SessionState.Working),
        Line("aaaa0002", "w-waiting", SessionState.Waiting),
        Line("aaaa0003", "w-idle", SessionState.Idle),
        Line("aaaa0004", "w-ended", SessionState.Ended),
        Line("aaaa0005", "w-failed", SessionState.Failed),
        Line("aaaa0006", "du1-pil5-node-validator", SessionState.Working),
        Line("aaaa0007", "xy1-watchtower", SessionState.Working, SessionRole.Watchtower),
        Line("aaaa0008", "w-archived", SessionState.Ended, archived: true),
    ]), "test", Live.At);

    private static (SessionsView<RgbaImage> View, List<string> Sent, RgbaImageRenderer Renderer) Make(Func<SessionLine, bool>? delivered = null)
    {
        var renderer = new RgbaImageRenderer(1, 1);
        var sent = new List<string>();
        var view = new SessionsView<RgbaImage>(renderer)
        {
            CanSay = true,
            OnSayAll = (line, text) =>
            {
                sent.Add($"{line.Name}: {text}");
                return Task.FromResult<string?>(delivered?.Invoke(line) ?? true ? null : "the node refused it");
            },
        };
        return (view, sent, renderer);
    }

    private static string? PillLabel(SessionsView<RgbaImage> view, SessionsScreen screen) =>
        Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 400, 40), CellMeasureContext.PixelAuthored)
            .Where(a => a.Node.Hit is HitResult.ButtonHit { Action: Pill })
            .Select(a => CellLayout.Describe([a]))
            .FirstOrDefault();

    [Fact]
    public void SayAllCountFollowsTheShownRowsAndLeavesOutEndedValidatorWatchtowerAndArchivedOnes()
    {
        var (view, _, renderer) = Make();
        using var _ = renderer;
        var screen = Screen();
        view.ShowArchived = true;
        view.Tree(screen);

        // Archived rows shown by the toggle are still never told.
        view.SayAllCount.ShouldBe(2);

        view.ShowArchived = false;
        view.Tree(screen);
        view.SayAllCount.ShouldBe(2);

        // Fewer rows shown (a node pill off) means a smaller count.
        view.Tree(screen with { Lines = [.. screen.Lines.Where(l => l.Name != "w-waiting")] });
        view.SayAllCount.ShouldBe(1);
    }

    [Fact]
    public void SayAllPillShowsOnlyWithTextInTheBarAndAtLeastOneRowToTell()
    {
        var (view, _, renderer) = Make();
        using var _ = renderer;
        var screen = Screen();

        PillLabel(view, screen).ShouldBeNull();

        view.SayInput.Text = "hello";
        PillLabel(view, screen).ShouldNotBeNull().ShouldContain("Say to all shown (2)");

        view.SayInput.Text = "   ";
        PillLabel(view, screen).ShouldBeNull();

        // Nothing to tell: only rows the defaults leave out.
        view.SayInput.Text = "hello";
        PillLabel(view, screen with { Lines = [.. screen.Lines.Where(l => l.Name is "w-idle" or "w-ended" or "xy1-watchtower")] }).ShouldBeNull();

        // No node to say through.
        view.CanSay = false;
        PillLabel(view, screen).ShouldBeNull();
    }

    [Fact]
    public void SayAllPressedSendsTheBarsTextToExactlyThosePickedRowsAndSumsUp()
    {
        var (view, sent, renderer) = Make(line => line.Name != "w-waiting");
        using var _ = renderer;
        var screen = Screen();
        view.SayInput.Text = " keep going ";
        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 400, 40), CellMeasureContext.PixelAuthored);
        var pill = arranged.First(a => a.Node.Hit is HitResult.ButtonHit { Action: Pill });

        CellLayout.HitTest(arranged, pill.Bounds.X + 1, pill.Bounds.Y);

        sent.Order().ShouldBe(["w-waiting: keep going", "w-working: keep going"]);
        view.SayInput.Text.ShouldBe("");
        view.SayStatus.ShouldBe("said to 1, 1 refused");
    }

    [Fact]
    public async Task SayAllCountsAWatchOnlyNodesRowsAsNotSaidAndSendsNothingForThem()
    {
        var (view, sent, renderer) = Make();
        using var _ = renderer;
        var se1 = new NodeMark("node.sock", "node.sock", "SE1", Here: true);
        var le1 = new NodeMark("10.0.0.2:5000", "10.0.0.2:5000", "LE1", Here: false);
        view.CanSteer = node => node is not { Name: "LE1" };
        var screen = new SessionsScreen(SessionLine.Order(
        [
            Line("aaaa0001", "se1-working", SessionState.Working) with { Node = se1 },
            Line("aaaa0001", "le1-working", SessionState.Working) with { Node = le1 },
            Line("aaaa0002", "le1-waiting", SessionState.Waiting) with { Node = le1 },
        ]), "test", Live.At);
        view.Tree(screen);
        view.SayInput.Text = "hi";

        view.SayAllCount.ShouldBe(3);
        await view.SayAllAsync();

        sent.ShouldBe(["se1-working: hi"]);
        view.SayStatus.ShouldBe("said to 1, 2 watch-only");
    }
}
