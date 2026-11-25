namespace Pilot.Ui;

/// <summary>
/// A <c>pilot:</c> link a desktop toast's click hands the GUI (#1356): <c>pilot://card/&lt;id&gt;</c> for one card, <c>pilot://cards</c> for
/// the burst toast, which opens the For you bar. The scheme is registered per user by the node, so the link arrives as the GUI's argument, or
/// as the payload a second launch hands to the running window.
/// </summary>
/// <param name="CardId">The card the link opens; null for the bare cards link.</param>
public sealed record PilotLink(string? CardId)
{
    /// <summary>The scheme, the one <c>ToastXml</c> writes.</summary>
    public const string Scheme = "pilot";

    /// <summary>Reads <paramref name="text"/> as a link; false for anything else (another scheme, another host, an empty id, junk).</summary>
    public static bool TryParse(string? text, out PilotLink link)
    {
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
}
