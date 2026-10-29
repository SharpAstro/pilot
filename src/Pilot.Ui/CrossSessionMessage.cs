using System.Text.RegularExpressions;

namespace Pilot.Ui;

/// <summary>
/// A message relayed between sessions arrives as the recipient's prompt wrapped in Claude Code's envelope,
/// <c>&lt;cross-session-message from="…" from-name="controller" from-mode="…"&gt;words (Sent through the pilot node. …)&lt;/cross-session-message&gt;</c>.
/// A transcript shows it as the message it is: who sent it, and their words, without the envelope or the relay's footer
/// (the owner, 2026-10-29: "why is it still this weird cross xml thing?").
/// </summary>
internal static partial class CrossSessionMessage
{
    /// <summary>The name a message from the person comes from: the node relays their words as the controller's.</summary>
    public const string Controller = "controller";

    /// <summary>The sender's name and the words, when <paramref name="text"/> is a relayed message; else null.</summary>
    public static (string From, string Body)? Parse(string text)
    {
        var match = Envelope().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }
        var body = match.Groups["body"].Value.ReplaceLineEndings("\n");
        // The relay's own footer: how to answer, which is for the recipient, not for a reader of the transcript.
        body = Footer().Replace(body, "").Trim();
        return (match.Groups["name"].Value, body);
    }

    [GeneratedRegex("""^<cross-session-message\b[^>]*\bfrom-name="(?<name>[^"]*)"[^>]*>(?<body>.*)</cross-session-message>$""", RegexOptions.Singleline)]
    private static partial Regex Envelope();

    [GeneratedRegex(@"\n*\((?:Sent through the pilot node|Sent from)[^)]*\)\s*$", RegexOptions.Singleline)]
    private static partial Regex Footer();
}
