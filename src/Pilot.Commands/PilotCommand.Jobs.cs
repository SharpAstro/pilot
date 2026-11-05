using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>A job in a line: <c>j3  running  dotnet test  (se1-tia1-parser, 4m)  [pwsh]</c>, the shell that ran it, then its step or how it ended.</summary>
    internal static string JobLine(JobRow job)
    {
        var state = job.State.ToString().ToLowerInvariant();
        if (job.Kind == JobKind.Bench)
        {
            state += " bench";
        }
        if (job.State == JobState.Running && job.PausedAt is { } pausedAt)
        {
            state = $"paused since {pausedAt.ToLocalTime():HH:mm}";
        }
        var end = job.Error is { } error ? $": {error}" : "";
        var took = (job.EndedAt ?? DateTimeOffset.UtcNow) - job.StartedAt;
        var told = job.EndedAt is not null && job.Owner is not null && !job.Told ? ", owner not told" : "";
        return $"{job.Id}  {state}{end}  {job.Command}  ({job.OwnerName ?? "terminal"}, {(int)took.TotalMinutes}m{told}){(job.Shell is { Length: > 0 } shell ? $"  [{shell}]" : "")}{(job.Step is { } step ? $"  · {step}" : "")}";
    }

    /// <summary>A merge queue entry in a line: <c>TIA61  #63  tianwen  1  abc1234  checks  · merging when checks pass</c>: flight, PR, repo, place, head, state, then why.</summary>
    internal static string MergeLine(MergeRow merge) =>
        $"{merge.Flight}  {(merge.Pr is { } pr ? $"#{pr}" : "no PR")}  {merge.Repo}  {(merge.Place is { } place ? place.ToString(CultureInfo.InvariantCulture) : "-")}  {merge.Head[..Math.Min(8, merge.Head.Length)]}  {merge.State.ToString().ToLowerInvariant()}{(merge.Why is { } why ? $"  · {why}" : "")}";

    /// <summary>A duration as people write it: <c>90m</c>, <c>2h</c>, <c>45s</c>.</summary>
    internal static bool TryDuration(string text, out TimeSpan span)
    {
        span = default;
        if (text.Length < 2 || !long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return false;
        }
        span = text[^1] switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => default };
        return span > TimeSpan.Zero;
    }
    /// <summary>A <c>--since</c> as a moment: a span back from <paramref name="now"/> (<c>30m</c>, <c>2h</c>, <c>3d</c>), else a time as <see cref="DateTimeOffset"/> reads it (a bare time is today, local); null when it is neither.</summary>
    internal static DateTimeOffset? ParseSince(string text, DateTimeOffset now)
    {
        text = text.Trim();
        if (text.Length > 1 && char.ToLowerInvariant(text[^1]) is var unit and ('m' or 'h' or 'd')
            && double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && amount >= 0)
        {
            return now - (unit switch { 'm' => TimeSpan.FromMinutes(amount), 'h' => TimeSpan.FromHours(amount), _ => TimeSpan.FromDays(amount) });
        }
        if (TimeSpan.TryParseExact(text, ["h\\:mm", "hh\\:mm", "h\\:mm\\:ss", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture, out var clock))
        {
            return new DateTimeOffset(now.Date + clock, now.Offset);
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) ? at : null;
    }

    private sealed partial class BuildContext
    {
        /// <summary>Makes the run, jobs, merges, spend, decisions and job: the node's jobs and ledgers.</summary>
        internal (Command, Command, Command, Command, Command, Command, Command) JobCommands()
        {
            // run, jobs, job: a long run handed to the node, which wakes the session that handed it over
            var runTimeout = new Option<string?>("--timeout") { Description = "How long it may run: 90m, 2h, 45s; 2h by default, 8h at most" };
            var runAlone = new Option<bool>("--alone") { Description = "Run with the node to itself (a benchmark): waits for no other job and a quiet CPU, and holds every other job and new leg until it ends" };
            var runWords = new Argument<string[]>("command") { Description = "The command, after --: pilot run -- dotnet test", Arity = ArgumentArity.OneOrMore };
            var runCommand = Action(new Command("run", "Hand a long run to the node as a job, then end your turn: the node wakes this session with how it ended")
                {
                    Arguments = { runWords },
                    Options = { runTimeout, runAlone },
                },
                async (p, client, ct) =>
                {
                    var seconds = p.GetValue(runTimeout) is { } given
                        ? (TryDuration(given, out var span) ? (long)span.TotalSeconds : throw new UsageException($"--timeout takes 90m, 2h or 45s, not {given}"))
                        : 0;
                    // The session to wake: the Claude session running this, by its row on the node; from a terminal, none.
                    var owner = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } sessionId
                        ? (await SnapshotAsync(client, ct)).Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        : null;
                    var target = owner is null ? Verbs.NodeTarget : Verbs.SessionTarget(owner.Id);
                    var sent = await SendCommandAsync(client, new CommandRequest
                    {
                        Verb = Verbs.Run,
                        Target = target,
                        Args = [.. p.GetValue(runAlone) ? new[] { Verbs.AloneArg } : [], cwd, seconds.ToString(CultureInfo.InvariantCulture), .. p.GetValue(runWords) ?? []],
                        Who = owner?.Name ?? who,
                        Agent = byAgent,
                        RequestId = requestId,
                    }, deadline, ct);
                    if (sent is not { Success: true, Response: { Ok: true, Made: { } job } })
                    {
                        await output.WriteLineAsync($"refused: run: {sent.Response?.Error ?? sent.Error}");
                        return 1;
                    }
                    // A job may have to wait its turn (the machine is shared between flights): say so, and where it stands. The
                    // node's dispatcher answers a moment after the run, so look for up to two seconds until it has.
                    var made = (await SnapshotAsync(client, ct)).Jobs.FirstOrDefault(j => j.Id == job);
                    for (var tries = 0; tries < 20 && made is { State: JobState.Queued, Step: null }; tries++)
                    {
                        await Task.Delay(100, ct);
                        made = (await SnapshotAsync(client, ct)).Jobs.FirstOrDefault(j => j.Id == job);
                    }
                    var how = made is { State: JobState.Queued } ? $"queued ({made.Step})" : "started";
                    await output.WriteLineAsync(owner is null
                        ? $"job {job} {how}; pilot job {job} shows how it stands"
                        : $"job {job} {how}. End your turn now: the node tells you how it ended, with its last lines. Do not wait for it or poll it.");
                    return 0;
                });
            var benchRepo = new Argument<string>("repo") { Description = "The repo (a clone the node knows: pilot repos)" };
            var benchRef = new Option<string?>("--ref") { Description = "Measure this one ref (a branch, tag or commit)" };
            var benchBaseline = new Option<string?>("--baseline") { Description = "The baseline ref of an A/B, measured first" };
            var benchCandidate = new Option<string?>("--candidate") { Description = "The candidate ref of an A/B, measured after the baseline" };
            var benchFetch = new Option<string?>("--fetch") { Description = "The repo's fetch step, run in each worktree before the command (fetch-webp-external.sh)" };
            var benchPr = new Option<int?>("--pr") { Description = "Also post the tables as a comment on this PR" };
            var benchTimeout = new Option<string?>("--timeout") { Description = "How long the whole job may run: 90m, 2h; 2h by default, 8h at most" };
            var benchWords = new Argument<string[]>("command") { Description = "The benchmark command, after --: pilot bench codecs --ref main -- dotnet run -c Release", Arity = ArgumentArity.OneOrMore };
            var benchCommand = Action(new Command("bench", "Run a benchmark alone on the node, on a ref or a baseline and a candidate, and post the tables on a card (and a PR)")
                {
                    Arguments = { benchRepo, benchWords },
                    Options = { benchRef, benchBaseline, benchCandidate, benchFetch, benchPr, benchTimeout },
                },
                async (p, client, ct) =>
                {
                    var (@ref, baseline, candidate) = (p.GetValue(benchRef), p.GetValue(benchBaseline), p.GetValue(benchCandidate));
                    var pair = baseline is not null && candidate is not null;
                    if (@ref is not null == pair || (baseline is null) != (candidate is null))
                    {
                        throw new UsageException("bench takes one --ref, or a --baseline and a --candidate together, not both and not neither");
                    }
                    var seconds = p.GetValue(benchTimeout) is { } given
                        ? (TryDuration(given, out var span) ? (long)span.TotalSeconds : throw new UsageException($"--timeout takes 90m, 2h or 45s, not {given}"))
                        : 0;
                    var owner = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } sessionId
                        ? (await SnapshotAsync(client, ct)).Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        : null;
                    List<string> args = [p.GetValue(benchRepo)!];
                    foreach (var (flag, value) in new[] { ("--ref", @ref), ("--baseline", baseline), ("--candidate", candidate), ("--fetch", p.GetValue(benchFetch)), ("--pr", p.GetValue(benchPr)?.ToString(CultureInfo.InvariantCulture)) })
                    {
                        if (value is not null)
                        {
                            args.AddRange([flag, value]);
                        }
                    }
                    args.AddRange(["--timeout", seconds.ToString(CultureInfo.InvariantCulture), "--", .. p.GetValue(benchWords) ?? []]);
                    var sent = await SendCommandAsync(client, new CommandRequest
                    {
                        Verb = Verbs.Bench,
                        Target = owner is null ? Verbs.NodeTarget : Verbs.SessionTarget(owner.Id),
                        Args = [.. args],
                        Who = owner?.Name ?? who,
                        Agent = byAgent,
                        RequestId = requestId,
                    }, deadline, ct);
                    if (sent is not { Success: true, Response: { Ok: true, Made: { } job } })
                    {
                        await output.WriteLineAsync($"refused: bench: {sent.Response?.Error ?? sent.Error}");
                        return 1;
                    }
                    await output.WriteLineAsync(owner is null
                        ? $"bench job {job} queued: it waits for the node to go quiet and holds other jobs while it runs; pilot job {job} shows how it stands, and a card carries the tables"
                        : $"bench job {job} queued. End your turn now: it waits for the node to go quiet, then the node wakes you when it ends, and a card carries the tables. Do not wait for it or poll it.");
                    return 0;
                });
            var jobsCommand = Action(new Command("jobs", "The node's jobs, running first"), async (_, client, ct) =>
            {
                var all = (await SnapshotAsync(client, ct)).Jobs;
                var jobs = all.OrderBy(j => j.State switch { JobState.Running => 0, JobState.Queued => 1, _ => 2 })
                .ThenByDescending(j => j.StartedAt).Take(20).ToList();
                await output.WriteAsync(jobs.Count == 0 ? "No jobs.\n" : string.Concat(jobs.Select(j => JobLine(j) + "\n")));
                if (JobQueue.HolderLine(all, DateTimeOffset.UtcNow) is { } holder)
                {
                    await output.WriteLineAsync(holder);
                }
                if (JobQueue.SlotsLine(all) is { } slots)
                {
                    await output.WriteLineAsync(slots);
                }
                return 0;
            });
            var mergesCommand = Action(new Command("merges", "The node's merge queue: a row per flight waiting to merge, per repo in order"), async (_, client, ct) =>
            {
                var merges = (await SnapshotAsync(client, ct)).Merges
                    .OrderBy(m => m.Open ? 0 : 1).ThenBy(m => m.Repo, StringComparer.Ordinal).ThenBy(m => m.Place ?? int.MaxValue).ThenByDescending(m => m.ChangedAt ?? m.AddedAt).Take(30).ToList();
                await output.WriteAsync(merges.Count == 0 ? "No merges.\n" : string.Concat(merges.Select(m => MergeLine(m) + "\n")));
                return 0;
            });
            var spendWindow = new Option<string>("--window") { Description = "5h, today or week: the account's 5-hour or 7-day window when its reset is known, else counted back from now; today is the local day", DefaultValueFactory = _ => "today" };
            spendWindow.AcceptOnlyFromAmong("5h", "today", "week");
            var spendBy = new Option<string?>("--by") { Description = "flight or role; both when left out" };
            spendBy.AcceptOnlyFromAmong("flight", "role");
            var spendWaste = new Option<bool>("--waste") { Description = "Only what bought nothing kept, worst first: later rounds of a leg, base-merge re-reviews, sessions that ended without a result, replanned plans, failed starts again" };
            var spendCommand = Action(new Command("spend", "Tokens spent over a window, by flight and by role, from the node's ledger") { Options = { spendWindow, spendBy, spendWaste } }, async (p, client, ct) =>
            {
                var window = p.GetValue(spendWindow)!;
                if (p.GetValue(spendWaste))
                {
                    var wasted = await client.GetSpendWasteAsync(window, ct);
                    if (wasted is not { Success: true, Response: { } waste })
                    {
                        await output.WriteLineAsync($"refused: {wasted.Error}");
                        return 1;
                    }
                    var culture = CultureInfo.InvariantCulture;
                    var span = $"{waste.From.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture)} to {waste.To.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture)}";
                    var name = window switch { "5h" => "5h window", "week" => "7-day window", _ => "today" };
                    await output.WriteLineAsync($"spend waste, {name}: {span} (local time){(waste.FromAccount ? ", the account's own window" : "")}");
                    await output.WriteLineAsync($"{waste.WasteWeighted.ToString("N0", culture)} of {waste.WindowWeighted.ToString("N0", culture)} weighted tokens spent for nothing kept ({waste.WasteShare.ToString("0.0", culture)} %), {span}");
                    if (waste.Rows.Length == 0)
                    {
                        await output.WriteLineAsync("  no waste");
                        return 0;
                    }
                    foreach (var row in waste.Rows)
                    {
                        var where = string.Join('/', new[] { row.Flight, row.Leg }.Where(x => x is { Length: > 0 }));
                        await output.WriteLineAsync($"  {row.Kind}: {row.Reason}  {(where.Length > 0 ? where : "-")}  {row.Role}  {row.Model ?? "-"}  {row.Tokens.ToString("N0", culture),14} tokens  {row.Weighted.ToString("N0", culture),14} weighted  {row.Share.ToString("0.0", culture),5} %");
                    }
                    return 0;
                }
                var envelope = await client.GetSpendAsync(window, ct);
                if (envelope is not { Success: true, Response: { } report })
                {
                    await output.WriteLineAsync($"refused: {envelope.Error}");
                    return 1;
                }

                var inv = CultureInfo.InvariantCulture;
                var range = $"{report.From.ToLocalTime().ToString("yyyy-MM-dd HH:mm", inv)} to {report.To.ToLocalTime().ToString("yyyy-MM-dd HH:mm", inv)}";
                var label = window switch { "5h" => "5h window", "week" => "7-day window", _ => "today" };
                await output.WriteLineAsync($"spend, {label}: {range} (local time){(report.FromAccount ? ", the account's own window" : "")}");
                await output.WriteLineAsync($"{report.Messages.ToString("N0", inv)} messages, {report.Tokens.ToString("N0", inv)} tokens, {report.Weighted.ToString("N0", inv)} weighted, {range}");
                var by = p.GetValue(spendBy);
                foreach (var (name, rows) in new[] { ("flight", report.Flights), ("role", report.Roles) })
                {
                    if (by is not null && by != name)
                    {
                        continue;
                    }
                    await output.WriteLineAsync();
                    await output.WriteLineAsync($"by {name}, {range}:");
                    if (rows.Length == 0)
                    {
                        await output.WriteLineAsync("  nothing spent");
                        continue;
                    }
                    var width = rows.Max(r => r.Key.Length);
                    foreach (var row in rows)
                    {
                        await output.WriteLineAsync($"  {row.Key.PadRight(width)}  {row.Tokens.ToString("N0", inv),14} tokens  {row.Weighted.ToString("N0", inv),14} weighted  {row.Share.ToString("0.0", inv),5} %");
                    }
                }
                return 0;
            });
            var decisionFlight = new Option<string?>("--flight") { Description = "Only this flight's decisions (its code, as PIL583), with those of its cards and PRs" };
            var decisionKind = new Option<string?>("--kind") { Description = "Only this kind: design, merge, go-around, accept-leg, pass-with-notes, overlap, priority, dismiss or card" };
            var decisionSince = new Option<string?>("--since") { Description = "Only decisions at or after this: a time (14:30, 2026-10-27T09:00) or a span back from now (30m, 2h, 3d)" };
            var decisionJson = new Option<bool>("--json") { Description = "The decisions as JSON, whole" };
            var decisionsCommand = Action(new Command("decisions", "The decisions in the black box, oldest first: what was asked, the options, the choice, who chose and why") { Options = { decisionFlight, decisionKind, decisionSince, decisionJson } }, async (p, client, ct) =>
            {
                DateTimeOffset? since = null;
                if (p.GetValue(decisionSince) is { Length: > 0 } sinceText)
                {
                    since = ParseSince(sinceText, DateTimeOffset.Now) ?? throw new UsageException($"--since {sinceText} is neither a time nor a span like 2h");
                }
                var envelope = await client.GetDecisionsAsync(p.GetValue(decisionFlight), p.GetValue(decisionKind), since, ct);
                if (envelope is not { Success: true, Response: { } report })
                {
                    await output.WriteLineAsync($"refused: {envelope.Error}");
                    return 1;
                }
                if (p.GetValue(decisionJson))
                {
                    await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(report, PilotJsonContext.Default.DecisionReport));
                    return 0;
                }
                if (report.Decisions.Length == 0)
                {
                    await output.WriteAsync("No decisions.\n");
                    return 0;
                }
                foreach (var d in report.Decisions)
                {
                    var options = d.Options.Length > 0 ? $" of [{string.Join(", ", d.Options)}]" : "";
                    var reason = d.Reason.Length > 0 ? $": {d.Reason}" : "";
                    await output.WriteLineAsync($"{d.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  {d.Subject}  {d.Kind}  {d.Choice}{options}  by {d.By} ({d.ByKind}){reason}");
                }
                return 0;
            });
            var jobId = Arg("job", "The job's id, as j3");
            var jobShow = Action(new Command("job", "One job: how it stands, and where its whole output is") { Arguments = { jobId } }, async (p, client, ct) =>
            {
                if ((await SnapshotAsync(client, ct)).Jobs.FirstOrDefault(j => j.Id == p.GetValue(jobId)) is not { } job)
                {
                    await output.WriteLineAsync($"refused: there is no job {p.GetValue(jobId)}");
                    return 1;
                }
                await output.WriteLineAsync(JobLine(job));
                await output.WriteLineAsync($"  in {job.Cwd}, for {job.OwnerName ?? "a terminal"}; log {job.LogPath}");
                // Through the node's job log route, as the GUI reads it: the node opens a running job's log alongside its
                // writer, where a plain read of the file here was refused while the job ran, and it works from the LAN too.
                if (await client.GetJobLogAsync(job.Id, ct) is { Success: true, Response: { } log })
                {
                    if (log.Pid is { } pid)
                    {
                        await output.WriteLineAsync($"  pid {pid}");
                    }
                    foreach (var process in log.Tree ?? [])
                    {
                        await output.WriteLineAsync($"  {new string(' ', process.Depth * 2)}{process.Name} {process.Pid}");
                    }
                    if (log.Stuck is { } stuck)
                    {
                        await output.WriteLineAsync($"  stuck: {stuck}; pilot job {job.Id} cancel --force ends it");
                    }
                    foreach (var line in log.Lines.TakeLast(15))
                    {
                        await output.WriteLineAsync($"  | {line}");
                    }
                }
                return 0;
            });
            var cancelForce = new Option<bool>("--force") { Description = "Kill the job's wrapper and every process under it at once, including ones that outlived the wrapper and hold its output" };
            var cancelJob = Action(new Command("cancel", "Stop a running job; its owner is told") { Arguments = { jobId }, Options = { cancelForce } },
                (p, client, ct) => Send(client, Verbs.CancelJob, Verbs.JobTarget(p.GetValue(jobId)!), p.GetValue(cancelForce) ? [Verbs.ForceArg] : [], who, ct));
            jobShow.Subcommands.Add(cancelJob);
            var pauseJob = Action(new Command("pause", "Freeze a running job's processes; it keeps its slot and its timeout stops") { Arguments = { jobId } },
                (p, client, ct) => Send(client, Verbs.PauseJob, Verbs.JobTarget(p.GetValue(jobId)!), [], who, ct));
            jobShow.Subcommands.Add(pauseJob);
            var resumeJob = Action(new Command("resume", "Let a paused job run on") { Arguments = { jobId } },
                (p, client, ct) => Send(client, Verbs.ResumeJob, Verbs.JobTarget(p.GetValue(jobId)!), [], who, ct));
            jobShow.Subcommands.Add(resumeJob);

            return (runCommand, jobsCommand, mergesCommand, spendCommand, decisionsCommand, jobShow, benchCommand);
        }
    }
}
