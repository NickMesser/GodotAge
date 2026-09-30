#nullable enable
using Godot;
using AAEmu.GodotViewer;

namespace AAEmu.GodotViewer.Data;

/// <summary>Loads item/UI DDS icons from the opened game pak and memoizes their Godot textures.</summary>
public sealed class IconLibrary
{
    private readonly GameData _data;
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IconLibrary(GameData data) => _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary>Resolves an icon database ID through the icons table, then loads the DDS from the pak.</summary>
    public Texture2D? GetIcon(int iconId)
    {
        var path = _data.IconPath(iconId);
        return path is null ? null : GetIcon(path);
    }

    /// <summary>Loads an icon by pak path or by an icon filename from the icons table.</summary>
    public Texture2D? GetIcon(string path) => Load(path);

    /// <summary>Gets the item-grade border texture. The item's grade icon is the frame overlay used by the Lua UI.</summary>
    public Texture2D? GetGradeFrame(int gradeId)
    {
        var grade = _data.GetItemGrade(gradeId);
        return grade is null || grade.IconPath is null ? null : Load(grade.IconPath);
    }

    private Texture2D? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (!normalized.Contains('/'))
            normalized = "game/ui/icon/" + normalized;
        else if (!normalized.StartsWith("game/", StringComparison.Ordinal))
            normalized = "game/" + normalized;

        lock (_gate)
        {
            if (_textures.TryGetValue(normalized, out var existing))
                return existing;

            Texture2D? texture = null;
            var dds = PakFiles.Read(normalized);
            if (dds is not null)
            {
                try
                {
                    var image = new Image();
                    if (image.LoadDdsFromBuffer(CryDds.PrepareForGodot(dds)) == Error.Ok && !image.IsEmpty())
                        texture = ImageTexture.CreateFromImage(image);
                }
                catch (Exception e)
                {
                    GD.PrintErr($"Could not load icon {normalized}: {e.Message}");
                }
            }

            _textures[normalized] = texture;
            return texture;
        }
    }
}
