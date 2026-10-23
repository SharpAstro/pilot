using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>runaway-agent's machine/Directory.Build.rsp: found by the setup checklist as missing, differing or installed,
/// and put in the home folder by its fix, a differing file backed up first and a second run changing nothing.</summary>
public sealed class BuildRspTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Rsp = "# node reuse off\n-nodeReuse:false\n";

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Home => Path.Combine(_root, "home");

    private string HomeRsp => Path.Combine(Home, NodeSetup.BuildRsp);

    private string Clone(bool withFile = true)
    {
        var clone = Directory.CreateDirectory(Path.Combine(_root, "src", "runaway-agent")).FullName;
        if (withFile)
        {
            Directory.CreateDirectory(Path.Combine(clone, "machine"));
            File.WriteAllText(Path.Combine(clone, "machine", NodeSetup.BuildRsp), Rsp);
        }
        return clone;
    }

    private NodeSetup Setup(string? clone)
    {
        Directory.CreateDirectory(Home);
        return new NodeSetup(new NodeOptions("/tmp/n.sock", _root), "pilot-node", Path.Combine(_root, ".claude"))
        {
            SettingsFile = Path.Combine(_root, ".claude", "settings.json"),
            MachineFolder = Home,
            FindRunawayAgent = () => clone,
            Run = (_, _, _) => (1, ""),
        };
    }

    private static SetupCheck RspCheck(NodeSetup setup) => setup.Checks().Single(c => c.Key == NodeSetup.BuildRspKey);

    private IEnumerable<string> Backups() => Directory.EnumerateFiles(Home, NodeSetup.BuildRsp + ".bak-*");

    [Fact]
    public async Task AMissingFileIsOfferedAndInstalledOnceOnly()
    {
        var setup = Setup(Clone());

        var before = RspCheck(setup);
        (before.Ok, before.Optional, before.Fix).ShouldBe((false, true, "Install response file"));
        before.Line.ShouldContain(HomeRsp);

        (await setup.FixAsync(NodeSetup.BuildRspKey, Ct)).ShouldBeNull();

        File.ReadAllText(HomeRsp).ShouldBe(Rsp);
        Backups().ShouldBeEmpty();
        var after = RspCheck(setup);
        (after.Ok, after.Fix).ShouldBe((true, null));
        var stamp = File.GetLastWriteTimeUtc(HomeRsp);

        (await setup.FixAsync(NodeSetup.BuildRspKey, Ct)).ShouldBeNull();

        File.ReadAllText(HomeRsp).ShouldBe(Rsp);
        File.GetLastWriteTimeUtc(HomeRsp).ShouldBe(stamp);
        Backups().ShouldBeEmpty();
    }

    [Fact]
    public async Task ADifferingFileFailsTheCheckAndIsBackedUpBeforeTheFixReplacesIt()
    {
        var setup = Setup(Clone());
        File.WriteAllText(HomeRsp, "-m:2\n");

        var differing = RspCheck(setup);
        (differing.Ok, differing.Optional, differing.Fix).ShouldBe((false, true, "Install response file"));
        differing.Line.ShouldContain("differs");

        (await setup.FixAsync(NodeSetup.BuildRspKey, Ct)).ShouldBeNull();

        File.ReadAllText(HomeRsp).ShouldBe(Rsp);
        var backup = Backups().ShouldHaveSingleItem();
        File.ReadAllText(backup).ShouldBe("-m:2\n");
        RspCheck(setup).Ok.ShouldBeTrue();

        (await setup.FixAsync(NodeSetup.BuildRspKey, Ct)).ShouldBeNull();

        File.ReadAllText(HomeRsp).ShouldBe(Rsp);
        Backups().ShouldHaveSingleItem().ShouldBe(backup);
    }

    [Fact]
    public void AnInstalledFileThatMatchesTheCloneIsOk()
    {
        var setup = Setup(Clone());
        File.WriteAllText(HomeRsp, Rsp);

        var check = RspCheck(setup);
        (check.Ok, check.Fix, check.Optional).ShouldBe((true, null, true));
    }

    [Fact]
    public async Task WithNoCloneTheCheckSaysWhereToGetItAndTheFixRefuses()
    {
        var setup = Setup(null);

        var check = RspCheck(setup);
        (check.Ok, check.Fix, check.Optional).ShouldBe((false, null, true));
        check.Line.ShouldContain(GuardHooks.Home);
        (await setup.FixAsync(NodeSetup.BuildRspKey, Ct)).ShouldNotBeNull().ShouldContain(GuardHooks.Home);
        File.Exists(HomeRsp).ShouldBeFalse();
    }

    [Fact]
    public void WithNoCloneAFileThatIsThereHasNothingToBeComparedWithAndPasses()
    {
        var setup = Setup(null);
        File.WriteAllText(HomeRsp, "-m:2\n");

        RspCheck(setup).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ACloneWithoutTheFileHasNoButtonAndTheFixSaysSo()
    {
        var setup = Setup(Clone(withFile: false));

        var check = RspCheck(setup);
        (check.Ok, check.Fix).ShouldBe((false, null));
        check.Line.ShouldContain("machine/" + NodeSetup.BuildRsp);
        (await setup.FixAsync(NodeSetup.BuildRspKey, Ct)).ShouldNotBeNull().ShouldContain("machine/" + NodeSetup.BuildRsp);
        File.Exists(HomeRsp).ShouldBeFalse();
    }
}
