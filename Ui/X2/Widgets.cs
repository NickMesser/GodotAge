#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

public enum ButtonState { Normal, Highlighted, Pushed, Disabled }

public sealed class EmptyWidget(UiRoot root, string type, string id, Widget? parent) : Widget(root, type, id, parent)
{
    public float MinValue { get; private set; }
    public float MaxValue { get; private set; }
    public void Init() { }
    public void SetMinMaxValues(float min, float max) { MinValue = min; MaxValue = Math.Max(min, max); }
}

public sealed class WindowWidget : Widget
{
    internal WindowWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent)
        => titleStyle = new WidgetStyle(root);

    public WidgetStyle titleStyle { get; }

    /// <summary>The window's own title line (title bars are "window" widgets), drawn with titleStyle inside TitleInset.</summary>
    public string TitleText { get; private set; } = "";
    public UiInsets TitleInset { get; private set; }
    public void SetTitleText(string? text) => TitleText = text ?? "";
    public string GetTitleText() => TitleText;
    /// <summary>SetStartAnimation(alpha, scale): the client's open animation (fade/scale in on Show). Recorded only; windows
    /// appear at once.</summary>
    public bool StartAnimation { get; private set; }
    private bool _startAlpha, _startScale;
    private float _animationFrom = 0f, _animationTo = 1f, _animationDuration = 200f;
    internal float AnimationElapsed { get; set; }
    internal float AnimationDuration => _animationDuration;
    internal float StartScale { get; set; } = 1f;
    internal bool AnimatingScale => _startScale && AnimationElapsed < _animationDuration;
    public void SetStartAnimation(bool alpha, bool scale = false)
    {
        StartAnimation = alpha || scale;
        _startAlpha = alpha;
        _startScale = scale;
    }
    public void SetAlphaAnimation(float from, float to, float seconds, float delay = 0)
    {
        _animationFrom = Math.Clamp(from, 0, 1);
        _animationTo = Math.Clamp(to, 0, 1);
        _animationDuration = Math.Max(1, seconds * 1000);
    }
    public override void Show(bool show = true, double fadeMilliseconds = 0)
    {
        var opening = show && !Visible;
        // decide before base.Show: its OnShow handler is where window.lua calls SetStartAnimation(true, true), and a fade
        // set up after a fade-less show never runs (the window stayed at alpha 0)
        var fadeIn = opening && _startAlpha;
        if (opening && StartAnimation)
        {
            AnimationElapsed = 0;
            StartScale = _startScale ? 0.9f : 1f;
            Root.TickSetChanged();
        }
        base.Show(show, fadeIn ? _animationDuration : fadeMilliseconds);
        if (fadeIn)
        {
            FadeFrom = _animationFrom;
            FadeTo = _animationTo;
            FadeAlpha = _animationFrom;
        }
    }
    public bool Resizing { get; private set; }
    public (float Width, float Height) MinResizingExtent { get; private set; }
    public (float Width, float Height) MaxResizingExtent { get; private set; }
    public UiInsets ResizingBorderSize { get; private set; }
    public string Category { get; private set; } = "";
    private object? _deletedHandler;
    private int _resizeEdges;
    private float _resizeX, _resizeY, _resizeWidth, _resizeHeight;
    public void UseResizing(bool enabled) => Resizing = enabled;
    public void SetMinResizingExtent(float width, float height) => MinResizingExtent = (width, height);
    public void SetMaxResizingExtent(float width, float height) => MaxResizingExtent = (width, height);
    public void SetResizingBorderSize(float left, float top, float right, float bottom)
        => ResizingBorderSize = new UiInsets(left, top, right, bottom);
    public void SetCategory(string? category, bool shared = false) => Category = category ?? "";
    public void SetDeletedHandler(object? handler) => _deletedHandler = handler;
    internal void OnDeleted() { if (_deletedHandler != null) Root.FunctionInvoker?.Invoke(_deletedHandler, [this]); }
    public override void SetExtent(float width, float height)
    {
        if (Resizing)
        {
            if (MinResizingExtent.Width > 0) width = Math.Max(width, MinResizingExtent.Width);
            if (MinResizingExtent.Height > 0) height = Math.Max(height, MinResizingExtent.Height);
            if (MaxResizingExtent.Width > 0) width = Math.Min(width, MaxResizingExtent.Width);
            if (MaxResizingExtent.Height > 0) height = Math.Min(height, MaxResizingExtent.Height);
        }
        base.SetExtent(width, height);
    }
    public bool CheckOutOfScreen()
    {
        var r = ScreenRect;
        return r.X < 0 || r.Y < 0 || r.Right > Root.ScreenWidth || r.Bottom > Root.ScreenHeight;
    }
    internal bool BeginResizeDrag(float x, float y)
    {
        if (!Resizing) return false;
        var r = ScreenRect;
        var b = ResizingBorderSize;
        var left = b.Left > 0 ? b.Left : 10;
        var top = b.Top > 0 ? b.Top : 10;
        var right = b.Right > 0 ? b.Right : 10;
        var bottom = b.Bottom > 0 ? b.Bottom : 10;
        _resizeEdges = (x <= r.X + left ? 1 : 0) | (x >= r.Right - right ? 2 : 0)
            | (y <= r.Y + top ? 4 : 0) | (y >= r.Bottom - bottom ? 8 : 0);
        if (_resizeEdges == 0) return false;
        (_resizeX, _resizeY, _resizeWidth, _resizeHeight) = (x, y, r.Width, r.Height);
        return true;
    }
    internal void ResizeDrag(float x, float y)
    {
        var dx = x - _resizeX;
        var dy = y - _resizeY;
        var width = _resizeWidth + ((_resizeEdges & 1) != 0 ? -dx : (_resizeEdges & 2) != 0 ? dx : 0);
        var height = _resizeHeight + ((_resizeEdges & 4) != 0 ? -dy : (_resizeEdges & 8) != 0 ? dy : 0);
        SetExtent(width, height);
    }
    public void SetTitleInset(float left, float top, float right, float bottom) => TitleInset = new UiInsets(left, top, right, bottom);

    /// <summary>Uses the wide dialog preset used by the native X2 dialog helper.</summary>
    public void UseExpandWidth() => SetWidth(450);
}

