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
    /// <param name="cause">
    /// What the card is about for a person's dismissal to be remembered (#853): a card of this kind and subject that a person dismissed
    /// is not raised again for the same cause, which is this key, or the title when none is given. Pass a stable key where the title
    /// carries a part that changes for one cause (an age, a count), and put in it what makes a new cause (a PR's head, a go-around's count).
    /// </param>
    /// <param name="askedBy">The assistant's session an ask card is about (<see cref="CardKinds.AssistantAsk"/>), and <paramref name="why"/> the message it acted on.</param>
    public CardRow RaiseCard(CardBin bin, string kind, string subject, string title, string? detail = null, IReadOnlyList<CardChoice>? choices = null, string? link = null, string? askedBy = null, string? why = null, string? summary = null, string? attachment = null, string? attachmentExt = null, string? cause = null)
    {
        // With the brain to ask (PIL855), a card that would ring is first filed in For you, silent, and rings only on the brain's verdict
        // (RingJudge): the old path, a ring at once, is what runs with no judge, and what the judge itself falls back to.
        if (bin != CardBin.Ring || RingJudge is not { CanJudge: true } judge || !RingJudge.Wants(kind, choices))
        {
            return RaiseCardCore(bin, kind, subject, title, detail, choices, link, askedBy, why, summary, attachment, attachmentExt, cause, out _);
        }
        var card = RaiseCardCore(CardBin.ForYou, kind, subject, title, detail, choices, link, askedBy, why, summary, attachment, attachmentExt, cause, out var created);
        // Only a card this call made: an open one of the same kind and subject is returned as it is, and is not judged again.
        if (created)
        {
            judge.Begin(card);
        }
        return card;
    }

    /// <summary>
    /// How long a person's dismissal keeps the same cause from being raised again (#853): a day. Past it the cause rings again if it is
    /// still, or once more, there. Not the stale-card hours (2): a dismissed card back every two hours is the nagging #853 set out to end.
    /// </summary>
    public TimeSpan DismissalHolds { get; set; } = TimeSpan.FromDays(1);

    /// <summary>The brain's word before a ring (<see cref="Flights.RingJudge"/>); null rings at once, as before.</summary>
    public RingJudge? RingJudge { get; set; }

    private CardRow RaiseCardCore(CardBin bin, string kind, string subject, string title, string? detail, IReadOnlyList<CardChoice>? choices, string? link, string? askedBy, string? why, string? summary, string? attachment, string? attachmentExt, string? cause, out bool created)
    {
        created = false;
        lock (_gate)
        {
            if (bin != CardBin.Handled && _cards.Values.FirstOrDefault(c => c.State == CardState.Open && c.Kind == kind && c.Subject == subject) is { } open)
            {
                return open;
            }
            if (bin != CardBin.Handled && OnHoldOf(subject) is { } onHold)
            {
                // The owner's hold (#771): the card says so. Its choices are all kept, never stripped: a card raised once (an overlap's Connect
                // and Plan) is not raised again on the release, so what it offers must still be there; the dispatch gate refuses a button the
                // hold forbids, with the hold's words, and the release takes this note off (LiftHoldFromCards).
                var said = FlightWaiting.OnHoldText(onHold)!;
                title = $"{title} ({said})";
                detail = $"{HeldLead}{said}.{HeldLeadMiddle}{onHold.Code}{HeldLeadEnd}{(detail is { Length: > 0 } ? " " + detail : "")}";
            }
            var now = _time.GetUtcNow();
            // A card a person dismissed for this cause is not raised again by the next look (#853): the caller gets the dismissed card, and
            // nothing is stored, published, rung or said. Read from the cards themselves, so a journal replay or a snapshot restores it.
            // It holds for DismissalHolds after the dismissal: a cause that comes back later (a watchtower that keeps ending again a week on)
            // rings again, and one still there is not nagged about inside the window (PIL855's review).
            if (bin != CardBin.Handled && _cards.Values.Where(c => IsPersonsDismissal(c) && c.Kind == kind && c.Subject == subject && (c.Cause ?? c.Title) == (cause ?? title)
                    && c.ClosedAt is { } closed && now - closed < DismissalHolds)
                .OrderByDescending(c => c.ClosedAt).FirstOrDefault() is { } dismissed)
            {
                return dismissed;
            }
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
                    Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = older with { State = CardState.Closed, Outcome = $"replaced by c{number}", ClosedBy = "node", ClosedAt = now } });
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
                Choices = [.. choices ?? []],
                Link = link,
                AskedBy = askedBy,
                Why = why,
                // The key a dismissal is remembered by, fixed when the card is raised: a later rewording of its title (the brain's, PIL855)
                // cannot move it, so a dismissed card raised again for the same cause stays dismissed (#853).
                Cause = cause ?? title,
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
            created = true;
            return card;
        }
    }

    private const string HeldLead = "The flight is ";
    private const string HeldLeadMiddle = " The node takes no step for it until it is released (pilot flight unground ";
    private const string HeldLeadEnd = "); a choice below that would is refused with the hold's words.";

    /// <summary>
    /// The release of the hold on flight <paramref name="code"/> (#771): the open cards about it that were raised while it was held lose the
    /// note that said so (the on-hold words on their title, the lead of their detail), and keep everything else, choices included. Read
    /// back from the card's own text, so it holds for a card raised before a node restart too.
    /// </summary>
    public void LiftHoldFromCards(string code)
    {
        lock (_gate)
        {
            var subject = Verbs.FlightTarget(code);
            foreach (var card in _cards.Values.Where(c => c.State == CardState.Open && (c.Subject == subject || c.Subject.StartsWith(subject + "/", StringComparison.Ordinal))).ToList())
            {
                if (card.Detail is not { } detail || !detail.StartsWith(HeldLead + "on hold by ", StringComparison.Ordinal))
                {
                    continue;
                }
                var middle = detail.IndexOf(HeldLeadMiddle, StringComparison.Ordinal);
                var end = middle < 0 ? -1 : detail.IndexOf(HeldLeadEnd, middle, StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }
                var said = detail[HeldLead.Length..(middle - 1)];
                var rest = detail[(end + HeldLeadEnd.Length)..].TrimStart();
                var title = card.Title.EndsWith($" ({said})", StringComparison.Ordinal) ? card.Title[..^(said.Length + 3)] : card.Title;
                UpdateCard(card.Id, title, rest.Length > 0 ? rest : null, card.Choices, card.Link);
            }
        }
    }

    /// <summary>The flight on the owner's hold that a card's subject (<c>flight/&lt;code&gt;</c> or <c>flight/&lt;code&gt;/&lt;leg&gt;</c>) is about; null when it is about none, or the flight is not held.</summary>
    private FlightRow? OnHoldOf(string subject)
    {
        if (!subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var code = subject[Verbs.FlightTargetPrefix.Length..].Split('/')[0];
        return _flights.Values.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase) && f.OnHold is not null);
    }

    /// <summary>
    /// A card a person dismissed (<c>dismissed</c> or <c>dismissed: why</c>, closed by a client or a session): not one the node closed
    /// itself, whether its stale sweep (<see cref="StaleCards.ClosedBy"/>) or a landing, which say <c>node</c>.
    /// </summary>
    private static bool IsPersonsDismissal(CardRow card) =>
        card.State == CardState.Closed && card.Outcome is { } outcome && outcome.StartsWith("dismissed", StringComparison.Ordinal)
        && !string.IsNullOrEmpty(card.ClosedBy) && card.ClosedBy != StaleCards.ClosedBy;

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
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { State = CardState.Closed, Outcome = outcome, ClosedBy = closedBy, ClosedAt = now } });
            return true;
        }
    }

    /// <summary>Gives an open card new choices (a merge's checks turned green, say); false when it is not open or nothing changed.</summary>
    public bool UpdateChoices(string id, IReadOnlyList<CardChoice> choices, IReadOnlyList<CardChoice>? ifStill = null)
    {
        lock (_gate)
        {
            // With ifStill, a compare-and-set: written only while the card still holds the choices the caller read, so a press
            // that armed the merge between that read and this write is not undone.
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open || card.Choices.SequenceEqual(choices)
                || ifStill is not null && !card.Choices.SequenceEqual(ifStill))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Choices = [.. choices] } });
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
            var next = card with { Title = title, Detail = detail, Choices = [.. choices], Link = link, Kind = kind ?? card.Kind, Bin = nextBin };
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
    /// card is not closed.
    /// </summary>
    public void NoteOutcome(string id, string outcome, string? link = null)
    {
        lock (_gate)
        {
            if (_cards.TryGetValue(id, out var card) && card.State == CardState.Closed)
            {
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Outcome = outcome, Link = link ?? card.Link } });
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
                Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { State = CardState.Open, Outcome = null, ClosedBy = null, ClosedAt = null } });
            }
        }
    }

    /// <summary>
    /// Closes every open card about <paramref name="subject"/> or anything under it (a flight's cards include its legs'),
    /// of the given kinds or all: its thing is done, so its card leaves.
    /// </summary>
    public void CloseCardsAbout(string subject, string outcome, params string[] kinds)
        => CloseCardsAboutBy("node", subject, outcome, kinds);

    /// <summary>Closes every open card about <paramref name="subject"/> (and its legs) except those of the kinds in <paramref name="keep"/>, which stay open.</summary>
    public void CloseCardsAboutExcept(string subject, string outcome, params string[] keep)
    {
        lock (_gate)
        {
            var kinds = _cards.Values.Where(c => c.State == CardState.Open && (c.Subject == subject || c.Subject.StartsWith(subject + "/", StringComparison.Ordinal)) && !keep.Contains(c.Kind))
                .Select(c => c.Kind).Distinct(StringComparer.Ordinal).ToArray();
            if (kinds.Length > 0)
            {
                CloseCardsAboutBy("node", subject, outcome, kinds);
            }
        }
    }

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
                Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { State = CardState.Closed, Outcome = outcome, ClosedBy = closedBy, ClosedAt = now } });
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
