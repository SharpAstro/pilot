using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Pull requests page's actions: opening a row, Refresh, and the Merge, Go around, Review and Address review presses with the node.
internal sealed partial class PullRequestsView<TSurface>
{
    /// <summary>Opens the PR's flight on the Flights page when it is one's, else the PR in the browser: what a double-click and Enter do.</summary>
    public void OpenRow(PullRequestRow pr)
    {
        if (pr.Flight is not null && OnFlight is { } flight)
        {
            flight(pr);
        }
        else
        {
            OnOpen?.Invoke(pr.Url);
        }
    }

    /// <summary>Asks the node to read GitHub again through <see cref="OnRefresh"/>: the button shows it running, and a refusal is said under the bar.</summary>
    public async Task RefreshAsync()
    {
        if (OnRefresh is not { } refresh || Refreshing)
        {
            return;
        }
        Refreshing = true;
        RefreshError = null;
        Changed?.Invoke();
        try
        {
            await refresh(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            RefreshError = ex.Message;
        }
        finally
        {
            Refreshing = false;
        }
        Changed?.Invoke();
    }

    /// <summary>The actions <paramref name="pr"/> offers now: what <see cref="PullRequestActions.Offered"/> says, less Review where the host has none.</summary>
    internal IReadOnlyList<PullRequestAction> ActionsOf(PullRequestRow pr) =>
        [.. PullRequestActions.Offered(pr, _cards).Where(a => a.Kind switch
        {
            PullRequestActionKind.Review => OnReview is not null,
            // A plain PR needs the host's fix-pr (a flight's goes through its card). Once the node took a press the action stays, drawn as
            // what the node is doing, until a read shows another review word.
            PullRequestActionKind.AddressReview => a.Card is not null || OnFixReview is not null,
            _ => true,
        })];

    /// <summary>Presses <paramref name="action"/> of <paramref name="pr"/>. The row is not changed by it: the node's next answer is what shows.</summary>
    public void Press(PullRequestRow pr, PullRequestAction action)
    {
        switch (action.Kind)
        {
            case PullRequestActionKind.Open:
                OnOpen?.Invoke(pr.Url);
                break;
            case PullRequestActionKind.Review:
                _ = ReviewAsync(pr);
                break;
            case PullRequestActionKind.AddressReview when action.Card is null:
                _ = FixReviewAsync(pr);
                break;
            default:
                ReviewRefused = null;
                FixRefused = null;
                if (action.Card is { } card)
                {
                    OnAnswer?.Invoke(card, action.Choice);
                }
                break;
        }
    }

    /// <summary>Asks the node to review <paramref name="pr"/> through <see cref="OnReview"/>; a refusal is said in the node's words in the bar above the list.</summary>
    public async Task ReviewAsync(PullRequestRow pr)
    {
        if (OnReview is not { } review)
        {
            return;
        }
        if (Presses.PendingOf(pr.Url) is not null)
        {
            return;
        }
        ReviewRefused = null;
        ReviewRefused = await Presses.PressAsync(pr.Url, ReviewHit(pr), async ct =>
        {
            await review(pr, ct);
            return null;
        }, say: why => $"review {pr.Repo}#{pr.Number} refused: {why}");
    }

    /// <summary>Asks the node to fix <paramref name="pr"/>'s review findings through <see cref="OnFixReview"/>; a refusal is said in the node's words above the list.</summary>
    public async Task FixReviewAsync(PullRequestRow pr)
    {
        if (OnFixReview is not { } fix || Presses.PendingOf(FixKey(pr.Url)) is not null)
        {
            return;
        }
        FixRefused = null;
        ReviewRefused = null;
        FixRefused = await Presses.PressAsync(FixKey(pr.Url), FixHit(pr), async ct =>
        {
            await fix(pr, ct);
            return null;
        }, say: why => $"review fix {pr.Repo}#{pr.Number} refused: {why}");
    }

    private static string ReviewHit(PullRequestRow pr) => $"pr:{pr.Url}:{ActionId(PullRequestActionKind.Review)}";

    private static string FixHit(PullRequestRow pr) => $"pr:{pr.Url}:{ActionId(PullRequestActionKind.AddressReview)}";

    /// <summary>
    /// What the node refused of the last action pressed on the list, in its words: a Review's, or a card's (the one a row's Merge or
    /// Go around pressed, which the panel shows too). Null when nothing was refused.
    /// </summary>
    internal string? Refusal() =>
        ReviewRefused ?? FixRefused ?? _cards.Where(c => c.Choices.Any(ch => ch.Verb is Verbs.Merge or Verbs.GoAround))
            .Select(c => RefusedOf?.Invoke(c) is { } why ? $"{c.Title}: {why}" : null).FirstOrDefault(r => r is not null);
}