public class LabelWidget(UiRoot root, string type, string id, Widget? parent) : Widget(root, type, id, parent)
{
    public bool NumberOnly { get; private set; }
    public void SetNumberOnly(bool enabled) => NumberOnly = enabled;
}

/// <summary>Multi-line text: word-wraps by default (SetAutoWordwrap(false) makes it a single growing line).</summary>
public sealed class TextBoxWidget : LabelWidget
{
    internal TextBoxWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) => InitWordwrap(true);

    public bool StrikeThrough { get; private set; }
    public bool UnderLine { get; private set; }
    public void SetStrikeThrough(bool enabled) => StrikeThrough = enabled;
    public void SetUnderLine(bool enabled) => UnderLine = enabled;
}

public class ButtonWidget : LabelWidget
{
    private readonly Dictionary<ButtonState, Drawable> _backgrounds = [];
    private readonly Dictionary<ButtonState, UiColor> _textColors = [];
    private ButtonState? _scriptState;
    private float _minWidth;
    private readonly HashSet<string> _registeredButtons = new(StringComparer.OrdinalIgnoreCase) { "LeftButton" };
    internal bool AcceptsClick(string button) => _registeredButtons.Contains(button);
    public void RegisterForClicks(params object?[] buttons)
    {
        _registeredButtons.Clear();
        foreach (var button in buttons)
            if (button is string name) _registeredButtons.Add(name);
    }
    public void SetAutoClipChar(bool enabled) => SetEllipsis(enabled);
    private bool _startAlphaAnimation;
    public void SetStartAnimation(bool alpha, bool scale = false) => _startAlphaAnimation = alpha;
    public override void Show(bool show = true, double fadeMilliseconds = 0)
        => base.Show(show, show && !Visible && _startAlphaAnimation ? 200 : fadeMilliseconds);

