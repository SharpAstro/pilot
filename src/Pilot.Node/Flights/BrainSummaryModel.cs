using System.Text.Json;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The summary's call asked of the brain (PIL855): the same fixed prompt, answered as the JSON object <c>{"summary": "&lt;the markdown&gt;"}</c> on
/// the brain's stdin, so no process starts and the brain's warm cache and its knowledge of the fleet answer it. When the brain is down, busy
/// with the person, late, or answers in no form the node can read, the old one-off call (<paramref name="fallback"/>) runs instead, so a
/// summary is never blocked on it.
/// </summary>
public sealed class BrainSummaryModel(IBrain brain, ISummaryModel fallback, Action<string>? log = null) : ISummaryModel
{
    /// <summary>How long the brain has to answer before the old call runs.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The JSON object the brain answers with.</summary>
    public const string AnswerShape = """{"summary": "<the whole answer, in exactly the format above, as one JSON string>"}""";

    /// <inheritdoc />
    public string? WatchtowerDown => brain.Unavailable;

    public async Task<SummaryAnswer> AskAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            return await AskBrainAsync(prompt, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"summary: {ex.Message}; the old call runs");
        }
        return await fallback.AskAsync(prompt, cancellationToken);
    }

    /// <summary>
    /// Asks the brain and nothing else, for the summary that follows changes (PIL903): never the paid fallback.
    /// Throws <see cref="InvalidOperationException"/>, saying why, when the brain is down or answers no summary.
    /// </summary>
    public Task<SummaryAnswer> AskWatchtowerAsync(string prompt, CancellationToken cancellationToken) =>
        brain.Unavailable is { } down
            ? throw new InvalidOperationException($"the watchtower is down ({down})")
            : AskBrainAsync(prompt, cancellationToken);

    private async Task<SummaryAnswer> AskBrainAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await brain.AskAsync("summary", $"{prompt}\n\nAnswer as this JSON object: {AnswerShape}", Timeout, cancellationToken);
            if (reply.Answer.TryGetProperty("summary", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                // Its tokens ride on the brain's own session; no call of its own, so none to count.
                return new SummaryAnswer(text.GetString()!.Trim(), 0, null);
            }
        }
        catch (BrainDownException ex)
        {
            throw new InvalidOperationException($"the brain could not be asked ({ex.Message})", ex);
        }
        throw new InvalidOperationException("the brain answered with no summary");
    }
}
