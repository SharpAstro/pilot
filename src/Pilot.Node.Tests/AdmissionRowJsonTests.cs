using System.Text.Json;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// An admission row written by an older node, without the arrays added since, reads back with them empty and serializes again: a
/// node that updated over such a row (its snapshot and journal) stopped at start on WaitingOnPerson being null (2026-11-07).
/// </summary>
public sealed class AdmissionRowJsonTests
{
    [Fact]
    public void ARowWithoutTheNewerArraysReadsThemEmptyAndWritesAgain()
    {
        var row = JsonSerializer.Deserialize("""{"opusCount":4,"opusCap":3,"oneFlightPerRepo":false}""", PilotJsonContext.Default.AdmissionRow)
            .ShouldNotBeNull();

        row.Counted.ShouldBeEmpty();
        row.Priority.ShouldBeEmpty();
        row.WaitingOnPerson.ShouldBeEmpty();
        JsonSerializer.Serialize(row, PilotJsonContext.Default.AdmissionRow).ShouldContain("\"waitingOnPerson\":[]");
    }
}
