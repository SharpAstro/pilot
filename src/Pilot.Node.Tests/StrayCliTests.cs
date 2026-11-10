using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary><c>pilot strays</c> and the strays under the Machine section of <c>pilot status</c> (pilot #909): what the node's sweep named, as the machine row carries it.</summary>
public sealed class StrayCliTests
{
    private static MachineStray Stray(int pid, string name = "testhost.exe", string owner = "job j7", long age = 3 * 3600, double? cpu = 3 * 3600) => new()
    {
        Pid = pid, Name = name, CommandLine = @"testhost.exe --port 5000 C:\repos\pilot\Pilot.Node.Tests.dll", StartedAt = DateTimeOffset.UnixEpoch, AgeSeconds = age, CpuSeconds = cpu,
        Owner = owner, Reason = "job j7 has run 3 h 0 min, over its time limit of 10 min", KillCommand = $"Stop-Process -Id {pid} -Force", NodeLaunched = true,
    };

    private static SnapshotEvent SnapshotWith(MachineRow? machine) => new()
    {
        Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [], Machine = machine,
    };

    private static MachineRow Machine(params MachineStray[] strays) => new()
    {
        SampledAt = DateTimeOffset.UnixEpoch, CpuPercent = 7, Cores = 8, MemoryUsed = 1L << 30, MemoryTotal = 16L << 30, Strays = strays,
    };

    [Fact]
    public void StraysListsEachProcessWithItsFactsAndTheKillCommand()
    {
        var text = PilotCommand.Strays(Machine(Stray(501), Stray(502, "pwsh.exe", "session se1-old", 90, null)));

        text.ShouldContain("2 strays in the node's tree:");
        text.ShouldContain("testhost.exe  pid 501  job j7  age 3 h 0 min  cpu 3 h 0 min");
        text.ShouldContain("why: job j7 has run 3 h 0 min, over its time limit of 10 min");
        text.ShouldContain(@"command line: testhost.exe --port 5000 C:\repos\pilot\Pilot.Node.Tests.dll");
        text.ShouldContain("end it: Stop-Process -Id 501 -Force");
        text.ShouldContain("pwsh.exe  pid 502  session se1-old  age 1 min");
        text.ShouldNotContain("pid 502  session se1-old  age 1 min  cpu");
    }

    [Fact]
    public void StraysSaysSoWhenThereAreNoneAndWhenTheNodeHasNotLookedYet()
    {
        PilotCommand.Strays(Machine()).ShouldContain("No strays");
        PilotCommand.Strays(null).ShouldContain("has not sampled the machine yet");
    }

    [Fact]
    public void StatusNamesTheStraysUnderItsMachineLineAndMarkdownAsAFact()
    {
        var snapshot = SnapshotWith(Machine(Stray(501)));

        var plain = PilotCommand.Status(snapshot);
        plain.ShouldContain("machine  cpu 7% · mem 1/16 GB");
        plain.ShouldContain("strays  1 in the node's tree (pilot strays has the kill commands)");
        plain.ShouldContain("    testhost.exe 501 (job j7, 3 h 0 min, cpu 3 h 0 min): job j7 has run 3 h 0 min, over its time limit of 10 min");
        plain.IndexOf("machine  ", StringComparison.Ordinal).ShouldBeLessThan(plain.IndexOf("strays  ", StringComparison.Ordinal));
        StatusMarkdown.Build(snapshot).ShouldContain("- **strays** 1 in the node's tree: testhost.exe 501 (job j7, 3 h 0 min, cpu 3 h 0 min)");
    }

    [Fact]
    public void StatusSaysNothingOfStraysWhenThereAreNone()
    {
        PilotCommand.Status(SnapshotWith(Machine())).ShouldNotContain("strays");
        StatusMarkdown.Build(SnapshotWith(Machine())).ShouldNotContain("strays");
    }

    [Fact]
    public void StraysAsMarkdownGivesEachAHeadingAndTheCommandInCode()
    {
        var text = PilotCommand.StraysMarkdown(Machine(Stray(501)));

        text.ShouldContain("# pilot strays");
        text.ShouldContain("## testhost.exe 501");
        text.ShouldContain("- **end it** `Stop-Process -Id 501 -Force`");
    }

    [Fact]
    public void TheStraysRideOnTheMachineRowOverTheWireAndARowWithoutThemReadsNone()
    {
        var row = Machine(Stray(501));

        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(row, PilotJsonContext.Default.MachineRow), PilotJsonContext.Default.MachineRow)!;

        back.Strays.ShouldHaveSingleItem().ShouldBe(row.Strays[0], "the record compares by value");
        // An older node's row has no field at all, and the optional field keeps NodeWire.Version where it was.
        JsonSerializer.Deserialize("""{"sampledAt":"1970-01-01T00:00:00+00:00","cpuPercent":1,"cores":2,"memoryUsed":3,"memoryTotal":4}""", PilotJsonContext.Default.MachineRow)!.Strays.ShouldBeEmpty();
    }
}
