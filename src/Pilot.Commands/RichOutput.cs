namespace Pilot.Commands;

/// <summary>
/// A console host's way of showing Markdown, for <c>--output-format</c> auto or rich: given the value asked for, it writes
/// <paramref name="markdown"/> rendered for the terminal and says true, or writes nothing and says false when the format
/// settles on plain (auto on a pipe, under <c>NO_COLOR</c>, or on a terminal without Sixel). The CLI's is Console.Lib's
/// <c>MarkdownOutput</c>; the command tree itself stays free of Console.Lib, whose <c>Console</c> namespace would hide
/// <c>System.Console</c> in every project that takes the tree.
/// </summary>
public delegate Task<bool> RichOutput(string format, string markdown, TextWriter output, TextWriter error);

/// <summary>
/// Where a command hands a picture to its host, beside its text: the MCP server turns each into an image content item
/// (PIL1186). Gets the media type, the bytes and the line that names the picture. Null for the CLI.
/// </summary>
public delegate Task PictureSink(string mimeType, byte[] bytes, string line);
