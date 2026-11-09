using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>A text file's lines, read alongside a writer that still has it open (the log is being appended to).</summary>
    private static async Task<List<string>> ReadLinesAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }
    private sealed partial class BuildContext
    {
        /// <summary>Makes the update, node, nodes, connect, grants, settings and logs: this node and the LAN.</summary>
        internal (Command, Command, Command, Command, Command, Command, Command) NodeCommands()
        {
            // update: the node installs the latest release of pilot over the folder it runs from, and restarts.
            var updateCheck = new Option<bool>("--check") { Description = "Only say whether a newer release is out" };
            var updateAdopt = new Option<bool>("--adopt") { Description = "Install the latest release over a build of one's own, which then updates itself" };
            var update = Action(new Command("update", "Install the latest release of pilot where the node runs from, and restart the node; its flights and sessions carry on")
                { Options = { updateCheck, updateAdopt, actingOn } },
                (p, client, ct) => Send(client, Verbs.Update, Verbs.NodeTarget,
                    [p.GetValue(updateCheck) ? "check" : "install", .. p.GetValue(updateAdopt) ? new[] { "--adopt" } : []], who, ct, p.GetValue(actingOn)));
            // node restart: the node starts its successor and stops, as for a setting or an update, so a session never kills the process.
            var nodeRestartWhy = Words("why", "Why, for its Handled line", required: false);
            var nodeRestart = Action(new Command("restart", "Restart the node in place: its flights, sessions and jobs carry on (jobs are not waited for; the answer says how many)")
                { Arguments = { nodeRestartWhy }, Options = { actingOn } },
                (p, client, ct) => Send(client, Verbs.RestartNode, Verbs.NodeTarget, p.GetValue(nodeRestartWhy) ?? [], who, ct, p.GetValue(actingOn)));
            // node reboot: the machine made ready to reboot (#916), never rebooted by the node itself.
            var rebootWhy = Words("why", "Why, for its Handled line", required: false);
            var rebootRequest = Action(new Command("request", "Make the machine ready to reboot: maintenance mode, and every live session told to commit, push and write its state to a file; the node collects them and says when it can reboot")
                { Arguments = { rebootWhy }, Options = { actingOn } },
                (p, client, ct) => Send(client, Verbs.Reboot, Verbs.NodeTarget, ["request", .. p.GetValue(rebootWhy) ?? []], who, ct, p.GetValue(actingOn)));
            var rebootStatus = Action(new Command("status", "How many sessions have saved their state, and whether the machine can reboot"),
                (p, client, ct) => Send(client, Verbs.Reboot, Verbs.NodeTarget, ["status"], who, ct));
            var rebootCancel = Action(new Command("cancel", "Drop the reboot request before the reboot and leave maintenance mode") { Options = { actingOn } },
                (p, client, ct) => Send(client, Verbs.Reboot, Verbs.NodeTarget, ["cancel"], who, ct, p.GetValue(actingOn)));
            var rebootDone = Action(new Command("done", "After the reboot: leave maintenance mode, each flight resuming from its saved state") { Options = { actingOn } },
                (p, client, ct) => Send(client, Verbs.Reboot, Verbs.NodeTarget, ["done"], who, ct, p.GetValue(actingOn)));
            var reboot = new Command("reboot", "Make the machine ready to reboot, and resume after it") { Subcommands = { rebootRequest, rebootStatus, rebootCancel, rebootDone } };
            var nodeCommand = new Command("node", "This machine's pilot node") { Subcommands = { nodeRestart, reboot } };

            // the LAN: find nodes, ask one for control, and see or take back what this node has granted
            var nodes = new Command("nodes", "The pilot nodes announcing themselves on the LAN");
            nodes.SetAction(async (_, ct) =>
            {
                var peers = await NodeTargets.DiscoverAsync(TimeSpan.FromSeconds(3), ct);
                if (peers.Count == 0)
                {
                    await output.WriteLineAsync("no pilot node announced itself on the LAN within 3 s (a node announces with pilot-node --lan)");
                    return 0;
                }
                foreach (var peer in peers)
                {
                    await output.WriteLineAsync($"{peer.EndPoint}  {peer.DisplayName}{(peer.Properties.TryGetValue("machine", out var m) ? $"  ({m})" : "")}");
                    await output.WriteLineAsync("  " + await NodeTargets.WatchtowerLineAsync(peer.EndPoint.ToString()!, TimeSpan.FromSeconds(2), ct));
                }
                return 0;
            });

            var endpoint = Arg("node", "The node, host:port");
            var askAs = new Option<string?>("--as") { Description = "Who to ask as, as its card and every command will say; else user@machine" };
            var connectNode = Action(new Command("connect", "Ask a node on the LAN for control: its person allows it on a card there, and the token is kept here")
                {
                    Arguments = { endpoint },
                    Options = { askAs },
                },
                async (p, _, ct) =>
                {
                    if (!NodeClient.IsEndpoint(p.GetValue(endpoint)!, out var target))
                    {
                        throw new UsageException($"connect takes a node as host:port, not {p.GetValue(endpoint)}");
                    }
                    using var remote = NodeClient.Remote(target);
                    var label = p.GetValue(askAs) ?? $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant();
                    var result = await LanAsk.RunAsync(
                        target,
                        async token =>
                        {
                            var asked = await remote.AskGrantAsync(label, token);
                            return asked is { Success: true, Response: { } ticket } ? new LanAskOffer(ticket.Id) : new LanAskOffer(null, asked.Error);
                        },
                        async (id, token) =>
                        {
                            var read = await remote.GrantTicketAsync(id, token);
                            var outcome = read.Response?.Outcome switch
                            {
                                "accepted" => LanInviteOutcome.Accepted,
                                "declined" => LanInviteOutcome.Declined,
                                "withdrawn" => LanInviteOutcome.Withdrawn,
                                "unknown" => LanInviteOutcome.Unknown,
                                _ => LanInviteOutcome.Pending,
                            };
                            return new LanAskReading(outcome, read.Response?.Token);
                        },
                        new LanHeldTokenFile(NodeTargets.TokenFile),
                        TimeProvider.System,
                        asked: () => output.WriteLine($"asked {target} as {label}: a card rings there for its person to Allow or Deny"),
                        cancellationToken: ct);
                    switch (result.Outcome)
                    {
                        case LanAskOutcome.Accepted:
                            await output.WriteLineAsync($"allowed: commands go to {target} with --node {target} (or PILOT_NODE={target})");
                            return 0;
                        case LanAskOutcome.Declined:
                            await output.WriteLineAsync($"denied by {target}'s person");
                            return 1;
                        case LanAskOutcome.Lapsed:
                            await output.WriteLineAsync($"the ask lapsed on {target}; ask again");
                            return 1;
                        case LanAskOutcome.Refused:
                            await output.WriteLineAsync($"refused: {result.Error}");
                            return 1;
                        default:
                            await output.WriteLineAsync($"no answer from {target} within 5 minutes; ask again when its person is there");
                            return 1;
                    }
                });

            var grants = Action(new Command("grants", "The control this node has granted to machines on the LAN"), async (_, client, ct) =>
            {
                var given = await client.GrantsAsync(ct);
                if (given is not { Success: true, Response: { } list })
                {
                    await output.WriteLineAsync($"refused: {given.Error}");
                    return 1;
                }
                await output.WriteLineAsync(list.Length == 0 ? "no grants" : string.Join(Environment.NewLine, list.Select(g => $"{g.Id}  {g.Label}  since {g.GrantedAt.ToLocalTime():yyyy-MM-dd HH:mm}")));
                return 0;
            });
            var grantId = Arg("grant", "The grant's id, from pilot grants");
            var revoke = Action(new Command("revoke", "Take a grant back: that machine can watch, and no longer command") { Arguments = { grantId } },
                (p, client, ct) => Send(client, Verbs.RevokeGrant, Verbs.NodeTarget, [p.GetValue(grantId)!], who, ct));
            grants.Subcommands.Add(revoke);

            // The Settings page's, for a terminal and an agent: this machine's node only.
            var settings = Action(new Command("settings", "This node's setup checklist and settings, and where each setting's value comes from"), async (_, client, ct) =>
            {
                // The settings and the checklist are two routes: the checks run programs, so the page does not wait on them.
                var page = await client.GetSettingsWithChecksAsync(ct);
                if (page is not { Success: true, Response: { } shown })
                {
                    await output.WriteLineAsync($"refused: {page.Error}");
                    return 1;
                }
                foreach (var check in shown.Checks)
                {
                    await output.WriteLineAsync($"{(check.Ok ? "ok  " : check.Optional ? "--  " : "TODO")} {check.Title}: {check.Line}{(check.Fix is null ? "" : $"  (pilot settings setup {check.Key})")}");
                }
                await output.WriteLineAsync();
                foreach (var setting in shown.Settings)
                {
                    var value = setting.Value.Replace('\n', ';');
                    var note = setting.Source == SettingSources.CommandLine ? "command line"
                        : setting.Pending ? $"saved as {(setting.Saved ?? "default").Replace('\n', ';')}, at the next start"
                        : setting.Source;
                    await output.WriteLineAsync($"{setting.Key,-16} {(value.Length == 0 ? "-" : value)}  ({note})");
                    if (setting.FleetValue is { Length: > 0 } fleetValue)
                    {
                        await output.WriteLineAsync($"{"",-16} fleet: {fleetValue.Replace('\n', ';')}");
                    }
                    if (setting.Deviations.Length > 0)
                    {
                        await output.WriteLineAsync($"{"",-16} differs: {string.Join("; ", setting.Deviations.Select(d => $"{d.Node} runs {d.Value}"))}");
                    }
                }
                if (shown.Restart is { } restart)
                {
                    await output.WriteLineAsync();
                    await output.WriteLineAsync(restart);
                }
                return 0;
            });
            var settingKey = Arg("setting", "lan, machine, repo-roots, update-channel, update-policy, max-jobs, job-memory or lan-port");
            var settingValue = Words("value", "Its value (every folder, for repo-roots); none, or default, takes it out", required: false);
            var setFleet = new Option<bool>("--fleet") { Description = "For a role range (worker-range, planner-range, reviewer-range, watchtower-range): set the fleet's range, pushed to every node, not only this node's own; written after the value" };
            var set = Action(new Command("set", "Save a setting; the node restarts to run with it at once, jobs carry on. A role range (worker-range, planner-range, reviewer-range, watchtower-range) takes --fleet after the value to set the fleet's layer") { Arguments = { settingKey, settingValue }, Options = { actingOn, setFleet } },
                (p, client, ct) => Send(client, Verbs.Set, Verbs.NodeTarget, [p.GetValue(settingKey)!, .. p.GetValue(settingValue) ?? [], .. p.GetValue(setFleet) ? new[] { "--fleet" } : []], who, ct, p.GetValue(actingOn)));
            var checkKey = Arg("check", "autostart, no-autostart, hooks, merge-guard, claude-permissions, lan, firewall, defender or trust");
            var defenderProcesses = new Option<bool>("--processes") { Description = "With defender: exclude pilot-node, dotnet, MSBuild, VBCSCompiler, testhost, git, git-lfs, git-remote-https and every *Tests*.exe runner built under the repo roots as processes too, not only the folders" };
            var setupCommand = Action(new Command("setup", "Make a setup check's fix: the logon entry, the hooks, the merge guard, Claude Code's allow rules for pilot's commands (claude-permissions), the LAN, the firewall rule and the Defender exclusions (each asks for elevation), the trust prompt") { Arguments = { checkKey }, Options = { defenderProcesses, actingOn } },
                (p, client, ct) => Send(client, Verbs.Setup, Verbs.NodeTarget, [p.GetValue(checkKey)!, .. p.GetValue(defenderProcesses) ? new[] { "--processes" } : []], who, ct, p.GetValue(actingOn)));
            settings.Subcommands.Add(set);
            settings.Subcommands.Add(setupCommand);

            // logs: the GUI's and the node's rolling logs, read from this machine's folder; no node needed, as neither writes to stderr
            var logWhich = new Argument<string?>("which") { Description = "gui or node; both when left out", Arity = ArgumentArity.ZeroOrOne };
            logWhich.AcceptOnlyFromAmong("gui", "node");
            var logTail = new Option<int>("--tail") { Description = "How many of the last lines to print", DefaultValueFactory = _ => 50 };
            var logs = new Command("logs", "The last lines of logs/gui.log and logs/node.log in this machine's pilot2 folder, each under its path") { Arguments = { logWhich }, Options = { logTail } };
            logs.SetAction(async (p, ct) =>
            {
                var tail = p.GetValue(logTail);
                if (tail < 1)
                {
                    await error.WriteLineAsync("pilot: --tail takes a number of lines, 1 or more");
                    return 2;
                }
                var names = p.GetValue(logWhich) is { } one ? new[] { one } : ["gui", "node"];
                var first = true;
                foreach (var name in names)
                {
                    var log = new RollingLog(name);
                    if (!first)
                    {
                        await output.WriteLineAsync();
                    }
                    first = false;
                    if (!File.Exists(log.Path))
                    {
                        await output.WriteLineAsync($"no {name} log yet at {log.Path}");
                        continue;
                    }
                    await output.WriteLineAsync($"== {name}: {log.Path} ==");
                    var lines = await ReadLinesAsync(log.Path, ct);
                    if (lines.Count < tail && File.Exists(log.RolledPath))
                    {
                        var rolled = await ReadLinesAsync(log.RolledPath, ct);
                        lines.InsertRange(0, rolled.Skip(Math.Max(0, rolled.Count - (tail - lines.Count))));
                    }
                    foreach (var line in lines.Skip(Math.Max(0, lines.Count - tail)))
                    {
                        await output.WriteLineAsync(line);
                    }
                }
                return 0;
            });

            return (update, nodeCommand, nodes, connectNode, grants, settings, logs);
        }

        /// <summary>Makes the mcp command: these same commands as an MCP server's tools, over this process's standard streams.</summary>
        internal Command McpCommand()
        {
            var mcp = new Command("mcp", "Serve these commands as MCP tools, and the node's flights, legs, cards and sessions as resources, over stdio: claude mcp add pilot -- pilot mcp");
            var handedOver = new Option<bool>("--handed-over") { Hidden = true, Description = "Started by another pilot mcp's hand-over: never hands over itself" };
            mcp.Options.Add(handedOver);
            mcp.SetAction(async (parseResult, ct) =>
            {
                var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var input = new StreamReader(Console.OpenStandardInput(), utf8);
                await using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8);
                await new McpServer(cwd, parseResult.GetValue(node), parseResult.GetValue(socket), handedOver: parseResult.GetValue(handedOver)).RunAsync(input, stdout, ct);
                return 0;
            });

            return mcp;
        }
    }
}
