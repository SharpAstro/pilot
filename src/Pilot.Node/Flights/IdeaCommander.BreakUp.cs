using System.Globalization;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The verbs that split an open issue: <c>break-up-issue</c> asks Claude and writes nothing to GitHub, <c>accept-break-up</c> writes the kept proposal:
/// the first part over the original, the rest as new issues in its milestone with its priority, each body naming the siblings.
/// </summary>
public sealed partial class IdeaCommander
{
    // By repo name (lower case) and then issue number: what the last break-up proposed, until it is accepted or proposed again.
    private readonly Dictionary<string, Dictionary<int, BreakUpProposal>> _keptBreakUps = [];

    private static string Num(int n) => "#" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>The body of one part: its text, then its open questions as a list.</summary>
    private static string PartBody(BreakUpPart part)
    {
        var body = new StringBuilder(part.Body.Trim());
        if (part.OpenQuestions.Length > 0)
        {
            body.Append(body.Length > 0 ? "\n\n" : "").Append("Open questions:");
            foreach (var question in part.OpenQuestions)
            {
                body.Append("\n- ").Append(question);
            }
        }
        return body.ToString();
    }

    /// <summary><paramref name="body"/> with the line that names where the issue was split from and its siblings.</summary>
    private static string WithSiblings(string body, int origin, IEnumerable<int> siblings)
    {
        var line = $"Split from {Num(origin)}; siblings {string.Join(", ", siblings.Select(Num))}";
        return body.Length > 0 ? body + "\n\n" + line : line;
    }

    private string? FlyingNow(string name, int number) =>
        IssueRows.FlightOf([.. state.Flights().Where(f => f.State != FlightState.Landed)], name, number);

    /// <summary><c>break-up-issue issue/&lt;repo&gt;/#&lt;n&gt; [N]</c>: Claude's proposal, kept for <see cref="AcceptBreakUpAsync"/>. Nothing is written.</summary>
    private async Task<CommandResult> BreakUpIssueAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (name, path, number) = await IssueTargetAsync(command, cancellationToken);
        var issue = await OpenIssueAsync(name, path, number, cancellationToken);
        if (FlyingNow(name, number) is { } flight)
        {
            throw new FlightException($"{Num(number)} of {name} is being flown as {flight}: it is not broken up until that flight has landed");
        }
        int? into = null;
        if ((command.Args ?? []).FirstOrDefault() is { } given)
        {
            into = int.TryParse(given.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 2
                ? n
                : throw new FlightException($"break-up-issue splits into two or more parts, not {given}");
        }
        var parts = splitter is null
            ? throw new FlightException("this node has no one to break an issue up with")
            : await splitter.SplitAsync(new IdeaText(issue.Title, issue.Body), into, path, cancellationToken);
        if (parts.Length < 2)
        {
            throw new FlightException("Claude proposed fewer than two parts, so there is nothing to break up");
        }
        var proposal = new BreakUpProposal(parts, issue.Title, issue.Body, issue.UpdatedAt, issue.Milestone, issue.Priority?.ToString());
        lock (_keptGate)
        {
            var key = name.ToLowerInvariant();
            (_keptBreakUps.TryGetValue(key, out var held) ? held : _keptBreakUps[key] = [])[number] = proposal;
        }
        return state.Complete(command, null, made: $"{name} {Num(number)}", breakUp: proposal);
    }

