using System.CommandLine;
using Node.Lib;
using Pilot.Contracts;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// <c>pilot-node</c>'s command line, on System.CommandLine as tianwen's CLI is: the node itself by default, <c>hook</c>
/// (what Claude Code runs on each hook), <c>hooks install|remove</c> and <c>autostart install|remove|show</c>.
/// </summary>
public static class NodeCommandLine
{
    /// <summary>Set to <c>1</c> to make a node local-only, as <c>--local-only</c> does (Node.Lib's <see cref="LocalOnlyMode"/>).</summary>
    public const string LocalOnlyVariable = "PILOT_LOCAL_ONLY";

    /// <summary>
    /// <c>--after &lt;pid&gt;</c>: wait for that process to exit before taking the socket. How a node that installed a
    /// release hands over to the new one (<see cref="NodeUpdater"/>); not for people, so hidden.
    /// </summary>
    public const string AfterOption = "--after";

    /// <summary>How long <see cref="AfterOption"/> waits for the node it takes over from.</summary>
    public static readonly TimeSpan AfterBudget = TimeSpan.FromMinutes(2);

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
        var noWatchtower = new Option<bool>("--no-watchtower") { Description = "Keep no watchtower session" };
        var lan = new Option<bool>("--lan") { Description = "Listen on the LAN too, and announce it: anyone there may watch, a command needs a grant this machine's person allows" };
        var maxJobs = new Option<int>("--max-jobs") { Description = "How many jobs (pilot run) run at once, from every flight; the rest wait their turn. 1 by default", DefaultValueFactory = _ => 1 };
        var jobMemory = new Option<double>("--job-memory")
        {
            Description = "The free memory, in GB, a job waits for before it starts; 2 by default, 0 to not wait",
            DefaultValueFactory = _ => 2,
        };
        var lanPort = new Option<int>("--lan-port") { Description = $"The TCP port for the LAN; {NodeWire.DefaultLanPort} by default", DefaultValueFactory = _ => NodeWire.DefaultLanPort };
        var localOnly = new Option<bool>(LocalOnlyMode.Argument)
        {
            Description = $"Bind nothing but the socket, whatever --lan says (also {LocalOnlyVariable}=1): for a node an agent starts, which the firewall would otherwise ask about",
        };
        var updatePolicy = new Option<string>("--update-policy")
        {
            Description = "When a newer release of pilot is out: auto installs it once no job runs, and says so; ask raises a card with Update; off does not look. auto by default; only an installed release looks",
            DefaultValueFactory = _ => UpdatePolicy.Auto.Name(),
        };
        updatePolicy.AcceptOnlyFromAmong([.. UpdatePolicies.Names]);
        var updateChannel = new Option<string>("--update-channel")
        {
            Description = "Which releases an installed pilot follows: stable (the tagged releases) or beta (main's every push, a Debug build on the .NET runtime). stable by default",
            DefaultValueFactory = _ => UpdateChannel.Stable.Name(),
        };
        updateChannel.AcceptOnlyFromAmong([.. UpdateChannels.Names]);
        var repoRoot = new Option<string[]>("--repo-root")
        {
            Description = $"A folder whose git clones the new-flight wizard offers (an org root); may be given more than once, beside {Flights.RepoCatalog.RootsVariable} and the flights' own repos' folders",
            AllowMultipleArgumentsPerToken = false,
        };
        var releaseFeed = new Option<string?>("--release-feed") { Description = "A folder of pilot-<version>-<rid>.zip to update from instead of GitHub", Hidden = true };
        var after = new Option<int?>(AfterOption) { Description = "Wait for this process to exit before taking the socket", Hidden = true };
        var watchtowerFolder = new Option<string?>("--watchtower-folder") { Description = "Where the watchtower runs, a folder Claude Code trusts; else PILOT_WATCHTOWER_FOLDER, else under --data" };
        machine.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } value && MachinePrefixes.Normalise(value) is null)
            {
                result.AddError($"--machine takes two letters and a number, as in DU1, not {value}");
            }
        });

        // hook: fail-open by rule, so it reads no option it could refuse.
        var hook = new Command(HookCommands.HookArgument, "Forward one Claude Code hook's stdin to the node; always exits 0, and prints only the node's answer to a Stop it holds");
        hook.SetAction((parseResult, ct) => HookCommands.HookAsync(parseResult.GetValue(socket), stdin, output));

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

        // autostart: this node at logon, with the node's own options after --, checked by the node's own parser first.
        var nodeArgs = new Argument<string[]>("node-options")
        {
            Description = @"The node's options, after --: pilot-node autostart install -- --lan --repo-root C:\src\org",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var install = new Command("install", "Start this node when you log in, with the options after --, and now when none runs") { Arguments = { nodeArgs } };
        install.SetAction(async (parseResult, ct) =>
        {
            var given = parseResult.GetValue(nodeArgs) ?? [];
            var check = Build(stdin, output, error, nodePath).Parse(given);
            if (check.Errors.Count > 0 || check.CommandResult.Command is not RootCommand)
            {
                await error.WriteLineAsync($"pilot-node: autostart takes the node's own options, and these are not: {(check.Errors.Count > 0 ? check.Errors[0].Message : string.Join(' ', given))}");
                return NodeExitCodes.InvalidArguments;
            }
            var socketGiven = given.SkipWhile(a => a != "--node-socket").Skip(1).FirstOrDefault() ?? parseResult.GetValue(socket);
            return await Autostart.InstallAsync(nodePath, given, NodeSocket.Resolve(socketGiven), output, error, ct);
        });
        var removeAutostart = new Command("remove", "Stop starting this node at logon; the node running now runs on");
        removeAutostart.SetAction((_, ct) => Autostart.RemoveAsync(output, ct));
        var showAutostart = new Command("show", "What starts this node at logon, if anything");
        showAutostart.SetAction(_ => Autostart.ShowAsync(output));
        var autostart = new Command("autostart", "Start this node when you log in (a Run entry on Windows, a systemd user unit on Linux)")
        {
            Subcommands = { install, removeAutostart, showAutostart },
        };

        // setup: everything a command line can do to put this machine's node in the fleet, then the checklist.
        var setupMachine = new Option<string?>("--machine") { Description = "The machine's prefix in session names, two letters and a number (DE1); else from its name" };
        setupMachine.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } value && MachinePrefixes.Normalise(value) is null)
            {
                result.AddError($"--machine takes two letters and a number, as in DE1, not {value}");
            }
        });
        var setupRepoRoot = new Option<string[]>("--repo-root") { Description = "A folder of git clones the new-flight wizard offers (an org root); may be given more than once", AllowMultipleArgumentsPerToken = false };
        var setupChannel = new Option<string?>("--update-channel") { Description = "stable (the tagged releases) or beta (main's every push)" };
        setupChannel.AcceptOnlyFromAmong([.. UpdateChannels.Names]);
        var noFirewall = new Option<bool>("--no-firewall") { Description = "Make no firewall rule (Windows asks for elevation to make one)" };
        var setup = new Command("setup", "Put this machine's node in the fleet: the LAN on, hooks, start at logon, the firewall; then what is left for you")
        {
            Options = { data, setupMachine, setupRepoRoot, setupChannel, noFirewall },
        };
        setup.SetAction((parseResult, ct) => NodeSetupCommand.RunAsync(
            NodeSocket.Resolve(parseResult.GetValue(socket)), Path.GetFullPath(parseResult.GetValue(data) ?? NodeSocket.DefaultDataFolder),
            parseResult.GetValue(setupMachine), parseResult.GetValue(setupRepoRoot), parseResult.GetValue(setupChannel), !parseResult.GetValue(noFirewall),
            nodePath, output, error, ct));

        var root = new RootCommand("pilot v2's node: one per machine, the one writer of its flights, legs, cards and sessions")
        {
            Options = { socket, data, machine, noWatchtower, watchtowerFolder, lan, lanPort, localOnly, maxJobs, jobMemory, updatePolicy, updateChannel, releaseFeed, repoRoot, after },
            Subcommands = { hook, hooks, autostart, setup },
        };
        root.SetAction(async (parseResult, ct) =>
        {
            var socketPath = NodeSocket.Resolve(parseResult.GetValue(socket));
            if (!NodeSocket.TryValidate(socketPath, out var invalid))
            {
                await error.WriteLineAsync(invalid);
                return NodeExitCodes.InvalidArguments;
            }
            // Local-only wins over --lan: it is how a launcher keeps a node it starts off the network.
            var mode = LocalOnlyMode.Of(LocalOnlyVariable, parseResult.GetValue(localOnly));
            var wantsLan = parseResult.GetValue(lan);
            if (wantsLan && !mode.MayBindTheNetwork && parseResult.GetResult(lan) is { Implicit: false })
            {
                await error.WriteLineAsync($"local-only ({LocalOnlyMode.Argument} or {LocalOnlyVariable}): --lan is ignored, the node listens on its socket only");
            }
            var options = new NodeOptions(socketPath, Path.GetFullPath(parseResult.GetValue(data) ?? NodeSocket.DefaultDataFolder), MachinePrefix: parseResult.GetValue(machine),
                Watchtower: !parseResult.GetValue(noWatchtower), WatchtowerFolderGiven: parseResult.GetValue(watchtowerFolder),
                Lan: wantsLan, LanPort: parseResult.GetValue(lanPort),
                MaxJobs: parseResult.GetValue(maxJobs), JobMemoryGigabytes: parseResult.GetValue(jobMemory),
                UpdatePolicy: UpdatePolicies.Parse(parseResult.GetValue(updatePolicy)) ?? UpdatePolicy.Auto,
                UpdateChannel: UpdateChannels.Parse(parseResult.GetValue(updateChannel)) ?? UpdateChannel.Stable,
                ReleaseFeed: parseResult.GetValue(releaseFeed) is { } feed ? Path.GetFullPath(feed) : null,
                RepoRootsGiven: parseResult.GetValue(repoRoot) is { Length: > 0 } roots ? [.. roots.Select(Path.GetFullPath)] : null);
            // The settings file under the command line: what the Settings page saved, wherever this start did not say.
            // The environment's PILOT_MACHINE counts as said, as it wins over the file just as --machine does.
            var given = new HashSet<string>(StringComparer.Ordinal);
            void Said(string key, Option option)
            {
                if (parseResult.GetResult(option) is { Implicit: false })
                {
                    given.Add(key);
                }
            }
            Said(NodeSettings.LanKey, lan);
            Said(NodeSettings.LanPortKey, lanPort);
            Said(NodeSettings.MachineKey, machine);
            Said(NodeSettings.RepoRootsKey, repoRoot);
            Said(NodeSettings.UpdatePolicyKey, updatePolicy);
            Said(NodeSettings.UpdateChannelKey, updateChannel);
            Said(NodeSettings.MaxJobsKey, maxJobs);
            Said(NodeSettings.JobMemoryKey, jobMemory);
            if (Environment.GetEnvironmentVariable("PILOT_MACHINE") is { Length: > 0 })
            {
                given.Add(NodeSettings.MachineKey);
            }
            options = NodeSettings.Read(options.SettingsFile).Over(options, given);
            if (!mode.MayBindTheNetwork)
            {
                options = options with { Lan = false, LocalOnly = true };
            }
            if (parseResult.GetValue(after) is { } predecessor)
            {
                await WaitForExitAsync(predecessor, ct);
            }
            return await NodeProgram.RunAsync(options, error, ct);
        });
        return root;
    }

    // The node this one takes over from stops once it has started this one; its lock goes with it.
    private static async Task WaitForExitAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(AfterBudget);
            await process.WaitForExitAsync(budget.Token);
        }
        catch (ArgumentException)
        {
            // gone already
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Still running after the budget: the lock says so, and this node exits with who holds it.
        }
    }
}
