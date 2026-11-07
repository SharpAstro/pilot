using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Reads whether the node's job queue has stopped moving (pilot #848), from the job rows alone, so a node that restarts reads
/// the same jam it left and neither closes nor re-times its card. Jammed means: at least one job is queued, the oldest queued
/// job has waited past a limit, and no job has started within that same span. A queue that keeps starting jobs is not jammed
/// however long one low-priority job waits in it.
/// </summary>
internal static class QueueJam
{
    /// <summary>What a queued job's step starts with when it heads the queue: how the reader finds the head in the rows.</summary>
    public const string NextInLine = "next in line: ";

    /// <summary>How many leading words of a command the card's text quotes.</summary>
    private const int CommandWords = 3;

    /// <summary>
    /// A jam as the card says it: the title, the detail, the running job that holds the queue (null when none runs), and
    /// <paramref name="Since"/>, the phrase that ends the title and names the last start, which is what one jam is told by: it is over
    /// when another job starts.
    /// </summary>
    internal sealed record Reading(string Title, string Detail, JobRow? Holder, string Since);

    /// <summary>What the title says the queue has not done since its last start.</summary>
    public static string Since(JobRow? last) => last is null ? "no job has started" : $"the queue has not moved since {last.Id} started";

    /// <summary>
    /// Whether a row records a real start. A job cancelled or dropped before its turn keeps its hand-over time as its
    /// <see cref="JobRow.StartedAt"/> and never had a process, so it is no start; a job that ran has a pid, whatever became of it.
    /// </summary>
    public static bool Started(JobRow job) => job.State == JobState.Running || (job.State != JobState.Queued && job.Pid is not null);

    /// <summary>The job that started last, from the rows; null when none ever started.</summary>
    public static JobRow? LastStart(IEnumerable<JobRow> jobs) =>
        jobs.Where(Started).OrderByDescending(j => j.StartedAt).ThenByDescending(j => j.Id, StringComparer.Ordinal).FirstOrDefault();

    private static DateTimeOffset QueuedSince(JobRow job) => job.QueuedAt ?? job.StartedAt;

    /// <summary>
    /// The jam the rows show, or null while the queue moves or is empty. <paramref name="slots"/> is how many jobs the node runs at
    /// once, <paramref name="after"/> how long a queue may stand still, and <paramref name="holdsOf"/> the sibling clones a running
    /// job holds (empty once it is past its build). A benchmark at the head with nothing running waits for a quiet CPU, which the
    /// bench-busy card owns, so it is no jam here.
    /// </summary>
    public static Reading? Read(IEnumerable<JobRow> jobs, int slots, DateTimeOffset now, TimeSpan after, Func<JobRow, IReadOnlyCollection<string>> holdsOf)
    {
        var all = jobs as IReadOnlyCollection<JobRow> ?? [.. jobs];
        var queued = all.Where(j => j.State == JobState.Queued).OrderBy(QueuedSince).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
        if (queued.Count == 0 || now - QueuedSince(queued[0]) <= after)
        {
            return null;
        }
        var last = LastStart(all);
        if (last is not null && now - last.StartedAt <= after)
        {
            return null;
        }
        var holder = JobQueue.Holder(all, now) is var (job, _) ? job : null;
        var head = queued.FirstOrDefault(j => j.Step is { } step && step.StartsWith(NextInLine, StringComparison.Ordinal)) ?? queued[0];
        if (head.Kind == JobKind.Bench && holder is null)
        {
            return null;
        }
        var oldest = queued[0];
        var waited = now - QueuedSince(oldest);
        var since = Since(last);
        var title = queued.Count == 1
            ? $"1 job has waited {Waited.Say(waited)}; {since}"
            : $"{queued.Count} jobs have waited up to {Waited.Say(waited)}; {since}";

        var sentences = new List<string>();
        if (holder is null)
        {
            sentences.Add($"No job is running, so {(slots == 1 ? "the node's only slot sits" : $"the node's {slots} slots sit")} idle and something other than a running job holds the queue");
        }
        else
        {
            var running = all.Count(j => j.State == JobState.Running);
            var clones = holdsOf(holder).Select(c => Path.GetFileName(c.TrimEnd('/', '\\'))).Where(n => n.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var paused = holder.PausedAt is null ? "" : ", paused";
            var holds = clones.Count > 0
                ? $"It holds {clones.Count} sibling {(clones.Count == 1 ? "clone" : "clones")} ({string.Join(", ", clones)}) and fills {Filled(running, slots)}"
                : $"It fills {Filled(running, slots)}";
            sentences.Add($"{holder.Id} holds the queue ({Describe(holder)}, held for {Waited.Say(now - holder.StartedAt)}{paused})");
            sentences.Add(holds);
        }
        sentences.Add($"The oldest waiting job is {oldest.Id} ({OwnerOf(oldest)}), {Words(oldest.Command)}");
        if (head.Step is { } why && why.Trim() is { Length: > 0 } text)
        {
            var reason = text.StartsWith(NextInLine, StringComparison.Ordinal) ? text[NextInLine.Length..] : text;
            sentences.Add($"Next in line is {head.Id} ({OwnerOf(head)}), which waits: {reason.Trim().TrimEnd('.')}");
        }
        return new Reading(Plain(title), Plain(string.Join(". ", sentences) + "."), holder, Plain(since));
    }

    /// <summary>
    /// Why there is no jam, for the card that closes: <c>the queue is empty</c>, <c>the queue moved: j12 started</c>, or that the
    /// oldest job has not waited long enough now.
    /// </summary>
    public static string Calm(IEnumerable<JobRow> jobs, DateTimeOffset now, TimeSpan after)
    {
        var all = jobs as IReadOnlyCollection<JobRow> ?? [.. jobs];
        if (!all.Any(j => j.State == JobState.Queued))
        {
            return "the queue is empty";
        }
        return LastStart(all) is { } last && now - last.StartedAt <= after
            ? $"the queue moved: {last.Id} started"
            : "the queue is not jammed now: no job has waited past the limit, or a benchmark waits for a quiet CPU";
    }

    private static string Filled(int running, int slots) =>
        slots == 1 ? "the node's only slot" : $"{running} of the node's {slots} slots";

    private static string OwnerOf(JobRow job) =>
        job.OwnerName ?? job.Owner ?? (job.Flight is null ? "the node" : $"{job.Flight}/{job.Leg}");

    private static string Describe(JobRow job) => $"{OwnerOf(job)}, {Words(job.Command)}";

    /// <summary>
    /// A command by its leading words, never its flags: <c>dotnet build Pilot.slnx</c> for <c>dotnet build Pilot.slnx -c Release --no-build</c>.
    /// The flags carry a double hyphen, which no text of the card may hold, and the program and target name the job well enough.
    /// </summary>
    private static string Words(string command)
    {
        var words = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).TakeWhile(w => !w.StartsWith('-')).Take(CommandWords).ToList();
        return words.Count == 0 ? "a command" : $"`{string.Join(' ', words)}`";
    }

    /// <summary>The text with no em-dash and no double hyphen, whatever a command or a step carried in.</summary>
    internal static string Plain(string text)
    {
        text = text.Replace("—", ", ").Replace("–", "-");
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-");
        }
        return text;
    }
}
