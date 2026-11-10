using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>One ref of a cloud bench: its label in the log (<c>baseline</c>, <c>candidate</c> or <c>ref</c>), the ref as it was given, and its commit.</summary>
public sealed record CloudBenchRef(string Label, string Ref, string Sha);

/// <summary>What one benchmark needs from a cloud VM (#823): the refs, the commands and the folders; everything else comes from <see cref="Cloud"/>.</summary>
public sealed record CloudBenchRequest
{
    /// <summary>The bench job's id: it is in the name of every Azure resource made, so a leftover is found by it.</summary>
    public required string JobId { get; init; }

    /// <summary>Where and how the VM is made: the node's saved settings.</summary>
    public required BenchCloud Cloud { get; init; }

    /// <summary>The repo's main checkout, where the git bundle of the refs is made. Planes are not pushed, so the VM never clones from GitHub.</summary>
    public required string RepoPath { get; init; }

    /// <summary>The refs, in the order they run: a baseline then a candidate, or the one ref.</summary>
    public required IReadOnlyList<CloudBenchRef> Refs { get; init; }

    /// <summary>The repo's fetch step, run (by <c>bash</c>, on the VM) in each ref's clone before the benchmark; null for none.</summary>
    public string? Fetch { get; init; }

    /// <summary>The benchmark command's words, run in each ref's clone. They are quoted for <c>sh</c> as <see cref="ShellLine"/> does.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>
    /// The build run in each clone before the fetch step and the benchmark: null builds the one solution at the root or under <c>src</c> in Release
    /// (and nothing when there is none, the benchmark command then builds), an empty string builds nothing, anything else is run as given.
    /// </summary>
    public string? BuildCommand { get; init; }

    /// <summary>
    /// Paths (relative to the clone's root, no <c>..</c>) copied back besides every <c>BenchmarkDotNet.Artifacts/results</c> folder, which always is.
    /// </summary>
    public IReadOnlyList<string> ResultPaths { get; init; } = [];

    /// <summary>
    /// The job's tmp folder: the ssh key, the bundle and the scripts live there while the VM does, and the files copied back are left in it
    /// (<see cref="AzureBench.ResultsFolderOf"/>). It is made when missing.
    /// </summary>
    public required string TmpFolder { get; init; }

    /// <summary>Where the bench's lines go: the job's log. Called from several threads at once; secrets are already left out.</summary>
    public required Action<string> Log { get; init; }
}

/// <summary>How a cloud bench ended.</summary>
public enum CloudBenchOutcome
{
    /// <summary>The benchmark ran on the VM and exited 0, and its results were copied back.</summary>
    Succeeded,

    /// <summary>The bench did not succeed for a reason of its own: a build, fetch or benchmark that failed, or a step of the cloud that failed with the VM still there. See <see cref="CloudBenchResult.Stage"/>.</summary>
    Failed,

    /// <summary>Azure took the spot VM back (it is gone or deallocated, and was not the node who did it) while the bench ran: not a failure of the bench, so it may run again.</summary>
    Evicted,

    /// <summary>The caller cancelled it. The VM and its resources are gone all the same.</summary>
    Cancelled,
}

/// <summary>The step a cloud bench had reached when it ended, in the order they run.</summary>
public enum CloudBenchStage
{
    /// <summary>Signing in to Azure (<c>az login</c> as the service principal, or the check that the machine's own login works).</summary>
    Login,

    /// <summary>The spot price, the git bundle of the refs and the ssh key.</summary>
    Preparation,

    /// <summary><c>az vm create</c>.</summary>
    Create,

    /// <summary>Waiting for the VM's ssh to answer.</summary>
    Connect,

    /// <summary>Copying the bundle and the script to the VM.</summary>
    Ship,

    /// <summary>The script on the VM: the .NET SDK, the clones, the builds, the fetch steps and the benchmarks.</summary>
    Run,

    /// <summary>Copying the result files back.</summary>
    Collect,

    /// <summary>All of it ran.</summary>
    Done,
}

/// <summary>What a cloud bench left: how it ended, the VM it ran on, the results folder and anything it could not delete.</summary>
public sealed record CloudBenchResult
{
    public required CloudBenchOutcome Outcome { get; init; }

    /// <summary>The step it ended in; <see cref="CloudBenchStage.Done"/> for a success. A stage before <see cref="CloudBenchStage.Run"/> means the benchmark never started.</summary>
    public CloudBenchStage Stage { get; init; } = CloudBenchStage.Done;

    /// <summary>The exit code of the script on the VM when it ran to its end (the last failing ref's code); null when it did not.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Why it did not succeed, in a line, secrets left out; null for a success.</summary>
    public string? Error { get; init; }