    internal ButtonWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public string? StyleName { get; private set; }
    internal Drawable? CheckedBackground { get; private set; }
    internal Drawable? DisabledCheckedBackground { get; private set; }

    internal ButtonState State => !Enabled ? ButtonState.Disabled : _scriptState ??
        (Pressed && Hovered ? ButtonState.Pushed : Hovered ? ButtonState.Highlighted : ButtonState.Normal);

    internal virtual bool IsCheckedVisual => false;

    /// <summary>Background drawables that belong to another state are hidden while drawing.</summary>
    internal bool IsDrawableActive(Drawable d)
    {
        var state = State;
        if (IsCheckedVisual)
        {
            if (!Enabled && DisabledCheckedBackground != null) return ReferenceEquals(d, DisabledCheckedBackground);
            if (CheckedBackground != null)
                return ReferenceEquals(d, CheckedBackground) || (!_backgrounds.ContainsValue(d) && d != DisabledCheckedBackground);
        }
        if (ReferenceEquals(d, CheckedBackground) || ReferenceEquals(d, DisabledCheckedBackground)) return false;
        if (!_backgrounds.ContainsValue(d)) return true;
        var current = _backgrounds.GetValueOrDefault(state) ?? _backgrounds.GetValueOrDefault(ButtonState.Normal);
        return ReferenceEquals(d, current);
    }

    internal UiColor TextColor
    {
        get
        {
            var state = State;
            if (_textColors.TryGetValue(state, out var c)) return c;
            if (_textColors.TryGetValue(ButtonState.Normal, out var n)) return n;
            return style.Color;
        }
    }

    internal void SetStateDrawable(ButtonState state, Drawable d) => SetBg(state, d);
    public void SetNormalBackground(Drawable? d) => SetBg(ButtonState.Normal, d);
    public void SetHighlightBackground(Drawable? d) => SetBg(ButtonState.Highlighted, d);
    public void SetPushedBackground(Drawable? d) => SetBg(ButtonState.Pushed, d);
    public void SetDisabledBackground(Drawable? d) => SetBg(ButtonState.Disabled, d);
    public void SetCheckedBackground(Drawable? d) => CheckedBackground = d;
    public void SetDisabledCheckedBackground(Drawable? d) => DisabledCheckedBackground = d;

    /// <summary>Native scripted state setter used by login race/gender selectors.</summary>
    public void SetButtonState(string? state)
    {
        _scriptState = state?.ToUpperInvariant() switch
        {
            "PUSHED" => ButtonState.Pushed,
            "HIGHLIGHTED" => ButtonState.Highlighted,
            "DISABLED" => ButtonState.Disabled,
            _ => null,
        };
    }

    private void SetBg(ButtonState state, Drawable? d)
    {
        if (d == null) _backgrounds.Remove(state);
        else _backgrounds[state] = d;
    }

    public void SetTextColor(float r, float g, float b, float a = 1) => _textColors[ButtonState.Normal] = new UiColor(r, g, b, a);
    public void SetHighlightTextColor(float r, float g, float b, float a = 1) => _textColors[ButtonState.Highlighted] = new UiColor(r, g, b, a);
    public void SetPushedTextColor(float r, float g, float b, float a = 1) => _textColors[ButtonState.Pushed] = new UiColor(r, g, b, a);
    public void SetDisabledTextColor(float r, float g, float b, float a = 1) => _textColors[ButtonState.Disabled] = new UiColor(r, g, b, a);

