using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Updates;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The settings file, laid under the command line; the Settings page's verbs on a real node; and the setup
/// checklist's reading of netsh and Claude Code's trust.</summary>
public sealed class SettingsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ASettingsFileReadsBackWhatWasWritten()
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        var settings = new NodeSettings
        {
            Lan = true,
            Machine = "DE1",
            RepoRoots = [_root],
            UpdateChannel = UpdateChannel.Beta,
            UpdatePolicy = UpdatePolicy.Ask,
            MaxJobs = 3,
            JobMemoryGigabytes = 1.5,
            LanPort = 47800,
        };
        settings.Write(file);

        NodeSettings.Read(file).ShouldSatisfyAllConditions(
            s => s.Lan.ShouldBe(true),
            s => s.Machine.ShouldBe("DE1"),
            s => s.RepoRoots.ShouldBe([_root]),
            s => s.UpdateChannel.ShouldBe(UpdateChannel.Beta),
            s => s.UpdatePolicy.ShouldBe(UpdatePolicy.Ask),
            s => s.MaxJobs.ShouldBe(3),
            s => s.JobMemoryGigabytes.ShouldBe(1.5),
            s => s.LanPort.ShouldBe(47800));
    }

    [Fact]
    public void TheWorkerLadderKeysRoundTripAndTakeTheirValues()
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        var settings = new NodeSettings();
        foreach (var (key, value) in new[] { ("worker-model-max", "sonnet"), ("worker-effort-max", "xhigh"), ("worker-ladder", "off") })
        {
            settings = NodeSettings.With(settings, key, [value]).Settings.ShouldNotBeNull();
        }
        settings.Write(file);

        var read = NodeSettings.Read(file);
        (string.Join(' ', read.Get("worker-model-max")!), string.Join(' ', read.Get("worker-effort-max")!), string.Join(' ', read.Get("worker-ladder")!)).ShouldBe(("sonnet", "xhigh", "off"));
        read.WorkerLadder.ShouldBe(false);
        var roles = new RoleModels(read);
        (roles.WorkerModelMax, roles.WorkerEffortMax, roles.WorkerLadder).ShouldBe(("sonnet", "xhigh", false));
        var defaults = new RoleModels(null);
        (defaults.WorkerModelMax, defaults.WorkerEffortMax, defaults.WorkerLadder).ShouldBe(("opus", "max", true));
        NodeSettings.With(read, "worker-ladder", ["default"]).Settings!.WorkerLadder.ShouldBeNull();
        NodeSettings.With(read, "worker-effort-max", ["loud"]).Error.ShouldBe("worker-effort-max takes low, medium, high, xhigh, max or none");
        NodeSettings.With(read, "worker-ladder", ["maybe"]).Error.ShouldBe("worker-ladder takes on or off");
    }

    [Fact]
    public void AValueTheFileCannotReadIsPassedOverNotFatal()
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        File.WriteAllText(file, """{ "lan": true, "machine": "nonsense", "max-jobs": "many", "update-channel": "beta", }""");

        var read = NodeSettings.Read(file);

        (read.Lan, read.Machine, read.MaxJobs, read.UpdateChannel).ShouldBe((true, (string?)null, (int?)null, UpdateChannel.Beta));
        File.WriteAllText(file, "not json");
        NodeSettings.Read(file).ShouldBe(new NodeSettings());
        NodeSettings.Read(Path.Combine(_root, "none.json")).ShouldBe(new NodeSettings());
    }

    [Theory]
    [InlineData("lan", "maybe", "lan takes on or off")]
    [InlineData("machine", "DESKTOP", "machine takes two letters and a number, as in DE1")]
    [InlineData("update-channel", "nightly", "update-channel takes stable or beta")]
    [InlineData("max-jobs", "0", "max-jobs takes a number, 1 to 64")]
    [InlineData("colour", "blue", "there is no setting colour; there are lan, machine, repo-roots, update-channel, update-policy, max-jobs, job-memory, lan-port, start-limit, priority-repos, priority-start-limit, week-limit, merge-method, maintenance, accept-fleet-flights, autopilot, review-deep-first, shortcut-token, repo-trackers, no-auto-merge-repos, opus-cap, one-flight-per-repo, sub-task-repos, merge-batch, watchtower-model, worker-model, planner-model, reviewer-model, reviewer-deep-model, watchtower-effort, worker-effort, planner-effort, reviewer-effort, reviewer-deep-effort, worker-model-max, worker-effort-max, worker-ladder")]
    public void ASettingRefusesWhatItDoesNotTake(string key, string value, string error)
        => NodeSettings.With(new NodeSettings(), key, [value]).ShouldBe((null, error));

    [Fact]
    public void TheOpusCapAndTheOneFlightPerRepoModeAreSettingsWithDefaults()
    {
        var none = new RoleModels(null);
        (none.OpusCap, none.OneFlightPerRepo).ShouldBe((3, false));
        var set = NodeSettings.With(NodeSettings.With(new NodeSettings(), "opus-cap", ["0"]).Settings!, "one-flight-per-repo", ["on"]).Settings!;
        var models = new RoleModels(set);
        (models.OpusCap, models.OneFlightPerRepo).ShouldBe((0, true));
        set.Get("opus-cap").ShouldBe(["0"]);
        set.Get("one-flight-per-repo").ShouldBe(["on"]);
        NodeSettings.With(set, "opus-cap", ["default"]).Settings!.OpusCap.ShouldBeNull();
        NodeSettings.With(set, "one-flight-per-repo", ["default"]).Settings!.OneFlightPerRepo.ShouldBeNull();
        NodeSettings.With(new NodeSettings(), "opus-cap", ["many"]).Error.ShouldNotBeNull();
        NodeSettings.With(new NodeSettings(), "one-flight-per-repo", ["maybe"]).Error.ShouldBe("one-flight-per-repo takes on or off");
        var file = Path.Combine(_root, NodeSettings.FileName);
        set.Write(file);
        var read = NodeSettings.Read(file);
        (read.OpusCap, read.OneFlightPerRepo).ShouldBe((0, true));
    }

    [Fact]
    public void DefaultOrNothingTakesASettingOut()
    {
        var on = new NodeSettings { Lan = true, Machine = "DE1" };

        NodeSettings.With(on, "lan", ["default"]).Settings.ShouldBe(new NodeSettings { Machine = "DE1" });
        NodeSettings.With(on, "machine", []).Settings.ShouldBe(new NodeSettings { Lan = true });
        NodeSettings.With(on, "machine", ["de2"]).Settings!.Machine.ShouldBe("DE2");
        NodeSettings.With(on, "repo-roots", [Path.Combine(_root, "missing")]).Error.ShouldBe($"{Path.Combine(_root, "missing")} is not a folder on this machine");
    }

    [Fact]
    public void TheCommandLineWinsOverTheFileAndTheFileOverTheDefaults()
    {
        var saved = new NodeSettings { Lan = true, Machine = "DE1", UpdateChannel = UpdateChannel.Beta, MaxJobs = 4 };
        var commandLine = new NodeOptions("/tmp/n.sock", _root, MachinePrefix: "SE1", MaxJobs: 1, UpdatePolicy: UpdatePolicy.Auto);

        var options = saved.Over(commandLine, new HashSet<string> { NodeSettings.MachineKey });

        options.ShouldSatisfyAllConditions(
            o => o.Machine.ShouldBe("SE1"),
            o => o.Lan.ShouldBeTrue(),
            o => o.UpdateChannel.ShouldBe(UpdateChannel.Beta),
            o => o.MaxJobs.ShouldBe(4),
            o => o.UpdatePolicy.ShouldBe(UpdatePolicy.Auto),
            o => o.Saved.ShouldBe(saved),
            o => o.CommandLineKeys.ShouldNotBeNull().ShouldContain(NodeSettings.MachineKey));
    }

    [Fact]
    public async Task SetSavesASettingAndThePageShowsItSavedForTheNextStart()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["update-channel", "beta"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        result.Ok.ShouldBeTrue();
        // A test host is not pilot-node, so it does not restart itself: the change waits for the next start.
        result.Way.ShouldNotBeNull().ShouldStartWith("saved; it applies at the node's next start");
        NodeSettings.Read(node.Options.SettingsFile).UpdateChannel.ShouldBe(UpdateChannel.Beta);
        var page = (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull();
        var channel = page.Settings.Single(s => s.Key == NodeSettings.UpdateChannelKey);
        (channel.Value, channel.Saved, channel.Pending, channel.Source).ShouldBe(("stable", "beta", true, SettingSources.Default));
        channel.Choices.ShouldBe(["stable", "beta"]);
        page.Settings.Select(s => s.Key).ShouldBe(NodeSettings.Keys);
        // The page carries no checklist: it has its own route.
        page.Checks.ShouldBeEmpty();
        (await node.Client.GetSetupChecksAsync(Ct)).Response.ShouldNotBeNull().Checks.Select(c => c.Key).ShouldContain(NodeSetup.AutostartKey);

        // A running job does not hold a restart: the answer is the same with one running (jobs carry on under the next node).
        node.State.PutJob(new JobRow
        {
            Id = "j1", Command = "dotnet test", Cwd = _root, State = JobState.Running, LogPath = Path.Combine(_root, "j1.log"),
            Timeout = TimeSpan.FromHours(1), StartedAt = DateTimeOffset.UtcNow,
        });
        var withJob = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["update-channel", "beta"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        withJob.Way.ShouldNotBeNull().ShouldStartWith("saved; it applies at the node's next start");
        withJob.Way.ShouldNotContain("once no job runs");
        (await node.Client.GetSettingsAsync(Ct)).Response!.Restart.ShouldBeNull();

        // The job limits are in force at once, no restart.
        var jobs = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["max-jobs", "3"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        jobs.Way.ShouldBe("max-jobs saved as 3, and in force now");
        node.App.Services.GetRequiredService<Pilot.Node.Flights.JobRunner>().MaxRunning.ShouldBe(3);
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == NodeSettings.MaxJobsKey).ShouldSatisfyAllConditions(
            s => s.Value.ShouldBe("3"), s => s.Pending.ShouldBeFalse());

        var refused = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["max-jobs", "lots"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        refused.Error.ShouldBe("max-jobs takes a number, 1 to 64");
        var watchtower = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["lan", "on"], Who = $"{node.Options.Machine.ToLowerInvariant()}-watchtower" }, Ct)).Response.ShouldNotBeNull();
        watchtower.Error.ShouldNotBeNull().ShouldContain("is the controller's");
    }

    // The settings commands over a node's own parts, with a setup whose every program run is counted, and a clock a test moves.
    private (NodeSettingsCommands Commands, Func<int> Runs, Pilot.Node.Tests.Sessions.ManualTime Time) CountingSettings(NodeHarness node, Func<string, IReadOnlyList<string>, TimeSpan, (int Exit, string Output)>? run = null)
    {
        var runs = 0;
        var time = new Pilot.Node.Tests.Sessions.ManualTime(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        var setup = new NodeSetup(node.Options, Path.Combine(_root, "pilot-node.exe"), _root)
        {
            Run = (program, args, budget) =>
            {
                Interlocked.Increment(ref runs);
                return run?.Invoke(program, args, budget) ?? (1, "");
            },
        };
        var services = node.App.Services;
        var commands = new NodeSettingsCommands(node.Options, node.State, services.GetRequiredService<NodeRestarter>(), setup, time,
            services.GetRequiredService<Pilot.Node.Flights.JobRunner>(), services.GetRequiredService<RoleModels>());
        return (commands, () => Volatile.Read(ref runs), time);
    }

    [Fact]
    public async Task TheSettingsPageRunsNoSetupCheck()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var (commands, runs, _) = CountingSettings(node);

        var page = await commands.PageAsync(Ct);

        page.Settings.Select(s => s.Key).ShouldBe(NodeSettings.Keys);
        page.Checks.ShouldBeEmpty();
        runs().ShouldBe(0);
    }

    [Fact]
    public async Task TheChecksRouteAnswersFromItsCacheWithTheTimeMeasuredAndRunsAgainOnRefreshOrAge()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        // Where reading a check runs a program (gh, and netsh on Windows), it is counted.
        var (commands, runs, time) = CountingSettings(node);
        var measuredAt = time.GetUtcNow();

        var first = await commands.SetupChecksAsync(refresh: false, Ct);
        var afterFirst = runs();
        first.CheckedAt.ShouldBe(measuredAt);
        first.Checks.Select(c => c.Key).ShouldContain(NodeSetup.GhKey);
        first.Checks.Single(c => c.Key == NodeSetup.GhKey).Runs.ShouldBe("runs gh, which asks GitHub");
        first.Checks.Single(c => c.Key == NodeSetup.AutostartKey).Runs.ShouldBeNull();
        afterFirst.ShouldBeGreaterThan(0);

        // Looked at again within the cache age: the same answer, measured then, with nothing run.
        time.Advance(TimeSpan.FromSeconds(3));
        var cached = await commands.SetupChecksAsync(refresh: false, Ct);
        cached.CheckedAt.ShouldBe(measuredAt);
        runs().ShouldBe(afterFirst);

        // A refresh runs them again and says when.
        var refreshed = await commands.SetupChecksAsync(refresh: true, Ct);
        refreshed.CheckedAt.ShouldBe(measuredAt.AddSeconds(3));
        runs().ShouldBe(afterFirst * 2);

        // Older than the cache age: run again without being asked.
        time.Advance(TimeSpan.FromSeconds(11));
        (await commands.SetupChecksAsync(refresh: false, Ct)).CheckedAt.ShouldBe(measuredAt.AddSeconds(14));
        runs().ShouldBe(afterFirst * 3);
    }

    [Fact]
    public async Task TheChecksRouteAnswersOverTheWire()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var before = DateTimeOffset.UtcNow;
        var checks = (await node.Client.GetSetupChecksAsync(Ct)).Response.ShouldNotBeNull();
        checks.CheckedAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        checks.Checks.Select(c => c.Key).ShouldContain(NodeSetup.UsageKey);

        // The CLI and the pages that show both read the page with the checks filled in from their route.
        var both = (await node.Client.GetSettingsWithChecksAsync(Ct)).Response.ShouldNotBeNull();
        both.Checks.Select(c => c.Key).ShouldBe(checks.Checks.Select(c => c.Key));
        both.Settings.Select(s => s.Key).ShouldBe(NodeSettings.Keys);
    }

    [Fact]
    public async Task SetNodeAnswersWithTheChangedSettingsNewRow()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        // Pending until the node restarts: the row says what the file holds, and what the node runs with.
        var channel = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["update-channel", "beta"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        channel.Setting.ShouldNotBeNull().ShouldSatisfyAllConditions(
            s => s.Key.ShouldBe(NodeSettings.UpdateChannelKey),
            s => s.Saved.ShouldBe("beta"),
            s => s.Value.ShouldBe("stable"),
            s => s.Pending.ShouldBeTrue());

        // A live setting: the row is read after it is applied, so its running value is the new one.
        var jobs = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["max-jobs", "3"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        jobs.Setting.ShouldNotBeNull().ShouldSatisfyAllConditions(
            s => s.Key.ShouldBe(NodeSettings.MaxJobsKey),
            s => s.Value.ShouldBe("3"),
            s => s.Saved.ShouldBe("3"),
            s => s.Pending.ShouldBeFalse());
        var maintenance = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["maintenance", "on"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        maintenance.Setting.ShouldNotBeNull().Value.ShouldBe("on");

        // The row is the page's own row for it.
        var page = (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull();
        var onPage = page.Settings.Single(s => s.Key == NodeSettings.MaxJobsKey);
        var answered = jobs.Setting.ShouldNotBeNull();
        (answered.Key, answered.Label, answered.Value, answered.Saved, answered.Source, answered.Pending).ShouldBe((onPage.Key, onPage.Label, onPage.Value, onPage.Saved, onPage.Source, onPage.Pending));
        answered.Choices.ShouldBe(onPage.Choices);

        // A refusal carries none.
        var refused = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["max-jobs", "lots"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        refused.Setting.ShouldBeNull();
    }

    [Fact]
    public void TheFirewallCheckFindsTheRuleForThisBinaryOnly()
    {
        var node = Path.Combine(_root, "pilot-node.exe");
        string netsh = "";
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), node, _root) { Run = (_, _, _) => (netsh.Length == 0 ? 1 : 0, netsh) };

        setup.FirewallAllows().Allows.ShouldBeFalse();
        netsh = $"\r\nRule Name:                            pilot-node\r\n----------\r\nEnabled:                              Yes\r\nAction:                               Allow\r\nProfiles:                             Private\r\nProgram:                              {Path.Combine(_root, "old", "pilot-node.exe")}\r\n\r\n";
        setup.FirewallAllows().ShouldBe((false, $"rule pilot-node is there but not for {node}, or not enabled: an earlier install's"));
        netsh += $"Rule Name:                            pilot-node\r\nEnabled:                              Yes\r\nAction:                               Allow\r\nProfiles:                             Private\r\nProgram:                              {node}\r\n";
        setup.FirewallAllows().ShouldBe((true, "rule pilot-node allows pilot-node.exe in, on Private networks"));
    }

    [Fact]
    public async Task TheFirewallLetsInTheLanListenersBesideTheNodeOnTheDiscoveryPortAlone()
    {
        var node = Path.Combine(_root, "pilot-node.exe");
        var gui = Path.Combine(_root, "pilot-gui.exe");
        File.WriteAllText(node, "");
        File.WriteAllText(gui, "");
        string Rule(string program) => $"Rule Name:                            pilot-node\r\nEnabled:                              Yes\r\nAction:                               Allow\r\nProfiles:                             Private\r\nProgram:                              {program}\r\n\r\n";
        var netsh = Rule(node);
        string? elevated = null;
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), node, _root)
        {
            Run = (_, _, _) => (0, netsh),
            // The elevated pwsh runs a script: what it says is read while it is there.
            RunElevated = (_, args) => { elevated = File.ReadAllText(args.Split("-File \"")[1].TrimEnd('"')); netsh += Rule(gui); return true; },
        };

        // pilot-gui.exe is beside the node with no rule of its own; pilot.exe and pilot-tui.exe are not installed here.
        setup.FirewallAllows().ShouldBe((false, "rule pilot-node lets pilot-node.exe in, but not pilot-gui.exe: the Fleet page and pilot nodes hear no other node"));

        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node makes a firewall rule on Windows only");
        (await setup.FixAsync(NodeSetup.FirewallKey, Ct)).ShouldBeNull();
        elevated.ShouldNotBeNull().ShouldContain($"program=\"{node}\"\n");
        elevated.ShouldContain("netsh --% advfirewall");
        elevated.ShouldContain($"program=\"{gui}\" protocol=udp localport={LAN.Lib.LanProtocol.DiscoveryPort}");
        elevated.ShouldNotContain("pilot-tui.exe");
        setup.FirewallAllows().Allows.ShouldBeTrue();
    }

    [Fact]
    public void TheWatchtowerFolderIsTrustedByItsOwnRecordNotByAFolderAbove()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_root, ".claude")).FullName;
        var watchtower = Directory.CreateDirectory(Path.Combine(_root, "data", "watchtower")).FullName;
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), "pilot-node", profile);
        string Key(string path) => path.Replace('\\', '/');

        setup.Trusted(watchtower).ShouldBeNull();
        // As SE1's file has it: the folder above trusted, the watchtower's own record not. Claude Code goes by the folder's
        // own record, so setup still has the watchtower's trust to give.
        File.WriteAllText(Path.Combine(_root, ".claude.json"), $$"""{ "projects": { "{{Key(watchtower)}}": { "hasTrustDialogAccepted": false }, "{{Key(_root)}}": { "hasTrustDialogAccepted": true } } }""");
        setup.Trusted(watchtower).ShouldBeNull();
        File.WriteAllText(Path.Combine(_root, ".claude.json"), $$"""{ "projects": { "{{Key(watchtower)}}": { "hasTrustDialogAccepted": true } } }""");
        setup.Trusted(watchtower).ShouldBe(watchtower);
    }

    [Fact]
    public void EachRoleHasItsModelAndNoneLeavesItToClaudeCode()
    {
        var defaults = new RoleModels(null);
        RoleModels.Roles.Select(defaults.Model).ShouldBe(["sonnet", "sonnet", "opus", "opus", "opus"]);
        RoleModels.Roles.Select(defaults.Effort).ShouldAllBe(e => e == null);
        RoleModels.Unset.Model(RoleModels.Worker).ShouldBeNull();

        var saved = NodeSettings.With(new NodeSettings(), "worker-model", ["opus"]).Settings!;
        saved = NodeSettings.With(saved, "watchtower-model", ["none"]).Settings!;
        saved = NodeSettings.With(saved, "reviewer-effort", ["max"]).Settings!;
        var chosen = new RoleModels(saved);
        (chosen.Model(RoleModels.Worker), chosen.Model(RoleModels.Watchtower), chosen.Effort(RoleModels.Reviewer)).ShouldBe(("opus", (string?)null, "max"));

        var file = Path.Combine(_root, NodeSettings.FileName);
        saved.Write(file);
        NodeSettings.Read(file).Get("worker-model").ShouldBe(["opus"]);
        NodeSettings.With(saved, "worker-model", ["Opus 5"]).Error.ShouldBe("worker-model takes a model (sonnet, opus, haiku, or a full id as claude-opus-5-5) or none");
        NodeSettings.With(saved, "planner-effort", ["huge"]).Error.ShouldBe("planner-effort takes low, medium, high, xhigh, max or none");
        NodeSettings.With(saved, "worker-model", ["claude-opus-5-5[1m]"]).Settings.ShouldNotBeNull();
    }

    [Fact]
    public async Task ARolesModelIsInForceForItsNextSessionWithNoRestart()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["worker-model", "opus"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        result.Way.ShouldBe("worker-model saved as opus; the next session of that kind starts with it");
        node.App.Services.GetRequiredService<RoleModels>().Model(RoleModels.Worker).ShouldBe("opus");
        var page = (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull();
        page.Settings.Single(s => s.Key == "worker-model").ShouldSatisfyAllConditions(s => s.Value.ShouldBe("opus"), s => s.Pending.ShouldBeFalse());
        page.Settings.Single(s => s.Key == "planner-model").Value.ShouldBe("opus");
        page.Settings.Single(s => s.Key == "watchtower-model").Value.ShouldBe("sonnet");
    }

    [Fact]
    public async Task ADeepReviewsSettingsShowWhatItRunsOnWhenNothingDeepIsSet()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["reviewer-model", "sonnet"], Who = "tests" }, Ct);

        var page = (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull();

        page.Settings.Single(s => s.Key == "reviewer-deep-model").Value.ShouldBe("sonnet");
        page.Settings.Single(s => s.Key == "reviewer-deep-effort").Value.ShouldBe("high");
    }

    [Fact]
    public void ADeepReviewsNoneSettingPassesNoFlagWhereUnsetFollowsTheReviewer()
    {
        var unset = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { ["reviewer-model"] = "sonnet", ["reviewer-effort"] = "medium" } });
        unset.ReviewerModel(ReviewDepth.Deep).ShouldBe("sonnet");
        unset.ReviewerEffort(ReviewDepth.Deep).ShouldBe("high");

        var none = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { ["reviewer-model"] = "sonnet", ["reviewer-deep-model"] = "none", ["reviewer-deep-effort"] = "none" } });
        none.ReviewerModel(ReviewDepth.Deep).ShouldBeNull();
        none.ReviewerEffort(ReviewDepth.Deep).ShouldBeNull();
    }

    [Fact]
    public async Task AFlightsPrIsMergedByRebaseUnlessTheSettingsSayOtherwise()
    {
        new RoleModels(null).MergeMethod.ShouldBe("rebase");
        RoleModels.Unset.MergeMethod.ShouldBe("squash");
        NodeSettings.With(new NodeSettings(), "merge-method", ["octopus"]).Error.ShouldBe("merge-method takes rebase, squash, merge");
        await using var node = await NodeHarness.StartAsync(Ct);

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["merge-method", "squash"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        result.Way.ShouldBe("merge-method saved as squash; the next merge uses it");
        node.App.Services.GetRequiredService<RoleModels>().MergeMethod.ShouldBe("squash");
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == "merge-method").Choices.ShouldBe(["rebase", "squash", "merge"]);
    }

    /// <summary>A list entry that names a folder by path must be one, and stands for the folder's name; a repo-trackers entry must be repo=tracker.</summary>
    [Fact]
    public async Task SetRefusesAListEntryThatIsNotAFolderAndTakesAFolderByItsName()
    {
        var clone = Directory.CreateDirectory(Path.Combine(_root, "my-clone")).FullName;
        var missing = Path.Combine(_root, "missing");

        foreach (var key in new[] { "priority-repos", "no-auto-merge-repos", "sub-task-repos" })
        {
            NodeSettings.With(new NodeSettings(), key, ["tianwen", missing]).ShouldBe((null, $"{missing} is not a folder on this machine"));
        }
        NodeSettings.With(new NodeSettings(), "repo-trackers", [$"{missing}=shortcut"]).ShouldBe((null, $"{missing} is not a folder on this machine"));
        NodeSettings.With(new NodeSettings(), "repo-trackers", ["tianwen=jira"]).Error.ShouldNotBeNull().ShouldContain("repo=tracker");
        NodeSettings.With(new NodeSettings(), "repo-trackers", ["tianwen"]).Error.ShouldNotBeNull().ShouldContain("repo=tracker");
        NodeSettings.With(new NodeSettings(), "repo-trackers", [clone]).Error.ShouldNotBeNull().ShouldContain("repo=tracker");

        NodeSettings.With(new NodeSettings(), "no-auto-merge-repos", ["tianwen", clone, "TIANWEN"]).Settings!.NoAutoMergeRepos.ShouldBe(["tianwen", "my-clone"]);
        NodeSettings.With(new NodeSettings(), "repo-trackers", [$"{clone}=Shortcut"]).Settings!.RepoTrackers.ShouldBe(["my-clone=shortcut"]);

        // Through the node: the refusal's reason is the reply's, and nothing is written.
        await using var node = await NodeHarness.StartAsync(Ct);
        var refused = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["no-auto-merge-repos", missing], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldBe($"{missing} is not a folder on this machine");
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == "no-auto-merge-repos").Saved.ShouldBeNull();
    }

    /// <summary>sub-task-repos opts repos in to their legs being kept as sub-tasks of the work item: set, shown as a list, and in force at once.</summary>
    [Fact]
    public async Task SubTaskReposIsSetShownAsAListAndInForceAtOnce()
    {
        NodeSettings.With(new NodeSettings(), "sub-task-repos", ["tianwen", "TIANWEN", "bullclip"]).Settings!.SubTaskRepos.ShouldBe(["tianwen", "bullclip"]);
        NodeSettings.With(new NodeSettings { SubTaskRepos = ["tianwen"] }, "sub-task-repos", ["default"]).Settings!.SubTaskRepos.ShouldBeNull();

        await using var node = await NodeHarness.StartAsync(Ct);
        var models = node.App.Services.GetRequiredService<RoleModels>();
        models.SubTasks("tianwen").ShouldBeFalse();
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == "sub-task-repos").Many.ShouldBeTrue();

        var saved = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["sub-task-repos", "tianwen", "bullclip"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        saved.Ok.ShouldBeTrue();
        models.SubTasks("tianwen").ShouldBeTrue();
        models.SubTasks("BULLCLIP").ShouldBeTrue();
        models.SubTasks("pilot").ShouldBeFalse();
        var row = (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == "sub-task-repos");
        row.Saved.ShouldBe("tianwen\nbullclip");
        row.Value.ShouldBe("tianwen\nbullclip");

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["sub-task-repos", "default"], Who = "tests" }, Ct)).Response.ShouldNotBeNull().Ok.ShouldBeTrue();
        models.SubTasks("tianwen").ShouldBeFalse();
    }

    /// <summary>A role's model and effort rows say "default leaves the choice to Claude Code" where the wire value is still none.</summary>
    [Fact]
    public async Task ARoleRowsHelpSaysDefaultLeavesTheChoiceToClaudeCode()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var rows = (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Where(s => NodeSettings.IsRoleKey(s.Key) && !s.Key.EndsWith("-max", StringComparison.Ordinal)).ToArray();

        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(r => r.Choices.Contains("none"));
        rows.ShouldAllBe(r => r.Detail.Contains("default leaves the choice to Claude Code"));
        rows.ShouldAllBe(r => !r.Detail.Contains("none leaves"));
    }

    [Fact]
    public async Task APriorityRepoIsInForceTheMomentItIsSaved()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["priority-repos", "tianwen"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        result.Way.ShouldBe("priority-repos saved as tianwen; the next start the node makes by itself weighs it");
        node.App.Services.GetRequiredService<RoleModels>().StartLimitFor("tianwen").ShouldBe(98);
        var row = (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == "priority-repos");
        (row.Value, row.Source, row.Pending).ShouldBe(("tianwen", SettingSources.File, false));
    }
}
