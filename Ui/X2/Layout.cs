#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

public enum AnchorPoint
{
    TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight
}

public sealed record Anchor(AnchorPoint Point, object? RelativeTo, AnchorPoint RelativePoint, float X, float Y);

/// <summary>Anchor/extent state shared by widgets and drawables; positions are solved lazily by <see cref="UiRoot"/>.</summary>
public abstract class LayoutElement
{
    private readonly List<Anchor> _anchors = [];
    internal abstract UiRoot Root { get; }
    /// <summary>The widget this element is positioned in when it has no anchors (parent for widgets, owner for drawables).</summary>
    internal abstract Widget? OwnerWidget { get; }
    internal float Width { get; private set; }
    internal float Height { get; private set; }
    internal UiRect CachedRect;
    internal int CachedVersion = -1;
    internal bool Solving;
    public IReadOnlyList<Anchor> Anchors => _anchors;

    /// <summary>Solved rectangle in UI units (UIParent's top-left is 0,0).</summary>
    public UiRect ScreenRect => Root.Solve(this);

    public virtual void SetExtent(float width, float height) { Width = width; Height = height; Root.InvalidateLayout(); }
    public LuaMulti GetExtent() { var r = ScreenRect; return new LuaMulti(r.Width, r.Height); }
    public virtual void SetWidth(float width) { Width = width; Root.InvalidateLayout(); }
    public virtual void SetHeight(float height) { Height = height; Root.InvalidateLayout(); }
    public float GetWidth() => ScreenRect.Width;
    public float GetHeight() => ScreenRect.Height;
    internal (float W, float H) NaturalSize => (Width, Height);

    /// <summary>Top-left of the solved rectangle.</summary>
    public LuaMulti GetOffset() { var r = ScreenRect; return new LuaMulti(r.X, r.Y); }
    public LuaMulti GetEffectiveOffset() => GetOffset();
    public LuaMulti GetEffectiveExtent() => GetExtent();

    /// <summary>
    /// AddAnchor(point, target[, relativePoint][, x, y]) or AddAnchor(point, x, y). Re-adding a point replaces it.
    /// </summary>
    public void AddAnchor(params object?[] args)
    {
        if (args.Length == 0 || args[0] is not string pointName)
            return;
        var point = ParsePoint(pointName);
        object? target = null;
        var relative = point;
        float x = 0, y = 0;
        var i = 1;
        if (i < args.Length && args[i] is not double)
        {
            target = args[i];
            i++;
            if (i < args.Length && args[i] is string rel)
            {
                relative = ParsePoint(rel);
                i++;
            }
        }
        if (i < args.Length && args[i] is double dx) { x = (float)dx; i++; }
        if (i < args.Length && args[i] is double dy) y = (float)dy;
        // A corner anchor supersedes a provisional side-center anchor on the
        // same edge. The Lua inventory view first places its slot area at
        // LEFT, then re-parents its layout with TOPLEFT; retaining both would
        // incorrectly derive the slot height from the screen center.
        if (point is AnchorPoint.TopLeft or AnchorPoint.BottomLeft)
            _anchors.RemoveAll(a => a.Point == AnchorPoint.Left);
        else if (point is AnchorPoint.TopRight or AnchorPoint.BottomRight)
            _anchors.RemoveAll(a => a.Point == AnchorPoint.Right);
        _anchors.RemoveAll(a => a.Point == point);
        _anchors.Add(new Anchor(point, target, relative, x, y));
        Root.InvalidateLayout();
        // Script-created textboxes commonly enable auto-resize before their
        // horizontal anchors are assigned.  Reflow after each anchor update so
        // wrapped text uses the final available width instead of the initial
        // unsized one-pixel width.
        if (this is Widget { AutoResize: true, WrapsText: true } widget)
            widget.ApplyAutoResize();
    }

    public void RemoveAllAnchors() { _anchors.Clear(); Root.InvalidateLayout(); }
    public int GetAnchorCount() => _anchors.Count;

    internal static AnchorPoint ParsePoint(string point) => point.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "TOPLEFT" => AnchorPoint.TopLeft,
        "TOP" => AnchorPoint.Top,
        "TOPRIGHT" => AnchorPoint.TopRight,
        "LEFT" => AnchorPoint.Left,
        "CENTER" => AnchorPoint.Center,
        "RIGHT" => AnchorPoint.Right,
        "BOTTOMLEFT" => AnchorPoint.BottomLeft,
        "BOTTOM" => AnchorPoint.Bottom,
        "BOTTOMRIGHT" => AnchorPoint.BottomRight,
        _ => AnchorPoint.TopLeft,
    };

    internal static (float X, float Y) Factors(AnchorPoint point) => point switch
    {
        AnchorPoint.TopLeft => (0, 0), AnchorPoint.Top => (.5f, 0), AnchorPoint.TopRight => (1, 0),
        AnchorPoint.Left => (0, .5f), AnchorPoint.Center => (.5f, .5f), AnchorPoint.Right => (1, .5f),
        AnchorPoint.BottomLeft => (0, 1), AnchorPoint.Bottom => (.5f, 1), AnchorPoint.BottomRight => (1, 1),
        _ => (0, 0),
    };
}

/// <summary>A method result carrying several Lua return values.</summary>
public sealed class LuaMulti(params object?[] values)
{
    public object?[] Values { get; } = values;
}
