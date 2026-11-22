using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// A widget that answers the pointer only inside its frame: a played prototype is painted under its frame's clip, but the player registers its
/// hit areas as it renders, so a click on a part the clip cut (under the chat, over the notes) still reached it (#1177 R1.2; the player's own
/// clip is SharpAstro/DIR.Lib#271). Its regions are cut to the frame, one wholly outside is dropped, and a hit test outside the frame is no hit.
/// Everything else is the inner widget's.
/// </summary>
internal sealed class FramedWidget(Func<IPixelWidget> inner, Func<RectF32?> frame) : IPixelWidget
{
    private IPixelWidget Inner => inner();

    /// <summary>The inner widget as it is now (a player swapped while pointing is the same frame).</summary>
    public IPixelWidget Widget => Inner;

    public HitResult? HitTest(float x, float y) => Inside(x, y) ? Inner.HitTest(x, y) : null;

    public void CollectPaintedRegions(List<ClickableRegion> into)
    {
        var start = into.Count;
        Inner.CollectPaintedRegions(into);
        if (frame() is not { } f)
        {
            into.RemoveRange(start, into.Count - start);
            return;
        }
        for (var i = into.Count - 1; i >= start; i--)
        {
            var r = into[i];
            var left = MathF.Max(r.X, f.X);
            var top = MathF.Max(r.Y, f.Y);
            var right = MathF.Min(r.X + r.Width, f.X + f.Width);
            var bottom = MathF.Min(r.Y + r.Height, f.Y + f.Height);
            if (right <= left || bottom <= top)
            {
                into.RemoveAt(i);
            }
            else if (left != r.X || top != r.Y || right != r.X + r.Width || bottom != r.Y + r.Height)
            {
                into[i] = r with { X = left, Y = top, Width = right - left, Height = bottom - top };
            }
        }
    }

    public void CollectPaintedNodes(List<Layout.ArrangedNode<float>> into) => Inner.CollectPaintedNodes(into);

    public List<TextInputState> GetRegisteredTextInputs() => Inner.GetRegisteredTextInputs();

    public int CaretIndexAt(HitResult.TextInputHit hit, float pointerX) => Inner.CaretIndexAt(hit, pointerX);

    public WindowUiSettings Ui => Inner.Ui;

    public bool HandleInput(InputEvent evt) => Inner.HandleInput(evt);

    public (float X, float Y)? Pointer
    {
        get => Inner.Pointer;
        set => Inner.Pointer = value;
    }

    public ListScrollController? ScrollTargetAt(float x, float y) => Inside(x, y) ? Inner.ScrollTargetAt(x, y) : null;

    public CursorKind? HitTestCursor(float x, float y) => Inside(x, y) ? Inner.HitTestCursor(x, y) : null;

    private bool Inside(float x, float y) => frame() is { } f && x >= f.X && x < f.X + f.Width && y >= f.Y && y < f.Y + f.Height;
}
