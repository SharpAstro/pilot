using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A wrapped link (a card's title link, a PR title through <see cref="PullRequestLink"/>) lights every word of it, on all its lines,
/// when the pointer is on any one word, as a hyperlink reads (pilot #1026); the base lights only the rect under the pointer.
/// </summary>
public sealed class WrappedLinkHoverTests
{
    private const string LongTitle = "A card whose title is long enough to wrap over two lines in a narrow panel of cards";
    private const float Width = 260f;

    private static CardRow Plain() => new()
    {
        Id = "p1", Bin = CardBin.ForYou, Kind = "session-stopped", Subject = "flight/X", Title = LongTitle, Link = "https://example.org/x",
        Summary = "Other text.", At = Live.At, State = CardState.Open,
    };

    private static CardRow PullRequest() => new()
    {
        Id = "r1", Bin = CardBin.ForYou, Kind = "pr-ready", Subject = "pr/x/y#7", Title = LongTitle, Link = "https://github.com/x/y/pull/7",
        Summary = "Other text.", At = Live.At, State = CardState.Open,
    };

    private sealed class Rig : IDisposable
    {
        public readonly RgbaImageRenderer Renderer = new(1, 1);
        public readonly CardsView<RgbaImage> View;
        public ImmutableArray<Layout.ArrangedNode<float>> Arranged;
        private int _asked;

        public Rig(bool pullRequests)
        {
            View = new CardsView<RgbaImage>(Renderer) { FontPath = SessionsCapture.FontPath, Changed = () => _asked++ };
            View.Arranged = arranged => Arranged = arranged;
            if (pullRequests)
            {
                View.OnPullRequest = (_, _) => { };
                View.PullRequests = new PullRequestsRow
                {
                    Orgs = ["x"], ReadAt = Live.At, Viewer = "v",
                    PullRequests = [new PullRequestRow { Repo = "x/y", Number = 7, Title = "A", Url = "https://github.com/x/y/pull/7", Kind = PullRequestKind.Ready, Label = "Ready", Flight = "A", UpdatedAt = Live.At, CreatedAt = Live.At }],
                };
            }
        }

        /// <summary>Renders until the view asks for no further frame (at most a few), and returns how many frames it took.</summary>
        public int Settle(CardRow card)
        {
            for (var frame = 1; frame <= 4; frame++)
            {
                _asked = 0;
                View.Render(CardsScreen.From([card]), new RectF32(0, 0, Width, 1200));
                if (_asked == 0)
                {
                    return frame;
                }
            }
            throw new InvalidOperationException("The view kept asking for frames.");
        }

        public void Dispose() => Renderer.Dispose();
    }

    private static List<Layout.ArrangedNode<float>> Words(Rig rig, string action) =>
        [.. rig.Arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && a.Node.Hit is HitResult.ButtonHit b && b.Action == action)];

    private static bool Lit(Layout.ArrangedNode<float> a) => a.Node.Background == Palette.Current.CardHover;

    private static void AssertWholeLinkLights(CardRow card, bool pullRequests)
    {
        using var rig = new Rig(pullRequests);
        var action = $"card:{card.Id}:title";
        rig.Settle(card);
        var words = Words(rig, action);
        words.Select(w => w.Bounds.Y).Distinct().Count().ShouldBeGreaterThan(1, "the title must wrap over several lines");
        words.ShouldNotContain(Lit);

        // The pointer on a word of the last line lights every word of every line, and no text outside the link.
        var last = words.OrderBy(w => w.Bounds.Y).ThenBy(w => w.Bounds.X).Last();
        rig.View.Pointer = (last.Bounds.X + last.Bounds.Width / 2, last.Bounds.Y + last.Bounds.Height / 2);
        rig.Settle(card);
        var lit = Words(rig, action);
        lit.ShouldAllBe(w => Lit(w));
        rig.Arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && Lit(a)).Count().ShouldBe(lit.Count);

        // Off the link, none.
        rig.View.Pointer = (Width - 1, 1100);
        rig.Settle(card);
        Words(rig, action).ShouldNotContain(Lit);
    }

    [Fact]
    public void APlainLinkTitleLightsEveryWordWhenThePointerIsOnOne() => AssertWholeLinkLights(Plain(), pullRequests: false);

    [Fact]
    public void APullRequestTitleLightsEveryWordWhenThePointerIsOnOne() => AssertWholeLinkLights(PullRequest(), pullRequests: true);
}
