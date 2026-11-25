namespace Pilot.Node;

/// <summary>
/// The page each client on the event stream says it shows (<see cref="Contracts.ClientPage"/>), kept for as long as its stream
/// is open: <see cref="EventHub"/> says and forgets, and whoever does more while someone looks (the pull request watch) counts.
/// A client that has said nothing shows no page.
/// </summary>
public sealed class PageViews
{
    private readonly object _gate = new();
    private readonly Dictionary<object, string> _pages = new(ReferenceEqualityComparer.Instance);

    /// <summary>Raised, outside the lock, when the clients showing a page go from none to some (true) or from some to none (false).</summary>
    public event Action<string, bool>? Changed;

    /// <summary>How many clients show <paramref name="page"/> now.</summary>
    public int Count(string page)
    {
        lock (_gate)
        {
            return CountOf(page);
        }
    }

    /// <summary>
    /// True while any client shows any page: a GUI or TUI window, local or over the LAN. A CLI command or an MCP call says no page,
    /// so it never counts.
    /// </summary>
    public bool Any
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count > 0;
            }
        }
    }

    /// <summary>Sets what <paramref name="client"/> shows; null says none.</summary>
    public void Say(object client, string? page)
    {
        var changes = new List<(string Page, bool Shown)>(2);
        lock (_gate)
        {
            var before = _pages.GetValueOrDefault(client);
            if (before == page)
            {
                return;
            }
            var joinedBefore = page is null ? 0 : CountOf(page);
            if (page is null)
            {
                _pages.Remove(client);
            }
            else
            {
                _pages[client] = page;
            }
            if (before is not null && CountOf(before) == 0)
            {
                changes.Add((before, false));
            }
            if (page is not null && joinedBefore == 0)
            {
                changes.Add((page, true));
            }
        }
        foreach (var (changed, shown) in changes)
        {
            Changed?.Invoke(changed, shown);
        }
    }

    private readonly HashSet<object> _cards = new(ReferenceEqualityComparer.Instance);

    /// <summary>True while any client says it shows the cards (its For you bar or Notifications page is open): normal desktop toasts wait (#1356).</summary>
    public bool AnyShowsCards
    {
        get
        {
            lock (_gate)
            {
                return _cards.Count > 0;
            }
        }
    }

    /// <summary>
    /// Sets whether <paramref name="client"/> shows the cards. Kept beside the page, never in it, so the pages the PR watch and the machine
    /// sampler read are untouched by it.
    /// </summary>
    public void SayCards(object client, bool shows)
    {
        lock (_gate)
        {
            if (shows) _cards.Add(client); else _cards.Remove(client);
        }
    }

    /// <summary>Forgets what <paramref name="client"/> showed: its stream closed.</summary>
    public void Forget(object client)
    {
        Say(client, null);
        SayCards(client, false);
    }

    private int CountOf(string page) => _pages.Values.Count(p => p == page);
}
