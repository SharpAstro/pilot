using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A card's button as either host (the GUI's window, pilot-tui's terminal) sends it: one verb on the card's node, and whatever became
/// of it said on the card. Every outcome reaches <see cref="CardsView{TSurface}.NoteRefused(string, string?, string?)"/>, a refusal, a
/// node only watched, a node that did not answer, or the node's ok: a Dismiss hides its card from the click, and only that brings a
/// refused one back (PIL853).
/// </summary>
internal static class CardCommands
{
    /// <summary>
    /// Sends <paramref name="verb"/> on <paramref name="card"/> through <paramref name="send"/>, the card's node's client (null when this
    /// window only watches that node, which <paramref name="watchOnly"/> says). Returns the refusal in the node's words, or null when it took it.
    /// </summary>
    public static async Task<string?> SendAsync<TSurface>(
        CardsView<TSurface> cards, string verb, CardRow card, string[] args, string who,
        Func<CommandRequest, CancellationToken, Task<ResponseEnvelope<CommandResult>>>? send, string watchOnly,
        CancellationToken cancellationToken, Action<ResponseEnvelope<CommandResult>>? answered = null)
    {
        // Read now: by the time the node answers, the panel drawn holds other rows, and the card's node is no longer known by it.
        var node = cards.NodeOf(card)?.Name;
        string? refused;
        if (send is null)
        {
            refused = watchOnly;
        }
        else
        {
            try
            {
                var envelope = await send(DashboardCommands.CardRequest(verb, card, args, who), cancellationToken);
                answered?.Invoke(envelope);
                refused = DashboardCommands.RefusalOf(envelope);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
            {
                // A node that did not answer took nothing: said on the card, which a Dismiss brings back.
                refused = $"the node did not answer: {ex.Message}";
            }
        }
        cards.NoteRefused(card.Id, node, refused);
        return refused;
    }
}
