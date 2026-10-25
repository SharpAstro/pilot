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
    [InlineData("colour", "blue", "there is no setting colour; there are lan, machine, repo-roots, update-channel, update-policy, max-jobs, job-memory, lan-port, start-limit, priority-repos, priority-start-limit, week-limit, merge-method, maintenance, accept-fleet-flights, autopilot, watchtower-model, worker-model, planner-model, reviewer-model, reviewer-deep-model, watchtower-effort, worker-effort, planner-effort, reviewer-effort, reviewer-deep-effort")]
    public void ASettingRefusesWhatItDoesNotTake(string key, string value, string error)
        => NodeSettings.With(new NodeSettings(), key, [value]).ShouldBe((null, error));

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
        page.Checks.Select(c => c.Key).ShouldContain(NodeSetup.AutostartKey);

        // The job limits are in force at once, no restart: one waiting for the queue would wait on what it is meant to clear.
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
            RunElevated = (_, args) => { elevated = args; netsh += Rule(gui); return true; },
        };

        // pilot-gui.exe is beside the node with no rule of its own; pilot.exe and pilot-tui.exe are not installed here.
        setup.FirewallAllows().ShouldBe((false, "rule pilot-node lets pilot-node.exe in, but not pilot-gui.exe: the Fleet page and pilot nodes hear no other node"));

        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node makes a firewall rule on Windows only");
        (await setup.FixAsync(NodeSetup.FirewallKey, Ct)).ShouldBeNull();
        elevated.ShouldNotBeNull().ShouldContain($"program=\"{node}\" &");
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
