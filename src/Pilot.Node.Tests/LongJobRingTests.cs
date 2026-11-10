using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A running job that has held its slot past <see cref="JobRunner.LongJobWindow"/> while another waits rings once (pilot #895), with a
/// choice that cancels it. The clock is the test's; the runner is not started, so the test is its dispatcher.
/// </summary>
[Collection(JobsCollection.Name)]
public sealed class LongJobRingTests : JobTestsBase
{
    private CardRow[] LongCards(bool openOnly = true) => [.. _state.Cards(openOnly).Where(c => c.Kind == JobRunner.LongJobKind)];

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARunningJobPastTheWindowWithAJobQueuedBehindItRaisesOneRingCardWhoseChoiceCancelsIt()
    {
        var (jobs, clock, _, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        var second = Hand(jobs, Echo("later"));
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        LongCards().ShouldBeEmpty();

        clock.Advance(TimeSpan.FromMinutes(21));
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        var card = LongCards().ShouldHaveSingleItem();
        (card.Kind, card.Bin, card.Subject).ShouldBe((JobRunner.LongJobKind, CardBin.Ring, Verbs.JobTarget(first)));
        card.Title.ShouldContain(first);
        card.Title.ShouldContain("se1-tia1-parser");
        card.Title.ShouldContain("21m");
        (card.Detail ?? "").ShouldContain(Sleep(60));
        (card.Detail ?? "").ShouldContain(second);
        var choice = card.Choices.ShouldHaveSingleItem();
        (choice.Label, choice.Verb, choice.Target).ShouldBe(($"Cancel job {first}", Verbs.CancelJob, Verbs.JobTarget(first)));

        // The choice is a plain cancel-job of the job: it ends it.
        jobs.Cancel(new CommandRequest { Verb = choice.Verb, Target = choice.Target, Who = "controller" }).Ok.ShouldBeTrue();
        await Until(first, j => j.EndedAt is not null);
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        LongCards().ShouldBeEmpty();
        LongCards(openOnly: false).ShouldHaveSingleItem();
        await gone(second);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobPastTheWindowWithNothingQueuedBehindItRaisesNoCard()
    {
        var (jobs, clock, _, gone) = Idle();
        var first = Hand(jobs, Sleep(60));

        clock.Advance(TimeSpan.FromMinutes(21));
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        LongCards(openOnly: false).ShouldBeEmpty();
        await gone(first);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACardIsRaisedOncePerJobAndClosesWhenTheQueueEmpties()
    {
        var (jobs, clock, _, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        var second = Hand(jobs, Echo("later"));
        clock.Advance(TimeSpan.FromMinutes(21));
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        LongCards().ShouldHaveSingleItem();

        // The waiting job leaves; nothing waits any more, so the card closes, and a new waiter does not raise it again.
        jobs.Cancel(new CommandRequest { Verb = Verbs.CancelJob, Target = Verbs.JobTarget(second), Who = "controller" });
        jobs.Dispatch(new JobRunner.Withdrawn(second, "controller"));
        LongCards().ShouldBeEmpty();
        Hand(jobs, Echo("again"));
        jobs.Dispatch(JobRunner.LookAgain.Instance);
        LongCards().ShouldBeEmpty();
        LongCards(openOnly: false).ShouldHaveSingleItem();

        await gone(first);
        await jobs.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AJobUnderTheWindowRaisesNoCard()
    {
        var (jobs, clock, _, gone) = Idle();
        var first = Hand(jobs, Sleep(60));
        Hand(jobs, Echo("later"));

        clock.Advance(TimeSpan.FromMinutes(19));
        jobs.Dispatch(JobRunner.LookAgain.Instance);

        LongCards(openOnly: false).ShouldBeEmpty();
        await gone(first);
        await jobs.StopAsync(CancellationToken.None);
    }
}
