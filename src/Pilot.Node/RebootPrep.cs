using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// A reboot made ready (#916): <c>pilot node reboot request [why]</c> puts the node in maintenance mode, tells every live session the
/// node started to commit, push and write its state to <c>&lt;data&gt;/reboot/&lt;name&gt;-reboot-&lt;stamp&gt;.md</c>, ends each session
/// once its file is in, writes the file itself for a session silent past <see cref="Bound"/> (from what the node knows of it) and
/// ends that one too, and says when the machine can reboot. The node never reboots the machine. <c>cancel</c> drops the request and
/// leaves maintenance mode; <c>done</c> (after the reboot) leaves maintenance mode and names each file on the flight it belongs to,
/// so its next session starts from it.
/// </summary>
/// <remarks>
/// The owner, 2026-11-09: "a pending reboot feature of the pilot that first puts into maint mode, then tells every active session to
/// write its state in a flight-&lt;code&gt;-reboot-&lt;reboot request date and time&gt;.md, and the node collect those, then it gets
/// terminated. once we have all, we show a message in the panel that reboot is ready." SE1 had to reboot for a kernel leak with
/// fixers, reviewers and workers running, and what was in their heads was saved by hand.
/// </remarks>
public sealed class RebootPrep(NodeState state, TimeProvider time, string folder) : BackgroundService
{
    /// <summary>The card kind that says the machine can reboot now: every session's file is in.</summary>
    public const string ReadyKind = "reboot-ready";

    /// <summary>The Handled card kind of a request, a cancel and a done.</summary>
    public const string Kind = "reboot";

    /// <summary>Tells a session (its row id) the words; true when they reached it.</summary>
    public Func<string, string, CancellationToken, Task<bool>>? Say { get; set; }

    /// <summary>Ends a session (its row id) with the reason.</summary>
    public Func<string, string, CancellationToken, Task>? End { get; set; }

    /// <summary>Turns maintenance mode on or off, as the setting does.</summary>
    public Action<bool>? SetMaintenance { get; set; }

