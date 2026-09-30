#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>HLS picker state and the two native hit areas decorated by palette.lua.</summary>
public sealed class PaintColorPickerWidget : Widget
{
    private Widget? _spectrum, _luminance;
    private Dictionary<object, object?>? _data;
    internal PaintColorPickerWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public float Hue { get; private set; }
    public float Lightness { get; private set; } = 0.5f;
    public float Saturation { get; private set; }
    public int Red { get; private set; } = 128;
    public int Green { get; private set; } = 128;
    public int Blue { get; private set; } = 128;
    public Drawable? SpectrumBackground { get; private set; }
    public Drawable? LuminanceBackground { get; private set; }

    internal override void CreateNativeChildren()
    {
        _spectrum = Root.CreateWidget("emptywidget", "spectrum", this);
        _luminance = Root.CreateWidget("emptywidget", "luminance", this);
        _spectrum.AddAnchor("TOPLEFT", this, 0.0, 0.0);
        _luminance.AddAnchor("TOPRIGHT", this, 0.0, 0.0);
    }
    public Widget? GetSpectrumWidget() => _spectrum;
    public Widget? GetLuminanceWidget() => _luminance;
    public void SetSpectrumBg(Drawable? drawable) => SpectrumBackground = drawable;
    public void SetLuminanceBg(Drawable? drawable) => LuminanceBackground = drawable;

    public void SetHLSColor(float hue, float lum, float sat)
    {
        Hue = WrapHue(hue); Lightness = Math.Clamp(lum, 0, 1); Saturation = Math.Clamp(sat, 0, 1);
        var chroma = (1 - Math.Abs(2 * Lightness - 1)) * Saturation;
        var h = Hue * 6;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = h switch
        {
            < 1 => (chroma, x, 0f), < 2 => (x, chroma, 0f), < 3 => (0f, chroma, x),
            < 4 => (0f, x, chroma), < 5 => (x, 0f, chroma), _ => (chroma, 0f, x)
        };
        var m = Lightness - chroma / 2;
        Red = Channel(r + m); Green = Channel(g + m); Blue = Channel(b + m);
    }
    public void SetRGBColor(double red, double green, double blue)
    {
        Red = Math.Clamp((int)Math.Round(red), 0, 255);
        Green = Math.Clamp((int)Math.Round(green), 0, 255);
        Blue = Math.Clamp((int)Math.Round(blue), 0, 255);
        var r = Red / 255f; var g = Green / 255f; var b = Blue / 255f;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        Lightness = (max + min) / 2;
        Saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * Lightness - 1));
        Hue = delta == 0 ? 0 : max == r ? (((g - b) / delta) % 6) / 6 : max == g ? ((b - r) / delta + 2) / 6 : ((r - g) / delta + 4) / 6;
        Hue = WrapHue(Hue);
    }
    public LuaMulti GetRGBColor() => new(Red, Green, Blue);
    public LuaMulti GetHLSColor() => new(Hue, Lightness, Saturation);
    public void SetData(IDictionary<object, object?>? data)
    {
        _data = data == null ? null : new Dictionary<object, object?>(data);
        if (data == null) return;
        if (data.TryGetValue("color", out var color) && color is IDictionary<object, object?> c)
        {
            if (Number(c, "r", out var r) && Number(c, "g", out var g) && Number(c, "b", out var b)) SetRGBColor(r, g, b);
            else if (Number(c, 1d, out r) && Number(c, 2d, out g) && Number(c, 3d, out b)) SetRGBColor(r, g, b);
        }
    }
    public Dictionary<object, object?> GetData()
    {
        var data = _data == null ? new Dictionary<object, object?>() : new Dictionary<object, object?>(_data);
        data["color"] = new Dictionary<object, object?> { ["r"] = (double)Red, ["g"] = (double)Green, ["b"] = (double)Blue };
        return data;
    }
    private static bool Number(IDictionary<object, object?> data, object key, out double value)
    { value = 0; return data.TryGetValue(key, out var o) && o is double d && (value = d) == d; }
    private static int Channel(float x) => Math.Clamp((int)Math.Round(x * 255), 0, 255);
    private static float WrapHue(float x) => ((x % 1) + 1) % 1;
}
