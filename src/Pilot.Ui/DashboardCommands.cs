using System.CommandLine.Parsing;
using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What the dashboard's buttons run, for either host (the GUI's window, pilot-tui's terminal): one verb on the node, a
/// <c>connect</c> through the CLI's own command tree (Pilot.Commands), in-process, against the host's node and as the host
/// (<c>gui</c>, <c>tui</c>), and the opening of a file or a URL.
/// </summary>
internal static class DashboardCommands
{
    /// <summary>Runs <paramref name="line"/> (<c>connect 192.168.1.20:47731</c>) through the CLI's commands; returns what it printed.</summary>
    /// <param name="node">Which node: <c>--node-socket &lt;path&gt;</c> for this machine's, <c>--node host:port</c> for one on the LAN.</param>
    /// <param name="who">The host, as the commands say who sent them.</param>
    public static async Task<string> RunAsync(string line, string[] node, string who, CancellationToken cancellationToken)
    {
        var args = CommandLineParser.SplitCommandLine(line).ToList();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync([.. node, .. args], Environment.CurrentDirectory, output, error, cancellationToken, who);
        var said = (output.ToString() + error.ToString()).TrimEnd();
        return said.Length > 0 ? said : exit == 0 ? "ok" : $"exit {exit}";
    }

    /// <summary>Runs one action's verb on the node; returns what became of it.</summary>
    /// <param name="answered">Told each envelope the node answers with, so a host can see a refused grant.</param>
    public static async Task<string> ActAsync(NodeClient client, FlightAction action, string who, CancellationToken cancellationToken, Action<ResponseEnvelope<Pilot.Contracts.CommandResult>>? answered = null)
    {
        // One press of a button at a time: a Retry move pressed again while the first was moving the flight was answered with a
        // refusal ("PIL437 is landed (moved to DE1)") that stood on the page above the first press's ok (SE1, 2026-10-29).
        var key = $"{action.Node?.Key}|{action.Verb}|{action.Target}";
        if (!Running.TryAdd(key, true))
        {
            return $"already running: {action.Label}";
        }
        try
        {
            var envelope = await client.SendAsync(new CommandRequest { Verb = action.Verb, Target = action.Target, Args = action.Args, Who = who }, cancellationToken);
            answered?.Invoke(envelope);
            return envelope.Success && envelope.Response is { Ok: true }
                ? $"ok: {action.Label}"
                : $"refused: {action.Label}: {envelope.Response?.Error ?? envelope.Error}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            // A button that fails says so on the page: a node that answered with no envelope (a crash) or not at all.
            return $"failed: {action.Label}: {ex.Message}";
        }
        finally
        {
            Running.TryRemove(key, out _);
        }
    }

    // The buttons whose verb the node has not answered yet, by node, verb and target.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Running = new(StringComparer.Ordinal);

    /// <summary>
    /// The command a card's button sends: <paramref name="verb"/> (<see cref="Verbs.Answer"/> with the choice's number, or
    /// <see cref="Verbs.Dismiss"/>) on <paramref name="card"/>. One builder for the panel's buttons in either host and for the
    /// Pull requests page's Merge and Go around, which press the flight's card, so a PR row says to the node what its card does.
    /// </summary>
    public static CommandRequest CardRequest(string verb, CardRow card, string[] args, string who) =>
        new() { Verb = verb, Target = Verbs.CardTarget(card.Id), Args = args, Who = who };

    /// <summary>The command a card's choice number is answered with.</summary>
    public static CommandRequest AnswerRequest(CardRow card, int number, string who) =>
        CardRequest(Verbs.Answer, card, [number.ToString(System.Globalization.CultureInfo.InvariantCulture)], who);

    /// <summary>What a PR's Review sends: <c>pilot review pr</c>, a fresh reviewer pinned at its head, or a flight's own review for a flight's PR.</summary>
    public static CommandRequest ReviewPrRequest(PullRequestRow pr, string who) => pr.Flight is { } flight
        // A flight's PR has one review, the flight's own: `pilot review pr` on it is refused, so its Review is `pilot flight review`.
        ? new() { Verb = Verbs.StartReview, Target = Verbs.FlightTarget(flight), Args = [], Who = who }
        : new() { Verb = Verbs.ReviewPr, Target = Verbs.PrTarget(pr.Repo, pr.Number), Args = [], Who = who };

    /// <summary>What <c>pilot review fix</c> sends for <paramref name="pr"/>: a fixer on its branch with the review's findings.</summary>
    public static CommandRequest FixPrRequest(PullRequestRow pr, string who) =>
        new() { Verb = Verbs.FixPr, Target = Verbs.PrTarget(pr.Repo, pr.Number), Args = [], Who = who };

    /// <summary>Opens a file or a URL the way the platform does (the browser, the image viewer); false when it could not.</summary>
    public static bool Open(string location)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(location) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
