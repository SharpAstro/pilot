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
