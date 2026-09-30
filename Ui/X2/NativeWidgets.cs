#nullable enable
using System.Globalization;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>A plain widget that exposes extra style objects as fields (damagedisplay.extraStyle, ...).</summary>
public sealed class StyledWidget : Widget
{
    internal StyledWidget(UiRoot root, string type, string id, Widget? parent, params string[] styles) : base(root, type, id, parent)
    {
        foreach (var s in styles) NativeFields[s] = new WidgetStyle(root);
    }
}

/// <summary>
/// "statusbar": a gauge. The bar is a region of a texture (SetBarTexture + SetBarTextureByKey or coords), tinted by
/// SetBarColor(ByKey), cropped to value/(max-min) along the orientation.
/// </summary>
public sealed class StatusBarWidget : Widget
{
    private readonly List<(LayoutElement Child, string Point, string Relative, double X, double Y)> _barChildren = [];
    internal StatusBarWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public double Min { get; private set; }
    public double Max { get; private set; } = 1;
    public double Value { get; private set; }
    public string? BarTexture { get; private set; }
    public string BarLayer { get; private set; } = "artwork";
    public string? BarKey { get; private set; }
    public UiRect? BarCoords { get; private set; }
    public UiColor BarColor { get; private set; } = UiColor.White;
    public bool Vertical { get; private set; }
    public bool Reversed { get; private set; }

    internal float Fraction => Max > Min ? (float)Math.Clamp((Value - Min) / (Max - Min), 0, 1) : 0;

    public void SetMinMaxValues(double min, double max) { Min = min; Max = Math.Max(min, max); UpdateBarChildren(); }
    public void SetMinMaxValuesForString(string? min, string? max) => SetMinMaxValues(Parse(min), Parse(max));
    public LuaMulti GetMinMaxValues() => new(Min, Max);
    public void SetValue(double value, object? unused = null) { Value = value; UpdateBarChildren(); }
    public void SetValueForString(string? value) => SetValue(Parse(value));
    public double GetValue() => Value;
    public void SetBarTexture(string? path, string? layer = null) { BarTexture = path; if (!string.IsNullOrEmpty(layer)) BarLayer = layer; }
    public void SetBarTextureByKey(string? key) => BarKey = key;
    public void SetBarTextureCoords(float x, float y, float w, float h) => BarCoords = new UiRect(x, y, w, h);
    public void SetBarColor(float r, float g, float b, float a = 1) => BarColor = new UiColor(r, g, b, a);

    public void SetBarColorByKey(string key)
    {
        var region = Root.Settings.TextureRegion(BarTexture, BarKey);
        if (region != null && region.Colors.TryGetValue(key, out var c)) BarColor = c;
        else if (Root.Settings.EtcColor(key) is { } etc) BarColor = etc;
        else if (Root.Settings.FontColor(key) is { } font) BarColor = font;
    }

    public void SetOrientation(object? orientation)
    {
        var o = orientation?.ToString()?.ToUpperInvariant() ?? "";
        Vertical = o.StartsWith("VERT", StringComparison.Ordinal) || o == "1";
    }

    public void SetReverseDirection(bool reverse) => Reversed = reverse;

    public double GetLeftWidth(double width) => Math.Max(0, width) * Fraction;

    public bool IsChangeAfterImageColor(string? value) => Parse(value) < Value;

    public void AddAnchorChildToBar(LayoutElement child, string point, string relative, double x = 0, double y = 0)
    {
        _barChildren.RemoveAll(a => ReferenceEquals(a.Child, child) && a.Point == point);
        _barChildren.Add((child, point, relative, x, y));
        UpdateBarChildren();
    }

    private void UpdateBarChildren()
    {
        var width = GetWidth();
        var height = GetHeight();
        foreach (var (child, point, relative, x, y) in _barChildren)
        {
            var anchor = LayoutElement.ParsePoint(relative);
            var right = anchor is AnchorPoint.TopRight or AnchorPoint.Right or AnchorPoint.BottomRight;
            var middle = anchor is AnchorPoint.Top or AnchorPoint.Center or AnchorPoint.Bottom;
            var bottom = anchor is AnchorPoint.BottomLeft or AnchorPoint.Bottom or AnchorPoint.BottomRight;
            var centerY = anchor is AnchorPoint.Left or AnchorPoint.Center or AnchorPoint.Right;
            child.AddAnchor(point, this, "TOPLEFT", (double)(x + (right ? width * Fraction : middle ? width * Fraction / 2 : 0)),
                y + (bottom ? height : centerY ? height / 2 : 0));
        }
    }

