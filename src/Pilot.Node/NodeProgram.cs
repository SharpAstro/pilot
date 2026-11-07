using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>The node process: parse, take the lock or say who holds it, run until stopped.</summary>
public static class NodeProgram
{
    /// <summary>The file in the data folder that a refusal to start is also written to, as a node started at logon or by an update has no console to say it on.</summary>
    public const string StartLogFile = "node-start.log";

    /// <returns>A <see cref="NodeExitCodes"/> value.</returns>
    /// <param name="error">Stderr: said to only when the log cannot take a line (the catastrophic case); the node writes to <c>logs/node.log</c> (pilot #416).</param>
    public static async Task<int> RunAsync(NodeOptions options, TextWriter error, CancellationToken cancellationToken)
    {
        var log = NodeApp.NodeLog(options);
        // RollingLog says when it could not write; only then does stderr get the line, as there is nowhere else to say it.
        void Say(string line)
        {
            if (!log.Write(line))
            {
                error.WriteLine(line);
            }
        }

        if (!NodeLock.TryAcquire(options.SocketPath, out var held, out var refusal))
        {
            Say($"pilot-node {FileJournal.RunningVersion}: {await NodeHolder.DescribeAsync(options.SocketPath, refusal, cancellationToken)}");
            return NodeExitCodes.AlreadyRunning;
        }

        using (held)
        {
            // The skills this release carries, kept current at every start (an update restarts the node), unless the person
            // turned user-skills off; a hand edit is kept. Never a reason to stop the start.
            if (options.Saved?.UserSkills ?? true)
            {
                IReadOnlyList<string> said;
                try
                {
                    var (lines, problem) = new NodeSetup(options, Environment.ProcessPath ?? "pilot-node", Sessions.SessionTrackerOptions.FromEnvironment().Profile.Root).ApplyUserSkills(on: true);
                    said = problem is null ? [.. lines.Select(l => $"skill {l}")] : [$"user skills: {problem}"];
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ApplyUserSkills answers the file errors itself; anything else (a profile path Windows refuses, a resource
                    // that cannot be read) is said here, and the node starts all the same.
                    said = [$"user skills: {ex.GetType().Name}: {ex.Message}"];
                }
                foreach (var line in said)
                {
                    Say($"pilot-node: {line}");
                }
            }
            return await RunHostAsync(options, held, Say, new UpdateRollback(NodeUpdater.DefaultInstallFolder, options.DataFolder, NodeUpdater.CurrentVersion),
                (o, lockHeld) => NodeApp.Build(o, lockHeld), cancellationToken);
        }
    }

    /// <summary>
    /// Builds the host and runs it until it stops. The first node of an update (the record <see cref="UpdateRollback"/> keeps names this version) is on
    /// trial: each start is counted before the host is built and cleared once the host has started with the store read. A start that fails with an
    /// exception before that starts the node once more, and the second one in a row puts the previous release back and starts that instead; a start that
    /// finds two counted (a crash nothing caught) goes back without trying a third. A store from a newer node is not the new version's fault and counts for nothing.
    /// </summary>
    /// <param name="say">Where a line goes: the log, and stderr when the log cannot take it.</param>
    /// <param name="build">Builds the host; a test replaces it.</param>
    internal static async Task<int> RunHostAsync(NodeOptions options, NodeLock held, Action<string> say, UpdateRollback rollback,
        Func<NodeOptions, NodeLock, WebApplication> build, CancellationToken cancellationToken)
    {
        var trial = rollback.Begin();
        if (trial is { Exhausted: true } && GoBack(options, say, rollback, trial) is { } restored)
        {
            return restored;
        }
        // The start and stop lines are the host's own (NodeLifecycleLog), so a node a test builds says them too.
        try
        {
            await using var app = build(options, held);
            try
            {
                // The store is read here, before the host starts a thing: a store from a newer node ends the start with its message.
                _ = app.Services.GetRequiredService<NodeState>();
            }
            catch (StoreTooNewException refused)
            {
                trial?.Forgive();
                say($"pilot-node: {refused.Message}");
                LogRefusal(options, refused);
                return NodeExitCodes.StoreTooNew;
            }
            // The host has started and the store is read: the update is good, and its record goes.
            app.Lifetime.ApplicationStarted.Register(() => trial?.Clear());
            await app.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The process dies here unless it is handed on: the log first, and stderr too when it has no log, then the crash as it is.
            say($"pilot-node: stopped by an error: {ex}");
            if (trial is { Cleared: false } && AfterFailedStart(options, say, rollback, trial, ex) is { } handedOn)
            {
                return handedOn;
            }
            throw;
        }
        return NodeExitCodes.Stopped;
    }

    // A start of a version on trial died of error: once more the first time, the previous release the second. Null when neither could be done, and the failure stands.
    private static int? AfterFailedStart(NodeOptions options, Action<string> say, UpdateRollback rollback, UpdateTrial trial, Exception error)
    {
        if (trial.Failed(error) == UpdateStep.RollBack)
        {
            return GoBack(options, say, rollback, trial);
        }
        var started = rollback.StartAgain();
        var line = started
            ? $"update: pilot {trial.Record.To} did not start (try {trial.Record.FailedStarts} of {UpdateRollback.StartsAllowed}); starting it once more"
            : $"update: pilot {trial.Record.To} did not start (try {trial.Record.FailedStarts} of {UpdateRollback.StartsAllowed}), and {rollback.RunningNode} could not be started again";
        say($"pilot-node: {line}");
        AppendStartLog(options, $"pilot-node {FileJournal.RunningVersion} {line}{ErrorOf(trial)}");
        return started ? 1 : null;
    }

    private static string ErrorOf(UpdateTrial trial) => trial.Record.LastError is { } error ? $"; the last error: {error}" : "";

    // The previous release back in place and its node started: this one exits. Null when it could not be put back, and the start goes on as it would have.
    private static int? GoBack(NodeOptions options, Action<string> say, UpdateRollback rollback, UpdateTrial trial)
    {
        var restore = rollback.Restore(trial.Record);
        say($"pilot-node: update: {restore.Message}");
        AppendStartLog(options, $"pilot-node {FileJournal.RunningVersion} update: {restore.Message}{ErrorOf(trial)}");
        return restore.Restored ? NodeExitCodes.Stopped : null;
    }

    // A node started at logon, or by the update that replaced its predecessor, writes to a pipe nobody reads, so the line is kept in a file too.
    private static void LogRefusal(NodeOptions options, StoreTooNewException refused) =>
        AppendStartLog(options, $"pilot-node {FileJournal.RunningVersion} did not start: {refused.Message}");

    /// <summary>Appends one dated line to <see cref="StartLogFile"/> in the data folder; a file that cannot be written changes nothing about what the line says.</summary>
    internal static void AppendStartLog(NodeOptions options, string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(options.DataFolder, StartLogFile), $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The line is in node.log (and on stderr when the log could not take it).
        }
    }
}
