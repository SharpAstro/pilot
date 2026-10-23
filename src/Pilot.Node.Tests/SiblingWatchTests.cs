using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Which sibling clones a repo's build compiles against, read from its props and projects, and which are behind.</summary>
public sealed class SiblingWatchTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _org = Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8]);

    private string Repo => Path.Combine(_org, "tianwen");

    public void Dispose()
    {
        if (Directory.Exists(_org))
        {
            Directory.Delete(_org, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Clone(string name) => Directory.CreateDirectory(Path.Combine(_org, name, ".git"));

    [Fact]
    public void TianwensOneSwitchNamesEverySiblingItProbesOnce()
    {
        Write("src/Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <UseLocalSiblings Condition="'$(UseLocalSiblings)' == ''
                  AND Exists('$(MSBuildThisFileDirectory)..\..\DIR.Lib\src\DIR.Lib\DIR.Lib.csproj')
                  AND Exists('$(MSBuildThisFileDirectory)..\..\Codecs\src\SharpAstro.Tiff\SharpAstro.Tiff.csproj')
                  AND Exists('$(MSBuildThisFileDirectory)..\..\Codecs\src\SharpAstro.Png\SharpAstro.Png.csproj')
                  AND Exists('$(MSBuildThisFileDirectory)..\..\QHYCCD.SDK\QHYCCD.SDK.csproj')">true</UseLocalSiblings>
              </PropertyGroup>
            </Project>
            """);

        SiblingWatch.SiblingNames(Repo).ShouldBe(["DIR.Lib", "Codecs", "QHYCCD.SDK"]);
    }

    [Fact]
    public void ProjectFilesProbeBothThroughTheOrgRootVariableAndPlainDotDotPaths()
    {
        Write("src/Pilot.Gui/Pilot.Gui.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <SharpAstroRoot Condition="'$(SharpAstroRoot)' == '' AND Exists('$(MSBuildThisFileDirectory)..\..\..\DIR.Lib')">$(MSBuildThisFileDirectory)..\..\..\</SharpAstroRoot>
                <UseLocalDirLib Condition="'$(UseLocalDirLib)' == '' AND '$(SharpAstroRoot)' != '' AND Exists('$(SharpAstroRoot)DIR.Lib\src\DIR.Lib\DIR.Lib.csproj')">true</UseLocalDirLib>
                <UseLocalFonts Condition="'$(UseLocalFonts)' == '' AND Exists('..\..\..\Fonts.Lib\src\SharpAstro.Fonts\SharpAstro.Fonts.csproj')">true</UseLocalFonts>
              </PropertyGroup>
            </Project>
            """);

        // The SharpAstroRoot probe is not a UseLocal property: DIR.Lib is named once, by the one that is.
        SiblingWatch.SiblingNames(Repo).ShouldBe(["DIR.Lib", "Fonts.Lib"]);
    }

    [Fact]
    public void AProbeOfTheReposOwnFilesOrAPackageFolderNamesNoSibling()
    {
        Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <UseLocalOwn Condition="Exists('$(MSBuildThisFileDirectory)src\Own\Own.csproj')">true</UseLocalOwn>
                <UseLocalPackage Condition="Exists('$(NuGetPackageRoot)foo\1.0\foo.props')">true</UseLocalPackage>
                <UseLocalSiblings Condition="'$(UseLocalSiblings)' == ''">false</UseLocalSiblings>
              </PropertyGroup>
              <ItemGroup Condition="'$(UseLocalOwn)' == 'true' AND Exists('..\Other\Other.csproj')" />
            </Project>
            """);

        SiblingWatch.SiblingNames(Repo).ShouldBeEmpty();
    }

    [Fact]
    public void BuildOutputAndOtherWorktreesAreNotRead()
    {
        var probe = "<Project><PropertyGroup><UseLocalX Condition=\"Exists('..\\X\\X.csproj')\">true</UseLocalX></PropertyGroup></Project>";
        Write("obj/Generated.csproj", probe);
        Write(".claude/worktrees/other/Directory.Build.props", probe);

        SiblingWatch.SiblingNames(Repo).ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyASiblingThatIsACloneBesideTheRepoIsAskedAndASelfProbeIsNot()
    {
        Write("Directory.Build.props", """
            <Project><PropertyGroup>
              <UseLocalSiblings Condition="Exists('..\DIR.Lib\a.csproj') AND Exists('..\Missing\b.csproj') AND Exists('..\tianwen\c.csproj') AND Exists('..\Plain\d.csproj')">true</UseLocalSiblings>
            </PropertyGroup></Project>
            """);
        Clone("DIR.Lib");
        Directory.CreateDirectory(Path.Combine(_org, "Plain"));
        var git = new CannedGit(behind: 2);

        var behind = await new SiblingWatch(git, TimeProvider.System).BehindAsync(Repo, Repo, Ct);

        var sibling = behind.ShouldHaveSingleItem();
        (sibling.Name, sibling.Path, sibling.Behind).ShouldBe(("DIR.Lib", Path.Combine(_org, "DIR.Lib"), 2));
        git.Calls.Select(c => c.Cwd).Distinct().ShouldBe([Path.Combine(_org, "DIR.Lib")]);
    }

    [Fact]
    public async Task ASiblingIsFetchedQuietlyBeforeItsCommitsAreCountedAndAnUpToDateOneIsLeftOut()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup><UseLocalA Condition=\"Exists('..\\DIR.Lib\\a.csproj')\">true</UseLocalA></PropertyGroup></Project>");
        Clone("DIR.Lib");
        var git = new CannedGit(behind: 0);

        (await new SiblingWatch(git, TimeProvider.System).BehindAsync(Repo, Repo, Ct)).ShouldBeEmpty();

        git.Calls.Select(c => string.Join(' ', c.Args)).ShouldBe(["fetch --quiet", "rev-list --count HEAD..@{upstream}"]);
    }

    [Fact]
    public async Task ASiblingAskedWithinTheFreshWindowIsNotFetchedAgainButIsAfterIt()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup><UseLocalA Condition=\"Exists('..\\DIR.Lib\\a.csproj')\">true</UseLocalA></PropertyGroup></Project>");
        Clone("DIR.Lib");
        var git = new CannedGit(behind: 1);
        var clock = new Clock();
        var watch = new SiblingWatch(git, clock);

        await watch.BehindAsync(Repo, Repo, Ct);
        clock.Now += TimeSpan.FromMinutes(1);
        (await watch.BehindAsync(Repo, Repo, Ct)).ShouldHaveSingleItem().Behind.ShouldBe(1);
        git.Calls.Count.ShouldBe(2);

        clock.Now += TimeSpan.FromMinutes(10);
        await watch.BehindAsync(Repo, Repo, Ct);
        git.Calls.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ASiblingWithNoUpstreamOrWhoseFetchHangsIsCountedOnWhatThereIs()
    {
        Write("Directory.Build.props", "<Project><PropertyGroup><UseLocalA Condition=\"Exists('..\\DIR.Lib\\a.csproj')\">true</UseLocalA></PropertyGroup></Project>");
        Clone("DIR.Lib");

        var none = new CannedGit(behind: 0) { RevListFails = true };
        (await new SiblingWatch(none, TimeProvider.System).BehindAsync(Repo, Repo, Ct)).ShouldBeEmpty();

        var hung = new CannedGit(behind: 4) { FetchHangs = true };
        var behind = await new SiblingWatch(hung, TimeProvider.System) { FetchTimeout = TimeSpan.FromMilliseconds(50) }.BehindAsync(Repo, Repo, Ct);
        behind.ShouldHaveSingleItem().Behind.ShouldBe(4);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CannedGit(int behind) : IGit
    {
        public List<(string[] Args, string Cwd)> Calls { get; } = [];

        public bool RevListFails { get; init; }

        public bool FetchHangs { get; init; }

        public async Task<GitRun> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken cancellationToken)
        {
            Calls.Add(([.. args], cwd));
            if (args[0] == "fetch")
            {
                if (FetchHangs)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return new GitRun(0, "", "");
            }
            return RevListFails ? new GitRun(128, "", "fatal: no upstream configured for branch 'main'") : new GitRun(0, $"{behind}\n", "");
        }
    }
}
