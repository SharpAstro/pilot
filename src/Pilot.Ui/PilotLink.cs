namespace Pilot.Ui;

/// <summary>
/// A <c>pilot:</c> link a desktop toast's click hands the GUI (#1356, #1384). Two forms are read: AppShell's, <c>pilot:card%2F&lt;id&gt;</c> for
/// one card and <c>pilot:cards</c> for the burst toast (the payload escaped; <c>LaunchScheme.Link</c>), and the older <c>pilot://card/&lt;id&gt;</c>
/// and <c>pilot://cards</c> (toasts left in the notification centre, the desktop cat's card link). Both open the card or the For you bar. The scheme is registered per user by the node, so the link arrives as the GUI's argument, or
/// as the payload a second launch hands to the running window.
/// </summary>
/// <param name="CardId">The card the link opens; null for the bare cards link.</param>
public sealed record PilotLink(string? CardId)
{
    /// <summary>The scheme, the one the node registers and the toasts launch (<c>ToastLink.Scheme</c>).</summary>
    public const string Scheme = "pilot";

    /// <summary>Reads <paramref name="text"/> as a link; false for anything else (another scheme, another host, an empty id, junk).</summary>
    public static bool TryParse(string? text, out PilotLink link)
    {
        if (TryParseAppShell(text, out link))
        {
            return true;
        }
        link = new PilotLink((string?)null);
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var path = uri.AbsolutePath.Trim('/');
        if (uri.Host.Equals("cards", StringComparison.OrdinalIgnoreCase) && path.Length == 0)
        {
            return true;
        }
        if (uri.Host.Equals("card", StringComparison.OrdinalIgnoreCase) && path.Length > 0 && !path.Contains('/'))
        {
            link = new PilotLink(Uri.UnescapeDataString(path));
            return true;
        }
        return false;
    }

    // AppShell's form: the scheme, a colon, then the payload escaped by Uri.EscapeDataString (not "//", which is the older form).
    private static bool TryParseAppShell(string? text, out PilotLink link)
    {
        link = new PilotLink((string?)null);
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || !t.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var escaped = t[(Scheme.Length + 1)..];
        if (escaped.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        string payload;
        try
        {
            payload = Uri.UnescapeDataString(escaped);
        }
        catch (UriFormatException)
        {
            return false;
        }
        if (!Pilot.Contracts.ToastLink.TryReadPayload(payload, out var cardId))
        {
            return false;
        }
        link = new PilotLink(cardId);
        return true;
    }
}
