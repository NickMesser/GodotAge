#nullable enable
namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Feeds the native roadmap/worldmap widgets (minimap, world map) from the live session.</summary>
public sealed class X2BridgeMapProvider(X2WorldBridge bridge, uint zoneId, float yaw,
    Func<IReadOnlySet<uint>>? partyUnits = null, Func<AAEmu.GodotViewer.Net.QuestState?>? questState = null,
    Func<QuestOfferContext?>? offerContext = null) : IX2MapProvider
{
    public X2MapPlayer? Player => bridge.Get(bridge.PlayerId) is { } p
        ? new X2MapPlayer(p.X, p.Y, bridge.Client.OwnYaw) : null;
    public int? CurrentZoneKey => (int)zoneId;
    public IReadOnlyList<X2MapMarker> Markers => X2MapIconCatalog.Markers(bridge, partyUnits?.Invoke(), questState?.Invoke(), offerContext?.Invoke());
    public X2MapSextant? GetSextant(float worldX, float worldY) => null;
    public Dictionary<object, object?>? GetClimateInfo(int zoneId) => null;
}
