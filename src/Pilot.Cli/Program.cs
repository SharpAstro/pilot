using Console.Lib;
using Pilot.Cli;
using Pilot.Commands;

// pilot v2's command line: what the controller, the watchtower, workers and scripts use against the node.
return await PilotCommand.RunAsync(args, Environment.CurrentDirectory, System.Console.Out, System.Console.Error, CancellationToken.None, console: true, rich: RenderAsync);

// --output-format auto or rich: Markdown rendered as mdcat renders it, when the terminal can show it. Auto asks the terminal
// only when standard output is one, so a pipe, a script or an agent reads plain text without waiting on a probe.
static async Task<bool> RenderAsync(string format, string markdown, TextWriter output, TextWriter error)
{
    MarkdownOutput.TryParseFormat(format, out var requested);
    var (resolved, probe) = await MarkdownOutput.ResolveFormatAsync(requested);
    if (resolved != OutputFormat.Rich)
    {
        return false;
    }
    await RenderMagnifiedAsync(markdown, output, new MarkdownOutputOptions(Probe: probe), error);
    return true;
}

// A marked copy of Console.Lib's MarkdownOutput.RenderAsync (src/Console.Lib/MarkdownOutput.cs), kept until
// MarkdownImageOptions (src/Console.Lib/MarkdownImageOptions.cs) takes a magnification cap that MarkdownRendererLalr's
// image block (src/Console.Lib/MarkdownRendererLalr.cs, FitWithin: "never upscale") honours. The only difference is that
// the image resolver is wrapped by ImageMagnifier, which enlarges a small image to the render width, up to 3x, within the
// mode's pixel budget (the same one the renderer fits down to). Upstream it into Console.Lib and delete this copy.
static async Task<bool> RenderMagnifiedAsync(string markdown, TextWriter output, MarkdownOutputOptions options, TextWriter error)
{
    var width = options.Width ?? ConsoleSize.GetWidth();
    var baseDir = options.BaseDir ?? Directory.GetCurrentDirectory();
    var (colorMode, theme) = MarkdownOutput.ResolveColorAndTheme(options.Color);

    var mathMode = options.Mode;
    MarkdownImageOptions? images = null;
    if (!options.Unicode && colorMode != ColorMode.None)
    {
        var probe = options.Probe ?? await MarkdownOutput.ProbeAsync();
        if (mathMode == null && probe.Available)
        {
            mathMode = probe.Sixel ? BoxRenderMode.Sixel : BoxRenderMode.Sextant;
        }
        images = MarkdownOutput.BuildImageOptions(probe, options.Mode, baseDir);
        if (images is not null)
        {
            images = Magnifying(images, width);
        }
    }

    var buffer = new StringWriter();
    try
    {
        MarkdownRenderer.Render(markdown, buffer, width, colorMode: colorMode, theme: theme, mathMode: mathMode,
            mathFontPath: options.MathFontPath, images: images, linkResolver: url => MarkdownOutput.ResolveLocalLink(url, baseDir));
    }
    catch (Exception ex)
    {
        error.WriteLine($"could not render markdown ({ex.Message}); emitting raw text.");
        output.Write(markdown);
        return false;
    }
    output.Write(buffer.ToString());
    return true;
}

// The image options with the resolver wrapped: the pixel budget per mode is MarkdownRendererLalr's (Sixel a cell's pixels
// per column and row, Sextant 2x3 per cell, HalfBlock 1x2).
static MarkdownImageOptions Magnifying(MarkdownImageOptions images, int width)
{
    var (maxPxW, maxPxH) = images.Mode switch
    {
        BoxRenderMode.Sixel => (width * images.CellPixelWidth, images.MaxRows * images.CellPixelHeight),
        BoxRenderMode.Sextant => (width * 2, images.MaxRows * 3),
        _ => (width, images.MaxRows * 2),
    };
    var resolve = images.Resolver;
    return images with { Resolver = src => ImageMagnifier.Magnify(resolve(src), Math.Max(1, maxPxW), Math.Max(1, maxPxH)) };
}
