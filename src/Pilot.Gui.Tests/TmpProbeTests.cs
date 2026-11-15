using Xunit;
namespace Pilot.Gui.Tests;
public class TmpProbeTests
{
    [Fact]
    public async Task Dump()
    {
        var frame = await TerminalWireframeSweepTests.RecordCase("home", "many", 160, 44);
        var sb = new System.Text.StringBuilder();
        foreach (var t in frame.Frame.Texts) { sb.AppendLine(t.ToString()); }
        System.IO.File.WriteAllText(@"C:\Users\SebastianGodelet\.claude\jobs\9c7bbce9\tmp\texts.txt", sb.ToString());
    }
}
