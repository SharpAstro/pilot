using Pilot.Node.Toasts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The toast tier of every Ring card kind (#1356): a new Ring kind cannot ship without deciding whether it toasts urgently.</summary>
public sealed class ToastTierTests
{
    private static readonly string[] UrgentKinds =
    [
        "busy-process", "poller-caller", "over-budget", "stop-unconfirmed", "job-bench-busy",
        "stopped-uncommitted", "closed-with-work", "push-blocked", "move-failed",
        "watchtower-down", "watchtower-stopped", "store", "worker-missing",
    ];

    [Fact]
    [Trait("Category", "Invariant")]
    public void EveryRingKindOfTheCatalogHasATier()
    {
        var ring = CardKindCatalog.All.Where(e => e.Bins.Split('|').Contains("Ring")).Select(e => e.Kind).ToArray();
        ring.ShouldNotBeEmpty();
        ring.Where(kind => ToastTier.Of(kind) is null).ShouldBeEmpty();
    }

    [Fact]
    public void EachUrgentKindOfTheIssueIsUrgent()
    {
        foreach (var kind in UrgentKinds) ToastTier.Of(kind).ShouldBeSameAs(ToastTier.Urgent, kind);
    }

    [Fact]
    public void BusyProcessIsUrgentBeforeItsCardKindLands()
    {
        CardKindCatalog.All.Any(e => e.Kind == "busy-process").ShouldBeFalse("this test is for the time before #1354 lands; drop it when it does");
        ToastTier.Of("busy-process").ShouldBeSameAs(ToastTier.Urgent);
    }

    [Fact]
    public void EveryOtherRingKindIsNormal()
    {
        var others = CardKindCatalog.All.Where(e => e.Bins.Split('|').Contains("Ring") && !UrgentKinds.Contains(e.Kind)).Select(e => e.Kind);
        foreach (var kind in others) ToastTier.Of(kind).ShouldBeSameAs(ToastTier.Normal, kind);
        foreach (var kind in new[] { "worker-waiting", "session-waiting", "pr-ready", "review-changes", "pr-checks-failed", "overlap", "grant-asked", "conflict-decision", "leg-failed", "failure-alert", "update" })
            ToastTier.Of(kind).ShouldBeSameAs(ToastTier.Normal, kind);
    }

    [Fact]
    public void AKindNotListedHasNoTier()
    {
        ToastTier.Of("no-such-kind").ShouldBeNull();
    }
}
