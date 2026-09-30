#nullable enable
using Godot;
using AAEmu.GodotViewer;

namespace AAEmu.GodotViewer.Maps;

/// <summary>Loads and caches map DDS textures from the opened game pak. Call texture methods on Godot's main thread.</summary>
public sealed class MapTextures
{
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);

    public Texture2D? Get(string pakPath)
    {
        var key = PakFiles.Normalize(pakPath);
        if (_textures.TryGetValue(key, out var cached)) return cached;
        var bytes = PakFiles.Read(key);
        if (bytes is null && key.Contains("/en_us/", StringComparison.Ordinal))
            bytes = PakFiles.Read(key.Replace("/en_us/", "/", StringComparison.Ordinal));
        if (bytes is null) return null;
        try
        {
            var image = new Image();
            if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(bytes)) != Error.Ok || image.IsEmpty()) return null;
            var texture = ImageTexture.CreateFromImage(image);
            _textures[key] = texture;
            return texture;
        }
        catch (Exception e)
        {
            GD.PushWarning($"Could not load map texture {key}: {e.Message}");
            return null;
        }
    }

    public void Clear() => _textures.Clear();
}
