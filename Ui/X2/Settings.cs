#nullable enable
using System.Text;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>Reads client files; paths are relative to the game folder ("ui/common/default.g", "scripts/x2ui/...").</summary>
public interface IUiFileSource
{
    byte[]? Read(string path);
    bool Exists(string path);
}

/// <summary>Localised UI strings (ui_texts), by category id and key; null when missing.</summary>
public interface IUiTextSource
{
    string? Get(int category, string key);
}

/// <summary>Font metrics for layout (auto-resize, word wrap, GetTextWidth). Text has no colour codes.</summary>
public interface ITextMeasurer
{
    float Width(string fontKey, float size, string text);
    float LineHeight(string fontKey, float size);
}

/// <summary>Rough metrics for tests without a font engine.</summary>
public sealed class ApproximateTextMeasurer : ITextMeasurer
{
    public float Width(string fontKey, float size, string text) => text.Length * size * 0.5f;
    public float LineHeight(string fontKey, float size) => size + 2;
}

public sealed record FontFace(string Path, IReadOnlyList<(int From, int To)> Ranges);

public sealed record ButtonStyleDef(
    string Name,
    string? TexturePath,
    string? TextureKey,
    string? TextureColor,
    string? TextureAnchor,
    string? ButtonAnchor,
    UiInsets FontInset,
    string? FontColor,
    string? FontSize,
    string? Align,
    string? FontPath,
    bool Ellipsis,
    bool AutoResize,
    float? Height);

/// <summary>
/// The data-driven parts of the client UI: ui/setting/*.g (font/texture colours, font sizes, button styles, line
/// spaces), game/fonts/fonts.g, and the .g region files that sit next to every UI texture.
/// </summary>
public sealed class UiSettings
{
    private readonly IUiFileSource _files;
    private readonly Dictionary<string, GeometryDocument?> _geometry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _localized = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UiColor> _fontColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UiColor> _textureColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _fontSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ButtonStyleDef> _buttonStyles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _lineSpaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _etcValues = new(StringComparer.OrdinalIgnoreCase);

    public string Locale { get; }
    public IUiFileSource Files => _files;
    public float DefaultFontSize { get; private set; } = 13;
    public IReadOnlyDictionary<string, List<FontFace>> Fonts { get; }
    public IReadOnlyDictionary<string, float> FontSizes => _fontSizes;

