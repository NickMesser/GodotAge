#nullable enable
using Godot;
using AAEmu.GodotViewer.Maps;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>World state supplied by the game host to both native map canvases.</summary>
public interface IX2MapProvider
{
    X2MapPlayer? Player { get; }
    int? CurrentZoneKey { get; }
    IReadOnlyList<X2MapMarker> Markers { get; }
    X2MapSextant? GetSextant(float worldX, float worldY);
    Dictionary<object, object?>? GetClimateInfo(int zoneId);
}

public sealed record X2MapPlayer(float X, float Y, float Yaw);
public sealed record X2MapMarker(string Key, string Category, float X, float Y,
    string? TexturePath = null, string? TextureRegion = null, bool Visible = true, int IconType = 0);
public sealed record X2MapSextant(string Longitude, int DegLong, int MinLong, int SecLong,
    string Latitude, int DegLat, int MinLat, int SecLat)
{
    public Dictionary<object, object?> ToLuaTable() => new()
    {
        ["longitude"] = Longitude, ["deg_long"] = DegLong, ["min_long"] = MinLong, ["sec_long"] = SecLong,
        ["latitude"] = Latitude, ["deg_lat"] = DegLat, ["min_lat"] = MinLat, ["sec_lat"] = SecLat,
    };
}

/// <summary>Script-facing native roadmap/worldmap state. Map textures are selected from MapDataCatalog by the renderer.</summary>
public sealed class MapWidget : Widget
{
    private readonly Dictionary<(int Kind, int Zone), TextureDrawable> _icons = [];
    private readonly Dictionary<(int Level, int Zone), TextureDrawable> _routes = [];
    private readonly HashSet<string> _activeCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, UiColor> _troubleColors = [];
    private readonly Dictionary<string, object?> _questInfo = [];
    private readonly Dictionary<int, bool> _filters = [];
    private readonly Dictionary<string, float> _layerAlphas = new(StringComparer.OrdinalIgnoreCase);
    private Widget? _tooltipController;
    private bool _pingMode;
    private readonly Dictionary<object, (Widget Frame, TextureDrawable Drawable)> _pingWidgets = [];

    internal MapWidget(UiRoot root, string typeName, string id, Widget? parent)
        : base(root, typeName, id, parent) { }

    public bool IsRoadMap => TypeName.Equals("roadmap", StringComparison.OrdinalIgnoreCase);
    public float MapSize { get; private set; } = 300;
    /// <summary>The roadmap calls SetMapSize and uses a player-centred local projection; the world map does not.</summary>
    public bool PlayerCenteredProjection { get; private set; }
    public float ExpandRatio { get; private set; } = 1;
    public float ZoomRatio { get; private set; } = 1;
    public bool RoadMapNpcEnabled { get; private set; } = true;
    public bool LeaderPingVisible { get; private set; } = true;
    public bool PortalVisible { get; private set; } = true;
    public bool CommonFarmVisible { get; private set; } = true;
    public bool QuestVisible { get; private set; } = true;
    public bool CursorReset { get; private set; }
    public string? MapFilePath { get; private set; }
    public string? ButtonTexturePath { get; private set; }
    public TextureDrawable? PlayerDrawable { get; private set; }
    public TextureDrawable? PortalDrawable { get; private set; }
    public TextureDrawable? CommonFarmDrawable { get; private set; }
    public IX2MapProvider? Provider { get; set; }
    public MapDataCatalog? Catalog { get; set; }
    public X2MapPlayer? PlayerWorldPosition => Provider?.Player;
    public int? CurrentZoneKey => Provider?.CurrentZoneKey;
    public IReadOnlyList<X2MapMarker> Markers => Provider?.Markers ?? Array.Empty<X2MapMarker>();
    public IReadOnlyCollection<string> ActiveCategories => _activeCategories;
    public IReadOnlyDictionary<int, UiColor> TroubleColors => _troubleColors;
    public UiColor FestivalZoneColor { get; private set; } = UiColor.White;
    public object? PrimaryTooltipColor { get; private set; }
    public object? SecondaryTooltipColor { get; private set; }
    public IReadOnlyDictionary<string, object?> QuestInfo => _questInfo;
    public IReadOnlyDictionary<int, bool> Filters => _filters;
    public IReadOnlyDictionary<string, float> LayerAlphas => _layerAlphas;
    public IReadOnlyDictionary<object, (Widget Frame, TextureDrawable Drawable)> PingWidgets => _pingWidgets;
    public void SetPingWidget(Widget frame, TextureDrawable drawable, object? pingType)
        => _pingWidgets[pingType ?? "default"] = (frame, drawable);
    public IReadOnlyCollection<TextureDrawable> IconDrawables => _icons.Values;
    public IReadOnlyCollection<TextureDrawable> RouteDrawables => _routes.Values;
    public object? PortalTarget { get; private set; }
    public object? FarmTarget { get; private set; }
    public object? QuestTarget { get; private set; }
    public object? SkillEffect { get; private set; }
    public object? TempNotifyColor { get; private set; }
    public object? MainNotifyCoord { get; private set; }
    public object? NormalNotifyCoord { get; private set; }

