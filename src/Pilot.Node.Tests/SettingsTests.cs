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
    [InlineData("colour", "blue", "there is no setting colour; there are lan, machine, repo-roots, update-channel, update-policy, max-jobs, job-memory, lan-port")]
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
    public void TrustInAFolderAboveCoversTheWatchtowerFolder()
    {
        var profile = Directory.CreateDirectory(Path.Combine(_root, ".claude")).FullName;
        var watchtower = Directory.CreateDirectory(Path.Combine(_root, "data", "watchtower")).FullName;
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), "pilot-node", profile);
        string Key(string path) => path.Replace('\\', '/');

        setup.Trusted(watchtower).ShouldBeNull();
        File.WriteAllText(Path.Combine(_root, ".claude.json"), $$"""{ "projects": { "{{Key(watchtower)}}": { "hasTrustDialogAccepted": false }, "{{Key(_root)}}": { "hasTrustDialogAccepted": true } } }""");
        setup.Trusted(watchtower).ShouldBe(_root);
    }
}
