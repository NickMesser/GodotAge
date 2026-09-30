#nullable enable
using Godot;
using AAEmu.GodotViewer.Maps;

namespace AAEmu.GodotViewer.Ui.X2;

public partial class X2UiLayer
{
    private MapDataCatalog? _nativeMapCatalog;
    private MapTextures? _nativeMapTextures;
    private bool _nativeMapCatalogAttempted;

    partial void DrawExtraNative(Widget widget, float alpha)
    {
        switch (widget)
        {
            case ListBoxWidget list: DrawNativeListBox(list, alpha); break;
            case LineWidget line: DrawNativeLine(line, alpha); break;
            case CircleDiagramWidget diagram: DrawNativeDiagram(diagram, alpha); break;
            case PaintColorPickerWidget picker: DrawNativePicker(picker, alpha); break;
            case UnitFrameTooltipWidget tooltip: DrawNativeSideLines(tooltip, alpha); break;
            case MapWidget map: DrawNativeMap(map, alpha); break;
        }
    }

    private void DrawNativeListBox(ListBoxWidget list, float alpha)
    {
        var rows = list.Items;
        var rect = list.ScreenRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var treeRows = list.Children.OfType<ButtonWidget>().Where(row => row.Id.StartsWith("treeItem[", StringComparison.Ordinal)).ToArray();
        var rowHeight = 22f;
        var visible = Math.Min(rows.Count - list.Top, Math.Max(0, (int)Math.Ceiling(rect.Height / rowHeight)));
        for (var offset = 0; offset < visible; offset++)
        {
            var index = list.Top + offset;
            var tree = index < treeRows.Length ? treeRows[index] : null;
            var row = tree?.ScreenRect ?? new UiRect(rect.X, rect.Y + offset * rowHeight, rect.Width, rowHeight);
            var selected = list.SelectedIndex == index + 1;
            var hovered = tree?.Hovered ?? (list.Hovered && list.HoveredIndex == index + 1);
            var info = selected ? list.SelectedItemTexture : hovered ? list.OveredItemTexture : list.DefaultItemTexture;
            if (list.ItemStateTexturePath is { } path && info is { } region && _textures.Get(path) is { } texture)
            {
                var data = Root.Settings.TextureRegion(path, region.Key);
                var coordinates = !selected && !hovered && list.DefaultItemCoord is { } defaultCoords ? defaultCoords : data?.Coords;
                if (coordinates is { Width: > 0, Height: > 0 } src)
                {
                    var inset = list.ItemStateTextureInset;
                    var destination = new UiRect(row.X + inset.Left, row.Y + inset.Top,
                        row.Width - inset.Left - inset.Right, row.Height - inset.Top - inset.Bottom);
                    if (destination.Width > 0 && destination.Height > 0)
                    {
                        var tint = region.Color != null && data.Colors.TryGetValue(region.Color, out var c) ? c : UiColor.White;
                        DrawRegion(texture, destination, src, C(tint, alpha), false, false);
                    }
                }
            }
            else if ((selected ? list.SelectedItemColor : hovered ? list.OveredItemColor : null) is { } color)
                ClippedRect(row, C(color, alpha));

            if (list.SubTextAt(index) is { Length: > 0 } sub)
            {
                var child = tree != null && list.ChildStyle;
                var subStyle = (WidgetStyle)list.NativeFields[child ? "childStyleSub" : "itemStyleSub"];
                var subX = child ? list.ChildSubTextX : list.SubTextX;
                var subY = child ? list.ChildSubTextY : list.SubTextY;
                var subWidth = subStyle.GetTextWidth(sub);
                var subRect = new UiRect(row.Right - subWidth - subX, row.Y + subY, subWidth, row.Height);
                DrawTextBlock(sub, subStyle, subRect, default, subStyle.Color, alpha, false, 0);
            }
            if (tree != null) continue; // Tree rows are child buttons and render their own text.
            var style = (WidgetStyle)list.NativeFields["itemStyle"];
            var textColor = !list.ItemEnabledAt(index) ? list.DisableItemTextColor
                : selected ? list.SelectedItemTextColor : hovered ? list.OveredItemTextColor : list.DefaultItemTextColor;
            var shown = rows[index].Text;
            if (list.TextLimit > 0 && shown.Length > list.TextLimit) shown = shown[..list.TextLimit];
            DrawTextBlock(shown, style, row, default, textColor ?? style.Color, alpha, false, 0);
            if (list.TailIconAt(index) is { Length: > 0 } tail && _textures.Get(tail) is { } tailTexture)
            {
                var source = list.TailIconCoordAt(index) is { Length: > 0 } coord
                    ? Root.Settings.TextureRegion(tail, coord)?.Coords : null;
                var src = source ?? new UiRect(0, 0, tailTexture.GetWidth(), tailTexture.GetHeight());
                var size = Math.Min(row.Height, Math.Abs(src.Height));
                DrawRegion(tailTexture, new UiRect(row.Right - size, row.Y, size, size),
                    src,
                    new Color(1, 1, 1, alpha), false, false);
            }
        }
    }

