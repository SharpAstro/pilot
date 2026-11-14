using System.Collections.Immutable;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// What every page's widget stands on: DIR.Lib's <see cref="PixelWidgetBase{TSurface}"/>, with the one hook the live text check
/// reads (<see cref="Arranged"/>): the page's arranged nodes, as <c>RenderLayout</c> arranged them and before it painted them.
/// </summary>
internal abstract class PilotWidgetBase<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    /// <summary>Told the arranged nodes each time the page is rendered (the chrome's <see cref="LiveTextCheck"/>); null when nobody looks.</summary>
    public Action<ImmutableArray<Layout.ArrangedNode<float>>>? Arranged { get; set; }

    // Hides the base's call every page's Render makes, so each one reports its arrangement without a line of its own.
    protected new ImmutableArray<Layout.ArrangedNode<float>> RenderLayout(Layout.Node root, RectF32 bounds, string? fontPath = null,
        DesignScale? scale = null, Action<Layout.Content.Fill, RectF32>? drawFill = null)
    {
        // The selection's Fill leaf (WithPageSelection) is painted for every page the same way, after the text it covers.
        var arranged = base.RenderLayout(root, bounds, fontPath, scale, (fill, rect) =>
        {
            drawFill?.Invoke(fill, rect);
            DrawSelection(fill);
        });
        Arranged?.Invoke(arranged);
        return arranged;
    }

    // The hit under the pointer as the last paint found it, which the next frame is built with.
    private HitResult? _hoveredHit;

    /// <summary>
    /// Whether the link whose hit is <paramref name="hit"/> has the pointer on any of its words, as the last paint found it: a wrapped link
    /// lights all its words together (the base lights only the rect under the pointer, one word). Settles one frame after the pointer moves.
    /// </summary>
    internal bool IsHovered(HitResult hit) => _hoveredHit is { } over && over.Equals(hit);

    /// <summary>
    /// Records the hit under the pointer, asked at the end of a render (the hit regions are this frame's only then); when it differs from the
    /// one the frame was built with, <paramref name="redraw"/> asks one more frame, which is built with it. One redraw per change, never a loop.
    /// </summary>
    protected void NoteHover(Action? redraw)
    {
        var now = Pointer is { } p ? HitTest(p.X, p.Y) : null;
        if (Equals(now, _hoveredHit))
        {
            return;
        }
        _hoveredHit = now;
        redraw?.Invoke();
    }

    /// <summary>
    /// The room's height in design units as the last <c>Render</c> had it (null before the first, and in a terminal): a page shorter than
    /// <see cref="ShortPageHeight"/> is laid out that tall and scrolls (<see cref="WhenShort"/>).
    /// </summary>
    internal float? PageHeight { get; set; }

    /// <summary>The least height a page is laid out in: its header (which wraps onto many lines at a narrow width) and a body of some use. A window shorter than this scrolls the page, as it does one too long.</summary>
    internal const float ShortPageHeight = 480f;

    private readonly ListScrollController _shortScroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>Takes the height of the room <paramref name="bounds"/> the page is rendered in, for <see cref="WhenShort"/>; call it before building the tree.</summary>
    protected void NoteHeight(RectF32 bounds) => PageHeight = Scale.ToDesignY(bounds.Height);

    private readonly ListScrollController _headScroll = new() { Mode = ScrollBarMode.Interactive };

    /// <summary>The room the body under a <see cref="ScrollingHead"/> keeps while the window is short: the page's own foot (the chat's box) stays in view.</summary>
    protected float BodyFloor => PageHeight is { } height && height < ShortPageHeight ? 160f : 0f;

    /// <summary>
    /// <paramref name="head"/> (a page's top bar, which wraps onto many lines at a narrow width) as it is, or, where the room is shorter than
    /// <see cref="ShortPageHeight"/>, taking what it needs of the room but leaving the body <see cref="BodyFloor"/> and scrolling itself past that: for
    /// the page whose foot must stay in reach (a chat's box), where <see cref="WhenShort"/> would scroll the box away.
    /// </summary>
    protected Layout.Node ScrollingHead(Layout.Node head) =>
        PageHeight is { } height && height < ShortPageHeight
            ? Layout.Builder.VStack(head).WithScroll(_headScroll).WStar().HStarUpToContent()
            : head;

    /// <summary>
    /// <paramref name="page"/> as it is, or, where the room is shorter than <see cref="ShortPageHeight"/>, laid out that tall inside a scroll: the
    /// body the page's own scrolls share would otherwise be left no room and the text of the page lie past its foot, cut (the 500 px
    /// window of the text-overlap sweep). A taller window gets the page itself, so nothing changes there.
    /// </summary>
    protected Layout.Node WhenShort(Layout.Node page) =>
        PageHeight is { } height && height < ShortPageHeight
            ? Layout.Builder.VStack(page.HFixed(ShortPageHeight)).WithScroll(_shortScroll).Stretch()
            : page;

    // The text selected on the page (its host has a clipboard to copy it to), made on first use: it measures through the renderer.
    // PageSelection, PageSelectionKey, PageSelectionViewport and WithPageSelection are pilot's own selection, named apart from
    // DIR.Lib 11.51's PixelWidgetBase selection (Selection, WithSelection, SelectionViewport), which pilot does not opt into:
    // never call the base's WithSelection, or its InputRouter would also drive a selection. Adopting DIR.Lib's is a follow-up.
    private TextSelection<TSurface>? _selection;

    /// <summary>The selection over the page's text; a view has one, as it has one link menu.</summary>
    internal TextSelection<TSurface> PageSelection => _selection ??= new TextSelection<TSurface>(Renderer);

    /// <summary>The Fill leaf laid over a page: painted after its text and before the menus over it, it is where the selection is drawn.</summary>
    private const string PageSelectionKey = "text-selection";

    /// <summary>Puts selected text on the clipboard: the view's own CopyText. Null where the host has no clipboard, and the page then selects nothing.</summary>
    internal virtual Action<string>? SelectionCopy => null;

    /// <summary>The view's link menu, which shows the "Copied" notice and the right-click Copy; null for a view with none (Ctrl+C then copies without a notice).</summary>
    internal virtual LinkMenu? SelectionLinks => null;

    /// <summary>The rect the page's scrolled part shows through (empty when nothing scrolls), so a selection is cut at its edge.</summary>
    internal virtual RectF32 PageSelectionViewport => default;

    /// <summary>Whether the page has text to select now (a flight's page only while it is open).</summary>
    internal virtual bool SelectionOn => SelectionCopy is not null;

    /// <summary>Whether a menu, wizard or viewer is up: their presses are theirs, and the selection lets go of them.</summary>
    internal virtual bool SelectionBlocked => SelectionLinks is { MenuItems.Count: > 0 };

    /// <summary>Lets go of the selected text: the page or panel it was made on was left.</summary>
    internal void ClearSelection() => _selection?.Clear();

    /// <summary><paramref name="page"/> with the selection's Fill leaf laid over it, where the host has a clipboard to copy to; place it after the page's text and before the menus over it.</summary>
    protected Layout.Node WithPageSelection(Layout.Node page)
    {
        if (!SelectionOn)
        {
            return page;
        }
        if (SelectionLinks is { } links)
        {
            links.SelectedText ??= () => PageSelection.Text;
        }
        return Layout.Builder.Overlay(page, Layout.Builder.Fill(key: PageSelectionKey).Stretch());
    }

    private void DrawSelection(Layout.Content.Fill fill)
    {
        if (fill.Key != PageSelectionKey)
        {
            return;
        }
        PageSelection.Frame(SelectableTextRegions, PageSelectionViewport);
        var palette = Palette.Current;
        PageSelection.Paint(
            (text, region, x, y, width, height, color) => DrawText(text, region.FontPath, x, y, width, height, region.FontSize, color, TextAlign.Near, region.VerticalAlign),
            palette.SelectionBg, palette.SelectionText);
    }

    /// <summary>
    /// The page's selection, ahead of the router: a left drag over its text selects it, Ctrl+C and a right-click on the
    /// selection copy it through <see cref="SelectionCopy"/>. Never takes a press that is a button's, a link's, a field's or a
    /// scrollbar's, and lets go while a menu or the viewer is up (those presses are theirs). True for what it took.
    /// </summary>
    internal bool HandleSelection(InputEvent evt)
    {
        if (SelectionCopy is not { } copy || !SelectionOn || SelectionBlocked)
        {
            return false;
        }
        var links = SelectionLinks;
        switch (evt)
        {
            case InputEvent.KeyDown { Key: InputKey.C, Modifiers: InputModifier.Ctrl } when PageSelection.Text is { } text:
                if (links is not null)
                {
                    links.Copy(text, PageSelection.Pointer.X, PageSelection.Pointer.Y);
                }
                else
                {
                    copy(text);
                }
                return true;
            case InputEvent.MouseDown { Button: MouseButton.Right } right:
                // A link keeps its own menu (Copy URL, and Copy beside it); the selection's is for the text itself.
                if (links is null || !PageSelection.Contains(right.X, right.Y) || Under(right.X, right.Y, r => r is { OnContextMenu: not null }))
                {
                    return false;
                }
                links.OpenSelection(new PointerPress(right.X, right.Y, right.Button, right.Modifiers, right.ClickCount));
                return true;
            default:
                // Property patterns throughout: a region that is not plain chrome, or has anything to do with a press, is not ours.
                return PageSelection.HandleInput(evt, (x, y) => !Under(x, y, r => r is not { Result: HitResult.ChromeHit }
                    || r is { OnPress: not null } or { OnActivate: not null } or { OnDoubleClick: not null } or { Opens: not null }
                    or { Dismisses: not null } or { FocusOnOpen: true }));
        }
    }

    // Whether the topmost region painted under the point is one the test holds for.
    private bool Under(float x, float y, Func<ClickableRegion, bool> test)
    {
        var regions = new List<ClickableRegion>();
        CollectPaintedRegions(regions);
        for (var i = regions.Count - 1; i >= 0; i--)
        {
            var r = regions[i];
            if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
            {
                return test(r);
            }
        }
        return false;
    }
}
