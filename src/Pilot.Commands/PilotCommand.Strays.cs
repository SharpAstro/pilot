using System.CommandLine;
using System.Globalization;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>The lines <c>pilot status</c> carries under its machine line when the node named strays: how many, then each stray as one line.</summary>
    internal static IEnumerable<string> StrayLines(MachineRow? machine)
    {
        if (machine?.Strays is not { Length: > 0 } strays)
        {
            yield break;
        }
        yield return $"strays  {strays.Length} in the node's tree (pilot strays has the kill commands)";
        foreach (var stray in strays)
        {
            yield return $"    {stray.Line()}";
        }
    }

    /// <summary>
    /// <c>pilot strays</c>: each process of the node's own tree the sweep named, with its pid, name, command line, age, CPU, owner, why and the
    /// exact command that ends it. A node that has named none says so.
    /// </summary>
    internal static string Strays(MachineRow? machine)
    {
        if (machine is null)
        {
            return "The node has not sampled the machine yet, so it has named no strays; ask again in a minute.\n";
        }
        if (machine.Strays.Length == 0)
        {
            return "No strays: nothing in the node's tree runs that should not.\n";
        }
        var text = new StringBuilder();
        text.AppendLine($"{machine.Strays.Length} {(machine.Strays.Length == 1 ? "stray" : "strays")} in the node's tree:");
        foreach (var stray in machine.Strays)
        {
            text.AppendLine($"{stray.Name}  pid {stray.Pid.ToString(CultureInfo.InvariantCulture)}  {stray.Owner}  age {MachineStray.Age(stray.AgeSeconds)}{(stray.CpuSeconds is { } cpu ? $"  cpu {MachineStray.Age((long)cpu)}" : "")}");
            text.AppendLine($"    why: {stray.Reason}");
            text.AppendLine($"    command line: {stray.CommandLine ?? "not readable"}");
            text.AppendLine($"    end it: {stray.KillCommand}{(stray.NodeLaunched ? "" : "  (the node did not start its owner)")}");
        }
        return text.ToString();
    }

    /// <summary><c>pilot strays</c> as Markdown: each stray a heading with its facts as a list.</summary>
    internal static string StraysMarkdown(MachineRow? machine)
    {
        if (machine is not { Strays.Length: > 0 })
        {
            return Strays(machine);
        }
        var text = new StringBuilder();
        text.AppendLine("# pilot strays").AppendLine();
        foreach (var stray in machine.Strays)
        {
            text.AppendLine($"## {Markdown.Text(stray.Name)} {stray.Pid.ToString(CultureInfo.InvariantCulture)}").AppendLine();
            text.AppendLine($"- **owner** {Markdown.Text(stray.Owner)}");
            text.AppendLine($"- **age** {MachineStray.Age(stray.AgeSeconds)}{(stray.CpuSeconds is { } cpu ? $", cpu {MachineStray.Age((long)cpu)}" : "")}");
            text.AppendLine($"- **why** {Markdown.Text(stray.Reason)}");
            text.AppendLine($"- **command line** {Markdown.Text(stray.CommandLine ?? "not readable")}");
            text.AppendLine($"- **end it** `{stray.KillCommand}`").AppendLine();
        }
        return text.ToString();
    }

    private sealed partial class BuildContext
    {
        /// <summary>Makes strays: the processes of the node's own tree its sweep named, as the machine row carries them.</summary>
        internal Command StraysCommand() =>
            Action(new Command("strays", "The processes of the node's own tree that should not be running (a job's or session's leftovers), each with why and the command that ends it"),
                async (p, client, ct) =>
                {
                    var snapshot = await SnapshotAsync(client, ct);
                    await WriteAsync(p.GetValue(outputFormat), () => Strays(snapshot.Machine), () => StraysMarkdown(snapshot.Machine));
                    return 0;
                });
    }
}
