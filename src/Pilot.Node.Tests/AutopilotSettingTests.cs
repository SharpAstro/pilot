using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The autopilot switch with its v2 meaning (PIL1285): the watchtower runs with a standing goal. A per-node setting, off by default,
/// changed by the person only; an old settings file's "autopilot" member (the switch PIL1271 removed) never turns it on.
/// </summary>
public sealed class AutopilotSettingTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PersonOnly = "autopilot is the person's to change";

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2a-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TestDirs.DeleteTree(_root);
        }
    }

    private static CommandRequest Set(string who, bool agent, string value) =>
        new() { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["autopilot", value], Who = who, Agent = agent };

    private static async Task<CommandResult> Send(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull();

    [Fact]
    public void ItIsOffByDefaultAndNullAndAbsentReadAsOff()
    {
        new RoleModels(new NodeSettings()).Autopilot.ShouldBeFalse();
        new RoleModels(new NodeSettings { Autopilot = null }).Autopilot.ShouldBeFalse();
        NodeSettings.Read(Path.Combine(_root, "none.json")).Autopilot.ShouldBeNull();
        NodeSettings.Keys.ShouldContain(NodeSettings.AutopilotKey);
    }

    [Fact]
    public void ItSavesOnAndOffClearsAndRefusesOther()
    {
        var on = NodeSettings.With(new NodeSettings(), "autopilot", ["on"]).Settings!;
        on.Get("autopilot").ShouldBe(["on"]);
        new RoleModels(on).Autopilot.ShouldBeTrue();
        var off = NodeSettings.With(on, "autopilot", ["off"]).Settings!;
        new RoleModels(off).Autopilot.ShouldBeFalse();
        NodeSettings.With(on, "autopilot", ["default"]).Settings!.Autopilot.ShouldBeNull();
        NodeSettings.With(new NodeSettings(), "autopilot", ["maybe"]).Error.ShouldBe("autopilot takes on or off");
    }

    [Fact]
    public void ItIsStoredUnderANewMemberAndReadsBack()
    {
        var file = Path.Combine(_root, NodeSettings.FileName);
        new NodeSettings { Autopilot = true }.Write(file);

        var text = File.ReadAllText(file);
        text.ShouldContain("\"autopilotGoal\": true");
        text.ShouldNotContain("\"autopilot\"");
        NodeSettings.Read(file).Autopilot.ShouldBe(true);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void AnOldFilesAutopilotMemberIsIgnoredSoNoNodeWakesUpWithItOn(string value)
    {
        var file = Path.Combine(_root, "old.json");
        File.WriteAllText(file, $$"""{ "autopilot": {{value}}, "lan": true, "max-jobs": 3 }""");

        var read = NodeSettings.Read(file);

        read.Autopilot.ShouldBeNull();
        new RoleModels(read).Autopilot.ShouldBeFalse();
        (read.Lan, read.MaxJobs).ShouldBe((true, (int?)3));
    }

    [Fact]
    public async Task ThePersonSetsItOnAndOffAndTheNodeSaysSo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        (await Send(node, Set("cli", false, "on"))).Error.ShouldBeNull();
        NodeSettings.Read(node.Options.SettingsFile).Autopilot.ShouldBe(true);
        (await Send(node, Set("gui", false, "off"))).Error.ShouldBeNull();
        NodeSettings.Read(node.Options.SettingsFile).Autopilot.ShouldBe(false);
    }

    [Fact]
    public async Task AnAssistantTheLookoutAndAFlightSessionAreRefusedWithThePersonOnlyLineAndNothingIsSaved()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lookout = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 1, Branch = "flight/tia1", Worktree = "/r/TIA1", Base = "main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = "w-tia1" });

        foreach (var who in new[] { "some-assistant", lookout, "w-tia1" })
        {
            foreach (var value in new[] { "on", "off" })
            {
                (await Send(node, Set(who, true, value))).Error.ShouldNotBeNull().ShouldContain(PersonOnly, customMessage: who);
            }
        }
        File.Exists(node.Options.SettingsFile).ShouldBeFalse();
    }
}
