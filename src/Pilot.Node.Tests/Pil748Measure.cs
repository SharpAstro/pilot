using System.Reflection;
using Xunit;
using Xunit.v3;
[assembly: Pilot.Node.Tests.Pil748Attr]
namespace Pilot.Node.Tests;
public static class Pil748Measure
{
    public static int Helpers;
    public static void Helper() => Interlocked.Increment(ref Helpers);
}
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class Pil748Attr : BeforeAfterTestAttribute
{
    static long _t; static int _g, _h;
    public override void Before(MethodInfo m, IXunitTest t) { _t = System.Diagnostics.Stopwatch.GetTimestamp(); _g = Pilot.Node.NodeTraffic.Shared.GitRunsSinceStart; _h = Pil748Measure.Helpers; }
    public override void After(MethodInfo m, IXunitTest t)
    {
        var n = m.DeclaringType!.Name;
        if (!(n.Contains("Overlap") || n.Contains("MergedUnderLeg") || n.Contains("PrReviewTests"))) return;
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(_t).TotalMilliseconds;
        File.AppendAllText(@"C:\Users\SebastianGodelet\.claude\jobs\64f68378\tmp\measure.tsv", $"{n}\t{m.Name}\t{ms:0}\t{Pilot.Node.NodeTraffic.Shared.GitRunsSinceStart - _g}\t{Pil748Measure.Helpers - _h}\n");
    }
}
