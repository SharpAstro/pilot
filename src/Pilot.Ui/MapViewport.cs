using System.Numerics;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// A pan and zoom viewport over a map of a known size in design units, on <see cref="PanZoomController"/>. It knows the room the
/// map is shown in and fits the map to it (fill the width or the height, whichever binds, capped so small text is not blown up and
/// floored so it stays legible), centres it, and maps design points to the view and back. Once the person has moved the view
/// (a pan or a zoom) it stays where they left it: a fit is redone only by <see cref="Fit"/> or when <see cref="Update"/> is given a
/// new key (what is shown has changed). Free of any page, so another board can use it. All lengths are design units, and
/// view coordinates are measured from the view's top-left corner.
/// </summary>
internal sealed class MapViewport
{
    /// <summary>The smallest scale a fit or a zoom reaches: below it the map's 9 pt labels are unreadable.</summary>
    public const float MinScale = 0.5f;

    /// <summary>The smallest scale a zoom reaches (a fit never goes below <see cref="MinScale"/>), so a wide map can be seen whole.</summary>
    public const float MinZoom = 0.25f;

    /// <summary>The largest scale a fit reaches: past it a small map is blown up, and its labels with it.</summary>
    public const float MaxFitScale = 1.5f;

    /// <summary>The largest scale a zoom reaches.</summary>
    public const float MaxScale = 4f;

    // How much of the map must stay in view when it is panned away, so it can always be dragged back.
    private const float KeepInView = 80f;

    private readonly PanZoomController _pan = new() { MinZoom = MinZoom, MaxZoom = MaxScale };
    private float _viewWidth;
    private float _viewHeight;
    private float _contentWidth;
    private float _contentHeight;
    private object? _key;
    private bool _keyed;

    /// <summary>The room left round the map when it is fitted.</summary>
    public float Margin { get; set; } = 8f;

    /// <summary>Whether the person has panned or zoomed since the last fit.</summary>
    public bool Moved { get; private set; }

    /// <summary>Design units of view per design unit of map.</summary>
    public float Scale => _pan.Zoom;

    /// <summary>How far the map's centre is from the view's centre, view units.</summary>
    public float PanX => _pan.PanOffset.X;

    public float PanY => _pan.PanOffset.Y;

    public float ViewWidth => _viewWidth;

    public float ViewHeight => _viewHeight;

    /// <summary>The scale a fit gives a map of <paramref name="contentWidth"/> by <paramref name="contentHeight"/> in a view of the given room.</summary>
    public static float FitScale(float viewWidth, float viewHeight, float contentWidth, float contentHeight, float margin = 0f)
    {
        if (contentWidth <= 0f || contentHeight <= 0f)
        {
            return 1f;
        }
        var fit = MathF.Min((viewWidth - 2f * margin) / contentWidth, (viewHeight - 2f * margin) / contentHeight);
        return float.IsFinite(fit) ? Math.Clamp(fit, MinScale, MaxFitScale) : MinScale;
    }

    /// <summary>Fits the map to the view and centres it, and forgets that the view was moved.</summary>
    public void Fit()
    {
        _pan.Zoom = FitScale(_viewWidth, _viewHeight, _contentWidth, _contentHeight, Margin);
        _pan.PanOffset = Vector2.Zero;
        _pan.ZoomToFit = true;
        Moved = false;
    }

    /// <summary>
    /// States the map's size and what is shown. A new <paramref name="key"/> (or the first) fits; the same key refits only while the
    /// view has not been moved, so a leg or a state change that alters the map's size leaves a view the person set alone.
    /// </summary>
    public void Update(float contentWidth, float contentHeight, object? key)
    {
        var (oldWidth, oldHeight) = (_contentWidth, _contentHeight);
        _contentWidth = contentWidth;
        _contentHeight = contentHeight;
        var changed = !_keyed || !Equals(_key, key);
        (_key, _keyed) = (key, true);
        if (changed || !Moved)
        {
            Fit();
        }
        else
        {
            // The map's centre anchors the view, so growth would slide it; keep its top-left corner where it was.
            _pan.PanOffset += new Vector2((contentWidth - oldWidth) * Scale / 2f, (contentHeight - oldHeight) * Scale / 2f);
            Clamp();
        }
    }

