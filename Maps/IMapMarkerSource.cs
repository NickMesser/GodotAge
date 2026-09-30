#nullable enable
using NVector3 = System.Numerics.Vector3;
using Godot;

namespace AAEmu.GodotViewer.Maps;

public enum MapMarkerKind { Player, Quest, Npc, Portal, Custom }

public sealed record MapMarker(NVector3 WorldPosition, string Label, MapMarkerKind Kind,
    string? TexturePath = null, Rect2? TextureRegion = null);

/// <summary>Supplies the current world-space map markers for a map view.</summary>
public interface IMapMarkerSource
{
    IEnumerable<MapMarker> GetMarkers(MapData map);
}
