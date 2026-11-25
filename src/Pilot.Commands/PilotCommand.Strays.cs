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
        yield return $"strays  {strays.Length} on this machine (pilot strays has the kill commands)";
        foreach (var stray in strays)
        {
            yield return $"    {stray.Line()}";
        }
    }

    /// <summary>
    /// <c>pilot strays</c>: each process the sweep named (a leftover of the node's own tree, or any process that computes without a break), with its pid, name, command line, age, CPU, owner, why and the
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
            return "No strays: nothing on this machine runs that should not.\n";
        }
        var text = new StringBuilder();
        text.AppendLine($"{machine.Strays.Length} {(machine.Strays.Length == 1 ? "stray" : "strays")} on this machine:");
        foreach (var stray in machine.Strays)
        {
            text.AppendLine($"{stray.Name}  pid {stray.Pid.ToString(CultureInfo.InvariantCulture)}  {stray.Owner}  age {MachineStray.Age(stray.AgeSeconds)}{(stray.CpuSeconds is { } cpu ? $"  cpu {MachineStray.Age((long)cpu)}" : "")}");
            text.AppendLine($"    why: {stray.Reason}");
            text.AppendLine($"    command line: {stray.CommandLine ?? "not readable"}");
            text.AppendLine($"    end it: pilot strays end {stray.Pid.ToString(CultureInfo.InvariantCulture)}, or by hand: {stray.KillCommand}{(stray.NodeLaunched ? "" : "  (the node did not start its owner)")}");
        }
        return text.ToString();
    }

    /// <summary>
    /// <c>pilot strays end &lt;pid&gt;</c>: the card key (pid and start, as <c>StraySweep.KeyOf</c>) of the stray the machine row names with
    /// that pid, or why there is none, naming what <c>pilot strays</c> lists. The node then ends it on the person's word.
    /// </summary>
    internal static (string? Key, string? Refusal) StrayToEnd(MachineRow? machine, int pid)
    {
        var named = machine?.Strays.FirstOrDefault(s => s.Pid == pid);
        if (named is null)
        {
            var known = machine?.Strays is { Length: > 0 } strays
                ? string.Join(", ", strays.Select(s => s.Pid.ToString(CultureInfo.InvariantCulture)))
                : "none";
            return (null, $"the sweep names no stray with pid {pid.ToString(CultureInfo.InvariantCulture)} (it names: {known}); 'pilot strays' lists them");
        }
        var start = named.StartedAt is { } at ? at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : "unknown";
        return ($"process:{pid.ToString(CultureInfo.InvariantCulture)}@{start}", null);
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
            text.AppendLine($"- **end it** `pilot strays end {stray.Pid.ToString(CultureInfo.InvariantCulture)}`, or by hand `{stray.KillCommand}`").AppendLine();
        }
        return text.ToString();
    }

    private sealed partial class BuildContext
    {
        /// <summary>Makes strays: the processes the node's sweep named (leftovers of its own tree, and busy processes on the machine), as the machine row carries them.</summary>
        internal Command StraysCommand() =>
            WithEnd(Action(new Command("strays", "The processes that should not be running (a job's or session's leftovers, or any process that computes without a break for half an hour), each with why and the command that ends it"),
                async (p, client, ct) =>
                {
                    var snapshot = await SnapshotAsync(client, ct);
                    await WriteAsync(p.GetValue(outputFormat), () => Strays(snapshot.Machine), () => StraysMarkdown(snapshot.Machine));
                    return 0;
                }));

        private Command WithEnd(Command strays)
        {
            var pid = new Argument<int>("pid") { Description = "The process id, as 'pilot strays' lists it" };
            strays.Subcommands.Add(Action(new Command("end", "End the stray the sweep names with this pid, on your word (a dismissed card does not matter); the shell's, the browser's and Windows' own are never ended, nor a pid that no longer starts when the sweep saw it") { Arguments = { pid }, Options = { actingOn } },
                async (p, client, ct) =>
                {
                    var snapshot = await SnapshotAsync(client, ct);
                    var (key, refusal) = StrayToEnd(snapshot.Machine, p.GetValue(pid));
                    if (key is null)
                    {
                        await error.WriteLineAsync($"pilot: {refusal}");
                        return 1;
                    }
                    return await Send(client, Verbs.EndStray, key, [], who, ct, p.GetValue(actingOn));
                }));
            return strays;
        }
    }
}