    private static double Parse(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// "slot" (item/skill/action slot) and "cooldownbutton": a button with an icon, a stack count, a cooldown sweep and a
/// cooldown text style (slot.cooltime_style). The X2 layer fills it through Establish*/SetIcon calls.
/// </summary>
public class SlotWidget : ButtonWidget
{
    internal SlotWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent)
    {
        NativeFields["cooltime_style"] = CooltimeStyle = new WidgetStyle(root, this);
        style.SetAlign(UiAlign.BottomRight);
    }

    public WidgetStyle CooltimeStyle { get; }
    /// <summary>Icon texture path (ui/icon/...).</summary>
    public string? IconPath { get; private set; }
    public int Count { get; private set; }
    public double CooldownStart { get; private set; }
    public double CooldownDuration { get; private set; }
    public double CooldownRemaining { get; private set; }
    public string SlotType { get; private set; } = "";
    public double SlotIndex { get; private set; }
    public string? ContentKind { get; private set; }
    public object?[] ContentArgs { get; private set; } = [];
    public bool Grayed { get; private set; }
    private int _skillType;
    private int _passiveBuffType;

    public int? GetSkillType() => _skillType > 0 ? _skillType : null;
    public int? GetPassiveBuffType() => _passiveBuffType > 0 ? _passiveBuffType : null;
    internal void SetAbilityViewContent(int skillType, int passiveBuffType)
    {
        _skillType = skillType;
        _passiveBuffType = passiveBuffType;
    }

    public void SetIcon(string? path) => IconPath = string.IsNullOrEmpty(path) ? null : path;
    public void SetIconPath(string? path) => SetIcon(path);
    public void SetStack(double count) => Count = (int)count;
    public void SetCount(double count) => Count = (int)count;
    public void SetGray(bool gray) => Grayed = gray;

    public void EstablishSlot(object? slotType, double slotIndex)
    {
        SlotType = slotType?.ToString() ?? "";
        SlotIndex = slotIndex;
        _skillType = 0;
        _passiveBuffType = 0;
        Root.SlotEstablished?.Invoke(this);
    }

    public void EstablishVirtualSlot(object? slotType, double slotIndex) => EstablishSlot(slotType, slotIndex);
    public void EstablishItem(params object?[] args) { ContentKind = "item"; ContentArgs = args; Root.SlotContent?.Invoke(this, "item", args); }
    public void EstablishSkill(params object?[] args) { ContentKind = "skill"; ContentArgs = args; EnableDrag(true); Root.SlotContent?.Invoke(this, "skill", args); }
    public void EstablishSkillSlot(params object?[] args) { ContentKind = "skillslot"; ContentArgs = args; EnableDrag(true); Root.SlotContent?.Invoke(this, "skillslot", args); }
    public void ReleaseSlot() { IconPath = null; Count = 0; CooldownDuration = 0; SlotType = ""; ContentKind = null; ContentArgs = []; }
    public void ResetState() { }

    /// <summary>Starts a cooldown sweep (milliseconds).</summary>
    public void SetCooldown(double remainingMs, double totalMs)
    {
        CooldownDuration = Math.Max(totalMs, remainingMs);
        CooldownRemaining = remainingMs;
    }

    internal void Tick(double ms)
    {
        if (CooldownRemaining > 0) CooldownRemaining = Math.Max(0, CooldownRemaining - ms);
    }

    internal float CooldownFraction => CooldownDuration > 0 ? (float)(CooldownRemaining / CooldownDuration) : 0;
}

/// <summary>"listbox": a list of text items (the combobox dropdown uses it too).</summary>
public class ListBoxWidget : Widget
{
    private readonly List<(string Text, object? Value)> _items = [];
    private readonly List<string> _subTexts = [];
    private readonly List<bool> _itemEnabled = [];
    private readonly List<string?> _tailIconPaths = [];
    private readonly List<string?> _tailIconCoords = [];
    private readonly List<(ButtonWidget Button, int Depth)> _treeRows = [];
    private readonly HashSet<int> _parentIndices = [];
    internal UiColor? DefaultItemTextColor, OveredItemTextColor, SelectedItemTextColor, DisableItemTextColor;
    internal UiColor? OveredItemColor, SelectedItemColor;
    internal string? ItemStateTexturePath;
    internal UiInsets ItemStateTextureInset;
    internal (string Key, string? Color)? DefaultItemTexture, OveredItemTexture, SelectedItemTexture;
    internal UiRect? DefaultItemCoord;
    internal bool SelectionToggle, SelectParent = true, OverParent = true, ChildStyle;
    internal bool TreeIndentEnabled;
    internal float TreeIndent = 14, TreeParentIndent = 5;
    internal float SubTextX, SubTextY, ChildSubTextX, ChildSubTextY;
    internal int TextLimit;

