using System.Text.RegularExpressions;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
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

        var press = presses.PressAsync("k", "k:go", _ => { asked++; return node.Task; });

        // The node has not answered: the press is said, and a redraw was asked at the press.
        presses.PendingOf("k").ShouldBe("k:go");
        redraws.Count.ShouldBe(1);
        // A second press meanwhile does nothing, and asks nothing.
        (await presses.PressAsync("k", "k:go", _ => { asked++; return Task.FromResult<string?>(null); })).ShouldBeNull();
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
        var press = presses.PressAsync("k", "k:go", _ => node.Task);
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
        var press = presses.PressAsync("k", "k:go", _ => node.Task);
        node.SetResult("head moved past what was reviewed");

        (await press).ShouldBe("head moved past what was reviewed");
        presses.RefusedOf("k").ShouldBe("head moved past what was reviewed");
        presses.IsTaken("k").ShouldBeFalse();
        presses.PendingOf("k").ShouldBeNull();

        // Pressed again: the old words go while the node thinks, and the next answer replaces them.
        var again = new TaskCompletionSource<string?>();
        var second = presses.PressAsync("k", "k:go", _ => again.Task);
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
        var why = await presses.PressAsync("k", "k:go", _ => throw (Exception)Activator.CreateInstance(fault, "the node is gone")!);

        why.ShouldBe("the node is gone");
        presses.RefusedOf("k").ShouldBe("the node is gone");
        presses.PendingOf("k").ShouldBeNull();
    }

    [Fact]
    public async Task ATakenMarkLapsesAfterItsTimeOrGivesWayToTheNextRead()
    {
        var presses = Counting(out _);
        await presses.PressAsync("lapses", "lapses:go", _ => Task.FromResult<string?>(null));
        await presses.PressAsync("read", "read:go", _ => Task.FromResult<string?>(null));
        presses.IsTaken("lapses").ShouldBeTrue();

        // A read that shows the outcome takes over.
        presses.ClearTaken("read");
        presses.IsTaken("read").ShouldBeFalse();
        presses.IsTaken("lapses").ShouldBeTrue();

        // Left alone it lapses: the row cannot say "reviewing" for a review that never ran.
        presses.TakenFor = TimeSpan.Zero;
        presses.IsTaken("lapses").ShouldBeFalse();
    }

    // ---- the buttons that bypass the helper ----

    // A node-bound button not yet made through Presses, by page and the pattern of its hit id: the legs of PIL461 that follow port these.
    // "*" is a button the chrome draws on every page.
    private static readonly (string Page, string Pattern)[] ToPort =
    [
        ("*", "^autopilot:toggle$"),
        ("flight page", @"^flight:[^:]+:(go-around|plan-leg|remove-leg)$"),
        ("flights table", @"^flight:[^:]+:(go-around|plan-leg|remove-leg)$"),
        ("flights canvas", @"^flight:[^:]+:(go-around|plan-leg|remove-leg)$"),
        // A Merge or a Go around of a row asks the node through the card's own press, which the row does not show.
        ("pull requests", @"^pr:.+:(merge|go-around)$"),
    ];

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
        (@"^wizard:", "the new-flight wizard's picks, steps and close; its own create is the wizard's"),
        (@"^card:[^:]+:(chat|leg)$", "opens the chat about the card, or the leg's page"),
        (@"^cards:chat$", "opens the chat with the watchtower"),
    ];

    [Fact]
    public async Task EveryNodeBoundButtonOfEveryPageGoesThroughPressesOrIsListedHere()
    {
        var made = Presses.Made.ToHashSet(StringComparer.Ordinal);
        var unlisted = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in PageTextOverlapTests.Pages)
        {
            var arranged = await PageTextOverlapTests.ArrangedOf(page, "sample", 1900, 1f, chrome =>
            {
                chrome.Cards.OnAnswer = (_, _) => { };
                chrome.Cards.OnDismiss = _ => { };
                chrome.PullRequests.OnReview = (_, _) => Task.CompletedTask;
            });
            // Made is read after the arranging: the buttons the page made are in it by then.
            made.UnionWith(Presses.Made);
            foreach (var hit in arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).Distinct())
            {
                seen.Add(hit);
                var listed = made.Contains(hit)
                    || ToPort.Any(p => (p.Page == page || p.Page == "*") && Regex.IsMatch(hit, p.Pattern))
                    || LocalOnly.Any(l => Regex.IsMatch(hit, l.Pattern));
                if (!listed)
                {
                    unlisted.Add($"{page}: {hit}");
                }
            }
        }

        // The arranging does reach the buttons made through the helper: a PR's Review and a card's Dismiss.
        seen.ShouldContain(h => h.EndsWith(":start-review", StringComparison.Ordinal));
        seen.ShouldContain(h => h.StartsWith("card:", StringComparison.Ordinal) && h.EndsWith(":dismiss", StringComparison.Ordinal));
        unlisted.ShouldBeEmpty("a button that asks the node is made through Presses (working at once, no second press, the outcome or the refusal where it was pressed); one that does not is listed in ToPort (a later leg ports it) or LocalOnly (why it needs none)");
    }
}