    /// <summary>Applies a ui/setting/button_style.g entry: four state textures, font inset/colour/size and auto-resize.</summary>
    public override void SetStyle(string? name)
    {
        var def = Root.Settings.ButtonStyle(name);
        if (def == null)
        {
            Root.Log($"button style '{name}' not found ({this})");
            return;
        }
        StyleName = name;
        float width = 0, height = 0;
        if (def.TexturePath != null && def.TextureKey != null)
        {
            var states = new[] { ("normal", ButtonState.Normal), ("highlighted", ButtonState.Highlighted), ("pushed", ButtonState.Pushed), ("disabled", ButtonState.Disabled) };
            foreach (var (suffix, state) in states)
            {
                var key = $"{def.TextureKey}_{suffix}";
                var region = Root.Settings.TextureRegion(def.TexturePath, key);
                if (region == null && state == ButtonState.Normal)
                {
                    key = def.TextureKey;
                    region = Root.Settings.TextureRegion(def.TexturePath, key);
                }
                if (region == null) continue;
                var d = CreateDrawable(def.TexturePath, key, "background");
                if (def.TextureColor != null) d.SetTextureColor(def.TextureColor);
                if (def.TextureAnchor != null)
                    d.AddAnchor(def.TextureAnchor.ToUpperInvariant(), this, (def.ButtonAnchor ?? def.TextureAnchor).ToUpperInvariant(), 0.0, 0.0);
                else
                {
                    d.AddAnchor("TOPLEFT", this, 0.0, 0.0);
                    d.AddAnchor("BOTTOMRIGHT", this, 0.0, 0.0);
                }
                SetBg(state, d);
                if (state == ButtonState.Normal)
                {
                    if (region.Extent is { } e) (width, height) = (e.Width, e.Height);
                    else if (region.Coords is { } c) (width, height) = (Math.Abs(c.Width), Math.Abs(c.Height));
                }
            }
        }
        if (def.Height is { } h) height = h;
        if (width > 0 && height > 0) base.SetExtent(width, height);
        _minWidth = width;
        SetInset(def.FontInset.Left, def.FontInset.Top, def.FontInset.Right, def.FontInset.Bottom);
        if (def.FontPath != null) style.SetFont(def.FontPath, 0);
        if (def.FontSize != null) style.SetFontSize(Root.Settings.FontSize(def.FontSize));
        if (def.Align != null) style.SetAlign(def.Align);
        if (def.FontColor != null)
        {
            void Pick(ButtonState state, string suffix)
            {
                if (Root.Settings.FontColor($"{def.FontColor}_{suffix}") is { } c) _textColors[state] = c;
            }
            Pick(ButtonState.Normal, "normal");
            Pick(ButtonState.Highlighted, "highlighted");
            Pick(ButtonState.Pushed, "pushed");
            Pick(ButtonState.Disabled, "disabled");
            if (!_textColors.ContainsKey(ButtonState.Normal) && Root.Settings.FontColor(def.FontColor) is { } one)
                foreach (var s in Enum.GetValues<ButtonState>()) _textColors[s] = one;
        }
        style.SetShadow(false);
        if (def.Ellipsis) SetEllipsis(true);
        SetAutoResize(def.AutoResize);
    }

    public override void SetExtent(float width, float height)
    {
        _minWidth = 0;
        base.SetExtent(width, height);
    }

    internal override void ApplyAutoResize()
    {
        if (WrapsText) { base.ApplyAutoResize(); return; }
        var w = TextLayout.PlainWidth(this, GetText()) + TextInset.Left + TextInset.Right;
        base.SetWidth(Math.Max(_minWidth, (float)Math.Ceiling(w)));
    }

    public void Click() { if (Enabled) InvokeHandler("OnClick", "LeftButton"); }
}

public sealed class CheckButtonWidget : ButtonWidget
{
    internal CheckButtonWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public bool Checked { get; private set; }
    internal override bool IsCheckedVisual => Checked;

    public void SetChecked(bool value, bool fireEvent = true)
    {
        if (Checked == value) return;
        Checked = value;
        if (fireEvent) InvokeHandler("OnCheckChanged", value);
    }

    public bool GetChecked() => Checked;
    public bool IsChecked() => Checked;
}

public class EditBoxWidget : LabelWidget
{
    internal EditBoxWidget(UiRoot root, string type, string id, Widget? parent, bool multiline) : base(root, type, id, parent)
    {
        Multiline = multiline;
        guideTextStyle = new WidgetStyle(root, this);
        EnableFocus(true);
        style.SetAlign(UiAlign.Left);
    }

