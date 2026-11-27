namespace Pilot.Contracts;

/// <summary>
/// What a desktop toast's click carries (#1384): the payload the node hands AppShell's <c>LaunchScheme.Link</c> (which writes
/// <c>pilot:</c> and the payload escaped) and the GUI reads back from the same link. One source for both sides. Not a wire type.
/// </summary>
public static class ToastLink
{
    /// <summary>The scheme the node registers per user.</summary>
    public const string Scheme = "pilot";

    private const string CardPrefix = "card/";

    /// <summary>The payload of the cards page (the burst toast).</summary>
    public const string CardsPayload = "cards";

    /// <summary>The payload that opens one card.</summary>
    public static string CardPayload(string cardId) => CardPrefix + cardId;

    /// <summary>
    /// Reads an unescaped payload. True for <c>cards</c> (<paramref name="cardId"/> null) and for <c>card/&lt;id&gt;</c> with a non-blank
    /// id (everything after <c>card/</c>, so an id may hold a slash and round-trips); false for anything else.
    /// </summary>
    public static bool TryReadPayload(string? payload, out string? cardId)
    {
        cardId = null;
        if (payload is null)
        {
            return false;
        }
        if (payload.Equals(CardsPayload, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (payload.StartsWith(CardPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = payload[CardPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(id))
            {
                cardId = id;
                return true;
            }
        }
        return false;
    }
}