    private void DrawNativeLine(LineWidget line, float alpha)
    {
        var r = line.ScreenRect;
        var points = line.Points;
        for (var i = 1; i < points.Count; i++)
            DrawLine(new Vector2(r.X + points[i - 1].X, r.Y + points[i - 1].Y),
                new Vector2(r.X + points[i].X, r.Y + points[i].Y), C(line.LineColor, alpha),
                Math.Max(1, line.LineThickness), true);
    }

    private void DrawNativeDiagram(CircleDiagramWidget diagram, float alpha)
    {
        var r = diagram.ScreenRect;
        if (diagram.Points.Count < 3 || diagram.MaxValue <= 0) return;
        var vertices = diagram.Points.Select(p => new Vector2(
            r.X + r.Width / 2 + (p.X - r.Width / 2) * Math.Clamp(p.Value / diagram.MaxValue, 0, 1),
            r.Y + r.Height / 2 + (p.Y - r.Height / 2) * Math.Clamp(p.Value / diagram.MaxValue, 0, 1))).ToArray();
        DrawColoredPolygon(vertices, C(diagram.DiagramColor, alpha * 0.55f));
        DrawPolyline(vertices.Append(vertices[0]).ToArray(), C(diagram.DiagramColor, alpha), 1.5f, true);
    }

    private void DrawNativePicker(PaintColorPickerWidget picker, float alpha)
    {
        var spectrum = picker.GetSpectrumWidget()?.ScreenRect ?? default;
        if (spectrum.Width > 1 && spectrum.Height > 1)
        {
            const int bands = 36;
            for (var i = 0; i < bands; i++)
            {
                var hue = (float)i / bands;
                var color = Color.FromHsv(hue, 1, 1, alpha);
                DrawRect(new Rect2(spectrum.X + spectrum.Width * i / bands, spectrum.Y,
                    spectrum.Width / bands + 1, spectrum.Height), color);
            }
        }
        var luminance = picker.GetLuminanceWidget()?.ScreenRect ?? default;
        if (luminance.Width > 1 && luminance.Height > 1)
        {
            const int bands = 24;
            for (var i = 0; i < bands; i++)
            {
                var value = 1f - (float)i / bands;
                DrawRect(new Rect2(luminance.X, luminance.Y + luminance.Height * i / bands,
                    luminance.Width, luminance.Height / bands + 1),
                    Color.FromHsv(picker.Hue, picker.Saturation, value, alpha));
            }
        }
    }

    private void DrawNativeSideLines(UnitFrameTooltipWidget tooltip, float alpha)
    {
        var rect = tooltip.ScreenRect;
        var y = rect.Y + tooltip.TextInset.Top;
        for (var i = 0; i < tooltip.TooltipLines.Count; i++)
        {
            var left = tooltip.TooltipLines[i];
            var lineHeight = Math.Max(left.Size, tooltip.style.FontSize) + tooltip.EffectiveLineSpace;
            if (tooltip.SideLines.TryGetValue(i + 1, out var side) && side.Text.Length > 0)
            {
                var font = _fonts.Get(side.Font?.ToString() ?? tooltip.style.FontKey);
                var px = X2FontCache.PixelSize(side.Size > 0 ? (float)side.Size : tooltip.style.FontSize);
                var width = font.GetStringSize(side.Text, HorizontalAlignment.Left, -1, px).X;
                DrawString(font, new Vector2(rect.Right - tooltip.TextInset.Right - (float)side.Indent - width,
                    y + font.GetAscent(px)), side.Text, HorizontalAlignment.Left, -1, px,
                    C(tooltip.style.Color, alpha));
                lineHeight = Math.Max(lineHeight, font.GetHeight(px) + tooltip.EffectiveLineSpace);
            }
            y += lineHeight;
        }
    }

    private void EnsureNativeMapResources()
    {
        if (_nativeMapCatalogAttempted) return;
        _nativeMapCatalogAttempted = true;
        try { _nativeMapCatalog = MapDataCatalog.LoadFromDatabase(DefaultDatabase); }
        catch (Exception ex) { Emit($"[x2] map catalog unavailable: {ex.Message}"); }
        _nativeMapTextures = new MapTextures();
    }