    public WidgetStyle guideTextStyle { get; }
    public bool Multiline { get; }
    public int MaxTextLength { get; private set; } = 0;
    public bool Password { get; private set; }
    public bool EnglishOnly { get; private set; }
    public bool DigitOnly { get; private set; }
    public bool DigitEmpty { get; private set; }
    public double? DigitMax { get; private set; }
    public string? AutocompleteKind { get; private set; }
    public string? AutocompleteScope { get; private set; }
    public float CursorHeightAdjustment { get; private set; }
    public float CursorOffset { get; private set; }
    public bool ReadOnly { get; private set; }
    public string GuideText { get; private set; } = "";
    public UiInsets GuideTextInset { get; private set; }
    public bool SelectAllWhenFocused { get; private set; }
    public UiColor CursorColor { get; private set; } = new(0.75f, 0.75f, 0.75f, 1);
    internal int Cursor { get; set; }
    internal bool AllSelected { get; set; }
    internal override bool WrapsText => Multiline;
    internal override string LineSpaceKey => "editboxmultiline";

    public void SetMaxTextLength(int length) => MaxTextLength = Math.Max(0, length);
    public int GetMaxTextLength() => MaxTextLength;
    public void SetPassword(bool enabled) => Password = enabled;
    public void SetEnglish(bool enabled) => EnglishOnly = enabled;
    public void SetDigit(bool enabled) => DigitOnly = enabled;
    public void SetDigitEmpty(bool enabled) => DigitEmpty = enabled;
    public void SetDigitMax(double value) => DigitMax = value >= 0 && double.IsFinite(value) ? value : null;
    public void SetInitVal(object? value)
    {
        InitialValue = value?.ToString() ?? "";
        if (GetText().Length == 0) SetText(InitialValue, false);
    }
    public string InitialValue { get; private set; } = "";
    internal void NormalizeOnFocusLost()
    {
        if (DigitOnly && !DigitEmpty && GetText().Length == 0 && InitialValue.Length > 0)
            SetText(InitialValue, false);
    }
    public void SetCursorHeight(double adjustment) => CursorHeightAdjustment = (float)adjustment;
    public void SetCursorOffset(double offset) => CursorOffset = (float)offset;
    public bool IsNowComposition() => false;
    public void SetAutocomplete(string? kind, string? scope = null) { AutocompleteKind = kind; AutocompleteScope = scope; }
    public void SetReadOnly(bool enabled) => ReadOnly = enabled;
    public void SetGuideText(string? text) => GuideText = text ?? "";
    public string GetGuideText() => GuideText;
    public void UseSelectAllWhenFocused(bool enabled) => SelectAllWhenFocused = enabled;
    public void SetCursorColorByColorKey(string key) { if (Root.Settings.EtcColor(key) is { } c) CursorColor = c; }
    public void SetCursorColor(float r, float g, float b, float a = 1) => CursorColor = new UiColor(r, g, b, a);
    public int GetCursorPosition() => Cursor;
    public void SetCursorPosition(int position) => Cursor = Math.Clamp(position, 0, GetText().Length);
    public void SelectAll() => AllSelected = true;

    /// <summary>SetGuideTextInset({l, t, r, b}) or SetGuideTextInset(l, t, r, b).</summary>
    public void SetGuideTextInset(object? a, double t = 0, double r = 0, double b = 0)
    {
        if (a is double l) GuideTextInset = new UiInsets((float)l, (float)t, (float)r, (float)b);
        else if (a is IDictionary<object, object?> table)
        {
            float V(int i) => table.TryGetValue((double)i, out var v) && v is double d ? (float)d : 0;
            GuideTextInset = new UiInsets(V(1), V(2), V(3), V(4));
        }
    }

    public override void SetText(string? text)
    {
        var value = text ?? "";
        if (MaxTextLength > 0 && value.Length > MaxTextLength) value = value[..MaxTextLength];
        if (DigitMax is { } max && double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && number > max)
            value = max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Cursor = value.Length;
        AllSelected = false;
        base.SetText(value);
    }

    internal string DisplayText => Password ? new string('*', GetText().Length) : GetText();

