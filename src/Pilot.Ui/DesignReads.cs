using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What the Designs page reads from the node: the list through the <c>designs</c> command and an open design through <c>design-show</c>, whose paths the
/// page's files are read from. A read runs on the pool, is kept for <see cref="Every"/> and asked for again once older, as <see cref="SpendReads"/> is.
/// </summary>
internal sealed class DesignReads(NodeClient client, string who, Action redraw)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    private DesignRow[]? _rows;
    private DateTimeOffset _rowsAt;
    private int _rowsBusy;

    private DesignPage? _page;
    private string? _pageId;
    private int? _pageVersion;
    private DateTimeOffset _pageAt;
    private int _pageBusy;

    /// <summary>The page as last read for the design and version asked for; null before the first answer or when none is open.</summary>
    public DesignPage? Page => Volatile.Read(ref _page);

    /// <summary>The screen as last read; null before the node's first answer.</summary>
    public DesignsScreen? Screen(string source, DateTimeOffset at, string? open) =>
        Volatile.Read(ref _rows) is { } rows ? new DesignsScreen(rows, source, at, open is not null && Page is { } p && p.Detail.Row.Id == open ? p : null) : null;

    /// <summary>Reads the list, unless that was done less than <see cref="Every"/> ago.</summary>
    public void AskList(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _rowsAt < Every || Interlocked.Exchange(ref _rowsBusy, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.Designs, Target = Verbs.NodeTarget, Who = who }, CancellationToken.None).ConfigureAwait(false);
                if (answer is { Success: true, Response: { Ok: true, Designs: { } list } })
                {
                    Volatile.Write(ref _rows, list.Rows);
                    redraw();
                }
            }
            catch (Exception)
            {
                // The node did not answer: the page keeps the list it had and asks again after Every.
            }
            finally
            {
                _rowsAt = DateTimeOffset.UtcNow;
                Volatile.Write(ref _rowsBusy, 0);
            }
        });
    }

    /// <summary>
    /// Sends <c>design-new</c> with <paramref name="brief"/> to the node the page reads (what <c>pilot design new</c> does) and says the made design's id, or
    /// the node's refusal. The list is read again at once so the new design is on it.
    /// </summary>
    public async Task<(string? Id, string? Error)> StartAsync(string brief, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.DesignNew, Target = Verbs.NodeTarget, Args = ["", "", brief], Who = who }, cancellationToken).ConfigureAwait(false);
            if (answer.Response is { Ok: true, Made: { Length: > 0 } id })
            {
                AskList(force: true);
                return (id, null);
            }
            return (null, answer.Response?.Error ?? answer.Error ?? "the node did not answer");
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Reads design <paramref name="id"/> and its version <paramref name="version"/> (null: the current one), unless that was done less than <see cref="Every"/> ago.</summary>
    public void AskDesign(string id, int? version, bool force = false)
    {
        if (!force && _pageId == id && _pageVersion == version && DateTimeOffset.UtcNow - _pageAt < Every || Interlocked.Exchange(ref _pageBusy, 1) == 1)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.DesignShow, Target = Verbs.DesignTarget(id), Who = who }, CancellationToken.None).ConfigureAwait(false);
                if (answer is { Success: true, Response: { Ok: true, Design: { } design } })
                {
                    Volatile.Write(ref _page, DesignPages.Load(design, version));
                    _pageId = id;
                    _pageVersion = version;
                    redraw();
                }
            }
            catch (Exception)
            {
                // The node did not answer: the page keeps what it had.
            }
            finally
            {
                _pageAt = DateTimeOffset.UtcNow;
                Volatile.Write(ref _pageBusy, 0);
            }
        });
    }
}
