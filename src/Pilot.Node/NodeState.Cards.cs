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
    /// The cards of one <paramref name="kind"/> about one <paramref name="subject"/>, open and closed, newest first: what a look that asks
    /// once per cause reads at every pass, without copying and sorting the whole store as <see cref="Cards"/> does.
    /// </summary>
    public CardRow[] CardsOf(string kind, string subject)
    {
        lock (_gate)
        {
            return [.. _cards.Values.Where(c => c.Kind == kind && c.Subject == subject).OrderByDescending(c => c.RungAt).ThenByDescending(c => c.Id, StringComparer.Ordinal)];
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
    public CardRow RaiseCard(CardBin bin, string kind, string subject, string title, string? detail = null, IReadOnlyList<CardChoice>? choices = null, string? link = null, string? askedBy = null, string? why = null, string? summary = null, string? attachment = null, string? attachmentExt = null, string? cause = null, string? aboutCard = null)
    {
        // With the brain to ask (PIL855), a card that would ring is first filed in For you, silent, and rings only on the brain's verdict
        // (RingJudge): the old path, a ring at once, is what runs with no judge, and what the judge itself falls back to.
        // With the switch on (PIL1285), a card bound for the ring waits in For you, quietly, until an investigator decides it; nothing else
        // rings the person. Off, the investigator holds nothing and the path below is as before.
        if (bin == CardBin.Ring && Investigations is { } investigations && investigations.Holds(kind, choices) && NodePresses?.Invoke(kind, subject, detail, choices) != true)
        {
            var waiting = RaiseCardCore(CardBin.ForYou, kind, subject, title, Investigator.HeldLead + detail, choices, link, askedBy, why, summary, attachment, attachmentExt, cause, aboutCard, out var made);
            if (made)
            {
                investigations.Hold(waiting);
            }
            return waiting;
        }
        if (bin != CardBin.Ring || RingJudge is not { CanJudge: true } judge || !RingJudge.Wants(kind, choices) || NodePresses?.Invoke(kind, subject, detail, choices) == true)
        {
            return RaiseCardCore(bin, kind, subject, title, detail, choices, link, askedBy, why, summary, attachment, attachmentExt, cause, aboutCard, out _);
        }
        var card = RaiseCardCore(CardBin.ForYou, kind, subject, title, detail, choices, link, askedBy, why, summary, attachment, attachmentExt, cause, aboutCard, out var created);
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

    /// <summary>The investigator (<see cref="Flights.Investigator"/>, PIL1285): with the switch on it holds a ring until a run decides it; null holds none.</summary>
    public Investigator? Investigations { get; set; }

    /// <summary>The brain's word before a ring (<see cref="Flights.RingJudge"/>); null rings at once, as before.</summary>
    public RingJudge? RingJudge { get; set; }

    /// <summary>
    /// Whether the node presses a card raised with this kind, subject, detail and choices itself now
    /// (<see cref="Flights.NodePress.WouldPress"/>): such a card is never sent to the ring judge.
    /// </summary>
    public Func<string, string, string?, IReadOnlyList<CardChoice>?, bool>? NodePresses { get; set; }

    private CardRow RaiseCardCore(CardBin bin, string kind, string subject, string title, string? detail, IReadOnlyList<CardChoice>? choices, string? link, string? askedBy, string? why, string? summary, string? attachment, string? attachmentExt, string? cause, string? aboutCard, out bool created)
    {
        created = false;
        lock (_gate)
        {
            if (bin != CardBin.Handled && _cards.Values.FirstOrDefault(c => c.State == CardState.Open && c.Kind == kind && c.Subject == subject) is { } open)
            {
                return open;
            }
            // The key a dismissal is remembered by, taken before the hold's words decorate the title: a card dismissed before the hold, one
            // raised during it and one raised after the release all share it, so the hold neither breaks a dismissal nor ends one (#853).
            var key = cause ?? title;
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
            if (bin != CardBin.Handled && _cards.Values.Where(c => IsPersonsDismissal(c) && c.Kind == kind && c.Subject == subject && (c.Cause ?? c.Title) == key
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
            // The cards about a replaced card close once the new card exists (_deferredCloses): a Handled line raised in between would take
            // the id this card is about to take.
            var replaced = new List<CardRow>();
            if (bin != CardBin.Handled && kind == LegCommander.PrReady)
            {
                _deferredCloses = replaced;
                try
                {
                    foreach (var older in _cards.Values.Where(c => c.State == CardState.Open && IsMergeKind(c.Kind) && c.Subject == subject).ToList())
                    {
                        Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = older with { State = CardState.Closed, Outcome = $"replaced by c{number}", ClosedBy = "node", ClosedAt = now, Answering = null } });
                    }
                }
                finally
                {
                    _deferredCloses = null;
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
                Choices = Available(choices ?? [], kind),
                Link = link,
                AskedBy = askedBy,
                Why = why,
                // The key a dismissal is remembered by, fixed when the card is raised: a later rewording of its title (the brain's, PIL855)
                // cannot move it, so a dismissed card raised again for the same cause stays dismissed (#853).
                Cause = key,
                AboutCard = aboutCard,
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
            foreach (var done in replaced)
            {
                CloseCardsAboutClosed(done);
            }
            return card;
        }
    }

    /// <summary>Cards closed while a raise is still to add its own card: the cards about them close after it (PIL1290). Under the lock.</summary>
    private List<CardRow>? _deferredCloses;

    /// <summary>True when the card with <paramref name="id"/> is open (PIL1290: what a raise that names a card links to).</summary>
    public bool IsCardOpen(string id)
    {
        lock (_gate)
        {
            return _cards.TryGetValue(id, out var card) && card.State == CardState.Open;
        }
    }

    /// <summary>
    /// PIL1290: a card closed just now closes the open cards about it (their <see cref="CardRow.AboutCard"/>), each with an outcome naming it
    /// and what became of it, and a Handled line says so. Called from <see cref="Publish"/>, never from a journal replay. Each close is
    /// itself a published close, so a chain of cards unwinds and a cycle ends: a card closes once.
    /// </summary>
    private void CloseCardsAboutClosed(CardRow closed)
    {
        foreach (var about in _cards.Values.Where(c => c.State == CardState.Open && c.AboutCard == closed.Id && c.Id != closed.Id).ToList())
        {
            if (_cards[about.Id].State != CardState.Open)
            {
                continue;
            }
            var now = _time.GetUtcNow();
            var outcome = $"{closed.Id} closed: {closed.Outcome ?? "closed"}";
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = about with { State = CardState.Closed, Outcome = outcome, ClosedBy = "node", ClosedAt = now, Answering = null } });
            RaiseCardCore(CardBin.Handled, CardKinds.NodeRan, Verbs.CardTarget(about.Id), $"{about.Id} closed with {closed.Id}: {closed.Outcome ?? "closed"}",
                $"{about.Id} ({about.Title}) asked about {closed.Id} ({closed.Title}), which was closed: {closed.Outcome ?? "closed"}.", null, null, null, null, null, null, null, null, null, out _);
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
    /// Rings an open card again where it is (#799 rule 5, #797 rule 7): one <see cref="CardChangedEvent"/> with <see cref="CardRow.RungAgainAt"/> set to
    /// now, so it sorts first again on every client and pushes as a change. Its id, kind, choices and <see cref="CardRow.At"/> stay, so
    /// no second card says what the first already did. With <paramref name="waited"/> (how long it has waited, as a person says it) the card's
    /// summary ends in "Waiting 1 h for you."; a second ring replaces that mark. False when it is not open.
    /// </summary>
    public bool RingAgain(string id, string? waited = null)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return false;
            }
            var now = _time.GetUtcNow();
            var summary = card.Summary;
            if (waited is { Length: > 0 })
            {
                var plain = WaitingMark().Replace(summary ?? "", "").TrimEnd();
                summary = plain.Length == 0 ? $"Waiting {waited} for you." : $"{plain} Waiting {waited} for you.";
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { RungAgainAt = now, Summary = summary } });
            return true;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*Waiting [^.]* for you\.$")]
    private static partial System.Text.RegularExpressions.Regex WaitingMark();

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
            var next = Available(choices, card.Kind);
            if (card.Choices.SequenceEqual(next) || ifStill is not null && !card.Choices.SequenceEqual(ifStill))
            {
                return false;
            }
            Publish(new CardChangedEvent { Seq = _seq + 1, At = _time.GetUtcNow(), Card = card with { Choices = next } });
            return true;
        }
    }

    /// <summary>
    /// A flight landed: the open 7-day card (<see cref="StartGate.WeekKind"/>) loses its <c>Hold &lt;code&gt;</c> button for that flight, as
    /// the buttons were fixed when the card was raised and a landed flight has nothing left to hold (the review of #797). The card keeps
    /// every other choice; when no flight button is left it still offers the node-wide one. One <see cref="CardChangedEvent"/> per card changed.
    /// </summary>
    public void DropHoldChoiceOfLanded(string code)
    {
        lock (_gate)
        {
            var target = Verbs.FlightTarget(code);
            foreach (var card in _cards.Values.Where(c => c.State == CardState.Open && c.Kind == StartGate.WeekKind
                && c.Choices.Any(ch => ch.Verb == Verbs.Ground && ch.Target == target)).ToList())
            {
                UpdateCard(card.Id, card.Title, card.Detail, [.. card.Choices.Where(ch => !(ch.Verb == Verbs.Ground && ch.Target == target))], card.Link);
            }
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
            // PIL1290: the words a ring on card/<id> put on the card stay through a rewrite of its detail that does not carry them itself.
            detail = KeepRungWords(card.Detail, detail);
            var next = card with { Title = title, Detail = detail, Choices = Available(choices, kind ?? card.Kind), Link = link, Kind = kind ?? card.Kind, Bin = nextBin };
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

    /// <summary>
    /// Opens a closed card again, as it was before: an answer whose choice was refused. PIL1290: the cards about it that its close closed, and those
    /// that closed with them, as the close unwinds chains (outcome <c>&lt;id&gt; closed: ...</c>, by the node) open again too, since the answer they were closed by never stood, and a Handled
    /// line says so beside the one that said they closed.
    /// </summary>
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
                var now = _time.GetUtcNow();
                Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = card with { State = CardState.Open, Outcome = null, ClosedBy = null, ClosedAt = null, Answering = null } });
                ReopenCardsAbout(card, now);
            }
        }
    }

    // Unwinds the cascade the close of `closed` ran, a chain as the close does: each card it closed opens, then those that closed with that one.
    // A card opens once (only a closed one is read), so a cycle ends.
    private void ReopenCardsAbout(CardRow closed, DateTimeOffset now)
    {
        var cascade = $"{closed.Id} closed: ";
        foreach (var about in _cards.Values.Where(c => c.State == CardState.Closed && c.AboutCard == closed.Id && c.ClosedBy == "node"
            && c.Outcome is { } o && o.StartsWith(cascade, StringComparison.Ordinal)).ToList())
        {
            Publish(new CardChangedEvent { Seq = _seq + 1, At = now, Card = about with { State = CardState.Open, Outcome = null, ClosedBy = null, ClosedAt = null, Answering = null } });
            RaiseCardCore(CardBin.Handled, CardKinds.NodeRan, Verbs.CardTarget(about.Id), $"{about.Id} open again with {closed.Id}",
                $"{about.Id} ({about.Title}) is open again: the answer on {closed.Id} ({closed.Title}) that closed it was refused.", null, null, null, null, null, null, null, null, null, out _);
            ReopenCardsAbout(about, now);
        }
    }

    /// <summary>What <see cref="PutWordsOnCard"/> did: the words went on, nothing changed (the same words again), or the card is not open.</summary>
    internal enum RungWordsResult { Put, Unchanged, Closed }

    /// <summary>Puts a ring's words on card <paramref name="id"/> (one paragraph per sender), moving it to <paramref name="bin"/> when given. Closed when the card is not open, as when it closed after the caller looked.</summary>
    internal RungWordsResult PutWordsOnCard(string id, string who, string title, string detail, CardBin? bin)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(id, out var card) || card.State != CardState.Open)
            {
                return RungWordsResult.Closed;
            }
            var merged = WithRungWords(card.Detail, who, title, detail);
            return UpdateCard(id, card.Title, merged, card.Choices, card.Link, bin: bin) ? RungWordsResult.Put : RungWordsResult.Unchanged;
        }
    }

    /// <summary>The longest a ring's words on a card may be (PIL1290): a raise on card/&lt;id&gt; is a nudge, not a report.</summary>
    internal const int RungWordsCap = 600;

    // A paragraph a ring on card/<id> put on a card: "From <who>: <title>. <detail>".
    [System.Text.RegularExpressions.GeneratedRegex(@"^From [^\s:]+: ")]
    private static partial System.Text.RegularExpressions.Regex RungWordsLead();

    private const string ParagraphBreak = "\n\n";

    private static IEnumerable<string> Paragraphs(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? [] : detail.Split(ParagraphBreak, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// PIL1290: <paramref name="detail"/> with <paramref name="who"/>'s words from a ring on the card in place of any they said before, so
    /// rings from one sender keep one paragraph on the card rather than piling up; the words are cut to <see cref="RungWordsCap"/>.
    /// </summary>
    internal static string WithRungWords(string? detail, string who, string title, string said)
    {
        var words = $"From {who}: {title}." + (said.Length > 0 ? " " + said : "");
        if (words.Length > RungWordsCap)
        {
            words = words[..(RungWordsCap - 3)].TrimEnd() + "...";
        }
        var kept = Paragraphs(detail).Where(p => !p.StartsWith($"From {who}: ", StringComparison.Ordinal));
        return string.Join(ParagraphBreak, kept.Append(words));
    }

    // A rewrite of a card's detail that carries none of the ring words the card holds keeps them, after its own text.
    private static string? KeepRungWords(string? before, string? after)
    {
        var rung = Paragraphs(before).Where(p => RungWordsLead().IsMatch(p)).ToList();
        if (rung.Count == 0 || Paragraphs(after).Any(p => RungWordsLead().IsMatch(p)))
        {
            return after;
        }
        var rungText = string.Join(ParagraphBreak, rung);
        return string.IsNullOrWhiteSpace(after) ? rungText : after.TrimEnd() + ParagraphBreak + rungText;
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
            foreach (var listed in _cards.Values.Where(c => c.State == CardState.Open
                && (c.Subject == subject || c.Subject.StartsWith(subject + "/", StringComparison.Ordinal))
                && (kinds.Length == 0 || kinds.Contains(c.Kind))).ToList())
            {
                // Re-read: closing an earlier card may have closed this one (a ring about it) already, with its own outcome.
                if (_cards[listed.Id] is not { State: CardState.Open } card)
                {
                    continue;
                }
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
