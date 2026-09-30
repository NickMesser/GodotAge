#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

public enum UiKey { Enter, Tab, Escape, Backspace, Delete, Left, Right, Up, Down, Home, End, SelectAll }

/// <summary>
/// The UIParent model: widget factory, anchor solver, event hub, update loop, focus and input routing.
/// Public instance methods are the script-facing UIParent API; engine-side members are internal.
/// </summary>
public sealed class UiRoot
{
    private static readonly string[] LayerOrder = ["background", "game", "normal", "hud", "questdirecting", "dialog", "system", "tooltip", "cursor"];

    private readonly List<Widget> _topLevel = [];
    private readonly Dictionary<string, Widget> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Widget>> _eventWidgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<object>> _eventHandlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);
    private int _sequence;
    private int _layoutVersion;
    private Widget? _hover;
    private ListBoxWidget? _hoveredList;
    private Widget? _pressed;
    private Widget? _dragSource;
    private ChatWindowWidget? _resizingChat;
    private WindowWidget? _resizingWindow;
    private SliderWidget? _dragSlider;
    private float _pressX, _pressY;
    private string _pressedButton = "LeftButton";
    private double _lastClickTime = -1;
    private Widget? _lastClickWidget;
    private double _clock;

    public UiRoot(UiSettings settings, ITextMeasurer measurer, float screenWidth = 1920, float screenHeight = 1080)
    {
        Settings = settings;
        Measurer = measurer;
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
    }

    internal UiSettings Settings { get; }
    internal ITextMeasurer Measurer { get; }
    internal float ScreenWidth { get; private set; }
    internal float ScreenHeight { get; private set; }
    internal float UiScale { get; set; } = 1;
    internal Widget? Focused { get; private set; }
    internal Widget? Hover => _hover;
    internal IReadOnlyList<Widget> TopLevel => _topLevel;
    internal UiRect RootRect => new(0, 0, ScreenWidth, ScreenHeight);

    /// <summary>Calls a script handler: (widget, handler object, handler name, args) -> first result.</summary>
    internal Func<Widget, object, string, object?[], object?>? HandlerInvoker { get; set; }
    /// <summary>Calls a plain script function (UIParent:SetEventHandler): (function, args) -> first result.</summary>
    internal Func<object, object?[], object?>? FunctionInvoker { get; set; }
    internal Action<string>? LogSink { get; set; }
    /// <summary>English for text the client data only has in Chinese/Korean (applied when text is measured or drawn).</summary>
    internal UiTranslator Translator { get; set; } = UiTranslator.None;
    /// <summary>Sets a script-visible field on a widget (e.g. listCtrl.items); tables are dictionaries.</summary>
    internal Action<object, string, object?>? ScriptFieldSetter { get; set; }
    /// <summary>Reads a field assigned by the client's Lua script to a widget userdata.</summary>
    internal Func<object, string, object?>? ScriptFieldReader { get; set; }
    internal event Action<Widget>? WidgetCreated;
    /// <summary>Raised after a newly-created widget has constructed all of its engine-owned children.</summary>
    internal event Action<Widget>? WidgetReady;
    /// <summary>Calls one of the native-widget global Lua callbacks.</summary>
    internal Func<string, object?[], object?>? NativeCallback { get; set; }
    /// <summary>Current persisted chat tabs, grouped by native chat-window id.</summary>
    internal Func<int, IReadOnlyList<ChatTabDefinition>>? ChatTabs { get; set; }
    /// <summary>Outgoing channels offered by the native chat method selector.</summary>
    internal Func<IReadOnlyList<ChatChannelDefinition>>? ChatChannels { get; set; }
    /// <summary>Forwards an accepted native chat edit line to the owning host.</summary>
    internal Action<int, string>? ChatSubmit { get; set; }
    /// <summary>Effect drawables whose animation is running.</summary>
    internal HashSet<TextureDrawable> ActiveEffects { get; } = [];

    /// <summary>
    /// Additional native widget types by type name (lower case), checked before the built-in ones. Register at start-up;
    /// the factory receives (root, type name, id, parent or null) and must call the Widget constructor with them.
    /// </summary>
    public static readonly Dictionary<string, Func<UiRoot, string, string, Widget?, Widget>> WidgetTypes = new(StringComparer.Ordinal);
    internal event Action<Drawable>? DrawableCreated;
    internal void OnDrawableCreated(Drawable d) => DrawableCreated?.Invoke(d);
    /// <summary>The index argument of the CreateChildWidget call in progress.</summary>
    internal int PendingChildIndex { get; set; }
    internal bool PendingPublish { get; set; } = true;
    /// <summary>A slot was bound to (slot type, index): the game layer fills icon/count/cooldown.</summary>
    internal Action<SlotWidget>? SlotEstablished { get; set; }
    /// <summary>A slot was given an item/skill directly (EstablishItem/EstablishSkill arguments).</summary>
    internal Action<SlotWidget, string, object?[]>? SlotContent { get; set; }
    /// <summary>A clickable slot completed its normal script OnClick handling.</summary>
    internal Action<SlotWidget>? SlotActivated { get; set; }
    /// <summary>A slot was dragged and released over another slot (or over nothing): action-bar placement.</summary>
    internal Action<SlotWidget, SlotWidget?>? SlotDropped { get; set; }
    /// <summary>Native inventory slot input when the loaded script supplies no handler.</summary>
    internal Func<SlotWidget, string, bool>? SlotInteraction { get; set; }
    internal event Action<Widget>? WidgetDestroyed;

    internal int NextSequence() { _paintOrder = null; return ++_sequence; }
    internal void InvalidateLayout() => _layoutVersion++;

    internal void SetScreenSize(float width, float height)
    {
        if (Math.Abs(width - ScreenWidth) < 0.01f && Math.Abs(height - ScreenHeight) < 0.01f) return;
        ScreenWidth = Math.Max(1, width);
        ScreenHeight = Math.Max(1, height);
        InvalidateLayout();
        foreach (var w in AllWidgets().ToArray())
            if (w.HasHandler("OnScale")) w.InvokeHandler("OnScale");
    }

    internal void Trace(string message) => LogSink?.Invoke(message);

    /// <summary>X2_TRACE_INPUT=1 logs presses, drags and drops with the widget under the pointer.</summary>
    private static readonly bool TraceInput = Environment.GetEnvironmentVariable("X2_TRACE_INPUT") == "1";
    internal bool ShiftDown { get; set; }

    internal static string Describe(Widget? w)
    {
        if (w == null) return "(nothing)";
        var parts = new List<string>();
        for (var p = w; p != null && parts.Count < 5; p = p.Parent) parts.Add($"{p.TypeName}:{p.Id}{(p.ChildIndex > 0 ? $"[{p.ChildIndex}]" : "")}");
        parts.Reverse();
        return string.Join("/", parts) + (w is SlotWidget s ? $" slot={s.SlotType}:{s.SlotIndex}" : "");
    }
    internal void SetScriptField(object target, string key, object? value) => ScriptFieldSetter?.Invoke(target, key, value);

    internal void TraceOnce(string key, string message)
    {
        if (_loggedOnce.Add(key)) Trace(message);
    }

    internal float MeasureText(string fontKey, float size, string text)
        => text.Length == 0 ? 0 : Measurer.Width(fontKey, size, TextLayout.Strip(Translator.Translate(text)));

    /// <summary>
    /// Every widget in pre-order. The list is cached until the hierarchy changes and is never modified in place, so
    /// callers may create or destroy widgets while iterating it.
    /// </summary>
    internal IReadOnlyList<Widget> AllWidgets()
    {
        if (_allWidgets != null) return _allWidgets;
        var all = new List<Widget>(_lastWidgetCount + 64);
        foreach (var w in _topLevel) w.CollectSelfAndDescendants(all);
        _lastWidgetCount = all.Count;
        return _allWidgets = all;
    }

    internal void HierarchyChanged() { _allWidgets = null; _tickWidgets = null; _paintOrder = null; }
    internal void TickSetChanged() => _tickWidgets = null;
    private Widget[]? _tickWidgets;

    private Widget[] TickWidgets()
    {
        if (_tickWidgets != null && _allWidgets != null) return _tickWidgets;
        return _tickWidgets = AllWidgets()
            .Where(w => w is SlotWidget or MessageWidget or SliderWidget { ScriptSlider: true } || w.FadeTime > 0 || w is WindowWidget { AnimatingScale: true } || w.HasHandler("OnUpdate"))
            .ToArray();
    }

    private List<Widget>? _allWidgets;
    private int _lastWidgetCount = 1024;

    // ------------------------------------------------------------------ script API (UIParent)

    public string GetId() => "UIParent";
    public string GetName() => "UIParent";
    public float GetScreenWidth() => ScreenWidth;
    public float GetScreenHeight() => ScreenHeight;
    public float GetUIScale() => UiScale;
    public void SetUIScale(float scale) { }
    // min, max and the step in percent (option screen_option.lua iterates the percent range by the third value)
    public LuaMulti GetUIScaleRange() => new(0.7, 1.2, 5d);
    public LuaMulti GetExtent() => new(ScreenWidth, ScreenHeight);
    public float GetWidth() => ScreenWidth;
    public float GetHeight() => ScreenHeight;
    public bool IsVisible() => true;
    public LuaMulti GetCursorPosition() => new(CursorX, CursorY);
    internal float CursorX, CursorY;
    public float GetFrameRate() => 60;
    public float GetFrameTime() => 1 / 60f;
    // UI:GetCurrentTimeStamp is a day stamp: eventcenter compares it with saved stamps to pop windows up once a day
    // (CheckPopupEventCenterOnceADay) and to flag new events (ShowEventInfoNewIcon).
    public double GetCurrentTimeStamp() => double.Parse(DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
        System.Globalization.CultureInfo.InvariantCulture);
    public double GetAccountUITimeStamp(object? key = null) => UiStamps.Get("account:" + key);
    public void SetAccountUITimeStamp(object? key = null) => UiStamps.Set("account:" + key, GetCurrentTimeStamp());
    public double GetCharacterTodayPlayedTimeStamp() => UiStamps.Get("character_played");
    public void SetCharacterTodayPlayedTimeStamp() => UiStamps.Set("character_played", GetCurrentTimeStamp());
    public double GetUIStamp(object? key = null) => 0;
    public void SetUIStamp(object? key = null, object? value = null) { }
    public bool GetPermission(object? permission = null) => true;
    public void SetUseInsertComma(bool use) { }

    // saved window positions (UIParent:SetUIBound/GetUIBound); kept for the session
    private readonly Dictionary<string, object?> _bounds = new(StringComparer.Ordinal);
    public object? GetUIBound(object? key) => key != null && _bounds.TryGetValue(key.ToString()!, out var v) ? v : null;
    public void SetUIBound(object? key, object? value) { if (key != null) _bounds[key.ToString()!] = value; }
    public void ClearUIBound(object? key = null) { if (key == null) _bounds.Clear(); else _bounds.Remove(key.ToString()!); }

    public void Log(params object?[] args) => Trace("[ui] " + string.Join(" ", args));
    public void DevLog(params object?[] args) => Trace("[dev] " + string.Join(" ", args));
    public void Warning(params object?[] args) => Trace("[warning] " + string.Join(" ", args));
    public void Error(params object?[] args) => Trace("[error] " + string.Join(" ", args));

    public Widget CreateWidget(object type, string id, object? parent = null)
    {
        var parentWidget = parent switch
        {
            Widget w => w,
            string s when !s.Equals("UIParent", StringComparison.OrdinalIgnoreCase) => GetWidget(s),
            _ => null,
        };
        var childIndex = PendingChildIndex;
        PendingChildIndex = 0;
        var publish = PendingPublish;
        PendingPublish = true;
        var canonical = TypeNameOf(type);
        Widget widget = WidgetTypes.TryGetValue(canonical, out var custom) ? custom(this, canonical, id, parentWidget) : canonical switch
        {
            "window" => new WindowWidget(this, canonical, id, parentWidget),
            "emptywidget" => new EmptyWidget(this, canonical, id, parentWidget),
            "label" => new LabelWidget(this, canonical, id, parentWidget),
            "textbox" => new TextBoxWidget(this, canonical, id, parentWidget),
            "button" => new ButtonWidget(this, canonical, id, parentWidget),
            "checkbutton" => new CheckButtonWidget(this, canonical, id, parentWidget),
            "editbox" or "x2editbox" => new EditBoxWidget(this, canonical, id, parentWidget, false),
            "editboxmultiline" => new EditBoxWidget(this, canonical, id, parentWidget, true),
            "listctrl" => new ListCtrlWidget(this, canonical, id, parentWidget),
            "tab" => new TabWidget(this, canonical, id, parentWidget),
            "slider" => new SliderWidget(this, canonical, id, parentWidget),
            "statusbar" => new StatusBarWidget(this, canonical, id, parentWidget),
            "slot" or "cooldownbutton" => new SlotWidget(this, canonical, id, parentWidget),
            "combobox" => new ComboBoxWidget(this, canonical, id, parentWidget),
            "listbox" => new ListBoxWidget(this, canonical, id, parentWidget),
            "radiogroup" => new RadioGroupWidget(this, canonical, id, parentWidget),
            "message" or "chatwindow" => new MessageWidget(this, canonical, id, parentWidget),
            "damagedisplay" => new StyledWidget(this, canonical, id, parentWidget, "extraStyle"),
            "gametooltip" or "unitframetooltip" => new GameTooltipWidget(this, canonical, id, parentWidget),
            _ => new Widget(this, canonical, id, parentWidget),
        };
        widget.ChildIndex = parentWidget == null ? 0 : childIndex;
        widget.PublishToParent = publish;
        // A root tooltip is a transient overlay and starts hidden. Child game tooltips are also used as inline,
        // rich-text layout widgets (for example a mail body), so they retain the native widget default visibility.
        if (widget is GameTooltipWidget && parentWidget is null) widget.InitVisible(false);
        if (parentWidget is null) { _topLevel.Add(widget); HierarchyChanged(); }
        else parentWidget.AddChild(widget);
        _byId[id] = widget;
        InvalidateLayout();
        WidgetCreated?.Invoke(widget);
        widget.CreateNativeChildren();
        WidgetReady?.Invoke(widget);
        return widget;
    }

    /// <summary>Maps UOT_* numbers and type strings to the factory's type names.</summary>
    internal static string TypeNameOf(object type) => type switch
    {
        double d => (int)d switch
        {
            1 => "label",
            3 or 53 => "x2editbox",
            4 => "editboxmultiline",
            5 => "listbox",
            24 => "slider",
            45 => "listctrl",
            46 => "emptywidget",
            _ => $"uot_{(int)d}",
        },
        string s => s.Trim().ToLowerInvariant(),
        _ => "emptywidget",
    };

    internal Widget? GetWidget(string id) => _byId.GetValueOrDefault(id);
    public Widget? GetChildByName(string id) => _topLevel.LastOrDefault(w => w.Id == id);

    /// <summary>UIParent:GetTextureData(path, key) -> { coords = {x,y,w,h}, inset = {...}, extent = {w,h}, colors = {...} }.</summary>
    public Dictionary<object, object?>? GetTextureData(string path, string key)
    {
        var data = Settings.TextureRegion(path, key);
        if (data == null) return null;
        var t = new Dictionary<object, object?>();
        static Dictionary<object, object?> Arr(params float[] v)
        {
            var a = new Dictionary<object, object?>();
            for (var i = 0; i < v.Length; i++) a[(double)(i + 1)] = (double)v[i];
            return a;
        }
        if (data.Coords is { } c) t["coords"] = Arr(c.X, c.Y, c.Width, c.Height);
        if (data.Inset is { } n) t["inset"] = Arr(n.Left, n.Top, n.Right, n.Bottom);
        if (data.Extent is { } e) t["extent"] = Arr(e.Width, e.Height);
        else if (data.Coords is { } c2) t["extent"] = Arr(Math.Abs(c2.Width), Math.Abs(c2.Height));
        var colors = new Dictionary<object, object?>();
        foreach (var (k, v) in data.Colors) colors[k] = Arr(v.R, v.G, v.B, v.A);
        t["colors"] = colors;
        if (data.Type != null) t["type"] = data.Type;
        return t;
    }

    public Dictionary<object, object?>? GetTextureKeyData(string path, string key) => GetTextureData(path, key);

    /// <summary>UIParent:GetColorData("font"|"texture", key) -> {r, g, b, a} in 0..1.</summary>
    public Dictionary<object, object?>? GetColorData(string kind, string key)
    {
        var c = kind.Equals("font", StringComparison.OrdinalIgnoreCase) ? Settings.FontColor(key) : Settings.EtcColor(key);
        // unknown keys give white: the client never returns nil here (components/money.lua passes an already
        // formatted "|cAARRGGBB" as a key and formats the result as a string)
        var color = c ?? UiColor.White;
        return new Dictionary<object, object?> { [1.0] = (double)color.R, [2.0] = (double)color.G, [3.0] = (double)color.B, [4.0] = (double)color.A };
    }

    public Dictionary<object, object?>? GetFontColor(string key) => GetColorData("font", key);
    public double GetEtcValue(string key) => Settings.EtcValue(key) ?? 0;
    /// <summary>UIParent:InitFontSize() -> { small = 11, middle = 13, ... } from ui/setting/font_size.g.</summary>
    public Dictionary<object, object?> InitFontSize()
        => Settings.FontSizes.ToDictionary(kv => (object)kv.Key.ToLowerInvariant(), kv => (object?)(double)kv.Value);

    public void SetEventHandler(string name, object? handler)
    {
        if (handler == null) return;
        if (!_eventHandlers.TryGetValue(name, out var list)) _eventHandlers[name] = list = [];
        list.Add(handler);
    }

    public void ReleaseEventHandler(string name, object? handler = null)
    {
        if (handler == null) _eventHandlers.Remove(name);
        else if (_eventHandlers.TryGetValue(name, out var list)) list.Remove(handler);
    }

    // ------------------------------------------------------------------ events

    internal void RegisterEvent(Widget widget, string name)
    {
        if (!_eventWidgets.TryGetValue(name, out var list)) _eventWidgets[name] = list = [];
        if (!list.Contains(widget)) list.Add(widget);
    }

    internal void UnregisterEvent(Widget widget, string name)
    {
        if (_eventWidgets.TryGetValue(name, out var list)) list.Remove(widget);
    }

    internal void UnregisterAllEvents(Widget widget)
    {
        foreach (var list in _eventWidgets.Values) list.Remove(widget);
    }

    internal bool HasEventListeners(string name) =>
        (_eventWidgets.TryGetValue(name, out var l) && l.Count > 0) || (_eventHandlers.TryGetValue(name, out var h) && h.Count > 0);

    /// <summary>Delivers a game event to every widget that registered it (OnEvent(self, name, ...)) and to UIParent handlers.</summary>
    /// <summary>X2_TRACE_EVENTS=NAME1,NAME2 (or *): logs matching UI events with their arguments and listener counts.</summary>
    private static readonly string[] TraceEvents = (System.Environment.GetEnvironmentVariable("X2_TRACE_EVENTS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Host veto for game events (false drops the event), e.g. LEFT_LOADING while the world's loading screen is up.</summary>
    public Func<string, bool>? EventFilter { get; set; }
    /// <summary>Filter for roots whose own EventFilter is not set yet (a stage's root dispatches during its build, before
    /// the host sees it).</summary>
    public static Func<string, bool>? DefaultEventFilter { get; set; }

    internal void DispatchEvent(string name, params object?[] arguments)
    {
        if ((EventFilter ?? DefaultEventFilter) is { } filter && !filter(name))
        {
            if (TraceEvents.Length > 0 && (TraceEvents[0] == "*" || TraceEvents.Any(t => name.Contains(t, StringComparison.Ordinal))))
                Trace($"[x2 event] {name} held by the host");
            return;
        }
        if (TraceEvents.Length > 0 && (TraceEvents[0] == "*" || TraceEvents.Any(t => name.Contains(t, StringComparison.Ordinal))))
            Trace($"[x2 event] {name}({string.Join(", ", arguments.Select(a => a?.ToString() ?? "nil"))}) -> " +
                  $"{(_eventWidgets.TryGetValue(name, out var l) ? l.Count : 0)} widget(s)");
        if (_eventWidgets.TryGetValue(name, out var list))
        {
            var args = new object?[arguments.Length + 1];
            args[0] = name;
            Array.Copy(arguments, 0, args, 1, arguments.Length);
            foreach (var widget in list.ToArray())
                if (!widget.Destroyed)
                    widget.InvokeHandler("OnEvent", args);
        }
        if (_eventHandlers.TryGetValue(name, out var handlers))
            foreach (var h in handlers.ToArray())
                FunctionInvoker?.Invoke(h, arguments);
    }

    internal object? InvokeHandler(Widget widget, object handler, string name, object?[] args)
        => handler is NativeHandler native ? native.Invoke(widget, args) : HandlerInvoker?.Invoke(widget, handler, name, args);

    // ------------------------------------------------------------------ lifetime / visibility

    internal void Destroy(Widget widget)
    {
        foreach (var w in widget.SelfAndDescendants().ToArray())
        {
            if (w is WindowWidget window) window.OnDeleted();
            w.Destroyed = true;
            UnregisterAllEvents(w);
            if (Focused == w) Focused = null;
            if (_hover == w) _hover = null;
            if (_pressed == w) _pressed = null;
            if (_byId.TryGetValue(w.Id, out var byId) && ReferenceEquals(byId, w)) _byId.Remove(w.Id);
            WidgetDestroyed?.Invoke(w);
        }
        if (widget.Parent == null) { _topLevel.Remove(widget); HierarchyChanged(); }
        else widget.Parent.RemoveChild(widget);
        InvalidateLayout();
    }

    /// <summary>OnShow/OnHide for the widget and for descendants whose effective visibility changed with it.</summary>
    internal void OnVisibilityChanged(Widget widget, bool shown)
    {
        _paintOrder = null;
        widget.InvokeHandler(shown ? "OnShow" : "OnHide");
        if (widget.Parent != null && !widget.Parent.IsEffectivelyVisible()) return;
        foreach (var child in widget.Children.ToArray())
            Propagate(child, shown);
        if (!shown)
        {
            if (Focused != null && !Focused.IsEffectivelyVisible()) SetFocusTo(null);
            if (_hover != null && !_hover.IsEffectivelyVisible()) SetHover(null);
        }
        InvalidateLayout();
    }

    private static void Propagate(Widget widget, bool shown)
    {
        if (!widget.Visible) return;
        widget.InvokeHandler(shown ? "OnShow" : "OnHide");
        foreach (var child in widget.Children.ToArray())
            Propagate(child, shown);
    }

    internal void SetFocusTo(Widget? widget)
    {
        if (ReferenceEquals(Focused, widget)) return;
        var old = Focused;
        Focused = widget;
        if (old is EditBoxWidget oldEdit) oldEdit.NormalizeOnFocusLost();
        old?.InvokeHandler("OnFocusLost");
        if (widget is EditBoxWidget edit)
        {
            edit.Cursor = edit.GetText().Length;
            edit.AllSelected = edit.SelectAllWhenFocused && edit.GetText().Length > 0;
        }
        widget?.InvokeHandler("OnFocusGained");
    }

    // ------------------------------------------------------------------ frame update

    internal void Update(double deltaMilliseconds)
    {
        _clock += deltaMilliseconds;
        if (ActiveEffects.Count > 0)
            ActiveEffects.RemoveWhere(d => d.Effect == null || !d.Effect.Tick(deltaMilliseconds / 1000));
        // Only widgets with per-frame work (fades, slot/message ticks, OnUpdate handlers); the list is rebuilt when the
        // hierarchy, an OnUpdate handler or a fade changes instead of scanning every widget each frame.
        foreach (var w in TickWidgets())
        {
            if (w is WindowWidget window && window.AnimatingScale)
            {
                window.AnimationElapsed = Math.Min(window.AnimationElapsed + (float)deltaMilliseconds, window.AnimationDuration);
                window.StartScale = 0.9f + 0.1f * Math.Clamp(window.AnimationElapsed / window.AnimationDuration, 0, 1);
            }
            if (w.FadeTime > 0 && w.FadeElapsed < w.FadeTime)
            {
                w.FadeElapsed = (float)Math.Min(w.FadeTime, w.FadeElapsed + deltaMilliseconds);
                var t = w.FadeElapsed / w.FadeTime;
                w.FadeAlpha = w.FadeFrom + (w.FadeTo - w.FadeFrom) * t;
                if (w.FadeElapsed >= w.FadeTime)
                {
                    var fadedOut = w.FadingOut && !w.Visible;
                    w.FadingOut = false; w.FadeTime = 0; w.FadeAlpha = 1;
                    _paintOrder = null;
                    if (fadedOut) w.InvokeHandler("OnVisibleChanged", false);
                }
            }
            if (w is SlotWidget slot) slot.Tick(deltaMilliseconds);
            else if (w is MessageWidget message) message.Tick(deltaMilliseconds);
            else if (w is SliderWidget slider) slider.Tick();
            if (w.HasHandler("OnUpdate") && !w.Destroyed && w.IsEffectivelyVisible())
            {
                if (!ProfileUpdates) { w.InvokeHandler("OnUpdate", deltaMilliseconds); continue; }
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                w.InvokeHandler("OnUpdate", deltaMilliseconds);
                var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                ProfileOnUpdateMs += ms;
                ProfileOnUpdateCalls++;
                var key = Describe(w);
                ProfileByWidget[key] = ProfileByWidget.GetValueOrDefault(key) + ms;
            }
        }
    }

    /// <summary>X2_PROFILE=1: time spent in Lua OnUpdate handlers (total, calls, per widget path), reset by the reader.</summary>
    internal static readonly bool ProfileUpdates = System.Environment.GetEnvironmentVariable("X2_PROFILE") == "1";
    internal double ProfileOnUpdateMs;
    internal int ProfileOnUpdateCalls;
    internal readonly Dictionary<string, double> ProfileByWidget = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ layout

    internal UiRect Solve(LayoutElement e)
    {
        if (e.CachedVersion == _layoutVersion || e.Solving) return e.CachedRect;
        e.Solving = true;
        UiRect rect;
        try
        {
            var owner = e.OwnerWidget;
            var parentRect = owner is null ? RootRect : Solve(owner);
            if (e is Widget ew && owner is ListCtrlWidget list && (ew.Anchors.Count == 0 || list.RowIndexOf(ew) != null)
                && list.LayoutChild(ew, parentRect) is { } listRect)
            {
                e.CachedRect = listRect;
                e.CachedVersion = _layoutVersion;
                return listRect;
            }
            if (e is Widget sw && owner?.Parent is ListCtrlWidget rowList && rowList.LayoutSubItem(owner, sw, parentRect) is { } subRect)
            {
                e.CachedRect = subRect;
                e.CachedVersion = _layoutVersion;
                return subRect;
            }
            var (nw, nh) = e.NaturalSize;
            var xs = new float?[3];
            var ys = new float?[3];
            foreach (var anchor in e.Anchors)
            {
                var rel = ResolveTarget(anchor.RelativeTo, owner);
                var own = LayoutElement.Factors(anchor.Point);
                var target = LayoutElement.Factors(anchor.RelativePoint);
                xs[Slot(own.X)] = rel.X + rel.Width * target.X + anchor.X;
                ys[Slot(own.Y)] = rel.Y + rel.Height * target.Y + anchor.Y;
            }
            var (x, w) = SolveAxis(xs, parentRect.X, nw);
            var (y, h) = SolveAxis(ys, parentRect.Y, nh);
            if (e is Widget && owner is not null &&
                e.Anchors.All(a => a.RelativeTo is null || ReferenceEquals(a.RelativeTo, owner)))
            {
                x -= owner.ChildScrollX;
                y -= owner.ChildScrollY;
            }
            rect = new UiRect(x, y, Math.Max(0, w), Math.Max(0, h));
        }
        finally
        {
            e.Solving = false;
        }
        e.CachedRect = rect;
        e.CachedVersion = _layoutVersion;
        return rect;
    }

    private static int Slot(float factor) => factor < 0.25f ? 0 : factor > 0.75f ? 2 : 1;

    private static (float Origin, float Size) SolveAxis(float?[] s, float fallback, float natural)
    {
        if (s[0] is { } a && s[2] is { } b) return (a, b - a);
        if (s[0] is { } a1 && s[1] is { } m1) return (a1, 2 * (m1 - a1));
        if (s[1] is { } m2 && s[2] is { } b2) { var size = 2 * (b2 - m2); return (b2 - size, size); }
        if (s[0] is { } a3) return (a3, natural);
        if (s[1] is { } m3) return (m3 - natural / 2, natural);
        if (s[2] is { } b3) return (b3 - natural, natural);
        return (fallback, natural);
    }

    private UiRect ResolveTarget(object? target, Widget? owner) => target switch
    {
        null => owner is null ? RootRect : Solve(owner),
        LayoutElement element => Solve(element),
        UiRoot => RootRect,
        string s when s.Equals("UIParent", StringComparison.OrdinalIgnoreCase) => RootRect,
        string s => GetWidget(s) is { } w ? Solve(w) : RootRect,
        _ => RootRect,
    };

    // ------------------------------------------------------------------ drawing order / hit testing

    internal static int LayerRank(string layer)
    {
        var i = Array.IndexOf(LayerOrder, layer.ToLowerInvariant());
        return i < 0 ? 2 : i;
    }

    /// <summary>Widgets in paint order (back to front), including ones still fading out.</summary>
    // PaintOrder is needed by the draw pass and by every hit test (each pointer move); build it at most once per UI frame
    // (Update advances the clock) and again whenever the hierarchy changes.
    private List<Widget>? _paintOrder;
    private double _paintOrderClock = -1;

    internal List<Widget> PaintOrder()
    {
        if (_paintOrder != null && _paintOrderClock == _clock && _allWidgets != null) return _paintOrder;
        _paintOrderClock = _clock;
        return _paintOrder = BuildPaintOrder();
    }

    private List<Widget> BuildPaintOrder()
    {
        var result = new List<Widget>(_paintOrder?.Count ?? 256);
        var tooltips = new List<Widget>();
        // The native quest-directing layer is exclusive.  The client scripts put the
        // cinema window (and its fade window) on this layer and fade their legacy
        // systemLayerParent to zero; native x2ui consequently leaves only the cinema
        // visible.  Our world widgets do not all share that compatibility parent, so
        // preserve the layer contract here for both painting and hit testing.
        var questDirecting = LayerRank("questdirecting");
        var cinemaVisible = _topLevel.Any(w => LayerRank(w.UiLayer) == questDirecting &&
            !w.Destroyed && (w.Visible || w.FadingOut));
        foreach (var top in _topLevel.OrderBy(w => LayerRank(w.UiLayer)).ThenBy(w => w.RaiseOrder))
        {
            // only the layers below it are covered: dialog/system/tooltip windows stay (the zone banner, a
            // "system" window on UIParent, shows over the cinema in the original; systemLayerParent's children are
            // faded out by the directing script itself)
            if (cinemaVisible && LayerRank(top.UiLayer) < questDirecting) continue;
            Collect(top, result, tooltips);
        }
        // tooltips nested in other windows still draw above everything, as in the client
        foreach (var t in tooltips) Collect(t, result, null);
        return result;
    }

    private static void Collect(Widget w, List<Widget> into, List<Widget>? deferredTooltips)
    {
        if (w.Destroyed || (!w.Visible && !w.FadingOut)) return;
        if (deferredTooltips != null && w is GameTooltipWidget && w.Parent != null)
        {
            deferredTooltips.Add(w);
            return;
        }
        into.Add(w);
        var children = w.Children;
        var sorted = true;
        for (var i = 1; i < children.Count && sorted; i++) sorted = children[i - 1].RaiseOrder <= children[i].RaiseOrder;
        if (sorted)
            for (var i = 0; i < children.Count; i++) Collect(children[i], into, deferredTooltips);
        else
            foreach (var c in children.OrderBy(c => c.RaiseOrder))
                Collect(c, into, deferredTooltips);
    }

    private static readonly string[] MouseHandlers =
        ["OnClick", "OnMouseDown", "OnMouseUp", "OnEnter", "OnLeave", "OnDragStart", "OnDragReceive", "OnWheelUp", "OnWheelDown", "OnDoubleClick"];

    private static Widget TopOf(Widget w)
    {
        while (w.Parent != null) w = w.Parent;
        return w;
    }

    private static bool IsPassiveContainer(Widget w)
        => (w is EmptyWidget || w is WindowWidget) && !w.DragEnabled && !MouseHandlers.Any(w.HasHandler);

    internal Widget? HitTest(float x, float y)
    {
        // The top-most widget under the pointer that reacts to the mouse wins; plain containers (empty widgets and
        // windows without mouse handlers, e.g. a tab page laid over the bag slots) only take the pointer when nothing
        // interactive lies beneath them, so they still keep clicks out of the world.
        var order = PaintOrder();
        Widget? fallback = null;
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var w = order[i];
            if (!w.PickEnabled || !w.ClickableState || !w.IsEffectivelyVisible() || w.FadingOut) continue;
            if (!w.ScreenRect.Contains(x, y)) continue;
            // never reach through a window into another top-level window behind it
            if (fallback != null && !ReferenceEquals(TopOf(w), TopOf(fallback))) return fallback;
            if (!IsPassiveContainer(w)) return w;
            fallback ??= w;
        }
        return fallback;
    }

    // ------------------------------------------------------------------ input

    private void SetHover(Widget? w)
    {
        if (ReferenceEquals(_hover, w)) return;
        var old = _hover;
        _hover = w;
        if (old != null) { old.Hovered = false; old.InvokeHandler("OnLeave"); }
        if (w != null) { w.Hovered = true; w.InvokeHandler("OnEnter"); }
    }

    internal void PointerMove(float x, float y)
    {
        CursorX = x;
        CursorY = y;
        if (_resizingChat != null)
        {
            _resizingChat.ResizeDrag(x, y);
            return;
        }
        if (_resizingWindow != null) { _resizingWindow.ResizeDrag(x, y); return; }
        if (_dragSlider != null)
        {
            SetSliderFromPointer(_dragSlider, x, y);
            return;
        }
        var hovered = HitTest(x, y);
        var hoveredList = hovered as ListBoxWidget;
        if (_hoveredList != null && !ReferenceEquals(_hoveredList, hoveredList)) _hoveredList.HoverAt(-1);
        _hoveredList = hoveredList;
        hoveredList?.HoverAt(y);
        SetHover(hovered);
        if (_pressed is { } source && (source.DragEnabled || source.HasHandler("OnDragStart")) && _dragSource == null
            && (source.DragCondition != 1 || ShiftDown)
            && Math.Abs(x - _pressX) + Math.Abs(y - _pressY) >= 4)
        {
            _dragSource = source;
            if (TraceInput) Trace($"[x2 input] drag start {Describe(source)}");
            if (source.HasHandler("OnDragStart")) source.InvokeHandler("OnDragStart", _pressedButton);
            else if (source is NativeSlotWidget slot && _pressedButton == "LeftButton")
                SlotInteraction?.Invoke(slot, "Start");
        }
    }

    internal void PointerButton(float x, float y, string button, bool pressed)
    {
        CursorX = x;
        CursorY = y;
        var hit = HitTest(x, y);
        SetHover(hit);
        if (pressed)
        {
            if (button == "LeftButton" && (hit as SliderWidget ?? hit?.Parent as SliderWidget) is { NativeInteraction: true } slider)
            {
                _dragSlider = slider;
                SetSliderFromPointer(slider, x, y);
                return;
            }
            if (button == "LeftButton")
            {
                for (var candidate = hit; candidate != null; candidate = candidate.Parent)
                    if (candidate is WindowWidget window && window.BeginResizeDrag(x, y))
                    {
                        _resizingWindow = window;
                        return;
                    }
                for (var candidate = hit; candidate != null; candidate = candidate.Parent)
                    if (candidate is ChatWindowWidget chat &&
                        (hit is not ButtonWidget || x >= chat.ScreenRect.Right - Math.Max(1, chat.ResizingBorderSize.Right)) &&
                        chat.BeginResizeDrag(x, y))
                    {
                        _resizingChat = chat;
                        return;
                    }
            }
            _pressed = hit;
            _pressedButton = button;
            if (TraceInput) Trace($"[x2 input] press {button} at {x:F0},{y:F0} on {Describe(hit)} drag={hit?.DragEnabled}");
            _pressX = x;
            _pressY = y;
            if (hit == null) { SetFocusTo(null); return; }
            // Activating a window brings the whole top-level content (including popups that
            // extend beyond its rectangle) ahead of overlapping normal-layer windows.
            TopOf(hit).Raise();
            hit.Pressed = true;
            hit.InvokeHandler("OnMouseDown", button);
            if (hit is EditBoxWidget { Enabled: true } edit) SetFocusTo(edit);
            else if (hit.FocusEnabled && hit.Enabled) SetFocusTo(hit);
            else if (Focused is EditBoxWidget && !(hit.IsDescendantOf(Focused))) SetFocusTo(null);
            return;
        }
        if (_resizingChat != null)
        {
            _resizingChat.ResizeDrag(x, y);
            _resizingChat = null;
            return;
        }
        if (_resizingWindow != null)
        {
            _resizingWindow.ResizeDrag(x, y);
            _resizingWindow = null;
            return;
        }
        if (_dragSlider != null)
        {
            SetSliderFromPointer(_dragSlider, x, y);
            _dragSlider = null;
            return;
        }
        var target = _pressed;
        _pressed = null;
        if (target == null) return;
        target.Pressed = false;
        if (_dragSource is { } source)
        {
            _dragSource = null;
            if (TraceInput) Trace($"[x2 input] drop {Describe(source)} on {Describe(hit)}");
            source.InvokeHandler("OnDragStop", button);
            if (hit != null && !ReferenceEquals(hit, source))
            {
                if (hit.HasHandler("OnDragReceive")) hit.InvokeHandler("OnDragReceive", source, button);
                else if (hit is NativeSlotWidget receivingSlot && button == "LeftButton")
                    SlotInteraction?.Invoke(receivingSlot, "Receive");
            }
            else if (source is NativeSlotWidget abandoned) SlotInteraction?.Invoke(abandoned, "Cancel");
            if (button == "LeftButton" && source is SlotWidget sourceSlot)
                SlotDropped?.Invoke(sourceSlot, hit as SlotWidget);
            return;
        }
        target.InvokeHandler("OnMouseUp", button);
        if (TraceInput) Trace($"[x2 input] release {button} on {Describe(hit)} (pressed {Describe(target)}, enabled {target.Enabled}" +
                              (target is NativeSlotWidget ns ? $", accepts {ns.AcceptsClick(button)}, OnClick {target.HasHandler("OnClick")}, PreClick {target.HasHandler("PreClick")}" : "") + ")");
        if (!ReferenceEquals(target, hit) || !target.Enabled) return;
        if (target is NativeSlotWidget slot && !slot.AcceptsClick(button)) return;
        if (target is ButtonWidget and not NativeSlotWidget && !((ButtonWidget)target).AcceptsClick(button)) return;
        var doubleClick = ReferenceEquals(_lastClickWidget, target) && _clock - _lastClickTime < 400;
        _lastClickTime = doubleClick ? -1 : _clock;
        _lastClickWidget = target;
        if (button == "LeftButton" && target is ListBoxWidget listBox) listBox.SelectAt(y);
        if (FindListRow(target) is { } row) row.List.OnRowClicked(row.Index, doubleClick);
        if (target is CheckButtonWidget check && button == "LeftButton") check.SetChecked(!check.Checked);
        target.InvokeHandler("OnClick", button);
        // CreateIconButton installs an OnClick wrapper on inventory slots, but its
        // OnClickProc is absent in the shipped inventory script. PreClick marks that
        // native inventory path; the wrapper only handles tooltip presentation.
        if (target is NativeSlotWidget nativeSlot && !nativeSlot.DefaultClickDisabled &&
            (!target.HasHandler("OnClick") || target.HasHandler("PreClick")))
        {
            if (button == "LeftButton" && target.InvokeHandler("PreClick") is not true)
                SlotInteraction?.Invoke(nativeSlot, "Click");
            else if (button == "RightButton" && target.InvokeHandler("PreUse") is not true)
                SlotInteraction?.Invoke(nativeSlot, "Use");
        }
        if (button == "LeftButton" && target is SlotWidget activated) SlotActivated?.Invoke(activated);
        if (doubleClick) target.InvokeHandler("OnDoubleClick", button);
    }

    private static void SetSliderFromPointer(SliderWidget slider, float x, float y)
    {
        if (slider.Max - slider.Min <= 0) return;
        slider.SetValue(slider.ValueAt(x, y));
    }

    private (ListCtrlWidget List, int Index)? FindListRow(Widget w)
    {
        for (var c = w; c != null; c = c.Parent)
            if (c.Parent is ListCtrlWidget list && list.RowIndexOf(c) is { } index)
                return (list, index);
        // a click on the list itself (its row items are not pickable, e.g. the mail list's text rows): the row under the cursor
        if (w is ListCtrlWidget clickedList)
            foreach (var child in clickedList.Children)
                if (child.IsEffectivelyVisible() && clickedList.RowIndexOf(child) is { } rowIndex && child.ScreenRect.Contains(CursorX, CursorY))
                    return (clickedList, rowIndex);
        return null;
    }

    internal void Wheel(float x, float y, bool up)
    {
        var name = up ? "OnWheelUp" : "OnWheelDown";
        for (var w = HitTest(x, y); w != null; w = w.Parent)
            if (w.HasHandler(name)) { w.InvokeHandler(name); return; }
    }

    internal void TextInput(string text)
    {
        if (Focused is EditBoxWidget edit && edit.IsEffectivelyVisible()) edit.InsertText(text);
    }

    /// <summary>Returns true when the key was consumed.</summary>
    internal bool KeyPress(UiKey key)
    {
        var focused = Focused is { } f && f.IsEffectivelyVisible() && !f.Destroyed ? f : null;
        switch (key)
        {
            case UiKey.Enter:
                if (focused == null)
                {
                    var chat = PaintOrder().OfType<ChatWindowWidget>().LastOrDefault(w => w.IsEffectivelyVisible());
                    if (chat == null) return false;
                    chat.ActivateInput();
                    return true;
                }
                focused.InvokeHandler("OnEnterPressed");
                return true;
            case UiKey.Escape:
                if (focused != null && focused.HasHandler("OnEscapePressed")) { focused.InvokeHandler("OnEscapePressed"); return true; }
                if (focused != null) { SetFocusTo(null); return true; }
                var closable = PaintOrder().LastOrDefault(w => w.CloseOnEscape && w.Visible);
                if (closable == null) return false;
                if (closable.HasHandler("OnCloseByEsc")) closable.InvokeHandler("OnCloseByEsc");
                else closable.Show(false);
                return true;
            case UiKey.Tab:
                if (focused == null) return false;
                if (focused.HasHandler("OnTabPressed")) { focused.InvokeHandler("OnTabPressed"); return true; }
                FocusNext(focused);
                return true;
        }
        if (focused is not EditBoxWidget edit) return false;
        switch (key)
        {
            case UiKey.Backspace: edit.Backspace(false); break;
            case UiKey.Delete: edit.Backspace(true); break;
            case UiKey.Left: edit.MoveCursor(-1); break;
            case UiKey.Right: edit.MoveCursor(1); break;
            case UiKey.Home: edit.MoveCursor(0, toStart: true); break;
            case UiKey.End: edit.MoveCursor(0, toEnd: true); break;
            case UiKey.SelectAll: edit.AllSelected = edit.GetText().Length > 0; break;
            default: return false;
        }
        return true;
    }

    private void FocusNext(Widget from)
    {
        var top = from;
        while (top.Parent != null) top = top.Parent;
        var candidates = top.SelfAndDescendants().OfType<EditBoxWidget>().Where(e => e.IsEffectivelyVisible() && e.Enabled).ToList();
        if (candidates.Count == 0) return;
        var i = candidates.IndexOf(from as EditBoxWidget ?? candidates[0]);
        SetFocusTo(candidates[(i + 1) % candidates.Count]);
    }
}