    /// <summary>States the room the map is shown in; a view not yet moved is fitted to it again.</summary>
    public void SetView(float width, float height)
    {
        _viewWidth = width;
        _viewHeight = height;
        if (!Moved)
        {
            Fit();
        }
        else
        {
            Clamp();
        }
    }

    // The view-coordinate of a design point: the map's centre sits at the view's centre, moved by the pan.
    public float ViewX(float x) => _viewWidth / 2f + _pan.PanOffset.X + (x - _contentWidth / 2f) * Scale;

    public float ViewY(float y) => _viewHeight / 2f + _pan.PanOffset.Y + (y - _contentHeight / 2f) * Scale;

    public float DesignX(float viewX) => (viewX - _viewWidth / 2f - _pan.PanOffset.X) / Scale + _contentWidth / 2f;

    public float DesignY(float viewY) => (viewY - _viewHeight / 2f - _pan.PanOffset.Y) / Scale + _contentHeight / 2f;

    /// <summary>Moves the map by <paramref name="dx"/>, <paramref name="dy"/> view units, kept within reach.</summary>
    public void PanBy(float dx, float dy)
    {
        _pan.PanOffset += new Vector2(dx, dy);
        Moved = true;
        Clamp();
    }

    /// <summary>Zooms by <paramref name="factor"/> about a view point, which stays over the same map point. Whether the scale changed.</summary>
    public bool ZoomAt(float factor, float viewX, float viewY)
    {
        if (!_pan.ZoomByFactor(factor, viewX, viewY, new RectF32(0f, 0f, _viewWidth, _viewHeight)))
        {
            return false;
        }
        Moved = true;
        Clamp();
        return true;
    }

    /// <summary>Zooms by <paramref name="factor"/> about the view's centre. Whether the scale changed.</summary>
    public bool ZoomCentre(float factor) => ZoomAt(factor, _viewWidth / 2f, _viewHeight / 2f);

    /// <summary>Sets the scale (kept within the floor and the ceiling) about the view's centre; the view counts as moved.</summary>
    public void SetScale(float scale) => ZoomCentre(Math.Clamp(scale, MinZoom, MaxScale) / Scale);

    /// <summary>Whether a drag that moves the map is in flight.</summary>
    public bool IsPanning => _pan.IsPanning;

    /// <summary>Starts a drag that moves the map, from a view point.</summary>
    public void BeginPan(float viewX, float viewY) => _pan.BeginPan(viewX, viewY);

    /// <summary>Moves the map by the pointer's motion since the last call. False when no drag is in flight.</summary>
    public bool UpdatePan(float viewX, float viewY)
    {
        if (!_pan.UpdatePan(viewX, viewY))
        {
            return false;
        }
        Moved = true;
        Clamp();
        return true;
    }

    public void EndPan() => _pan.EndPan();

    /// <summary>One wheel step: up and down, or left and right with <paramref name="sideways"/>. A positive delta (wheel up, or left) shows more of the top (or left).</summary>
    public void Wheel(float delta, bool sideways, float step = 60f)
    {
        var d = delta * step;
        PanBy(sideways ? d : 0f, sideways ? 0f : d);
    }

    // Keeps at least a margin of the map on screen, whichever way it was pushed.
    private void Clamp()
    {
        var reachX = MathF.Max(0f, _contentWidth * Scale / 2f + _viewWidth / 2f - KeepInView);
        var reachY = MathF.Max(0f, _contentHeight * Scale / 2f + _viewHeight / 2f - KeepInView);
        _pan.PanOffset = new Vector2(Math.Clamp(_pan.PanOffset.X, -reachX, reachX), Math.Clamp(_pan.PanOffset.Y, -reachY, reachY));
    }
}
