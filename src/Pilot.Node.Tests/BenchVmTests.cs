using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A bench row's record of where it ran (<see cref="JobRow.BenchVm"/>) and its <see cref="JobRow.Local"/> flag (#823) cross the wire and a
/// stored journal, and a row written before either existed reads as a job that is not local and has no VM: both are optional, so
/// <see cref="NodeWire.Version"/> stays.
/// </summary>
public sealed class BenchVmTests
{
    private static JobRow Row() => new()
    {
        Id = "j7",
        Command = "pwsh bench.ps1",
        Cwd = "/repos/codecs",
        Kind = JobKind.Bench,
        State = JobState.Succeeded,
        LogPath = "/data/jobs/j7.log",
        Timeout = TimeSpan.FromHours(2),
        StartedAt = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void ABenchRowKeepsItsVmAndItsLocalFlagThroughTheWire()
    {
        var onVm = Row() with
        {
            BenchVm = new BenchVm { Size = "Standard_F8s_v2", Image = "Canonical:ubuntu-24_04-lts:server:latest", Region = "westeurope", PricePerHour = 0.3, PriceIsUpperBound = true, Minutes = 41.5, Cost = 0.2075, Evictions = 1 },
        };
        var local = Row() with { Local = true, BenchVm = new BenchVm { LocalBecause = "asked for with --local" } };

        foreach (var row in new[] { onVm, local })
        {
            var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(row, PilotJsonContext.Default.JobRow), PilotJsonContext.Default.JobRow);

            back.ShouldBe(row);
        }
    }

    [Fact]
    public void ARowFromBeforeTheVmFieldsReadsAsNotLocalWithNoVm()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Row() with { Local = true, BenchVm = new BenchVm { Size = "Standard_F8s_v2" } }, PilotJsonContext.Default.JobRow))!.AsObject();
        node.Remove("local").ShouldBeTrue();
        node.Remove("benchVm").ShouldBeTrue();

        var old = JsonSerializer.Deserialize(node.ToJsonString(), PilotJsonContext.Default.JobRow).ShouldNotBeNull();

        (old.Local, old.BenchVm).ShouldBe((false, null));
        old.ShouldBe(Row());
    }

    [Fact]
    public void AVmFromBeforeThePriceBoundFieldReadsAsAPriceThatWasRead()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Row() with { BenchVm = new BenchVm { Size = "Standard_F8s_v2", PricePerHour = 0.07, PriceIsUpperBound = true } }, PilotJsonContext.Default.JobRow))!.AsObject();
        node["benchVm"]!.AsObject().Remove("priceIsUpperBound").ShouldBeTrue();

        var old = JsonSerializer.Deserialize(node.ToJsonString(), PilotJsonContext.Default.JobRow).ShouldNotBeNull();

        old.BenchVm.ShouldNotBeNull().PriceIsUpperBound.ShouldBeFalse();
    }

    [Fact]
    public void ARowFromANewerNodeWithFieldsThisOneDoesNotKnowStillReads()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Row(), PilotJsonContext.Default.JobRow))!.AsObject();
        node["benchVm"] = new JsonObject { ["size"] = "Standard_F8s_v2", ["spotNote"] = "a field of a later node" };

        var read = JsonSerializer.Deserialize(node.ToJsonString(), PilotJsonContext.Default.JobRow).ShouldNotBeNull();

        read.BenchVm.ShouldNotBeNull().Size.ShouldBe("Standard_F8s_v2");
    }
}
