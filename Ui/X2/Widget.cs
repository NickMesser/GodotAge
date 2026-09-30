#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// Base X2 widget (emptywidget/window/label/...). Public instance methods are the script API: the Lua binding maps
/// widget:Method(...) onto them by name, so names and argument order follow the client scripts.
/// </summary>
public class Widget : LayoutElement
{
    private readonly UiRoot _root;
    private readonly List<Widget> _children = [];
    private readonly List<Drawable> _drawables = [];
    private readonly Dictionary<string, object> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private string _text = "";

    internal override UiRoot Root => _root;
    internal override Widget? OwnerWidget => Parent;
    public string TypeName { get; }
    public string Id { get; }
    public Widget? Parent { get; }
    public IReadOnlyList<Widget> Children => _children;
    public IReadOnlyList<Drawable> Drawables => _drawables;
    public WidgetStyle style { get; }

    internal int Sequence { get; }
    internal int RaiseOrder { get; set; }
    internal bool Destroyed { get; set; }
    public bool Visible { get; private set; } = true;
    public bool Enabled { get; private set; } = true;
    public bool PickEnabled { get; private set; } = true;
    public bool FocusEnabled { get; private set; }
    public float Alpha { get; private set; } = 1;
    public string UiLayer { get; private set; } = "normal";
    public UiInsets TextInset { get; private set; }
    public bool AutoResize { get; private set; }
    public bool AutoWordwrap { get; private set; }
    public bool EllipsisEnabled { get; private set; }
    public float LineSpace { get; private set; } = -1;
    public string Title { get; private set; } = "";
    public bool CloseOnEscape { get; private set; }
    public bool HidingIsRemove { get; private set; }
    public bool Modal { get; private set; }
    public bool DragEnabled { get; private set; }
    public int DragCondition { get; private set; }
    public bool ClickableState { get; private set; } = true;
    public int ChildIndex { get; internal set; }
    internal float ChildScrollX { get; private set; }
    internal float ChildScrollY { get; private set; }

    /// <summary>Shift directly anchored children inside a scrolling viewport.</summary>
    public void ChangeChildAnchorByScrollValue(string direction, float value)
    {
        if (direction.Equals("horz", StringComparison.OrdinalIgnoreCase)) ChildScrollX = value;
        else if (direction.Equals("vert", StringComparison.OrdinalIgnoreCase)) ChildScrollY = value;
        else return;
        Root.InvalidateLayout();
    }

    // input / animation state (renderer + input router)
    internal bool Hovered;
    internal bool Pressed;
    internal float FadeAlpha = 1;
    internal float FadeFrom, FadeTo = 1, FadeTime, FadeElapsed;
    internal bool FadingOut;

    internal Widget(UiRoot root, string typeName, string id, Widget? parent)
    {
        _root = root;
        TypeName = typeName;
        Id = id;
        Parent = parent;
        Sequence = root.NextSequence();
        RaiseOrder = Sequence;
        style = new WidgetStyle(root, this);
    }

    internal void InitVisible(bool visible) => Visible = visible;

    /// <summary>Script-visible fields the native widget carries (style objects such as slot.cooltime_style).</summary>
    internal Dictionary<string, object> NativeFields { get; } = new(StringComparer.Ordinal);

    /// <summary>Creates the native child widgets of composite types (combobox.dropdown, ...); runs after creation.</summary>
    internal virtual void CreateNativeChildren() { }
    internal void AddChild(Widget child) { _children.Add(child); Root.HierarchyChanged(); }
    internal void RemoveChild(Widget child) { _children.Remove(child); Root.HierarchyChanged(); }
    internal void AddDrawable(Drawable drawable) => _drawables.Add(drawable);
    internal virtual void OnStyleChanged() { if (AutoResize) ApplyAutoResize(); }
    /// <summary>Appends this widget and its descendants in pre-order (no nested iterators: the UI has ~10k widgets).</summary>
    internal void CollectSelfAndDescendants(List<Widget> into)
    {
        into.Add(this);
        for (var i = 0; i < _children.Count; i++) _children[i].CollectSelfAndDescendants(into);
    }

