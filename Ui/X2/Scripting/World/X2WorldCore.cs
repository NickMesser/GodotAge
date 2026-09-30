#nullable enable
using AAEmu.GodotViewer.Lua;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.World;

/// <summary>
/// Engine-side pieces of the in-game UI that are not an API family of their own: the content registry behind
/// ADDON/X2:RegisterContentWidget/ShowContent/ToggleContent (how the menu bar and hotkeys open windows) and the clock.
/// </summary>
public sealed class X2WorldCore
{
    private const double OptionFrameContentId = 12;
    private readonly Dictionary<double, (Widget Widget, LuaFunctionReference? ShowFunc)> _widgets = [];
    private readonly Dictionary<double, LuaFunctionReference> _triggers = [];
    private X2LuaHost? _host;

    /// <summary>Game time of day (hours, 0..24) for X2Time:GetGameTime; null uses a 4-hour real-time day cycle.</summary>
    public Func<double>? GameHour { get; set; }

    public void Install(X2LuaHost host)
    {
        _host = host;
        _widgets.Clear();
        _triggers.Clear();
        foreach (var ns in new[] { "ADDON", "X2" })
        {
            host.Define(ns, "RegisterContentWidget", a =>
            {
                if (a[1] is Widget w) _widgets[a.Num(0)] = (w, a.Func(2));
                return null;
            });
            host.Define(ns, "RegisterContentTriggerFunc", a =>
            {
                if (a.Func(1) is { } f)
                {
                    var contentId = a.Num(0);
                    _triggers[contentId] = f;
                    // The labor bar is created hidden by the stock HUD script. The native
                    // content registry enables this persistent HUD content at world entry.
                    if (contentId == 123) host.Call(f, [true], "labor bar initial visibility");
                }
                return null;
            });
            host.Define(ns, "GetContent", a => _widgets.TryGetValue(a.Num(0), out var e) ? e.Widget : null);
            host.Define(ns, "ShowContent", a => ShowContent(a.Num(0), a[1] is bool b ? b : a[1] == null ? null : true, a.Values.Skip(2).ToArray()));
            host.Define(ns, "ToggleContent", a => ShowContent(a.Num(0), null, a.Values.Skip(1).ToArray()));
            host.Define(ns, "IsEnteredWorld", _ => true);
        }

        host.Define("X2Time", "GetGameTime", _ =>
        {
            var hour = GameHour?.Invoke() ?? DateTime.Now.TimeOfDay.TotalHours * 6 % 24;
            var h = (int)Math.Floor(hour);
            var minute = (hour - h) * 60;
            var isAm = h < 12;
            var h12 = h % 12;
            if (h12 == 0) h12 = 12;
            return new LuaMulti(isAm, (double)h12, minute);
        });
        host.Define("X2Time", "GetUiMsec", _ => (double)Environment.TickCount64);
        host.Define("X2Time", "GetLocalTime", _ => DateTime.Now.ToString("HH:mm"));
        host.Define("X2Time", "GetLocalDate", _ => DateTable(DateTime.Now));
        host.Define("X2Time", "GetServerTime", _ => DateTable(DateTime.Now));
    }

    private static LuaTable DateTable(DateTime t) => new()
    {
        ["year"] = (double)t.Year, ["month"] = (double)t.Month, ["day"] = (double)t.Day,
        ["hour"] = (double)t.Hour, ["minute"] = (double)t.Minute, ["second"] = (double)t.Second,
    };

    /// <summary>Shows (true), hides (false) or toggles (null) a registered content window.</summary>
    public object? ShowContent(double uic, bool? show, object?[] extra)
    {
        var host = _host;
        if (host == null) return null;
        if (_triggers.TryGetValue(uic, out var trigger))
        {
            host.Call(trigger, new object?[] { show }.Concat(extra).ToArray(), $"content {uic}");
            return null;
        }
        if (!_widgets.TryGetValue(uic, out var e) || e.Widget.Destroyed)
        {
            // The stock option package creates and registers UIC_OPTION_FRAME lazily in
            // ToggleOptionFrame. Route the first content-id open through that native entry point;
            // subsequent calls use the registered widget like every other content window.
            if (uic == OptionFrameContentId)
                host.CallGlobal("ToggleOptionFrame", show);
            return null;
        }
        var visible = show ?? !e.Widget.IsVisible();
        if (e.ShowFunc != null) host.Call(e.ShowFunc, new object?[] { visible }.Concat(extra).ToArray(), $"content {uic}");
        else e.Widget.Show(visible);
        return null;
    }
}
