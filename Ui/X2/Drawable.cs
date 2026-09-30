#nullable enable
using AAEmu.GodotViewer.Lua;
namespace AAEmu.GodotViewer.Ui.X2;

public enum DrawableKind { Image, NinePart, ThreePart, Color, Text }

/// <summary>A widget's drawable (texture region, nine/three-part frame, colour fill or text).</summary>
public class Drawable : LayoutElement
{
    private readonly UiRoot _root;
    internal override UiRoot Root => _root;
    internal override Widget OwnerWidget { get; }
    public DrawableKind Kind { get; }
    public string Layer { get; private set; }
    public bool Visible { get; private set; } = true;
    public UiColor Tint { get; private set; } = UiColor.White;
    internal int Sequence { get; }

    internal Drawable(UiRoot root, Widget owner, DrawableKind kind, string layer)
    {
        _root = root;
        OwnerWidget = owner;
        Kind = kind;
        Layer = string.IsNullOrEmpty(layer) ? "background" : layer;
        Sequence = root.NextSequence();
    }

    public Widget GetParent() => OwnerWidget;
    public void SetVisible(bool visible) => Visible = visible;
    public void Show(bool visible) => Visible = visible;
    public bool IsVisible() => Visible;
    public void SetLayer(string layer) => Layer = layer;
    public string GetLayer() => Layer;
    public void SetColor(float r, float g, float b, float a = 1) => Tint = new UiColor(r, g, b, a);
    public void SetAlpha(float a) => Tint = new UiColor(Tint.R, Tint.G, Tint.B, a);
    public float GetAlpha() => Tint.A;
    // the client returns an array table {r, g, b, a}
    public AAEmu.GodotViewer.Lua.LuaTable GetColor() => new()
    {
        // scripts read both forms: colorTable[4] (icon.lua) and color.r (chat option colour buttons)
        [1d] = (double)Tint.R, [2d] = (double)Tint.G, [3d] = (double)Tint.B, [4d] = (double)Tint.A,
        ["r"] = (double)Tint.R, ["g"] = (double)Tint.G, ["b"] = (double)Tint.B, ["a"] = (double)Tint.A,
    };
    internal void SetTintValue(UiColor c) => Tint = c;

    public virtual void SetTextureColor(string colorKey)
    {
        if (Root.Settings.EtcColor(colorKey) is { } etc) Tint = etc;
    }
}

public sealed class TextureDrawable : Drawable
{
    public readonly record struct AnimationFrame(UiRect Source, double DurationMs, float Scale);
    private readonly List<AnimationFrame> _animationFrames = [];
    private long _animationStart;
    private bool _animationLoop;
    public bool AnimationRunning { get; private set; }
    public IReadOnlyList<AnimationFrame> AnimationFrames => _animationFrames;
    public bool Snap { get; private set; } = true;

    public void SetSnap(bool snap) => Snap = snap;

    public void SetAnimFrameInfo(LuaTable? frames)
    {
        _animationFrames.Clear();
        if (frames == null) return;
        for (var i = 1; frames.TryGetValue((double)i, out var value) && value is LuaTable frame; i++)
        {
            static float Number(LuaTable table, string key, float fallback = 0)
                => table.TryGetValue(key, out var v) && v is double n ? (float)n : fallback;
            _animationFrames.Add(new AnimationFrame(
                new UiRect(Number(frame, "x"), Number(frame, "y"), Number(frame, "w"), Number(frame, "h")),
                Math.Max(1, Number(frame, "time", 100)), Number(frame, "scale", 1)));
        }
        _animationStart = Environment.TickCount64;
    }

    public void Animation(bool running, bool loop = false, bool unused = false)
    {
        AnimationRunning = running && _animationFrames.Count > 0;
        _animationLoop = loop;
        _animationStart = Environment.TickCount64;
    }

    public UiRect AnimatedSourceRect
    {
        get
        {
            if (!AnimationRunning || _animationFrames.Count == 0) return SourceRect ?? default;
            var elapsed = Math.Max(0, Environment.TickCount64 - _animationStart);
            var total = _animationFrames.Sum(f => f.DurationMs);
            if (total <= 0) return _animationFrames[0].Source;
            if (!_animationLoop && elapsed >= total)
            {
                AnimationRunning = false;
                return _animationFrames[^1].Source;
            }
            var position = _animationLoop ? elapsed % total : elapsed;
            foreach (var frame in _animationFrames)
            {
                if (position < frame.DurationMs) return frame.Source;
                position -= frame.DurationMs;
            }
            return _animationFrames[^1].Source;
        }
    }

