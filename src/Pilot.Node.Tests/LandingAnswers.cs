using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The shapes of GitHub's answer every merge path is driven with (pilot #691), and what each path must end in: merged, retried, or
/// exactly one open card that asks (not a Handled line) whose detail is GitHub's whole text. FakeGitHub.MergeScript sorts each
/// scripted text once, so a "Retried" row proves the outcome <see cref="LandKind.RetryLater"/> and what the path does with it; the gh
/// layer's own retries of a 5xx and a timeout are proved in <see cref="MergeAnswerTests"/> (its Runner and Delay).
/// </summary>
public static class LandingAnswers
{
    public const string Marker = "marker-7431";

    public const string SecondLine = "A second line GitHub gave.";

    public enum Shape { Done, Retried, Refused }

    public static readonly (string Text, Shape Shape)[] All =
    [
        ($"GraphQL: Pull request is already in the queue {Marker}", Shape.Done),
        ($"Pull request is already merged {Marker}", Shape.Done),
        ($"GraphQL: Auto merge is already enabled {Marker}", Shape.Done),
        ($"GraphQL: This branch can't be rebased {Marker} (mergePullRequest)", Shape.Done),
        ($"HTTP 502 Bad Gateway {Marker}", Shape.Retried),
        ($"gh timed out {Marker}", Shape.Retried),
        ($"GraphQL: Base branch was modified {Marker}. Review and try the merge again.", Shape.Refused),
        ($"GraphQL: Pull request has a merge conflict {Marker}", Shape.Refused),
        ($"GraphQL: Head branch was modified {Marker}. Review and try the merge again.", Shape.Refused),
        ($"GraphQL: Repository rule violations found {Marker}\n{SecondLine}", Shape.Refused),
    ];

    /// <summary>Every path of <typeparamref name="TPath"/> times every answer.</summary>
    public static TheoryData<TPath, string, Shape> Cases<TPath>() where TPath : struct, Enum
    {
        var data = new TheoryData<TPath, string, Shape>();
        foreach (var path in Enum.GetValues<TPath>())
        {
            foreach (var (text, shape) in All)
            {
                data.Add(path, text, shape);
            }
        }
        return data;
    }

    /// <summary>A behind or conflicting answer, which a path the node runs itself answers by bringing the branch up to date.</summary>
    public static bool UpdatesTheBranch(string answer) =>
        answer.Contains("Base branch was modified", StringComparison.Ordinal) || answer.Contains("merge conflict", StringComparison.Ordinal);

    /// <summary>
    /// A head that moved past the one named: the node's own wait (PIL864), so a path the node runs itself starts a review of the new head
    /// and shows no card for it.
    /// </summary>
    public static bool HeadMoved(string answer) => answer.Contains("Head branch was modified", StringComparison.Ordinal);

    /// <summary>The open cards that ask for something (not Handled lines) and show GitHub's text.</summary>
    public static CardRow[] Asking(NodeState state) =>
        [.. state.Cards(openOnly: true).Where(c => c.Bin != CardBin.Handled && $"{c.Title}\n{c.Detail}".Contains(Marker, StringComparison.Ordinal))];

    /// <summary>
    /// Nothing rung and no card showing GitHub's text: a done or retried answer. The flight's own PR-ready card is not counted: it
    /// is there before the merge (in Ring with the autopilot off), and a retried merge says on it that it waits.
    /// </summary>
    public static void NothingAsks(NodeState state)
    {
        state.Cards(openOnly: true).Where(c => c.Bin is CardBin.Ring && c.Kind != LegCommander.PrReady).ShouldBeEmpty();
        Asking(state).Where(c => c.Kind != LegCommander.PrReady).ShouldBeEmpty();
    }

    /// <summary>Exactly one open card asks, and its detail holds GitHub's whole text, every line.</summary>
    public static CardRow OneCardHoldsTheWholeText(NodeState state, string answer)
    {
        var card = Asking(state).ShouldHaveSingleItem();
        foreach (var line in answer.Split('\n'))
        {
            card.Detail.ShouldNotBeNull().ShouldContain(line);
        }
        return card;
    }

    // Every 7-to-40 hex run a card shows (a sha mixes digits and letters) is a commit the card links; a button label holds none. A run
    // inside a path or a name (after a slash, a backslash, a dot, a dash or an underscore: a temp folder p2p-15a13b33) is no sha.
    private static readonly Regex HexRun = new(@"(?<![0-9A-Za-z/\\_.-])(?=[0-9a-f]*[0-9])(?=[0-9a-f]*[a-f])[0-9a-f]{7,40}(?![0-9A-Za-z])", RegexOptions.CultureInvariant);

    public static void EveryShaIsALink(NodeState state)
    {
        foreach (var card in state.Cards())
        {
            foreach (var text in new[] { card.Title, card.Detail, card.Summary }.Concat(card.Choices.Select(c => c.Note)))
            {
                foreach (Match m in HexRun.Matches(text ?? ""))
                {
                    card.Commits.ShouldContainKey(m.Value, $"card {card.Id} ({card.Kind}) shows {m.Value} without linking it: {text}");
                    card.Commits[m.Value].ShouldEndWith($"/commit/{m.Value}");
                }
            }
            foreach (var choice in card.Choices)
            {
                HexRun.IsMatch(choice.Label).ShouldBeFalse($"card {card.Id} ({card.Kind}): label '{choice.Label}' holds a sha");
            }
        }
    }
}