    public MapData? GetMapData(MapDataCatalog catalog)
    {
        if (CurrentZoneKey is int key && catalog.TryByZoneKey(key, out var zone)) return zone;
        if (PlayerWorldPosition is { } p && catalog.TryByWorldPosition(new Vector2(p.X, p.Y), out var found)) return found;
        return IsRoadMap
            ? catalog.ZoneMaps.FirstOrDefault() ?? catalog.WorldMaps.FirstOrDefault()
            : catalog.WorldMaps.FirstOrDefault() ?? catalog.ZoneMaps.FirstOrDefault();
    }

    public void SetMapSize(float size)
    {
        MapSize = Math.Max(1, size);
        PlayerCenteredProjection = true;
    }
    public void SetExpandRatio(float ratio) => ExpandRatio = Math.Clamp(ratio, 0.01f, 4f);
    public void SetZoomRatio(float ratio) => ZoomRatio = Math.Clamp(ratio, 0.01f, 4f);

    /// <summary>Projects a nearby world marker with the same player-centred transform used by the roadmap renderer.</summary>
    public static Vector2 ProjectPlayerCenteredMarker(UiRect rect, X2MapPlayer player, X2MapMarker marker,
        Vector2 playerScreen, float expandRatio, float zoomRatio)
    {
        var scale = rect.Width * Math.Max(0.1f, expandRatio * zoomRatio) / 1200f;
        return new Vector2(playerScreen.X + (marker.X - player.X) * scale,
            playerScreen.Y - (marker.Y - player.Y) * scale);
    }
    public void SetRoadMapNpc(bool enabled) => RoadMapNpcEnabled = enabled;
    public void ShowLeaderPing(bool visible) => LeaderPingVisible = visible;
    public void SetPlayerDrawable(TextureDrawable drawable) => PlayerDrawable = drawable;
    public void SetPortalDrawable(TextureDrawable drawable) => PortalDrawable = drawable;
    public void SetCommonFarmDrawable(TextureDrawable drawable) => CommonFarmDrawable = drawable;
    public void SetTooltipColor(object? primary, object? secondary) { PrimaryTooltipColor = primary; SecondaryTooltipColor = secondary; }
    public void SetMapFilter(double iconType, bool visible) => _filters[(int)iconType] = visible;
    public bool IsCheckedMapFilter(double iconType) => _filters.GetValueOrDefault((int)iconType, true);
    public void SetDrawableLayerAlpha(float alpha, string layer) => _layerAlphas[layer] = Math.Clamp(alpha, 0, 1);
    public void SetTroubleZoneColor(double state, float r, float g, float b, float a) => _troubleColors[(int)state] = new(r, g, b, a);
    public void SetFestivalZoneColor(float r, float g, float b, float a) => FestivalZoneColor = new(r, g, b, a);
    public void InitMapData(float width = 0, float height = 0, string? filePath = null, string? buttonTexturePath = null)
    {
        if (width > 0 && height > 0) SetExtent(width, height);
        MapFilePath = filePath;
        ButtonTexturePath = buttonTexturePath;
    }

    public LuaMulti GetPlayerViewPos()
    {
        if (PlayerWorldPosition is not { } p || Catalog is not { } catalog) return new LuaMulti(GetWidth() / 2, GetHeight() / 2);
        var map = GetMapData(catalog);
        if (map == null) return new LuaMulti(GetWidth() / 2, GetHeight() / 2);
        var pixel = map.WorldToPixel(new Vector2(p.X, p.Y), 928, 556);
        return new LuaMulti(pixel.X * GetWidth() / 928, pixel.Y * GetHeight() / 556);
    }

