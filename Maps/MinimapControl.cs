using Godot;
#nullable enable
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer.Maps;

public partial class MinimapControl : Control
{
    [Export] public bool Circular { get; set; }
    [Export] public bool RotateWithPlayer { get; set; }
    [Export] public float[] ZoomLevels { get; set; } = [0.6f, 0.8f, 1f, 1.2f];
    [Export] public int ZoomIndex { get; set; } = 2;
    [Export] public string FrameTexturePath { get; set; } = "game/ui/common/hud.dds";
    [Export] public string PlayerTexturePath { get; set; } = "game/ui/map/icon/player_cursor.dds";

    public MapData? Map { get; set; }
    public MapTextures? Textures { get; set; }
    public IMapMarkerSource? MarkerSource { get; set; }
    public NVector3 PlayerWorldPosition { get; set; }
    /// <summary>Yaw in radians around Cry +Z; zero faces north (+Y).</summary>
    public float PlayerYaw { get; set; }
    public float ViewDiameter { get; set; } = 1200f;

    public override void _Ready() => QueueRedraw();

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } mouse && mouse.ButtonIndex == MouseButton.WheelUp)
        { ZoomIndex = Mathf.Min(ZoomIndex + 1, ZoomLevels.Length - 1); QueueRedraw(); AcceptEvent(); }
        else if (@event is InputEventMouseButton { Pressed: true } mouseDown && mouseDown.ButtonIndex == MouseButton.WheelDown)
        { ZoomIndex = Mathf.Max(0, ZoomIndex - 1); QueueRedraw(); AcceptEvent(); }
    }

    public override void _Draw()
    {
        var side = Mathf.Min(Size.X, Size.Y);
        if (side <= 0) return;
        var rect = new Rect2((Size - new Vector2(side, side)) * 0.5f, new Vector2(side, side));
        var mapTexture = Map is null ? null : Textures?.Get(Map.ImagePath);
        if (Map is not null && mapTexture is not null)
        {
            if (Circular) DrawCircularMap(rect, mapTexture);
            else DrawRectMap(rect, mapTexture);
            DrawMarkers(rect, mapTexture);
        }
        else
        {
            if (Circular) DrawCircle(rect.GetCenter(), side * 0.5f, new Color("171d24"));
            else DrawRect(rect, new Color("171d24"));
        }
        var frame = Textures?.Get(FrameTexturePath);
        if (frame is not null) DrawFrame(frame, rect);
        else if (Circular) DrawArc(rect.GetCenter(), side * 0.5f - 1, 0, Mathf.Tau, 96, new Color("b5a889"), 2f, true);
        else DrawRect(rect, new Color("b5a889"), false, 2f);
        DrawPlayer(rect);
    }

    private void DrawRectMap(Rect2 rect, Texture2D texture)
    {
        var center = rect.GetCenter();
        var worldPerPixel = ViewDiameter / Mathf.Max(0.1f, ZoomLevels[Mathf.Clamp(ZoomIndex, 0, ZoomLevels.Length - 1)]) / rect.Size.X;
        var uvs = new Vector2[4]; var pts = new[] { rect.Position, new Vector2(rect.End.X, rect.Position.Y), rect.End, new Vector2(rect.Position.X, rect.End.Y) };
        for (var i = 0; i < pts.Length; i++) uvs[i] = WorldUv(pts[i] - center, worldPerPixel, texture);
        var colors = new[] { Colors.White, Colors.White, Colors.White, Colors.White };
        DrawPolygon(pts.AsSpan(), colors.AsSpan(), uvs.AsSpan(), texture);
    }

    private void DrawCircularMap(Rect2 rect, Texture2D texture)
    {
        const int segments = 64;
        var center = rect.GetCenter(); var radius = rect.Size.X * 0.5f;
        var points = new Vector2[segments]; var uvs = new Vector2[segments];
        var colors = Enumerable.Repeat(Colors.White, segments).ToArray();
        for (var i = 0; i < segments; i++)
        {
            var a = Mathf.Tau * i / segments;
            var offset = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            points[i] = center + offset;
            uvs[i] = WorldUv(offset, ViewDiameter / ZoomAt / rect.Size.X, texture);
        }
        DrawPolygon(points.AsSpan(), colors.AsSpan(), uvs.AsSpan(), texture);
    }

    private float ZoomAt => ZoomLevels.Length == 0 ? 1f : ZoomLevels[Mathf.Clamp(ZoomIndex, 0, ZoomLevels.Length - 1)];

    private void DrawFrame(Texture2D texture, Rect2 dest)
    {
        const float left = 3, top = 3, right = 4, bottom = 4;
        var xs = new[] { 0f, left, 8f - right, 8f };
        var ys = new[] { 0f, top, 8f - bottom, 8f };
        var dx = new[] { dest.Position.X, dest.Position.X + left, dest.End.X - right, dest.End.X };
        var dy = new[] { dest.Position.Y, dest.Position.Y + top, dest.End.Y - bottom, dest.End.Y };
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                DrawTextureRectRegion(texture,
                    new Rect2(dx[col], dy[row], dx[col + 1] - dx[col], dy[row + 1] - dy[row]),
                    new Rect2(85 + xs[col], 18 + ys[row], xs[col + 1] - xs[col], ys[row + 1] - ys[row]));
    }

    private Vector2 WorldUv(Vector2 screenOffset, float worldPerPixel, Texture2D texture)
    {
        var yaw = RotateWithPlayer ? PlayerYaw : 0f;
        var dx = screenOffset.X * worldPerPixel;
        var dy = screenOffset.Y * worldPerPixel;
        var world = new Vector2(PlayerWorldPosition.X + dx * Mathf.Cos(yaw) + dy * Mathf.Sin(yaw),
            PlayerWorldPosition.Y + dx * Mathf.Sin(yaw) - dy * Mathf.Cos(yaw));
        var px = Map!.WorldToPixel(new Godot.Vector2(world.X, world.Y), texture.GetWidth(), texture.GetHeight());
        return new Vector2(px.X / texture.GetWidth(), px.Y / texture.GetHeight());
    }

    private void DrawMarkers(Rect2 rect, Texture2D texture)
    {
        if (Map is null || MarkerSource is null) return;
        var center = rect.GetCenter(); var scale = rect.Size.X * ZoomAt / ViewDiameter;
        var yaw = RotateWithPlayer ? PlayerYaw : 0f;
        foreach (var marker in MarkerSource.GetMarkers(Map))
        {
            var dx = marker.WorldPosition.X - PlayerWorldPosition.X;
            var dy = marker.WorldPosition.Y - PlayerWorldPosition.Y;
            var screen = center + new Vector2(dx * Mathf.Cos(yaw) + dy * Mathf.Sin(yaw),
                dx * Mathf.Sin(yaw) - dy * Mathf.Cos(yaw)) * scale;
            if (Circular && screen.DistanceSquaredTo(center) > rect.Size.X * rect.Size.X * 0.25f) continue;
            DrawMarker(this, Textures, screen, marker, 5f);
        }
    }

    private void DrawPlayer(Rect2 rect)
    {
        var center = rect.GetCenter();
        var arrow = Textures?.Get(PlayerTexturePath);
        if (arrow is null) { DrawCircle(center, 5f, new Color("fff2ae")); DrawArc(center, 6f, 0, Mathf.Tau, 24, Colors.Black, 1.5f); return; }
        var size = new Vector2(24, 24); var r = new Rect2(center - size * 0.5f, size);
        DrawSetTransform(center, RotateWithPlayer ? 0f : -PlayerYaw);
        DrawTextureRect(arrow, new Rect2(-size * 0.5f, size), false, Colors.White);
        DrawSetTransform(Vector2.Zero, 0f);
    }

    internal static void DrawMarker(CanvasItem canvas, MapTextures? textures, Vector2 p, MapMarker marker, float radius)
    {
        if (textures is not null && marker.TexturePath is not null && marker.TextureRegion is Rect2 region)
        {
            var atlas = textures.Get(marker.TexturePath);
            if (atlas is not null)
            {
                var size = region.Size;
                canvas.DrawTextureRectRegion(atlas, new Rect2(p - size * 0.5f, size), region);
                return;
            }
        }
        var color = marker.Kind switch
        {
            MapMarkerKind.Quest => new Color("f5d044"),
            MapMarkerKind.Npc => new Color("74d8a0"),
            MapMarkerKind.Portal => new Color("bd8cff"),
            MapMarkerKind.Player => new Color("7fc5ff"),
            _ => new Color("ff845e")
        };
        canvas.DrawCircle(p, radius, color);
        canvas.DrawArc(p, radius, 0, Mathf.Tau, 16, new Color("17202a"), 1.5f, true);
    }
}
