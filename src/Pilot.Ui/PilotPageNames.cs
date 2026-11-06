namespace Pilot.Ui;

/// <summary>
/// The stable lower-case name of each page, which a tool outside the process sends to take the window there (the inspector's
/// <c>page</c> signal) and which a page's fault snapshot says it is for. They are the names the text-overlap sweep uses
/// (<c>PageTextOverlapTests</c>), so a fault found there and one found in the running window name the page the same way.
/// </summary>
internal static class PilotPageNames
{
    private static readonly (PilotPage Page, string Name)[] All =
    [
        (PilotPage.Home, "home"),
        (PilotPage.Chat, "chat"),
        (PilotPage.Sessions, "sessions"),
        (PilotPage.Flights, "flights"),
        (PilotPage.Repos, "repos"),
        (PilotPage.PullRequests, "pull requests"),
        (PilotPage.Jobs, "jobs"),
        (PilotPage.Fleet, "fleet"),
        (PilotPage.Settings, "settings"),
        (PilotPage.Ideas, "ideas"),
        (PilotPage.Spend, "spend"),
    ];

    /// <summary>Every page's name, in the rail's order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(a => a.Name)];

    /// <summary>The name of <paramref name="page"/>; for <see cref="PilotPage.Cards"/>, which is never a page shown, its enum name in lower case.</summary>
    public static string Of(PilotPage page) =>
        All.FirstOrDefault(a => a.Page == page).Name ?? page.ToString().ToLowerInvariant();

    /// <summary>
    /// The page called <paramref name="name"/>, ignoring case; a hyphen or underscore stands for the space in "pull requests", so the
    /// name goes through a command line or a JSON argument unquoted. False for a name no page has.
    /// </summary>
    public static bool TryParse(string? name, out PilotPage page)
    {
        var key = name?.Trim().Replace('-', ' ').Replace('_', ' ');
        foreach (var (p, n) in All)
        {
            if (string.Equals(n, key, StringComparison.OrdinalIgnoreCase))
            {
                page = p;
                return true;
            }
        }

        page = default;
        return false;
    }

    /// <summary>What to say to a caller whose <paramref name="name"/> is no page: what was asked and the names that exist.</summary>
    public static string Refusal(string? name) =>
        $"no page called '{name}'. Pages: {string.Join(", ", Names)}";
}
