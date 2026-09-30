#nullable enable
using Godot;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Maps;

public enum MapResourceLevel { World, Zone, SubZone }

/// <summary>One client map image and its world rectangle and destination rectangle within that image.</summary>
public sealed record MapData(
    string Key,
    string DisplayName,
    string ImagePath,
    float WorldX,
    float WorldY,
    float WorldWidth,
    float WorldHeight,
    int ZoneGroupId,
    IReadOnlyList<int> ZoneKeys,
    MapResourceLevel Level,
    Rect2 ImageRect)
{
    public Vector2 WorldToPixel(Vector2 world, int imageWidth, int imageHeight) => new(
        (ImageRect.Position.X + (world.X - WorldX) / WorldWidth * ImageRect.Size.X) * imageWidth / 928f,
        (ImageRect.Position.Y + (WorldY + WorldHeight - world.Y) / WorldHeight * ImageRect.Size.Y) * imageHeight / 556f);

    public Vector2 PixelToWorld(Vector2 pixel, int imageWidth, int imageHeight) => new(
        WorldX + (pixel.X * 928f / imageWidth - ImageRect.Position.X) / ImageRect.Size.X * WorldWidth,
        WorldY + WorldHeight - (pixel.Y * 556f / imageHeight - ImageRect.Position.Y) / ImageRect.Size.Y * WorldHeight);

    public bool Contains(Vector2 world) => world.X >= WorldX && world.X <= WorldX + WorldWidth &&
        world.Y >= WorldY && world.Y <= WorldY + WorldHeight;
}

/// <summary>Read-only map catalog built from map_resources, world_groups, zone_groups, sub_zones and localized_texts.</summary>
public sealed class MapDataCatalog
{
    private readonly Dictionary<int, MapData> _byZoneKey;
    private readonly Dictionary<string, MapData> _byKey;
    public IReadOnlyList<MapData> Maps { get; }
    public IReadOnlyList<MapData> WorldMaps { get; }
    public IReadOnlyList<MapData> ZoneMaps { get; }
    public IReadOnlyList<MapData> SubZoneMaps { get; }

