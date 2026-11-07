using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

public sealed partial class NodeState
{
    /// <summary>The cards, newest first; open ones only when <paramref name="openOnly"/>.</summary>
    public CardRow[] Cards(bool openOnly = false)
    {
        lock (_gate)
        {
            return [.. _cards.Values.Where(c => !openOnly || c.State == CardState.Open).OrderByDescending(c => c.RungAt).ThenByDescending(c => c.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Raises a card, numbered by the node; returns it. A card of the same kind about the same subject still open is
    /// that card, not a second one: one event, one card. A Handled card is born closed, a line and nothing to answer.
    /// </summary>
    /// <param name="link">The page the card is about (a flight's PR), opened from the card.</param>
    /// <param name="attachment">The id of the image the node keeps for the card, with <paramref name="attachmentExt"/> its extension.</param>
    /// <param name="askedBy">The assistant's session an ask card is about (<see cref="CardKinds.AssistantAsk"/>), and <paramref name="why"/> the message it acted on.</param>
    public CardRow RaiseCard(CardBin bin, string kind, string subject, string title, string? detail = null, IReadOnlyList<CardChoice>? choices = null, string? link = null, string? askedBy = null, string? why = null, string? summary = null, string? attachment = null, string? attachmentExt = null)
    {
        lock (_gate)
        {
            if (bin != CardBin.Handled && _cards.Values.FirstOrDefault(c => c.State == CardState.Open && c.Kind == kind && c.Subject == subject) is { } open)
            {
                return open;
            }
            var now = _time.GetUtcNow();
            // A landed flight (a folded or moved one lands too) has nothing left to ring about: a leg or worker card for it is a card
            // about a cause already settled (#799 rule 2, row c3651). It is not stored or published; the caller gets the card it would
            // have raised, closed. Said nowhere else: a Handled line per look of the watch would only be noise.
            if (bin != CardBin.Handled && LegFlightOf(subject) is { } landedCode && _flights.TryGetValue(landedCode, out var landed) && landed.State == FlightState.Landed)
            {
                return new CardRow
                {
                    Id = "c0", Bin = CardBin.Handled, Kind = kind, Subject = subject, Title = title, Detail = detail, Choices = [], At = now,
                    State = CardState.Closed, Outcome = $"{landedCode} landed already", ClosedBy = "node", ClosedAt = now,
                };
            }
            var number = _cards.Count == 0 ? 1 : _cards.Keys.Max(k => int.TryParse(k.AsSpan(1), out var n) ? n : 0) + 1;
            // One PR, one merge card: a pr-ready card replaces an older open merge card of the same subject, however it got there.
            if (bin != CardBin.Handled && kind == LegCommander.PrReady)
            {
                foreach (var older in _cards.Values.Where(c => c.State == CardState.Open && IsMergeKind(c.Kind) && c.Subject == subject).ToList())
                {
                    Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = older with { State = CardState.Closed, Outcome = $"replaced by c{number}", ClosedBy = "node", ClosedAt = now, Answering = null } });
                }
            }
            var card = new CardRow
            {
                Id = $"c{number}",
                Bin = bin,
                Kind = kind,
                Subject = subject,
                Title = title,
                Detail = detail,
                Summary = CardSummaries.Resolve(summary, detail, kind, subject, title, choices ?? []),
                // A choice the node would refuse already (its flight is held) is born marked, not offered until the next sweep (#792).
                Choices = Available(choices ?? []),
                Link = link,
                AskedBy = askedBy,
                Why = why,
                Attachment = attachment,
                AttachmentExt = attachment is null ? null : attachmentExt,
                At = now,
                State = bin == CardBin.Handled ? CardState.Closed : CardState.Open,
                Outcome = bin == CardBin.Handled ? "handled" : null,
                ClosedBy = bin == CardBin.Handled ? "node" : null,
                ClosedAt = bin == CardBin.Handled ? now : null,
            };
            card = card with { Commits = CommitsOf(card, link) };
            Publish(new CardAddedEvent { Seq = _seq + 1, At = now, Card = card });
            return card;
        }
    }

    /// <summary>The flight code of a subject naming a leg or anything under one (<c>flight/&lt;code&gt;/&lt;leg&gt;</c>); null for a flight's own subject or any other.</summary>
    private static string? LegFlightOf(string subject)
    {
        if (!subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var parts = subject[Verbs.FlightTargetPrefix.Length..].Split('/');
        return parts.Length >= 2 && parts[0].Length > 0 ? parts[0] : null;
    }

    /// <summary>
    /// Rings an open card again where it is (#799 rule 5): one <see cref="CardChangedEvent"/> with <see cref="CardRow.RungAgainAt"/> set to
    /// now, so it sorts first again on every client and pushes as a change. Its id, kind, choices and <see cref="CardRow.At"/> stay, so
    /// no second card says what the first already did. False when it is not open.
    /// </summary>
    public bool RingAgain(string id)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return false;
            }
            var now = _time.GetUtcNow();
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { RungAgainAt = now } });
            return true;
        }
    }

    /// <summary>Closes an open card with what became of it; false when it is not open (closed already, or no such card).</summary>
    public bool CloseCard(string id, string outcome, string closedBy)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return false;
            }
            var now = _time.GetUtcNow();
            // The answer underway ends with the card: what it came to is the outcome now.
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { State = CardState.Closed, Outcome = outcome, ClosedBy = closedBy, ClosedAt = now, Answering = null } });
            return true;
        }
    }

    /// <summary>
    /// Takes the answer to an open card, atomically, so two presses that race cannot both run (#792): the card then says, on every client,
    /// that this choice is underway, by whom and since when (one <see cref="CardChangedEvent"/>). Taken is null when it was not taken:
    /// another answer to the card is underway and younger than <paramref name="stuckAfter"/> (Underway names it, and the card is left as it
    /// is), or the card is not open (both null). An answer underway for longer is stuck, so it no longer shields the card: the new
    /// one takes its place, and the old one's clearing of the mark then finds it is not its own and leaves it.
    /// </summary>
    public (CardAnswering? Taken, CardAnswering? Underway) BeginAnswering(string id, string choice, string by, string? waiting, TimeSpan stuckAfter)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return (null, null);
            }
            var now = _time.GetUtcNow();
            if (card.Answering is { } underway && now - underway.At < stuckAfter)
            {
                return (null, underway);
            }
            var taken = new CardAnswering { Choice = choice, By = by, At = now, Waiting = waiting };
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { Answering = taken } });
            return (taken, null);
        }
    }

    /// <summary>
    /// Ends an answer that did not close the card (it was refused, or cut off): the card says nothing is underway and offers its buttons
    /// again, one <see cref="CardChangedEvent"/>. Only the answer given is cleared, never a newer one that took the card after it went stale.
    /// </summary>
    public bool ClearAnswering(string id, CardAnswering answering)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.Answering is not { } current || current.Choice != answering.Choice || current.By != answering.By || current.At != answering.At)
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Answering = null } });
            return true;
        }
    }

    /// <summary>
    /// Gives an open card new choices (a merge's checks turned green, say); false when it is not open or nothing changed. A choice the node
    /// would refuse now (its flight held, its session gone) reads <see cref="ChoiceStatus.Unavailable"/> whatever the caller gave, so a
    /// refresh of a card's statuses never offers it again (#792); a choice that would not be refused no longer carries that mark.
    /// </summary>
    public bool UpdateChoices(string id, IReadOnlyList<CardChoice> choices, IReadOnlyList<CardChoice>? ifStill = null)
    {
        lock (_gate)
        {
            // With ifStill, a compare-and-set: written only while the card still holds the choices the caller read, so a press
            // that armed the merge between that read and this write is not undone.
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return false;
            }
            var next = Available(choices);
            if (card.Choices.SequenceEqual(next) || ifStill is not null && !card.Choices.SequenceEqual(ifStill))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Choices = next } });
            return true;
        }
    }

    /// <summary>
    /// Turns an open card into what its thing has become (a merge armed, then failed on one check), keeping its place and id:
    /// one <see cref="CardChangedEvent"/>, none when nothing changed. A <paramref name="kind"/> given is the card's new kind.
    /// False when the card is not open or nothing changed.
    /// </summary>
    public bool UpdateCard(string id, string title, string? detail, IReadOnlyList<CardChoice> choices, string? link, string? kind = null, string? summary = null, CardBin? bin = null)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return false;
            }
            // An open card may move between Ring and For-you; never to or from Handled, which is born closed.
            var nextBin = bin is { } b && b != CardBin.Handled && card.Bin != CardBin.Handled ? b : card.Bin;
            var next = card with { Title = title, Detail = detail, Choices = Available(choices), Link = link, Kind = kind ?? card.Kind, Bin = nextBin };
            // A card that says something new says it in new words: the summary follows the title and kind, as the raiser wrote it or the table has it.
            next = next with { Summary = summary is null && next.Title == card.Title && next.Kind == card.Kind ? card.Summary : CardSummaries.Resolve(summary, detail, next.Kind, next.Subject, next.Title, next.Choices) };
            next = next with { Commits = CommitsOf(next, link) };
            if (next.Bin == card.Bin && next.Commits.Count == card.Commits.Count && next.Commits.All(c => card.Commits.TryGetValue(c.Key, out var u) && u == c.Value) && next.Title == card.Title && next.Detail == card.Detail && next.Link == card.Link && next.Kind == card.Kind && next.Summary == card.Summary
                && next.Choices.SequenceEqual(card.Choices, EqualityComparer<CardChoice>.Create((a, b) => a! with { Args = [] } == b! with { Args = [] } && a.Args.SequenceEqual(b.Args))))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = next });
            return true;
        }
    }

    /// <summary>
    /// Marks the Merge choice of every card about <paramref name="subject"/> that offers one, open or closed already, as merged
    /// (<see cref="ChoiceStatus.Merged"/>): the card reads Merged on every client for the few seconds it lingers, as the answer that
    /// merged it closes it, or the landing does. One <see cref="CardChangedEvent"/> per card changed.
    /// </summary>
    public void MarkMerged(string subject)
    {
        lock (_gate)
        {
            foreach (var card in _cards.Values.Where(c => c.Subject == subject && c.Choices.Any(ch => ch.Verb == Verbs.Merge && ch.Status != ChoiceStatus.Merged)).ToList())
            {
                // Closed within the last minute only: an older card's Merge says what it said when it closed.
                if (card.State == CardState.Closed && (card.ClosedAt is not { } at || _time.GetUtcNow() - at > TimeSpan.FromMinutes(1)))
                {
                    continue;
                }
                Publish(new CardChangedEvent
                {
                    Seq = _seq + 1, At = _time.GetUtcNow(),
                    Card = card with { Choices = [.. card.Choices.Select(ch => ch.Verb == Verbs.Merge ? ch with { Status = ChoiceStatus.Merged, Note = null } : ch)] },
                });
            }
        }
    }

    /// <summary>
    /// Says what the choice a card closed with came to (a review posted, by its id), and where to see it; nothing when the
    /// card is not closed. A <paramref name="closedBy"/> given replaces who closed it: an answer's presser, when the landing closed the card first.
    /// </summary>
    public void NoteOutcome(string id, string outcome, string? link = null, string? closedBy = null)
    {
        lock (_gate)
        {
            if (_cards.TryGetValue(id, out var card) && card.State == CardState.Closed)
            {
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Outcome = outcome, Link = link ?? card.Link, ClosedBy = closedBy ?? card.ClosedBy } });
            }
        }
    }

    /// <summary>Adds <paramref name="who"/> to the sessions an open ask card tells what became of it; false when it is not open or they are on it.</summary>
    public bool AddAsker(string id, string who)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open
                || string.Equals(card.AskedBy, who, StringComparison.OrdinalIgnoreCase) || card.AlsoAskedBy.Contains(who, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { AlsoAskedBy = [.. card.AlsoAskedBy, who] } });
            return true;
        }
    }

    private static bool IsMergeKind(string kind) => kind is LegCommander.PrReady or LegCommander.MergeFailed;

    /// <summary>Opens a closed card again, as it was before: an answer whose choice was refused.</summary>
    public void ReopenCard(string id)
    {
        lock (_gate)
        {
            if (_cards.TryGetValue(id, out var card) && card.State == CardState.Closed)
            {
                // A newer open merge card of the same flight is the one card now: the older stays closed.
                if (IsMergeKind(card.Kind) && _cards.Values.Any(c => c.State == CardState.Open && c.Id != id && IsMergeKind(c.Kind) && c.Subject == card.Subject))
                {
                    return;
                }
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { State = CardState.Open, Outcome = null, ClosedBy = null, ClosedAt = null, Answering = null } });
            }
        }
    }

    /// <summary>
    /// Closes every open card about <paramref name="subject"/> or anything under it (a flight's cards include its legs'),
    /// of the given kinds or all: its thing is done, so its card leaves.
    /// </summary>
    public void CloseCardsAbout(string subject, string outcome, params string[] kinds)
        => CloseCardsAboutBy("node", subject, outcome, kinds);

    /// <summary>As above, closed by <paramref name="closedBy"/> (an override names its session, not the node).</summary>
    public void CloseCardsAboutBy(string closedBy, string subject, string outcome, params string[] kinds)
    {
        lock (_gate)
        {
            foreach (var card in _cards.Values.Where(c => c.State == CardState.Open
                && (c.Subject == subject || c.Subject.StartsWith(subject + "/", StringComparison.Ordinal))
                && (kinds.Length == 0 || kinds.Contains(c.Kind))).ToList())
            {
                var now = _time.GetUtcNow();
                Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { State = CardState.Closed, Outcome = outcome, ClosedBy = closedBy, ClosedAt = now, Answering = null } });
            }
        }
    }

    /// <summary>The commits a card's text names (title, detail, summary, choice notes), each with its commit page: in the repo of the card's link, of its PR, or of its flight's PR (<see cref="CommitRefs"/>).</summary>
    private Dictionary<string, string> CommitsOf(CardRow card, string? link)
    {
        var repo = CommitRefs.RepoOf(link ?? card.Link);
        if (repo is null && card.Subject.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal) && card.Subject.LastIndexOf('#') is > 0 and var hash)
        {
            var named = card.Subject[Verbs.PrTargetPrefix.Length..hash];
            repo = named.Contains('/') ? named : null;
        }
        if (repo is null && card.Subject.StartsWith("flight/", StringComparison.Ordinal))
        {
            var code = card.Subject["flight/".Length..].Split('/')[0];
            repo = _flights.TryGetValue(code, out var flight) ? CommitRefs.RepoOf(flight.PullRequestUrl) : null;
        }
        return CommitRefs.Named(repo, [card.Title, card.Detail, card.Summary, .. card.Choices.Select(c => c.Label), .. card.Choices.Select(c => c.Note)]);
    }
}