    /// <summary>How long a told session has to write its file before the node writes it and ends the session.</summary>
    public TimeSpan Bound { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often a request in progress is looked at.</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>One session a request waits on: where its file goes, when it was told, and how it ended.</summary>
    public sealed record Expected(string SessionId, string Name, string? Flight, string File, DateTimeOffset ToldAt, string? Outcome = null);

    private readonly object _gate = new();
    private DateTimeOffset? _requestedAt;
    private string _why = "";
    private List<Expected> _expected = [];

    /// <summary>The sessions the request waits on, as they stand; empty with no request.</summary>
    public IReadOnlyList<Expected> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _expected];
            }
        }
    }

    /// <summary>Whether a request is in progress or ready.</summary>
    public bool Requested
    {
        get
        {
            lock (_gate)
            {
                return _requestedAt is not null;
            }
        }
    }

    /// <summary>Whether every session the request waits on is saved: the machine can reboot.</summary>
    public bool Ready
    {
        get
        {
            lock (_gate)
            {
                return _requestedAt is not null && _expected.All(e => e.Outcome is not null);
            }
        }
    }

    /// <summary><c>reboot node &lt;request|status|cancel|done&gt; [why]</c>.</summary>
    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var args = command.Args ?? [];
        var what = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "status";
        var why = string.Join(' ', args.Skip(1)).Trim();
        string? error = null;
        string? way = null;
        switch (what)
        {
            case "request":
                (error, way) = await RequestAsync(command.Who, why, cancellationToken);
                break;
            case "status":
                way = Status();
                break;
            case "cancel":
                (error, way) = Cancel(command.Who);
                break;
            case "done":
                (error, way) = Done(command.Who);
                break;
            default:
                error = $"reboot takes request [why], status, cancel or done, not {what}";
                break;
        }
        return state.Complete(command, error, way: way);
    }

    /// <summary>The request: maintenance mode, then every live node-started session told to save itself.</summary>
    public async Task<(string? Error, string? Way)> RequestAsync(string who, string why, CancellationToken cancellationToken)
    {
        DateTimeOffset at;
        lock (_gate)
        {
            if (_requestedAt is not null)
            {
                return (null, Status());
            }
            at = time.GetUtcNow();
            _requestedAt = at;
            _why = why;
            _expected = [];
        }
        SetMaintenance?.Invoke(true);
        Directory.CreateDirectory(folder);
        var stamp = Stamp(at);
        var told = new List<Expected>();
        foreach (var row in LiveNodeSessions())
        {
            var file = Path.Combine(folder, FileName(row, stamp));
            var text = Tell(at, why, file);
            var reached = false;
            try
            {
                reached = Say is not null && await Say(row.Id, text, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not reached: the bound writes its file for it.
            }
            told.Add(new Expected(row.Id, row.Name, row.Flight, file, reached ? time.GetUtcNow() : at - Bound));
        }
        lock (_gate)
        {
            _expected = told;
        }
        state.RaiseCard(CardBin.Handled, Kind, Verbs.NodeTarget, $"Preparing to reboot: {told.Count} {(told.Count == 1 ? "session" : "sessions")} told to save their state",
            $"{who}{(why.Length > 0 ? $": {why}" : "")}. Maintenance mode is on: nothing new starts. Each session writes its state to {folder} and is ended once it is in; "
                + $"one silent for {Bound.TotalMinutes:0} minutes gets a file the node writes from what it knows. pilot node reboot status says how far it is.");
        await TickAsync(cancellationToken);
        return (null, Status());
    }

    /// <summary>One look: a file that is in ends its session, a session past the bound gets the node's file and is ended, and all in is ready.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        List<Expected> open;
        lock (_gate)
        {
            if (_requestedAt is null)
            {
                return;
            }
            open = [.. _expected.Where(e => e.Outcome is null)];
        }
        if (open.Count == 0)
        {
            RaiseReady();
            return;
        }
        var now = time.GetUtcNow();
        foreach (var expected in open)
        {
            string? outcome = null;
            if (File.Exists(expected.File))
            {
                outcome = "saved";
            }
            else if (now - expected.ToldAt >= Bound)
            {
                File.WriteAllText(expected.File, NodeWritten(expected, now));
                outcome = "written by the node";
            }
            if (outcome is null)
            {
                continue;
            }
            try
            {
                if (End is not null)
                {
                    await End(expected.SessionId, $"the machine is being made ready to reboot; its state is in {expected.File}", cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Gone already: its file is what counts.
            }
            lock (_gate)
            {
                var at = _expected.FindIndex(e => e.SessionId == expected.SessionId);
                if (at >= 0)
                {
                    _expected[at] = _expected[at] with { Outcome = outcome };
                }
            }
        }
        if (Ready)
        {
            RaiseReady();
        }
    }

    /// <summary>Where the request stands, in a line per session; "no reboot requested" with none.</summary>
    public string Status()
    {
        lock (_gate)
        {
            if (_requestedAt is not { } at)
            {
                return "no reboot requested";
            }
            var saved = _expected.Count(e => e.Outcome is not null);
            var head = _expected.All(e => e.Outcome is not null)
                ? $"Ready to reboot: {saved} {(saved == 1 ? "session" : "sessions")} saved, 0 running"
                : $"Preparing to reboot (asked {at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}): {saved} of {_expected.Count} saved";
            return string.Join('\n', new[] { head }.Concat(_expected.Select(e => $"  {e.Name}: {e.Outcome ?? "told, waiting for its file"} ({e.File})")));
        }
    }

    /// <summary>Drops the request before the reboot and leaves maintenance mode; nothing new is started by it.</summary>
    public (string? Error, string? Way) Cancel(string who)
    {
        lock (_gate)
        {
            if (_requestedAt is null)
            {
                return ("no reboot was requested", null);
            }
            _requestedAt = null;
            _expected = [];
        }
        SetMaintenance?.Invoke(false);
        state.CloseCardsAbout(Verbs.NodeTarget, $"the reboot was cancelled by {who}", ReadyKind);
        state.RaiseCard(CardBin.Handled, Kind, Verbs.NodeTarget, $"The reboot was cancelled by {who}", "Maintenance mode is off; the files written so far stay in " + folder + ".");
        return (null, "the reboot request is cancelled and maintenance mode is off");
    }

    /// <summary>After the reboot: leaves maintenance mode and names each saved file on its flight, for its next session to start from.</summary>
    public (string? Error, string? Way) Done(string who)
    {
        List<Expected> saved;
        lock (_gate)
        {
            saved = [.. _expected];
            _requestedAt = null;
            _expected = [];
        }
        SetMaintenance?.Invoke(false);
        state.CloseCardsAbout(Verbs.NodeTarget, $"resumed by {who}", ReadyKind);
        var files = saved.Count > 0 ? saved : Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "flight-*-reboot-*.md").Select(f => new Expected("", Path.GetFileName(f), FlightOf(f), f, DateTimeOffset.MinValue, "saved"))] : [];
        foreach (var file in files.Where(f => f.Flight is not null))
        {
            state.RaiseCard(CardBin.Handled, Kind, Verbs.FlightTarget(file.Flight!), $"{file.Flight} resumes from its reboot file",
                $"Its session's state before the reboot is in {file.File}: the next session of {file.Flight} starts from it.");
        }
        state.RaiseCard(CardBin.Handled, Kind, Verbs.NodeTarget, $"Resumed after the reboot by {who}", $"Maintenance mode is off; {files.Count} saved {(files.Count == 1 ? "state" : "states")} in {folder}.");
        return (null, $"maintenance mode is off; {files.Count} saved {(files.Count == 1 ? "state is" : "states are")} named on their flights");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, time, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that could not be read or written now is tried at the next tick.
            }
        }
    }

    private void RaiseReady()
    {
        if (state.Cards(openOnly: true).Any(c => c.Kind == ReadyKind))
        {
            return;
        }
        // In the panel, as the owner asked ("we show a message in the panel that reboot is ready"): nothing to press but the reboot itself.
        state.RaiseCard(CardBin.ForYou, ReadyKind, Verbs.NodeTarget, "Ready to reboot: every session's state is saved", Status());
    }

    // The background sessions the node started that are live now: workers, validators, planners, reviewers, fixers and the watchtower.
    private IEnumerable<SessionRow> LiveNodeSessions() =>
        state.Sessions().Where(s => !s.Archived && s.Kind == SessionKind.Background && s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle
            && (s.Role != SessionRole.AdHoc || s.StartReason is not null));

    /// <summary>The file a session writes: <c>flight-&lt;code&gt;-reboot-&lt;stamp&gt;.md</c>, <c>watchtower-reboot-…</c>, else its name's.</summary>
    internal static string FileName(SessionRow row, string stamp) =>
        row.Flight is { Length: > 0 } flight ? $"flight-{flight}-reboot-{stamp}.md"
        : row.Role == SessionRole.Watchtower ? $"watchtower-reboot-{stamp}.md"
        : $"{string.Concat(row.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'))}-reboot-{stamp}.md";

    /// <summary>The request's time as the files name it: <c>20261109-1912</c>, local time.</summary>
    internal static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

    private static string? FlightOf(string path) =>
        Path.GetFileName(path) is var name && name.StartsWith("flight-", StringComparison.Ordinal) && name.IndexOf("-reboot-", StringComparison.Ordinal) is > 7 and var end
            ? name[7..end] : null;

    /// <summary>What a session is told: commit and push first, then the file and what it says, then end the turn.</summary>
    internal static string Tell(DateTimeOffset at, string why, string file) =>
        $"The node is preparing to reboot the machine (asked {at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}{(why.Length > 0 ? $": {why}" : "")}). "
        + "Stop where you are. First commit and push what you can: unpushed work is lost work. "
        + $"Then write {file} with four parts: what you were doing and where you stopped; what is committed and pushed, and what is not; "
        + "what you were waiting on; the exact next step. Then end your turn: the node ends this session once the file is in.";

    // The file the node writes for a session that did not answer: what the node itself knows of it.
    private string NodeWritten(Expected expected, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine($"# {expected.Name}: state before the reboot, written by the node");
        text.AppendLine();
        text.AppendLine($"The session did not write its own file within {Bound.TotalMinutes:0} minutes of being told ({now.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}), so this is what the node knows.");
        if (expected.Flight is { } code && state.Flights().FirstOrDefault(f => f.Code == code) is { } flight)
        {
            text.AppendLine();
            text.AppendLine($"- Flight {flight.Code}: {flight.Title}, {flight.State.ToString().ToLowerInvariant()}, branch {flight.Branch}, plane {flight.Worktree}");
            foreach (var leg in state.Legs(code).OrderBy(l => l.Order))
            {
                var done = leg.Checklist.Count(i => i.Done);
                text.AppendLine($"- Leg {leg.Name}: {leg.State.ToString().ToLowerInvariant()}, {done}/{leg.Checklist.Length} steps{(leg.Worker == expected.Name ? " (this session's leg)" : "")}");
                if (leg.Worker == expected.Name && leg.Report is { Length: > 0 } report)
                {
                    text.AppendLine($"  - Its last report: {report.ReplaceLineEndings(" ")}");
                }
            }
            text.AppendLine();
            text.AppendLine("Next step: the next session of this flight checks the plane for uncommitted or unpushed work first, then carries on the leg that was flying.");
        }
        return text.ToString();
    }
}
