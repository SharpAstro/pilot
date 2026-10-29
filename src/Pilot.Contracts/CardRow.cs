using System.Text.Json.Serialization;

namespace Pilot.Contracts;

/// <summary>
/// One card: an event that needs a person, or that the watchtower handled, with its choices in one place (after Sam's
/// campaigner: one event, one card). A card leaves when its thing is done: answered, dismissed, or closed by the node
/// when its flight lands or its leg finishes, so nothing sits unread forever. State alone is not a card: a session that
/// went quiet changes how its row looks, nothing more, unless it is a flying leg's worker, whose flight it stalls.
/// </summary>
public sealed record CardRow
{
    /// <summary>The card's id, <c>c&lt;n&gt;</c>, unique on the node for good.</summary>
    public required string Id { get; init; }

    public required CardBin Bin { get; init; }

    /// <summary>What happened, as a word a client can match: <c>pr-ready</c>, <c>leg-failed</c>, <c>landed</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>What it is about, as a target: <c>flight/TIA1611</c>, <c>flight/TIA1611/parser</c>, <c>session/a1b2c3d4</c>.</summary>
    public required string Subject { get; init; }

    /// <summary>The card in a line: <c>TIA1611's PR #63 is ready to merge</c>.</summary>
    public required string Title { get; init; }

    /// <summary>What else the person needs to decide, in a few lines; null when the title says it all. The raw text, a log if need be.</summary>
    public string? Detail { get; init; }

    /// <summary>One or two plain sentences: what happened and what the choices do, no log. Detail stays the raw text; null only on a card an older node wrote.</summary>
    public string? Summary { get; init; }

    /// <summary>What can be done about it, each a command the node runs when the card is answered with it.</summary>
    public CardChoice[] Choices { get; init => field = value ?? []; } = [];

    /// <summary>The page the card is about, one click away on every client: a flight's PR; null when there is none.</summary>
    public string? Link { get; init; }

    public required DateTimeOffset At { get; init; }

    public required CardState State { get; init; }

    /// <summary>How it was closed: the choice taken, <c>dismissed</c>, or what closed it (<c>TIA1611 landed</c>).</summary>
    public string? Outcome { get; init; }

    /// <summary>Who closed it: a client, a session's name, or <c>node</c>.</summary>
    public string? ClosedBy { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>The assistant's session that asked for what the card confirms (<see cref="CardKinds.AssistantAsk"/>), told what became of it; null for any other card.</summary>
    public string? AskedBy { get; init; }

    /// <summary>Other sessions that asked the same thing while the card was open: told what became of it as <see cref="AskedBy"/> is.</summary>
    public string[] AlsoAskedBy { get; init => field = value ?? []; } = [];

    /// <summary>The message that assistant said it acted on (<see cref="CommandRequest.Why"/>), when it gave one.</summary>
    public string? Why { get; init; }
}

/// <summary>A card's button: what it says, and the command it runs.</summary>
public sealed record CardChoice
{
    public required string Label { get; init; }

    public required string Verb { get; init; }

    public required string Target { get; init; }

    public string[] Args { get; init => field = value ?? []; } = [];

    /// <summary>
    /// How ready the choice is, where the node can tell: a merge's PR checks, <c>passing</c>, <c>pending</c> or <c>failing</c>,
    /// which a client shows on the button (green, amber, red); null where there is nothing to tell.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// What the status adds, as GitHub has it: <c>6 of 7</c> for checks, <c>position 2</c> in a merge queue, the failed check's
    /// name when blocked. A client shows it after the status's words; null where there is nothing to add.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>Kinds of card a client treats specially, matched by word (<see cref="CardRow.Kind"/>).</summary>
public static class CardKinds
{
    /// <summary>
    /// A session waiting on its person that no leg's worker is: raised by the node with the session as its subject
    /// (<c>session/&lt;id&gt;</c>) and its Remote Control URL, if it has one, as <see cref="CardRow.Link"/>. It closes itself
    /// when the session stops waiting or its process is gone.
    /// </summary>
    public const string SessionWaiting = "session-waiting";

    /// <summary>
    /// A flight found to overlap another (on this node or another): its title names both flights, the files they share and the choice,
    /// and its buttons are Fold into, Connect to and Plan in parallel. Its title and detail are what it is for, so a panel wraps them.
    /// </summary>
    public const string Overlap = "overlap";

    /// <summary>The node's watchtower keeps ending (<see cref="WatchtowerHealth.Churning"/>): one For-you card about it, its text kept up to date, closed when it calms down.</summary>
    public const string WatchtowerChurning = "watchtower-churning";

    /// <summary>
    /// An assistant's your-only command (a merge, a landing, a setting) waiting on the person with the autopilot off (#208): a
    /// For-you card whose <c>Allow</c> runs the command as the person and whose <c>Deny</c> closes it, the asking session
    /// told either way (<see cref="CardRow.AskedBy"/>). Answering it is the person's alone.
    /// </summary>
    public const string AssistantAsk = "assistant-ask";

