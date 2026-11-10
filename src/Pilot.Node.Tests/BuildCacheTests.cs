using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A dotnet build a job runs in a plane goes through the content-keyed build cache while job-build-cache is on (PIL860, a trial); off by default.</summary>
[Collection(JobsCollection.Name)]
public sealed class BuildCacheTests : JobTestsBase
{
    private const string MsBuild = @"C:\VS\MSBuild\Current\Bin\amd64\MSBuild.exe";

    public BuildCacheTests() : base(fakeLauncher: true)
    {
    }

    private string PlaneIn(string code)
    {
        var plane = Path.Combine(_root, "org", "pilot", ".claude", "worktrees", code);
        Directory.CreateDirectory(plane);
        OptIn(plane);
        return plane;
    }

    // A repo opts in by naming the package in its Directory.Packages.props.
    private static string OptIn(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Directory.Packages.props"), "<Project><ItemGroup><PackageVersion Include=\"Microsoft.MSBuildCache.Local\" Version=\"0.1.0\" /></ItemGroup></Project>");
        return folder;
    }

    private string Root => BuildCache.RootIn(Path.Combine(_root, "data"));

    // The runner's own dispatcher starts the job a moment after Run; wait for the fake launcher to have it.
    private async Task<JobLaunch> Launched(bool? setting, string command, string cwd)
    {
        if (setting is { } on)
        {
            _jobs.BuildCache = on;
        }
        _jobs.MsBuild = () => MsBuild;
        var id = _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = "session/a1b2c3d4", Args = [cwd, "0", command], Who = "se1-tia1-parser" }).Made!;
        await Until(id, _ => Fake!.JobOrNull(id) is not null);
        Row(id).Command.ShouldBe(command);
        return Fake!.JobOrNull(id)!.Launch;
    }

    private JobRow Row(string id) => _state.Jobs().Single(j => j.Id == id);

    [Fact]
    public void TheSettingIsOffByDefaultAndListed()
    {
        NodeSettings.Keys.ShouldContain(NodeSettings.JobBuildCacheKey);
        NodeSettings.JobBuildCacheDefault.ShouldBeFalse();
        new NodeSettings().JobBuildCache.ShouldBeNull();
        _jobs.BuildCache.ShouldBeFalse();
        NodeSettings.With(new NodeSettings(), NodeSettings.JobBuildCacheKey, ["on"]).Settings!.JobBuildCache.ShouldBe(true);
        NodeSettings.With(new NodeSettings(), NodeSettings.JobBuildCacheKey, ["off"]).Settings!.JobBuildCache.ShouldBe(false);
        NodeSettings.With(new NodeSettings(), NodeSettings.JobBuildCacheKey, ["maybe"]).Error.ShouldNotBeNull();
    }

    [Fact]
    public void TheVariablesAreSetOnlyWithTheSettingOnAndOnlyForAPlane()
    {
        var plane = PlaneIn("pil1");

        BuildCache.Variables(true, plane, Root).ShouldBe(new Dictionary<string, string>
        {
            ["MSBuildCacheEnabled"] = "true",
            ["MSBuildCacheLocalCacheRootPath"] = Root,
        });
        BuildCache.Variables(true, Path.Combine(plane, "src"), Root).ShouldContainKey("MSBuildCacheEnabled");
        BuildCache.Variables(false, plane, Root).ShouldBeEmpty();
        BuildCache.Variables(true, OptIn(Path.Combine(_root, "org", "pilot")), Root).ShouldBeEmpty();
    }

    [Fact]
    public void ADotnetBuildBecomesARestoreAndTheCachedMsBuild()
    {
        var plane = PlaneIn("pil1");

        var command = BuildCache.With("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build", true, plane, Root, MsBuild);

        command.ShouldStartWith("cd src && dotnet restore Pilot.slnx -p:Configuration=Release -p:MSBuildCacheEnabled=true -p:UseLocalSiblings=false && & 'C:\\VS\\MSBuild\\Current\\Bin\\amd64\\MSBuild.exe' Pilot.slnx -graph -reportfileaccesses");
        command.ShouldContain("-p:MSBuildCacheAsyncCacheMaterialization=false");
        command.ShouldContain(@"'-p:MSBuildCacheIdenticalDuplicateOutputPatterns=**\*'");
        command.ShouldContain($"'-p:MSBuildCacheLocalCacheRootPath={Root}'");
        command.ShouldContain($"'-p:MSBuildCacheLogDirectory={Path.Combine(Root, "logs")}'");
        command.ShouldEndWith(" && dotnet test Pilot.Node.Tests -c Release --no-build");
    }

    [Fact]
    public void ACommandAlreadyCarryingTheFlagsOrNoBuildAtAllIsLeftAlone()
    {
        var plane = PlaneIn("pil1");
        string[] untouched =
        [
            "dotnet build Pilot.slnx -c Release -p:MSBuildCacheEnabled=true",
            "dotnet build Pilot.slnx --no-build",
            "dotnet test Pilot.Node.Tests -c Release --no-build",
            "dotnet test Pilot.Node.Tests -c Release",
            "echo hello",
        ];
        foreach (var command in untouched)
        {
            BuildCache.With(command, true, plane, Root, MsBuild).ShouldBe(command);
        }
        // Rewriting what is already rewritten changes nothing more.
        var once = BuildCache.With("dotnet build Pilot.slnx -c Release", true, plane, Root, MsBuild);
        BuildCache.With(once, true, plane, Root, MsBuild).ShouldBe(once);
    }

    [Fact]
    public void NothingIsRewrittenWhenOffOutsideAPlaneOrWithoutMsBuild()
    {
        var plane = PlaneIn("pil1");
        const string build = "dotnet build Pilot.slnx -c Release";

        BuildCache.With(build, false, plane, Root, MsBuild).ShouldBe(build);
        BuildCache.With(build, true, OptIn(Path.Combine(_root, "org", "pilot")), Root, MsBuild).ShouldBe(build);
        BuildCache.With(build, true, plane, Root, null).ShouldBe(build);
    }

    [Fact]
    public void ASeedJobGetsTheVariablesAndTheCachedFormThoughItsFolderIsNoPlane()
    {
        var main = OptIn(Path.Combine(_root, "org", "pilot"));

        BuildCache.Variables(true, main, Root, seed: true).ShouldContainKey("MSBuildCacheEnabled");
        BuildCache.Variables(false, main, Root, seed: true).ShouldBeEmpty();
        BuildCache.With("dotnet build Pilot.slnx -c Release", true, main, Root, MsBuild, seed: true).ShouldContain("-reportfileaccesses");
        BuildCache.With("dotnet build Pilot.slnx -c Release", false, main, Root, MsBuild, seed: true).ShouldBe("dotnet build Pilot.slnx -c Release");
    }

    [Fact]
    public async Task AQueuedSeedRunsAsAnOrdinaryNodeJobWithTheCacheOnAndAnOrdinaryJobInTheSameCloneDoesNot()
    {
        var main = OptIn(Path.Combine(_root, "org", "pilot"));
        _jobs.BuildCache = true;
        _jobs.MsBuild = () => MsBuild;

        _jobs.QueueSeed(main, "dotnet build Pilot.slnx -c Release").ShouldBeNull();
        var seed = _state.Jobs().Single();
        await Until(seed.Id, _ => Fake!.JobOrNull(seed.Id) is not null);
        var launch = Fake!.JobOrNull(seed.Id)!.Launch;

        seed.Owner.ShouldBeNull();
        launch.Environment!.ShouldContainKey("MSBuildCacheEnabled");
        launch.Command.ShouldContain("-reportfileaccesses");
    }

    [Fact]
    public void ASeedAndAPlaneCheckCarryTheSameGlobalProperties()
    {
        var plane = PlaneIn("pil1");
        var main = OptIn(Path.Combine(_root, "org", "pilot"));
        const string build = "dotnet build Pilot.slnx -c Release";

        var fromPlane = BuildCache.With(build, true, plane, Root, MsBuild);
        var fromMain = BuildCache.With(build, true, main, Root, MsBuild, seed: true);

        fromMain.ShouldBe(fromPlane);
        fromPlane.ShouldContain("-p:UseLocalSiblings=false");
        BuildCache.With(build, true, plane, Root, MsBuild, siblingSource: true).ShouldContain("-p:UseLocalSiblings=true");
        BuildCache.With("dotnet build Pilot.slnx -c Release -p:UseLocalSiblings=true", true, plane, Root, MsBuild).ShouldNotContain("UseLocalSiblings=false");
    }

    [Fact]
    public async Task ARepoThatDoesNotNameThePackageIsLeftAloneInPlaneAndMainClone()
    {
        var plane = Path.Combine(_root, "org", "other", ".claude", "worktrees", "x1");
        Directory.CreateDirectory(plane);
        var main = Directory.CreateDirectory(Path.Combine(_root, "org", "other")).FullName;
        const string build = "dotnet build Other.slnx -c Release";

        BuildCache.OptsIn(plane).ShouldBeFalse();
        BuildCache.With(build, true, plane, Root, MsBuild).ShouldBe(build);
        BuildCache.With(build, true, main, Root, MsBuild, seed: true).ShouldBe(build);
        BuildCache.Variables(true, plane, Root).ShouldBeEmpty();
        BuildCache.Variables(true, main, Root, seed: true).ShouldBeEmpty();
        BuildSeed.BuildOf(main).ShouldBeNull();
        (await Launched(true, build, plane)).Command.ShouldBe(build);
    }

    [Fact]
    public async Task TheSeedMarkIsTheJobsAndGoesWhenItEnds()
    {
        var main = OptIn(Path.Combine(_root, "org", "pilot"));
        _jobs.BuildCache = true;
        _jobs.MsBuild = () => MsBuild;
        const string build = "dotnet build Pilot.slnx -c Release";

        _jobs.QueueSeed(main, build).ShouldBeNull();
        var seed = _state.Jobs().Single();
        await Until(seed.Id, _ => Fake!.JobOrNull(seed.Id) is not null);
        _jobs.EnvironmentOf(seed).ShouldContainKey("MSBuildCacheEnabled");

        // Another job with the identical command in the clone is no seed.
        var other = seed with { Id = "jother" };
        _jobs.EnvironmentOf(other).ShouldNotContainKey("MSBuildCacheEnabled");

        // When the job ends the mark goes: the same row is no seed any more.
        Fake!.Exit(seed.Id, 0);
        // The ended row is written a moment before the runner drops the mark, so wait (with Until's deadline) for the mark to go.
        await Until(seed.Id, row => row.EndedAt is not null && !_jobs.EnvironmentOf(row).ContainsKey("MSBuildCacheEnabled"));
    }

    [Fact]
    public async Task AJobOfASiblingSourceFlightsPlaneIsStartedWithUseLocalSiblingsTrue()
    {
        _state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "pilot", RepoPath = "/r/pilot", Issue = 1, Branch = "flight/tia1", Worktree = "/w/TIA1", Base = "origin/main",
            State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch, SiblingSource = true,
        });
        _jobs.SiblingsOf = _ => [];

        var launch = await Launched(true, "dotnet build Pilot.slnx -c Release", PlaneIn("tia1"));

        launch.Command.ShouldContain("-p:UseLocalSiblings=true");
        launch.Command.ShouldNotContain("UseLocalSiblings=false");
    }

    [Fact]
    public async Task AnOrdinaryNodeJobInAMainCloneKeepsNoCacheVariablesEvenWithTheSettingOn()
    {
        var main = OptIn(Path.Combine(_root, "org", "pilot"));
        _jobs.BuildCache = true;
        _jobs.MsBuild = () => MsBuild;

        var other = _jobs.Run(new CommandRequest { Verb = Verbs.Run, Target = Verbs.NodeTarget, Args = [main, "0", "dotnet build Other.slnx"], Who = "node" }).Made!;
        await Until(other, _ => Fake!.JobOrNull(other) is not null);
        Fake!.JobOrNull(other)!.Launch.Environment!.ShouldNotContainKey("MSBuildCacheEnabled");
        Fake!.JobOrNull(other)!.Launch.Command.ShouldBe("dotnet build Other.slnx");
    }

    [Fact]
    public async Task ByDefaultAPlaneJobRunsAsGivenWithNoCacheVariables()
    {
        var launch = await Launched(null, "dotnet build Pilot.slnx -c Release", PlaneIn("pil1"));

        launch.Command.ShouldBe("dotnet build Pilot.slnx -c Release");
        launch.Environment!.ShouldNotContainKey("MSBuildCacheEnabled");
    }

    [Fact]
    public async Task WithTheSettingOnAPlaneJobGetsTheVariablesAndTheRewrittenCommandWhileItsRowKeepsTheGivenOne()
    {
        var launch = await Launched(true, "dotnet build Pilot.slnx -c Release", PlaneIn("pil1"));

        launch.Command.ShouldContain(MsBuild);
        launch.Environment!["MSBuildCacheEnabled"].ShouldBe("true");
        launch.Environment["MSBuildCacheLocalCacheRootPath"].ShouldBe(Root);
    }

    [Fact]
    public async Task WithTheSettingOnAJobInTheMainCloneIsUntouched()
    {
        var launch = await Launched(true, "dotnet build Pilot.slnx -c Release", _root);

        launch.Command.ShouldBe("dotnet build Pilot.slnx -c Release");
        launch.Environment!.ShouldNotContainKey("MSBuildCacheEnabled");
    }

    [Fact]
    public async Task ATestNoBuildJobWithTheSettingOnIsLeftAlone()
    {
        var launch = await Launched(true, "dotnet test Pilot.Node.Tests -c Release --no-build --filter FullyQualifiedName~Foo", PlaneIn("pil1"));

        launch.Command.ShouldBe("dotnet test Pilot.Node.Tests -c Release --no-build --filter FullyQualifiedName~Foo");
    }
}
