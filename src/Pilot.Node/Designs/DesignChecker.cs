using System.Text;
using System.Text.Json;
using DIR.Lib.Prototyping;
using Pilot.Contracts;
using SharpAstro.Png;

namespace Pilot.Node.Designs;

/// <summary>
/// The check and the render of a design's draft (PIL1096), done by the node with DIR.Lib's <see cref="Prototype"/>: <c>Load</c> reads the draft's
/// <c>prototype.json</c> and says every problem with its JSON path, <c>Check</c> draws each screen at <see cref="DesignSizes"/> and reports what the
/// wireframe rules find, <c>Render</c> draws each screen at the design size. Nothing here uses reflection-based JSON (AOT).
/// </summary>
public static class DesignChecker
{
    /// <summary>The folder of a draft's renders, <c>draft/renders/</c>.</summary>
    public const string RendersFolderName = "renders";

    /// <summary>The stamp beside the renders: the hash of the prototype they were drawn from.</summary>
    public const string StampFile = "render.json";

    /// <summary>
    /// Every problem of the prototype at <paramref name="prototypeFile"/>, a line each: a loader problem as <c>&lt;json path&gt;: &lt;message&gt;</c>, a finding as
    /// <c>&lt;screen&gt; | &lt;width&gt;x&lt;height&gt; | &lt;rule&gt;: &lt;message&gt;</c>. Empty when the prototype is clean at every <see cref="DesignSizes"/>.
    /// </summary>
    public static string[] Problems(string prototypeFile)
    {
        var loaded = Load(prototypeFile, out var problems);
        if (loaded is null)
        {
            return problems;
        }
        try
        {
            return [.. Prototype.Check(loaded, DesignSizes.All).Select(Line).Distinct(StringComparer.Ordinal)];
        }
        catch (FileNotFoundException ex)
        {
            // No font on the machine: a check that drew no text would call everything clean, so it says so instead.
            return [$"the check cannot run: {ex.Message}"];
        }
    }

    private static string Line(PrototypeFinding f)
    {
        // A state variant (a when's name=value) is named after its screen; the default state is the screen alone.
        var screen = f.State == "default" || f.State.Length == 0 ? f.Screen : $"{f.Screen} [{f.State}]";
        return $"{screen} | {f.Width}x{f.Height} | {f.Finding.Rule}: {f.Finding.Message}";
    }

    /// <summary>The prototype at <paramref name="prototypeFile"/>, or null with <paramref name="problems"/> (a line each, with its JSON path) when it cannot be read.</summary>
    public static PrototypeDocument? Load(string prototypeFile, out string[] problems)
    {
        string json;
        try
        {
            json = File.ReadAllText(prototypeFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems = [$"$: {Path.GetFileName(prototypeFile)} cannot be read: {ex.Message}"];
            return null;
        }
        var result = Prototype.Load(json);
        if (result.Document is { } document)
        {
            problems = [];
            return document;
        }
        problems = [.. result.Problems.Select(p => $"{p.Path}: {p.Message}")];
        return null;
    }

    /// <summary>
    /// Draws every screen of <paramref name="document"/> at its design size into <paramref name="rendersFolder"/> as <c>&lt;screen id&gt;.png</c>, after clearing
    /// the PNGs of an earlier render, and stamps the folder with <paramref name="prototypeSha256"/>. Returns the screens' ids.
    /// </summary>
    public static string[] Render(PrototypeDocument document, string rendersFolder, string prototypeSha256)
    {
        Directory.CreateDirectory(rendersFolder);
        foreach (var old in Directory.EnumerateFiles(rendersFolder, "*.png"))
        {
            File.Delete(old);
        }
        var stamp = Path.Combine(rendersFolder, StampFile);
        if (File.Exists(stamp))
        {
            // A stamp is of what was drawn: while the PNGs are being remade there is none to trust.
            File.Delete(stamp);
        }
        var images = Prototype.Render(document, ((uint)document.Width, (uint)document.Height));
        foreach (var (screen, image) in images)
        {
            File.WriteAllBytes(Path.Combine(rendersFolder, screen + ".png"), PngWriter.Encode(image.Pixels, image.Width, image.Height));
        }
        File.WriteAllText(stamp, JsonSerializer.Serialize(new DesignRenderStamp { PrototypeSha256 = prototypeSha256 }, PilotJsonContext.Default.DesignRenderStamp), new UTF8Encoding(false));
        return [.. images.Select(i => i.Screen)];
    }

    /// <summary>The hash a folder of renders was stamped with, or null when there is no readable stamp.</summary>
    public static string? StampOf(string rendersFolder)
    {
        try
        {
            var file = Path.Combine(rendersFolder, StampFile);
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), PilotJsonContext.Default.DesignRenderStamp)?.PrototypeSha256 : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
