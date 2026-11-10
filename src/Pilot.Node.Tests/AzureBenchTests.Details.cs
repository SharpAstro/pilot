using System.Net.Http;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

// The price and the minutes, the secret, the script on the VM, what is left in Azure, the retry after an eviction and the bundle.
public sealed partial class AzureBenchTests
{
    [Fact]
    public async Task TheSpotPriceIsReadForTheSizeAndRegionAndTheVmMinutesFromCreateToDeleteCostIt()
    {
        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        _prices.Asked.ShouldBe([("westeurope", "Standard_F8s_v2")]);
        result.Vm.PricePerHour.ShouldBe(0.07);
        result.Vm.PriceIsUpperBound.ShouldBeFalse();
        // 4 minutes to make the VM, 30 for the bench, 1 to delete it. The NIC, disk and the rest go after it and take 10 more, which are no VM time.
        result.Vm.Minutes.ShouldNotBeNull().ShouldBe(35, 0.001);
        result.Vm.Cost.ShouldNotBeNull().ShouldBe(35 / 60.0 * 0.07, 0.00001);
        Log.ShouldContain(line => line.StartsWith("=== the VM ran 35 minutes, $0.04083", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public async Task WithNoPriceFromTheApiTheMaxPriceStandsAsTheUpperBoundAndTheRowSaysSo(double? answer)
    {
        _prices.Price = answer;

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Vm.PricePerHour.ShouldBe(0.3);
        result.Vm.PriceIsUpperBound.ShouldBeTrue();
        result.Vm.Cost.ShouldNotBeNull().ShouldBe(35 / 60.0 * 0.3, 0.00001);
        Log.ShouldContain(line => line.Contains("could not be read: the max price $0.3 an hour stands as the upper bound", StringComparison.Ordinal));
        Log.ShouldContain(line => line.Contains("at most $0.175", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnApiThatFailsOutrightIsTheSameAsOneThatHasNoPrice()
    {
        _prices.Throws = new HttpRequestException("no route to prices.azure.com");

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Succeeded);
        result.Vm.PriceIsUpperBound.ShouldBeTrue();
        result.Vm.PricePerHour.ShouldBe(0.3);
    }

    [Fact]
    public async Task AnEvictedAttemptStillTellsItsMinutesAndCost()
    {
        _cloud.On(c => c.Remote() == "bash bench.sh", _ =>
        {
            _cloud.Clock.Advance(TimeSpan.FromMinutes(5));
            return new CloudProcessRun(255, "", "Connection reset");
        });
        _cloud.On(c => c.Is("az", "vm", "get-instance-view"), _ => FakeCloud.NotFound());

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        // 4 to make it, 5 until it went, 1 for the delete of what was left.
        result.Vm.Minutes.ShouldNotBeNull().ShouldBe(10, 0.001);
        result.Vm.Cost.ShouldNotBeNull().ShouldBe(10 / 60.0 * 0.07, 0.00001);
    }

    [Fact]
    public async Task TheServicePrincipalSignsInFirstInAConfigFolderOfItsOwnAndItsSecretIsInNoLogLine()
    {
        var config = Path.Combine(Tmp, "az-config");
        _cloud.On(c => c.Is("az", "login"), c =>
        {
            // What az keeps for a service principal: the secret, in its config folder.
            File.WriteAllText(Path.Combine(c.Environment!["AZURE_CONFIG_DIR"], "service_principal_entries.json"), Secret);
            return FakeCloud.Ok();
        });
        // A bench that prints its environment, and an az that quotes the secret back in its error text.
        _cloud.On(c => c.Remote() == "bash bench.sh", c =>
        {
            c.OnLine?.Invoke($"AZURE_SECRET={Secret}");
            return FakeCloud.Ok();
        });

        var result = await Bench().RunAsync(Request(Cloud(servicePrincipal: true)), CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Succeeded);
        var login = Commands[0];
        login.Is("az", "login", "--service-principal").ShouldBeTrue();
        login.After("--username").ShouldBe("app-1234");
        login.After("--password").ShouldBe(Secret);
        login.After("--tenant").ShouldBe("tenant-5678");
        Commands.Where(c => c.Program == "az").ShouldAllBe(c => c.Environment!["AZURE_CONFIG_DIR"] == config);
        // The machine's own login is not looked at when the service principal is set.
        Commands.ShouldNotContain(c => c.Is("az", "account", "show"));
        Log.ShouldNotContain(line => line.Contains(Secret, StringComparison.Ordinal));
        Log.ShouldContain("$ az login --service-principal --username app-1234 --password *** --tenant tenant-5678 --allow-no-subscriptions --output none --only-show-errors");
        Log.ShouldContain("AZURE_SECRET=***");
        // The folder with the token az kept is gone with the VM.
        Directory.Exists(config).ShouldBeFalse();
    }

    [Fact]
    public async Task AFailedServicePrincipalLoginThatQuotesTheSecretLeavesItOutOfTheLogAndTheResult()
    {
        _cloud.On(c => c.Is("az", "login"), _ => FakeCloud.Fail(1, $"AADSTS7000215: Invalid client secret provided. The secret {Secret} is wrong"));

        var result = await Bench().RunAsync(Request(Cloud(servicePrincipal: true)), CancellationToken.None);

        result.ShouldSatisfyAllConditions(
            r => r.Outcome.ShouldBe(CloudBenchOutcome.Failed),
            r => r.Stage.ShouldBe(CloudBenchStage.Login),
            r => r.Started.ShouldBeFalse(),
            r => r.Error.ShouldNotBeNull().ShouldNotContain(Secret),
            r => r.Error.ShouldNotBeNull().ShouldContain("***"));
        Log.ShouldNotContain(line => line.Contains(Secret, StringComparison.Ordinal));
        Log.ShouldContain(line => line.Contains("AADSTS7000215") && line.Contains("***"));
        Commands.Select(c => c.Program).ShouldBe(["az"], "nothing was made, so nothing is deleted");
        Directory.Exists(Path.Combine(Tmp, "az-config")).ShouldBeFalse();
    }

    [Fact]
    public async Task TheScriptOnTheVmClonesEachRefFromTheBundleBuildsFetchesAndRunsInTheOrderBenchRunDoes()
    {
        var result = await Bench().RunAsync(Request(paths: ["data/out", "src/B/results.json"]), CancellationToken.None);
        result.Outcome.ShouldBe(CloudBenchOutcome.Succeeded);

        var script = _cloud.ShippedScript.ShouldNotBeNull();
        script.Contains('\r').ShouldBeFalse("bash reads LF whatever the node's OS");
        script.ShouldStartWith("#!/usr/bin/env bash\n");
        script.ShouldContain("--channel 10.0");
        foreach (var (label, reference, sha) in new[] { ("baseline", "main", FirstSha), ("candidate", "flight/pil823", SecondSha) })
        {
            // The same marker line BenchRun writes, then the clone of the commit from the bundle, no GitHub and no credentials.
            var marker = script.IndexOf($"echo '{BenchMarkers.Start(label, reference, sha)}'", StringComparison.Ordinal);
            marker.ShouldBeGreaterThan(0, $"the marker of {label}");
            var section = script[marker..];
            var clone = section.IndexOf($"git -C \"$HOME/bench/{label}\" fetch -q \"$HOME/bench.bundle\" 'refs/pilot-bench/j7/{label}' && git -C \"$HOME/bench/{label}\" checkout -q --detach '{sha}'", StringComparison.Ordinal);
            var build = section.IndexOf("dotnet build \"$sln\" -c Release", StringComparison.Ordinal);
            var fetch = section.IndexOf("echo fetching", StringComparison.Ordinal);
            var bench = section.IndexOf("dotnet run -c Release --project bench/B -- --filter '*'", StringComparison.Ordinal);
            new[] { clone, build, fetch, bench }.ShouldBe([.. new[] { clone, build, fetch, bench }.Order()]);
            new[] { clone, build, fetch, bench }.ShouldAllBe(i => i >= 0);
        }
        script.ShouldContain($"echo '{BenchMarkers.FetchFailed}'");
        // Every BenchmarkDotNet results folder and the named paths are gathered for the copy back.
        script.ShouldContain("-type d -path '*/BenchmarkDotNet.Artifacts/results'");
        script.ShouldContain("cp -r --parents 'data/out' \"$HOME/out/baseline/\"");
        script.ShouldContain("cp -r --parents 'src/B/results.json' \"$HOME/out/candidate/\"");
        script.ShouldEndWith("[ \"$bad\" -gt 254 ] && bad=254\nexit \"$bad\"\n");
    }

    [Fact]
    public async Task TheBuildIsTheRequestsOwnOrNoneWhenItIsEmpty()
    {
        await Bench().RunAsync(Request(build: "dotnet build -c Release src/B/B.csproj"), CancellationToken.None);
        var own = _cloud.ShippedScript.ShouldNotBeNull();

        own.ShouldContain("dotnet build -c Release src/B/B.csproj");
        own.ShouldNotContain("dotnet build \"$sln\"");

        var none = new FakeCloud();
        await new AzureBench(none, _prices, none.Clock, (_, _) => Task.CompletedTask).RunAsync(Request(build: ""), CancellationToken.None);

        none.ShippedScript.ShouldNotBeNull().ShouldNotContain("dotnet build");
    }

    [Fact]
    public void TheNamesCarryTheJobIdAndEndInADashSoOneJobsSweepNeverTakesAnothers()
    {
        var seven = CloudBenchNames.For("j7");
        var seventyOne = CloudBenchNames.For("j71");

        seven.Prefix.ShouldBe("pilot-bench-j7-");
        new[] { seven.Vm, seven.Nic, seven.PublicIp, seven.Nsg, seven.Vnet, seven.Disk }.ShouldAllBe(name => name.StartsWith(seven.Prefix, StringComparison.Ordinal));
        seven.Nic.ShouldBe("pilot-bench-j7-vmVMNic", "az vm create names the NIC after the VM");
        seventyOne.Vm.StartsWith(seven.Prefix, StringComparison.Ordinal).ShouldBeFalse();
        CloudBenchNames.For("J-7 !/x").Prefix.ShouldBe("pilot-bench-j7x-");
        CloudBenchNames.For("---").Prefix.ShouldBe("pilot-bench-x-");
    }

    [Fact]
    public async Task WhatTheNamesMissedButTheSweepFindsIsDeletedById()
    {
        _cloud.On(c => c.Is("az", "resource", "list"), _ => FakeCloud.Ok("/subscriptions/s/resourceGroups/rg-bench/providers/Microsoft.Network/networkInterfaces/pilot-bench-j7-other\r\n/subscriptions/s/resourceGroups/rg-bench/providers/Microsoft.Compute/disks/pilot-bench-j7-disk_1\r\n"));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Leaked.ShouldBeEmpty();
        var sweep = Only(c => c.Is("az", "resource", "delete"), "sweep delete");
        sweep.Args.Skip(3).Take(2).ShouldBe(
            ["/subscriptions/s/resourceGroups/rg-bench/providers/Microsoft.Network/networkInterfaces/pilot-bench-j7-other", "/subscriptions/s/resourceGroups/rg-bench/providers/Microsoft.Compute/disks/pilot-bench-j7-disk_1"]);
    }

    [Fact]
    public async Task ResourcesThatCannotBeDeletedAreNamedLoudlyBecauseTheyCostMoney()
    {
        const string leftover = "/subscriptions/s/resourceGroups/rg-bench/providers/Microsoft.Network/publicIPAddresses/pilot-bench-j7-ip";
        _cloud.On(c => c.Is("az", "network", "public-ip", "delete"), _ => FakeCloud.Fail(1, "ERROR: (InternalServerError) Azure is having a bad day"));
        _cloud.On(c => c.Is("az", "resource", "list"), _ => FakeCloud.Ok(leftover + "\n"));
        _cloud.On(c => c.Is("az", "resource", "delete"), _ => FakeCloud.Fail(1, "ERROR: still attached"));

        var result = await Bench().RunAsync(Request(), CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Succeeded, "the bench itself was fine");
        result.Leaked.ShouldBe([leftover]);
        Log.ShouldContain(line => line.StartsWith("=== NOT DELETED, delete by hand in resource group rg-bench", StringComparison.Ordinal) && line.Contains(leftover, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedDeleteThatLeftNothingBehindIsNoLeakAndAListThatCannotBeReadNamesTheFailedDeletes()
    {
        // The list is the truth: the delete failed but the resource is not there.
        _cloud.On(c => c.Is("az", "network", "nsg", "delete"), _ => FakeCloud.Fail(1, "ERROR: timed out"));

        (await Bench().RunAsync(Request(), CancellationToken.None)).Leaked.ShouldBeEmpty();

        // With no list to check against, what failed to delete is what may be left.
        _cloud.On(c => c.Is("az", "resource", "list"), _ => FakeCloud.Fail(1, "ERROR: throttled"));
        var unverified = await Bench().RunAsync(Request(), CancellationToken.None);

        unverified.Leaked.ShouldBe(["pilot-bench-j7-nsg"]);
    }

    private CloudCleanupRequest CleanupRequest(BenchCloud? cloud = null) => new()
    {
        JobId = "j7",
        Cloud = cloud ?? Cloud(),
        TmpFolder = Path.Combine(_root, "lost.tmp"),
        Log = Say,
    };

    [Fact]
    public async Task ACleanupOfALostBenchSignsInThenDeletesByNameAndSweepsByPrefixWithoutMakingAnything()
    {
        const string stray = "/subscriptions/s/resourceGroups/rg-bench/providers/Microsoft.Compute/disks/pilot-bench-j7-disk_1";
        _cloud.On(c => c.Is("az", "resource", "list"), _ => FakeCloud.Ok(stray + "\n"));

        var result = await Bench().CleanUpAsync(CleanupRequest(), CancellationToken.None);

        result.Clean.ShouldBeTrue();
        result.Swept.ShouldBe([stray]);
        Commands.ShouldContain(c => c.Is("az", "account", "show"));
        Only(c => c.Is("az", "vm", "delete"), "vm delete").After("--name").ShouldBe("pilot-bench-j7-vm");
        Only(c => c.Is("az", "resource", "list"), "sweep").After("--query").ShouldBe("[?starts_with(name, 'pilot-bench-j7-')].id");
        Commands.ShouldNotContain(c => c.Is("az", "vm", "create"));
        Commands.ShouldNotContain(c => c.Program is "ssh" or "scp" or "git");
    }

    [Fact]
    public async Task ACleanupThatCannotSignInDeletesNothingAndSaysWhy()
    {
        _cloud.On(c => c.Is("az", "account", "show"), _ => FakeCloud.Fail(1, "ERROR: Please run 'az login'"));

        var result = await Bench().CleanUpAsync(CleanupRequest(), CancellationToken.None);

        result.Clean.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("az is not signed in");
        Commands.ShouldNotContain(c => c.Is("az", "vm", "delete"));
    }

    [Fact]
    public async Task ACleanupNamesTheResourcesThatCouldNotBeDeleted()
    {
        const string leftover = "/subscriptions/s/resourceGroups/rg-bench/providers/Microsoft.Network/publicIPAddresses/pilot-bench-j7-ip";
        _cloud.On(c => c.Is("az", "resource", "list"), _ => FakeCloud.Ok(leftover + "\n"));
        _cloud.On(c => c.Is("az", "resource", "delete"), _ => FakeCloud.Fail(1, "ERROR: still attached"));

        var result = await Bench().CleanUpAsync(CleanupRequest(), CancellationToken.None);

        result.Clean.ShouldBeFalse();
        result.Leaked.ShouldBe([leftover]);
    }

    [Fact]
    public async Task AnEvictedVmIsRunAgainOnceAndTheAttemptsAddUp()
    {
        var bench = new ScriptedBench(
            Scripted(CloudBenchOutcome.Evicted, minutes: 12, cost: 0.014, evictions: 1),
            Scripted(CloudBenchOutcome.Succeeded, minutes: 30, cost: 0.035));
        var request = Request();

        var result = await bench.RunWithRetryAsync(request, 2, CancellationToken.None);

        bench.Runs.ShouldBe(2);
        result.Outcome.ShouldBe(CloudBenchOutcome.Succeeded);
        result.Vm.Minutes.ShouldBe(42);
        result.Vm.Cost.ShouldNotBeNull().ShouldBe(0.049, 0.00001);
        result.Vm.Evictions.ShouldBe(1);
        Log.ShouldContain("=== the spot VM was evicted by Azure: starting the benchmark again (2 of 2) ===");
    }

    [Fact]
    public async Task ASecondEvictionEndsItAsEvictedAndAFailedBenchIsNeverRunAgain()
    {
        var twice = new ScriptedBench(
            Scripted(CloudBenchOutcome.Evicted, minutes: 10, cost: 0.01, evictions: 1),
            Scripted(CloudBenchOutcome.Evicted, minutes: 5, cost: 0.005, evictions: 1),
            Scripted(CloudBenchOutcome.Succeeded, minutes: 1, cost: 0.001));
        var failing = new ScriptedBench(Scripted(CloudBenchOutcome.Failed, minutes: 3, cost: 0.003), Scripted(CloudBenchOutcome.Succeeded, minutes: 1, cost: 0.001));

        var evicted = await twice.RunWithRetryAsync(Request(), 2, CancellationToken.None);
        var failed = await failing.RunWithRetryAsync(Request(), 2, CancellationToken.None);

        twice.Runs.ShouldBe(2);
        (evicted.Outcome, evicted.Vm.Evictions, evicted.Vm.Minutes).ShouldBe((CloudBenchOutcome.Evicted, 2, 15));
        failing.Runs.ShouldBe(1);
        (failed.Outcome, failed.Vm.Evictions).ShouldBe((CloudBenchOutcome.Failed, 0));
    }

    // Real git over a seed repo, so it can pass the 5 s a plain test gets.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheBundleCarriesEachRefsCommitSoAFreshRepoCanFetchItWithNoRemoteAndNoTemporaryRefIsLeftInTheRepo()
    {
        var real = new SystemCloudProcess();
        var repo = SeedRepo.Make(Path.Combine(_root, "seed"));
        async Task<string> Git(string cwd, params string[] args)
        {
            var run = await real.RunAsync(new CloudCommand("git", args, cwd), CancellationToken.None);
            run.Ok.ShouldBeTrue($"git {string.Join(' ', args)}: {run.Why}");
            return run.Output.Trim();
        }
        var first = await Git(repo, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "second\n", TestContext.Current.CancellationToken);
        await Git(repo, "commit", "-q", "-am", "second");
        var second = await Git(repo, "rev-parse", "HEAD");
        var vm = Path.Combine(_root, "vm");
        Directory.CreateDirectory(vm);

        // git is the real one; the bundle is "copied to the VM" into a folder, and the clone below is what the script does there.
        _cloud.On(c => c.Program == "git", (c, ct) => real.RunAsync(c, ct).GetAwaiter().GetResult());
        _cloud.On(c => c.Program == "scp" && !c.Args.Contains("-r"), c =>
        {
            File.Copy(Path.Combine(c.WorkingDirectory!, "bench.bundle"), Path.Combine(vm, "bench.bundle"));
            return FakeCloud.Ok();
        });
        var request = Request() with { RepoPath = repo, Refs = [new CloudBenchRef("baseline", "main~1", first), new CloudBenchRef("candidate", "main", second)] };

        var result = await Bench().RunAsync(request, CancellationToken.None);

        result.Outcome.ShouldBe(CloudBenchOutcome.Succeeded);
        (await Git(repo, "for-each-ref", "refs/pilot-bench")).ShouldBeEmpty("the temporary refs are removed");
        foreach (var (label, sha) in new[] { ("baseline", first), ("candidate", second) })
        {
            var clone = Path.Combine(vm, label);
            Directory.CreateDirectory(clone);
            await Git(clone, "init", "-q");
            await Git(clone, "fetch", "-q", Path.Combine(vm, "bench.bundle"), $"refs/pilot-bench/j7/{label}");
            await Git(clone, "checkout", "-q", "--detach", sha);
            (await Git(clone, "rev-parse", "HEAD")).ShouldBe(sha);
            (await Git(clone, "remote")).ShouldBeEmpty("the VM clones nothing from GitHub");
        }
        // Trimmed: a checkout on Windows may turn the line ending into CRLF.
        File.ReadAllText(Path.Combine(vm, "baseline", "README.md")).Trim().ShouldBe("hello");
        File.ReadAllText(Path.Combine(vm, "candidate", "README.md")).Trim().ShouldBe("second");
    }

    private static CloudBenchResult Scripted(CloudBenchOutcome outcome, double minutes, double cost, int evictions = 0) => new()
    {
        Outcome = outcome,
        Vm = new BenchVm { Minutes = minutes, Cost = cost, Evictions = evictions, PricePerHour = 0.07 },
        ResultsFolder = "results",
    };

    private sealed class ScriptedBench(params CloudBenchResult[] results) : ICloudBench
    {
        private readonly Queue<CloudBenchResult> _results = new(results);

        public int Runs { get; private set; }

        public Task<CloudBenchResult> RunAsync(CloudBenchRequest request, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(_results.Dequeue());
        }
    }
}
