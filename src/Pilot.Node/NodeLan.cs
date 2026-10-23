using System.Collections.Concurrent;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Node;

/// <summary>
/// The node on the LAN (<c>--lan</c>): seeing is free, a command needs a grant. A client on another machine asks
/// (<c>POST /api/v1/grants</c>), which rings a card here with Allow and Deny; an Allow gives it a grant whose bearer token
/// it reads back once, and every command it sends carries the token and goes out under the grant's label. The rules are
/// LAN.Lib's (<see cref="LanInvites{T}"/>: one ask at a time, alive only while its asker polls; <see cref="LanGrants"/>:
/// tokens kept as hashes, checked in constant time, revoked), lifted out of the chess lobby so pilot keeps them rather
/// than a copy.
/// </summary>
public sealed class NodeLan(NodeState state, NodeOptions options, TimeProvider time) : IDisposable
{
    public const string AskedKind = "grant-asked";

    /// <summary>Where a request from the LAN keeps the label of the grant it came with, for the commands route.</summary>
    public const string GrantLabelKey = "pilot.grant";

    /// <summary>How long an ask lives without its asker reading its ticket: a client polls every second or two.</summary>
    public static readonly TimeSpan PresenceLapse = TimeSpan.FromSeconds(30);

    private readonly LanInvites<string> _asks = new(time, PresenceLapse);
    private readonly LanGrants _grants = new(new LanGrantFile(Path.Combine(options.DataFolder, "grants.json")), time);
    private readonly ConcurrentDictionary<string, string> _issued = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loaded = new(1, 1);
    private bool _isLoaded;
    private ITimer? _sweep;

    /// <summary>How often an ask whose asker stopped reading its ticket is looked for, so its card closes with it.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Closes the ask card once no ask waits: an asker that gave up (pilot connect stops after a while) never reads its
    /// ticket again, so without this its card stayed open, and its Allow pointed at an ask long gone (SE1's ask on DE1,
    /// 2026-10-23: refused an hour later, and a second ask folded into the stale card). Started on the first ask.
    /// </summary>
    public void Sweep()
    {
        if (_asks.Pending is null && state.Cards(openOnly: true).Any(c => c.Kind == AskedKind))
        {
            state.CloseCardsAbout(Verbs.NodeTarget, "the asker went away", AskedKind);
        }
    }

    /// <summary>Whether a request came over TCP, from the LAN (or this machine's loopback), rather than the node's socket.</summary>
    public static bool IsRemote(HttpContext context) => context.Connection.RemoteIpAddress is not null;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_isLoaded)
        {
            return;
        }
        await _loaded.WaitAsync(cancellationToken);
        try
        {
            if (!_isLoaded)
            {
                await _grants.LoadAsync(cancellationToken);
                _isLoaded = true;
            }
        }
        finally
        {
            _loaded.Release();
        }
    }

    /// <summary>The grant a request's bearer token names, or null.</summary>
    public async Task<LanGrant?> VerifyAsync(HttpContext context)
    {
        await EnsureLoadedAsync(context.RequestAborted);
        return _grants.TryVerify(NodeAccessGate.BearerToken(context), out var grant) ? grant : null;
    }

    /// <summary>An ask for control, as <paramref name="label"/>: its ticket, or null while another ask waits.</summary>
    public GrantTicket? Ask(string label)
    {
        label = label.Trim();
        if (!_asks.TryOffer(label, label, out var invite))
        {
            return null;
        }
        // A card of an earlier ask is the earlier ask's: its Allow names that ask. Closed, so this one gets its own.
        state.CloseCardsAbout(Verbs.NodeTarget, "a newer ask came", AskedKind);
        _sweep ??= time.CreateTimer(_ => Sweep(), null, SweepEvery, SweepEvery);
        state.RaiseCard(CardBin.Ring, AskedKind, Verbs.NodeTarget, $"{label} asks to fly flights on this node",
            $"from the LAN; an Allow lets it run any command here as {label}/…, until revoked (pilot grants)",
            [
                new CardChoice { Label = "Allow", Verb = Verbs.AnswerGrant, Target = Verbs.NodeTarget, Args = [invite.Id, "allow"] },
                new CardChoice { Label = "Deny", Verb = Verbs.AnswerGrant, Target = Verbs.NodeTarget, Args = [invite.Id, "deny"] },
            ]);
        return new GrantTicket { Id = invite.Id, Outcome = "pending" };
    }

    /// <summary>Where an ask stands; its asker is still there, as it reads. The token comes with the first read after an Allow.</summary>
    public GrantTicket Ticket(string id)
    {
        _asks.Seen(id);
        var outcome = _asks.OutcomeOf(id);
        if (outcome is LanInviteOutcome.Withdrawn or LanInviteOutcome.Unknown && _asks.Pending is null)
        {
            state.CloseCardsAbout(Verbs.NodeTarget, "the asker went away", AskedKind);
        }
        return new GrantTicket
        {
            Id = id,
            Outcome = outcome.ToString().ToLowerInvariant(),
            Token = outcome == LanInviteOutcome.Accepted && _issued.TryRemove(id, out var token) ? token : null,
        };
    }

    /// <summary><c>answer-grant node &lt;id&gt; allow|deny</c>, from this machine: null when done, else why not.</summary>
    public async Task<string?> AnswerAsync(string id, bool allow, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (_asks.Answer(id, allow) is not { } invite)
        {
            return "that ask is not waiting any more: its asker went away, or it was answered";
        }
        if (allow)
        {
            var issued = await _grants.GrantAsync(invite.Payload, cancellationToken);
            _issued[id] = issued.Token;
        }
        return null;
    }

    /// <summary><c>revoke-grant node &lt;grant id&gt;</c>: null when revoked, else why not.</summary>
    public async Task<string?> RevokeAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return await _grants.RevokeAsync(id, cancellationToken) ? null : $"there is no grant {id}";
    }

    public async Task<GrantInfo[]> GrantsAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return [.. _grants.All.Select(g => new GrantInfo { Id = g.Id, Label = g.Label, GrantedAt = g.GrantedAt })];
    }

    public void Dispose()
    {
        _sweep?.Dispose();
        _grants.Dispose();
        _loaded.Dispose();
    }
}
