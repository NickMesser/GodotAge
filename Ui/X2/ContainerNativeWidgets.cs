#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>A native folding container; the title and arrow buttons are supplied by Lua.</summary>
public sealed class FolderWidget : Widget
{
    private Widget? _openButton, _closeButton, _titleButton, _child;
    private float _titleHeight, _extendLength;
    // Native folders begin closed; Lua explicitly opens those that should show their child content.
    private bool _open;
    private bool _titleButtonAnchored;

    internal FolderWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public bool IsOpen => _open;
    public string GetState() => _open ? "open" : "close";
    public bool AnimationEnabled { get; private set; } = true;
    public float AnimateStep { get; private set; } = 1;
    public void SetAnimateStep(float step) => AnimateStep = Math.Max(0, step);
    public void UseAnimation(bool enable) => AnimationEnabled = enable;
    public void SetOpenStateButton(Widget? button) { _openButton = button; Refresh(); }
    public void SetCloseStateButton(Widget? button) { _closeButton = button; Refresh(); }
    public void SetTitleButtonWidget(Widget? button) { _titleButton = button; _titleButtonAnchored = false; LayoutTitleButton(); }
    public void SetChildWidget(Widget? child) { _child = child; Refresh(); }
    public void SetTitleHeight(float height) { _titleHeight = Math.Max(0, height); LayoutTitleButton(); Refresh(); }
    public override void SetInset(float left, float top, float right, float bottom)
    {
        base.SetInset(left, top, right, bottom);
        LayoutTitleButton();
    }
    public void SetExtendLength(float length) { _extendLength = Math.Max(0, length); Refresh(); }
    public string GetTitleText() => _titleButton?.GetText() ?? GetText();
    public void OpenFolder() { _open = true; Refresh(); }
    public void CloseFolder() { _open = false; Refresh(); }
    public void FixedCloseFolder() => CloseFolder();
    public void ToggleState() { _open = !_open; Refresh(); }
    private void LayoutTitleButton()
    {
        if (_titleButton == null || (_titleButton.Anchors.Count != 0 && !_titleButtonAnchored)) return;
        if (_titleButtonAnchored) _titleButton.RemoveAllAnchors();
        _titleButton.AddAnchor("TOPLEFT", this, (double)TextInset.Left, (double)TextInset.Top);
        _titleButton.AddAnchor("TOPRIGHT", this, (double)TextInset.Right, (double)TextInset.Top);
        _titleButtonAnchored = true;
        if (_titleHeight > 0) _titleButton.SetHeight(_titleHeight);
    }
    private void Refresh()
    {
        _openButton?.Show(_open);
        _closeButton?.Show(!_open);
        _child?.Show(_open);
        if (_titleHeight > 0) SetHeight(_titleHeight + (_open ? _extendLength : 0));
    }
}

/// <summary>The notifier uses grid as an item-owning container, while the market's grid is Lua-built.</summary>
public sealed class GridWidget : Widget
{
    private readonly List<Widget> _items = [];
    private readonly Dictionary<(int Row, int Col), Widget> _cells = [];
    private readonly Dictionary<int, float> _columnWidths = [];
    private readonly Dictionary<int, float> _rowHeights = [];
    private readonly Dictionary<(int Row, int Col), UiInsets> _cellInsets = [];
    private int _columnCount;

