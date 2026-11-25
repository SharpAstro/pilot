using System.Globalization;
using System.Security;

namespace Pilot.Node.Toasts;

/// <summary>
/// The toast XML of a Ring card (#1356), pure: the title is the card's title, the body its summary (or detail), both XML-escaped and cut
/// with an ellipsis to the toast's limits; <c>scenario="urgent"</c> only for <see cref="ToastTier.Urgent"/>; a click is a protocol activation of
/// <c>pilot://card/&lt;id&gt;</c>; a dismiss action closes it. The burst toast ("N cards need you") stands for a run of normal rings.
/// </summary>
public static class ToastXml
{
    /// <summary>The longest title, in characters, the toast shows before the ellipsis.</summary>
    public const int TitleLimit = 60;

    /// <summary>The longest body, in characters, the toast shows before the ellipsis.</summary>
    public const int BodyLimit = 200;

    /// <summary>The tag of the one burst toast, replaced as more cards come.</summary>
    public const string BurstTag = "pilot-burst";

    /// <summary>The scheme the toast's click launches.</summary>
    public const string Scheme = "pilot";

    /// <summary>The toast's dismiss button label.</summary>
    public const string DismissLabel = "Dismiss";

    /// <summary>The link a click on a card's toast opens: <c>pilot://card/&lt;id&gt;</c>.</summary>
    public static string CardLink(string cardId) => $"{Scheme}://card/{Uri.EscapeDataString(cardId)}";

    /// <summary>The link a click on the burst toast opens: the cards page.</summary>
    public static string CardsLink => $"{Scheme}://cards";

    /// <summary>The toast of one card: <paramref name="body"/> is the card's summary, or its detail when it has none.</summary>
    public static string ForCard(string cardId, string title, string? body, ToastTier tier) =>
        Build(CardLink(cardId), tier, title, body);

    /// <summary>The one toast for <paramref name="count"/> normal cards that came in a burst.</summary>
    public static string Burst(int count) =>
        Build(CardsLink, ToastTier.Normal, BurstTitle(count), null);

    /// <summary>The burst toast's title: <c>N cards need you</c>.</summary>
    public static string BurstTitle(int count) => string.Create(CultureInfo.InvariantCulture, $"{count} cards need you");

    /// <summary>
    /// <paramref name="text"/> cut to <paramref name="limit"/> characters, the last of them an ellipsis when it was cut; whitespace runs
    /// (newlines too) become one space, since a toast line has no room for them. Never splits a surrogate pair.
    /// </summary>
    public static string Cut(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= limit) return flat;
        var end = limit - 1;
        if (end > 0 && char.IsHighSurrogate(flat[end - 1])) end--;
        return flat[..end].TrimEnd() + "…";
    }

    private static string Build(string launch, ToastTier tier, string title, string? body)
    {
        var scenario = tier == ToastTier.Urgent ? " scenario=\"urgent\"" : string.Empty;
        var lines = $"<text>{Escape(Cut(title, TitleLimit))}</text>";
        var text = Cut(body, BodyLimit);
        if (text.Length > 0) lines += $"<text>{Escape(text)}</text>";
        return $"<toast{scenario} activationType=\"protocol\" launch=\"{Escape(launch)}\">"
            + $"<visual><binding template=\"ToastGeneric\">{lines}</binding></visual>"
            + $"<actions><action content=\"{DismissLabel}\" arguments=\"dismiss\" activationType=\"system\"/></actions>"
            + "</toast>";
    }

    /// <summary>XML-escapes <paramref name="text"/> for element text and attribute values: <c>&lt; &gt; &amp; " '</c>.</summary>
    public static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;
}
