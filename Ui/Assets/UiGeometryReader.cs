#nullable enable
using System.Globalization;
using System.Text.RegularExpressions;

namespace AAEmu.GodotViewer;

/// <summary>Pixel-space rectangle stored by an ArcheAge UI geometry entry.</summary>
public readonly record struct UiPixelRect(float X, float Y, float Width, float Height);

/// <summary>Left, top, right, and bottom stretch borders in source pixels.</summary>
public readonly record struct UiNineSliceInsets(float Left, float Top, float Right, float Bottom);

/// <summary>RGBA multiplier as stored by the geometry file; values are preserved without normalization.</summary>
public readonly record struct UiColor(float R, float G, float B, float A);

/// <summary>A named piece of a UI texture, with optional sizing and rendering metadata.</summary>
public sealed record UiGeometryRegion(
    string Name,
    UiPixelRect? Rect,
    UiNineSliceInsets? Insets,
    (float Width, float Height)? Extent,
    string? Type,
    IReadOnlyDictionary<string, UiColor> Colors);

/// <summary>Parsed contents of a CryEngine UI .g file. TexturePath is the conventional sibling .dds path.</summary>
public sealed record UiGeometryDocument(string? TexturePath, IReadOnlyList<UiGeometryRegion> Regions);

/// <summary>
/// Reads the line-oriented ArcheAge UI geometry DSL used by game/ui/*.g. Entries define named source-texture
/// rectangles and may add nine-slice insets, default extents, a draw type, and named RGBA multipliers.
/// </summary>
public static class UiGeometryReader
{
    private static readonly Regex PairPattern = new(@"^\s*(?<key>[A-Za-z0-9_]+)\s*\(\s*(?<values>[^)]*)\s*\)\s*$", RegexOptions.Compiled);

    /// <summary>Parse source text. Pass the sibling .dds path explicitly when it is known.</summary>
    public static UiGeometryDocument Parse(string source, string? texturePath = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var regions = new List<UiGeometryRegion>();
        var colors = new Dictionary<string, UiColor>(StringComparer.OrdinalIgnoreCase);
        string? name = null, type = null;
        UiPixelRect? rect = null;
        UiNineSliceInsets? insets = null;
        (float Width, float Height)? extent = null;
        var inColorBlock = false;

        void Finish()
        {
            if (name is null) return;
            regions.Add(new UiGeometryRegion(name, rect, insets, extent, type,
                new Dictionary<string, UiColor>(colors, StringComparer.OrdinalIgnoreCase)));
            name = null; rect = null; insets = null; extent = null; type = null;
            colors.Clear(); inColorBlock = false;
        }

        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = StripComment(line).Trim();
            if (trimmed.Length == 0) continue;
            var indented = char.IsWhiteSpace(line[0]);
            if (!indented)
            {
                Finish();
                name = trimmed;
                continue;
            }
            if (name is null) continue;
            if (trimmed.Equals("colors", StringComparison.OrdinalIgnoreCase))
            {
                inColorBlock = true;
                continue;
            }
            if (trimmed.StartsWith("type ", StringComparison.OrdinalIgnoreCase))
            {
                type = trimmed[5..].Trim(); inColorBlock = false; continue;
            }
            var match = PairPattern.Match(trimmed);
            if (!match.Success) continue;
            var key = match.Groups["key"].Value;
            if (inColorBlock)
            {
                if (TryParseValues(match.Groups["values"].Value, out var colorValues) && colorValues.Length == 4)
                    colors[key] = new UiColor(colorValues[0], colorValues[1], colorValues[2], colorValues[3]);
                continue;
            }
            var normalizedKey = key.ToLowerInvariant();
            if (normalizedKey is not ("coords" or "inset" or "extent")) continue;
            if (!TryParseValues(match.Groups["values"].Value, out var values)) continue;
            switch (normalizedKey)
            {
                case "coords" when values.Length == 4:
                    rect = new UiPixelRect(values[0], values[1], values[2], values[3]); break;
                case "inset" when values.Length == 4:
                    insets = new UiNineSliceInsets(values[0], values[1], values[2], values[3]); break;
                case "extent" when values.Length == 2:
                    extent = (values[0], values[1]); break;
            }
        }
        Finish();
        return new UiGeometryDocument(texturePath?.Replace('\\', '/'), regions);
    }

    /// <summary>Read a UTF-8 .g file and infer its conventional sibling .dds texture path.</summary>
    public static UiGeometryDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var texture = Path.ChangeExtension(path, ".dds").Replace('\\', '/');
        return Parse(File.ReadAllText(path), texture);
    }

    private static bool TryParseValues(string text, out float[] values)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        values = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        return true;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        var dash = line.IndexOf("--", StringComparison.Ordinal);
        var end = hash < 0 ? dash : dash < 0 ? hash : Math.Min(hash, dash);
        return end < 0 ? line : line[..end];
    }
}
