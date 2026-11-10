using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// One attempt of a cloud bench: the state of one VM from its create to its delete (<see cref="AzureBench"/> makes one per call, so the bench
/// itself holds none). The flow is here, the cleanup in <c>CloudBenchAttempt.Cleanup.cs</c>.
/// </summary>
internal sealed partial class CloudBenchAttempt(ICloudProcess process, IRetailPrices prices, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay, CloudBenchRequest request)
{
    private const string User = "pilot";
    private const string KeyFile = "id_bench";
    private const string KnownHostsFile = "known_hosts";
    private const int ConnectTries = 18;
    private const int ProbeTries = 3;

    private static readonly TimeSpan ConnectPause = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbePause = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(30);

    private readonly BenchCloud _cloud = request.Cloud;
    private readonly CloudBenchNames _names = CloudBenchNames.For(request.JobId);
    private readonly string _tmp = Path.GetFullPath(request.TmpFolder);
    private readonly List<string> _leaked = [];
    private readonly List<string> _swept = [];

    private CloudBenchStage _stage = CloudBenchStage.Login;
    private bool _created;
    private DateTimeOffset _createdAt;
    private DateTimeOffset? _vmEndedAt;
    private string _image = request.Cloud.Image;
    private string? _ip;
    private double? _price;
    private bool _priceIsUpperBound;

    /// <summary>How the flow ended, before the VM is deleted and its minutes are known.</summary>
    private sealed record Ending(CloudBenchOutcome Outcome, CloudBenchStage Stage, int? ExitCode = null, string? Error = null);

    private static Ending Failed(CloudBenchStage stage, string error, int? exitCode = null) => new(CloudBenchOutcome.Failed, stage, exitCode, error);

