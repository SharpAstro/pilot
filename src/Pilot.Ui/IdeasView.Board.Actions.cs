using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The board's verbs on the node: Make flight, Pin, priority, the milestone suggestions and their accept, and the move to a milestone.
internal sealed partial class IdeasView<TSurface>
{
    internal const string MakeFlightLabel = "Make flight";

    /// <summary>
    /// Makes a flight of an open issue that has none (<c>create-flight</c> on the issue, then <c>plan-flight</c> on the flight made, as
    /// the New flight wizard does) and says what the node answered. The issue stays open: the flight links it (create-flight records it
    /// as the flight's work item) and the flight's PR closes it on merge, so nothing here closes it.
    /// </summary>
    internal async Task MakeFlightAsync(BoardItem item)
    {
        var busy = "flight:" + item.Key;
        if (Send is null || !Begin(busy))
        {
            return;
        }
        Changed?.Invoke();
        try
        {
            string? path = null;
            if (LoadRepos is { } load)
            {
                try
                {
                    path = (await load(CancellationToken.None)).FirstOrDefault(r => string.Equals(r.Name, item.Repo, StringComparison.OrdinalIgnoreCase))?.Path;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Say($"no flight made of {Label(item)}: {ex.Message}", refusal: true);
                    return;
                }
            }
            if (path is null)
            {
                Say($"no flight made of {Label(item)}: the node has no checkout of {item.Repo}", refusal: true);
                return;
            }
            var made = await RunAsync("making:" + item.Key, "ideas:board:" + Verbs.CreateFlight + ":" + item.Key, "making a flight", Verbs.CreateFlight, Verbs.RepoTarget(path), ["#" + item.Number.ToString(CultureInfo.InvariantCulture), item.Title]);
            if (made is not { Ok: true, Made: { Length: > 0 } code })
            {
                Say($"no flight made of {Label(item)}: {made?.Error ?? "the node did not answer"}", refusal: true);
                return;
            }
            if (made.Way is { Length: > 0 } way)
            {
                // Made on another node of the fleet: this node has no flight of that code to plan.
                Say($"{Label(item)}: {way}", refusal: false);
                return;
            }
            // The node starts the planner itself off the create's reply, so no plan-flight follows.
            Say($"made {code} of {Label(item)}; its planner is planning the legs", refusal: false);
        }
        finally
        {
            End(busy);
        }
    }

    /// <summary>Pins the item (the node puts the pin label on its issue on GitHub), or unpins it when it is pinned; a press again while it runs sends nothing.</summary>
    internal async Task PinAsync(BoardItem item)
    {
        var busy = "pin:" + item.Key;
        if (Send is null || !Begin(busy))
        {
            return;
        }
        Changed?.Invoke();
        try
        {
            var reply = await RunAsync("pinning:" + item.Key, "ideas:board:" + Verbs.PinIssue + ":" + item.Target, "pinning", Verbs.PinIssue, item.Target, item.Pinned ? [Verbs.PinOff] : []);
            if (reply is null)
            {
                Say($"{Label(item)} not {(item.Pinned ? "unpinned" : "pinned")}: the node did not answer", refusal: true);
            }
            else if (reply is { Ok: false })
            {
                Say($"{Label(item)} not {(item.Pinned ? "unpinned" : "pinned")}: {reply.Error}", refusal: true);
            }
        }
        finally
        {
            End(busy);
        }
    }

    private readonly HashSet<string> _priorityOpen = [];

    private void TogglePriority(string key)
    {
        if (!_priorityOpen.Remove(key))
        {
            _priorityOpen.Add(key);
        }
        Changed?.Invoke();
    }

    /// <summary>The code of the flight that flies the item: the node's own row, or a live flight on this screen that holds its issue (a flight just made shows at once).</summary>
    private string? FlightCodeOf(BoardItem item) =>
        item.Flight ?? _screen.Flights.FirstOrDefault(f => f.Tracker == TrackerKind.GitHub && f.Issue == item.Number && f.State != FlightState.Landed
            && string.Equals(f.Repo, item.Repo, StringComparison.OrdinalIgnoreCase))?.Code;

    /// <summary>Sets the item's Priority field through the node (null clears it); a press again while it runs sends nothing.</summary>
    internal async Task PriorityAsync(BoardItem item, IssuePriority? level)
    {
        var busy = "priority:" + item.Key;
        if (Send is null || !Begin(busy))
        {
            return;
        }
        Changed?.Invoke();
        try
        {
            var reply = await RunAsync("prioritising:" + item.Key, "ideas:board:" + Verbs.SetPriority + ":" + item.Target, "setting the priority", Verbs.SetPriority, item.Target, [level is { } set ? set.ToString().ToLowerInvariant() : "none"]);
            if (reply is null)
            {
                Say($"{Label(item)} priority not set: the node did not answer", refusal: true);
            }
            else if (reply is { Ok: false })
            {
                Say($"{Label(item)} priority not set: {reply.Error}", refusal: true);
            }
        }
        finally
        {
            End(busy);
        }
    }

    /// <summary>Asks the node where the item belongs: an open milestone or a new one, which nothing moves into until it is accepted.</summary>
    internal Task SuggestAsync(BoardItem item) => AskAsync("suggest:" + item.Key, [(item.Repo, item.Target)], Label(item));

    /// <summary>Asks the node where each item of the section belongs, once for each repo its items are in.</summary>
    internal Task SuggestSectionAsync(BoardSection section) =>
        AskAsync("suggest:" + section.Key, [.. section.Repos.Select(repo => (repo, IssueIds.SectionTarget(repo, section.Kind == BoardSectionKind.Unsorted ? Verbs.SectionUnsorted : Verbs.NoMilestone)))], section.Title);