    /// <summary><c>accept-break-up issue/&lt;repo&gt;/#&lt;n&gt; &lt;updated_at&gt;</c>: the kept proposal written; the numbers of the new issues are in <c>made</c>.</summary>
    private async Task<CommandResult> AcceptBreakUpAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (name, path, number) = await IssueTargetAsync(command, cancellationToken);
        BreakUpProposal kept;
        lock (_keptGate)
        {
            kept = _keptBreakUps.TryGetValue(name.ToLowerInvariant(), out var held) && held.TryGetValue(number, out var one)
                ? one
                : throw new FlightException($"no break-up is kept for {Num(number)} of {name}: break-up-issue first");
        }
        if (!DateTimeOffset.TryParse((command.Args ?? []).FirstOrDefault()?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var seen))
        {
            throw new FlightException("accept-break-up takes the updated_at the proposal was made from");
        }
        var now = await OpenIssueAsync(name, path, number, cancellationToken);
        if (seen != kept.UpdatedAt || now.UpdatedAt != kept.UpdatedAt)
        {
            throw new FlightException($"{Num(number)} of {name} changed on GitHub since the proposal was made: break it up again");
        }
        if (FlyingNow(name, number) is { } flight)
        {
            throw new FlightException($"{Num(number)} of {name} is being flown as {flight}: it is not broken up until that flight has landed");
        }
        var parts = kept.Parts;
        var hasField = now.Priority is not null && (await github.IssuePriorityAsync(path, number, cancellationToken)).HasField;

        // The others are made first, so the original is only rewritten once they exist.
        var made = new List<MilestoneIssue>();
        try
        {
            foreach (var part in parts.Skip(1))
            {
                var body = PartBody(part);
                MilestoneIssue issue;
                if (now.Milestone is { } milestone)
                {
                    issue = await github.CreateIssueInMilestoneAsync(path, part.Title, body, milestone, cancellationToken);
                }
                else
                {
                    var info = await github.CreateIssueAsync(path, part.Title, body, cancellationToken);
                    issue = await github.IssueNowAsync(path, info.Number, cancellationToken)
                        ?? new MilestoneIssue(info.Number, part.Title, body, true, null, now.UpdatedAt, now.UpdatedAt, "", "");
                }
                made.Add(issue);
                if (hasField)
                {
                    await github.SetIssuePriorityAsync(path, issue.Number, now.Priority, cancellationToken);
                }
            }
        }
        catch (FlightException ex)
        {
            await RefreshBoardAsync(name, path, cancellationToken);
            throw new FlightException($"{(made.Count > 0 ? $"made {string.Join(", ", made.Select(m => Num(m.Number)))} but " : "")}the break-up stopped: {ex.Message}; {Num(number)} is untouched");
        }
        var numbers = made.Select(m => m.Number).ToArray();
        if (!await github.EditIssueAsync(path, number, parts[0].Title, WithSiblings(PartBody(parts[0]), number, numbers), now.UpdatedAt, cancellationToken))
        {
            await RefreshBoardAsync(name, path, cancellationToken);
            throw new FlightException($"{Num(number)} of {name} changed while it was broken up; {string.Join(", ", numbers.Select(Num))} were made, so edit {Num(number)} by hand");
        }
        var failed = new List<string>();
        for (var i = 0; i < made.Count; i++)
        {
            var mine = made[i].Number;
            var siblings = new[] { number }.Concat(numbers.Where(n => n != mine));
            var fresh = await github.IssueNowAsync(path, mine, cancellationToken) ?? made[i];
            try
            {
                if (!await github.EditIssueAsync(path, mine, parts[i + 1].Title, WithSiblings(PartBody(parts[i + 1]), number, siblings), fresh.UpdatedAt, cancellationToken))
                {
                    failed.Add(Num(mine));
                }
            }
            catch (FlightException)
            {
                failed.Add(Num(mine));
            }
        }
        lock (_keptGate)
        {
            if (_keptBreakUps.TryGetValue(name.ToLowerInvariant(), out var held))
            {
                held.Remove(number);
            }
        }
        await RefreshBoardAsync(name, path, cancellationToken);
        return failed.Count > 0
            ? throw new FlightException($"{Num(number)} is broken up into {string.Join(", ", numbers.Select(Num))}, but the sibling line is missing in {string.Join(", ", failed)}")
            : state.Complete(command, null, made: string.Join(' ', numbers.Select(Num)));
    }
}
