namespace Pilot.Ui;

/// <summary>
/// The whole of a text that a narrow column may end in an ellipsis (PIL380): a leaf the layout squeezes (<c>WStar</c> with a
/// minimum of 0, a shrinking fixed column) is cut at the width it is given, so it carries its whole text as its tooltip, and the
/// person can always read what was cut. A leaf whose row already has a tooltip with the text in it (a <c>ChipBrief</c>) needs no
/// call: the tooltip of the innermost node wins, so a leaf tooltip would hide the row's.
/// </summary>
internal static class WholeOnHover
{
    /// <summary><paramref name="node"/> with <paramref name="text"/> as its tooltip, when there is any text.</summary>
    public static Layout.Node Whole(this Layout.Node node, string? text) =>
        string.IsNullOrEmpty(text) ? node : node.WithTooltip(text);
}
