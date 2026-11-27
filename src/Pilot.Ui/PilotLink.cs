namespace Pilot.Ui;

/// <summary>What a <see cref="PilotLink"/> opens.</summary>
public enum PilotLinkKind
{
    /// <summary>One card (<c>pilot://card/&lt;id&gt;</c>), or the bare cards link (null id) that opens the For you bar.</summary>
    Card,

    /// <summary>A flight's page, on a leg's line when one is given (<c>pilot://flight/&lt;code&gt;[/&lt;leg&gt;]</c>).</summary>
    Flight,

    /// <summary>A session's row on the Sessions page (<c>pilot://session/&lt;id&gt;</c>).</summary>
    Session,
}

/// <summary>
/// A <c>pilot:</c> link a desktop toast's click or a buddy peek row hands the GUI (#1356, #1381): <c>pilot://card/&lt;id&gt;</c> for one card,
/// <c>pilot://cards</c> for the burst toast (the For you bar), <c>pilot://flight/&lt;code&gt;[/&lt;leg&gt;]</c> for a flight (and its leg's line) and
/// <c>pilot://session/&lt;id&gt;</c> for a session; each may end in <c>?node=&lt;name&gt;</c> for another node's. The scheme is registered per user by
/// the node, so the link arrives as the GUI's argument, or as the payload a second launch hands to the running window.
/// </summary>
/// <param name="CardId">The card the link opens; null for the bare cards link and for the flight and session kinds.</param>
public sealed record PilotLink(string? CardId)
{
    /// <summary>The scheme, the one <c>ToastXml</c> writes.</summary>
    public const string Scheme = "pilot";

    /// <summary>What the link opens.</summary>
    public PilotLinkKind Kind { get; init; } = PilotLinkKind.Card;

    /// <summary>The flight code of a flight link.</summary>
    public string? FlightCode { get; init; }

    /// <summary>The leg of a flight link, when it names one.</summary>
    public string? Leg { get; init; }

    /// <summary>The session id of a session link.</summary>
    public string? SessionId { get; init; }

    /// <summary>The node the link is about (<c>?node=</c>), when it is not the window's own.</summary>
    public string? Node { get; init; }

    /// <summary>The link for a flight (and a leg), with the node when it is another's.</summary>
    public static string ForFlight(string code, string? leg = null, string? node = null)
        => $"{Scheme}://flight/{Uri.EscapeDataString(code)}" + (leg is { Length: > 0 } ? "/" + Uri.EscapeDataString(leg) : string.Empty) + NodeQuery(node);

    /// <summary>The link for a session, with the node when it is another's.</summary>
    public static string ForSession(string id, string? node = null) => $"{Scheme}://session/{Uri.EscapeDataString(id)}" + NodeQuery(node);

    private static string NodeQuery(string? node) => node is { Length: > 0 } ? "?node=" + Uri.EscapeDataString(node) : string.Empty;

    /// <summary>Reads <paramref name="text"/> as a link; false for anything else (another scheme, another host, an empty id, extra path parts, junk).</summary>
    public static bool TryParse(string? text, out PilotLink link)
    {
        link = new PilotLink((string?)null);
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // One leading and one trailing slash are the URI's own; any other empty segment (pilot://flight//leg) makes the link junk.
        var path = uri.AbsolutePath;
        path = path.StartsWith('/') ? path[1..] : path;
        path = path.EndsWith('/') ? path[..^1] : path;
        string? node = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals("node", StringComparison.OrdinalIgnoreCase) && Uri.UnescapeDataString(pair[(eq + 1)..]).Trim() is { Length: > 0 } name)
            {
                node = name;
            }
        }
        var parts = path.Length == 0 ? [] : path.Split('/');
        if (uri.Host.Equals("cards", StringComparison.OrdinalIgnoreCase) && parts.Length == 0)
        {
            link = new PilotLink((string?)null) { Node = node };
            return true;
        }
        if (uri.Host.Equals("card", StringComparison.OrdinalIgnoreCase) && parts is [var card] && card.Length > 0)
        {
            link = new PilotLink(Uri.UnescapeDataString(card)) { Node = node };
            return true;
        }
        if (uri.Host.Equals("flight", StringComparison.OrdinalIgnoreCase) && parts.Length is 1 or 2 && parts.All(p => p.Length > 0))
        {
            link = new PilotLink((string?)null)
            {
                Kind = PilotLinkKind.Flight, FlightCode = Uri.UnescapeDataString(parts[0]), Leg = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : null, Node = node,
            };
            return true;
        }
        if (uri.Host.Equals("session", StringComparison.OrdinalIgnoreCase) && parts is [var session] && session.Length > 0)
        {
            link = new PilotLink((string?)null) { Kind = PilotLinkKind.Session, SessionId = Uri.UnescapeDataString(session), Node = node };
            return true;
        }
        return false;
    }
}
