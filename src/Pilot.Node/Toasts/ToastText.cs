using Pilot.Contracts;
using System.Globalization;

namespace Pilot.Node.Toasts;

/// <summary>
/// What pilot owns of a desktop toast (#1384): the tags it shows under, the words of the burst and test toasts, and the click payloads
/// (from <see cref="ToastLink"/>, one source with the GUI). Building the toast, cutting its text and shortening a long tag are AppShell's
/// <c>DesktopNotification</c>.
/// </summary>
public static class ToastText
{
    /// <summary>The tag of the one burst toast, replaced as more cards come.</summary>
    public const string BurstTag = "pilot-burst";

    /// <summary>The tag of the test toast the Desktop toasts setup check sends.</summary>
    public const string TestTag = "pilot-test";

    /// <summary>The test toast's title.</summary>
    public const string TestTitle = "pilot test toast";

    /// <summary>The test toast's body.</summary>
    public const string TestBody = "Desktop toasts work on this machine.";

    /// <summary>The toast of one card: <paramref name="body"/> is the card's summary, or its detail when it has none. The tag is the card's subject.</summary>
    public static ToastRequest ForCard(string subject, string cardId, string title, string? body, ToastTier tier) =>
        new(subject, title, body, tier, ToastLink.CardPayload(cardId));

    /// <summary>The one toast for <paramref name="count"/> normal cards that came in a burst; a click opens the cards page.</summary>
    public static ToastRequest Burst(int count) =>
        new(BurstTag, BurstTitle(count), null, ToastTier.Normal, ToastLink.CardsPayload);

    /// <summary>The test toast of the setup check: a click opens the cards page.</summary>
    public static ToastRequest Test() =>
        new(TestTag, TestTitle, TestBody, ToastTier.Normal, ToastLink.CardsPayload);

    /// <summary>The burst toast's title: <c>N cards need you</c>.</summary>
    public static string BurstTitle(int count) => string.Create(CultureInfo.InvariantCulture, $"{count} cards need you");
}