    /// <summary>Keyboard entry (from the input router).</summary>
    internal void InsertText(string input)
    {
        if (ReadOnly || !Enabled) return;
        var text = GetText();
        if (AllSelected) { text = ""; Cursor = 0; AllSelected = false; }
        foreach (var ch in input)
        {
            if (char.IsControl(ch)) continue;
            if (EnglishOnly && ch > 0x7E) continue;
            if (DigitOnly && !char.IsDigit(ch)) continue;
            if (MaxTextLength > 0 && text.Length >= MaxTextLength) break;
            Cursor = Math.Clamp(Cursor, 0, text.Length);
            var candidate = text.Insert(Cursor, ch.ToString());
            if (DigitMax is { } max && double.TryParse(candidate, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) && number > max) continue;
            text = candidate;
            Cursor++;
        }
        var cursor = Cursor;
        base.SetText(text);
        Cursor = Math.Min(cursor, text.Length);
    }

    internal void Backspace(bool forward)
    {
        if (ReadOnly || !Enabled) return;
        var text = GetText();
        if (AllSelected) { AllSelected = false; Cursor = 0; base.SetText(""); return; }
        Cursor = Math.Clamp(Cursor, 0, text.Length);
        if (forward ? Cursor >= text.Length : Cursor == 0) return;
        var at = forward ? Cursor : Cursor - 1;
        text = text.Remove(at, 1);
        var cursor = at;
        base.SetText(text);
        Cursor = cursor;
    }

    internal void MoveCursor(int delta, bool toEnd = false, bool toStart = false)
    {
        AllSelected = false;
        var len = GetText().Length;
        Cursor = toEnd ? len : toStart ? 0 : Math.Clamp(Cursor + delta, 0, len);
    }
}

public sealed class SliderWidget : Widget
{
    internal SliderWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public float Min { get; private set; }
    public float Max { get; private set; } = 1;
    public float Value { get; private set; }
    public float Step { get; private set; } = 1;
    public float PageStep { get; private set; } = 1;
    public bool Vertical { get; private set; } = true;
    public Widget? Thumb { get; private set; }
    public bool FixedThumb { get; private set; }
    public float MinThumbLength { get; private set; }
    public void SetFixedThumb(bool fixedThumb) { FixedThumb = fixedThumb; PositionThumb(); }
    public void SetMinThumbLength(float length) { MinThumbLength = Math.Max(0, length); PositionThumb(); }
    internal bool NativeInteraction { get; private set; }
    internal void EnableNativeInteraction() { NativeInteraction = true; PositionThumb(); }

