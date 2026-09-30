using Godot;
using NVector3 = System.Numerics.Vector3;
namespace AAEmu.GodotViewer.Maps;
public partial class MapTest : Control
{
    private int _frames;
    public override void _Ready()
    {
        PakFiles.Open(ClientPaths.Pak);
        var catalog = MapDataCatalog.LoadFromDatabase(ClientPaths.Database);
        var tex = new MapTextures();
        var pos = new NVector3(15523.9f, 15339.6f, 130f);
        var world = new WorldMapWindow { Catalog = catalog, Textures = tex, PlayerWorldPosition = pos, Position = new Vector2(20, 20), Size = new Vector2(928, 556) };
        AddChild(world);
        world.ShowZone(179);
        catalog.TryByZoneKey(179, out var map);
        var mini = new MinimapControl { Map = map, Textures = tex, PlayerWorldPosition = pos, PlayerYaw = 0.6f, Position = new Vector2(1000, 20), Size = new Vector2(300, 300) };
        AddChild(mini);
        var world2 = new WorldMapWindow { Catalog = catalog, Textures = tex, PlayerWorldPosition = pos, Position = new Vector2(1000, 340), Size = new Vector2(560, 336) };
        AddChild(world2);
        world2.ShowContinent();
    }
    public override void _Process(double d)
    {
        if (++_frames == 20) { GetViewport().GetTexture().GetImage().SavePng("res://maptest.png"); GetTree().Quit(); }
    }
}