    private void DrawNativeMap(MapWidget widget, float alpha)
    {
        var rect = widget.ScreenRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;
        EnsureNativeMapResources();
        widget.Catalog ??= _nativeMapCatalog;
        var map = _nativeMapCatalog is null ? null : widget.GetMapData(_nativeMapCatalog);
        var texture = map is null ? null : _nativeMapTextures?.Get(map.ImagePath);
        if (texture is null || map is null)
        {
            ClippedRect(rect, new Color(0.09f, 0.13f, 0.16f, alpha));
            return;
        }

        // the scripts size the canvas (zoom) and scroll it inside a clipping window (GetPlayerViewPos + scroll anchors)
        DrawRegion(texture, rect, new UiRect(0, 0, texture.GetWidth(), texture.GetHeight()), new Color(1, 1, 1, alpha), false, false);
        if (widget.PlayerWorldPosition is { } worldPlayer)
        {
            var pixel = map.WorldToPixel(new Vector2(worldPlayer.X, worldPlayer.Y), texture.GetWidth(), texture.GetHeight());
            var p = new Vector2(rect.X + pixel.X / texture.GetWidth() * rect.Width,
                rect.Y + pixel.Y / texture.GetHeight() * rect.Height);
            DrawNativeMarkers(widget, map, rect, texture,
                widget.PlayerCenteredProjection ? worldPlayer : null, p, alpha);
            DrawNativePlayer(widget, p, worldPlayer.Yaw, alpha);
        }
        else DrawNativeMarkers(widget, map, rect, texture, null, null, alpha);
    }

    private void DrawNativeMarkers(MapWidget widget, MapData map, UiRect rect, Texture2D texture,
        X2MapPlayer? centeredPlayer, Vector2? centeredPlayerScreen, float alpha)
    {
        foreach (var marker in widget.Markers)
        {
            if (!marker.Visible) continue;
            if (marker.IconType != 0 && !Online.X2MapIconCatalog.IsChecked(marker.IconType)) continue;
            if (marker.IconType != 0 && marker.Category is "Npc" or "Doodad" or "Housing" or "Structure")
            {
                var filter = marker.Category switch { "Npc" => 0, "Doodad" => 1, "Housing" => 2, "Structure" => 3, _ => -1 };
                if (filter >= 0 && !Online.X2MapIconCatalog.FolderChecked(filter)) continue;
            }
            if (widget.ActiveCategories.Count > 0 && !widget.ActiveCategories.Contains(marker.Category)) continue;
            Vector2 p;
            if (centeredPlayer is { } player)
            {
                var origin = centeredPlayerScreen ?? new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                p = MapWidget.ProjectPlayerCenteredMarker(rect, player, marker, origin,
                    widget.ExpandRatio, widget.ZoomRatio);
            }
            else
            {
                var pixel = map.WorldToPixel(new Vector2(marker.X, marker.Y), texture.GetWidth(), texture.GetHeight());
                p = new Vector2(rect.X + pixel.X / texture.GetWidth() * rect.Width,
                    rect.Y + pixel.Y / texture.GetHeight() * rect.Height);
            }
            if (p.X < rect.X || p.X > rect.Right || p.Y < rect.Y || p.Y > rect.Bottom) continue;
            var icon = marker.TexturePath is { } path ? _nativeMapTextures?.Get(path) : null;
            var region = marker.TexturePath is { } source && marker.TextureRegion is { } key
                ? Root.Settings.TextureRegion(source, key)?.Coords : null;
            if (icon is not null && region is { } src)
            {
                var size = new Vector2(Math.Abs(src.Width), Math.Abs(src.Height));
                DrawRegion(icon, new UiRect(p.X - size.X / 2, p.Y - size.Y / 2, size.X, size.Y), src,
                    new Color(1, 1, 1, alpha), false, false);
            }
            else DrawCircle(p, 4, new Color(0.95f, 0.82f, 0.3f, alpha));
        }
    }

    private void DrawNativePlayer(MapWidget widget, Vector2 center, float yaw, float alpha)
    {
        var drawable = widget.PlayerDrawable;
        var texture = drawable?.TexturePath is { } path ? _textures.Get(path) : null;
        if (texture is null)
        {
            DrawCircle(center, 5, new Color(1, 0.92f, 0.55f, alpha));
            return;
        }
        var src = drawable!.SourceRect ?? new UiRect(0, 0, texture.GetWidth(), texture.GetHeight());
        var size = new Vector2(Math.Min(32, Math.Abs(src.Width)), Math.Min(32, Math.Abs(src.Height)));
        var scale = Math.Max(0.1f, UiScale);
        DrawSetTransform(center * scale, -yaw, new Vector2(scale, scale));
        DrawTextureRectRegion(texture, new Rect2(-size / 2, size), R(src), new Color(1, 1, 1, alpha));
        DrawSetTransform(Vector2.Zero, 0, new Vector2(scale, scale));
    }
}