    /// <summary>
    /// An edit of an idea that contradicts what GitHub has got since (<see cref="Verbs.EditIdea"/>): a For-you card naming the idea
    /// (<c>I381: title</c>) with both versions in its detail, offering <c>Keep mine</c>, <c>Keep GitHub's</c> and <c>Keep the merge</c>
    /// (the default, first). Its subject is the idea's target; nothing is written until it is answered.
    /// </summary>
    public const string IdeaConflict = "idea-conflict";

    /// <summary>
    /// A new idea that fits more than one repo equally (<see cref="Verbs.NoteIdea"/> with <see cref="Verbs.AnyRepo"/>): a For-you card
    /// with one choice per likely repo, <c>File in tianwen</c>, each filing it there. Nothing is created until one is chosen.
    /// </summary>
    public const string IdeaRepo = "idea-repo";

    /// <summary>What an assistant did on the autopilot, as a Handled line: the session, what it did, and the message it acted on.</summary>
    public const string AutopilotRan = "autopilot-ran";
}

/// <summary>How long a thing has gone on, as a card or a banner says it: <c>under 1 m</c>, <c>37 m</c>, <c>9 h 37 m</c>, <c>2 d 3 h</c>.</summary>
public static class Waited
{
    public static string Say(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        return span.TotalMinutes < 1 ? "under 1 m"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} m"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes} m"
            : $"{(int)span.TotalDays} d {span.Hours} h";
    }
}

/// <summary>The <see cref="CardChoice.Status"/> values a merge's checks give.</summary>
public static class ChoiceStatus
{
    public const string Passing = "passing";
    public const string Pending = "pending";
    public const string Failing = "failing";

    /// <summary>The PR conflicts with its base: GitHub cannot merge it until it is rebased, which the node does by itself.</summary>
    public const string Conflicting = "conflicting";

    /// <summary>A merge answered while its checks run, or one that joined a merge queue: it goes through by itself once they pass or the queue takes it.</summary>
    public const string Armed = "armed";

    /// <summary>The PR is in its base's merge queue (<see cref="CardChoice.Note"/> says its position): the queue merges it, nothing to press.</summary>
    public const string Queued = "queued";

    /// <summary>The PR is behind its base and GitHub will not merge it as it is: the node brings it up to date (merging the base in, or rebasing, as the repo's merge method wants), so the card offers that, not Merge.</summary>
    public const string Behind = "behind";

    /// <summary>GitHub blocks the merge (a required check failed, a review is required, the PR is a draft): <see cref="CardChoice.Note"/> says which. Not a button.</summary>
    public const string Blocked = "blocked";

    /// <summary>A review on GitHub asks for changes. Not a button.</summary>
    public const string ChangesRequested = "changes-requested";

    /// <summary>The PR merged: the card reads Merged for a few seconds, then goes.</summary>
    public const string Merged = "merged";
}

/// <summary>
/// How far an armed merge's checks are, said in one form for the node to write into a card's detail and for a client to read
/// back for the Merge button (<c>Merging when checks pass · 6 of 7</c>): the format lives here, in one place.
/// </summary>
public static partial class ArmedProgress
{
    /// <summary><c>6 of 7</c>.</summary>
    public static string Say(int done, int total) => $"{done} of {total}";

    /// <summary>The <c>6 of 7</c> a card's detail says, or null when it says none (a re-run's detail, a card that is not armed).</summary>
    public static string? Read(string? detail) =>
        detail is { Length: > 0 } && Progress().Match(detail) is { Success: true } m ? Say(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(\d{1,4}) of (\d{1,4})\b")]
    private static partial System.Text.RegularExpressions.Regex Progress();
}

/// <summary>Which of the bins a card is in, and so how it reaches the controller.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardBin>))]
public enum CardBin
{
    /// <summary>Pushed, and waits until someone acts: a stop the watchtower may not resolve, a gate before an irreversible step, a PR to merge.</summary>
    [JsonStringEnumMemberName("ring")]
    Ring,

    /// <summary>A card and a count, no push: a flight landed, a question a worker answered on its own assumption.</summary>
    [JsonStringEnumMemberName("forYou")]
    ForYou,

    /// <summary>What the watchtower or the node did itself: one line, born closed, never a count.</summary>
    [JsonStringEnumMemberName("handled")]
    Handled,
}

/// <summary>Links a card can carry that a client opens inside itself instead of handing to the system.</summary>
public static class InAppLinks
{
    /// <summary>The Fleet page.</summary>
    public const string Fleet = "pilot:fleet";

    /// <summary>Whether <paramref name="link"/> is one a client opens itself.</summary>
    public static bool Is(string link) => link.StartsWith("pilot:", StringComparison.Ordinal);
}

/// <summary>Whether a card still waits.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CardState>))]
public enum CardState
{
    [JsonStringEnumMemberName("open")]
    Open,

    [JsonStringEnumMemberName("closed")]
    Closed,
}
