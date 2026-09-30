#nullable enable

using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace AAEmu.GodotViewer.Client;

/// <summary>One client housing-area shape: a closed polygon in world metres and the housing_areas row it stands for.</summary>
/// <param name="AreaId">The shape's value1: housing_areas.id.</param>
/// <param name="GroupId">The shape's Group: housing_groups.id.</param>
public sealed record HousingAreaShape(string Name, int AreaId, int GroupId, IReadOnlyList<Vector2> Polygon, float Z)
{
    public (Vector2 Min, Vector2 Max) Bounds { get; } = (
        new Vector2(Polygon.Min(p => p.X), Polygon.Min(p => p.Y)),
        new Vector2(Polygon.Max(p => p.X), Polygon.Max(p => p.Y)));

    /// <summary>Even-odd point-in-polygon test in world X/Y.</summary>
    public bool Contains(float x, float y)
    {
        if (x < Bounds.Min.X || y < Bounds.Min.Y || x > Bounds.Max.X || y > Bounds.Max.Y)
            return false;
        var inside = false;
        for (int i = 0, j = Polygon.Count - 1; i < Polygon.Count; j = i++)
        {
            var a = Polygon[i];
            var b = Polygon[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// The housing areas the original client knows: <c>game/worlds/&lt;world&gt;/level_design/zone/&lt;zone&gt;/client/housing_area.xml</c>
/// (AreaShape entities; Pos is relative to the zone's origin cell from world.xml plus the entity's cellX/cellY,
/// points are relative to Pos). In Solzreed (zone 179) these are the "moang" plots whose value1 are
/// housing_areas 337/338/356-360. The builder uses them for its placement feedback; the server decides.
/// </summary>
public sealed class HousingAreaCatalog
{
    private const float CellSize = 1024f;
    private readonly string _worldRoot;
    private readonly ConcurrentDictionary<int, IReadOnlyList<HousingAreaShape>> _zones = new();
    private Dictionary<int, (int X, int Y)>? _origins;

    public HousingAreaCatalog(string worldName = "main_world") => _worldRoot = $"game/worlds/{worldName}";

    /// <summary>All housing-area shapes of a zone (zones.zone_key), or none.</summary>
    public IReadOnlyList<HousingAreaShape> ForZone(int zoneKey) => zoneKey < 0 ? [] : _zones.GetOrAdd(zoneKey, Load);

    /// <summary>The first shape containing the world point, or null.</summary>
    public HousingAreaShape? AreaAt(int zoneKey, float x, float y) =>
        ForZone(zoneKey).FirstOrDefault(shape => shape.Contains(x, y));

    private IReadOnlyList<HousingAreaShape> Load(int zoneKey)
    {
        try
        {
            var origins = _origins ??= LoadOrigins();
            if (!origins.TryGetValue(zoneKey, out var origin))
                return [];
            var text = PakFiles.ReadText($"{_worldRoot}/level_design/zone/{zoneKey}/client/housing_area.xml");
            if (string.IsNullOrWhiteSpace(text))
                return [];
            var shapes = new List<HousingAreaShape>();
            foreach (var entity in XDocument.Parse(text).Root?.Elements("Entity") ?? [])
            {
                var area = entity.Element("Area");
                if (area is null || ParseVector(entity.Attribute("Pos")?.Value) is not { } pos)
                    continue;
                var cellX = (int?)entity.Attribute("cellX") ?? 0;
                var cellY = (int?)entity.Attribute("cellY") ?? 0;
                var baseX = (origin.X + cellX) * CellSize + pos.X;
                var baseY = (origin.Y + cellY) * CellSize + pos.Y;
                var points = area.Element("Points")?.Elements("Point")
                    .Select(point => ParseVector(point.Attribute("Pos")?.Value))
                    .Where(point => point is not null)
                    .Select(point => new Vector2(baseX + point!.Value.X, baseY + point.Value.Y))
                    .ToArray() ?? [];
                if (points.Length < 3)
                    continue;
                shapes.Add(new HousingAreaShape(entity.Attribute("Name")?.Value ?? "",
                    (int?)area.Attribute("value1") ?? 0, (int?)area.Attribute("Group") ?? 0, points, pos.Z));
            }
            return shapes;
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or FormatException or IOException)
        {
            Godot.GD.PrintErr($"[housing] housing_area.xml of zone {zoneKey}: {exception.Message}");
            return [];
        }
    }

    private Dictionary<int, (int X, int Y)> LoadOrigins()
    {
        var origins = new Dictionary<int, (int X, int Y)>();
        var text = PakFiles.ReadText($"{_worldRoot}/world.xml");
        if (string.IsNullOrWhiteSpace(text))
            return origins;
        foreach (var zone in XDocument.Parse(text).Descendants("Zone"))
        {
            var id = (int?)zone.Attribute("id");
            var x = (int?)zone.Attribute("originX");
            var y = (int?)zone.Attribute("originY");
            if (id is { } zoneId && x is { } originX && y is { } originY)
                origins[zoneId] = (originX, originY);
        }
        return origins;
    }

    private static Vector3? ParseVector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Split(',');
        if (parts.Length < 3)
            return null;
        return new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}
