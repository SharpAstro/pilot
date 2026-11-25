using System.CommandLine;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    private sealed partial class BuildContext
    {
        /// <summary>
        /// <c>pilot follow-up &lt;owner/name&gt; --title --body [--package] [--flight code]</c> (PIL1340): opens an issue in another repo for what the flight
        /// cannot do in its own, and keeps it on the flight. From a flight's plane it finds the flight; elsewhere it takes --flight.
        /// </summary>
        internal Command FollowUpCommand()
        {
            var repo = Arg("repo", "The other repo, as owner/name; cloned beside the flight's own repo");
            var title = new Option<string>("--title") { Description = "The issue's title", Required = true };
            var body = new Option<string>("--body") { Description = "What to do there and why; the node adds the flight and its issue and PR", Required = true };
            var package = new Option<bool>("--package") { Description = "The flight needs the change as a published package (else it is read at land)" };
            var flight = new Option<string?>("--flight") { Description = "The flight's code; inside a plane, that plane's flight" };
            return Action(new Command("follow-up", "Open an issue in another repo for a follow-up this flight cannot do itself, and list it on the flight, in status and in its PR. The flight's own repo is refused: a same-repo finding is done in the flight")
                {
                    Arguments = { repo },
                    Options = { title, body, package, flight },
                },
                async (p, client, ct) =>
                {
                    var (target, sender) = p.GetValue(flight) is { } named
                        ? (Verbs.FlightTarget(named), who)
                        : FlightHere(await SnapshotAsync(client, ct), cwd, who);
                    string[] args = [p.GetValue(repo)!, p.GetValue(title)!, p.GetValue(body)!, .. p.GetValue(package) ? new[] { Verbs.PackageFlag } : []];
                    return await Send(client, Verbs.FlightFollowUp, target, args, sender, ct);
                });
        }
    }
}
