using System.Text.RegularExpressions;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The shared feedback of a button that asks the node (<see cref="Presses"/>; issue 461, the owner 2026-10-29: "all buttons should
/// always have a feedback in one form or the other"): working on the frame it is pressed, then taken or refused in the node's words.
/// And the list of the buttons that do not go through it yet.
/// </summary>
public sealed class ButtonFeedbackTests
{
    private static Presses Counting(out List<string> log)
    {
        var seen = new List<string>();
        log = seen;
        return new Presses { Changed = () => seen.Add("changed") };
    }

    [Fact]
    public async Task APressIsWorkingAtOnceTakesNoSecondPressAndIsTakenWhenTheNodeAnswers()
    {
        var presses = Counting(out var redraws);
        var node = new TaskCompletionSource<string?>();
        var asked = 0;

        var press = presses.PressAsync("k", "k:go", _ => { asked++; return node.Task; }, TestContext.Current.CancellationToken);

        // The node has not answered: the press is said, and a redraw was asked at the press.
        presses.PendingOf("k").ShouldBe("k:go");
        redraws.Count.ShouldBe(1);
        // A second press meanwhile does nothing, and asks nothing.
        (await presses.PressAsync("k", "k:go", _ => { asked++; return Task.FromResult<string?>(null); }, TestContext.Current.CancellationToken)).ShouldBeNull();
        asked.ShouldBe(1);
        redraws.Count.ShouldBe(1);

        node.SetResult(null);
        (await press).ShouldBeNull();
        presses.PendingOf("k").ShouldBeNull();
        presses.IsTaken("k").ShouldBeTrue();
        presses.RefusedOf("k").ShouldBeNull();
        // And a redraw at the answer.
        redraws.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AButtonIsItsWorkingLabelWithNoHitWhilePendingAndItsOwnAgainAfter()
    {
        var presses = Counting(out _);
        var node = new TaskCompletionSource<string?>();
        Layout.Node Make(string key) => presses.Button(key, $"{key}:go",
            () => Layout.Builder.Text("Go", 12f).Clickable(new HitResult.ButtonHit($"{key}:go"), _ => { }),
            pressed => Layout.Builder.Text(pressed ? "Going…" : "Go", 12f));

        Make("k").Hit.ShouldBeOfType<HitResult.ButtonHit>();
        var press = presses.PressAsync("k", "k:go", _ => node.Task, TestContext.Current.CancellationToken);
        var working = Make("k");
        working.Hit.ShouldNotBeOfType<HitResult.ButtonHit>();
        Presses.Made.ShouldContain("k:go");

        node.SetResult(null);
        await press;
        Make("k").Hit.ShouldBeOfType<HitResult.ButtonHit>();
    }

    [Fact]
    public async Task ARefusalIsKeptBesideTheButtonInTheNodesWordsUntilTheNextPress()
    {
        var presses = Counting(out _);
        var node = new TaskCompletionSource<string?>();
        var press = presses.PressAsync("k", "k:go", _ => node.Task, TestContext.Current.CancellationToken);
        node.SetResult("head moved past what was reviewed");

        (await press).ShouldBe("head moved past what was reviewed");
        presses.RefusedOf("k").ShouldBe("head moved past what was reviewed");
        presses.IsTaken("k").ShouldBeFalse();
        presses.PendingOf("k").ShouldBeNull();

        // Pressed again: the old words go while the node thinks, and the next answer replaces them.
        var again = new TaskCompletionSource<string?>();
        var second = presses.PressAsync("k", "k:go", _ => again.Task, TestContext.Current.CancellationToken);
        presses.RefusedOf("k").ShouldBeNull();
        again.SetResult(null);
        await second;
        presses.RefusedOf("k").ShouldBeNull();
        presses.IsTaken("k").ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    public async Task AThrownFaultOfTheNodesWireIsARefusalInItsMessage(Type fault)
    {
        var presses = Counting(out _);
        var why = await presses.PressAsync("k", "k:go", _ => throw (Exception)Activator.CreateInstance(fault, "the node is gone")!, TestContext.Current.CancellationToken);

        why.ShouldBe("the node is gone");
        presses.RefusedOf("k").ShouldBe("the node is gone");
        presses.PendingOf("k").ShouldBeNull();
    }

    [Fact]
    public async Task ATakenMarkLapsesAfterItsTimeOrGivesWayToTheNextRead()
    {
        var presses = Counting(out _);
        await presses.PressAsync("lapses", "lapses:go", _ => Task.FromResult<string?>(null), TestContext.Current.CancellationToken);
        await presses.PressAsync("read", "read:go", _ => Task.FromResult<string?>(null), TestContext.Current.CancellationToken);
        presses.IsTaken("lapses").ShouldBeTrue();

        // A read that shows the outcome takes over.
        presses.ClearTaken("read");
        presses.IsTaken("read").ShouldBeFalse();
        presses.IsTaken("lapses").ShouldBeTrue();

        // Left alone it lapses: the row cannot say "reviewing" for a review that never ran.
        presses.TakenFor = TimeSpan.Zero;
        presses.IsTaken("lapses").ShouldBeFalse();
    }

    [Fact]
    public async Task AWatchOnlyCardsConnectIsWorkingAtOnceTakesNoSecondPressAndSaysARefusalOnTheCard()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var cards = chrome.Cards;
        var node = new PageButtonFeedbackTests.LateNode();
        var far = new NodeMark("192.168.0.79:47731", "192.168.0.79:47731", "DE1", Here: false);
        cards.CanSteer = n => n?.Here != false;
        cards.OnConnect = n => node.Ask($"connect {n.Name}");
        var card = new CardRow
        {
            Id = "cfar", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/X", Title = "landed on DE1", At = Live.At, State = CardState.Open,
            Choices = [new CardChoice { Label = "Merge", Verb = "answer", Target = "card/x" }],
        };
        var screen = CardsScreen.Merge([(far, CardsScreen.From([card], [], Live.At))], null);
        const string connect = "card:cfar:connect";

        // Pressed: the pill says it is working on this frame, with no hit, so a second press asks nothing.
        PageButtonFeedbackTests.Click(cards.Tree(screen), connect);
        node.Asked.ShouldBe(["connect DE1"]);
        PageButtonFeedbackTests.Words(cards.Tree(screen)).ShouldContain("Connect …");
        PageButtonFeedbackTests.Has(cards.Tree(screen), connect).ShouldBeFalse();

        // Refused: the node's words on the card, and the pill back to press again.
        node.Answer("DE1 did not allow it");
        await PageButtonFeedbackTests.Until(() => PageButtonFeedbackTests.Has(cards.Tree(screen), connect));
        PageButtonFeedbackTests.Words(cards.Tree(screen)).ShouldContain("DE1 did not allow it");

        // Pressed again and taken: the old words go, and nothing is refused.
        PageButtonFeedbackTests.Click(cards.Tree(screen), connect);
        node.Asked.Count.ShouldBe(2);
        node.Answer(null);
        await PageButtonFeedbackTests.Until(() => PageButtonFeedbackTests.Has(cards.Tree(screen), connect));
        PageButtonFeedbackTests.Words(cards.Tree(screen)).ShouldNotContain("DE1 did not allow it");
        cards.RefusedOn("cfar").ShouldBeNull();
    }

    // ---- the buttons that bypass the helper ----

    // A button that asks the node nothing: navigation, folds, links, copy; the reason is why it needs no working state.
    private static readonly (string Pattern, string Why)[] LocalOnly =
    [
        (@"^about:(toggle|restart)$", "the About card's fold, and a restart of this window, not a command on the node"),
        (@"^night:toggle$", "the window's palette"),
        (@"^nodes:", "the node filter of the rail: which nodes the pages show"),
        (@"^play:", "the in-play filter of a list: what the page shows"),
        (@":table:divider:\d+$", "a column divider dragged in a table"),
        (@"^flights:view:", "the Flights page's view switch"),
        (@"^flight:(back|[^:]+:open)$", "navigation to and from the flight page"),
        (@"^pr:(PIL\d+|https://\S+/pull/\d+)$", "a flight's PR link, or a PR row picked on the Pull requests page"),
        (@"^pr:.+:(open|flight)$", "a row of the Pull requests page opens GitHub or the flight page"),
        (@"^pr:\d+$", "a PR chip on the Repos page opens it on GitHub"),
        (@"^prs:", "the Pull requests page's filters and folds"),
        (@"^home:", "a Home row opens its page"),
        (@"^repo:|^repos:", "the Repos page's folds and links"),
        (@"^transcript:", "the transcript's folds and its way back"),
        (@"^wizard:", "the new-flight wizard's picks, steps and close; its Create and Create issue go through its Presses"),
        (@"^fleet:.+:(open|waiting)$", "opens a window of its own on that node, or takes a row's waiting count to those sessions"),
        (@"^card:[^:]+:(chat|leg)$", "opens the chat about the card, or the leg's page"),
        (@":table:[^:]*$", "a column header of a list: the page's sort"),
        (@"^chat:node:", "the chat's pick of a node's watchtower: which chat the page shows"),
        (@"^flight:[^:]+:checklist:", "a checklist line of the flight page reveals its leg"),
        (@"^FlightMapLayer|^map:", "the flight map's layer switches, grip and nodes: what the canvas shows and where its cursor is"),
        (@"^cards:chat$", "opens the chat with the watchtower"),
        (@"^job:[^:]+:(cancel|force|cancel-no|force-no)$", "a job's Cancel… and Force cancel… only ask the question, and Keep running drops it; the answer that cancels goes through its Presses"),
        (@"^chat:(end|back|restart|restart-no|drop-card)$", "the chat's jump to end, way back, the Restart ask and its Keep it, and Drop of a quote: no command on the node; its choices and Stop it and start fresh go through its Presses"),
    ];

    [Fact]
    public async Task EveryNodeBoundButtonOfEveryPageGoesThroughPressesOrIsListedHere()
    {
        var made = Presses.Made.ToHashSet(StringComparer.Ordinal);
        var unlisted = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in PageTextOverlapTests.Pages)
        {
            // Every callback a button is gated on is wired, as the per-page tests wire them: a button the page shows only when its host
            // can do it is arranged too, and a bypass among them fails here rather than passing unseen.
            var arranged = await PageTextOverlapTests.ArrangedOf(page, "sample", 1900, 1f, chrome =>
            {
                Task<string?> Took() => Task.FromResult<string?>(null);
                chrome.Cards.OnAnswer = (_, _) => { };
                chrome.Cards.OnDismiss = _ => { };
                chrome.Cards.OnConnect = _ => Took();
                chrome.PullRequests.OnReview = (_, _) => Task.CompletedTask;
                chrome.PullRequests.OnFixReview = (_, _) => Task.CompletedTask;
                chrome.Jobs.OnCancel = _ => Took();
                chrome.Jobs.OnForceCancel = _ => Took();
                chrome.Jobs.OnPause = _ => Took();
                chrome.Jobs.OnResume = _ => Took();
                chrome.Jobs.OnConnect = _ => Took();
                chrome.Fleet.OnConnect = _ => Took();
                chrome.Fleet.OnForget = _ => Took();
                chrome.Fleet.OnMaintenance = (_, _) => Took();
                chrome.Sessions.OnAction = _ => Took();
                chrome.Sessions.OnConnect = _ => Took();
                chrome.Flights.OnAction = _ => Took();
                chrome.Flights.OnConnect = _ => Took();
                chrome.Home.OnAction = _ => Took();
            });
            // Made is read after the arranging: the buttons the page made are in it by then.
            made.UnionWith(Presses.Made);
            foreach (var hit in arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Distinct())
            {
                seen.Add(hit);
                var listed = made.Contains(hit)
                    || LocalOnly.Any(l => Regex.IsMatch(hit, l.Pattern));
                if (!listed)
                {
                    unlisted.Add($"{page}: {hit}");
                }
            }
        }

        // The arranging does reach the buttons made through the helper: a PR's Review, a card's Dismiss, and the ones shown only when
        // their host callback is set (a watch-only Fleet row's Connect, a node's maintenance switch).
        seen.ShouldContain(h => h.EndsWith(":start-review", StringComparison.Ordinal));
        seen.ShouldContain(h => h.StartsWith("card:", StringComparison.Ordinal) && h.EndsWith(":dismiss", StringComparison.Ordinal));
        seen.ShouldContain(h => h.StartsWith("fleet:", StringComparison.Ordinal) && h.EndsWith(":connect", StringComparison.Ordinal));
        seen.ShouldContain(h => h.StartsWith("fleet:", StringComparison.Ordinal) && h.EndsWith(":maintenance", StringComparison.Ordinal));
        unlisted.ShouldBeEmpty("a button that asks the node is made through Presses (working at once, no second press, the outcome or the refusal where it was pressed); one that does not is listed in LocalOnly (why it needs none)");
    }
}