    public Dictionary<object, object?>? GetPlayerSextants()
        => PlayerWorldPosition is { } p ? Provider?.GetSextant(p.X, p.Y)?.ToLuaTable() : null;

    public Dictionary<object, object?>? GetCursorSextants()
    {
        if (Provider == null || Catalog == null || GetMapData(Catalog) is not { } map) return null;
        var rect = ScreenRect;
        if (!rect.Contains(Root.CursorX, Root.CursorY) || rect.Width <= 0 || rect.Height <= 0) return null;
        var pixel = new Vector2((Root.CursorX - rect.X) * 928 / rect.Width, (Root.CursorY - rect.Y) * 556 / rect.Height);
        var world = map.PixelToWorld(pixel, 928, 556);
        return Provider.GetSextant(world.X, world.Y)?.ToLuaTable();
    }

    public Dictionary<object, object?>? GetClimateInfo(double zoneId) => Provider?.GetClimateInfo((int)zoneId);
    public bool IsPingMode() => _pingMode;
    public bool MapWidgetShown() => IsVisible();
    public void SetPingBtn(bool pressed, object? pingType = null) => _pingMode = pressed;
    public void RemovePing(object? pingType = null) => _pingMode = false;
    public void RemovePingAll() => _pingMode = false;
    public void ResetCursor(bool reset) => CursorReset = reset;
    public void ShowPortal(object? zoneId, object? x = null, object? y = null, object? z = null)
    { PortalVisible = true; PortalTarget = (zoneId, x, y, z); }
    public void ShowCommonFarm(object? groupType, object? farmType = null, object? x = null, object? y = null)
    { CommonFarmVisible = true; FarmTarget = (groupType, farmType, x, y); }
    public void ShowQuest(object? questType, object? decalIndex = null, object? visible = null)
    { QuestVisible = visible is not false; QuestTarget = (questType, decalIndex); }

    public TextureDrawable GetIconDrawable(double type, double zoneId)
    {
        var key = ((int)type, (int)zoneId);
        if (!_icons.TryGetValue(key, out var drawable))
            _icons[key] = drawable = CreateImageDrawable("", "overlay");
        return drawable;
    }
    public LuaMulti GetRouteDrawable(double level, double zoneId)
    {
        var key = ((int)level, (int)zoneId);
        var created = !_routes.TryGetValue(key, out var drawable);
        if (created) _routes[key] = drawable = CreateImageDrawable("", "background");
        return new LuaMulti(drawable, created);
    }
    public Widget GetTooltipController() => _tooltipController ??= CreateChildWidget("emptywidget", "tooltipController");
    public void HideAllIconDrawable() { foreach (var icon in _icons.Values) icon.SetVisible(false); }