    internal ListBoxWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent)
        { NativeFields["itemStyle"] = new WidgetStyle(root, this); NativeFields["childStyle"] = new WidgetStyle(root, this); NativeFields["itemStyleSub"] = new WidgetStyle(root, this); NativeFields["childStyleSub"] = new WidgetStyle(root, this); } // childStyle: tree-style rows (UseChildStyle)

    /// <summary>Row tooltips for truncated entries (drawn by the host when a row does not fit).</summary>
    public bool RowTooltips { get; private set; }
    public void ShowTooltip(bool show) => RowTooltips = show;

    /// <summary>Tree lists: the expand/collapse images drawn before parent rows.</summary>
    public TextureDrawable? OpenedImage { get; private set; }
    public TextureDrawable? ClosedImage { get; private set; }
    /// <summary>Tree lists: the separator line drawn between groups.</summary>
    public TextureDrawable? SeparatorImage { get; private set; }
    public TextureDrawable CreateSeparatorImageDrawable(string texturePath, string layer = "background")
        => SeparatorImage = new TextureDrawable(Root, this, DrawableKind.Image, texturePath, null, layer);
    public TextureDrawable CreateOpenedImageDrawable(string texturePath) => OpenedImage = new TextureDrawable(Root, this, DrawableKind.Image, texturePath, null, "artwork");
    public TextureDrawable CreateClosedImageDrawable(string texturePath) => ClosedImage = new TextureDrawable(Root, this, DrawableKind.Image, texturePath, null, "artwork");

    internal IReadOnlyList<(string Text, object? Value)> Items => _items;
    public int SelectedIndex { get; private set; }
    internal int HoveredIndex { get; private set; }
    public int Top { get; private set; }

    public void AppendItem(string? text, object? value = null)
    { _items.Add((text ?? "", value)); _subTexts.Add(""); _itemEnabled.Add(true); _tailIconPaths.Add(null); _tailIconCoords.Add(null); }
    public void AppendItemByTable(IDictionary<object, object?>? data)
    {
        if (data == null) return;
        AppendItem(data.TryGetValue("text", out var text) ? text?.ToString() : "",
            data.TryGetValue("value", out var value) ? value : null);
        if (data.TryGetValue("subtext", out var sub)) _subTexts[^1] = sub?.ToString() ?? "";
        if (data.TryGetValue("enable", out var enabled) && enabled is bool valueEnabled) _itemEnabled[^1] = valueEnabled;
        if (data.TryGetValue("tailIconPath", out var tail)) _tailIconPaths[^1] = tail?.ToString();
        if (data.TryGetValue("tailIconCoord", out var coord)) _tailIconCoords[^1] = coord?.ToString();
    }
    public void AppendItems(IDictionary<object, object?>? datas, object? unused = null)
    {
        if (datas == null) return;
        foreach (var k in datas.Keys.OfType<double>().OrderBy(k => k))
            if (datas[k] is IDictionary<object, object?> d)
                AppendItem(d.TryGetValue("text", out var t) ? t?.ToString() : "", d.TryGetValue("value", out var v) ? v : null);
    }
    /// <summary>
    /// Tree lists (SetItemTrees({ {text=, value=, child={...}}, ... })): the entries are kept in display order, children after
    /// their parent. Collapsing/expanding and per-item colours are not modelled yet.
    /// </summary>
    /// <summary>GetViewItemsInfo(): the listed entries as { {text=, value=}, ... }.</summary>
    public AAEmu.GodotViewer.Lua.LuaTable GetViewItemsInfo()
    {
        var t = new AAEmu.GodotViewer.Lua.LuaTable();
        for (var i = 0; i < _items.Count; i++)
            t[(double)(i + 1)] = new AAEmu.GodotViewer.Lua.LuaTable { ["text"] = _items[i].Text, ["value"] = _items[i].Value, ["indexing"] = (double)(i + 1) };
        return t;
    }

    /// <summary>UpdateItem({ indexing = n, text =, ... }): changes the listed entry n (colours/sub text are not modelled yet).</summary>
    public void UpdateItem(IDictionary<object, object?>? datas)
    {
        if (datas == null || !datas.TryGetValue("indexing", out var ix) || ix is not double d) return;
        var i = (int)d - 1;
        if (i < 0 || i >= _items.Count) return;
        if (datas.TryGetValue("text", out var text) && text != null)
        {
            _items[i] = (text.ToString() ?? "", _items[i].Value);
            if (i < _treeRows.Count) _treeRows[i].Button.SetText(_items[i].Text);
        }
        if (datas.TryGetValue("subtext", out var sub)) _subTexts[i] = sub?.ToString() ?? "";
        if (datas.TryGetValue("enable", out var enabled) && enabled is bool valueEnabled) _itemEnabled[i] = valueEnabled;
    }