    public void SetVisibleForString(string? min, string? max, string? value)
    {
        if (!double.TryParse(min, out var a) || !double.TryParse(max, out var b) ||
            !double.TryParse(value, out var v)) return;
        SetVisible(v > Math.Min(a, b));
    }
    internal TextureDrawable(UiRoot root, Widget owner, DrawableKind kind, string? texturePath, string? key, string layer)
        : base(root, owner, kind, layer)
    {
        TexturePath = texturePath;
        Key = key;
        ApplyRegionExtent();
    }

    public string? TexturePath { get; private set; }
    public string? Key { get; private set; }
    public UiRect? CoordsOverride { get; private set; }
    public UiInsets? InsetOverride { get; private set; }
    public string? ColorKey { get; private set; }

    public TextureData? Region => Root.Settings.TextureRegion(TexturePath, Key);

    /// <summary>Source rectangle in texture pixels (negative width/height flips).</summary>
    public UiRect? SourceRect => CoordsOverride ?? Region?.Coords;

    public UiInsets? SliceInset => InsetOverride ?? Region?.Inset;

    /// <summary>How the region is stretched: explicit nine/three-part drawables, or the region's own type.</summary>
    public DrawableKind EffectiveKind
    {
        get
        {
            if (Kind != DrawableKind.Image) return Kind;
            var type = Region?.Type;
            if (string.Equals(type, "ninepart", StringComparison.OrdinalIgnoreCase)) return DrawableKind.NinePart;
            if (string.Equals(type, "threepart", StringComparison.OrdinalIgnoreCase)) return DrawableKind.ThreePart;
            return DrawableKind.Image;
        }
    }

    private void ApplyRegionExtent()
    {
        var data = Region;
        if (data?.Extent is { } extent) SetExtent(extent.Width, extent.Height);
        else if (data?.Coords is { } coords) SetExtent(Math.Abs(coords.Width), Math.Abs(coords.Height));
        if (ColorKey != null && data != null && data.Colors.TryGetValue(ColorKey, out var c)) SetTintValue(c);
    }

    public void SetTexture(string texturePath) => TexturePath = texturePath;

    /// <summary>Icon drawables stack textures (item icon, overlay icon, grade frame): AddTexture layers them in order.</summary>
    public List<string> ExtraTextures { get; } = [];
    public void AddTexture(string? texturePath)
    {
        if (string.IsNullOrEmpty(texturePath)) return;
        if (TexturePath == null) TexturePath = texturePath;
        else ExtraTextures.Add(texturePath);
    }
    public void ClearAllTextures()
    {
        TexturePath = null;
        ExtraTextures.Clear();
    }
    public string? GetTexture() => TexturePath;

    /// <summary>Selects a named region of the texture's .g file (and optionally one of its colours).</summary>
    public void SetTextureInfo(string key, string? colorKey = null)
    {
        Key = key;
        CoordsOverride = null;
        if (colorKey != null) ColorKey = colorKey;
        var data = Region;
        if (data?.Extent is { } extent && GetWidth() == 0) SetExtent(extent.Width, extent.Height);
        if (ColorKey != null && data != null && data.Colors.TryGetValue(ColorKey, out var c)) SetTintValue(c);
    }

    public string? GetTextureInfo() => Key;

    public void SetCoords(float x, float y, float width, float height)
    {
        CoordsOverride = new UiRect(x, y, width, height);
        if (GetWidth() == 0 && GetHeight() == 0) SetExtent(Math.Abs(width), Math.Abs(height));
    }

    public LuaMulti GetCoords() { var r = SourceRect ?? default; return new LuaMulti(r.X, r.Y, r.Width, r.Height); }
    public void SetInset(float left, float top, float right, float bottom) => InsetOverride = new UiInsets(left, top, right, bottom);

    public override void SetTextureColor(string colorKey)
    {
        ColorKey = colorKey;
        if (Region?.Colors.TryGetValue(colorKey, out var c) == true) SetTintValue(c);
        else base.SetTextureColor(colorKey);
    }

    public void SetTextureColorKey(string colorKey) => SetTextureColor(colorKey);

    // ------------------------------------------------------------------ effects (CreateEffectDrawable*)

    /// <summary>Set for effect drawables: drawn only while the effect runs.</summary>
    public DrawableEffect? Effect { get; internal set; }
    private DrawableEffect Fx => Effect ??= new DrawableEffect();

