using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Autostart: the command line a Run entry holds and the unit systemd runs, quoted as each reads it back, and options
/// the node would refuse refused before anything is written. Installing itself writes the user's Run key or unit, so
/// it is not run here.
/// </summary>
public sealed class AutostartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("--lan", "--lan")]
    [InlineData(@"C:\src\sharp astro", @"""C:\src\sharp astro""")]
    [InlineData(@"C:\a b\", @"""C:\a b\\""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData("", @"""""")]
    public void AnArgumentIsQuotedAsWindowsReadsItBack(string arg, string quoted) => Autostart.Quote(arg).ShouldBe(quoted);

    [Fact]
    public void TheRunEntryStartsTheNodeWindowlessWithItsOptions()
    {
        Autostart.WindowsCommand(@"C:\Users\ada\AppData\Local\pilot2\bin\pilot-node.exe", ["--lan", "--repo-root", @"C:\src\sharp astro"], @"C:\Windows\System32")
            .ShouldBe(@"C:\Windows\System32\conhost.exe --headless C:\Users\ada\AppData\Local\pilot2\bin\pilot-node.exe --lan --repo-root ""C:\src\sharp astro""");
    }

    [Fact]
    public void TheCatsRunEntryIsItsQuotedPathWithNoConsoleWrapperAndItsOwnName()
    {
        Autostart.BuddyName.ShouldBe("pilot-buddy");
        Autostart.BuddyName.ShouldNotBe(Autostart.Name, "the cat and the node are two Run values; one never replaces the other");
        Autostart.BuddyCommand(@"C:\Users\ada\AppData\Local\pilot2\bin\pilot-buddy.exe").ShouldBe(@"C:\Users\ada\AppData\Local\pilot2\bin\pilot-buddy.exe");
        Autostart.BuddyCommand(@"C:\Program Files\pilot\pilot-buddy.exe").ShouldBe(@"""C:\Program Files\pilot\pilot-buddy.exe""");
    }

    [Fact]
    public void ANodeInstalledAloneHasNoCatToStart()
    {
        // A folder that does not exist holds no cat: no entry is made for a binary that is not there.
        Autostart.BuddyBeside(Path.Combine(Path.GetTempPath(), "pilot-no-such-folder-" + Guid.NewGuid().ToString("N"), "pilot-node.exe")).ShouldBeNull();
    }

    [Fact]
    public void ACatBesideTheNodeIsTheOneStarted()
    {
        // A temporary folder holding an empty cat binary beside the node: its path is the cat to start. The folder is removed after.
        var folder = Directory.CreateTempSubdirectory("pilot-autostart-");
        try
        {
            var buddy = Path.Combine(folder.FullName, OperatingSystem.IsWindows() ? "pilot-buddy.exe" : "pilot-buddy");
            File.WriteAllBytes(buddy, []);
            Autostart.BuddyBeside(Path.Combine(folder.FullName, OperatingSystem.IsWindows() ? "pilot-node.exe" : "pilot-node")).ShouldBe(buddy);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class FakeRuns : Autostart.IRunValues
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Read(string name) => Values.GetValueOrDefault(name);

        public void Write(string name, string command) => Values[name] = command;

        public bool Delete(string name) => Values.Remove(name);
    }

    // A temporary folder with an empty node and an empty cat beside it, removed after: the cat is there to be found.
    private static void WithNodeAndCat(Action<string> body)
    {
        var folder = Directory.CreateTempSubdirectory("pilot-autostart-");
        try
        {
            var node = Path.Combine(folder.FullName, OperatingSystem.IsWindows() ? "pilot-node.exe" : "pilot-node");
            File.WriteAllBytes(Path.Combine(folder.FullName, OperatingSystem.IsWindows() ? "pilot-buddy.exe" : "pilot-buddy"), []);
            body(node);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void TurningTheCatOffTakesItsRunValueOutAndKeepsTheNodes()
    {
        WithNodeAndCat(node =>
        {
            var runs = new FakeRuns();
            runs.Write(Autostart.Name, "node command");
            Autostart.InstallBuddy(node, runs, out _).ShouldBeTrue();
            runs.Values.ShouldContainKey(Autostart.BuddyName);

            Autostart.RemoveBuddy(runs).ShouldBeTrue();

            runs.Values.ShouldNotContainKey(Autostart.BuddyName);
            runs.Values[Autostart.Name].ShouldBe("node command");
            Autostart.BuddyEntry(runs).ShouldBeNull();
        });
    }

    [Fact]
    public void TurningTheCatOnWritesItsOwnValueAndLeavesTheNodesAlone()
    {
        WithNodeAndCat(node =>
        {
            var runs = new FakeRuns();
            runs.Write(Autostart.Name, "node command");

            Autostart.InstallBuddy(node, runs, out _).ShouldBeTrue();

            runs.Values[Autostart.Name].ShouldBe("node command");
            Autostart.BuddyEntry(runs).ShouldNotBeNull().ShouldContain("pilot-buddy");
        });
    }

    [Fact]
    public void WithNoCatBesideTheNodeNothingIsWritten()
    {
        var runs = new FakeRuns();

        Autostart.InstallBuddy(Path.Combine(Path.GetTempPath(), "pilot-no-such-folder-" + Guid.NewGuid().ToString("N"), "pilot-node.exe"), runs, out var line).ShouldBeFalse();

        runs.Values.ShouldBeEmpty();
        line.ShouldContain("no pilot-buddy beside the node");
    }

    [Fact]
    public async Task AFreshSetupStartsTheCatToo_ButANodeAlreadySetUpKeepsTheChoice()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The cat's Run value is Windows only: elsewhere a fresh setup writes none.
            var none = new FakeRuns();
            await Autostart.InstallFreshBuddyAsync(false, "/opt/pilot-node", none, TextWriter.Null);
            none.Values.ShouldBeEmpty();
            return;
        }
        var fresh = new FakeRuns();
        var kept = new FakeRuns();
        WithNodeAndCatAsync(async node =>
        {
            await Autostart.InstallFreshBuddyAsync(false, node, fresh, TextWriter.Null);
            await Autostart.InstallFreshBuddyAsync(true, node, kept, TextWriter.Null);
        });
        fresh.Values.ShouldContainKey(Autostart.BuddyName);
        kept.Values.ShouldBeEmpty();
    }

    private static void WithNodeAndCatAsync(Func<string, Task> body) => WithNodeAndCat(node => body(node).GetAwaiter().GetResult());

    [Fact]
    public void TheCatsSetupCheckReadsItsOwnRunValueAndOffersStartAtLogonWhenItIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "pilot-setup-" + Guid.NewGuid().ToString("N"));
        var runs = new FakeRuns();
        NodeSetup Setup() => new(new NodeOptions("/tmp/n.sock", root), "/opt/pilot-node", Path.Combine(root, ".claude"))
        {
            AutostartEntry = () => "node command",
            BuddyRuns = runs,
            BuddyAutostartApplies = true,
            BuddyBesideNode = () => "/opt/pilot-buddy",
        };

        var off = Setup().BuddyAutostartCheck();
        runs.Write(Autostart.BuddyName, "/opt/pilot-buddy");
        var on = Setup().BuddyAutostartCheck();

        off.Ok.ShouldBeFalse();
        off.Optional.ShouldBeTrue();
        off.Fix.ShouldBe("Start at logon");
        on.Ok.ShouldBeTrue();
        on.Fix.ShouldBeNull();
    }

    [Fact]
    public async Task TheCatsSetupVerbsTurnItOnAndOffAndTheSetupListNamesThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "pilot-setup-" + Guid.NewGuid().ToString("N"));
        var runs = new FakeRuns();
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", root), "/opt/pilot-node", Path.Combine(root, ".claude"))
        {
            BuddyRuns = runs,
            BuddyAutostartApplies = true,
        };
        runs.Write(Autostart.Name, "node command");
        runs.Write(Autostart.BuddyName, "/opt/pilot-buddy");

        (await setup.FixAsync(NodeSetup.NoBuddyAutostartKey, Ct)).ShouldBeNull();

        runs.Values.ShouldNotContainKey(Autostart.BuddyName);
        runs.Values.ShouldContainKey(Autostart.Name);
        (await setup.FixAsync("nonsense", Ct)).ShouldNotBeNull().ShouldContain(NodeSetup.BuddyAutostartKey);
    }

    [Fact]
    public async Task TheCatsVerbsRefuseOffWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "pilot-setup-" + Guid.NewGuid().ToString("N"));
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", root), "/opt/pilot-node", Path.Combine(root, ".claude")) { BuddyRuns = new FakeRuns(), BuddyAutostartApplies = false };

        (await setup.FixAsync(NodeSetup.BuddyAutostartKey, Ct)).ShouldNotBeNull().ShouldContain("Windows only");
        setup.BuddyAutostartCheck().Line.ShouldContain("Windows only");
    }

    [Fact]
    public void TheUnitRestartsTheNodeButNotWhenAnotherHoldsTheSocket()
    {
        var unit = Autostart.SystemdUnit("/home/ada/.local/share/pilot2/bin/pilot-node", ["--lan", "--repo-root", "/home/ada/src/my org"]);

        unit.ShouldContain("ExecStart=/home/ada/.local/share/pilot2/bin/pilot-node --lan --repo-root \"/home/ada/src/my org\"\n");
        unit.ShouldContain("Restart=always");
        unit.ShouldContain($"RestartPreventExitStatus={NodeExitCodes.AlreadyRunning}");
        unit.ShouldContain("WantedBy=default.target");
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--machine", "DUX")]
    [InlineData("hooks", "install")]
    public async Task OptionsTheNodeWouldRefuseAreRefusedBeforeAnythingIsWritten(params string[] nodeArgs)
    {
        var error = new StringWriter();

        var exit = await NodeCommandLine.RunAsync(["autostart", "install", "--", .. nodeArgs], Stream.Null, TextWriter.Null, error, Ct);

        exit.ShouldBe(NodeExitCodes.InvalidArguments);
        error.ToString().ShouldContain("autostart takes the node's own options");
    }

    [Theory]
    [InlineData("/home/ada/.local/share/pilot2/bin/pilot-node")]
    [InlineData("/home/ada/my bin/pilot-node", "--lan", "--repo-root", "/home/ada/src/my org")]
    [InlineData("/opt/pilot-node", "50%", "$HOME", "say \"hi\"", "back\\slash", "", "it's")]
    public void ExecStartReadsBackAsThePathAndArgumentsTheUnitWasWrittenFrom(string path, params string[] args)
    {
        var unit = Autostart.SystemdUnit(path, args);
        var exec = unit.Split('\n').Single(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))["ExecStart=".Length..];

        var parsed = Autostart.ParseExecStart(exec).ShouldNotBeNull();

        parsed.Path.ShouldBe(path);
        parsed.Args.ShouldBe(args);
        Autostart.UnitIsCurrent(unit).ShouldBeTrue();
    }

    [Fact]
    public void ExecOfReadsThePathAndOptionsOfTheUnitInPlace()
    {
        var unit = Autostart.SystemdUnit("/opt/pilot-node", ["--data", "/x y", "--lan"]).Replace("\n", "\r\n");

        var own = Autostart.ExecOf(unit).ShouldNotBeNull();

        own.Path.ShouldBe("/opt/pilot-node");
        own.Args.ShouldBe(["--data", "/x y", "--lan"]);
        Autostart.ExecOf("[Service]\n").ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void ASetupCheckFlagsAStaleUnitForRewriteAndPassesACurrentOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "pilot-setup-" + Guid.NewGuid().ToString("N"));
        NodeSetup Setup(string unit) => new(new NodeOptions("/tmp/n.sock", root), "/opt/pilot-node", Path.Combine(root, ".claude"))
        {
            AutostartEntry = () => "unit: ExecStart=/opt/pilot-node",
            UnitText = () => unit,
        };
        var current = Autostart.SystemdUnit("/opt/pilot-node", ["--data", "/x"]);

        var stale = Setup(current.Replace("Restart=always", "Restart=always\nKillMode=process")).Checks().Single(c => c.Key == NodeSetup.AutostartKey);
        var ok = Setup(current).Checks().Single(c => c.Key == NodeSetup.AutostartKey);

        stale.Ok.ShouldBeFalse();
        stale.Fix.ShouldBe("Rewrite");
        ok.Ok.ShouldBeTrue();
        ok.Fix.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ExecStart=\"/unclosed")]
    public void AnExecStartThatDoesNotReadIsNotCurrent(string line) => Autostart.UnitIsCurrent("[Service]\n" + line + "\n").ShouldBeFalse();

    [Fact]
    public void AUnitThatDiffersFromTheOneTheNodeWritesIsStale()
    {
        var current = Autostart.SystemdUnit("/home/ada/pilot-node", ["--lan"]);

        Autostart.UnitIsCurrent(current).ShouldBeTrue();
        Autostart.UnitIsCurrent(current.Replace("Restart=always", "Restart=always\nKillMode=process")).ShouldBeFalse();
        Autostart.UnitIsCurrent(current.Replace("RestartSec=5", "RestartSec=1")).ShouldBeFalse();
        Autostart.UnitIsCurrent(current.Replace("\n", "\r\n")).ShouldBeTrue();
    }

    [Fact]
    public void UnderSystemdARestartStopsTheNodeAndStartsNoSuccessor()
    {
        var (restarter, lifetime, started) = Restarter("/home/ada/pilot-node", systemd: true);

        restarter.Request("a setting").ShouldBeNull();

        lifetime.Stopped.ShouldBeTrue();
        started.ShouldBeEmpty();
    }

    [Fact]
    public void WithoutSystemdARestartStartsTheSuccessorAndStopsTheNode()
    {
        var (restarter, lifetime, started) = Restarter("/home/ada/pilot-node", systemd: false);

        restarter.Request("a setting").ShouldBeNull();

        lifetime.Stopped.ShouldBeTrue();
        started.ShouldBe(["/home/ada/pilot-node"]);
    }

    [Fact]
    public void SystemdOrNotOnlyAPilotNodeRestartsItself()
    {
        NodeRestarter.UnderSystemdUnit("/usr/bin/dotnet").ShouldBeFalse();
        var (restarter, lifetime, started) = Restarter("/usr/bin/dotnet", systemd: false);

        restarter.Request("a setting").ShouldNotBeNull().ShouldContain("does not restart itself");

        lifetime.Stopped.ShouldBeFalse();
        started.ShouldBeEmpty();
    }

    private static (NodeRestarter Restarter, StopRecorder Lifetime, List<string> Started) Restarter(string node, bool systemd)
    {
        var lifetime = new StopRecorder();
        var started = new List<string>();
        var restarter = new NodeRestarter(lifetime, NullLogger<NodeRestarter>.Instance)
        {
            NodePath = node,
            RestartedBySystemd = _ => systemd,
            StartSuccessor = path =>
            {
                started.Add(path);
                return true;
            },
        };
        return (restarter, lifetime, started);
    }

    private sealed class StopRecorder : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
    }
}
