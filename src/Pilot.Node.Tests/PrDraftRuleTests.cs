using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Draft follows the verdict (PIL483): a PR is ready from when it is opened for review or a review of it starts, and only a review
/// asking for changes or failed checks turns it into a draft. The pilot: label, the pilot/review status and the draft state agree
/// after every step, each read back from the fake GitHub.
/// </summary>
public sealed class PrDraftRuleTests : ReviewTestsBase
{
    private void Agree(bool draft, string label, string status)
    {
        var pr = _github.Pr.ShouldNotBeNull();
        pr.Draft.ShouldBe(draft);
        PilotLabels(_github.Labels[pr.Number]).ShouldBe([label]);
        _github.Statuses.Last().State.ShouldBe(status);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightsPrIsReadyInReviewAndADraftOnlyOnChangesOrFailedChecks()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        var target = Verbs.FlightTarget(flight.Code);

        (await Run(Verbs.OpenPr, target, "controller")).Error.ShouldBeNull();
        Agree(draft: false, "pilot: reviewing", "pending");

        (await Run(Verbs.Review, target, Reviewer(flight.Code), "changes", "fix the null check")).Error.ShouldBeNull();
        Agree(draft: true, "pilot: changes", "failure");

        (await Run(Verbs.StartReview, target, "controller")).Error.ShouldBeNull();
        await Started(_legs, flight.Code);
        // The marks follow the recorded reviewer, outside the lock: a moment on a loaded machine.
        for (var i = 0; i < 2000 && _github.Pr!.Draft; i++)
        {
            await Task.Delay(10, Ct);
        }
        Agree(draft: false, "pilot: reviewing", "pending");

        (await Run(Verbs.Review, target, Reviewer(flight.Code), "pass", "fixed")).Error.ShouldBeNull();
        Agree(draft: false, "pilot: passed", "success");

        (await _legs.ChecksFailedAsync(flight.Code, _github.Pr!.Head, Ct)).ShouldBeNull();
        Agree(draft: true, "pilot: changes", "failure");
    }
}

/// <summary>The same rule for a PR outside a flight, reviewed by <c>review-pr</c>.</summary>
public sealed class PrDraftRuleOutsideTests : PrReviewTestsBase
{
    private void Agree(bool draft, string? label, string status)
    {
        _github.Prs["acme/widget#7"].Draft.ShouldBe(draft);
        if (label is not null)
        {
            _github.Labels[7].Where(l => l.StartsWith("pilot: ", StringComparison.Ordinal)).ShouldBe([label]);
        }
        _github.Statuses.Last().State.ShouldBe(status);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APrOutsideAFlightIsReadyInReviewAndADraftOnlyOnChanges()
    {
        _github.Login = "ada";
        _github.Prs["acme/widget#7"] = _github.Prs["acme/widget#7"] with { Draft = true };

        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        Agree(draft: false, null, "pending");

        var reviewer = Row("acme/widget", 7).Reviewer;
        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", reviewer, "changes", "- fix it")).Error.ShouldBeNull();
        Agree(draft: true, "pilot: changes", "failure");

        (await Run(Verbs.ReviewPr, "pr/acme/widget#7", "controller")).Error.ShouldBeNull();
        Agree(draft: false, "pilot: changes", "pending");

        (await Run(Verbs.PrVerdict, "pr/acme/widget#7", Row("acme/widget", 7).Reviewer, "approve", "fixed")).Error.ShouldBeNull();
        Agree(draft: false, "pilot: passed", "success");
    }
}
