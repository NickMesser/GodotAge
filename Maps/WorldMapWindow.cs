using Godot;
#nullable enable
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Maps;

public enum WorldMapLevel { World, Continent, Zone, City }

/// <summary>Interactive map canvas that composes client zone maps at their DB world bounds.</summary>
public partial class WorldMapWindow : Control
{
    public MapDataCatalog? Catalog { get; set; }
    public MapTextures? Textures { get; set; }
    public IMapMarkerSource? MarkerSource { get; set; }
    public NVector3 PlayerWorldPosition { get; set; }
    public int ZoneKey { get; private set; }
    public WorldMapLevel Level { get; private set; } = WorldMapLevel.Continent;
    public float Zoom { get; private set; } = 1f;
    public Vector2 Pan { get; private set; }
    private bool _dragging;
    private Vector2 _lastMouse;

    public void ShowZone(int zoneKey)
    {
        ZoneKey = zoneKey;
        Level = Catalog?.TryByZoneKey(zoneKey, out _) == true ? WorldMapLevel.Zone : WorldMapLevel.Continent;
        Zoom = 1f; Pan = Vector2.Zero; QueueRedraw();
    }

    public void ShowContinent()
    {
        Level = WorldMapLevel.Continent; ZoneKey = 0; Zoom = 1f; Pan = Vector2.Zero; QueueRedraw();
    }

    public void ShowWorld()
    {
        Level = WorldMapLevel.World; ZoneKey = 0; Zoom = 1f; Pan = Vector2.Zero; QueueRedraw();
    }

    public void ShowCity()
    {
        Level = WorldMapLevel.City; Zoom = 1f; Pan = Vector2.Zero; QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
        {
            _dragging = mouse.Pressed; _lastMouse = mouse.Position; AcceptEvent();
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            Pan += motion.Position - _lastMouse; _lastMouse = motion.Position; QueueRedraw(); AcceptEvent();
        }
        else if (@event is InputEventMouseButton { Pressed: true } wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            var old = Zoom;
            Zoom = Mathf.Clamp(Zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1.2f : 1f / 1.2f), 0.2f, 8f);
            Pan = wheel.Position - (wheel.Position - Pan) * (Zoom / old);
            QueueRedraw(); AcceptEvent();
        }
        else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape } && Level is WorldMapLevel.Zone or WorldMapLevel.City)
        { ShowContinent(); AcceptEvent(); }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("111720"));
        if (Catalog is null || Textures is null || Catalog.Maps.Count == 0) return;
        MapData[] maps = Level switch
        {
            WorldMapLevel.World => Catalog.WorldMaps.Where(m => m.Key.Equals("main_world", StringComparison.OrdinalIgnoreCase)).ToArray(),
            WorldMapLevel.Continent => Catalog.WorldMaps.Where(m => m.Contains(new Vector2(PlayerWorldPosition.X, PlayerWorldPosition.Y)) &&
                !m.Key.Equals("main_world", StringComparison.OrdinalIgnoreCase)).Take(1).ToArray(),
            WorldMapLevel.Zone when Catalog.TryByZoneKey(ZoneKey, out var current) => [current],
            WorldMapLevel.City => Catalog.SubZoneMaps.Where(m => m.Contains(new Vector2(PlayerWorldPosition.X, PlayerWorldPosition.Y))).Take(1).ToArray(),
            _ => Catalog.ZoneMaps.ToArray()
        };
        if (maps.Length == 0) maps = Catalog.ZoneMaps.ToArray();
        var bounds = GetBounds(maps);
        var margin = new Vector2(32, 54);
        var area = new Rect2(margin, Size - margin * 2);
        var fit = Mathf.Min(area.Size.X / bounds.Z, area.Size.Y / bounds.W) * Zoom;
        var offset = area.GetCenter() + Pan;
        foreach (var map in maps)
        {
            var dest = WorldRectToScreen(map.WorldX, map.WorldY, map.WorldWidth, map.WorldHeight, bounds, fit, offset);
            var texture = Textures.Get(map.ImagePath);
            if (texture is not null) DrawTextureRectRegion(texture, dest, map.ImageRect, Colors.White);
            else { DrawRect(dest, new Color("384451")); DrawRect(dest, new Color("708090"), false, 1f); }
            DrawRect(dest, new Color(0.8f, 0.84f, 0.9f, 0.5f), false, 1f);
            var titleAt = new Vector2(dest.Position.X + 5, dest.Position.Y + 19);
            DrawString(ThemeDB.FallbackFont, titleAt, map.DisplayName, HorizontalAlignment.Left, -1, 13, new Color("f0e8d6"));
            DrawMapMarkers(map, bounds, fit, offset, dest);
        }
        var player = WorldToScreen(new Vector2(PlayerWorldPosition.X, PlayerWorldPosition.Y), bounds, fit, offset);
        DrawCircle(player, 6, new Color("86d2ff"));
        DrawArc(player, 7, 0, Mathf.Tau, 24, new Color("101820"), 1.5f);
        DrawRect(new Rect2(new Vector2(12, 10), new Vector2(Size.X - 24, 30)), new Color("101720"));
        var title = Level is WorldMapLevel.Zone or WorldMapLevel.City && maps.Length > 0 ? maps[0].DisplayName :
            Level == WorldMapLevel.World ? "World Map" : maps.FirstOrDefault()?.DisplayName ?? "World Map";
        DrawString(ThemeDB.FallbackFont, new Vector2(22, 31), title, HorizontalAlignment.Left, -1, 17, new Color("efe5d0"));
    }

    private void DrawMapMarkers(MapData map, Vector4 bounds, float fit, Vector2 offset, Rect2 mapRect)
    {
        if (MarkerSource is null) return;
        foreach (var marker in MarkerSource.GetMarkers(map))
        {
            var p = WorldToScreen(new Vector2(marker.WorldPosition.X, marker.WorldPosition.Y), bounds, fit, offset);
            if (!mapRect.HasPoint(p)) continue;
            var radius = Mathf.Clamp(4f * Mathf.Sqrt(Zoom), 3f, 8f);
            MinimapControl.DrawMarker(this, Textures, p, marker, radius);
            if (!string.IsNullOrWhiteSpace(marker.Label))
                DrawString(ThemeDB.FallbackFont, p + new Vector2(radius + 3, 4), marker.Label, HorizontalAlignment.Left, -1, 11, new Color("f0eadc"));
        }
    }

    private static Vector4 GetBounds(IEnumerable<MapData> maps)
    {
        var list = maps.ToArray();
        var x0 = list.Min(m => m.WorldX); var y0 = list.Min(m => m.WorldY);
        var x1 = list.Max(m => m.WorldX + m.WorldWidth); var y1 = list.Max(m => m.WorldY + m.WorldHeight);
        return new Vector4(x0, y0, x1 - x0, y1 - y0);
    }

    private static Rect2 WorldRectToScreen(float x, float y, float w, float h, Vector4 bounds, float scale, Vector2 center) =>
        new(WorldToScreen(new Vector2(x, y + h), bounds, scale, center), new Vector2(w * scale, h * scale));

    private static Vector2 WorldToScreen(Vector2 world, Vector4 bounds, float scale, Vector2 center) =>
        center + new Vector2((world.X - (bounds.X + bounds.Z * 0.5f)) * scale,
            ((bounds.Y + bounds.W * 0.5f) - world.Y) * scale);
}
