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
        return plane;
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
        BuildCache.Variables(true, Path.Combine(_root, "org", "pilot"), Root).ShouldBeEmpty();
    }

    [Fact]
    public void ADotnetBuildBecomesARestoreAndTheCachedMsBuild()
    {
        var plane = PlaneIn("pil1");

        var command = BuildCache.With("cd src && dotnet build Pilot.slnx -c Release && dotnet test Pilot.Node.Tests -c Release --no-build", true, plane, Root, MsBuild);

        command.ShouldStartWith("cd src && dotnet restore Pilot.slnx -p:Configuration=Release -p:MSBuildCacheEnabled=true && & 'C:\\VS\\MSBuild\\Current\\Bin\\amd64\\MSBuild.exe' Pilot.slnx -graph -reportfileaccesses");
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
        BuildCache.With(build, true, Path.Combine(_root, "org", "pilot"), Root, MsBuild).ShouldBe(build);
        BuildCache.With(build, true, plane, Root, null).ShouldBe(build);
    }

    [Fact]
    public void ASeedJobGetsTheVariablesAndTheCachedFormThoughItsFolderIsNoPlane()
    {
        var main = Path.Combine(_root, "org", "pilot");

        BuildCache.Variables(true, main, Root, seed: true).ShouldContainKey("MSBuildCacheEnabled");
        BuildCache.Variables(false, main, Root, seed: true).ShouldBeEmpty();
        BuildCache.With("dotnet build Pilot.slnx -c Release", true, main, Root, MsBuild, seed: true).ShouldContain("-reportfileaccesses");
        BuildCache.With("dotnet build Pilot.slnx -c Release", false, main, Root, MsBuild, seed: true).ShouldBe("dotnet build Pilot.slnx -c Release");
    }

    [Fact]
    public async Task AQueuedSeedRunsAsAnOrdinaryNodeJobWithTheCacheOnAndAnOrdinaryJobInTheSameCloneDoesNot()
    {
        var main = Directory.CreateDirectory(Path.Combine(_root, "org", "pilot")).FullName;
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
    public async Task AnOrdinaryNodeJobInAMainCloneKeepsNoCacheVariablesEvenWithTheSettingOn()
    {
        var main = Directory.CreateDirectory(Path.Combine(_root, "org", "pilot")).FullName;
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
