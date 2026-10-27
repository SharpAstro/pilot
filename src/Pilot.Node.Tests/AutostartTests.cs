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
        // The node alone is the unit's to stop: its jobs run in the cgroup too, and outlive an update or restart (review of #357).
        unit.ShouldContain("KillMode=process");
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
}
