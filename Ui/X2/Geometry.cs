#nullable enable
using System.Globalization;

namespace AAEmu.GodotViewer.Ui.X2;

public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Contains(float x, float y) => x >= X && y >= Y && x < Right && y < Bottom;
}

public readonly record struct UiInsets(float Left, float Top, float Right, float Bottom);

public readonly record struct UiColor(float R, float G, float B, float A = 1)
{
    public static readonly UiColor White = new(1, 1, 1, 1);
    public UiColor Multiply(UiColor o) => new(R * o.R, G * o.G, B * o.B, A * o.A);
    public UiColor WithAlpha(float a) => new(R, G, B, A * a);
}

/// <summary>One named region of a texture's .g file (or one entry of a ui/setting/*.g colour file).</summary>
public sealed record TextureData(
    string Name,
    UiRect? Coords,
    UiInsets? Inset,
    (float Width, float Height)? Extent,
    string? Type,
    IReadOnlyDictionary<string, UiColor> Colors,
    UiColor? Color);

public sealed record GeometryDocument(string? TexturePath, IReadOnlyDictionary<string, TextureData> Regions);

/// <summary>A line of an indentation-structured .g file: the first word is the key, the rest the value.</summary>
public sealed class GNode
{
    public string Key = "";
    public string Value = "";
    public int Indent;
    public List<GNode> Children { get; } = [];

    public GNode? Child(string key) => Children.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    public string? ChildValue(string key) => Child(key)?.Value;

    /// <summary>Numbers inside "( a, b, c )" (or bare "a b c"); null when a value is not a number.</summary>
    public float[]? Numbers => GFile.ParseNumbers(Value);
    public override string ToString() => $"{Key} {Value} ({Children.Count})";
}

/// <summary>Parser for the line-oriented game/ui/*.g and ui/setting/*.g formats.</summary>
public static class GFile
{
    public static List<GNode> Parse(string source)
    {
        var roots = new List<GNode>();
        var stack = new List<GNode>();
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } raw)
        {
            var line = StripComment(raw.TrimStart('﻿'));
            if (line.Trim().Length == 0)
                continue;
            var indent = 0;
            foreach (var ch in line)
            {
                if (ch == ' ') indent++;
                else if (ch == '\t') indent += 4;
                else break;
            }
            var text = line.Trim();
            var split = 0;
            while (split < text.Length && !char.IsWhiteSpace(text[split]) && text[split] != '(')
                split++;
            var node = new GNode
            {
                Key = text[..split],
                Value = text[split..].Trim().Trim('"'),
                Indent = indent,
            };
            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) roots.Add(node);
            else stack[^1].Children.Add(node);
            stack.Add(node);
        }
        return roots;
    }

    public static float[]? ParseNumbers(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('(')) text = text.Trim('(', ')', ' ');
        if (text.Length == 0) return null;
        var parts = text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var result = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                return null;
        return result;
    }

    /// <summary>Colour values are either 0..1 floats or 0..255 bytes; any component above 1 means bytes.</summary>
    public static UiColor? ToColor(float[]? v)
    {
        if (v is null || v.Length < 3) return null;
        var bytes = v.Any(x => x > 1);
        var d = bytes ? 255f : 1f;
        var a = v.Length > 3 ? v[3] : d;
        return new UiColor(v[0] / d, v[1] / d, v[2] / d, a / d);
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        var dash = line.IndexOf("--", StringComparison.Ordinal);
        var end = hash < 0 ? dash : dash < 0 ? hash : Math.Min(hash, dash);
        return end < 0 ? line : line[..end];
    }
}

/// <summary>Texture region documents (same stem as the .dds).</summary>
public static class GeometryReader
{
    public static GeometryDocument Parse(string source, string? texturePath = null)
    {
        var regions = new Dictionary<string, TextureData>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in GFile.Parse(source))
            regions[node.Key] = ToTextureData(node);
        return new GeometryDocument(texturePath?.Replace('\\', '/'), regions);
    }

    public static TextureData ToTextureData(GNode node)
    {
        UiRect? coords = null;
        UiInsets? inset = null;
        (float, float)? extent = null;
        string? type = null;
        UiColor? color = null;
        var colors = new Dictionary<string, UiColor>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in node.Children)
        {
            var v = c.Numbers;
            switch (c.Key.ToLowerInvariant())
            {
                case "coords" when v is { Length: 4 }: coords = new UiRect(v[0], v[1], v[2], v[3]); break;
                case "inset" when v is { Length: 4 }: inset = new UiInsets(v[0], v[1], v[2], v[3]); break;
                case "extent" when v is { Length: 2 }: extent = (v[0], v[1]); break;
                case "type": type = c.Value.Trim(); break;
                case "color": color = GFile.ToColor(v); break;
                case "colors":
                    foreach (var cc in c.Children)
                        if (GFile.ToColor(cc.Numbers) is { } col)
                            colors[cc.Key] = col;
                    break;
            }
        }
        return new TextureData(node.Key, coords, inset, extent, type, colors, color);
    }
}

public readonly record struct SlicePatch(UiRect Source, UiRect Destination);

public static class NineSliceGeometry
{
    public static IReadOnlyList<SlicePatch> Build(UiRect source, UiRect destination, UiInsets inset)
    {
        static (float A, float B) Fit(float available, float a, float b)
        {
            var total = a + b;
            if (total <= Math.Abs(available) || total <= 0) return (a, b);
            var ratio = Math.Abs(available) / total;
            return (a * ratio, b * ratio);
        }

        var (dl, dr) = Fit(destination.Width, inset.Left, inset.Right);
        var (dt, db) = Fit(destination.Height, inset.Top, inset.Bottom);
        var sx = new[] { source.X, source.X + inset.Left, source.Right - inset.Right, source.Right };
        var sy = new[] { source.Y, source.Y + inset.Top, source.Bottom - inset.Bottom, source.Bottom };
        var dx = new[] { destination.X, destination.X + dl, destination.Right - dr, destination.Right };
        var dy = new[] { destination.Y, destination.Y + dt, destination.Bottom - db, destination.Bottom };
        var result = new List<SlicePatch>(9);
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
            {
                var s = new UiRect(sx[x], sy[y], sx[x + 1] - sx[x], sy[y + 1] - sy[y]);
                // insets that meet (e.g. main_bg: 140 + 140 of 280) leave no middle: the client stretches the
                // pixel column/row at the seam across the gap
                if (x == 1 && s.Width <= 0) s = s with { X = sx[1] - 0.5f, Width = 1 };
                if (y == 1 && s.Height <= 0) s = s with { Y = sy[1] - 0.5f, Height = 1 };
                var d = new UiRect(dx[x], dy[y], dx[x + 1] - dx[x], dy[y + 1] - dy[y]);
                if (d.Width > 0.01f && d.Height > 0.01f && s.Width > 0 && s.Height > 0)
                    result.Add(new SlicePatch(s, d));
            }
        return result;
    }

    /// <summary>Horizontal three-part: fixed left/right caps, stretched middle, full height.</summary>
    public static IReadOnlyList<SlicePatch> BuildThree(UiRect source, UiRect destination, UiInsets inset)
        => Build(source, destination, new UiInsets(inset.Left, 0, inset.Right, 0));
}