    private void Refresh(string category) => _activeCategories.Add(category);
    private void Clear(string category) => _activeCategories.Remove(category);
    public void ClearAllInfo() { _activeCategories.Clear(); _questInfo.Clear(); HideAllIconDrawable(); }
    public void ReloadAllInfo() { foreach (var category in AllCategories) Refresh(category); }
    private static readonly string[] AllCategories = ["Zone", "Npc", "Doodad", "GivenQuestStatic", "Housing", "ShipTelescope", "TransferTelescope", "BossTelescope", "CarryingBackpackSlave", "FishSchool", "Corpse", "MySlave", "Ping", "CompletedQuest", "Dominion"];
    public void UpdateZoneInfo() => Refresh("Zone");
    public void UpdateNpcInfo() => Refresh("Npc");
    public void ClearNpcInfo() => Clear("Npc");
    public void UpdateDoodadInfo(bool roadMap = false) => Refresh("Doodad");
    public void ClearDoodadInfo() => Clear("Doodad");
    public void UpdateGivenQuestStaticInfo() => Refresh("GivenQuestStatic");
    public void ClearGivenQuestStaticInfo() => Clear("GivenQuestStatic");
    public void UpdateHousingInfo() => Refresh("Housing");
    public void ClearHousingInfo() => Clear("Housing");
    public void UpdateShipTelescopeInfo() => Refresh("ShipTelescope");
    public void ClearShipTelescopeInfo() => Clear("ShipTelescope");
    public void UpdateTransferTelescopeInfo() => Refresh("TransferTelescope");
    public void ClearTransferTelescopeInfo() => Clear("TransferTelescope");
    public void UpdateBossTelescopeInfo() => Refresh("BossTelescope");
    public void ClearBossTelescopeInfo() => Clear("BossTelescope");
    public void UpdateCarryingBackpackSlaveInfo() => Refresh("CarryingBackpackSlave");
    public void ClearCarryingBackpackSlaveInfo() => Clear("CarryingBackpackSlave");
    public void UpdateFishSchoolInfo() => Refresh("FishSchool");
    public void ClearFishSchoolInfo() => Clear("FishSchool");
    public void UpdateCorpseInfo() => Refresh("Corpse");
    public void ClearCorpseInfo() => Clear("Corpse");
    public void UpdateMySlaveInfo() => Refresh("MySlave");
    public void ClearMySlaveInfo() => Clear("MySlave");
    public void UpdatePingInfo() => Refresh("Ping");
    public void UpdateCompletedQuestInfo() => Refresh("CompletedQuest");
    public void ClearCompletedQuestInfo() => Clear("CompletedQuest");
    public void UpdateDominionInfo() => Refresh("Dominion");
    public void UpdateMonitorNpcInfo() => Refresh("MonitorNpc");
    public void UpdateFactionRezDistrictInfo() => Refresh("FactionRezDistrict");
    public void UpdateTelescopeArea() => Refresh("TelescopeArea");
    public void UpdateTransferTelescopeArea() => Refresh("TransferTelescopeArea");
    public void UpdateBossTelescopeArea() => Refresh("BossTelescopeArea");
    public void UpdateFishSchoolArea() => Refresh("FishSchoolArea");
    public void UpdateRouteMap(object? drawable) => Refresh("Route");
    public void UpdateEventMap() => Refresh("Event");
    public void UpdateZoneStateDrawable() => Refresh("ZoneState");
    public void UpdateAllDrawableAnchor() { }
    public void RemoveShipTelescopeInfo(object? key) => Refresh("ShipTelescope");
    public void RemoveTransferTelescopeInfo(object? key) => Refresh("TransferTelescope");
    public void RemoveBossTelescopeInfo(object? key) => Refresh("BossTelescope");
    public void RemoveCarryingBackpackSlaveInfo(object? key) => Refresh("CarryingBackpackSlave");
    public void RemoveFishSchoolInfo(object? key) => Refresh("FishSchool");
    public void AddGivenQuestInfo(object? kind, object? id) { _questInfo[$"given:{kind}:{id}"] = id; Refresh("GivenQuestStatic"); }
    public void RemoveGivenQuestInfo(object? kind, object? id) => _questInfo.Remove($"given:{kind}:{id}");
    public void AddNotifyQuestInfo(object? id) { _questInfo[$"notify:{id}"] = id; Refresh("NotifyQuest"); }
    public void RemoveNotifyQuestInfo(object? id) => _questInfo.Remove($"notify:{id}");
    public void ClearNotifyQuestInfo() { foreach (var key in _questInfo.Keys.Where(x => x.StartsWith("notify:", StringComparison.Ordinal)).ToArray()) _questInfo.Remove(key); Clear("NotifyQuest"); }
    public void StartNotifyQuestEffect(object? index, object? questType, bool start) { if (start) Refresh("NotifyQuestEffect"); else Clear("NotifyQuestEffect"); }
    public void ShowSkillMapEffect(object? x, object? y, object? z, object? radius, object? index)
    { SkillEffect = (x, y, z, radius, index); Refresh("SkillMapEffect"); }
    public void SetTempNotifyColor(object? color) => TempNotifyColor = color;
    public void SetTempNotifyCoord(bool mainQuest, object? coord)
    { if (mainQuest) MainNotifyCoord = coord; else NormalNotifyCoord = coord; }
    public void ToggleMapWithPortal(object? zoneId, object? x, object? y, object? z)
    { ShowPortal(zoneId, x, y, z); Show(true); }
    public void ToggleMapWithCommonFarm(object? groupType, object? farmType, object? x, object? y)
    { ShowCommonFarm(groupType, farmType, x, y); Show(true); }
    public void ToggleMapWithLocation(object? zoneId, object? x, object? y, object? z)
    { PortalTarget = (zoneId, x, y, z); Show(true); }
}