    public object? SetItemTrees(IDictionary<object, object?>? tables)
    {
        ClearItem();
        void Add(IDictionary<object, object?> list, int depth)
        {
            for (var i = 1; list.TryGetValue((double)i, out var o); i++)
            {
                if (o is not IDictionary<object, object?> d) continue;
                AppendItem(d.TryGetValue("text", out var t) ? t?.ToString() : "", d.TryGetValue("value", out var v) ? v : 0d);
                if (d.TryGetValue("subtext", out var sub)) _subTexts[^1] = sub?.ToString() ?? "";
                var index = _items.Count;
                var row = (ButtonWidget)Root.CreateWidget("button", $"treeItem[{index}]", this);
                row.SetText(_items[index - 1].Text);
                row.SetHeight(22);
                row.SetHandler("OnClick", new NativeHandler((_, _) =>
                {
                    if (SelectParent || !_parentIndices.Contains(index)) Select(index);
                    return true;
                }));
                _treeRows.Add((row, depth));
                if (d.TryGetValue("child", out var c) && c is IDictionary<object, object?> children)
                { _parentIndices.Add(index); Add(children, depth + 1); }
            }
        }
        if (tables != null) Add(tables, 0);
        RefreshTreeRows();
        return new AAEmu.GodotViewer.Lua.LuaTable
        {
            ["itemInfos"] = new AAEmu.GodotViewer.Lua.LuaTable(),
        };
    }

    private int VisibleTreeRowCount => Math.Max(1,
        (int)Math.Floor(Math.Max(264, Math.Max(Height, Parent?.Height ?? 0)) / 22.0));

    private void RefreshTreeRows()
    {
        // The scroll-list frame receives its size before the anchored content does. SetItemTrees can
        // run in that interval, so use the frame's height to expose the rows that fit on screen.
        var visibleCount = VisibleTreeRowCount;
        for (var i = 0; i < _treeRows.Count; i++)
        {
            var (row, depth) = _treeRows[i];
            var visible = i >= Top && i < Top + visibleCount;
            row.Show(visible);
            if (!visible) continue;
            var y = (i - Top) * 22.0;
            row.RemoveAllAnchors();
            var indent = TreeIndentEnabled ? TreeParentIndent + depth * TreeIndent : 5.0 + depth * 14.0;
            row.AddAnchor("TOPLEFT", this, indent, y);
            row.SetExtent((float)Math.Max(100, Math.Max(Width, (Parent?.Width ?? 0) - 20) - 10 - indent), 22);
            var color = SelectedIndex == i + 1 ? SelectedItemTextColor : DefaultItemTextColor;
            if (color is { } c) row.SetTextColor(c.R, c.G, c.B, c.A);
            if (OveredItemTextColor is { } over) row.SetHighlightTextColor(over.R, over.G, over.B, over.A);
        }
    }

    public void ClearItem()
    {
        foreach (var (row, _) in _treeRows) Root.Destroy(row);
        _treeRows.Clear();
        _parentIndices.Clear();
        _items.Clear();
        _subTexts.Clear();
        _itemEnabled.Clear();
        _tailIconPaths.Clear();
        _tailIconCoords.Clear();
        SelectedIndex = 0;
        HoveredIndex = 0;
        Top = 0;
    }
    public void ClearItems() => ClearItem();
    public int GetItemCount() => _items.Count;
    internal string SubTextAt(int index) => index >= 0 && index < _subTexts.Count ? _subTexts[index] : "";
    internal bool ItemEnabledAt(int index) => index >= 0 && index < _itemEnabled.Count && _itemEnabled[index];
    internal string? TailIconAt(int index) => index >= 0 && index < _tailIconPaths.Count ? _tailIconPaths[index] : null;
    internal string? TailIconCoordAt(int index) => index >= 0 && index < _tailIconCoords.Count ? _tailIconCoords[index] : null;

