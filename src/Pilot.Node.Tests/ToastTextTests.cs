using Pilot.Contracts;
using Pilot.Node.Toasts;
using SharpAstro.AppShell;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>What pilot owns of a toast (#1384): the burst and test words, the tags, the click payloads and the tier to urgency mapping. No Windows call.</summary>
public sealed class ToastTextTests
{
    [Fact]
    public void TheBurstToastSaysHowManyCardsNeedYouAndOpensTheCardsPage()
    {
        var burst = ToastText.Burst(4);
        burst.Title.ShouldBe("4 cards need you");
        burst.Body.ShouldBeNull();
        burst.Tag.ShouldBe("pilot-burst");
        burst.Tier.ShouldBe(ToastTier.Normal);
        burst.Payload.ShouldBe(ToastLink.CardsPayload);
    }

    [Fact]
    public void TheTestToastHasItsWordsItsTagAndOpensTheCardsPage()
    {
        var test = ToastText.Test();
        test.Title.ShouldBe("pilot test toast");
        test.Body.ShouldBe("Desktop toasts work on this machine.");
        test.Tag.ShouldBe("pilot-test");
        test.Payload.ShouldBe(ToastLink.CardsPayload);
    }

    [Fact]
    public void ACardToastIsTaggedByItsSubjectAndOpensThatCard()
    {
        var request = ToastText.ForCard("flight/A", "card 7/x", "title", "body", ToastTier.Urgent);
        request.Tag.ShouldBe("flight/A");
        request.Title.ShouldBe("title");
        request.Body.ShouldBe("body");
        request.Tier.ShouldBe(ToastTier.Urgent);
        request.Payload.ShouldBe(ToastLink.CardPayload("card 7/x"));
        ToastLink.TryReadPayload(request.Payload, out var id).ShouldBeTrue();
        id.ShouldBe("card 7/x");
    }

    [Fact]
    public void OnlyTheUrgentTierAsksForAnUrgentToast()
    {
        AppShellToastRunner.UrgencyOf(ToastTier.Urgent).ShouldBe(ToastUrgency.Urgent);
        AppShellToastRunner.UrgencyOf(ToastTier.Normal).ShouldBe(ToastUrgency.Normal);
    }

    [Fact]
    public void ANormalToastIsNeitherAReminderNorUrgentSoItHasNoDismissButton()
    {
        // AppShell gives only the reminder and urgent scenarios the system's Dismiss button; pilot asks for neither on a normal card.
        var options = AppShellToastRunner.OptionsOf(ToastText.ForCard("flight/A", "c1", "t", "b", ToastTier.Normal));
        options.Urgency.ShouldBe(ToastUrgency.Normal);
        options.Urgency.ShouldNotBe(ToastUrgency.Reminder);
        AppShellToastRunner.OptionsOf(ToastText.Burst(4)).Urgency.ShouldBe(ToastUrgency.Normal);
    }

    [Fact]
    public void DescribeWordsAFailureAsStatusColonReasonAndASuccessAsNull()
    {
        AppShellToastRunner.Describe(DesktopNotificationStatus.Succeeded, null).ShouldBeNull();
        AppShellToastRunner.Describe(DesktopNotificationStatus.Disabled, "turned off for pilot").ShouldBe("Disabled: turned off for pilot");
        AppShellToastRunner.Describe(DesktopNotificationStatus.Unsupported, "not Windows").ShouldBe("Unsupported: not Windows");
    }

    [Fact]
    public void TheOptionsCarryTheGroupAppIdSchemeAndPayload()
    {
        var options = AppShellToastRunner.OptionsOf(ToastText.ForCard("flight/A", "c1", "t", "b", ToastTier.Urgent));
        options.Tag.ShouldBe("flight/A");
        options.Group.ShouldBe("pilot");
        options.AppId.ShouldBe("SharpAstro.Pilot");
        options.Scheme.ShouldBe("pilot");
        options.LaunchPayload.ShouldBe("card/c1");
        options.Urgency.ShouldBe(ToastUrgency.Urgent);
    }
}