    public void SetMinMaxValues(float min, float max) { Min = min; Max = Math.Max(min, max); SetValue(Value, false); PositionThumb(); }
    public LuaMulti GetMinMaxValues() => new(Min, Max);
    public void SetValue(float value, bool fireEvent = true)
    {
        var v = Math.Clamp(value, Min, Max);
        if (Math.Abs(v - Value) < 1e-6) return;
        Value = v;
        PositionThumb();
        if (fireEvent) InvokeHandler("OnSliderChanged", v);
    }
    public float GetValue() => Value;
    public void Up(float amount = 1) => SetValue(Value - amount);
    public void Down(float amount = 1) => SetValue(Value + amount);
    public void SetValueStep(float step) => Step = step;
    public void SetPageStep(float step) => PageStep = step;
    /// <summary>X2 orientation: 0 (or "VERTICAL") is vertical, 1 (or "HORIZONTAL") horizontal (baselib/slider.lua passes 1, the
    /// palette's luminance bar 0); scroll bars never set it and are vertical.</summary>
    public void SetOrientation(object? orientation) => Vertical = orientation switch
    {
        double d => d == 0,
        string s => !s.Equals("HORIZONTAL", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };
    public Widget? GetThumbButtonWidget() => Thumb;
    public void SetThumbButtonWidget(Widget? thumb) => AttachThumb(thumb);
    public void SetThumbButton(Widget? thumb) => AttachThumb(thumb);

    /// <summary>
    /// A script slider that owns a thumb behaves like the client's native slider: it places the thumb from its value and follows
    /// the pointer (scripts never move the thumb themselves). Unlike the chat log's slider, its minimum is at the top.
    /// </summary>
    internal bool ScriptSlider { get; private set; }
    private void AttachThumb(Widget? thumb)
    {
        Thumb = thumb;
        if (thumb != null && !NativeInteraction)
        {
            NativeInteraction = ScriptSlider = true;
            Root.TickSetChanged();
        }
        _placed = default;
        PositionThumb();
    }

    private (float W, float H, float Fraction, float Thumb) _placed;

    /// <summary>Per frame: re-places the thumb when the track was laid out anew (anchors resolve after the value is set).</summary>
    internal void Tick()
    {
        if (Thumb == null || !IsEffectivelyVisible()) return;
        var r = ScreenRect;
        if (r.Width != _placed.W || r.Height != _placed.H || Fraction() != _placed.Fraction ||
            (Vertical ? Thumb.GetHeight() : Thumb.GetWidth()) != _placed.Thumb)
            PositionThumb();
    }

    private float Fraction() => Max > Min ? (Value - Min) / (Max - Min) : 0;

    /// <summary>The value under a pointer position (script sliders: the thumb's centre follows the pointer).</summary>
    internal float ValueAt(float x, float y)
    {
        var r = ScreenRect;
        float fraction;
        if (ScriptSlider && Thumb != null)
        {
            var thumb = Vertical ? Thumb.GetHeight() : Thumb.GetWidth();
            var travel = Math.Max(1, (Vertical ? r.Height : r.Width) - thumb);
            fraction = Math.Clamp(((Vertical ? y - r.Y : x - r.X) - thumb / 2) / travel, 0, 1);
        }
        else
            fraction = Vertical ? 1f - Math.Clamp((y - r.Y) / Math.Max(1, r.Height), 0, 1) : Math.Clamp((x - r.X) / Math.Max(1, r.Width), 0, 1);
        return Min + fraction * (Max - Min);
    }

    private void PositionThumb()
    {
        if (!NativeInteraction || Thumb == null || (Max <= Min && !ScriptSlider)) return;
        if (!FixedThumb && MinThumbLength > 0)
        {
            if (Vertical && Thumb.NaturalSize.H < MinThumbLength) Thumb.SetHeight(MinThumbLength);
            if (!Vertical && Thumb.NaturalSize.W < MinThumbLength) Thumb.SetWidth(MinThumbLength);
        }
        var fraction = Fraction();
        _placed = (ScreenRect.Width, ScreenRect.Height, fraction, Vertical ? Thumb.GetHeight() : Thumb.GetWidth());
        if (Vertical && ScriptSlider)
        {
            var travel = Math.Max(0, GetHeight() - Thumb.GetHeight());
            Thumb.RemoveAllAnchors();
            Thumb.AddAnchor("TOP", this, 0.0, fraction * travel);
        }
        else if (Vertical)
        {
            var travel = Math.Max(0, GetHeight() - Thumb.GetHeight());
            Thumb.RemoveAllAnchors();
            Thumb.AddAnchor("LEFT", this, 5.0, (0.5 - fraction) * travel);
        }
        else
        {
            var travel = Math.Max(0, GetWidth() - Thumb.GetWidth());
            Thumb.RemoveAllAnchors();
            Thumb.AddAnchor("TOP", this, (fraction - 0.5) * travel, 0.0);
        }
    }
}

/// <summary>Tooltip widget: lines of text added by AddLine.</summary>
public class GameTooltipWidget : Widget
{
    internal readonly record struct TooltipLine(string Text, float Size, UiAlign Align, float Indent,
        float UpperSpace = 0, float LowerSpace = 0);

    private readonly List<TooltipLine> _lines = [];
    private readonly Dictionary<int, (string Text, float Size, float Indent)> _sideLines = [];

    internal GameTooltipWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent)
        => style.SetAlign(UiAlign.Left);

    internal IReadOnlyList<TooltipLine> TooltipLines => _lines;
    internal IReadOnlyDictionary<int, (string Text, float Size, float Indent)> TooltipSideLines => _sideLines;