    /// <summary>1-based index of the item created with this value (numbers compare by value), or 0.</summary>
    public int GetIndexByValue(object? value)
    {
        for (var k = 0; k < _items.Count; k++)
        {
            var v = _items[k].Value;
            if (Equals(v, value) || (v is IConvertible && value is IConvertible && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var a) && double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var b) && Math.Abs(a - b) < 1e-9))
                return k + 1;
        }
        return 0;
    }

    public object? GetValueByIndex(double index) => (int)index >= 1 && (int)index <= _items.Count ? _items[(int)index - 1].Value : null;
    public void Select(double index, bool notify = true)
    {
        var next = Math.Clamp((int)index, 0, _items.Count);
        SelectedIndex = SelectionToggle && next == SelectedIndex ? 0 : next;
        RefreshTreeRows();
        if (notify) InvokeHandler("OnSelChanged", (double)SelectedIndex);
    }
    internal void SelectAt(float screenY)
    {
        if (_treeRows.Count != 0) return; // Tree entries receive clicks through their child buttons.
        var row = (int)Math.Floor((screenY - ScreenRect.Y) / 22f);
        var index = Top + row + 1;
        if (row >= 0 && index <= _items.Count && ItemEnabledAt(index - 1)) Select(index);
    }
    internal void HoverAt(float screenY)
    {
        var row = (int)Math.Floor((screenY - ScreenRect.Y) / 22f);
        var index = Top + row + 1;
        HoveredIndex = row >= 0 && index <= _items.Count ? index : 0;
    }
    public void InitializeSelect(double index) { SelectedIndex = Math.Clamp((int)index, 0, _items.Count); RefreshTreeRows(); }
    /// <summary>SelectWithText(text, fireEvent): selects the first entry showing that text (the assignment window opens its
    /// category list on the summary/completed page this way).</summary>
    public bool SelectWithText(string? text, bool fireEvent = true)
    {
        var i = _items.FindIndex(x => x.Text == text);
        if (i < 0) return false;
        Select(i + 1, fireEvent);
        return true;
    }
    /// <summary>SelectWithValue(value, fireEvent): selects the first entry created with that value.</summary>
    public bool SelectWithValue(object? value, bool fireEvent = true)
    {
        var i = GetIndexByValue(value);
        if (i <= 0) return false;
        Select(i, fireEvent);
        return true;
    }
    /// <summary>TurnoffOverParent(bool): tree rows do not highlight their parent on hover; hover highlight is not modelled.</summary>
    public void TurnoffOverParent(bool off = true) => OverParent = !off;
    public void EnableSelectParent(bool enable) => SelectParent = enable;
    public void EnableSelectionToggle(bool enable) => SelectionToggle = enable;
    public void UseChildStyle(bool enable) => ChildStyle = enable;
    public void SetTreeTypeIndent(bool enable, double indent, double parentIndent)
    { TreeIndentEnabled = enable; TreeIndent = (float)indent; TreeParentIndent = (float)parentIndent; RefreshTreeRows(); }
    public void SetSubTextOffset(double x, double y, bool child = false)
    { if (child) { ChildSubTextX = (float)x; ChildSubTextY = (float)y; } else { SubTextX = (float)x; SubTextY = (float)y; } }
    public void SetTextLimit(double limit) => TextLimit = Math.Max(0, (int)limit);
    public void ShowAutoTooltip(bool show) => RowTooltips = show;
    public void SetDefaultItemCoord(double x, double y, double width, double height)
        => DefaultItemCoord = new UiRect((float)x, (float)y, (float)width, (float)height);
    public void SetListItemStateTexture(string? path) => ItemStateTexturePath = path;
    public void SetListItemStateTextureInset(double left, double top, double right, double bottom)
        => ItemStateTextureInset = new UiInsets((float)left, (float)top, (float)right, (float)bottom);
    public void SetItemDefaultTextureInfo(string key, string? color = null) => DefaultItemTexture = (key, color);
    public void SetItemOveredTextureInfo(string key, string? color = null) => OveredItemTexture = (key, color);
    public void SetItemSelectedTextureInfo(string key, string? color = null) => SelectedItemTexture = (key, color);
    public void SetOveredItemColor(double r, double g, double b, double a = 1) => OveredItemColor = new((float)r, (float)g, (float)b, (float)a);
    public void SetSelectedItemColor(double r, double g, double b, double a = 1) => SelectedItemColor = new((float)r, (float)g, (float)b, (float)a);
    public void SetDefaultItemTextColor(double r, double g, double b, double a = 1) => DefaultItemTextColor = new((float)r, (float)g, (float)b, (float)a);
    public void SetOveredItemTextColor(double r, double g, double b, double a = 1) => OveredItemTextColor = new((float)r, (float)g, (float)b, (float)a);
    public void SetSelectedItemTextColor(double r, double g, double b, double a = 1) => SelectedItemTextColor = new((float)r, (float)g, (float)b, (float)a);
    public void SetDisableItemTextColor(double r, double g, double b, double a = 1) => DisableItemTextColor = new((float)r, (float)g, (float)b, (float)a);
    public void ClearAllSelected() => SelectedIndex = 0;
    public int GetSelectedIndex() => SelectedIndex;
    public string GetSelectedText() => SelectedIndex >= 1 && SelectedIndex <= _items.Count ? _items[SelectedIndex - 1].Text : "";
    public object? GetSelectedValue() => SelectedIndex >= 1 && SelectedIndex <= _items.Count ? _items[SelectedIndex - 1].Value : null;
    public void SetTop(double top) { Top = Math.Clamp((int)top, 0, GetMaxTop()); RefreshTreeRows(); }
    public int GetMaxTop() => Math.Max(0, _items.Count - (_treeRows.Count > 0 ? VisibleTreeRowCount : 1));
    public int GetTop() => Top;

    internal override void CreateNativeChildren()
    {
        Root.CreateWidget("button", "upBtn", this);
        Root.CreateWidget("button", "downBtn", this);
        var slider = Root.CreateWidget("slider", "vslider", this);
        Root.CreateWidget("button", "thumb", slider);
    }
}

