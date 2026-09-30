#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>Text alignment as the client's ALIGN_* constants (0..8, row-major top-left to bottom-right).</summary>
public enum UiAlign
{
    TopLeft = 0, Top = 1, TopRight = 2, Left = 3, Center = 4, Right = 5, BottomLeft = 6, Bottom = 7, BottomRight = 8
}

/// <summary>The widget.style / guideTextStyle / titleStyle object: font, size, colour, alignment, shadow.</summary>
public sealed class WidgetStyle
{
    private readonly UiRoot _root;

    internal WidgetStyle(UiRoot root, Widget? owner = null)
    {
        _root = root;
        Owner = owner;
        FontSize = root.Settings.DefaultFontSize;
    }

    internal Widget? Owner { get; }
    public string FontKey { get; private set; } = "font_main";
    public float FontSize { get; private set; }
    public UiColor Color { get; private set; } = UiColor.White;
    public string? ColorKey { get; private set; }
    public UiAlign Align { get; private set; } = UiAlign.Center;
    public bool Shadow { get; private set; } = true;
    public bool Outline { get; private set; }
    public bool Snap { get; private set; }
    public bool Ellipsis { get; private set; }
    public float LineSpaceOverride { get; private set; } = -1;

    private void Changed() { if (Owner != null) Owner.OnStyleChanged(); }

    public void SetFont(string path, float size)
    {
        FontKey = string.IsNullOrEmpty(path) ? "font_main" : path;
        if (size > 0) FontSize = size;
        Changed();
    }

    public void SetFontSize(float size) { if (size > 0) FontSize = size; Changed(); }
    public float GetFontSize() => FontSize;

    public void SetColor(float r, float g, float b, float a = 1) { Color = new UiColor(r, g, b, a); ColorKey = null; }

    public void SetColorByKey(string key)
    {
        ColorKey = key;
        if (_root.Settings.FontColor(key) is { } c) Color = c;
    }

    public void SetAlign(object? align) { Align = ParseAlign(align, Align); Changed(); }
    public void SetShadow(bool enabled) => Shadow = enabled;
    public void SetOutline(bool enabled) => Outline = enabled;
    public void SetSnap(bool enabled) => Snap = enabled;
    public void SetEllipsis(bool enabled) => Ellipsis = enabled;
    public void SetLineSpace(float space) { LineSpaceOverride = space; Changed(); }

    public float GetTextWidth(string? text)
    {
        var lines = TextLayout.Lines(_root, FontKey, FontSize, text ?? "", float.MaxValue);
        return lines.Count == 0 ? 0 : lines.Max(line => line.Width);
    }
    public float GetLineHeight() => _root.Measurer.LineHeight(FontKey, FontSize);
    public float GetTextHeight(string? text) => GetLineHeight();

    internal void CopyFrom(WidgetStyle other)
    {
        FontKey = other.FontKey;
        FontSize = other.FontSize;
        Color = other.Color;
        ColorKey = other.ColorKey;
        Align = other.Align;
        Shadow = other.Shadow;
        Snap = other.Snap;
    }

    internal void SetColorValue(UiColor color) { Color = color; ColorKey = null; }

    public static UiAlign ParseAlign(object? align, UiAlign fallback)
    {
        switch (align)
        {
            case double d when d is >= 0 and <= 8: return (UiAlign)(int)d;
            case int i when i is >= 0 and <= 8: return (UiAlign)i;
            case string s:
                return s.Replace("_", "").ToLowerInvariant() switch
                {
                    "topleft" => UiAlign.TopLeft,
                    "top" => UiAlign.Top,
                    "topright" => UiAlign.TopRight,
                    "left" => UiAlign.Left,
                    "center" => UiAlign.Center,
                    "right" => UiAlign.Right,
                    "bottomleft" => UiAlign.BottomLeft,
                    "bottom" => UiAlign.Bottom,
                    "bottomright" => UiAlign.BottomRight,
                    _ => fallback,
                };
            default: return fallback;
        }
    }
}
