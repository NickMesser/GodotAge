using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// Client files from game_pak through <see cref="PakFiles"/> (paths relative to game/). A file under
/// <c>res://Ui/X2/Overrides/game/</c> with the same path wins (e.g. the restored scripts of the feature re-enable patch).
/// </summary>
public sealed class PakUiFiles : IUiFileSource
{
    public const string OverrideRoot = "res://Ui/X2/Overrides/game/";

    public byte[] Read(string path)
    {
        var p = UiSettings.NormalizePath(path);
        if (Godot.FileAccess.FileExists(OverrideRoot + p)) return Godot.FileAccess.GetFileAsBytes(OverrideRoot + p);
        return PakFiles.Read("game/" + p);
    }

    public bool Exists(string path)
    {
        var p = UiSettings.NormalizePath(path);
        return Godot.FileAccess.FileExists(OverrideRoot + p) || PakFiles.Exists("game/" + p);
    }
}

/// <summary>
/// UI textures from the pak (CryEngine DDS), cached per path. A PNG under <see cref="OverrideRoot"/> with the same pak
/// path (game/ui/.../name.png) replaces the pak texture, e.g. English versions of textures with baked-in Chinese text.
/// </summary>
public sealed class X2TextureCache
{
    private readonly UiSettings _settings;
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);

    public X2TextureCache(UiSettings settings, string overrideRoot = null)
    {
        _settings = settings;
        OverrideRoot = overrideRoot;
    }

    public int Count => _textures.Count;
    public string OverrideRoot { get; }
    public IReadOnlyDictionary<string, Texture2D> Loaded => _textures;

    private Texture2D LoadOverride(string resolved)
    {
        if (string.IsNullOrEmpty(OverrideRoot)) return null;
        var stem = "game/" + resolved;
        foreach (var candidate in new[] { $"{OverrideRoot}/{Path.ChangeExtension(stem, ".png")}", $"{OverrideRoot}/{stem}.png" })
        {
            if (!Godot.FileAccess.FileExists(candidate)) continue;
            var image = Image.LoadFromFile(ProjectSettings.GlobalizePath(candidate));
            if (image == null || image.IsEmpty()) continue;
            GD.Print($"X2 texture override: {candidate}");
            return ImageTexture.CreateFromImage(image);
        }
        return null;
    }

    public Texture2D Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var resolved = _settings.LocalizeTexturePath(path);
        if (_textures.TryGetValue(resolved, out var cached)) return cached;
        var texture = LoadOverride(resolved);
        if (texture != null)
        {
            _textures[resolved] = texture;
            return texture;
        }
        try
        {
            var bytes = _settings.Files.Read(resolved);
            if (bytes != null)
            {
                var image = new Image();
                if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) == Error.Ok && !image.IsEmpty())
                    texture = ImageTexture.CreateFromImage(image);
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"X2 texture {resolved}: {e.Message}");
        }
        if (texture == null) GD.PrintErr($"X2 texture missing: {resolved}");
        _textures[resolved] = texture;
        return texture;
    }
}

/// <summary>
/// The client fonts (game/fonts/fonts.g for the locale): font_main, font_sub, ... with their fallback chains, loaded
/// from the pak TTFs. Also measures text for the widget model.
/// </summary>
public sealed class X2FontCache : ITextMeasurer
{
    private readonly UiSettings _settings;
    private readonly Dictionary<string, Font> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int, string), float> _widths = new();

    public X2FontCache(UiSettings settings) => _settings = settings;

    public Font Get(string key)
    {
        key = string.IsNullOrEmpty(key) ? "font_main" : key;
        if (_fonts.TryGetValue(key, out var font)) return font;
        font = Build(key) ?? (key != "font_main" ? Get("font_main") : ThemeDB.FallbackFont);
        _fonts[key] = font;
        return font;
    }

    private Font Build(string key)
    {
        if (_settings.Fonts.TryGetValue(key, out var faces) && faces.Count > 0)
        {
            var files = faces.Select(f => LoadFile(f.Path)).Where(f => f != null).ToList();
            if (files.Count == 0) return null;
            var primary = files[0];
            if (files.Count > 1)
            {
                var chain = new Godot.Collections.Array<Font>();
                foreach (var f in files.Skip(1)) chain.Add(f);
                primary.Fallbacks = chain;
            }
            return primary;
        }
        // a direct path ("ui/font/xxx.ttf")
        return key.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? LoadFile(key) : null;
    }

    private FontFile LoadFile(string path)
    {
        var p = UiSettings.NormalizePath(path);
        if (_files.TryGetValue(p, out var cached)) return cached;
        FontFile file = null;
        var bytes = _settings.Files.Read(p);
        if (bytes != null)
        {
            file = new FontFile
            {
                Data = bytes,
                Antialiasing = TextServer.FontAntialiasing.Gray,
                Hinting = TextServer.Hinting.Light,
                SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
                GenerateMipmaps = false,
            };
        }
        else GD.PrintErr($"X2 font missing: {p}");
        _files[p] = file;
        return file;
    }

    public static int PixelSize(float size) => Math.Max(6, (int)Math.Round(size));

    public float Width(string fontKey, float size, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var px = PixelSize(size);
        var k = (fontKey ?? "", px, text);
        if (_widths.TryGetValue(k, out var w)) return w;
        w = Get(fontKey).GetStringSize(text, HorizontalAlignment.Left, -1, px).X;
        if (_widths.Count > 20000) _widths.Clear();
        _widths[k] = w;
        return w;
    }

    public float LineHeight(string fontKey, float size) => Get(fontKey).GetHeight(PixelSize(size));
}