    /// <summary>The VM as it ran, for the job row: size, image, region, price per hour, minutes from create to delete, cost, evictions (one for this attempt when evicted).</summary>
    public required BenchVm Vm { get; init; }

    /// <summary>The folder the result files were copied into, one folder per ref label under it (<see cref="AzureBench.ResultsFolderOf"/>).</summary>
    public required string ResultsFolder { get; init; }

    /// <summary>Azure resources that could not be deleted, by name or id. Each one costs money until a person deletes it, so a caller says so loudly.</summary>
    public IReadOnlyList<string> Leaked { get; init; } = [];

    /// <summary>Whether the script on the VM was started: false means a bench that never began, which a caller may run on the node's machine instead.</summary>
    public bool Started => Stage >= CloudBenchStage.Run;
}

/// <summary>A cloud bench run: one VM for the one bench, always deleted after.</summary>
public interface ICloudBench
{
    /// <summary>
    /// Runs the benchmark on a fresh spot VM and deletes the VM and everything made with it however it ends. A cancel ends it with
    /// <see cref="CloudBenchOutcome.Cancelled"/> after the cleanup, not with an exception, so the money spent is still told.
    /// </summary>
    Task<CloudBenchResult> RunAsync(CloudBenchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes what a bench left in Azure when the node that ran it is gone (a crash, or a stop that did not wait for the delete): the VM and each
    /// resource by the names its job id gives, then everything in the resource group that starts with them. Never cancelled by the caller's stop
    /// halfway: it ends with what it deleted and what it could not.
    /// </summary>
    Task<CloudCleanupResult> CleanUpAsync(CloudCleanupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CloudCleanupResult { Error = "this runner cannot delete what an earlier node left" });
}

/// <summary>What a node that takes up a lost cloud bench needs to delete its Azure resources: the job, where they are and a folder to work in.</summary>
public sealed record CloudCleanupRequest
{
    public required string JobId { get; init; }

    public required BenchCloud Cloud { get; init; }

    /// <summary>The job's tmp folder, where the service principal's az config folder is made (and removed again).</summary>
    public required string TmpFolder { get; init; }

    public required Action<string> Log { get; init; }
}

/// <summary>What a cleanup of a lost cloud bench did.</summary>
public sealed record CloudCleanupResult
{
    /// <summary>Why nothing could be deleted (az not signed in, the group not listable); null when the deletes ran.</summary>
    public string? Error { get; init; }

    /// <summary>What the sweep found by prefix after the named deletes (deleted by id); empty when the names had taken it all.</summary>
    public IReadOnlyList<string> Swept { get; init; } = [];

    /// <summary>Resources that could not be deleted, by name or id; each costs money until a person deletes it.</summary>
    public IReadOnlyList<string> Leaked { get; init; } = [];

    public bool Clean => Error is null && Leaked.Count == 0;
}

public static class CloudBenches
{
    /// <summary>What the log line that says a spot VM was evicted starts with, so the node's job can tell it from the bench's own output and put it in the step.</summary>
    public const string EvictedPrefix = "=== the spot VM was evicted by Azure";

    /// <summary>
    /// The bench with a spot eviction run again: Azure may take the VM back at any time, which says nothing about the bench, so after one
    /// the node starts it once more (<paramref name="attempts"/> in all) and says so in the log. The VM of the result adds up the minutes, the
    /// cost and the evictions of every attempt, and keeps the price of the last.
    /// </summary>
    public static async Task<CloudBenchResult> RunWithRetryAsync(this ICloudBench bench, CloudBenchRequest request, int attempts, CancellationToken cancellationToken)
    {
        BenchVm? earlier = null;
        for (var attempt = 1; ; attempt++)
        {
            var result = await bench.RunAsync(request, cancellationToken);
            var total = earlier is null ? result.Vm : Combine(earlier, result.Vm);
            if (result.Outcome != CloudBenchOutcome.Evicted || attempt >= attempts || cancellationToken.IsCancellationRequested)
            {
                return result with { Vm = total };
            }
            request.Log($"{EvictedPrefix}: starting the benchmark again ({attempt + 1} of {attempts}) ===");
            earlier = total;
        }
    }

    private static BenchVm Combine(BenchVm before, BenchVm last) => last with
    {
        Minutes = Sum(before.Minutes, last.Minutes),
        Cost = Sum(before.Cost, last.Cost),
        Evictions = before.Evictions + last.Evictions,
        PriceIsUpperBound = before.PriceIsUpperBound || last.PriceIsUpperBound,
    };

    private static double? Sum(double? a, double? b) => a is null ? b : b is null ? a : a + b;
}
