using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Updates;

namespace Pilot.Node;

/// <summary>
/// <c>pilot-node setup</c>: one command for a machine joining the fleet, in place of a runbook. It saves the LAN on (and
/// the prefix, repo roots and channel given) in the settings file, installs the hooks, installs the logon entry (which
/// carries no options now, the settings file has them) and starts the node, asks for the firewall rule, and prints the
/// checklist: what is left is a person's, and says so.
/// </summary>
public static class NodeSetupCommand
{
    public static async Task<int> RunAsync(string socketPath, string dataFolder, string? machine, string[]? repoRoots, string? channel, bool firewall,
        string nodePath, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var file = NodeSettings.PathIn(dataFolder);
        var settings = NodeSettings.Read(file) with { Lan = true };
        foreach (var (key, values) in new (string, string[]?)[]
        {
            (NodeSettings.MachineKey, machine is null ? null : [machine]),
            (NodeSettings.RepoRootsKey, repoRoots is { Length: > 0 } ? repoRoots : null),
            (NodeSettings.UpdateChannelKey, channel is null ? null : [channel]),
        })
        {
            if (values is null)
            {
                continue;
            }
            var (updated, problem) = NodeSettings.With(settings, key, values);
            if (updated is null)
            {
                await error.WriteLineAsync($"setup: {problem}");
                return NodeExitCodes.InvalidArguments;
            }
            settings = updated;
        }
        settings.Write(file);
        await output.WriteLineAsync($"setup: saved {file}: {string.Join(", ", NodeSettings.Keys.Where(k => settings.Get(k) is not null).Select(k => $"{k} {string.Join(' ', settings.Get(k)!)}"))}");

        var options = settings.Over(new NodeOptions(socketPath, dataFolder, UpdatePolicy: UpdatePolicy.Auto), new HashSet<string>());
        var setup = new NodeSetup(options, nodePath, SessionTrackerOptions.FromEnvironment().Profile.Root);

        await Step(NodeSetup.HooksKey, "hooks installed");
        // A node already running started before these settings: ask it to restart with them (a set it runs already is a no-op).
        var running = await NodeSocket.IsListeningAsync(socketPath, cancellationToken);
        await Step(NodeSetup.AutostartKey, running ? "starts at logon" : "starts at logon, and started now");
        if (running)
        {
            using var client = new NodeClient(socketPath);
            var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = [NodeSettings.LanKey, "on"], Who = "setup" }, cancellationToken);
            await output.WriteLineAsync($"setup: the running node: {envelope.Response?.Way ?? envelope.Response?.Error ?? envelope.Error}");
        }
        if (firewall && OperatingSystem.IsWindows() && setup.FirewallAllows() is (false, _))
        {
            await output.WriteLineAsync("setup: asking Windows for elevation, to let pilot-node in on private networks...");
            await Step(NodeSetup.FirewallKey, "firewall rule made");
        }
        Directory.CreateDirectory(options.WatchtowerFolder);

        await output.WriteLineAsync();
        var left = 0;
        foreach (var check in setup.Checks())
        {
            await output.WriteLineAsync($"  {(check.Ok ? "ok  " : check.Optional ? "--  " : "TODO")} {check.Title}: {check.Line}");
            left += check.Ok || check.Optional ? 0 : 1;
        }
        await output.WriteLineAsync(left == 0
            ? "\nsetup: done. Ask another machine for control from its row on the Fleet page (Ctrl+5)."
            : $"\nsetup: {left} left for you, above; the Settings page (Ctrl+6) has a button for those the node can fix.");
        return 0;

        async Task Step(string key, string done)
        {
            var problem = await setup.FixAsync(key, cancellationToken);
            await output.WriteLineAsync(problem is null ? $"setup: {done}" : $"setup: {key}: {problem}");
        }
    }
}
