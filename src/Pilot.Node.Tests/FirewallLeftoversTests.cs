using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The firewall-leftovers check and fix: netsh's verbose rule list parsed, the folder and existence rules, and the delete script.</summary>
public sealed class FirewallLeftoversTests
{
    private const string Repos = @"C:\Users\me\source\repos";
    private const string Data = @"C:\Users\me\AppData\Local\pilot2";

    /// <summary>Shaped as <c>netsh advfirewall firewall show rule name=all dir=in verbose</c> prints it: blocks split by a blank line, rules with and without a program, several profiles.</summary>
    private const string Sample = """
        Rule Name:                            pilot-node
        ----------------------------------------------------------------------
        Enabled:                              Yes
        Direction:                            In
        Profiles:                             Domain,Private,Public
        Grouping:
        LocalIP:                              Any
        RemoteIP:                             Any
        Protocol:                             TCP
        LocalPort:                            Any
        RemotePort:                           Any
        Edge traversal:                       No
        Program:                              C:\Users\me\source\repos\sharpastro\pilot\.claude\worktrees\pil1\src\Pilot.Node\bin\Release\net10.0\pilot-node.exe
        Action:                               Allow

        Rule Name:                            pilot2 node
        ----------------------------------------------------------------------
        Enabled:                              Yes
        Direction:                            In
        Profiles:                             Domain,Private
        Protocol:                             TCP
        LocalPort:                            7345
        Action:                               Allow

        Rule Name:                            pilot-node
        ----------------------------------------------------------------------
        Enabled:                              Yes
        Direction:                            In
        Profiles:                             Private
        Protocol:                             UDP
        Program:                              C:\USERS\ME\SOURCE\REPOS\sharpastro\pilot\.claude\worktrees\pil1\src\Pilot.Node\bin\Release\net10.0\pilot-node.exe
        Action:                               Allow

        Rule Name:                            Blocked thing
        ----------------------------------------------------------------------
        Enabled:                              Yes
        Direction:                            In
        Program:                              C:\Users\me\source\repos\a\gone.exe
        Action:                               Block

        Rule Name:                            Alive
        ----------------------------------------------------------------------
        Program:                              C:\Users\me\source\repos\a\alive.exe
        Action:                               Allow

        Rule Name:                            Lookalike
        ----------------------------------------------------------------------
        Program:                              C:\Users\me\source\repos2\a\gone.exe
        Action:                               Allow

        Rule Name:                            Elsewhere
        ----------------------------------------------------------------------
        Program:                              C:\Program Files\App\app.exe
        Action:                               Allow

        Rule Name:                            Scratch
        ----------------------------------------------------------------------
        Program:                              C:\Users\me\AppData\Local\pilot2\jobs\j1.tmp\x\pilot-gui.exe
        Action:                               Allow
        """;

    private static NodeSetup Setup(Func<string, bool> exists, Func<string, string, bool>? elevated = null, List<string>? ran = null) =>
        new(new NodeOptions("/tmp/n.sock", Data, RepoRootsGiven: [Repos]), "pilot-node.exe", Path.GetTempPath())
        {
            Run = (file, args, _) => { ran?.Add(file + " " + string.Join(' ', args)); return (0, Sample); },
            RunElevated = elevated ?? ((_, _) => true),
            ProgramExists = exists,
        };

    [Fact]
    public void ParsesRulesWithAndWithoutProgramAndTheirAction()
    {
        var rules = FirewallLeftovers.Parse(Sample);

        rules.Count.ShouldBe(8);
        rules[1].ShouldBe(new FirewallLeftovers.Rule("pilot2 node", "Allow", ""));
        rules[3].Action.ShouldBe("Block");
        rules[0].Program.ShouldEndWith("pilot-node.exe");
    }

    [Fact]
    public void FolderMatchIsADirectoryPrefixAndCaseInsensitive()
    {
        var found = FirewallLeftovers.Leftovers(FirewallLeftovers.Parse(Sample), [Repos, Data + @"\jobs"], _ => false);

        found.ShouldBe([
            @"C:\Users\me\source\repos\sharpastro\pilot\.claude\worktrees\pil1\src\Pilot.Node\bin\Release\net10.0\pilot-node.exe",
            @"C:\Users\me\source\repos\a\alive.exe",
            @"C:\Users\me\AppData\Local\pilot2\jobs\j1.tmp\x\pilot-gui.exe",
        ]);
    }

    [Fact]
    public void ARuleWhoseFileExistsIsKept()
    {
        var found = FirewallLeftovers.Leftovers(FirewallLeftovers.Parse(Sample), [Repos], p => p.EndsWith("alive.exe"));

        found.ShouldNotContain(p => p.EndsWith("alive.exe"));
        found.Count.ShouldBe(1);
    }

    [Fact]
    public void TheScriptDeletesEachProgramByPath()
    {
        FirewallLeftovers.Script([@"C:\a\x.exe", @"C:\a\y.exe"]).ShouldBe(
            "netsh advfirewall firewall delete rule name=all dir=in program=\"C:\\a\\x.exe\" | Out-Null\n" +
            "netsh advfirewall firewall delete rule name=all dir=in program=\"C:\\a\\y.exe\" | Out-Null");
        FirewallLeftovers.Script([]).ShouldBeNull();
    }

    [Fact]
    public void TheCheckCountsLeftoversAndIsOptionalWithOneNetshRun()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the firewall check is Windows only");
        var ran = new List<string>();
        var check = Setup(_ => false, ran: ran).Checks().Single(c => c.Key == NodeSetup.FirewallLeftoversKey);

        check.ShouldSatisfyAllConditions(
            c => c.Ok.ShouldBeFalse(),
            c => c.Optional.ShouldBeTrue(),
            c => c.Fix.ShouldBe("Remove (asks for elevation)"),
            c => c.Line.ShouldContain("firewall rules for programs that no longer exist (worktree builds)"));
        ran.Count(r => r.Contains("name=all")).ShouldBe(1);
    }

    [Fact]
    public async Task NothingElevatesWhenThereAreNone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the firewall check is Windows only");
        var elevations = 0;

        (await Setup(_ => true, (_, _) => { elevations++; return true; }).FixAsync(NodeSetup.FirewallLeftoversKey, TestContext.Current.CancellationToken)).ShouldBeNull();

        elevations.ShouldBe(0);
    }

    [Fact]
    public async Task TheFixElevatesOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the firewall check is Windows only");
        var elevations = 0;
        string? script = null;

        await Setup(_ => false, (_, args) => { elevations++; script = File.ReadAllText(args.Split("-File \"")[1].TrimEnd('"')); return true; }).FixAsync(NodeSetup.FirewallLeftoversKey, TestContext.Current.CancellationToken);

        elevations.ShouldBe(1);
        script.ShouldNotBeNull().ShouldContain("netsh advfirewall firewall delete rule name=all dir=in program=");
    }

    [Fact]
    public void TheScriptOfTheSetupIsTheExactDeleteText()
    {
        Setup(p => !p.EndsWith("pilot-gui.exe")).FirewallLeftoversScript().ShouldBe(
            "netsh advfirewall firewall delete rule name=all dir=in program=\"C:\\Users\\me\\AppData\\Local\\pilot2\\jobs\\j1.tmp\\x\\pilot-gui.exe\" | Out-Null");
    }
}
