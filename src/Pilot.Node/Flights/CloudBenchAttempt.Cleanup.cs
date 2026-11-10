namespace Pilot.Node.Flights;

/// <summary>
/// The end of every attempt, whatever ended it (success, failure, cancel, eviction): the VM and what was made with it are deleted, and what
/// could not be is named, because it costs money until a person deletes it.
/// </summary>
internal sealed partial class CloudBenchAttempt
{
    private static readonly TimeSpan DeleteBudget = TimeSpan.FromMinutes(10);

    private async Task CleanupAsync()
    {
        try
        {
            if (_created)
            {
                await DeleteAzureAsync();
            }
        }
        finally
        {
            DeleteLocalFiles();
        }
    }

    /// <summary>The delete of a bench whose node is gone: sign in, then the same delete by name and sweep by prefix, with no VM of this attempt's own.</summary>
    public async Task<CloudCleanupResult> CleanUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_tmp);
            if (await LoginAsync(cancellationToken) is { } loginError)
            {
                return new CloudCleanupResult { Error = Redact(loginError) };
            }
            await DeleteAzureAsync();
            return new CloudCleanupResult { Swept = [.. _swept], Leaked = [.. _leaked] };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CloudCleanupResult { Error = "the cleanup was cancelled before it ended" };
        }
        finally
        {
            DeleteLocalFiles();
        }
    }

    // Never cancelled: a bench that was cancelled still deletes its VM. The VM goes first and the clock of its minutes stops with it, since the
    // rest of what was made costs next to nothing; then each resource by name, then the sweep for whatever the names missed.
    private async Task DeleteAzureAsync()
    {
        Say("=== deleting the VM and everything made with it ===");
        var failed = new List<string>();
        await DeleteAsync(_names.Vm, ["vm", "delete", "--name", _names.Vm, "--yes"], failed);
        _vmEndedAt = time.GetUtcNow();
        await DeleteAsync(_names.Nic, ["network", "nic", "delete", "--name", _names.Nic], failed);
        await DeleteAsync(_names.PublicIp, ["network", "public-ip", "delete", "--name", _names.PublicIp], failed);
        await DeleteAsync(_names.Nsg, ["network", "nsg", "delete", "--name", _names.Nsg], failed);
        await DeleteAsync(_names.Disk, ["disk", "delete", "--name", _names.Disk, "--yes"], failed);
        await DeleteAsync(_names.Vnet, ["network", "vnet", "delete", "--name", _names.Vnet], failed);

        // The list is the truth: what it does not show is gone, whatever a delete said, and what it shows is deleted by id.
        var list = await ExecAsync(Az(["resource", "list", "--subscription", _cloud.Subscription, "--resource-group", _cloud.ResourceGroup,
            "--query", $"[?starts_with(name, '{_names.Prefix}')].id", "--output", "tsv", "--only-show-errors"], DeleteBudget), CancellationToken.None, quiet: true);
        if (!list.Ok)
        {
            Say($"  could not list what is left in {_cloud.ResourceGroup}: {list.Why}");
            _leaked.AddRange(failed);
            return;
        }
        var left = list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (left.Length == 0)
        {
            return;
        }
        _swept.AddRange(left);
        var sweep = await ExecAsync(Az(["resource", "delete", "--ids", .. left, "--only-show-errors"], DeleteBudget), CancellationToken.None);
        if (!sweep.Ok)
        {
            _leaked.AddRange(left);
        }
    }

    // A resource that is not there is deleted already (the VM's own delete options take the NIC and the disk with it, and an eviction takes the VM).
    private async Task DeleteAsync(string name, IReadOnlyList<string> args, List<string> failed)
    {
        var run = await ExecAsync(AzOf([.. args, "--only-show-errors"], DeleteBudget), CancellationToken.None);
        if (!run.Ok && !IsNotFound(run))
        {
            failed.Add(name);
        }
    }

    private void DeleteLocalFiles()
    {
        foreach (var file in new[] { KeyFile, KeyFile + ".pub", KnownHostsFile, CloudBenchScript.BundleFile, CloudBenchScript.ScriptFile })
        {
            DeleteFile(file);
        }
        try
        {
            if (Directory.Exists(ConfigFolder))
            {
                Directory.Delete(ConfigFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left in the job's tmp, which goes with the job; it holds the token az kept, so the log says so.
            Say($"  the az config folder {ConfigFolder} could not be deleted: {ex.Message}");
        }
    }
}
