using System.Text;
using System.Text.Json;
using DIR.Lib;
using Pilot.Contracts;
using SharpAstro.Png;

namespace Pilot.Gui;

/// <summary>Where the rows come from, and the one frame the CPU renders of them.</summary>
internal static class SessionsCapture
{
    /// <summary>The UI face, shipped beside the binary (Fonts\DejaVuSans.ttf).</summary>
    public static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    /// <summary>The contract's sample snapshot, shipped beside the binary: what the GUI shows until it reads a node.</summary>
    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot.sample.json");

    /// <summary>Reads a snapshot file: a node's <see cref="SnapshotEvent"/> as the event stream sends it.</summary>
    public static SnapshotEvent LoadSnapshot(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), PilotJsonContext.Default.NodeEvent) switch
        {
            SnapshotEvent snapshot => snapshot,
            var other => throw new InvalidDataException($"{path} holds a {other?.GetType().Name ?? "null"}, not a snapshot."),
        };

    /// <summary>
    /// The screen for a snapshot file. A file is a moment, so "3m ago" counts from the moment the node made it, and
    /// the same file always reads the same.
    /// </summary>
    public static SessionsScreen ScreenFor(SnapshotEvent snapshot, string source) =>
        new(SessionLine.Build(snapshot.Sessions, snapshot.At), source, snapshot.At);

    /// <summary>Paints <paramref name="screen"/> once on the CPU and returns the view, its regions current.</summary>
    /// <param name="canSay">Whether to draw the say bar, as the window does on a live node.</param>
    public static SessionsView<RgbaImage> Render(RgbaImageRenderer renderer, SessionsScreen screen, float dpiScale = 1f, bool canSay = false)
    {
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = FontPath, DpiScale = dpiScale, CanSay = canSay };
        view.Render(screen, new RectF32(0, 0, renderer.Width, renderer.Height));
        return view;
    }

    /// <summary>Renders one frame to <paramref name="pngPath"/> and its rows to <c>&lt;pngPath&gt;.rows.txt</c>; returns the dump's path.</summary>
    public static string Write(SessionsScreen screen, string pngPath, int width, int height, bool canSay = false)
    {
        using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        Render(renderer, screen, canSay: canSay);
        if (Path.GetDirectoryName(Path.GetFullPath(pngPath)) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(pngPath, PngWriter.Encode(renderer.Surface.Pixels, width, height));
        var rowsPath = pngPath + ".rows.txt";
        File.WriteAllText(rowsPath, RowsText(screen), new UTF8Encoding(false));
        return rowsPath;
    }

    /// <summary>One line per row, in drawing order, the fields as shown.</summary>
    public static string RowsText(SessionsScreen screen)
    {
        var text = new StringBuilder();
        foreach (var line in screen.Lines)
        {
            text.Append(line.ToDumpLine()).Append('\n');
        }

        return text.ToString();
    }
}
