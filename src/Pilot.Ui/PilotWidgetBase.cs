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
        var arranged = base.RenderLayout(root, bounds, fontPath, scale, drawFill);
        Arranged?.Invoke(arranged);
        return arranged;
    }
}
