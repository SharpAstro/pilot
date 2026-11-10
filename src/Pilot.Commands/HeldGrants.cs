using System.Collections.Concurrent;
using System.Text.Json;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>What <see cref="HeldGrants.FindAsync"/> found for an address: the node's machine name when known, and the token held for it.</summary>
public sealed record HeldLookup(string? Name, string? Token);

/// <summary>
/// The grants this user holds for nodes on the LAN (<c>client-grants.json</c>), kept by the node's machine name (its
/// <c>MachinePrefix</c>, <c>SE1</c>) with the last address it answered at as a hint: a node that changes network keeps its grant.
/// An address is looked up by its hint first, and otherwise by asking the node there what it is called
/// (<c>GET /api/v1/node</c>, no grant needed). The old file, <c>host:port: token</c>, is read as it stands, and an entry of it is
/// re-keyed by name the first time its address answers. As an <see cref="ILanHeldTokens"/> it is what <see cref="LanAsk"/> saves through.
/// </summary>
public sealed class HeldGrants(string path) : ILanHeldTokens
{
    /// <summary>How long a node has to say its name before the address counts as not answering.</summary>
    public static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(3);

    // address -> machine name, for the process: a lookup does not cost a request on every call.
    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    private readonly HeldGrantFile _file = new(path);

    /// <summary>Forgets the names learnt from the nodes this process asked (a test's, after it moved a node).</summary>
    internal static void ForgetNames() => Names.Clear();

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every entry held.</summary>
    public IReadOnlyList<HeldGrant> Entries => _file.Read();

    /// <summary>The entry held under <paramref name="name"/> (compared without regard to case), or null.</summary>
    public HeldGrant? ByName(string name) => _file.Read().FirstOrDefault(e => Same(e.Name, name));

    /// <summary>The token whose hint (or, in an old file, key) is <paramref name="endpoint"/>: no request is made.</summary>
    public string? TokenFor(string endpoint) => AtAddress(_file.Read(), endpoint)?.Token;

    private static HeldGrant? AtAddress(IReadOnlyList<HeldGrant> entries, string endpoint) =>
        entries.FirstOrDefault(e => e.Name is not null && Same(e.Address, endpoint)) ?? entries.FirstOrDefault(e => e.Name is null && Same(e.Address, endpoint));

    /// <summary>The hint of every entry, lower-cased and in ordinal order.</summary>
    public IReadOnlyList<string> Endpoints => [.. _file.Read().Where(e => e.Address.Length > 0).Select(e => e.Address).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>
    /// Keeps <paramref name="token"/> for the node at <paramref name="endpoint"/> whose name is not known here: over the entry whose hint it is,
    /// else as an old address-keyed one, which the first lookup that can ask the node re-keys by name. <c>pilot connect</c> saves by name (<see cref="SaveNamed"/>).
    /// </summary>
    public void Save(string endpoint, string token) => _file.Update(entries =>
    {
        var address = endpoint.ToLowerInvariant();
        var at = AtAddress(entries, address);
        if (at is not null)
        {
            entries[entries.IndexOf(at)] = at with { Token = token };
        }
        else
        {
            entries.Add(new HeldGrant(null, address, token));
        }
        return true;
    });

    /// <summary>
    /// Keeps <paramref name="token"/> for the node called <paramref name="name"/>, last answering at <paramref name="endpoint"/>, over any entry of that
    /// name whatever its address was, and over an old entry of that address: one entry per node.
    /// </summary>
    public void SaveNamed(string name, string endpoint, string token) => _file.Update(entries =>
    {
        var address = endpoint.ToLowerInvariant();
        entries.RemoveAll(e => Same(e.Name, name) || (e.Name is null && Same(e.Address, address)));
        entries.Add(new HeldGrant(name, address, token));
        return true;
    });

    /// <summary>Drops the entry named or addressed <paramref name="key"/>; false when none was held.</summary>
    public bool Forget(string key)
    {
        var dropped = false;
        _file.Update(entries =>
        {
            dropped = entries.RemoveAll(e => Same(e.Name, key) || Same(e.Address, key)) > 0;
            return dropped;
        });
        return dropped;
    }

    /// <summary>A store for <see cref="LanAsk"/> that keeps what it is given under <paramref name="name"/>, the node's machine name, whatever address it is saved at.</summary>
    public ILanHeldTokens Named(string name) => new NamedStore(this, name);

    private sealed class NamedStore(HeldGrants grants, string name) : ILanHeldTokens
    {
        public string? TokenFor(string endpoint) => grants.ByName(name)?.Token;

        public IReadOnlyList<string> Endpoints => grants.Endpoints;

        public void Save(string endpoint, string token) => grants.SaveNamed(name, endpoint, token);

        public bool Forget(string endpoint) => grants.Forget(name);
    }

    /// <summary>
    /// What the node at <paramref name="endpoint"/> is called, from <c>GET /api/v1/node</c> (no grant needed); null when it does not answer in
    /// <see cref="AskTimeout"/>. Kept for the process once heard.
    /// </summary>
    public static async Task<string?> NameAtAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (Names.TryGetValue(endpoint, out var known))
        {
            return known;
        }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(AskTimeout);
            using var client = NodeClient.Remote(endpoint);
            var info = await client.GetNodeAsync(limit.Token).ConfigureAwait(false);
            var name = info.MachinePrefix is { Length: > 0 } prefix ? prefix : info.MachineName;
            if (name.Length == 0)
            {
                return null;
            }
            Names[endpoint] = name;
            return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// The grant to send the node at <paramref name="endpoint"/>. An address that is an entry's hint is that entry's; otherwise the node is asked its name
    /// and the entry of that name is found, its hint updated to this address; an old address-keyed entry is re-keyed by name the same way. A node that does not
    /// answer leaves the file as it is (an old entry is still sent its token). Never another entry's token.
    /// </summary>
    public async Task<HeldLookup> FindAsync(string endpoint, CancellationToken cancellationToken)
    {
        var address = endpoint.ToLowerInvariant();
        var entries = _file.Read();
        var at = AtAddress(entries, address);
        if (at is { Name: not null })
        {
            return new HeldLookup(at.Name, at.Token);
        }
        var name = await NameAtAsync(address, cancellationToken).ConfigureAwait(false);
        if (name is null)
        {
            return new HeldLookup(null, at?.Token);
        }
        HeldGrant? found = null;
        _file.Update(current =>
        {
            var legacy = current.FirstOrDefault(e => e.Name is null && Same(e.Address, address));
            found = current.FirstOrDefault(e => Same(e.Name, name));
            if (found is null && legacy is null)
            {
                return false;
            }
            // The entry of this name is the one kept; an old entry of this address is the same node's, so it goes, or becomes the named one.
            var kept = (found ?? legacy!) with { Name = name, Address = address };
            current.RemoveAll(e => Same(e.Name, name) || (e.Name is null && Same(e.Address, address)));
            current.Add(kept);
            found = kept;
            return true;
        });
        return new HeldLookup(name, found?.Token);
    }
}
