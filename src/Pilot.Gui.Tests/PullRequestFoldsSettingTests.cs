using Console.Lib;
using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>What is folded on the Pull requests page is kept in gui-settings.json, a fold of an org or repo not listed included.</summary>
public sealed class PullRequestFoldsSettingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static PullRequestRow Pr(string repo, int number) => new()
    {
        Repo = repo, Number = number, Title = "t", Url = $"https://github.com/{repo}/pull/{number}", Author = "a", Kind = PullRequestKind.Ready, Label = "Ready",
        UpdatedAt = Now, CreatedAt = Now, Head = "feature/x", Base = "main",
    };

    private static PullRequestsScreen Screen(params string[] repos) => new(
        new PullRequestsRow { Orgs = [.. repos.Select(r => r.Split('/')[0]).Distinct()], ReadAt = Now, Viewer = "sebgod", PullRequests = [.. repos.Select((r, i) => Pr(r, i + 1))] },
        "live", Now);

    private static PilotChrome<RgbaImage> Build(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    private static void Paint(PilotChrome<RgbaImage> chrome, PullRequestsScreen screen) =>
        chrome.Render(new SessionsScreen([], "live", Now), null, null, null, null, null, screen, new RectF32(0, 0, 1280, 800));

    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory("pilot-folds-").FullName, "gui-settings.json");

    [Fact]
    public void FoldsSurviveAWriteAndAReopenAndAnAbsentRepoOrOrgFoldIsKeptAndAppliedWhenItComesBack()
    {
        var path = TempFile();
        try
        {
            using var renderer = new RgbaImageRenderer(1280, 800);
            var all = Screen("SharpAstro/pilot", "SharpAstro/DIR.Lib", "sebgod/notes");
            using (var file = GuiSettingsFile.Open(path))
            {
                var chrome = Build(renderer);
                chrome.KeepColumnWidths(file, () => { });
                chrome.Show(PilotPage.PullRequests);
                Paint(chrome, all);
                chrome.PullRequests.Toggle("SharpAstro/DIR.Lib");
                chrome.PullRequests.ToggleOrg("sebgod");
                Paint(chrome, all);
                file.Flush();
            }
            File.ReadAllText(path).ShouldContain("\"repo:SharpAstro/DIR.Lib\"");

            using (var file = GuiSettingsFile.Open(path))
            {
                file.PullRequestFolds.ShouldBe(["org:sebgod", "repo:SharpAstro/DIR.Lib"]);
                var chrome = Build(renderer);
                chrome.KeepColumnWidths(file, () => { });
                chrome.Show(PilotPage.PullRequests);
                // Neither the folded repo nor the folded org is listed at first: their folds are kept anyway.
                Paint(chrome, Screen("SharpAstro/pilot"));
                chrome.PullRequests.Folds.ShouldBe(["org:sebgod", "repo:SharpAstro/DIR.Lib"]);
                // They come back folded.
                Paint(chrome, all);
                chrome.PullRequests.IsFolded("SharpAstro/DIR.Lib").ShouldBeTrue();
                chrome.PullRequests.IsOrgFolded("sebgod").ShouldBeTrue();
                chrome.PullRequests.IsFolded("SharpAstro/pilot").ShouldBeFalse();
                chrome.PullRequests.VisiblePullRequests.Select(p => p.Repo).ShouldBe(["SharpAstro/pilot"]);
                // Unfolding one is a change that is written; the fold of the other stays.
                chrome.PullRequests.ToggleOrg("sebgod");
                Paint(chrome, all);
                file.Flush();
            }
            GuiSettingsFile.Open(path).PullRequestFolds.ShouldBe(["repo:SharpAstro/DIR.Lib"]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ABadFileLeavesNoFolds()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ \"pullRequestFolds\": [\"org:x\"], \"tables\": 7 }");
            GuiSettingsFile.Open(path).PullRequestFolds.ShouldBeEmpty();
            File.WriteAllText(path, "not json");
            GuiSettingsFile.Open(path).PullRequestFolds.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