    public virtual void ClearLines() { _lines.Clear(); _sideLines.Clear(); SetTextSilently(""); FitToLines(); }

    public virtual int AddLine(string? text, object? font = null, double size = 0, object? align1 = null, object? align2 = null, double indent = 0)
    {
        _lines.Add(new TooltipLine(text ?? "", size > 0 ? (float)size : style.FontSize,
            WidgetStyle.ParseAlign(align2 ?? align1, UiAlign.Left), (float)indent));
        SetTextSilently(string.Join("\n", _lines.Select(l => l.Text)));
        FitToLines();
        return _lines.Count;
    }

    public virtual void AddAnotherSideLine(double index, string? text, object? font = null, double size = 0, object? align = null, double indent = 0)
    {
        var row = (int)index;
        if (row < 1 || row > _lines.Count) return;
        _sideLines[row] = (text ?? "", size > 0 ? (float)size : style.FontSize, (float)indent);
        FitToLines();
    }

    public void AttachUpperSpaceLine(double index, double space)
    {
        var row = (int)index - 1;
        if (row < 0 || row >= _lines.Count || space <= 0) return;
        _lines[row] = _lines[row] with { UpperSpace = _lines[row].UpperSpace + (float)space };
        FitToLines();
    }

    public void AttachLowerSpaceLine(double index, double space)
    {
        var row = (int)index - 1;
        if (row < 0 || row >= _lines.Count || space <= 0) return;
        // The native method accumulates spacing. Some tooltip sections intentionally call it twice.
        _lines[row] = _lines[row] with { LowerSpace = _lines[row].LowerSpace + (float)space };
        FitToLines();
    }

    public new int GetLineCount() => _lines.Count;
    // The client API returns both the last line height and height including attached spacing.
    // Tooltip Lua uses the second result for section layout.
    public LuaMulti GetHeightToLastLine()
    {
        var heights = TooltipHeights();
        return new LuaMulti(heights.Line, heights.WithSpace);
    }

    internal override bool WrapsText => AutoWordwrap;
    internal override string LineSpaceKey => "gametooltip";

    private void FitToLines()
    {
        var contentWidth = _lines.Select((line, index) =>
        {
            var left = LineWidth(line.Text, line.Size) + Math.Max(0, line.Indent);
            if (!_sideLines.TryGetValue(index + 1, out var side)) return left;
            return left + 8 + LineWidth(side.Text, side.Size) + Math.Max(0, side.Indent);
        }).DefaultIfEmpty(0).Max();
        var insetWidth = TextInset.Left + TextInset.Right;
        var w = AutoWordwrap ? Math.Max(NaturalSize.W, contentWidth + insetWidth) : contentWidth + insetWidth;
        var h = TooltipHeights().WithSpace + TextInset.Top + TextInset.Bottom;
        base.SetExtent(Math.Max(w, 1), Math.Max(h, 1));
    }

    private (float Line, float WithSpace) TooltipHeights()
    {
        var lineHeight = 0f;
        var withSpace = 0f;
        for (var i = 0; i < _lines.Count; i++)
        {
            var height = TooltipLineHeight(i);
            lineHeight += height;
            withSpace += _lines[i].UpperSpace + height + _lines[i].LowerSpace;
        }
        return (lineHeight, withSpace);
    }

    internal float TooltipLineHeight(int index)
    {
        if (index < 0 || index >= _lines.Count) return 0;
        var line = _lines[index];
        var width = AutoWordwrap
            ? Math.Max(1, InnerWidth() - Math.Max(0, line.Indent))
            : float.MaxValue;
        var rows = TextLayout.Lines(Root, style.FontKey, line.Size, line.Text, width);
        var count = Math.Max(1, rows.Count);
        return count * Root.Measurer.LineHeight(style.FontKey, line.Size)
            + (count - 1) * EffectiveLineSpace;
    }

    private float LineWidth(string text, float size)
    {
        var lines = TextLayout.Lines(Root, style.FontKey, size, text, float.MaxValue);
        return lines.Count == 0 ? 0 : lines.Max(line => line.Width);
    }
}