    private MapDataCatalog(List<MapData> maps, Dictionary<int, MapData> byZoneKey)
    {
        Maps = maps;
        WorldMaps = maps.Where(m => m.Level == MapResourceLevel.World).ToArray();
        ZoneMaps = maps.Where(m => m.Level == MapResourceLevel.Zone).ToArray();
        SubZoneMaps = maps.Where(m => m.Level == MapResourceLevel.SubZone).ToArray();
        _byZoneKey = byZoneKey;
        // Several maps share a key (e.g. a zone group and its sub-zone): the first, broadest one wins.
        _byKey = new Dictionary<string, MapData>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in maps)
            _byKey.TryAdd(m.Key, m);
    }

    public static MapDataCatalog LoadFromDatabase(string databasePath, string locale = "en_us")
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        var records = new List<MapData>();
        var keys = new Dictionary<int, MapData>();
        using var world = db.CreateCommand();
        world.CommandText = """
            SELECT wg.id, wg.name, wg.x, wg.y, wg.w, wg.h, wg.image_x, wg.image_y, wg.image_w, wg.image_h,
                   COALESCE(NULLIF(lt.en_us, ''), wg.display_text, wg.name), mr.folder_name
            FROM map_resources mr JOIN world_groups wg ON mr.map_target_type='WorldGroup' AND mr.map_target_id=wg.id
            LEFT JOIN localized_texts lt ON lt.tbl_name='world_groups' AND lt.tbl_column_name='display_text' AND lt.idx=wg.id
            WHERE mr.enable='t' AND wg.w>0 AND wg.h>0 ORDER BY wg.id
            """;
        using (var r = world.ExecuteReader()) while (r.Read())
            records.Add(new MapData(r.GetString(1), r.GetString(10), Image(r.GetString(11), locale),
                r.GetFloat(2), r.GetFloat(3), r.GetFloat(4), r.GetFloat(5), 0, Array.Empty<int>(),
                MapResourceLevel.World, new Rect2(r.GetFloat(6), r.GetFloat(7), r.GetFloat(8), r.GetFloat(9))));

        using var zones = db.CreateCommand();
        zones.CommandText = """
            SELECT g.id, g.name, g.x, g.y, g.w, g.h,
                   COALESCE(NULLIF(lt.en_us, ''), g.display_text, g.name), mr.folder_name, z.zone_key
            FROM map_resources mr JOIN zone_groups g ON mr.map_target_type='ZoneGroup' AND mr.map_target_id=g.id
            LEFT JOIN zones z ON z.group_id=g.id
            LEFT JOIN localized_texts lt ON lt.tbl_name='zone_groups' AND lt.tbl_column_name='display_text' AND lt.idx=g.id
            WHERE mr.enable='t' AND mr.folder_name IS NOT NULL AND g.w>0 AND g.h>0
            ORDER BY g.id, z.zone_key
            """;
        var groups = new Dictionary<int, (string name, string folder, float x, float y, float w, float h, HashSet<int> zones)>();
        using (var r = zones.ExecuteReader()) while (r.Read())
        {
            var id = r.GetInt32(0);
            if (!groups.TryGetValue(id, out var g))
                groups[id] = g = (r.GetString(6), r.GetString(7), r.GetFloat(2), r.GetFloat(3), r.GetFloat(4), r.GetFloat(5), new HashSet<int>());
            if (!r.IsDBNull(8)) g.zones.Add(r.GetInt32(8));
        }
        foreach (var (id, g) in groups)
        {
            var map = new MapData(g.folder, g.name, Image(g.folder, locale), g.x, g.y, g.w, g.h, id,
                g.zones.Order().ToArray(), MapResourceLevel.Zone, new Rect2(0, 0, 928, 556));
            records.Add(map);
            foreach (var zoneKey in g.zones) keys.TryAdd(zoneKey, map);
        }

        using var sub = db.CreateCommand();
        sub.CommandText = """
            SELECT s.id, s.name, s.x, s.y, s.w, s.h, s.linked_zone_group_id, mr.folder_name,
                   COALESCE(NULLIF(lt.en_us, ''), s.name)
            FROM map_resources mr JOIN sub_zones s ON mr.map_target_type='SubZone' AND mr.map_target_id=s.id
            LEFT JOIN localized_texts lt ON lt.tbl_name='sub_zones' AND lt.tbl_column_name='name' AND lt.idx=s.id
            WHERE mr.enable='t' AND s.w>0 AND s.h>0 ORDER BY s.id
            """;
        using (var r = sub.ExecuteReader()) while (r.Read())
            records.Add(new MapData(r.GetString(7), r.GetString(8), Image(r.GetString(7), locale),
                r.GetFloat(2), r.GetFloat(3), r.GetFloat(4), r.GetFloat(5), r.GetInt32(6), Array.Empty<int>(),
                MapResourceLevel.SubZone, new Rect2(0, 0, 928, 556)));

        return new MapDataCatalog(records, keys);
    }

    private static string Image(string folder, string locale) =>
        $"game/ui/map/map_resources/{folder}/{locale.ToLowerInvariant()}/world.dds";

    public bool TryByZoneKey(int zoneKey, out MapData map) => _byZoneKey.TryGetValue(zoneKey, out map!);
    public bool TryByKey(string key, out MapData map) => _byKey.TryGetValue(key, out map!);

    public bool TryByWorldPosition(Vector2 world, out MapData map)
    {
        map = Maps.Where(m => m.Level != MapResourceLevel.World && m.Contains(world))
            .OrderBy(m => m.WorldWidth * m.WorldHeight).FirstOrDefault()!;
        return map is not null;
    }
}
