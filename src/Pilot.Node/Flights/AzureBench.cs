using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// <see cref="ICloudBench"/> through the <c>az</c> CLI, <c>ssh</c> and <c>scp</c> (#823): one spot VM per bench, made in the settings' subscription,
/// resource group and region with a throwaway ssh key, fed the refs as a git bundle (planes are never pushed, so the VM clones nothing from GitHub
/// and holds no credentials), and deleted with its NIC, disk, public IP, NSG and virtual network however the bench ends.
/// <para>
/// Every external command goes through the <see cref="ICloudProcess"/> seam and the spot price through <see cref="IRetailPrices"/>, so a test runs
/// the whole of it against fakes and nothing here reaches Azure from a test. The steps, in order: sign in (the service principal, in a config
/// folder of its own under the job's tmp, or the machine's own <c>az login</c>), read the spot price, bundle the refs, make the key, create the VM,
/// wait for ssh, copy the bundle and the script over, run the script (its output streams into the log as it comes), copy the results back, delete.
/// </para>
/// <para>
/// A failure after the VM exists is told from an eviction by asking Azure about the VM: gone, or deallocated or stopped when the node never
/// stopped it, is <see cref="CloudBenchOutcome.Evicted"/>; running is a bench that failed.
/// </para>
/// </summary>
public sealed class AzureBench(ICloudProcess process, IRetailPrices prices, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : ICloudBench
{
    /// <summary>The folder (under the job's tmp) the results are copied into, one folder per ref label.</summary>
    public const string ResultsFolderName = "results";

    /// <summary>The folder a ref's result files are in once the bench ended: <c>&lt;tmp&gt;/results/&lt;label&gt;</c>, laid out as in the clone, so <see cref="BenchRun"/>'s table gathering finds the <c>*-report-github.md</c> files in it.</summary>
    public static string ResultsFolderOf(string tmpFolder, string label) => Path.Combine(tmpFolder, ResultsFolderName, label);

    public async Task<CloudBenchResult> RunAsync(CloudBenchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Refs.Count == 0 || request.Command.Count == 0 || string.IsNullOrWhiteSpace(request.TmpFolder))
        {
            throw new ArgumentException("a cloud bench needs a ref, the benchmark command and a tmp folder", nameof(request));
        }
        foreach (var path in request.ResultPaths)
        {
            if (CloudBenchScript.WhyNotAResultPath(path) is { } why)
            {
                throw new ArgumentException(why, nameof(request));
            }
        }
        return await new CloudBenchAttempt(process, prices, time ?? TimeProvider.System, delay ?? DefaultDelay, request).RunAsync(cancellationToken);
    }

    public async Task<CloudCleanupResult> CleanUpAsync(CloudCleanupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new CloudBenchRequest
        {
            JobId = request.JobId,
            Cloud = request.Cloud,
            RepoPath = "",
            Refs = [],
            Command = [],
            TmpFolder = request.TmpFolder,
            Log = request.Log,
        };
        return await new CloudBenchAttempt(process, prices, time ?? TimeProvider.System, delay ?? DefaultDelay, run).CleanUpAsync(cancellationToken);
    }

    private static Task DefaultDelay(TimeSpan span, CancellationToken cancellationToken) => Task.Delay(span, cancellationToken);
}
