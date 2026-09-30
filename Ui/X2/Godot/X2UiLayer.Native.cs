using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// Drawing of the native widget types that carry their own visuals (gauges, slots, message frames).
public partial class X2UiLayer
{
    private void DrawNative(Widget widget, float alpha)
    {
        switch (widget)
        {
            case StatusBarWidget bar:
                DrawStatusBar(bar, alpha);
                break;
            case SlotWidget slot:
                DrawSlot(slot, alpha);
                break;
            case MessageWidget message:
                DrawMessages(message, alpha);
                break;
            case ModelViewWidget modelView:
                DrawNativeModelView(modelView, alpha);
                break;
            default:
                DrawExtraNative(widget, alpha);
                break;
        }
    }

    /// <summary>Drawing for additional native widget types (implemented in another part of this class).</summary>
    partial void DrawExtraNative(Widget widget, float alpha);

    private void DrawStatusBar(StatusBarWidget bar, float alpha)
    {
        var rect = bar.ScreenRect;
        var f = bar.Fraction;
        if (f <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
        var tint = C(bar.BarColor, alpha);
        var tex = bar.BarTexture == null ? null : _textures.Get(bar.BarTexture);
        var src = bar.BarCoords ?? Root.Settings.TextureRegion(bar.BarTexture, bar.BarKey)?.Coords;
        UiRect dst;
        UiRect? s = null;
        if (bar.Vertical)
        {
            var h = rect.Height * f;
            dst = new UiRect(rect.X, rect.Bottom - h, rect.Width, h);
            if (src is { } r) s = new UiRect(r.X, r.Y + r.Height * (1 - f), r.Width, r.Height * f);
        }
        else
        {
            var w = rect.Width * f;
            dst = bar.Reversed ? new UiRect(rect.Right - w, rect.Y, w, rect.Height) : new UiRect(rect.X, rect.Y, w, rect.Height);
            if (src is { } r) s = new UiRect(r.X, r.Y, r.Width * f, r.Height);
        }
        if (tex == null || s == null)
        {
            ClippedRect(dst, tint);
            return;
        }
        var abs = new UiRect(Math.Min(s.Value.X, s.Value.X + s.Value.Width), Math.Min(s.Value.Y, s.Value.Y + s.Value.Height), Math.Abs(s.Value.Width), Math.Abs(s.Value.Height));
        DrawRegion(tex, dst, abs, tint, false, false);
    }

    private void DrawSlot(SlotWidget slot, float alpha)
    {
        var rect = slot.ScreenRect;
        if (slot.IconPath != null && _textures.Get(slot.IconPath) is { } icon)
        {
            var tint = slot.Grayed || !slot.Enabled ? new Color(0.45f, 0.45f, 0.45f, alpha) : new Color(1, 1, 1, alpha);
            DrawRegion(icon, rect, new UiRect(0, 0, icon.GetWidth(), icon.GetHeight()), tint, false, false);
        }
        var cd = slot.CooldownFraction;
        if (cd > 0)
        {
            // cooldown sweep: a dark pie over the remaining part, clockwise from 12 o'clock
            var center = new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            var radius = Math.Max(rect.Width, rect.Height);
            var points = new List<Vector2> { center };
            var steps = Math.Max(2, (int)(32 * cd));
            for (var i = 0; i <= steps; i++)
            {
                var a = -MathF.PI / 2 + 2 * MathF.PI * (1 - cd) + 2 * MathF.PI * cd * i / steps;
                var p = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
                points.Add(new Vector2(Math.Clamp(p.X, rect.X, rect.Right), Math.Clamp(p.Y, rect.Y, rect.Bottom)));
            }
            if (points.Count >= 3) DrawColoredPolygon(points.ToArray(), new Color(0, 0, 0, 0.6f * alpha));
            var seconds = slot.CooldownRemaining / 1000;
            var label = seconds >= 60 ? $"{(int)(seconds / 60)}m" : seconds >= 1 ? $"{(int)seconds}" : $"{seconds:0.0}";
            DrawTextBlock(label, slot.CooltimeStyle, rect, default, slot.CooltimeStyle.Color, alpha, false, 0);
        }
        if (slot.Count > 1)
            DrawTextBlock(slot.Count.ToString(), slot.style, rect, new UiInsets(2, 2, 3, 2), slot.style.Color, alpha, false, 0);
    }

    private void DrawMessages(MessageWidget message, float alpha)
    {
        var rect = message.ScreenRect;
        if (rect.Width <= 1 || rect.Height <= 1 || message.Lines.Count == 0) return;
        var style = message.style;
        var font = _fonts.Get(style.FontKey);
        var px = X2FontCache.PixelSize(style.FontSize);
        var lineHeight = font.GetHeight(px);
        var ascent = font.GetAscent(px);
        var inner = new UiRect(rect.X + message.TextInset.Left, rect.Y + message.TextInset.Top,
            rect.Width - message.TextInset.Left - message.TextInset.Right, rect.Height - message.TextInset.Top - message.TextInset.Bottom);
        var y = inner.Bottom;
        var space = Math.Max(0, message.EffectiveLineSpace);
        for (var i = message.Lines.Count - 1 - message.Scroll; i >= 0 && y > inner.Y; i--)
        {
            var (text, color, time) = message.Lines[i];
            var fade = 1f;
            if (message.VisibleTime > 0)
            {
                var age = message.Clock - time;
                var fadeTime = Math.Max(0.1, message.FadeDuration);
                if (age > message.VisibleTime + fadeTime) continue;
                if (age > message.VisibleTime) fade = (float)(1 - (age - message.VisibleTime) / fadeTime);
            }
            var lines = TextLayout.Lines(Root, style.FontKey, style.FontSize, text, Math.Max(1, inner.Width));
            for (var k = lines.Count - 1; k >= 0 && y > inner.Y; k--)
            {
                y -= lineHeight;
                var x = inner.X;
                if (!LineVisible(y, lineHeight)) { y -= space; continue; }
                foreach (var run in lines[k].Runs)
                {
                    var c = run.Color is { } rc ? C(rc, alpha * fade) : C(color, alpha * fade);
                    x += DrawInlineRun(run, font, px, x, y, lineHeight, MathF.Round(y + ascent), c, style.Shadow, true);
                }
                y -= space;
            }
        }
    }
}