/// <summary>
/// "combobox": selector button (or editable selector), toggle arrow and a dropdown list. Items come from AppendItems
/// ({ { text = , value = }, ... }); the selection shows on the selector.
/// </summary>
public sealed class ComboBoxWidget : Widget
{
    private readonly List<(string Text, object? Value)> _items = [];
    private int _selected;
    private Widget? _selectorBtn;
    private Widget? _dropdown;

    internal ComboBoxWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public bool Editable { get; private set; }
    public int VisibleItemCount { get; private set; } = 10;
    public bool AutocompleteEnabled { get; private set; }
    public void ApplyUIScale() { }
    public void SetAutocomplete(bool enabled) => AutocompleteEnabled = enabled;
    public void SetDropdownVisibleLimit(double count)
    {
        VisibleItemCount = Math.Max(1, (int)count);
        UpdateDropdown();
    }
    private void UpdateDropdown()
    {
        if (_dropdown is not ListBoxWidget list) return;
        list.ClearItems();
        foreach (var (text, value) in _items) list.AppendItem(text, value);
        var rowHeight = Math.Max(1, list.GetHeight() > 0 ? list.GetHeight() : 20);
        list.SetHeight(rowHeight * Math.Min(VisibleItemCount, Math.Max(1, _items.Count)));
    }

    internal override void CreateNativeChildren()
    {
        _selectorBtn = Root.CreateWidget("button", "selectorBtn", this);
        _selectorBtn.AddAnchor("TOPLEFT", this, 0.0, 0.0);
        _selectorBtn.AddAnchor("BOTTOMRIGHT", this, 0.0, 0.0);
        var selector = Root.CreateWidget("x2editbox", "selector", this);
        selector.AddAnchor("TOPLEFT", this, 0.0, 0.0);
        selector.AddAnchor("BOTTOMRIGHT", this, 0.0, 0.0);
        selector.InitVisible(false);
        Root.CreateWidget("button", "toggle", this);
        _dropdown = Root.CreateWidget("listbox", "dropdown", this);
        _dropdown.AddAnchor("TOPLEFT", this, "BOTTOMLEFT", 0.0, 0.0);
        _dropdown.AddAnchor("TOPRIGHT", this, "BOTTOMRIGHT", 0.0, 0.0);
        _dropdown.InitVisible(false);
        _selectorBtn.SetHandler("OnClick", new NativeHandler((_, _) => { _dropdown.Show(!_dropdown.Visible); return null; }));
        _dropdown.SetHandler("OnSelChanged", new NativeHandler((_, args) =>
        {
            if (args.Length > 0 && args[0] is double index) Select(index);
            HideDropDown();
            return null;
        }));
    }

    public void SetEditable(bool editable)
    {
        Editable = editable;
        if (_selectorBtn != null) _selectorBtn.InitVisible(!editable);
        if (GetChildByName("selector") is { } s) s.InitVisible(editable);
    }

    public void SetVisibleItemCount(double count) => SetDropdownVisibleLimit(count);

    public void AppendItems(IDictionary<object, object?>? datas, object? unused = null)
    {
        if (datas == null) return;
        foreach (var k in datas.Keys.OfType<double>().OrderBy(k => k))
            if (datas[k] is IDictionary<object, object?> d)
                _items.Add((d.TryGetValue("text", out var t) ? t?.ToString() ?? "" : "", d.TryGetValue("value", out var v) ? v : null));
        if (_selected == 0 && _items.Count > 0) Select(1, false);
        UpdateDropdown();
    }

    public void AppendItem(string? text, object? value = null) { _items.Add((text ?? "", value)); UpdateDropdown(); }
    public void ClearItems() { _items.Clear(); _selected = 0; _selectorBtn?.SetText(""); UpdateDropdown(); }
    public int GetItemCount() => _items.Count;