    internal IEnumerable<Widget> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in _children.ToArray())
            foreach (var d in child.SelfAndDescendants())
                yield return d;
    }

    // ------------------------------------------------------------------ identity / hierarchy

    public string GetId() => Id;
    public string GetName() => Id;
    public string GetObjectTypeName() => TypeName;
    public string GetObjectType() => TypeName;
    public Widget? GetParent() => Parent;
    public Widget? GetChildByName(string id) => _children.FirstOrDefault(x => x.Id == id);
    public int GetChildCount() => _children.Count;
    public bool IsDescendantOf(Widget? other)
    {
        for (var p = Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, other)) return true;
        return false;
    }

    /// <summary>
    /// CreateChildWidget(type, id, index, reflectToScriptTable): the flag publishes the child as parent.id (parent.id[index])
    /// for the scripts; it does not hide the child (labels made with false are shown, e.g. the esc menu sections).
    /// </summary>
    public Widget CreateChildWidget(string type, string id, double index = 0, bool reflect = true)
    {
        Root.PendingChildIndex = (int)index;
        Root.PendingPublish = reflect;
        return Root.CreateWidget(type, id, this);
    }

    public Widget CreateChildWidgetByType(object type, string id, double index = 0, bool reflect = true)
        => CreateChildWidget(UiRoot.TypeNameOf(type), id, index, reflect);

    /// <summary>Whether the scripts see this widget as a field of its parent (see CreateChildWidget).</summary>
    public bool PublishToParent { get; internal set; } = true;

    // ------------------------------------------------------------------ drawables

    public TextureDrawable CreateDrawable(string texturePath, string key, string layer)
        => Add(new TextureDrawable(Root, this, DrawableKind.Image, texturePath, key, layer));
    public TextureDrawable CreateImageDrawable(string texturePath, string layer)
        => Add(new TextureDrawable(Root, this, DrawableKind.Image, texturePath, null, layer));
    public TextureDrawable CreateNinePartDrawable(string texturePath, string layer)
        => Add(new TextureDrawable(Root, this, DrawableKind.NinePart, texturePath, null, layer));
    public TextureDrawable CreateThreePartDrawable(string texturePath, string layer)
        => Add(new TextureDrawable(Root, this, DrawableKind.ThreePart, texturePath, null, layer));
    public TextureDrawable CreateEffectDrawable(string texturePath, string layer)
        => Add(new TextureDrawable(Root, this, DrawableKind.Image, texturePath, null, layer) { Effect = new DrawableEffect() });
    public TextureDrawable CreateEffectDrawableByKey(string texturePath, string key, string layer)
        => Add(new TextureDrawable(Root, this, DrawableKind.Image, texturePath, key, layer) { Effect = new DrawableEffect() });
    public TextureDrawable CreateIconDrawable(string layer)
        => Add(new TextureDrawable(Root, this, DrawableKind.Image, null, null, layer));
    /// <summary>CreateStateDrawable(UI_BUTTON_*, UOT_*_DRAWABLE, texture, layer): a drawable shown in one button state.</summary>
    public TextureDrawable CreateStateDrawable(double state, double drawableType, string texturePath, string layer)
    {
        var kind = (int)drawableType == 8 ? DrawableKind.NinePart : DrawableKind.Image;
        var d = Add(new TextureDrawable(Root, this, kind, texturePath, null, layer));
        if (this is ButtonWidget button) button.SetStateDrawable((ButtonState)Math.Clamp((int)state, 0, 3), d);
        return d;
    }

    public ColorDrawable CreateColorDrawable(float r, float g, float b, float a, string layer)
        => Add(new ColorDrawable(Root, this, new UiColor(r, g, b, a), layer));
    public ColorDrawable CreateColorDrawableByKey(string colorKey, string layer)
        => Add(new ColorDrawable(Root, this, Root.Settings.EtcColor(colorKey) ?? UiColor.White, layer));
    public TextDrawable CreateTextDrawable(object? font, object? size, string layer)
    {
        var d = Add(new TextDrawable(Root, this, layer));
        if (font is string f && size is double s) d.style.SetFont(f, (float)s);
        return d;
    }

    private T Add<T>(T drawable) where T : Drawable
    {
        AddDrawable(drawable);
        Root.OnDrawableCreated(drawable);
        return drawable;
    }

    // ------------------------------------------------------------------ visibility / state

    public virtual void Show(bool show = true, double fadeMilliseconds = 0)
    {
        var fade = (float)Math.Max(0, fadeMilliseconds);
        if (Visible == show)
        {
            if (show && FadingOut) { FadingOut = false; StartFade(FadeAlpha, 1, fade); }
            return;
        }
        var wasEffective = IsEffectivelyVisible();
        Visible = show;
        if (show)
        {
            FadingOut = false;
            StartFade(fade > 0 ? 0 : 1, 1, fade);
            // a window that opens comes to the front of its layer, as in the client
            if (Parent == null) Raise();
        }
        else if (fade > 0 && wasEffective)
        {
            FadingOut = true;
            StartFade(FadeAlpha, 0, fade);
        }
        else
        {
            FadingOut = false;
            FadeAlpha = 1;
        }
        Root.OnVisibilityChanged(this, show);
        // OnVisibleChanged(visible): immediately for a show or an instant hide; for a faded hide when the fade-out ends
        // (UiRoot.Update), which the quest directing window waits for (quest_context_directing.lua CreateFadeWnd)
        if (show || !FadingOut) InvokeHandler("OnVisibleChanged", show);
        if (!show && HidingIsRemove) Root.Destroy(this);
    }

    private void StartFade(float from, float to, float time)
    {
        FadeFrom = from;
        FadeTo = to;
        FadeTime = time;
        FadeElapsed = 0;
        FadeAlpha = time > 0 ? from : to;
        if (time > 0) Root.TickSetChanged();
    }

    /// <summary>EnableScroll(true): a scroll viewport; the client clips its children to it (the minimap window, scroll lists).</summary>
    public bool ScrollEnabled { get; private set; }
    public void EnableScroll(bool enable) => ScrollEnabled = enable;

    public void Hide() => Show(false);
    public void SetVisible(bool visible) => Show(visible);
    // The native query reports this widget's own Show state. Parent clipping
    // belongs to IsEffectivelyVisible, used by painting and input routing.
    public bool IsVisible() => Visible;
    public bool IsVisibleSelf() => Visible;
    internal bool IsEffectivelyVisible() => !Destroyed && Visible && (Parent == null || Parent.IsEffectivelyVisible());

    public virtual void Enable(bool enabled = true, bool children = false)
    {
        if (children)
            foreach (var c in _children) c.Enable(enabled, true);
        if (Enabled == enabled) return;
        Enabled = enabled;
        if (!enabled && Root.Focused == this) Root.SetFocusTo(null);
        InvokeHandler("OnEnableChanged", enabled);
    }

    public bool IsEnabled() => Enabled;

    public void EnablePick(bool enabled = true, bool children = false)
    {
        PickEnabled = enabled;
        if (children)
            foreach (var c in _children) c.EnablePick(enabled, true);
    }

    public bool IsPickEnabled() => PickEnabled;
    public void EnableFocus(bool enabled = true) => FocusEnabled = enabled;
    public void SetFocus() => Root.SetFocusTo(this);
    public void ClearFocus() { if (Root.Focused == this) Root.SetFocusTo(null); }
    public bool HasFocus() => Root.Focused == this;
    public bool IsFocused() => Root.Focused == this;
    public void SetAlpha(float alpha) => Alpha = Math.Clamp(alpha, 0, 1);
    public float GetAlpha() => Alpha;
    public void SetUILayer(string layer) { UiLayer = layer; Raise(); }
    public string GetUILayer() => UiLayer;
    public void Raise() => RaiseOrder = Root.NextSequence();
    public void Lower() { RaiseOrder = 0; Root.HierarchyChanged(); }
    public bool IsMouseOver() => Hovered;
    public void SetTitle(string? title) => Title = title ?? "";
    public string GetTitle() => Title;
    public void SetCloseOnEscape(bool enabled, bool unused = false) => CloseOnEscape = enabled;
    public void EnableHidingIsRemove(bool enabled) => HidingIsRemove = enabled;
    public void SetWindowModal(bool enabled) => Modal = enabled;
    public void EnableDrag(bool enabled) => DragEnabled = enabled;
    public void SetDragCondition(int condition) => DragCondition = condition;
    public void Clickable(bool enabled) => ClickableState = enabled;
    public void ApplyUIScale(bool enabled) { }
    public float WidgetScale { get; private set; } = 1f;
    public void SetScale(float scale) => WidgetScale = scale > 0 ? scale : 1f;
    public void SetCharacterCacheDataHandler(object? handler) { }
    public void CancelRequestCharacterCacheData() { }
    public void SetDelegator(string name, Widget owner, object? callback)
    {
        if (callback == null) { ReleaseHandler(name); return; }
        SetHandler(name, new NativeHandler((_, args) => Root.FunctionInvoker?.Invoke(callback, [owner, .. args])));
    }
    public void SetSounds(string? sounds) { }
    public void StartMoving() { }
    public void StopMovingOrSizing() { }

    // ------------------------------------------------------------------ text

    public virtual void SetText(string? text)
    {
        var value = text ?? "";
        var changed = value != _text;
        _text = value;
        if (AutoResize) ApplyAutoResize();
        // OnTextChanged: not for SetText(text, false), and never re-entered from its own handler (the client's money edits
        // call SetText inside OnTextChanged; components/money.lua)
        if (changed && !_suppressTextChanged && !_inTextChanged)
        {
            _inTextChanged = true;
            try { InvokeHandler("OnTextChanged"); }
            finally { _inTextChanged = false; }
        }
    }

    /// <summary>SetText(text, fireEvent): the client's second argument chooses whether OnTextChanged fires.</summary>
    public void SetText(string? text, bool fireEvent)
    {
        if (fireEvent) { SetText(text); return; }
        _suppressTextChanged = true;
        try { SetText(text); }
        finally { _suppressTextChanged = false; }
    }

    private bool _suppressTextChanged, _inTextChanged;

    public string GetText() => _text;
    internal void SetTextSilently(string text) { _text = text; if (AutoResize) ApplyAutoResize(); }
    public int GetTextLength() => _text.Length;

    public virtual void SetInset(float left, float top, float right, float bottom)
    {
        TextInset = new UiInsets(left, top, right, bottom);
        if (AutoResize) ApplyAutoResize();
    }

    public LuaMulti GetInset() => new(TextInset.Left, TextInset.Top, TextInset.Right, TextInset.Bottom);

    public void SetAutoResize(bool enabled)
    {
        AutoResize = enabled;
        if (enabled) ApplyAutoResize();
    }

    public void SetAutoWordwrap(bool enabled) { AutoWordwrap = enabled; if (AutoResize) ApplyAutoResize(); }
    internal void InitWordwrap(bool enabled) => AutoWordwrap = enabled;
    public UiColor LineColor { get; private set; } = new(0.3f, 0.5f, 0.9f, 1);
    public void SetLineColor(float r, float g, float b, float a = 1) => LineColor = new UiColor(r, g, b, a);
    public void SetLineColorByKey(string key) { if (Root.Settings.FontColor(key) is { } c) LineColor = c; }
    public void SetEllipsis(bool enabled) => EllipsisEnabled = enabled;
    public void SetLineSpace(float space) { LineSpace = space; if (AutoResize) ApplyAutoResize(); }

    /// <summary>Whether text wraps inside the width (textboxes do; labels are one line).</summary>
    internal virtual bool WrapsText => AutoWordwrap;
    internal virtual string LineSpaceKey => "textbox";

    internal float EffectiveLineSpace => LineSpace >= 0 ? LineSpace : WrapsText ? Root.Settings.LineSpace(LineSpaceKey) : 0;

    public float GetLongestLineWidth()
    {
        var lines = TextLayout.Lines(this, WrapsText ? InnerWidth() : float.MaxValue);
        return lines.Count == 0 ? 0 : lines.Max(l => l.Width);
    }

    public float GetTextHeight()
    {
        var lines = TextLayout.Lines(this, WrapsText ? InnerWidth() : float.MaxValue);
        var lh = style.GetLineHeight();
        var n = Math.Max(1, lines.Count);
        return n * lh + (n - 1) * EffectiveLineSpace;
    }

    public int GetLineCount() => Math.Max(1, TextLayout.Lines(this, WrapsText ? InnerWidth() : float.MaxValue).Count);

    /// <summary>Text width available: the laid-out width (anchors may stretch it), minus the text inset.</summary>
    internal float InnerWidth()
    {
        var width = Anchors.Count > 1 ? ScreenRect.Width : NaturalSize.W;
        return Math.Max(1, width - TextInset.Left - TextInset.Right);
    }

    /// <summary>Labels and buttons grow to their text width, textboxes to their wrapped text height.</summary>
    internal virtual void ApplyAutoResize()
    {
        if (WrapsText)
        {
            var h = GetTextHeight() + TextInset.Top + TextInset.Bottom;
            base.SetHeight(h);
        }
        else
        {
            var w = TextLayout.PlainWidth(this, _text) + TextInset.Left + TextInset.Right;
            base.SetWidth(Math.Max(1, (float)Math.Ceiling(w)));
        }
    }

    // ------------------------------------------------------------------ handlers / events

    public void SetHandler(string name, object? handler)
    {
        if (handler == null) _handlers.Remove(name);
        else _handlers[name] = handler;
        if (name == "OnUpdate") Root.TickSetChanged();
    }

    public void ReleaseHandler(string name)
    {
        _handlers.Remove(name);
        if (name == "OnUpdate") Root.TickSetChanged();
    }
    public bool HasHandler(string name) => _handlers.ContainsKey(name);
    internal object? GetHandler(string name) => _handlers.GetValueOrDefault(name);

    /// <summary>Calls a script handler as handler(self, args...); returns its first result.</summary>
    internal object? InvokeHandler(string name, params object?[] args)
    {
        if (Destroyed || !_handlers.TryGetValue(name, out var handler)) return null;
        return Root.InvokeHandler(this, handler, name, args);
    }

    public void RegisterEvent(string name) => Root.RegisterEvent(this, name);
    public void UnregisterEvent(string name) => Root.UnregisterEvent(this, name);
    public void ReleaseEvent(string name) => Root.UnregisterEvent(this, name);
    public void UnregisterAllEvents() => Root.UnregisterAllEvents(this);

    // ------------------------------------------------------------------ misc widget API used by the base library

    public virtual void SetStyle(string? name) { }
    public void EnableDrawables(string layer) { }
    public void SetExtentByText() => ApplyAutoResize();

    public override string ToString() => $"{TypeName}:{Id}";
}
