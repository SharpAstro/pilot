using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The window tells its nodes when the Pull requests page is shown and when it is left (#436); no display, only the chrome.</summary>
public sealed class PullRequestsPageSaysTests
{
    [Fact]
    public void SwitchingToAndFromThePullRequestsPageSaysPrsThenNothing()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer));
        var said = new List<string?>();
        chrome.OnPageShown = said.Add;

        // Leaving Home, where the machine is drawn, says so too (#794).
        chrome.Show(PilotPage.Flights);
        said.ShouldBe([null]);

        chrome.Show(PilotPage.PullRequests);
        said.ShouldBe([null, ClientPages.PullRequests]);

        // Showing the page it already shows says nothing again.
        chrome.Show(PilotPage.PullRequests);
        said.Count.ShouldBe(2);

        chrome.Show(PilotPage.Jobs);
        said.ShouldBe([null, ClientPages.PullRequests, null]);

        // Back returns to the page through the same switch, so the node hears of it too.
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        said.ShouldBe([null, ClientPages.PullRequests, null, ClientPages.PullRequests]);

        // A PR link opens the page as well.
        chrome.Show(PilotPage.Home);
        chrome.OpenPullRequest("SharpAstro/pilot", 1);
        said.Last().ShouldBe(ClientPages.PullRequests);
    }

    [Fact]
    public void HomeSaysHomeSoTheNodeSamplesTheMachineFasterOnlyWhileItIsShown()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer));
        var said = new List<string?>();
        chrome.OnPageShown = said.Add;

        // The window opens on Home with no switch: the host says this once at start.
        chrome.SaidPage.ShouldBe(ClientPages.Home);

        chrome.Show(PilotPage.Sessions);
        chrome.Show(PilotPage.Jobs);
        said.ShouldBe([null]);
        chrome.SaidPage.ShouldBeNull();

        chrome.Show(PilotPage.Home);
        said.ShouldBe([null, ClientPages.Home]);
        chrome.Show(PilotPage.PullRequests);
        said.ShouldBe([null, ClientPages.Home, ClientPages.PullRequests]);
    }
}
