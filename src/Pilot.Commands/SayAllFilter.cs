using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// Which sessions <c>pilot say-all</c> tells: the filters, and <see cref="Pick"/> over a listing of rows. Pure, so the
/// Sessions page picks the rows it shows with the same rules the CLI does.
/// </summary>
/// <param name="All">Idle sessions too, not only the active (working or waiting) ones. Ended, failed and archived are never picked.</param>
/// <param name="Roles">
/// A <see cref="SessionRole"/>'s name (<c>worker</c>, <c>planner</c>, <c>reviewer</c>, <c>watchtower</c>, <c>pilot</c>, <c>adHoc</c>)
/// or <c>validator</c>, case-insensitive. Empty: every role but the two kept safe, validators and the watchtower, which are
/// picked only when named here.
/// </param>
/// <param name="Flights">Flight codes, case-insensitive; empty for any session, flying or not.</param>
/// <param name="Repos">
/// A repo's folder name (<c>pilot</c>) matching the row's <see cref="SessionRow.Repo"/>, or a checkout's path matching a
/// <see cref="SessionRow.Cwd"/> in or under it; empty for any.
/// </param>
public sealed record SayAllFilter(bool All = false, IReadOnlyList<string>? Roles = null, IReadOnlyList<string>? Flights = null, IReadOnlyList<string>? Repos = null)
{
    public const string Validator = "validator";

    /// <summary>
    /// A validator is a worker-named session ending <c>-validator</c> (<c>du1-pil5-node-validator</c>): there is no
    /// validator role on the wire, so it is told apart by its name, as the node names it.
    /// </summary>
    public static bool IsValidator(SessionRow row) => row.Name.EndsWith("-" + Validator, StringComparison.Ordinal);

    /// <summary>The roles a <c>--role</c> may name, as the CLI spells them.</summary>
    public static IReadOnlyList<string> RoleNames { get; } = [.. Enum.GetNames<SessionRole>().Select(n => n == nameof(SessionRole.AdHoc) ? "adHoc" : n.ToLowerInvariant()), Validator];

    /// <summary>The role a row has for the filter: <c>validator</c> for a validator, else its <see cref="SessionRole"/>'s name.</summary>
    public static string RoleOf(SessionRow row) => IsValidator(row) ? Validator : RoleNames[(int)row.Role];

    /// <summary>The <see cref="RoleNames"/> spelling of a <c>--role</c> value, or null when it names no role.</summary>
    public static string? NormalizeRole(string value) => RoleNames.FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a session in this state is told: working, waiting, and idle with <see cref="All"/>.</summary>
    public bool TellsState(SessionState state) => state is SessionState.Working or SessionState.Waiting || (All && state == SessionState.Idle);

    public bool Matches(SessionRow row)
    {
        if (row.Archived || !TellsState(row.State))
        {
            return false;
        }

        var role = RoleOf(row);
        if (Roles is { Count: > 0 })
        {
            if (!Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }
        else if (role is Validator or "watchtower")
        {
            return false;
        }

        if (Flights is { Count: > 0 } && (row.Flight is null || !Flights.Any(f => string.Equals(f, row.Flight, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return Repos is not { Count: > 0 } || Repos.Any(r => InRepo(row, r));
    }

    /// <summary>The rows the filter picks, in the listing's order.</summary>
    public static IReadOnlyList<SessionRow> Pick(IEnumerable<SessionRow> rows, SayAllFilter filter) => [.. rows.Where(filter.Matches)];

    private static bool InRepo(SessionRow row, string repo)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!repo.Contains('/') && !repo.Contains('\\'))
        {
            return string.Equals(row.Repo, repo, StringComparison.OrdinalIgnoreCase);
        }

        // A path: the checkout itself or any folder under it (its worktrees included, when they sit inside it).
        var root = Normalize(repo);
        var cwd = Normalize(row.Cwd);
        return cwd.Equals(root, comparison) || cwd.StartsWith(root + '/', comparison);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