    private async Task AskAsync(string busy, IEnumerable<(string Repo, string Target)> asks, string what)
    {
        if (Send is null || !Begin(busy))
        {
            return;
        }
        Changed?.Invoke();
        try
        {
            foreach (var (repo, target) in asks)
            {
                var reply = await RunAsync(Verbs.SuggestMilestones, target, []);
                if (reply is null)
                {
                    continue;
                }
                if (!reply.Ok)
                {
                    Say($"no milestone suggested for {what}: {reply.Error}", refusal: true);
                    continue;
                }
                Keep(repo, reply.Suggestions);
            }
        }
        finally
        {
            End(busy);
        }
    }

    private void Keep(string repo, MilestoneSuggestions? answer)
    {
        if (answer is null)
        {
            return;
        }
        foreach (var item in answer.Items)
        {
            ImmutableInterlocked.Update(ref _suggestions, held => held.SetItem(BoardItem.KeyOf(repo, item.Number), item));
        }
        ImmutableInterlocked.Update(ref _groups, held => held.SetItem(repo.ToLowerInvariant(),
            [.. (held.GetValueOrDefault(repo.ToLowerInvariant()) ?? []).Where(g => answer.New.All(n => !string.Equals(n.Title, g.Title, StringComparison.OrdinalIgnoreCase))), .. answer.New]));
        if (answer.Note is { Length: > 0 } note)
        {
            Say(note, refusal: answer.Items.Length == 0);
        }
    }

    /// <summary>Accepts the node's suggestion for the item: the node makes the milestone if it is new, and moves the issue; the row follows the node's read.</summary>
    internal async Task AcceptAsync(BoardItem item)
    {
        var busy = "accept:" + item.Key;
        if (!_suggestions.TryGetValue(item.Key, out var suggestion) || !Actionable(suggestion) || Send is null || !Begin(busy))
        {
            return;
        }
        Changed?.Invoke();
        try
        {
            var reply = await RunAsync(Verbs.AcceptMilestones, item.Target, []);
            if (reply is null)
            {
                return;
            }
            if (reply.Ok)
            {
                ImmutableInterlocked.TryRemove(ref _suggestions, item.Key, out _);
            }
            Say(reply.Ok ? $"the node moved {Label(item)} to {Where(suggestion)}" : $"{Label(item)} not moved: {reply.Error}", !reply.Ok);
        }
        finally
        {
            End(busy);
        }
    }

    /// <summary>Accepts every suggestion still held for the section's items, the ones declined left out.</summary>
    internal async Task AcceptAllAsync(BoardSection section)
    {
        var busy = "accept-all:" + section.Key;
        if (Send is null || !Begin(busy))
        {
            return;
        }
        Changed?.Invoke();
        try
        {
            foreach (var repo in section.Repos)
            {
                var items = section.Items.Where(i => string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase) && _suggestions.TryGetValue(i.Key, out var s) && Actionable(s)).ToList();
                if (items.Count == 0)
                {
                    continue;
                }
                var reply = await RunAsync(Verbs.AcceptMilestones, Verbs.RepoTarget(repo), [.. items.Select(i => i.Number).Order().Select(n => n.ToString(CultureInfo.InvariantCulture))]);
                if (reply is null)
                {
                    continue;
                }
                if (reply.Ok)
                {
                    ImmutableInterlocked.Update(ref _suggestions, held => held.RemoveRange(items.Select(i => i.Key)));
                }
                Say(reply.Ok ? $"the node moved {items.Count} {(items.Count == 1 ? "issue" : "issues")} of {repo}" : $"not all moved: {reply.Error}", !reply.Ok);
            }
        }
        finally
        {
            End(busy);
        }
    }

    /// <summary>Drops the node's suggestion for the item; nothing is sent, and the item is left out of Accept all.</summary>
    internal void Decline(BoardItem item)
    {
        ImmutableInterlocked.TryRemove(ref _suggestions, item.Key, out _);
        Changed?.Invoke();
    }

    /// <summary>
    /// Asks the node to move the item into the milestone <paramref name="to"/> (or out of every milestone). The row stays where it is until the
    /// node's read of the repo says it moved: what is shown meanwhile is that the node was asked, and then what it answered.
    /// </summary>
    internal async Task MoveToAsync(BoardItem item, BoardSection to)
    {
        if (Send is null || !to.Accepts || _board.SectionOf(item.Key)?.Key == to.Key)
        {
            return;
        }
        if (to.Kind == BoardSectionKind.Milestone && !string.Equals(to.Repo, item.Repo, StringComparison.OrdinalIgnoreCase))
        {
            Say($"{Label(item)} is an issue of {item.Repo}, and {to.Title} is a milestone of {to.Repo}", refusal: true);
            return;
        }
        var busy = "move:" + item.Key;
        if (!Begin(busy))
        {
            return;
        }
        Say($"asking the node to move {Label(item)} to {to.Title}…", refusal: false);
        try
        {
            var reply = await RunAsync(Verbs.SetMilestone, item.Target, [to.Kind == BoardSectionKind.NoMilestone ? Verbs.NoMilestone : to.Title, item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)]);
            if (reply is not null)
            {
                Say(reply.Ok ? $"the node moved {Label(item)} to {to.Title}" : $"{Label(item)} not moved: {reply.Error}", !reply.Ok);
            }
        }
        finally
        {
            End(busy);
        }
    }
}
