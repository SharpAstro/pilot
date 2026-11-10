using System.Text.Json;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The defender setup check and fix: Get-MpPreference's answer read through <see cref="NodeSetup.Run"/>, the diff of
/// what is excluded against what a build machine wants, and the elevated script that adds the rest.</summary>
public sealed class DefenderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The node's data folder in a test, where the elevated fix's record goes; its own for each test, as they run side by side.</summary>
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-defender-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            TestDirs.DeleteTree(_data);
        }
    }

    private const string Hidden = "N/A: Must be an administrator to view exclusions";

    /// <summary>What the elevated script leaves in the data folder: the exclusions as Get-MpPreference gives them, at <paramref name="at"/>.</summary>
    private void WriteRecord(string[] paths, string[]? processes = null, string at = "2026-10-20T10:00:00.0000000+02:00")
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, DefenderExclusions.RecordFile),
            JsonSerializer.Serialize(new { At = at, Paths = paths, Processes = processes ?? [] }));
    }

    private const string Repos = @"C:\src";
    private const string Packages = @"C:\Users\me\.nuget\packages";
    private const string Data = @"C:\Users\me\AppData\Local\pilot2";

    /// <summary>What the pwsh call prints, as ConvertTo-Json -Compress writes it.</summary>
    private static string Json(string[]? paths = null, string[]? processes = null, string mode = "Normal", bool realtime = true, string[]? devDrives = null) =>
        JsonSerializer.Serialize(new { Paths = paths ?? [], Processes = processes ?? [], Mode = mode, Realtime = realtime, DevDrives = devDrives ?? [] });

    private NodeSetup Setup(Func<string> answer, string[]? folders = null, bool applies = true, Func<string, string, bool>? elevated = null, Func<IReadOnlyList<string>>? wantedProcesses = null) =>
        new(new NodeOptions("/tmp/n.sock", _data), "pilot-node.exe", Path.GetTempPath())
        {
            DefenderProcesses = wantedProcesses,
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
    public void TheBuildCacheFolderUnderTheDataFolderIsAmongTheWantedOnes()
    {
        Directory.CreateDirectory(Pilot.Node.Flights.BuildCache.RootIn(_data));
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _data), "pilot-node.exe", Path.GetTempPath()) { Run = (_, _, _) => (1, "") };

        setup.WantedFolders().ShouldContain(Path.GetFullPath(Pilot.Node.Flights.BuildCache.RootIn(_data)));
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
        var check = Setup(() => Json(paths: [Hidden], processes: [Hidden])).DefenderCheck();

        check.Ok.ShouldBeFalse();
        check.Fix.ShouldNotBeNull();
        check.Line.ShouldStartWith("Defender shows its exclusions to an elevated process only");
    }

    [Fact]
    public void HiddenExclusionsAndARecordCoveringEveryFolderReadOkWithTheDateOfTheRecord()
    {
        WriteRecord([Repos, Packages, Data]);

        var check = Setup(() => Json(paths: [Hidden], processes: [Hidden])).DefenderCheck();

        check.ShouldSatisfyAllConditions(
            c => c.Ok.ShouldBeTrue(),
            c => c.Fix.ShouldBeNull(),
            c => c.Line.ShouldBe("3 folders excluded, as recorded at the last fix on 2026-10-20"));
    }

    [Fact]
    public void HiddenExclusionsAndARecordMissingAFolderAddedSinceNameThatFolderAndOfferTheFix()
    {
        WriteRecord([Repos, Packages]);

        var check = Setup(() => Json(paths: [Hidden], processes: [Hidden])).DefenderCheck();

        check.ShouldSatisfyAllConditions(
            c => c.Ok.ShouldBeFalse(),
            c => c.Fix.ShouldNotBeNull(),
            c => c.Line.ShouldBe($"not excluded, as recorded at the last fix on 2026-10-20: {Data}: every file a build and its tests write is scanned"));
    }

    [Fact]
    public void AnUnreadableRecordIsTakenForNone()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, DefenderExclusions.RecordFile), "not json");

        var check = Setup(() => Json(paths: [Hidden], processes: [Hidden])).DefenderCheck();

        check.Ok.ShouldBeFalse();
        check.Line.ShouldStartWith("Defender shows its exclusions to an elevated process only");
    }

    [Fact]
    public async Task TheFixOnAHiddenListReadsTheRecordTheElevatedScriptWroteAndAddsOnlyWhatIsMissingFromIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node adds Defender's exclusions on Windows only");
        WriteRecord([Repos]);
        string? elevated = null;
        var setup = Setup(() => Json(paths: [Hidden], processes: [Hidden]), elevated: (_, args) =>
        {
            elevated = File.ReadAllText(args.Split("-File \"")[1].TrimEnd('"'));
            WriteRecord([Repos, Packages, Data], at: "2026-10-27T09:00:00.0000000+01:00");
            return true;
        });

        (await setup.FixAsync(NodeSetup.DefenderKey, Ct)).ShouldBeNull();

        elevated.ShouldNotBeNull().ShouldContain($"Add-MpPreference -ExclusionPath '{Packages}'\nAdd-MpPreference -ExclusionPath '{Data}'\n");
        elevated.ShouldNotContain($"-ExclusionPath '{Repos}'");
        setup.DefenderCheck().Line.ShouldBe("3 folders excluded, as recorded at the last fix on 2026-10-27");
    }

    [Fact]
    public async Task TheFixOnAHiddenListThatLeavesNoRecordSaysSoAndARefusedRunLeavesTheOldRecord()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node adds Defender's exclusions on Windows only");
        var hidden = () => Json(paths: [Hidden], processes: [Hidden]);

        (await Setup(hidden).FixAsync(NodeSetup.DefenderKey, Ct)).ShouldNotBeNull().ShouldContain("left no record");

        WriteRecord([Repos]);
        (await Setup(hidden, elevated: (_, _) => false).FixAsync(NodeSetup.DefenderKey, Ct)).ShouldBe("the elevation prompt was refused or did not open");
        Setup(hidden).DefenderCheck().Line.ShouldContain($"{Packages}, {Data}");
    }

    [Fact]
    public void TheElevatedScriptEndsByWritingTheExclusionsItCanReadToTheRecordFile()
    {
        var record = Path.Combine(_data, DefenderExclusions.RecordFile);

        var script = DefenderExclusions.Script([Repos], [], record);

        script.ShouldStartWith("Add-MpPreference -ExclusionPath 'C:\\src'\n$now = Get-MpPreference\n");
        script.ShouldContain("Paths = @($now.ExclusionPath); Processes = @($now.ExclusionProcess)");
        script.ShouldEndWith($"| Set-Content -LiteralPath '{record}' -Encoding utf8 }}");
        DefenderExclusions.Script([Repos], []).ShouldNotContain("Set-Content");
    }

    [Fact]
    public void TheRecordParsesWithItsDateAndAJunkOneIsNone()
    {
        var parsed = DefenderExclusions.ParseRecord(JsonSerializer.Serialize(new { At = "2026-10-20T10:00:00+02:00", Paths = new[] { Repos }, Processes = new[] { "dotnet.exe" } }));

        parsed.ShouldNotBeNull().ShouldSatisfyAllConditions(
            r => r.At.ToString("yyyy-MM-dd").ShouldBe("2026-10-20"),
            r => r.Paths.ShouldBe([Repos]),
            r => r.Processes.ShouldBe(["dotnet.exe"]));
        DefenderExclusions.ParseRecord("{}").ShouldBeNull();
        DefenderExclusions.ParseRecord("[1]").ShouldBeNull();
        DefenderExclusions.ParseRecord("nonsense").ShouldBeNull();
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
        elevated.ShouldContain("Add-MpPreference -ExclusionProcess 'git.exe'");
        elevated.ShouldContain("Add-MpPreference -ExclusionProcess 'git-lfs.exe'");
        elevated.ShouldContain("Add-MpPreference -ExclusionProcess 'git-remote-https.exe'");
        elevated.ShouldNotContain("'dotnet.exe'");
    }

    /// <summary>A discovered runner name reaches the elevated script, and a runner the re-check finds still missing is named.</summary>
    [Fact]
    public async Task ADiscoveredRunnerReachesTheElevatedScriptAndTheStillNotExcludedCheck()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the node adds Defender's exclusions on Windows only");
        string? elevated = null;
        var excludedProcesses = new List<string>();
        var setup = Setup(() => Json(paths: [Repos, Packages, Data], processes: [.. excludedProcesses]),
            elevated: (_, args) =>
            {
                elevated = File.ReadAllText(args.Split("-File \"")[1].TrimEnd('"'));
                excludedProcesses.AddRange(DefenderExclusions.Processes);
                return true;
            },
            wantedProcesses: () => [.. DefenderExclusions.Processes, "Pilot.Node.Tests.exe"]);

        (await setup.FixAsync(NodeSetup.DefenderKey, Ct, processes: true)).ShouldBe("still not excluded: Pilot.Node.Tests.exe");

        elevated.ShouldNotBeNull().ShouldContain("Add-MpPreference -ExclusionProcess 'Pilot.Node.Tests.exe'");
    }

    /// <summary>A file name under a root is data: only a conservative one reaches the elevated script, never one with a typographic quote.</summary>
    [Fact]
    public void AHostileRunnerNameIsNotWanted()
    {
        var root = Directory.CreateDirectory(Path.Combine(_data, "hostile")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(root, "p", "bin", "Release")).FullName;
        var hostile = new[] { "x’;Set-Content injected.txt injected;’Tests.exe", "x'Tests.exe", "a b Tests.exe", "Contests.exe", "Attests.exe" };
        foreach (var name in hostile.Append("Good.Tests.exe"))
        {
            try { File.WriteAllText(Path.Combine(bin, name), ""); } catch (IOException) { }
        }

        var wanted = DefenderExclusions.WantedProcesses([root]);

        wanted.ShouldBe([.. DefenderExclusions.Processes, "Good.Tests.exe"], ignoreOrder: true);
        DefenderExclusions.IsRunnerName("x‘Tests.exe").ShouldBeFalse();
        DefenderExclusions.IsRunnerName("FooTests.exe").ShouldBeTrue();
    }

    /// <summary>A root that itself sits under a bin folder does not let obj files in; the walk does not enter obj at all.</summary>
    [Fact]
    public void ARootUnderABinFolderDoesNotAdmitObjFiles()
    {
        var root = Directory.CreateDirectory(Path.Combine(_data, "bin", "repos")).FullName;
        var obj = Directory.CreateDirectory(Path.Combine(root, "p", "obj")).FullName;
        File.WriteAllText(Path.Combine(obj, "Obj.Tests.exe"), "");
        var other = Directory.CreateDirectory(Path.Combine(root, "p", "out")).FullName;
        File.WriteAllText(Path.Combine(other, "Out.Tests.exe"), "");

        DefenderExclusions.WantedProcesses([root]).ShouldBe(DefenderExclusions.Processes, ignoreOrder: true);
    }

    /// <summary>The test runners built under the repo roots join the list by name, once each, and only from a bin folder.</summary>
    [Fact]
    public void TheWantedProcessesAddEveryTestRunnerBuiltUnderTheRoots()
    {
        var root = Directory.CreateDirectory(Path.Combine(_data, "repos")).FullName;
        foreach (var exe in new[]
        {
            "pilot/src/Pilot.Node.Tests/bin/Release/net10.0/Pilot.Node.Tests.exe",
            "pilot/.claude/worktrees/pil1/src/Pilot.Node.Tests/bin/Debug/net10.0/Pilot.Node.Tests.exe",
            "tianwen/src/TianWen.Lib.Tests.Functional/bin/Release/net10.0/TianWen.Lib.Tests.Functional.exe",
            "tianwen/src/TianWen.Lib.Tests/obj/Release/net10.0/TianWen.Lib.Tests.exe",
        })
        {
            var path = Path.Combine(root, exe.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        var wanted = DefenderExclusions.WantedProcesses([root, Path.Combine(_data, "missing")]);

        wanted.ShouldBe([.. DefenderExclusions.Processes, "Pilot.Node.Tests.exe", "TianWen.Lib.Tests.Functional.exe"], ignoreOrder: true);
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
    [Trait("Category", "Slow")]
    public async Task TheSetupVerbListsDefenderAmongItsKeys()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        var refused = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Setup, Target = Verbs.NodeTarget, Args = ["nonsense", "--processes"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        refused.Error.ShouldNotBeNull().ShouldContain("defender");
    }
}