    internal GridWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }
    public void AddItem(Widget? item) { if (item != null && !_items.Contains(item)) _items.Add(item); }
    public void InsertItem(Widget? item) => AddItem(item);
    public void RemoveItem(Widget? item)
    {
        if (item == null) return;
        _items.Remove(item);
        foreach (var cell in _cells.Where(x => ReferenceEquals(x.Value, item)).Select(x => x.Key).ToArray())
            _cells.Remove(cell);
    }
    public void RemoveAllItems()
    {
        foreach (var item in _items) item.Show(false);
        _items.Clear();
        _cells.Clear();
        _rowHeights.Clear();
        _cellInsets.Clear();
    }
    public int GetItemCount() => _items.Count;
    public Widget? GetItem(double index) => index >= 1 && index <= _items.Count ? _items[(int)index - 1] : null;
    public LuaMulti GetGridExtent() => new(GetWidth(), GetHeight());

    /// <summary>Sets the number of one-based columns used by the native grid layout.</summary>
    public void SetColCount(double count)
    {
        _columnCount = Math.Max(0, (int)count);
        foreach (var col in _columnWidths.Keys.Where(x => x > _columnCount).ToArray()) _columnWidths.Remove(col);
        Reflow();
    }

    /// <summary>SetColWidth(width, column), matching the argument order used by the X2 scripts.</summary>
    public void SetColWidth(float width, double column)
    {
        var col = (int)column;
        if (col < 1) return;
        _columnWidths[col] = Math.Max(0, width);
        Reflow();
    }

    /// <summary>Places a widget in a one-based grid cell. The remaining native flags do not affect its position.</summary>
    public void SetItem(Widget? item, double row, double column, bool occupyCell = true, double alignment = 0, bool resize = false)
    {
        if (item == null) return;
        var key = ((int)row, (int)column);
        if (key.Item1 < 1 || key.Item2 < 1) return;

        foreach (var previous in _cells.Where(x => ReferenceEquals(x.Value, item) && x.Key != key).Select(x => x.Key).ToArray())
            _cells.Remove(previous);
        if (_cells.TryGetValue(key, out var replaced) && !ReferenceEquals(replaced, item))
            _items.Remove(replaced);

        _cells[key] = item;
        AddItem(item);
        item.Show(true);
        Position(key, item);
    }

    /// <summary>SetRowHeight(height, row), matching the argument order used by the X2 scripts.</summary>
    public void SetRowHeight(float height, double row)
    {
        var index = (int)row;
        if (index < 1) return;
        _rowHeights[index] = Math.Max(0, height);
        Reflow();
    }

    public void SetItemInset(double row, double column, float left, float top, float right, float bottom)
    {
        var key = ((int)row, (int)column);
        if (key.Item1 < 1 || key.Item2 < 1) return;
        _cellInsets[key] = new UiInsets(left, top, right, bottom);
        if (_cells.TryGetValue(key, out var item)) Position(key, item);
    }

    private void Reflow()
    {
        foreach (var (cell, item) in _cells) Position(cell, item);
    }

    private void Position((int Row, int Col) cell, Widget item)
    {
        float x = 0, y = 0;
        for (var col = 1; col < cell.Col; col++) x += _columnWidths.GetValueOrDefault(col);
        for (var row = 1; row < cell.Row; row++) y += _rowHeights.GetValueOrDefault(row);
        var inset = _cellInsets.GetValueOrDefault(cell);
        item.RemoveAllAnchors();
        item.AddAnchor("TOPLEFT", this, (double)(x + inset.Left), (double)(y + inset.Top));
        // Native grids give an otherwise unsized item the usable height of
        // its row. Quest title labels rely on this: they set only their width
        // before insertion, then establish the title row's height afterward.
        if (item.NaturalSize.H <= 0 && _rowHeights.TryGetValue(cell.Row, out var rowHeight))
            item.SetHeight(Math.Max(0, rowHeight - inset.Top - inset.Bottom));
    }
}

public sealed class CircleDiagramWidget : Widget
{
    private readonly List<(float X, float Y, float Value)> _points = [];
    internal CircleDiagramWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }
    public UiColor DiagramColor { get; private set; } = UiColor.White;
    public float MaxValue { get; private set; } = 1;
    public IReadOnlyList<(float X, float Y, float Value)> Points => _points;
    public void SetDiagramColor(float r, float g, float b, float a = 1) => DiagramColor = new UiColor(r, g, b, a);
    public void SetMaxValue(float max) => MaxValue = Math.Max(0.001f, max);
    public void AddPoint(float x, float y) => _points.Add((x, y, 0));
    public void SetPointValue(double index, float value)
    {
        var i = (int)index - 1;
        if (i >= 0 && i < _points.Count) _points[i] = (_points[i].X, _points[i].Y, Math.Clamp(value, 0, MaxValue));
    }
    public void ClearPoints() => _points.Clear();
}

public sealed class LineWidget : Widget
{
    private readonly List<(float X, float Y)> _points = [];
    internal LineWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }
    public IReadOnlyList<(float X, float Y)> Points => _points;
    public float LineThickness { get; private set; } = 1;
    public void SetLineThickness(float thickness) => LineThickness = Math.Max(0, thickness);
    public void ClearPoints() => _points.Clear();
    public void SetPoints(IDictionary<object, object?>? points)
    {
        _points.Clear();
        if (points == null) return;
        foreach (var key in points.Keys.OfType<double>().OrderBy(x => x))
        {
            if (points[key] is not IDictionary<object, object?> p) continue;
            if (TryNumber(p, 1, out var x) && TryNumber(p, 2, out var y)) _points.Add(((float)x, (float)y));
        }
    }
    private static bool TryNumber(IDictionary<object, object?> p, double index, out double value)
    {
        value = 0;
        return p.TryGetValue(index, out var v) && v is double d && (value = d) == d;
    }
}
