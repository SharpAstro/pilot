using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

/// <summary>
/// The node's state and the one writer of it: the session rows, the notes, the numbered deltas and who is listening.
/// Every change goes through here, is numbered once, journaled (<see cref="INodeJournal"/>) and queued for every
/// client in the same order, so the stream, the journal and every client agree. Commands arrive here too
/// (<see cref="Execute"/>).
/// </summary>
/// <remarks>
/// This is the seam the store leg backs with files: it gives this class a journal that writes the data folder, and
/// the flights, legs and budgets it adds become more of the state held here and more kinds of delta.
/// </remarks>
public sealed partial class NodeState
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>How many of the latest deltas the node holds for a client resuming with <c>since</c>.</summary>
    public const int DefaultRetainedDeltas = 256;

    private readonly Lock _gate = new Lock();
    private readonly INodeJournal _journal;
    private readonly TimeProvider _time;
    private readonly int _retainedDeltas;
    private readonly Dictionary<string, SessionRow> _sessions = new Dictionary<string, SessionRow>(StringComparer.Ordinal);
    private readonly List<NodeNote> _notes = [];
    private readonly Dictionary<string, FlightRow> _flights = new Dictionary<string, FlightRow>(StringComparer.Ordinal);
    private readonly Dictionary<(string Flight, string Name), LegRow> _legs = [];
    private readonly Dictionary<string, CardRow> _cards = new Dictionary<string, CardRow>(StringComparer.Ordinal);
    private UsageRow? _usage;
    private MachineRow? _machine;
    private AdmissionRow? _admission;
    private PullRequestsRow? _pullRequests;
    private ReposRow? _repos;
    private NodeRow? _node;
    private readonly Dictionary<string, MergeRow> _merges = new Dictionary<string, MergeRow>(StringComparer.Ordinal);
    private readonly Dictionary<string, JobRow> _jobs = new Dictionary<string, JobRow>(StringComparer.Ordinal);
    private readonly Dictionary<string, PrReviewRow> _prReviews = new Dictionary<string, PrReviewRow>(StringComparer.OrdinalIgnoreCase);
    private IdeaRow[] _ideas = [];
    private IdeaMoveRow[] _ideaMoves = [];
    private MilestoneRow[] _milestones = [];
    private IssueRow[] _issues = [];
    private readonly Dictionary<string, SummaryRow> _summaries = new Dictionary<string, SummaryRow>(StringComparer.Ordinal);

    // The latest deltas, oldest first, serialised once: what a resuming client is sent.
    private readonly Queue<(long Seq, byte[] Json)> _held = new Queue<(long Seq, byte[] Json)>();
    private readonly List<INodeEventSink> _sinks = [];
    private long _seq;

    public NodeState(INodeJournal journal, TimeProvider time, int retainedDeltas = DefaultRetainedDeltas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedDeltas, 1);
        _journal = journal;
        _time = time;
        _retainedDeltas = retainedDeltas;

        var history = journal.Load();
        Stream = history.Stream ?? Guid.NewGuid().ToString("N");
        if (history.Snapshot is { } snapshot)
        {
            Restore(snapshot);
        }
        foreach (var delta in history.Deltas)
        {
            ApplyToState(delta);
            _seq = delta.Seq;
            Hold(delta.Seq, Serialize(delta));
        }
        // A check a finish was rerunning died with the node that ran it (a bare process of that node, not a job): no leg comes
        // back from a restart still rechecking. Turned here, in memory, into an outcome saying so, which the leg watch tells the
        // worker (it was left waiting on the answer), so what the journal holds is never read as running.
        foreach (var key in _legs.Where(l => l.Value.Recheck is { Outcome: null }).Select(l => l.Key).ToList())
        {
            var check = _legs[key].Recheck!.Check;
            // A rerun whose finish named who asked for it is finished by this node itself once it is up (the leg watch resumes it, pilot #819):
            // it keeps no outcome, for there is nothing for the worker to do, and carries on as rechecking. The check's job, whether queued,
            // still running or ended by a crash, is the finish's to attach to or to run afresh, so the job's state is not read here.
            if (_legs[key].Recheck!.By is { Length: > 0 })
            {
                _legs[key] = _legs[key] with { Recheck = _legs[key].Recheck! with { Resume = true } };
                continue;
            }
            // A check job still queued carries over the restart (the job runner puts it back in its place): say so, not that the rerun was lost.
            // One running with a process to follow carried over too, unless a crash ended it (then the next finish runs it afresh).
            var open = _jobs.Values.Where(j => j is { Kind: JobKind.Check, EndedAt: null } && j.Flight == key.Flight && j.Leg == key.Name).ToList();
            var outcome = open.Any(j => j.State == JobState.Queued) ? LegRecheck.CarriedOutcome(check)
                : open.Any(j => j is { State: JobState.Running, Pid: not null, ProcessStartedAt: not null }) ? LegRecheck.RunningCarriedOutcome(check)
                : LegRecheck.RestartedOutcome(check);
            _legs[key] = _legs[key] with { Recheck = _legs[key].Recheck! with { Outcome = outcome } };
        }

        journal.SnapshotOutcome += OnSnapshotOutcome;
        if (history.Stream is null)
        {
            // A starting snapshot that cannot be made or kept is no reason to stay down: the journal holds every delta
            // that follows, and the card says why there is no snapshot yet.
            try
            {
                var started = SnapshotLocked();
                // No bytes: the journal streams a snapshot to its file itself, never built whole on the LOH (#794).
                _journal.Append(started, ReadOnlyMemory<byte>.Empty);
            }
            catch (Exception e)
            {
                OnSnapshotOutcome(e);
            }
        }
        ReportSnapshotOutcome();
    }

    /// <summary>The kind and subject of the card that says the store could not write its snapshot.</summary>
    public const string StoreCardKind = "store";
    public const string StoreCardSubject = "snapshot";

    // What the journal said of its last snapshot, held until the delta being published is done: the card it calls for is
    // itself a delta, and publishing it from inside the journal's own append would re-enter Publish.
    private bool _snapshotReported;
    private Exception? _snapshotError;

    private void OnSnapshotOutcome(Exception? error)
    {
        _snapshotReported = true;
        _snapshotError = error;
    }

    private void ReportSnapshotOutcome()
    {
        if (!_snapshotReported)
        {
            return;
        }

        var error = _snapshotError;
        _snapshotReported = false;
        _snapshotError = null;
        try
        {
            if (error is null)
            {
                CloseCardsAbout(StoreCardSubject, "written", StoreCardKind);
                return;
            }

            Error.WriteLine($"store: a snapshot could not be written, the journal is kept whole: {error.GetType().Name}: {error.Message}");
            var card = RaiseCard(CardBin.ForYou, StoreCardKind, StoreCardSubject, "The store could not write its snapshot",
                $"{error.GetType().Name}: {error.Message}. Every change is still journaled and a restart loses nothing; the node tries again later.",
                [new CardChoice { Label = "I will fix it", Verb = Verbs.Dismiss, Target = "" }],
                summary: $"Fix the store's folder or disk: {error.Message.Split('\n')[0].Trim()}. Nothing is lost meanwhile; the node tries again later.");
            AimDismiss(card);
        }
        catch (Exception e)
        {
            // A store that cannot record the card either has nothing more to say here: the log line stands.
            Error.WriteLine($"store: the card for a snapshot outcome could not be kept: {e.Message}");
        }
    }
}
