using System.Globalization;

namespace Pilot.Gui;

/// <summary>
/// The command line: <c>pilot-gui [--snapshot &lt;file.json&gt;] [--capture &lt;out.png&gt; [--size 1280x800]] [--exit-after 5s]</c>.
/// </summary>
internal sealed record GuiOptions
{
    public const string Usage =
        "usage: pilot-gui [--snapshot <file.json>] [--capture <out.png> [--size <W>x<H>]] [--exit-after <n>s|<n>ms|<n>m]";

    /// <summary>The snapshot to show instead of a node; null shows the bundled sample.</summary>
    public string? Snapshot { get; init; }

    /// <summary>Render one frame on the CPU to this PNG (and a sibling <c>.rows.txt</c>), then exit without a window.</summary>
    public string? Capture { get; init; }

    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 800;

    /// <summary>Close the window by itself after this long, so a check needs no hands.</summary>
    public TimeSpan? ExitAfter { get; init; }

    /// <summary>Parses <paramref name="args"/>; on a mistake returns null and says what in <paramref name="error"/>.</summary>
    public static GuiOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        var options = new GuiOptions();
        error = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                error = Usage;
                return null;
            }

            if (i + 1 >= args.Count || arg is not ("--snapshot" or "--capture" or "--size" or "--exit-after"))
            {
                error = i + 1 >= args.Count && arg.StartsWith("--", StringComparison.Ordinal)
                    ? $"{arg} needs a value. {Usage}"
                    : $"unknown argument '{arg}'. {Usage}";
                return null;
            }

            var value = args[++i];
            switch (arg)
            {
                case "--snapshot":
                    options = options with { Snapshot = value };
                    break;
                case "--capture":
                    options = options with { Capture = value };
                    break;
                case "--size":
                    if (!TryParseSize(value, out var width, out var height))
                    {
                        error = $"--size takes <W>x<H>, as in 1280x800, not '{value}'.";
                        return null;
                    }
                    options = options with { Width = width, Height = height };
                    break;
                default:
                    if (!TryParseDuration(value, out var after))
                    {
                        error = $"--exit-after takes a duration such as 5s, 500ms or 2m, not '{value}'.";
                        return null;
                    }
                    options = options with { ExitAfter = after };
                    break;
            }
        }

        return options;
    }

    private static bool TryParseSize(string value, out int width, out int height)
    {
        width = height = 0;
        var parts = value.Split('x', 'X');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width is > 0 and <= 16384 && height is > 0 and <= 16384;
    }

    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        var (number, unit) = value.EndsWith("ms", StringComparison.Ordinal) ? (value[..^2], 1d)
            : value.EndsWith('s') ? (value[..^1], 1000d)
            : value.EndsWith('m') ? (value[..^1], 60_000d)
            : (value, 1000d);
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        duration = TimeSpan.FromMilliseconds(amount * unit);
        return true;
    }
}
