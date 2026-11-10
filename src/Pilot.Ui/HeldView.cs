using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The held grants as the dashboard reads them: a node's token is found by its machine name (<c>SE1</c>), not the address a row was heard at, so a node
/// that changed network keeps its grant, its Held mark and its steering. An entry of the old file has no name and is found by its address.
/// </summary>
internal sealed class HeldView(IReadOnlyList<HeldGrant> entries)
{
    public static readonly HeldView None = new([]);

    public IReadOnlyList<HeldGrant> Entries => entries;

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The node's machine name: its own word (<c>MachinePrefix</c>) once heard, else the machine its beacon announced; null before either.</summary>
    public static string? NameOf(NodeSummary summary, string? announcedMachine) =>
        summary.Node is { MachinePrefix.Length: > 0 } node ? node.MachinePrefix : announcedMachine is { Length: > 0 } ? announcedMachine : null;

    /// <summary>The entry for the node called <paramref name="name"/> (when known), else the one whose address is <paramref name="where"/>.</summary>
    public HeldGrant? Find(string where, string? name) =>
        (name is null ? null : entries.FirstOrDefault(e => Same(e.Name, name))) ?? entries.FirstOrDefault(e => e.Address.Length > 0 && Same(e.Address, where));

    /// <summary>
    /// The nodes to watch: every held entry at its hint, except one whose name the LAN announces at another address (that node moved, and is
    /// watched where it is now), and every node heard on the LAN with its machine name.
    /// </summary>
    internal static IEnumerable<(string Endpoint, string? Name, string? Machine)> Known(IReadOnlyList<HeldGrant> entries, IReadOnlyList<LanNode> lan) =>
        entries.Where(e => e.Address.Length > 0 && !lan.Any(n => Same(n.Machine, e.Name) && !Same(n.Endpoint, e.Address)))
            .Select(e => (e.Address, (string?)null, e.Name))
            .Concat(lan.Select(n => (n.Endpoint, (string?)n.Name, n.Machine)));

    public bool Holds(string where, string? name) => Find(where, name) is not null;

    public string? TokenFor(string where, string? name) => Find(where, name)?.Token;

    /// <summary>
    /// The address of an old (unnamed) entry that a node of <paramref name="name"/> at <paramref name="where"/> may have moved from: none when the node has an
    /// entry, or when the old address answers (is in <paramref name="reached"/>) or was already offered to another row (<paramref name="spoken"/>).
    /// </summary>
    public static string? OldAddress(IReadOnlyList<HeldGrant> entries, string where, string name, ISet<string> reached, ISet<string> spoken)
    {
        if (entries.Any(e => Same(e.Name, name) || Same(e.Address, where)))
        {
            return null;
        }
        return entries.FirstOrDefault(e => e.Name is null && e.Address.Length > 0 && !reached.Contains(e.Address) && !spoken.Contains(e.Address))?.Address;
    }
}
