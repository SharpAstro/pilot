using System.Net.Http;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The cloud bench (#823) over a fake <c>az</c>, <c>ssh</c>, <c>scp</c> and <c>git</c> and a fake price list: the command lines it issues, the VM
/// and its resources deleted however the bench ends, an eviction told from a failed bench, the minutes and the price kept, and no secret in the
/// log. Nothing here starts a process that matters or reaches Azure.
/// </summary>
public sealed partial class AzureBenchTests : IDisposable
{
    private const string Subscription = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "s3cr3t~Value&x";
    private const string FirstSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string SecondSha = "f6e5d4c3b2a10987654321fedcba098765432100";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pilot-azurebench-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeCloud _cloud = new();
    private readonly FakePrices _prices = new() { Price = 0.07 };
    private readonly List<string> _log = [];
    private readonly List<TimeSpan> _pauses = [];

    public AzureBenchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp folder goes with the machine's next clean-up.
        }
        GC.SuppressFinalize(this);
    }

    private string Tmp => Path.Combine(_root, "tmp");

    private IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
            {
                return [.. _log];
            }
        }
    }

    private void Say(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    private AzureBench Bench() => new(_cloud, _prices, _cloud.Clock, (span, _) =>
    {
        lock (_pauses)
        {
            _pauses.Add(span);
        }
        return Task.CompletedTask;
    });

    private static BenchCloud Cloud(bool servicePrincipal = false, double spot = 0.3) => new NodeSettings
    {
        BenchSubscription = Subscription,
        BenchResourceGroup = "rg-bench",
        BenchRegion = "westeurope",
        BenchVmSize = "Standard_F8s_v2",
        BenchImage = "Canonical:ubuntu-24_04-lts:server:latest",
        BenchSpotPrice = spot,
        BenchAuth = servicePrincipal ? NodeSettings.AuthServicePrincipal : null,
        BenchSpAppId = servicePrincipal ? "app-1234" : null,
        BenchSpTenant = servicePrincipal ? "tenant-5678" : null,
        BenchSpSecret = servicePrincipal ? Secret : null,
    }.BenchCloudOrWhy().Cloud.ShouldNotBeNull();

    private CloudBenchRequest Request(BenchCloud? cloud = null, string? build = null, IReadOnlyList<string>? paths = null, string? fetch = "echo fetching") => new()
    {
        JobId = "j7",
        Cloud = cloud ?? Cloud(),
        RepoPath = Path.Combine(_root, "repo"),
        Refs = [new CloudBenchRef("baseline", "main", FirstSha), new CloudBenchRef("candidate", "flight/pil823", SecondSha)],
        Fetch = fetch,
        Command = ["dotnet", "run", "-c", "Release", "--project", "bench/B", "--", "--filter", "*"],
        BuildCommand = build,
        ResultPaths = paths ?? [],
        TmpFolder = Tmp,
        Log = Say,
    };

    private IReadOnlyList<CloudCommand> Commands => [.. _cloud.Seen.Select(s => s.Command)];

    private int IndexOf(Func<CloudCommand, bool> match) => Commands.ToList().FindIndex(c => match(c));

    private CloudCommand Only(Func<CloudCommand, bool> match, string what) => Commands.Where(match).ToList() is [var one] ? one : throw new ShouldAssertException($"expected one {what}, got: {string.Join("; ", Commands.Where(match).Select(c => c.Line))}");

    private static bool Delete(CloudCommand c, string resource) => c.Is("az") && c.Args.Contains("delete") && c.Args.Contains(resource);

    [Fact]
    public async Task AFullRunMakesASpotVmFromTheSettingsRunsTheBenchAndCopiesTheResultsBack()
    {
        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(CloudBenchOutcome.Succeeded),
            r => r.Stage.ShouldBe(CloudBenchStage.Done),
            r => r.ExitCode.ShouldBe(0),
            r => r.Error.ShouldBeNull(),
            r => r.Started.ShouldBeTrue(),
            r => r.Leaked.ShouldBeEmpty());

        // The VM: spot, at most the settings' price, deleted by Azure's policy when evicted, in the settings' place, with an ssh key and no password.
        var create = Only(c => c.Is("az", "vm", "create"), "az vm create");
        create.After("--priority").ShouldBe("Spot");
        create.After("--max-price").ShouldBe("0.3");
        create.After("--eviction-policy").ShouldBe("Delete");
        create.After("--size").ShouldBe("Standard_F8s_v2");
        create.After("--image").ShouldBe("Canonical:ubuntu-24_04-lts:server:latest");
        create.After("--location").ShouldBe("westeurope");
        create.After("--subscription").ShouldBe(Subscription);
        create.After("--resource-group").ShouldBe("rg-bench");
        create.After("--name").ShouldBe("pilot-bench-j7-vm");
        create.After("--authentication-type").ShouldBe("ssh");
        create.After("--ssh-key-values").ShouldBe("id_bench.pub");
        create.After("--os-disk-delete-option").ShouldBe("Delete");
        create.After("--nic-delete-option").ShouldBe("Delete");

        // Each step after the one before it: sign-in, bundle, key, VM, ssh, the copy over, the run, the copy back, the delete.
        var steps = new[]
        {
            IndexOf(c => c.Is("az", "account", "show")),
            IndexOf(c => c.Is("git", "bundle", "create")),
            IndexOf(c => c.Program == "ssh-keygen"),
            IndexOf(c => c.Is("az", "vm", "create")),
            IndexOf(c => c.Program == "ssh" && c.Remote() == "true"),
            IndexOf(c => c.Program == "scp" && !c.Args.Contains("-r")),
            IndexOf(c => c.Program == "ssh" && c.Remote() == "bash bench.sh"),
            IndexOf(c => c.Program == "scp" && c.Args.Contains("-r")),
            IndexOf(c => c.Is("az", "vm", "delete")),
        };
        steps.ShouldAllBe(i => i >= 0);
        steps.ShouldBe([.. steps.Order()]);

        // The bundle holds a ref per commit, made and removed around it; the copy over takes the bundle and the script to the VM's home.
        Commands.Where(c => c.Is("git", "update-ref") && !c.Args.Contains("-d")).Select(c => c.Line).ShouldBe(
            [$"git update-ref refs/pilot-bench/j7/baseline {FirstSha}", $"git update-ref refs/pilot-bench/j7/candidate {SecondSha}"]);
        Commands.Count(c => c.Is("git", "update-ref", "-d")).ShouldBe(2);
        Only(c => c.Is("git", "bundle", "create"), "git bundle").Args.ShouldBe(
            ["bundle", "create", Path.Combine(Tmp, "bench.bundle"), "refs/pilot-bench/j7/baseline", "refs/pilot-bench/j7/candidate"]);
        Only(c => c.Is("git", "bundle", "create"), "git bundle").WorkingDirectory.ShouldBe(Path.Combine(_root, "repo"));
        Only(c => c.Program == "scp" && !c.Args.Contains("-r"), "scp over").Args.TakeLast(3).ShouldBe(["bench.bundle", "bench.sh", $"pilot@{FakeCloud.PublicIp}:"]);
        Commands.Where(c => c.Program == "scp" && c.Args.Contains("-r")).Select(c => c.Args[^2]).ShouldBe(
            [$"pilot@{FakeCloud.PublicIp}:out/baseline", $"pilot@{FakeCloud.PublicIp}:out/candidate"]);

        // The bench's own output streamed into the log as it came, and the results are where BenchRun's table gathering looks.
        Log.ShouldContain("=== bench baseline: main (abc1234) ===");
        Log.ShouldContain("| Mean | 1.00 ms |");
        foreach (var label in new[] { "baseline", "candidate" })
        {
            File.ReadAllText(Path.Combine(AzureBench.ResultsFolderOf(Tmp, label), "BenchmarkDotNet.Artifacts", "results", "Sample.Bench-report-github.md")).ShouldContain($"| {label} |");
        }
        result.ResultsFolder.ShouldBe(Path.Combine(Tmp, "results"));

        // The key, the bundle and the script are gone with the VM; only the results stay.
        Directory.EnumerateFileSystemEntries(Tmp).Select(Path.GetFileName).ShouldBe(["results"]);

        result.Vm.ShouldSatisfyAllConditions(
            v => v.Size.ShouldBe("Standard_F8s_v2"),
            v => v.Region.ShouldBe("westeurope"),
            v => v.Evictions.ShouldBe(0),
            // `latest` is told as the version it resolved to.
            v => v.Image.ShouldBe("Canonical:ubuntu-24_04-lts:server:24.04.202411010"));
    }

    public enum Ending
    {
        Success,
        BenchFailed,
        Cancelled,
        Evicted,
        CreateFailed,
        ShipFailed,
    }

    [Theory]
    [InlineData(Ending.Success, CloudBenchOutcome.Succeeded)]
    [InlineData(Ending.BenchFailed, CloudBenchOutcome.Failed)]
    [InlineData(Ending.Cancelled, CloudBenchOutcome.Cancelled)]
    [InlineData(Ending.Evicted, CloudBenchOutcome.Evicted)]
    [InlineData(Ending.CreateFailed, CloudBenchOutcome.Failed)]
    [InlineData(Ending.ShipFailed, CloudBenchOutcome.Failed)]
    public async Task TheVmAndItsNicDiskIpNsgAndNetworkAreDeletedInEveryEnding(Ending ending, CloudBenchOutcome expected)
    {
        using var cancel = new CancellationTokenSource();
        switch (ending)
        {
            case Ending.BenchFailed:
                _cloud.On(c => c.Remote() == "bash bench.sh", _ => new CloudProcessRun(3, "", ""));
                break;
            case Ending.Cancelled:
                _cloud.On(c => c.Remote() == "bash bench.sh", (_, _) =>
                {
                    cancel.Cancel();
                    throw new OperationCanceledException(cancel.Token);
                });
                break;
            case Ending.Evicted:
                _cloud.On(c => c.Remote() == "bash bench.sh", _ => new CloudProcessRun(255, "", "Connection to 20.1.2.3 closed by remote host."));
                _cloud.On(c => c.Is("az", "vm", "get-instance-view"), _ => FakeCloud.NotFound());
                break;
            case Ending.CreateFailed:
                // The create may have made some of it before it failed; what was not made is "not found", which is no failure of the clean-up.
                _cloud.On(c => c.Is("az", "vm", "create"), _ => FakeCloud.Fail(1, "ERROR: (SkuNotAvailable) The requested VM size is not available in westeurope."));
                _cloud.On(c => c.Is("az") && c.Args.Contains("delete"), _ => FakeCloud.NotFound());
                break;
            case Ending.ShipFailed:
                _cloud.On(c => c.Program == "scp" && !c.Args.Contains("-r"), _ => FakeCloud.Fail(1, "scp: Permission denied"));
                break;
        }

        var result = await Bench().RunAsync(Request(), cancel.Token);

        result.Outcome.ShouldBe(expected);
        result.Leaked.ShouldBeEmpty();
        var resources = new[] { "pilot-bench-j7-vm", "pilot-bench-j7-vmVMNic", "pilot-bench-j7-ip", "pilot-bench-j7-nsg", "pilot-bench-j7-disk", "pilot-bench-j7-vnet" };
        foreach (var resource in resources)
        {
            var deletion = _cloud.Seen.Where(s => Delete(s.Command, resource)).ToList();
            deletion.Count.ShouldBe(1, $"{resource} deleted once");
            // The delete is not stopped by a cancel, and is made where the VM was.
            deletion[0].TokenCancelled.ShouldBeFalse();
            deletion[0].Command.After("--subscription").ShouldBe(Subscription);
            deletion[0].Command.After("--resource-group").ShouldBe("rg-bench");
        }
        var kinds = new[] { new[] { "vm", "delete" }, ["network", "nic", "delete"], ["network", "public-ip", "delete"], ["network", "nsg", "delete"], ["disk", "delete"], ["network", "vnet", "delete"] };
        var order = kinds.Select(k => IndexOf(c => c.Is("az", k))).ToList();
        order.ShouldBe([.. order.Order()], "the VM first, then what it used");
        // And the sweep looks for what the names missed, by this job's prefix alone.
        Only(c => c.Is("az", "resource", "list"), "sweep").After("--query").ShouldBe("[?starts_with(name, 'pilot-bench-j7-')].id");
        Directory.EnumerateFiles(Tmp, "id_bench*").ShouldBeEmpty();
        Directory.Exists(Path.Combine(Tmp, "az-config")).ShouldBeFalse();
    }

    [Fact]
    public async Task AnEvictionIsToldFromAFailedBenchByAskingAzureAboutTheVm()
    {
        // The connection dropped and the VM is gone: Azure took it back.
        _cloud.On(c => c.Remote() == "bash bench.sh", _ => new CloudProcessRun(255, "", "client_loop: send disconnect: Connection reset"));
        _cloud.On(c => c.Is("az", "vm", "get-instance-view"), _ => FakeCloud.NotFound());

        var gone = await Bench().RunAsync(Request(), CancellationToken.None);

        gone.ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(CloudBenchOutcome.Evicted),
            r => r.Stage.ShouldBe(CloudBenchStage.Run),
            r => r.Started.ShouldBeTrue(),
            r => r.Error.ShouldNotBeNull().ShouldContain("evicted by Azure"),
            r => r.Vm.Evictions.ShouldBe(1));
        // Nothing to copy back from a VM that is gone.
        Commands.ShouldNotContain(c => c.Program == "scp" && c.Args.Contains("-r"));
    }

    [Fact]
    public async Task AVmAzureDeallocatedIsAnEvictionToo()
    {
        _cloud.On(c => c.Remote() == "bash bench.sh", _ => new CloudProcessRun(255, "", "Connection timed out"));
        _cloud.On(c => c.Is("az", "vm", "get-instance-view"), _ => FakeCloud.Ok("PowerState/deallocated\n"));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Evicted);
        result.Error.ShouldNotBeNull().ShouldContain("deallocated");
        result.Vm.Evictions.ShouldBe(1);
    }

    [Fact]
    public async Task AnEvictionThatShowsAfterTheConnectionDroppedIsFoundOnALaterLook()
    {
        // Azure's notice comes before the VM goes: the first look still says running.
        var looks = 0;
        _cloud.On(c => c.Remote() == "bash bench.sh", _ => new CloudProcessRun(255, "", "Connection reset"));
        _cloud.On(c => c.Is("az", "vm", "get-instance-view"), _ => ++looks < 3 ? FakeCloud.Ok("PowerState/running\n") : FakeCloud.NotFound());

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Evicted);
        looks.ShouldBe(3);
        _pauses.ShouldBe([TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)]);
    }

    [Fact]
    public async Task ALostConnectionToAVmThatIsStillRunningIsAFailureNotAnEviction()
    {
        _cloud.On(c => c.Remote() == "bash bench.sh", _ => new CloudProcessRun(255, "", "Connection reset"));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(CloudBenchOutcome.Failed),
            r => r.Stage.ShouldBe(CloudBenchStage.Run),
            r => r.Error.ShouldNotBeNull().ShouldContain("connection"),
            r => r.Vm.Evictions.ShouldBe(0));
        Commands.Count(c => c.Is("az", "vm", "get-instance-view")).ShouldBe(3);
    }

    [Fact]
    public async Task ABenchThatExitsNonZeroIsAFailureWithItsExitCodeAndItsResultsAreStillCopiedBack()
    {
        _cloud.On(c => c.Remote() == "bash bench.sh", c =>
        {
            c.OnLine?.Invoke("Unhandled exception. System.InvalidOperationException");
            return new CloudProcessRun(2, "", "");
        });

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(CloudBenchOutcome.Failed),
            r => r.Stage.ShouldBe(CloudBenchStage.Run),
            r => r.ExitCode.ShouldBe(2),
            r => r.Error.ShouldNotBeNull().ShouldContain("exit 2"),
            r => r.Vm.Evictions.ShouldBe(0));
        // The VM was reachable when the bench ended, so Azure is not asked about it, and whatever tables there are come back.
        Commands.ShouldNotContain(c => c.Is("az", "vm", "get-instance-view"));
        Commands.Count(c => c.Program == "scp" && c.Args.Contains("-r")).ShouldBe(2);
        Log.ShouldContain("Unhandled exception. System.InvalidOperationException");
    }

    [Fact]
    public async Task TheVmNeverAnsweringOnSshIsAFailureOfTheCloudBeforeTheBenchStarted()
    {
        _cloud.On(c => c.Remote() == "true", _ => FakeCloud.Fail(255, "ssh: connect to host 20.1.2.3 port 22: Connection timed out"));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(CloudBenchOutcome.Failed),
            r => r.Stage.ShouldBe(CloudBenchStage.Connect),
            // A bench that never began is one a caller may run on the node's machine instead.
            r => r.Started.ShouldBeFalse());
        Commands.Count(c => c.Remote() == "true").ShouldBe(18);
        _pauses.Count(p => p == TimeSpan.FromSeconds(10)).ShouldBe(17);
        Commands.ShouldNotContain(c => c.Remote() == "bash bench.sh");
        Commands.ShouldContain(c => c.Is("az", "vm", "delete"));
    }

    [Fact]
    public async Task TheMachinesOwnAzLoginIsCheckedFirstAndNothingIsMadeWithoutIt()
    {
        _cloud.On(c => c.Is("az", "account", "show"), _ => FakeCloud.Fail(1, "ERROR: Please run 'az login' to setup account."));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(CloudBenchOutcome.Failed),
            r => r.Stage.ShouldBe(CloudBenchStage.Login),
            r => r.Started.ShouldBeFalse(),
            r => r.Error.ShouldNotBeNull().ShouldContain("az login"),
            r => r.Vm.Minutes.ShouldBeNull(),
            r => r.Vm.Cost.ShouldBeNull());
        Commands.Select(c => c.Program).ShouldBe(["az"], "no key, no bundle, no VM and so nothing to delete");
        Commands[0].Is("az", "account", "show").ShouldBeTrue();
        Commands[0].After("--subscription").ShouldBe(Subscription);
    }

    [Fact]
    public async Task AzNotBeingInstalledIsAFailureToSignIn()
    {
        _cloud.On(c => c.Program == "az", _ => new CloudProcessRun(CloudProcessRun.NotStarted, "", "az could not be started (is it installed and on PATH?): The system cannot find the file specified"));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Failed);
        result.Stage.ShouldBe(CloudBenchStage.Login);
        result.Error.ShouldNotBeNull().ShouldContain("could not be started");
    }

    [Fact]
    public async Task AVmThatAnswersNoPublicIpIsAFailureOfTheCreateAndIsStillDeleted()
    {
        _cloud.On(c => c.Is("az", "vm", "create"), _ => FakeCloud.Ok("{\"powerState\": \"VM running\"}"));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Failed);
        result.Stage.ShouldBe(CloudBenchStage.Create);
        result.Error.ShouldNotBeNull().ShouldContain("no public IP");
        Commands.ShouldContain(c => c.Is("az", "vm", "delete"));
    }

    [Fact]
    public async Task ACancelWhileTheBenchRunsEndsCancelledWithTheMoneySpentTold()
    {
        using var cancel = new CancellationTokenSource();
        _cloud.On(c => c.Remote() == "bash bench.sh", (_, _) =>
        {
            _cloud.Clock.Advance(TimeSpan.FromMinutes(10));
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        var result = await Bench().RunAsync(Request(), cancel.Token);

        result.Outcome.ShouldBe(CloudBenchOutcome.Cancelled);
        result.Stage.ShouldBe(CloudBenchStage.Run);
        // 4 minutes to make it, 10 to the cancel, 1 to delete the VM.
        result.Vm.Minutes.ShouldNotBeNull().ShouldBe(15, 0.001);
        result.Vm.Cost.ShouldNotBeNull().ShouldBe(15 / 60.0 * 0.07, 0.00001);
        Commands.ShouldContain(c => c.Is("az", "vm", "delete"));
    }

    [Fact]
    public async Task ARunWithNoRefNoCommandOrAPathOutsideTheCloneIsRefusedBeforeAnythingRuns()
    {
        var bench = Bench();

        await Should.ThrowAsync<ArgumentException>(() => bench.RunAsync(Request() with { Refs = [] }, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => bench.RunAsync(Request() with { Command = [] }, CancellationToken.None));
        foreach (var bad in new[] { "../outside", "/etc/passwd", "C:\\data", "-rf", "a b'; rm -rf ~; '", "x*", "~/x" })
        {
            await Should.ThrowAsync<ArgumentException>(() => bench.RunAsync(Request(paths: [bad]), CancellationToken.None), bad);
        }
        _cloud.Seen.ShouldBeEmpty();
    }
}

/// <summary>An <see cref="IRetailPrices"/> that answers what the test says, and what it was asked.</summary>
internal sealed class FakePrices : IRetailPrices
{
    public double? Price { get; set; }

    public Exception? Throws { get; set; }

    public List<(string Region, string Size)> Asked { get; } = [];

    public Task<double?> SpotPerHourAsync(string region, string vmSize, CancellationToken cancellationToken)
    {
        Asked.Add((region, vmSize));
        return Throws is { } ex ? Task.FromException<double?>(ex) : Task.FromResult(Price);
    }
}
