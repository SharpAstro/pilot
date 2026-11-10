using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The build cache (docs/build-cache.md, PIL860) is an opt-in: Microsoft.MSBuildCache.* is pinned and referenced only when a build is
/// given <c>-p:MSBuildCacheEnabled=true</c>, so a plain build, a restore and CI never see the package. A reference that lost its
/// condition would put the plugin, which needs Visual Studio's x64 MSBuild, into every build.
/// </summary>
[Trait("Category", InvariantTraitTests.Invariant)]
public sealed class BuildCacheOptInTests
{
    private const string Package = "Microsoft.MSBuildCache";

    private const string Local = "Microsoft.MSBuildCache.Local";

    private const string OptIn = "'$(MSBuildCacheEnabled)'=='true'";

    private static readonly Regex OrWord = new(@"\bor\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] MsBuildExtensions = [".props", ".targets", ".csproj"];

    private static string Src()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }
        throw new InvalidOperationException("no folder holding src/Pilot.slnx above the test's directory");
    }

    /// <summary>Every item that names a Microsoft.MSBuildCache package: a PackageVersion, PackageReference, GlobalPackageReference.</summary>
    private static List<XElement> CacheItems(XDocument doc) =>
        [.. doc.Descendants().Where(e => e.Attributes().Any(a =>
            a.Name.LocalName is "Include" or "Update" && a.Value.StartsWith(Package, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>A condition counts when it requires MSBuildCacheEnabled to be 'true' and cannot be satisfied another way (no Or).</summary>
    private static bool Requires(string condition) =>
        condition.Replace(" ", "", StringComparison.Ordinal).Contains(OptIn, StringComparison.Ordinal) && !OrWord.IsMatch(condition);

    /// <summary>The item's own Condition or that of any ItemGroup or Project around it must require the opt-in.</summary>
    private static bool IsGuarded(XElement item) =>
        item.AncestorsAndSelf().Select(e => e.Attribute("Condition")?.Value).OfType<string>().Any(Requires);

    private static List<XElement> Unguarded(XDocument doc) => [.. CacheItems(doc).Where(e => !IsGuarded(e))];

    private static string Describe(XElement e) => e.ToString(SaveOptions.DisableFormatting);

    [Fact]
    public void ThePinsInDirectoryPackagesPropsExistAndOnlyApplyWhenTheCacheIsEnabled()
    {
        var doc = XDocument.Load(Path.Combine(Src(), "Directory.Packages.props"));
        var items = CacheItems(doc);

        items.Where(e => e.Name.LocalName == "PackageVersion" && e.Attribute("Include")?.Value == Local)
            .ShouldNotBeEmpty($"src/Directory.Packages.props no longer pins {Local}");
        items.Where(e => !IsGuarded(e)).Select(Describe)
            .ShouldBeEmpty($"a {Package}.* pin must sit under Condition=\"'$(MSBuildCacheEnabled)' == 'true'\", or every restore sees it");
    }

    [Fact]
    public void TheReferenceInDirectoryBuildPropsExistsAndOnlyAppliesWhenTheCacheIsEnabled()
    {
        var doc = XDocument.Load(Path.Combine(Src(), "Directory.Build.props"));
        var items = CacheItems(doc);

        items.Where(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value == Local)
            .ShouldNotBeEmpty($"src/Directory.Build.props no longer references {Local}");
        items.Where(e => !IsGuarded(e)).Select(Describe)
            .ShouldBeEmpty($"a {Package}.* reference must sit under Condition=\"'$(MSBuildCacheEnabled)' == 'true'\", or every build sees it");
    }

    [Fact]
    public void NoOtherProjectOrPropsFileReferencesTheCacheWithoutTheCondition()
    {
        var unguarded = Directory.EnumerateFiles(Src(), "*", SearchOption.AllDirectories)
            .Where(p => MsBuildExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "obj" or "bin"))
            .SelectMany(p => Unguarded(XDocument.Load(p)).Select(e => Path.GetRelativePath(Src(), p) + ": " + Describe(e)))
            .ToList();

        unguarded.ShouldBeEmpty($"these name a {Package}.* package outside the MSBuildCacheEnabled opt-in");
    }

    [Fact]
    public void TheCachePackagesRideOneExactVersion()
    {
        var versions = CacheItems(XDocument.Load(Path.Combine(Src(), "Directory.Packages.props")))
            .Select(e => e.Attribute("Version")?.Value)
            .ToList();

        versions.ShouldNotBeEmpty();
        versions.ShouldAllBe(v => !string.IsNullOrEmpty(v) && !v.Contains('*'), "a cache package is pinned to one exact version, never floating");
        versions.Distinct().Count().ShouldBe(1, "Local and SharedCompilation must ride the same version");
    }

    [Theory]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Microsoft.MSBuildCache.Local\" /></ItemGroup></Project>", 1)]
    [InlineData("<Project><ItemGroup Condition=\"'$(MSBuildCacheEnabled)' == 'false'\"><PackageReference Include=\"Microsoft.MSBuildCache.Local\" /></ItemGroup></Project>", 1)]
    [InlineData("<Project><ItemGroup Condition=\"'$(MSBuildCacheEnabled)' == 'true' Or '$(CI)' == 'x'\"><PackageReference Include=\"Microsoft.MSBuildCache.Local\" /></ItemGroup></Project>", 1)]
    [InlineData("<Project><ItemGroup><PackageVersion Update=\"Microsoft.MSBuildCache.Local\" Version=\"1\" Condition=\"'$(Other)' == 'true'\" /></ItemGroup></Project>", 1)]
    [InlineData("<Project><ItemGroup Condition=\"'$(MSBuildCacheEnabled)' == 'true'\"><PackageReference Include=\"Microsoft.MSBuildCache.Local\" /></ItemGroup></Project>", 0)]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Microsoft.MSBuildCache.Local\" Condition=\"'$(MSBuildCacheEnabled)'=='true' And '$(OS)' == 'Windows_NT'\" /></ItemGroup></Project>", 0)]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Shouldly\" /></ItemGroup></Project>", 0)]
    public void TheGuardTellsAGuardedReferenceFromAnUnguardedOne(string xml, int unguarded)
    {
        Unguarded(XDocument.Parse(xml)).Count.ShouldBe(unguarded);
    }
}