    public void Select(double index, bool fireEvent = true)
    {
        var i = (int)index;
        if (i < 1 || i > _items.Count) return;
        _selected = i;
        _selectorBtn?.SetText(_items[i - 1].Text);
        GetChildByName("selector")?.SetText(_items[i - 1].Text, false);
        if (fireEvent) InvokeHandler("OnSelChanged", (double)i);
    }

    public int GetSelectedIndex() => _selected;

    public Dictionary<object, object?>? GetSelectedInfo()
    {
        if (_selected < 1 || _selected > _items.Count) return null;
        return new Dictionary<object, object?> { ["text"] = _items[_selected - 1].Text, ["value"] = _items[_selected - 1].Value, ["index"] = (double)_selected };
    }

    public void SelectWithValue(object? value, bool fireEvent = true)
    {
        var i = _items.FindIndex(x => Equals(x.Value, value));
        if (i >= 0) Select(i + 1, fireEvent);
    }

    public void SelectWithText(string? text, bool fireEvent = true)
    {
        var i = _items.FindIndex(x => x.Text == text);
        if (i >= 0) Select(i + 1, fireEvent);
    }

    public void HideDropDown() => _dropdown?.Show(false);
    public void PauseAutocomplete(bool pause) { }
}

/// <summary>
/// "message" (scrolling message frame) and "chatwindow": lines added with AddMessage(text[, r, g, b, a]) drawn from
/// the bottom up; older lines scroll out. The chat window's tabs/filters live in its scripts.
/// </summary>
public class MessageWidget : Widget
{
    private readonly List<(string Text, UiColor Color, double Time)> _lines = [];
    private double _clock;
    private Widget? _upButton, _downButton, _bottomButton;
    private SliderWidget? _slider;

    internal MessageWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent)
        => style.SetAlign(UiAlign.BottomLeft);

    internal IReadOnlyList<(string Text, UiColor Color, double Time)> Lines => _lines;
    public bool ItemLinksEnabled { get; private set; }
    public void EnableItemLink(bool enabled) => ItemLinksEnabled = enabled;
    public void ChangeTextStyle() { }
    internal double Clock => _clock;
    internal override void CreateNativeChildren()
    {
        _upButton = Root.CreateWidget("button", "upButton", this);
        _downButton = Root.CreateWidget("button", "downButton", this);
        _bottomButton = Root.CreateWidget("button", "downToBottomButton", this);
        _slider = (SliderWidget)Root.CreateWidget("slider", "slider", this);
        _slider.EnableNativeInteraction();
        var thumb = Root.CreateWidget("button", "thumb", _slider);
        _slider.SetThumbButtonWidget(thumb);
        _upButton.SetHandler("OnClick", new NativeHandler((_, _) => { ScrollUp(); return null; }));
        _downButton.SetHandler("OnClick", new NativeHandler((_, _) => { ScrollDown(); return null; }));
        _bottomButton.SetHandler("OnClick", new NativeHandler((_, _) => { ScrollToBottom(); return null; }));
        _slider.SetHandler("OnSliderChanged", new NativeHandler((_, args) =>
        {
            Scroll = args.Length > 0 ? args[0] switch
            {
                double d => Math.Clamp((int)Math.Round(d), 0, _lines.Count),
                float f => Math.Clamp((int)Math.Round(f), 0, _lines.Count),
                _ => Scroll
            } : Scroll;
            return null;
        }));
    }
    public Widget GetUpButton() => _upButton!;
    public Widget GetDownButton() => _downButton!;
    public Widget GetDownToBottomButton() => _bottomButton!;
    public SliderWidget GetSlider() => _slider!;
    public void MouseWheelUp(double delta = 1) => ScrollUp(delta);
    public void MouseWheelDown(double delta = 1) => ScrollDown(delta);
    public int GetPagePerMaxLines() => Math.Max(1, (int)(GetHeight() / Math.Max(1, style.GetLineHeight())));
    public int MaxLines { get; private set; } = 200;
    public int Scroll { get; private set; }
    public double FadeDuration { get; private set; }
    public double VisibleTime { get; private set; }

    public void AddMessage(string? text, params object?[] args)
    {
        var color = style.Color;
        if (args.Length >= 3 && args[0] is double r && args[1] is double g && args[2] is double b)
            color = new UiColor((float)r, (float)g, (float)b, args.Length > 3 && args[3] is double a ? (float)a : 1);
        _lines.Add((text ?? "", color, _clock));
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        UpdateSlider();
        InvokeHandler("OnContentUpdated", Scroll == 0 ? "read_chat_message" : "unread_chat_message");
    }

    /// <summary>Adds a line with a resolved native chat colour.</summary>
    public void AddMessage(string? text, UiColor color)
    {
        _lines.Add((text ?? "", color, _clock));
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        UpdateSlider();
        InvokeHandler("OnContentUpdated", Scroll == 0 ? "read_chat_message" : "unread_chat_message");
    }

    public void AddMessageEx(string? text, params object?[] args) => AddMessage(text, args);
    public void AddMessageRefresh(string? text, params object?[] args) => AddMessage(text, args);
    public void Clear() { _lines.Clear(); Scroll = 0; UpdateSlider(); }
    public void RemoveLastMessage() { if (_lines.Count > 0) _lines.RemoveAt(_lines.Count - 1); UpdateSlider(); }
    public int GetMessageLines() => _lines.Count;
    /// <summary>
    /// Native message widgets return a link-info record even when the pointer is over ordinary text. The Lua
    /// chat handlers index linkType unconditionally, so a missing hit-test result must be represented as "none".
    /// </summary>
    public Dictionary<string, object?> GetLinkInfoOnCursor() => new() { ["linkType"] = "none" };
    public void SetMaxLines(double lines) => MaxLines = Math.Max(1, (int)lines);
    public int GetMaxLines() => MaxLines;
    public void ScrollUp(double n = 1) { Scroll = Math.Min(_lines.Count, Scroll + (int)Math.Max(1, n)); UpdateSlider(); }
    public void ScrollDown(double n = 1) { Scroll = Math.Max(0, Scroll - (int)Math.Max(1, n)); UpdateSlider(); }
    public void ScrollToBottom() { Scroll = 0; UpdateSlider(); InvokeHandler("OnContentUpdated", "read_chat_message"); }
    public void ScrollToTop() { Scroll = _lines.Count; UpdateSlider(); }
    public void SetFadeDuration(double seconds) => FadeDuration = seconds;
    public void SetTimeVisible(double seconds) => VisibleTime = seconds;
    public void SetFading(bool fading) { if (!fading) VisibleTime = 0; }

    internal virtual void Tick(double ms) => _clock += ms / 1000;

    private void UpdateSlider()
    {
        if (_slider == null) return;
        _slider.SetMinMaxValues(0, Math.Max(1, _lines.Count));
        _slider.SetValue(Scroll, false);
    }
}

