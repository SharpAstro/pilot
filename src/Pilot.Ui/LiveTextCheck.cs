using System.Collections.Immutable;
using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The running GUI's run of <see cref="TextOverlap"/>: every page hands it the nodes it arranged, and it checks them once for each
/// page, size, DPI and set of words (a steady window showing the same words is checked once, never each frame; a page whose rows
/// arrive after its first frame is checked again), over a bounded number of text leaves. A page whose text
/// is drawn over text raises one for-you card on its node, at most one for a page in a run, through <see cref="Send"/>; with
/// nothing to send through (no node connected) it is only <see cref="Log"/>ged.
/// </summary>
internal sealed class LiveTextCheck
{
    /// <summary>Text leaves a check reads: the pairs it compares grow with the square, and a page with more is checked on its first ones.</summary>
    public const int MaxLeaves = 400;

    // Where the last check of each page was: the size is the arranged root's, so a resize or a new DPI is a new check, and so is a
    // change of the words on it: a page's first frame at a size is often empty or loading, and its rows arrive later at the same size.
    private readonly Dictionary<string, (int Width, int Height, float Dpi, int Content)> _checked = [];
    private readonly HashSet<string> _raised = [];
    // The fault (line, size and DPI) each page last logged: a page whose words change every frame says the same fault once, not at every change.
    private readonly Dictionary<string, string> _logged = [];

    /// <summary>The window's size in pixels, as the chrome last drew it: the card names it beside the page's own, which is the window less the rail and the panel. Null where the host gives none.</summary>
    public (int Width, int Height)? Window { get; set; }

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
        var key = ((int)MathF.Round(root.Width), (int)MathF.Round(root.Height), dpi, Content(arranged));
        if (_checked.TryGetValue(page, out var last) && last == key)
        {
            return;
        }
        _checked[page] = key;
        Checks++;

        var faults = TextOverlap.Faults(arranged, maxLeaves: MaxLeaves);
        if (faults.Count == 0)
        {
            _logged.Remove(page);
            return;
        }
        var dpiText = dpi.ToString("0.##", CultureInfo.InvariantCulture);
        var where = Window is { } window
            ? $"window {window.Width}x{window.Height}, page {key.Item1}x{key.Item2} at DPI {dpiText}"
            : $"page {key.Item1}x{key.Item2} at DPI {dpiText}";
        var detail = $"{faults[0].Line}{(faults.Count > 1 ? $" (+{faults.Count - 1} more)" : "")}; {where}";
        if (!_logged.TryGetValue(page, out var said) || said != detail)
        {
            _logged[page] = detail;
            Log?.Invoke($"GUI drew text over text on {page}: {detail}");
        }
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

    // The words of the text leaves a check reads (the first MaxLeaves), and how many there are: what a page says changes when its
    // rows arrive, and the check must look again at the size it already looked at.
    private static int Content(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        var hash = new HashCode();
        var leaves = 0;
        foreach (var (node, _) in arranged)
        {
            if (node is Layout.Node.Leaf { Content: Layout.Content.Text { Value.Length: > 0 } text } && !string.IsNullOrWhiteSpace(text.Value))
            {
                hash.Add(text.Value, StringComparer.Ordinal);
                if (++leaves >= MaxLeaves)
                {
                    break;
                }
            }
        }
        hash.Add(leaves);
        return hash.ToHashCode();
    }
}