    public UiSettings(IUiFileSource files, string locale = "en_us")
    {
        _files = files;
        Locale = locale;
        LoadColors("ui/setting/font_color.g", _fontColors);
        LoadColors("ui/setting/etc_color.g", _textureColors);
        foreach (var n in ReadG("ui/setting/font_size.g"))
        {
            if (float.TryParse(n.ChildValue("value"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                _fontSizes[n.Key] = v;
                if (n.Child("default") != null) DefaultFontSize = v;
            }
        }
        foreach (var path in new[] { "ui/setting/widget.g", $"ui/setting/{locale}/widget.g" })
            foreach (var n in ReadG(path))
                if (GFile.ParseNumbers(n.ChildValue("line_space") ?? "") is { Length: > 0 } ls)
                    _lineSpaces[n.Key] = ls[0];
        foreach (var path in new[] { "ui/setting/etc_value.g", $"ui/setting/{locale}/etc_value.g" })
            foreach (var n in ReadG(path))
                if (GFile.ParseNumbers(n.ChildValue("value") ?? "") is { Length: > 0 } ev)
                    _etcValues[n.Key] = ev[0];
        foreach (var n in ReadG("ui/setting/button_style.g"))
            _buttonStyles[n.Key] = ParseButtonStyle(n);
        Fonts = LoadFonts(locale);
    }

    public string? ReadText(string path)
    {
        var bytes = _files.Read(path);
        return bytes == null ? null : Encoding.UTF8.GetString(bytes);
    }

    private List<GNode> ReadG(string path) => ReadText(path) is { } t ? GFile.Parse(t) : [];

    private void LoadColors(string path, Dictionary<string, UiColor> into)
    {
        foreach (var n in ReadG(path))
            if (GFile.ToColor(n.Child("color")?.Numbers) is { } c)
                into[n.Key] = c;
    }

    private static ButtonStyleDef ParseButtonStyle(GNode n)
    {
        var tex = n.Child("texture");
        var font = n.Child("font");
        var anchor = tex?.ChildValue("anchor")?.Trim('(', ')', ' ').Split(',', StringSplitOptions.TrimEntries);
        var inset = font?.Child("inset")?.Numbers;
        return new ButtonStyleDef(
            n.Key,
            tex?.ChildValue("path"),
            tex?.ChildValue("key"),
            tex?.ChildValue("color"),
            anchor is { Length: > 0 } ? anchor[0] : null,
            anchor is { Length: > 1 } ? anchor[1] : null,
            inset is { Length: 4 } ? new UiInsets(inset[0], inset[1], inset[2], inset[3]) : default,
            font?.ChildValue("color"),
            font?.ChildValue("size"),
            font?.ChildValue("align"),
            font?.ChildValue("path"),
            string.Equals(n.ChildValue("ellipsis"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(n.ChildValue("auto_resize"), "true", StringComparison.OrdinalIgnoreCase),
            GFile.ParseNumbers(n.ChildValue("height") ?? "") is { Length: > 0 } h ? h[0] : null);
    }

    private Dictionary<string, List<FontFace>> LoadFonts(string locale)
    {
        var result = new Dictionary<string, List<FontFace>>(StringComparer.OrdinalIgnoreCase);
        var root = ReadG("fonts/fonts.g").FirstOrDefault(n => n.Key.Equals(locale, StringComparison.OrdinalIgnoreCase))
                   ?? ReadG("fonts/fonts.g").FirstOrDefault(n => n.Key.Equals("en_us", StringComparison.OrdinalIgnoreCase));
        if (root == null)
            return result;
        foreach (var kind in root.Children)
        {
            var faces = new List<FontFace>();
            foreach (var face in kind.Children)
            {
                var ranges = new List<(int, int)>();
                foreach (var r in face.Children)
                    if (GFile.ParseNumbers(r.Key + r.Value) is { Length: 2 } v)
                        ranges.Add(((int)v[0], (int)v[1]));
                var path = face.Key.Replace('\\', '/');
                if (path.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) path = path[5..];
                faces.Add(new FontFace(path, ranges));
            }
            result[kind.Key] = faces;
        }
        return result;
    }

    public UiColor? FontColor(string? key) => key != null && _fontColors.TryGetValue(key, out var c) ? c : null;
    public UiColor? EtcColor(string? key) => key != null && _textureColors.TryGetValue(key, out var c) ? c : null;
    public ButtonStyleDef? ButtonStyle(string? name) => name != null && _buttonStyles.TryGetValue(name, out var s) ? s : null;
    public float LineSpace(string widgetType) => _lineSpaces.GetValueOrDefault(widgetType, 0);
    public float? EtcValue(string key) => _etcValues.TryGetValue(key, out var v) ? v : null;

    public float FontSize(string? name)
    {
        if (name == null) return DefaultFontSize;
        return _fontSizes.TryGetValue(name, out var v) ? v : DefaultFontSize;
    }

    public static string NormalizePath(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (p.StartsWith("game/", StringComparison.Ordinal)) p = p[5..];
        return p;
    }

    /// <summary>"ui/x/y.dds" becomes "ui/x/&lt;locale&gt;/y.dds" when the locale has its own copy (as the client does).</summary>
    public string LocalizeTexturePath(string path)
    {
        var p = NormalizePath(path);
        if (_localized.TryGetValue(p, out var cached)) return cached;
        var slash = p.LastIndexOf('/');
        var candidate = slash < 0 ? $"{Locale}/{p}" : $"{p[..slash]}/{Locale}/{p[(slash + 1)..]}";
        var result = _files.Exists(candidate) ? candidate : p;
        _localized[p] = result;
        return result;
    }

    public GeometryDocument? Geometry(string texturePath)
    {
        var p = LocalizeTexturePath(texturePath);
        if (_geometry.TryGetValue(p, out var doc)) return doc;
        var g = Path.ChangeExtension(p, ".g");
        var text = ReadText(g);
        doc = text == null ? null : GeometryReader.Parse(text, p);
        _geometry[p] = doc;
        return doc;
    }

    public TextureData? TextureRegion(string? texturePath, string? key)
        => texturePath == null || key == null ? null : Geometry(texturePath)?.Regions.GetValueOrDefault(key);
}
