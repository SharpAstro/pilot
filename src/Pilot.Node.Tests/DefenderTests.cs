using System.Text.Json;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The defender setup check and fix: Get-MpPreference's answer read through <see cref="NodeSetup.Run"/>, the diff of
/// what is excluded against what a build machine wants, and the elevated script that adds the rest.</summary>
public sealed class DefenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Repos = @"C:\src";
    private const string Packages = @"C:\Users\me\.nuget\packages";
    private const string Data = @"C:\Users\me\AppData\Local\pilot2";

    /// <summary>What the pwsh call prints, as ConvertTo-Json -Compress writes it.</summary>
    private static string Json(string[]? paths = null, string[]? processes = null, string mode = "Normal", bool realtime = true, string[]? devDrives = null) =>
        JsonSerializer.Serialize(new { Paths = paths ?? [], Processes = processes ?? [], Mode = mode, Realtime = realtime, DevDrives = devDrives ?? [] });

    private static NodeSetup Setup(Func<string> answer, string[]? folders = null, bool applies = true, Func<string, string, bool>? elevated = null) =>
        new(new NodeOptions("/tmp/n.sock", Path.GetTempPath()), "pilot-node.exe", Path.GetTempPath())
        {
            Run = (_, _, _) => (0, answer()),
            DefenderApplies = applies,
            DefenderFolders = () => folders ?? [Repos, Packages, Data],
            RunElevated = elevated ?? ((_, _) => true),
        };

    [Fact]
    public void TheCheckNamesTheFoldersDefenderDoesNotExcludeYet()
    {
        var check = Setup(() => Json(paths: [Packages.ToLowerInvariant() + @"\"])).DefenderCheck();

        check.ShouldSatisfyAllConditions(
            c => c.Key.ShouldBe(NodeSetup.DefenderKey),
            c => c.Ok.ShouldBeFalse(),
            c => c.Line.ShouldBe($@"not excluded: {Repos}, {Data}: every file a build and its tests write is scanned"),
            c => c.Fix.ShouldNotBeNull(),
            c => c.Optional.ShouldBeTrue());
    }

    [Fact]
    public void AFolderUnderAnExcludedOneNeedsNoExclusionOfItsOwn()
    {
        var check = Setup(() => Json(paths: [@"C:\"])).DefenderCheck();

        check.Ok.ShouldBeTrue();
        check.Fix.ShouldBeNull();
        check.Line.ShouldBe("3 folders excluded");
    }

    [Fact]
    public void AFolderOnADevDriveIsSkippedAndTheLineSaysSo()
    {
        var setup = Setup(() => Json(paths: [Packages, Data], devDrives: ["D"]), folders: [@"D:\repos", Packages, Data]);

        var check = setup.DefenderCheck();

        check.Ok.ShouldBeTrue();
        check.Line.ShouldBe(@"2 folders excluded; D:\repos on a Dev Drive, which Defender treats on its own");
    }

    [Fact]
    public void DefenderThatIsNotActiveIsFine()
    {
        // Passive beside another antivirus, and real-time protection off, are both not the antivirus in force.
        foreach (var answer in new[] { Json(mode: "Passive mode"), Json(realtime: false), Json(mode: "") })
        {
            var check = Setup(() => answer).DefenderCheck();

            check.Ok.ShouldBeTrue();
            check.Fix.ShouldBeNull();
            check.Line.ShouldStartWith("Windows Defender is not the antivirus in force");
        }
    }

    [Fact]
    public void ExclusionsHiddenFromANonElevatedProcessAreSaidSoAndTheFixIsOffered()
    {
        var check = Setup(() => Json(paths: ["N/A: Must be an administrator to view exclusions"], processes: ["N/A: Must be an administrator to view exclusions"])).DefenderCheck();

        check.Ok.ShouldBeFalse();
        check.Fix.ShouldNotBeNull();
        check.Line.ShouldStartWith("Defender shows its exclusions to an elevated process only");
    }

    [Fact]
    public void ANonsenseAnswerIsNotTakenForExcluded()
    {
        var check = Setup(() => "pwsh: The term 'Get-MpPreference' is not recognized").DefenderCheck();

        check.Ok.ShouldBeFalse();
        check.Fix.ShouldBeNull();
    }

    [Fact]
    public void TheScriptHasOneAddMpPreferenceLineForEachAndDoublesAQuote()
    {
        var script = DefenderExclusions.Script([Repos, @"C:\it's here"], ["dotnet.exe"]);

        script.ShouldBe("Add-MpPreference -ExclusionPath 'C:\\src'\nAdd-MpPreference -ExclusionPath 'C:\\it''s here'\nAdd-MpPreference -ExclusionProcess 'dotnet.exe'");
        DefenderExclusions.Script([], []).ShouldBe("");
    }

    [Fact]
    public void TheDotnetRootIsTheFolderAboveTheSdkFolder() =>
        DefenderExclusions.SdkRoot("9.0.300 [C:\\Program Files\\dotnet\\sdk]\r\n10.0.100 [C:\\Program Files\\dotnet\\sdk]\r\n").ShouldBe(@"C:\Program Files\dotnet");

    [Fact]
    public async Task TheFixAsksOnceForTheFoldersAndForTheProcessesOnlyWhenGiven()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node adds Defender's exclusions on Windows only");
        var excluded = new List<string> { Packages };
        var excludedProcesses = new List<string> { "dotnet.exe" };
        var prompts = 0;
        string? elevated = null;
        var setup = Setup(() => Json(paths: [.. excluded], processes: [.. excludedProcesses]), elevated: (_, args) =>
        {
            prompts++;
            elevated = File.ReadAllText(args.Split("-File \"")[1].TrimEnd('"'));
            excluded.AddRange([Repos, Data]);
            excludedProcesses.AddRange(DefenderExclusions.Processes);
            return true;
        });

        (await setup.FixAsync(NodeSetup.DefenderKey, Ct)).ShouldBeNull();

        prompts.ShouldBe(1);
        elevated.ShouldNotBeNull().ShouldContain($"Add-MpPreference -ExclusionPath '{Repos}'\nAdd-MpPreference -ExclusionPath '{Data}'\n");
        elevated.ShouldNotContain(Packages);
        elevated.ShouldNotContain("-ExclusionProcess");

        // With --processes: only the processes not excluded yet, in the same one script.
        excluded.Clear();
        excluded.AddRange([Repos, Packages, Data]);
        excludedProcesses.Clear();
        excludedProcesses.Add("DOTNET.EXE");
        (await setup.FixAsync(NodeSetup.DefenderKey, Ct, processes: true)).ShouldBeNull();

        prompts.ShouldBe(2);
        elevated.ShouldNotContain("-ExclusionPath");
        elevated.ShouldContain("Add-MpPreference -ExclusionProcess 'pilot-node.exe'");
        elevated.ShouldContain("Add-MpPreference -ExclusionProcess 'MSBuild.exe'");
        elevated.ShouldContain("Add-MpPreference -ExclusionProcess 'VBCSCompiler.exe'");
        elevated.ShouldContain("Add-MpPreference -ExclusionProcess 'testhost.exe'");
        elevated.ShouldNotContain("'dotnet.exe'");
    }

    [Fact]
    public async Task TheFixAsksForNothingWhenNothingIsMissingOrDefenderIsNotActive()
    {
        var prompts = 0;
        bool Count(string a, string b) { prompts++; return true; }

        (await Setup(() => Json(paths: [Repos, Packages, Data]), elevated: Count).FixAsync(NodeSetup.DefenderKey, Ct)).ShouldBeNull();
        (await Setup(() => Json(mode: "Passive mode"), elevated: Count).FixAsync(NodeSetup.DefenderKey, Ct)).ShouldBeNull();

        prompts.ShouldBe(0);
    }

    [Fact]
    public async Task ARefusedPromptAndAFolderStillMissingEachGiveALine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node adds Defender's exclusions on Windows only");

        (await Setup(() => Json(), elevated: (_, _) => false).FixAsync(NodeSetup.DefenderKey, Ct))
            .ShouldBe("the elevation prompt was refused or did not open");
        (await Setup(() => Json(paths: [Packages, Data])).FixAsync(NodeSetup.DefenderKey, Ct))
            .ShouldBe($"still not excluded: {Repos}");
    }

    [Fact]
    public async Task OffWindowsTheCheckAndTheFixSayItDoesNotApply()
    {
        var setup = Setup(() => "", applies: false);

        setup.DefenderCheck().ShouldSatisfyAllConditions(
            c => c.Ok.ShouldBeTrue(),
            c => c.Fix.ShouldBeNull(),
            c => c.Line.ShouldBe("does not apply here: Windows Defender is Windows only"));
        (await setup.FixAsync(NodeSetup.DefenderKey, Ct, processes: true)).ShouldBe("Windows Defender's exclusions apply on Windows only");
        setup.Checks().ShouldNotContain(c => c.Key == NodeSetup.DefenderKey);
    }

    [Fact]
    public async Task TheSetupVerbListsDefenderAmongItsKeys()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var refused = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Setup, Target = Verbs.NodeTarget, Args = ["nonsense", "--processes"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        refused.Error.ShouldNotBeNull().ShouldContain("defender");
    }
}
