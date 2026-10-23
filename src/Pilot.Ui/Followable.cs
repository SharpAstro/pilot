using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// A button or a link: clickable, with the hand cursor over it, so anything that does something when clicked says so
/// before the click. A list row stays <see cref="Layout.Node.Clickable"/> with the host's arrow: picking a row is not
/// following anything.
/// </summary>
internal static class Followable
{
    public static Layout.Node Follows(this Layout.Node node, HitResult hit, Action<InputModifier> onClick) =>
        node.Clickable(hit, onClick, CursorKind.Pointer);

    /// <summary>The mark after a link's text: it opens elsewhere (a browser, a folder), not an action here.</summary>
    public const string Away = " ↗";
}
