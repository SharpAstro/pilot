namespace Pilot.Contracts;

/// <summary>
/// What a client says to the node over its own event stream (a text message, JSON): the page it shows now, or none. The node
/// keeps it for as long as that stream is open and forgets it when the stream closes, so a client that sends nothing, as an older
/// one does, shows no page. An optional message: it does not raise <see cref="NodeWire.Version"/>, since a node that does not
/// know it ignores what a client sends.
/// </summary>
public sealed record ClientPage
{
    /// <summary>The page shown (<see cref="ClientPages"/>), or null for one the node has no use of knowing.</summary>
    public string? Page { get; init; }
}

/// <summary>The pages a client may say it shows, where the node does more while someone looks.</summary>
public static class ClientPages
{
    /// <summary>The Pull requests page: the node reads the open PRs every minute while a client shows it.</summary>
    public const string PullRequests = "prs";
}
