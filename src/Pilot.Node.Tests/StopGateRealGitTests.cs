using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The stop gate names the files, not a folder. Real git folds an untracked folder into one <c>?? dir/</c> line unless asked
/// for <c>-uall</c>, which the in-memory model never does, so this one runs on git itself.
/// </summary>
[Trait("Category", "Slow")] // Real git: the in-memory model lists every untracked file already and cannot show the folding.
public sealed class StopGateRealGitTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SeededRepos _repos;

    public StopGateRealGitTests() => _repos = new SeededRepos(_root, realGit: true);

    public void Dispose() => _repos.Dispose();

    [Fact]
    public async Task AnUntrackedFolderIsListedAsItsFilesNotAsOneFolder()
    {
        var repo = _repos.Seed("tianwen");
        Directory.CreateDirectory(Path.Combine(repo, "src", "X.Tests"));
        File.WriteAllText(Path.Combine(repo, "src", "X.Tests", "ScratchLookTests.cs"), "//\n");

        var files = await _repos.Planes().UncommittedFilesAsync(repo, Ct);

        files.ShouldBe(["?? src/X.Tests/ScratchLookTests.cs"]);
    }
}