    public void SetEffectPriority(double phase, string? kind, double time, double acceleration = 0) => Fx.SetPriority((int)phase, time);
    public void SetEffectInitialColor(double phase, float r, float g, float b, float a) => Fx.SetInitialColor((int)phase, new UiColor(r, g, b, a));
    public void SetEffectFinalColor(double phase, float r, float g, float b, float a) => Fx.SetFinalColor((int)phase, new UiColor(r, g, b, a));
    public void SetEffectScale(double phase, float x0, float x1, float y0, float y1) => Fx.SetScale((int)phase, x0, x1, y0, y1);
    public void SetEffectInterval(double phase, double delay) => Fx.SetInterval((int)phase, delay);
    public void SetInterval(double delay) => Fx.InitialDelay = delay;
    public void SetRepeatCount(double count) => Fx.RepeatCount = (int)count;
    public void SetEffectRotate(double phase, double from, double to) { }
    public void SetMoveEffectType(params object?[] args) { }
    public void SetMoveEffectEdge(params object?[] args) { }
    public void SetMoveEffectCircle(params object?[] args) { }
    public void SetMoveInterval(double delay) { }
    public void SetMoveEffectInterval(double phase, double delay) { }
    public void SetMoveRepeatCount(double count) { }
    public void SetMoveRotate(params object?[] args) { }

    public void SetStartEffect(bool start)
    {
        Fx.Start(start);
        if (Fx.Running) Root.ActiveEffects.Add(this);
    }

    public bool IsNowAnimation() => Effect?.Running == true;
}

public sealed class ColorDrawable : Drawable
{
    internal ColorDrawable(UiRoot root, Widget owner, UiColor color, string layer) : base(root, owner, DrawableKind.Color, layer)
        => SetTintValue(color);
}

public sealed class TextDrawable : Drawable
{
    internal TextDrawable(UiRoot root, Widget owner, string layer) : base(root, owner, DrawableKind.Text, layer)
        => style = new WidgetStyle(root);

    public WidgetStyle style { get; }
    public string Text { get; private set; } = "";
    public void SetText(string? text) => Text = text ?? "";
    public string GetText() => Text;
}

/// <summary>
/// The client's drawable effects (CreateEffectDrawable*): phases of colour/scale interpolation started with
/// SetStartEffect(true). An effect drawable is only drawn while its effect runs.
/// </summary>
public sealed class DrawableEffect
{
    private sealed class Phase
    {
        public double Time = 0.3;
        public UiColor From = UiColor.White, To = UiColor.White;
        public (float X, float Y) ScaleFrom = (1, 1), ScaleTo = (1, 1);
        public double DelayAfter;
    }

    private readonly SortedDictionary<int, Phase> _phases = [];
    private double _elapsed;
    private int _loops;

    public bool Running { get; private set; }
    public double InitialDelay { get; set; }
    public int RepeatCount { get; set; } = 1;
    public UiColor Color { get; private set; } = UiColor.White;
    public (float X, float Y) Scale { get; private set; } = (1, 1);

    private Phase Get(int index)
    {
        if (!_phases.TryGetValue(index, out var p)) _phases[index] = p = new Phase();
        return p;
    }

    public void SetPriority(int phase, double time) => Get(phase).Time = Math.Max(0.001, time);
    public void SetInitialColor(int phase, UiColor c) => Get(phase).From = c;
    public void SetFinalColor(int phase, UiColor c) => Get(phase).To = c;
    public void SetScale(int phase, float x0, float x1, float y0, float y1) { var p = Get(phase); p.ScaleFrom = (x0, y0); p.ScaleTo = (x1, y1); }
    public void SetInterval(int phase, double delay) => Get(phase).DelayAfter = delay;

    public void Start(bool start)
    {
        Running = start && _phases.Count > 0;
        _elapsed = 0;
        _loops = 0;
        if (Running) Evaluate();
    }

    /// <summary>Advances by seconds; returns false when finished.</summary>
    public bool Tick(double seconds)
    {
        if (!Running) return false;
        _elapsed += seconds;
        Evaluate();
        return Running;
    }

    private void Evaluate()
    {
        var total = InitialDelay + _phases.Values.Sum(p => p.Time + p.DelayAfter);
        if (total <= 0) { Running = false; return; }
        while (_elapsed >= total)
        {
            _elapsed -= total;
            _loops++;
            if (RepeatCount > 0 && _loops >= RepeatCount) { Running = false; return; }
        }
        var t = _elapsed - InitialDelay;
        var first = _phases.Values.First();
        if (t < 0) { Color = first.From; Scale = first.ScaleFrom; return; }
        foreach (var p in _phases.Values)
        {
            if (t <= p.Time)
            {
                var f = (float)(t / p.Time);
                Color = new UiColor(p.From.R + (p.To.R - p.From.R) * f, p.From.G + (p.To.G - p.From.G) * f,
                    p.From.B + (p.To.B - p.From.B) * f, p.From.A + (p.To.A - p.From.A) * f);
                Scale = (p.ScaleFrom.X + (p.ScaleTo.X - p.ScaleFrom.X) * f, p.ScaleFrom.Y + (p.ScaleTo.Y - p.ScaleFrom.Y) * f);
                return;
            }
            t -= p.Time;
            if (t <= p.DelayAfter) { Color = p.To; Scale = p.ScaleTo; return; }
            t -= p.DelayAfter;
        }
    }
}
