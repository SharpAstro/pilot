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
            TestDirs.DeleteTree(_root);
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
    public void TheWatchtowerCompactionKeysRoundTripAndTakeTheirValues()
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        var settings = NodeSettings.With(new NodeSettings(), "watchtower-compact-share", ["50"]).Settings.ShouldNotBeNull();
        settings = NodeSettings.With(settings, "watchtower-chat-messages", ["25"]).Settings.ShouldNotBeNull();
        settings.Write(file);

        var read = NodeSettings.Read(file);
        (string.Join(' ', read.Get("watchtower-compact-share")!), string.Join(' ', read.Get("watchtower-chat-messages")!)).ShouldBe(("50", "25"));
        var roles = new RoleModels(read);
        (roles.WatchtowerCompactShare, roles.WatchtowerChatMessages).ShouldBe((50.0, 25));
        var defaults = new RoleModels(null);
        (defaults.WatchtowerCompactShare, defaults.WatchtowerChatMessages).ShouldBe((60.0, 40));
        NodeSettings.With(read, "watchtower-compact-share", ["0"]).Error.ShouldNotBeNull();
        NodeSettings.With(read, "watchtower-chat-messages", ["-1"]).Error.ShouldNotBeNull();
        NodeSettings.With(read, "watchtower-chat-messages", ["default"]).Settings!.WatchtowerChatMessages.ShouldBeNull();
    }

    [Fact]
    public void TheWorkerRangeAndLadderKeysRoundTripAndTakeTheirValues()
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        var settings = new NodeSettings();
        foreach (var (key, value) in new[] { ("worker-range", "sonnet-low..opus-xhigh"), ("worker-ladder", "off") })
        {
            settings = NodeSettings.With(settings, key, [value]).Settings.ShouldNotBeNull();
        }
        settings.Write(file);

        var read = NodeSettings.Read(file);
        (string.Join(' ', read.Get("worker-range")!), string.Join(' ', read.Get("worker-ladder")!)).ShouldBe(("sonnet-low..opus-xhigh", "off"));
        read.WorkerLadder.ShouldBe(false);
        var roles = new RoleModels(read);
        (roles.Range(RoleModels.Worker).ToString(), roles.WorkerLadder).ShouldBe(("sonnet-low..opus-xhigh", false));
        var defaults = new RoleModels(null);
        (defaults.Range(RoleModels.Worker).ToString(), defaults.WorkerLadder).ShouldBe(("sonnet..opus-xhigh", true));
        NodeSettings.With(read, "worker-ladder", ["default"]).Settings!.WorkerLadder.ShouldBeNull();
        NodeSettings.With(read, "worker-range", ["loud"]).Error.ShouldNotBeNull().ShouldStartWith("worker-range takes pairs of sonnet, opus");
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
    [InlineData("colour", "blue", "there is no setting colour; there are lan, machine, repo-roots, update-channel, update-policy, max-jobs, jobs-per-repo, job-memory, job-priority, job-plane-output, job-build-cache, lan-port, start-limit, priority-repos, priority-start-limit, week-limit, week-pace, spend-alert, watchtower-compact-share, watchtower-chat-messages, watchtower-compact-idle, watchtower-compact-above, merge-method, maintenance, accept-fleet-flights, autopilot, review-deep-first, user-skills, shortcut-token, repo-trackers, no-auto-merge-repos, opus-cap, review-limit, one-flight-per-repo, sub-task-repos, merge-batch, pr-watch-idle, adopt-after, max-go-arounds, scratch-days, disk-alert-gb, flight-ci-budget, stale-flight-hours, stale-pr-hours, stale-recheck-hours, stale-card-hours, stale-holds, stale-unreachable-hours, watchtower-range, worker-range, planner-range, reviewer-range, summary-model, worker-ladder")]
    // Invariant: the "colour" case spells out every key, so a filtered check that adds a setting runs it too.
    [Trait("Category", InvariantTraitTests.Invariant)]
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

    /// <summary>PIL802: a node with nothing saved has job-plane-output off (planes build on the packages) and the page says off (default); a saved on or off is in force at once.</summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task JobPlaneOutputIsOffByDefaultAndASetIsInForceAtOnce()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var runner = node.App.Services.GetRequiredService<Pilot.Node.Flights.JobRunner>();

        // Off by default since planes build on the packages (PIL802).
        runner.PlaneOutput.ShouldBeFalse();
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == NodeSettings.JobPlaneOutputKey).ShouldSatisfyAllConditions(
            s => s.Value.ShouldBe("off"), s => s.Saved.ShouldBeNull(), s => s.Source.ShouldBe(SettingSources.Default), s => s.Pending.ShouldBeFalse(),
            s => s.Choices.ShouldBe(["on", "off"]), s => s.Detail.ShouldContain("Off by default"));

        var first = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["job-plane-output", "on"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        first.Way.ShouldNotBeNull().ShouldStartWith("job-plane-output saved as on, and in force now");
        runner.PlaneOutput.ShouldBeTrue();

        var off = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["job-plane-output", "off"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        off.Way.ShouldNotBeNull().ShouldStartWith("job-plane-output saved as off, and in force now");
        runner.PlaneOutput.ShouldBeFalse();
        NodeSettings.Read(node.Options.SettingsFile).JobPlaneOutput.ShouldBe(false);
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == NodeSettings.JobPlaneOutputKey).ShouldSatisfyAllConditions(
            s => s.Value.ShouldBe("off"), s => s.Saved.ShouldBe("off"), s => s.Source.ShouldBe(SettingSources.File), s => s.Pending.ShouldBeFalse());

        var on = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["job-plane-output", "on"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        on.Way.ShouldNotBeNull().ShouldStartWith("job-plane-output saved as on, and in force now");
        runner.PlaneOutput.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
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
        // The job priority is in force at once, is read back, is refused when bad, and is on the page with its label and choices.
        var priority = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["job-priority", "idle"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        priority.Way.ShouldNotBeNull().ShouldStartWith("job-priority saved as idle, and in force now");
        node.App.Services.GetRequiredService<Pilot.Node.Flights.JobRunner>().Priority.ShouldBe(Pilot.Node.Flights.JobPriority.Idle);
        NodeSettings.Read(node.Options.SettingsFile).JobPriority.ShouldBe(Pilot.Node.Flights.JobPriority.Idle);
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == NodeSettings.JobPriorityKey).ShouldSatisfyAllConditions(
            s => s.Value.ShouldBe("idle"), s => s.Saved.ShouldBe("idle"), s => s.Label.ShouldBe("Job priority"),
            s => s.Choices.ShouldBe(["below-normal", "idle", "normal"]), s => s.Pending.ShouldBeFalse());
        var badPriority = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["job-priority", "high"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();
        badPriority.Error.ShouldBe("job-priority takes below-normal, idle, normal");

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

    /// <summary>
    /// user-skills is applied live, so the setup check follows the file, never the node's start-time snapshot (options.Saved):
    /// the review of #849 found the checklist offering "Install skills" right after set node user-skills off had taken them out.
    /// </summary>
    [Fact]
    public async Task UserSkillsSetLiveIsWhatTheSetupCheckReads()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var skills = Path.Combine(_root, "skills");
        var setup = new NodeSetup(node.Options, Path.Combine(_root, "pilot-node.exe"), _root)
        {
            Run = (_, _, _) => (1, ""),
            SkillsFolder = skills,
            CarriedSkills = () => [new UserSkills.Skill("demo", new Dictionary<string, byte[]> { ["SKILL.md"] = "v1"u8.ToArray() })],
        };
        var services = node.App.Services;
        var commands = new NodeSettingsCommands(node.Options, node.State, services.GetRequiredService<NodeRestarter>(), setup, TimeProvider.System,
            services.GetRequiredService<Pilot.Node.Flights.JobRunner>(), services.GetRequiredService<RoleModels>());
        SetupCheck Check() => setup.Checks().Single(c => c.Key == NodeSetup.UserSkillsKey);
        CommandResult Set(string value) => commands.Set(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.UserSkillsKey, value], Who = "tests" });

        Set("on").Error.ShouldBeNull();
        Directory.Exists(Path.Combine(skills, "demo")).ShouldBeTrue();
        (Check().Ok, Check().Fix).ShouldBe((true, null));

        Set("off").Error.ShouldBeNull();
        Directory.Exists(Path.Combine(skills, "demo")).ShouldBeFalse();
        var off = Check();
        (off.Ok, off.Optional, off.Fix).ShouldBe((false, true, null));
        off.Line.ShouldContain("off");

        Set("on").Error.ShouldBeNull();
        (Check().Ok, Check().Fix).ShouldBe((true, null));
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
    [Trait("Category", "Slow")]
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
    public void EachRoleHasItsRangeAndTheDefaultPairIsWhatASessionStartsOn()
    {
        var defaults = new RoleModels(null);
        RoleModels.Roles.Select(defaults.Model).ShouldBe(["sonnet", "sonnet", "opus", "opus"]);
        RoleModels.Roles.Select(defaults.Effort).ShouldAllBe(e => e == null);
        RoleModels.Unset.Model(RoleModels.Worker).ShouldBeNull();

        var saved = NodeSettings.With(new NodeSettings(), "worker-range", ["opus"]).Settings!;
        saved = NodeSettings.With(saved, "watchtower-range", ["sonnet", "sonnet-high"]).Settings!;
        saved = NodeSettings.With(saved, "reviewer-range", ["opus-medium..opus-xhigh"]).Settings!;
        var chosen = new RoleModels(saved);
        (chosen.Model(RoleModels.Worker), chosen.Model(RoleModels.Watchtower), chosen.Effort(RoleModels.Reviewer)).ShouldBe(("opus", "sonnet", "medium"));

        var file = Path.Combine(_root, NodeSettings.FileName);
        saved.Write(file);
        NodeSettings.Read(file).Get("worker-range").ShouldBe(["opus..opus"]);
        NodeSettings.With(saved, "worker-range", ["Opus 5"]).Error.ShouldNotBeNull().ShouldStartWith("worker-range takes pairs");
        NodeSettings.With(saved, "planner-range", ["opus-huge"]).Error.ShouldNotBeNull().ShouldStartWith("planner-range takes pairs");
    }

    [Fact]
    public async Task ARolesRangeIsInForceForItsNextSessionWithNoRestart()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["worker-range", "opus-low..opus-high"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        result.Way.ShouldBe("worker-range saved as opus-low..opus-high; the next session of that kind starts with it");
        node.App.Services.GetRequiredService<RoleModels>().Model(RoleModels.Worker).ShouldBe("opus");
        var page = (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull();
        page.Settings.Single(s => s.Key == "worker-range").ShouldSatisfyAllConditions(s => s.Value.ShouldBe("opus-low..opus-high"), s => s.Source.ShouldBe(SettingSources.ThisNode), s => s.Pending.ShouldBeFalse());
        page.Settings.Single(s => s.Key == "planner-range").ShouldSatisfyAllConditions(s => s.Value.ShouldBe("opus..opus-high"), s => s.Source.ShouldBe(SettingSources.BuiltIn));
        page.Settings.Single(s => s.Key == "watchtower-range").Value.ShouldBe("sonnet..sonnet-high");
    }

    [Fact]
    public void ADeepReviewRunsOnTheReviewersMaxAndNeverOnEffortMax()
    {
        var range = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { ["reviewer-range"] = "sonnet-high..opus-medium" } });
        (range.ReviewerModel(ReviewDepth.Deep), range.ReviewerEffort(ReviewDepth.Deep)).ShouldBe(("opus", "medium"));
        (range.ReviewerModel(ReviewDepth.Standard), range.ReviewerEffort(ReviewDepth.Standard)).ShouldBe(("sonnet", "high"));
        NodeSettings.With(new NodeSettings(), "reviewer-range", ["sonnet-high..opus-max"]).Error.ShouldNotBeNull().ShouldContain("effort max");
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

    /// <summary>A repo list keeps every entry through the file: priority-repos was written as its first entry alone, so a second repo was lost on save.</summary>
    [Theory]
    [InlineData("priority-repos")]
    [InlineData("no-auto-merge-repos")]
    [InlineData("sub-task-repos")]
    [InlineData("repo-trackers")]
    public void ARepoListKeepsEveryEntryThroughTheFile(string key)
    {
        var file = Path.Combine(_root, $"{key}.json");
        string[] entries = key == "repo-trackers" ? ["tianwen=github", "pilot=shortcut"] : ["tianwen", "pilot"];
        NodeSettings.With(new NodeSettings(), key, entries).Settings.ShouldNotBeNull().Write(file);

        NodeSettings.Read(file).Get(key).ShouldBe(entries);
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

        var rows = (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Where(s => NodeSettings.IsRoleKey(s.Key)).ToArray();

        rows.Length.ShouldBe(RoleModels.Roles.Count);
        rows.ShouldAllBe(r => !r.Choices.Contains("none"));
        rows.ShouldAllBe(r => r.Detail.Contains("a bare model leaves the effort to Claude Code"));
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