/// <summary>
/// "radiogroup": items created with CreateRadioItem(value) (an item frame with a "check" button); Check(index) selects
/// one and fires OnRadioChanged(index, value).
/// </summary>
public sealed class RadioGroupWidget : Widget
{
    private readonly List<(Widget Item, CheckButtonWidget Check, object? Value, bool Enabled)> _items = [];
    private int _checked;

    internal RadioGroupWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public Widget CreateRadioItem(object? value)
    {
        var index = _items.Count + 1;
        var item = Root.CreateWidget("emptywidget", $"item[{index}]", this);
        var check = (CheckButtonWidget)Root.CreateWidget("checkbutton", "check", item);
        check.SetHandler("OnClick", new NativeHandler((_, _) => { Check(index, true); return null; }));
        _items.Add((item, check, value, true));
        return item;
    }

    public void ShowIndex(double index, bool visible)
    {
        var i = (int)index;
        if (i >= 1 && i <= _items.Count) _items[i - 1].Item.Show(visible);
    }
    // Option scripts may override this callback on individual radio groups.
    public void InitProc(params object?[] args) { }

    public void Check(double index, bool fireEvent = false)
    {
        var i = (int)index;
        if (i < 1 || i > _items.Count) return;
        _checked = i;
        for (var k = 0; k < _items.Count; k++) _items[k].Check.SetChecked(k + 1 == i, false);
        if (fireEvent) InvokeHandler("OnRadioChanged", (double)i, ScriptValue(_items[i - 1].Value));
    }

    private static object? ScriptValue(object? v) => v;

    public int GetChecked() => _checked;
    public object? GetCheckedValue() => _checked >= 1 && _checked <= _items.Count ? _items[_checked - 1].Value : null;
    public object? GetCheckedData() => GetCheckedValue();
    public int GetItemCount() => _items.Count;

    public double? GetIndexByValue(object? value)
    {
        for (var index = 0; index < _items.Count; index++)
            if (Equals(_items[index].Value, value)) return index + 1;
        return null;
    }

    public void EnableIndex(double index, bool enable)
    {
        var i = (int)index;
        if (i >= 1 && i <= _items.Count) _items[i - 1].Check.Enable(enable);
    }

    public void Clear()
    {
        foreach (var (item, _, _, _) in _items) Root.Destroy(item);
        _items.Clear();
        _checked = 0;
    }

    public void ReleaseCheck() { _checked = 0; foreach (var it in _items) it.Check.SetChecked(false, false); }
}
