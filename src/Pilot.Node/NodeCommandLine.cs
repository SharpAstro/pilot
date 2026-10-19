using System.CommandLine;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// <c>pilot-node</c>'s command line, on System.CommandLine as tianwen's CLI is: the node itself by default, <c>hook</c>
/// (what Claude Code runs on each hook) and <c>hooks install|remove</c>.
/// </summary>
public static class NodeCommandLine
{
    /// <param name="nodePath">The binary the hooks run; this process's own unless a test names one (a test host is no pilot-node).</param>
    public static Task<int> RunAsync(string[] args, Stream stdin, TextWriter output, TextWriter error, CancellationToken cancellationToken, string? nodePath = null)
    {
        var parsed = Build(stdin, output, error, nodePath ?? Environment.ProcessPath!).Parse(args);
        // hook runs inside every Claude Code session, fail-open by rule: even a command line it cannot read costs the
        // session nothing, no error line and no exit code Claude Code would show as a hook error.
        if (parsed.Errors.Count > 0 && args is [HookCommands.HookArgument, ..])
        {
            return Task.FromResult(0);
        }
        if (parsed.Errors.Count > 0)
        {
            foreach (var problem in parsed.Errors)
            {
                error.WriteLine($"pilot-node: {problem.Message}");
            }
            return Task.FromResult(NodeExitCodes.InvalidArguments);
        }
        return parsed.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }

    internal static RootCommand Build(Stream stdin, TextWriter output, TextWriter error, string nodePath)
    {
        var socket = new Option<string?>("--node-socket")
        {
            Description = "Where the node listens; else PILOT_NODE_SOCKET, else this user's default",
            Recursive = true,
        };
        var data = new Option<string?>("--data") { Description = "Where the node keeps its state; else this user's pilot2 folder" };
        var machine = new Option<string?>("--machine") { Description = "The machine's prefix in session names, two letters and a number (DU1); else PILOT_MACHINE, else from its name" };
        machine.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } value && MachinePrefixes.Normalise(value) is null)
            {
                result.AddError($"--machine takes two letters and a number, as in DU1, not {value}");
            }
        });

        // hook: fail-open by rule, so it reads no option it could refuse.
        var hook = new Command(HookCommands.HookArgument, "Forward one Claude Code hook's stdin to the node; always exits 0 and prints nothing");
        hook.SetAction((parseResult, ct) => HookCommands.HookAsync(parseResult.GetValue(socket), stdin));

        var settings = new Option<string?>("--settings") { Description = "The Claude Code settings file; else ~/.claude/settings.json (or under CLAUDE_CONFIG_DIR)" };
        Command HooksAction(string name, string description, bool install)
        {
            var command = new Command(name, description) { Options = { settings } };
            command.SetAction(parseResult => HookCommands.Hooks(install, parseResult.GetValue(settings), parseResult.GetValue(socket), nodePath, output, error));
            return command;
        }
        var hooks = new Command("hooks", "Put pilot's hooks in a Claude Code settings file, or take them out")
        {
            Subcommands =
            {
                HooksAction("install", "Add pilot's hook per event, beside the file's other hooks; a second install changes nothing", install: true),
                HooksAction("remove", "Take out pilot's hooks only", install: false),
            },
        };

        var root = new RootCommand("pilot v2's node: one per machine, the one writer of its flights, legs, cards and sessions")
        {
            Options = { socket, data, machine },
            Subcommands = { hook, hooks },
        };
        root.SetAction(async (parseResult, ct) =>
        {
            var socketPath = NodeSocket.Resolve(parseResult.GetValue(socket));
            if (!NodeSocket.TryValidate(socketPath, out var invalid))
            {
                await error.WriteLineAsync(invalid);
                return NodeExitCodes.InvalidArguments;
            }
            var options = new NodeOptions(socketPath, Path.GetFullPath(parseResult.GetValue(data) ?? NodeSocket.DefaultDataFolder), MachinePrefix: parseResult.GetValue(machine));
            return await NodeProgram.RunAsync(options, error, ct);
        });
        return root;
    }
}