    public async Task<CloudBenchResult> RunAsync(CancellationToken cancellationToken)
    {
        Ending ending;
        try
        {
            ending = await FlowAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ending = new Ending(CloudBenchOutcome.Cancelled, _stage, Error: "the bench was cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            ending = Failed(_stage, ex.Message);
        }
        finally
        {
            // Whatever happened above, nothing made in Azure is left, and the job's tmp keeps only the results.
            await CleanupAsync();
        }
        return Result(ending);
    }

    private CloudBenchResult Result(Ending ending)
    {
        double? minutes = _created ? ((_vmEndedAt ?? time.GetUtcNow()) - _createdAt).TotalMinutes : null;
        var vm = new BenchVm
        {
            Size = _cloud.VmSize,
            Image = _image,
            Region = _cloud.Region,
            PricePerHour = _price,
            PriceIsUpperBound = _priceIsUpperBound,
            Minutes = minutes,
            Cost = minutes is { } m && _price is { } p ? m / 60 * p : null,
            Evictions = ending.Outcome == CloudBenchOutcome.Evicted ? 1 : 0,
        };
        if (_leaked.Count > 0)
        {
            Say($"=== NOT DELETED, delete by hand in resource group {_cloud.ResourceGroup} (they cost money until then): {string.Join(", ", _leaked)} ===");
        }
        if (minutes is { } shown)
        {
            Say($"=== the VM ran {shown:0.##} minutes{(vm.Cost is { } cost ? $", {(_priceIsUpperBound ? "at most " : "")}${cost:0.0####}" : "")} ===");
        }
        return new CloudBenchResult
        {
            Outcome = ending.Outcome,
            Stage = ending.Stage,
            ExitCode = ending.ExitCode,
            Error = ending.Error is { } error ? Redact(error) : null,
            Vm = vm,
            ResultsFolder = Path.Combine(_tmp, AzureBench.ResultsFolderName),
            Leaked = [.. _leaked],
        };
    }

    private async Task<Ending> FlowAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_tmp);
        var results = Path.Combine(_tmp, AzureBench.ResultsFolderName);
        if (Directory.Exists(results))
        {
            Directory.Delete(results, recursive: true);
        }
        Directory.CreateDirectory(results);
        Say($"=== cloud bench {request.JobId}: spot {_cloud.VmSize} in {_cloud.Region} ({_cloud.Image}), at most ${AzureRetailPrices.Show(_cloud.SpotPricePerHour)} an hour, resource group {_cloud.ResourceGroup} ===");

        if (await LoginAsync(cancellationToken) is { } loginError)
        {
            return Failed(CloudBenchStage.Login, loginError);
        }
        _stage = CloudBenchStage.Preparation;
        await ReadPriceAsync(cancellationToken);
        if (await BundleAsync(cancellationToken) is { } bundleError)
        {
            return Failed(CloudBenchStage.Preparation, bundleError);
        }
        if (await MakeKeyAsync(cancellationToken) is { } keyError)
        {
            return Failed(CloudBenchStage.Preparation, keyError);
        }

        _stage = CloudBenchStage.Create;
        if (await CreateVmAsync(cancellationToken) is { } createError)
        {
            return Failed(CloudBenchStage.Create, createError);
        }

        _stage = CloudBenchStage.Connect;
        if (!await WaitForSshAsync(cancellationToken))
        {
            return await LostOrFailedAsync(CloudBenchStage.Connect, "the VM never answered on ssh", cancellationToken);
        }

        _stage = CloudBenchStage.Ship;
        var script = Path.Combine(_tmp, CloudBenchScript.ScriptFile);
        await File.WriteAllTextAsync(script, CloudBenchScript.For(request, _names), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        var shipped = await ExecAsync(Scp(false, [CloudBenchScript.BundleFile, CloudBenchScript.ScriptFile, $"{User}@{_ip}:"], Long), cancellationToken);
        if (!shipped.Ok)
        {
            return await LostOrFailedAsync(CloudBenchStage.Ship, $"the bundle could not be copied to the VM: {shipped.Why}", cancellationToken);
        }

        _stage = CloudBenchStage.Run;
        Say("=== running on the VM ===");
        var ran = await ExecAsync(Ssh([$"bash {CloudBenchScript.ScriptFile}"], Timeout.InfiniteTimeSpan, Say), cancellationToken);
        if (ran.ExitCode == SshLost)
        {
            return await LostOrFailedAsync(CloudBenchStage.Run, "the ssh connection to the VM was lost", cancellationToken);
        }
        if (ran.ExitCode < 0)
        {
            return Failed(CloudBenchStage.Run, ran.Why, ran.ExitCode);
        }

        _stage = CloudBenchStage.Collect;
        var collectError = await CollectAsync(cancellationToken);
        if (ran.ExitCode != 0)
        {
            if (collectError is not null)
            {
                Say($"=== {collectError} ===");
            }
            return Failed(CloudBenchStage.Run, $"the benchmark failed (exit {ran.ExitCode.ToString(CultureInfo.InvariantCulture)})", ran.ExitCode);
        }
        if (collectError is not null)
        {
            return await LostOrFailedAsync(CloudBenchStage.Collect, collectError, cancellationToken);
        }
        return new Ending(CloudBenchOutcome.Succeeded, CloudBenchStage.Done, 0);
    }

    // ssh's own exit code when the connection fails or drops (a remote exit code is 0 to 254 here: the script clamps it).
    private const int SshLost = 255;

    private async Task<string?> LoginAsync(CancellationToken cancellationToken)
    {
        if (_cloud.ServicePrincipal)
        {
            // A config folder of its own, so the secret az keeps for a service principal is in the job's tmp (deleted after) and not in the machine's ~/.azure.
            var config = ConfigFolder;
            Directory.CreateDirectory(config);
            _azEnvironment["AZURE_CONFIG_DIR"] = config;
            var login = await ExecAsync(Az(["login", "--service-principal", "--username", _cloud.AppId!, "--password", _cloud.Secret!, "--tenant", _cloud.Tenant!, "--allow-no-subscriptions", "--output", "none", "--only-show-errors"]), cancellationToken);
            return login.Ok ? null : $"az login as the service principal {_cloud.AppId} failed: {login.Why}";
        }
        var account = await ExecAsync(Az(["account", "show", "--subscription", _cloud.Subscription, "--output", "none", "--only-show-errors"]), cancellationToken);
        return account.Ok ? null : $"az is not signed in for subscription {_cloud.Subscription} (run az login, or set the service principal): {account.Why}";
    }

    private async Task ReadPriceAsync(CancellationToken cancellationToken)
    {
        double? read = null;
        try
        {
            read = await prices.SpotPerHourAsync(_cloud.Region, _cloud.VmSize, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            // The max price below stands in.
        }
        _priceIsUpperBound = read is not > 0;
        _price = _priceIsUpperBound ? _cloud.SpotPricePerHour : read;
        Say(_priceIsUpperBound
            ? $"=== the retail price of {_cloud.VmSize} spot in {_cloud.Region} could not be read: the max price ${AzureRetailPrices.Show(_cloud.SpotPricePerHour)} an hour stands as the upper bound ==="
            : $"=== spot price ${AzureRetailPrices.Show(read!.Value)} an hour (Azure retail prices) ===");
    }

    // A bundle holds refs, not bare commits, so each commit gets a ref of its own under refs/pilot-bench for as long as the bundle takes.
    private async Task<string?> BundleAsync(CancellationToken cancellationToken)
    {
        var bundle = Path.Combine(_tmp, CloudBenchScript.BundleFile);
        var made = new List<string>();
        try
        {
            foreach (var leg in request.Refs)
            {
                var name = _names.TempRef(leg.Label);
                var update = await ExecAsync(Git(["update-ref", name, leg.Sha]), cancellationToken);
                if (!update.Ok)
                {
                    return $"git could not hold {leg.Ref} ({CommitRefs.Short(leg.Sha)}) for the bundle: {update.Why}";
                }
                made.Add(name);
            }
            var create = await ExecAsync(Git(["bundle", "create", bundle, .. made]), cancellationToken);
            return create.Ok ? null : $"git could not bundle the refs: {create.Why}";
        }
        finally
        {
            foreach (var name in made)
            {
                await process.RunAsync(Git(["update-ref", "-d", name]), CancellationToken.None);
            }
        }
    }

    private async Task<string?> MakeKeyAsync(CancellationToken cancellationToken)
    {
        // ssh-keygen would ask before it overwrote a key a former attempt left.
        DeleteFile(KeyFile);
        DeleteFile(KeyFile + ".pub");
        var key = await ExecAsync(new CloudCommand("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", _names.Prefix.TrimEnd('-'), "-f", KeyFile], _tmp, Short), cancellationToken);
        return key.Ok ? null : $"ssh-keygen could not make the key: {key.Why}";
    }

    private async Task<string?> CreateVmAsync(CancellationToken cancellationToken)
    {
        // From here the VM may exist, even when the call fails: it is cleaned up whatever this says.
        _created = true;
        _createdAt = time.GetUtcNow();
        var create = await ExecAsync(AzOf(
            [
                "vm", "create",
                "--name", _names.Vm,
                "--location", _cloud.Region,
                "--image", _cloud.Image,
                "--size", _cloud.VmSize,
                "--priority", "Spot",
                "--max-price", _cloud.SpotPricePerHour.ToString("0.#####", CultureInfo.InvariantCulture),
                "--eviction-policy", "Delete",
                "--admin-username", User,
                "--authentication-type", "ssh",
                "--ssh-key-values", KeyFile + ".pub",
                "--public-ip-address", _names.PublicIp,
                "--public-ip-sku", "Standard",
                "--nsg", _names.Nsg,
                "--vnet-name", _names.Vnet,
                "--subnet", "default",
                "--os-disk-name", _names.Disk,
                "--os-disk-delete-option", "Delete",
                "--nic-delete-option", "Delete",
                "--tags", $"pilot-bench={_names.Id}",
                "--output", "json",
                "--only-show-errors",
            ], Long), cancellationToken);
        if (!create.Ok)
        {
            return $"az vm create failed: {create.Why}";
        }
        _ip = PublicIpOf(create.Output);
        if (_ip is null)
        {
            return "az vm create answered no public IP address for the VM";
        }
        Say($"=== the VM {_names.Vm} is up at {_ip} ===");
        if (_cloud.Image.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            // The version `latest` was resolved to, so two runs on the same image can be told from two on another.
            var version = await ExecAsync(AzOf(["vm", "show", "--name", _names.Vm, "--query", "storageProfile.imageReference.exactVersion", "--output", "tsv", "--only-show-errors"], Short), cancellationToken);
            if (version.Ok && version.Output.Trim() is { Length: > 0 } exact && !exact.Contains(' ', StringComparison.Ordinal))
            {
                _image = _cloud.Image[..^"latest".Length] + exact;
            }
        }
        return null;
    }

    private static string? PublicIpOf(string output)
    {
        // az may print a line of its own before the JSON.
        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(output[start..]);
            return document.RootElement.TryGetProperty("publicIpAddress", out var ip) && ip.ValueKind == JsonValueKind.String && ip.GetString() is { Length: > 0 } address ? address : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> WaitForSshAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ConnectTries; attempt++)
        {
            var hello = await ExecAsync(Ssh(["true"], Short), cancellationToken, quiet: true);
            if (hello.Ok)
            {
                return true;
            }
            if (hello.ExitCode < 0)
            {
                // ssh is not installed: waiting will not make it so.
                Say($"=== {hello.Why} ===");
                return false;
            }
            if (attempt < ConnectTries)
            {
                await delay(ConnectPause, cancellationToken);
            }
        }
        return false;
    }

    private async Task<string?> CollectAsync(CancellationToken cancellationToken)
    {
        var failed = new List<string>();
        foreach (var leg in request.Refs)
        {
            var copy = await ExecAsync(Scp(true, [$"{User}@{_ip}:{CloudBenchScript.OutFolder}/{leg.Label}", $"{AzureBench.ResultsFolderName}/"], Long), cancellationToken);
            if (!copy.Ok)
            {
                failed.Add($"{leg.Label}: {copy.Why}");
            }
        }
        return failed.Count == 0 ? null : $"the results could not be copied back ({string.Join("; ", failed)})";
    }

    /// <summary>
    /// Ends the attempt after a failure with the VM in doubt. Azure is asked about the VM, and when it is gone or deallocated (the node never
    /// stops it, so Azure did) that is an eviction and not a failure of the bench; when it is running, or Azure will not say, it is the failure it
    /// looks like. A lost connection is asked about again for a minute, since Azure's eviction notice comes before the VM goes.
    /// </summary>
    private async Task<Ending> LostOrFailedAsync(CloudBenchStage stage, string reason, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ProbeTries; attempt++)
        {
            var state = await ExecAsync(AzOf(["vm", "get-instance-view", "--name", _names.Vm, "--query", "instanceView.statuses[?starts_with(code, 'PowerState/')].code | [0]", "--output", "tsv", "--only-show-errors"], Short), cancellationToken, quiet: true);
            var power = state.Output.Trim();
            if (!state.Ok && IsNotFound(state))
            {
                Say("=== the VM is gone: Azure took it back ===");
                return Evicted(stage, $"the spot VM was evicted by Azure while the bench ran ({reason})");
            }
            if (state.Ok && (power.Contains("deallocat", StringComparison.OrdinalIgnoreCase) || power.Contains("stop", StringComparison.OrdinalIgnoreCase)))
            {
                Say($"=== the VM is {power.Replace("PowerState/", "", StringComparison.Ordinal)}: Azure took it back ===");
                return Evicted(stage, $"the spot VM was evicted by Azure while the bench ran ({reason}; it is {power})");
            }
            if (attempt < ProbeTries)
            {
                await delay(ProbePause, cancellationToken);
            }
        }
        return Failed(stage, reason);
    }

    private static Ending Evicted(CloudBenchStage stage, string error) => new(CloudBenchOutcome.Evicted, stage, Error: error);

    // What az says of a resource that is not there.
    private static bool IsNotFound(CloudProcessRun run) =>
        run.ExitCode > 0 && (run.Error.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase)
            || run.Error.Contains("was not found", StringComparison.OrdinalIgnoreCase)
            || run.Error.Contains("could not be found", StringComparison.OrdinalIgnoreCase)
            || run.Error.Contains("NotFound", StringComparison.Ordinal));
}
