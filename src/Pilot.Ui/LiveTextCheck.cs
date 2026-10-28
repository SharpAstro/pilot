using System.Collections.Immutable;
using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The running GUI's run of <see cref="TextOverlap"/>: every page hands it the nodes it arranged, and it checks them once for each
/// page, size and DPI (a steady window is checked once, never each frame), over a bounded number of text leaves. A page whose text
/// is drawn over text raises one for-you card on its node, at most one for a page in a run, through <see cref="Send"/>; with
/// nothing to send through (no node connected) it is only <see cref="Log"/>ged.
/// </summary>
internal sealed class LiveTextCheck
{
    /// <summary>Text leaves a check reads: the pairs it compares grow with the square, and a page with more is checked on its first ones.</summary>
    public const int MaxLeaves = 400;

    // Where the last check of each page was: the size is the arranged root's, so a resize or a new DPI is a new check.
    private readonly Dictionary<string, (int Width, int Height, float Dpi)> _checked = [];
    private readonly HashSet<string> _raised = [];

    /// <summary>Who the commands say they are from (the host's: the node shows it on the card's Handled line).</summary>
    public string Who { get; set; } = "gui";

    /// <summary>Takes the raise command; null where there is no node to tell (the check then only logs).</summary>
    public Action<CommandRequest>? Send { get; set; }

    /// <summary>A line for the console, for every fault found.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>How many pages were checked: what a test reads to see that a steady window is not checked again.</summary>
    public int Checks { get; private set; }

    /// <summary>
    /// A page was rendered: its <paramref name="arranged"/> nodes at <paramref name="dpi"/>. Checks them unless this page was already
    /// checked at this size and DPI, and raises its card on the first fault found on a page this run has not raised one for.
    /// </summary>
    public void Observe(string page, ImmutableArray<Layout.ArrangedNode<float>> arranged, float dpi)
    {
        if (arranged.IsDefaultOrEmpty)
        {
            return;
        }
        var root = arranged[0].Bounds;
        var key = ((int)MathF.Round(root.Width), (int)MathF.Round(root.Height), dpi);
        if (_checked.TryGetValue(page, out var last) && last == key)
        {
            return;
        }
        _checked[page] = key;
        Checks++;

        var faults = TextOverlap.Faults(arranged, maxLeaves: MaxLeaves);
        if (faults.Count == 0)
        {
            return;
        }
        var where = $"page {key.Item1}x{key.Item2} at DPI {dpi.ToString("0.##", CultureInfo.InvariantCulture)}";
        var detail = $"{faults[0].Line}{(faults.Count > 1 ? $" (+{faults.Count - 1} more)" : "")}; {where}";
        Log?.Invoke($"GUI drew text over text on {page}: {detail}");
        if (Send is null || !_raised.Add(page))
        {
            return;
        }
        Send(new CommandRequest
        {
            Verb = Verbs.Raise,
            Target = Verbs.NodeTarget,
            Args = ["for-you", $"GUI drew text over text on {page}", detail],
            Who = Who,
        });
    }
}
