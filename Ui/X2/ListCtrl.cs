#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// UOT_LIST_CTRL: a header of column buttons and rows of sub-items, exposed to scripts as the Lua fields
/// listCtrl.column[i] and listCtrl.items[row].subItems[col]. Row data and paging live in the Lua base library;
/// the native part owns layout, selection and the hover/selection highlight images.
/// </summary>
public sealed class ListCtrlWidget : Widget
{
    private readonly List<(Widget Button, float Width, int ItemType)> _columns = [];
    private readonly List<Widget> _rows = [];
    private readonly List<List<Widget>> _subItems = [];
    private readonly HashSet<int> _rowsWithData = [];
    private float _headerHeight = 35;
    private int _selected = -1;
    private TextureDrawable? _overImage;
    private TextureDrawable? _selectedImage;

    internal ListCtrlWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public bool UseDoubleClick { get; private set; }

    public void InsertColumn(float width, double itemType = 0)
    {
        var index = _columns.Count + 1;
        var button = Root.CreateWidget("button", $"column[{index}]", this);
        _columns.Add((button, width, (int)itemType));
        for (var r = 0; r < _rows.Count; r++)
            _subItems[r].Add(CreateSubItem(_rows[r], index, (int)itemType));
        PublishColumns();
        PublishRows();
        Root.InvalidateLayout();
    }

    public void SetHeaderColumnHeight(float height) { _headerHeight = height; Root.InvalidateLayout(); }
    public float GetHeaderColumnHeight() => _headerHeight;
    public void SetColumnWidth(double column, float width)
    {
        var i = (int)column - 1;
        if (i >= 0 && i < _columns.Count) { _columns[i] = (_columns[i].Button, width, _columns[i].ItemType); Root.InvalidateLayout(); }
    }

    public void InsertRows(double count, bool useEventWindow = false)
    {
        for (var n = 0; n < (int)count; n++)
        {
            var index = _rows.Count + 1;
            var row = Root.CreateWidget("emptywidget", $"item[{index}]", this);
            _rows.Add(row);
            var subs = new List<Widget>();
            for (var c = 0; c < _columns.Count; c++)
                subs.Add(CreateSubItem(row, c + 1, _columns[c].ItemType));
            _subItems.Add(subs);
        }
        PublishRows();
        Root.InvalidateLayout();
    }

    private Widget CreateSubItem(Widget row, int column, int itemType)
    {
        var type = itemType switch { 1 => "button", 2 => "emptywidget", 3 => "textbox", _ => "label" };
        var sub = Root.CreateWidget(type, $"subItem[{column}]", row);
        if (itemType == 0) sub.EnablePick(false);
        return sub;
    }

    private void PublishColumns()
    {
        var t = new Dictionary<object, object?>();
        for (var i = 0; i < _columns.Count; i++) t[(double)(i + 1)] = _columns[i].Button;
        Root.SetScriptField(this, "column", t);
    }

    private void PublishRows()
    {
        var items = new Dictionary<object, object?>();
        for (var r = 0; r < _rows.Count; r++)
        {
            items[(double)(r + 1)] = _rows[r];
            var subs = new Dictionary<object, object?>();
            for (var c = 0; c < _subItems[r].Count; c++) subs[(double)(c + 1)] = _subItems[r][c];
            Root.SetScriptField(_rows[r], "subItems", subs);
        }
        Root.SetScriptField(this, "items", items);
    }

    public int GetRowCount() => _rows.Count;
    public int GetColumnCount() => _columns.Count;

    /// <summary>Marks a row as holding data (only such rows are selectable).</summary>
    public void InsertData(double row, double column = 1, object? data = null)
    {
        var r = (int)row - 1;
        if (r < 0 || r >= _rows.Count) return;
        _rowsWithData.Add(r);
        if (column >= 1 && (int)column - 1 < _subItems[r].Count && data is string s && s.Length > 0)
            _subItems[r][(int)column - 1].SetText(s);
    }

    public void DeleteData(double row) { _rowsWithData.Remove((int)row - 1); if (_selected == (int)row - 1) _selected = -1; UpdateHighlights(); }
    public void DeleteAllDatas() { _rowsWithData.Clear(); _selected = -1; UpdateHighlights(); }
    public void SetUseDoubleClick(bool use) => UseDoubleClick = use;

    /// <summary>Select(index0 [, fireEvent]); -1 clears.</summary>
    public void Select(double index, bool fireEvent = true)
    {
        var i = (int)index;
        _selected = i >= 0 && i < _rows.Count ? i : -1;
        UpdateHighlights();
        if (fireEvent && _selected >= 0) InvokeHandler("OnSelChanged", (double)(_selected + 1), false);
    }

    /// <summary>1-based selected row, 0 when nothing is selected.</summary>
    public int GetSelectedIdx() => _selected + 1;
    public void ClearSelection() { _selected = -1; UpdateHighlights(); }

    public TextureDrawable CreateOveredImage()
    {
        _overImage = CreateDrawable("ui/common_new/default.dds", "common_ov", "artwork");
        _overImage.SetVisible(false);
        return _overImage;
    }

    public TextureDrawable CreateSelectedImage()
    {
        _selectedImage = CreateDrawable("ui/common_new/default.dds", "bg", "artwork");
        _selectedImage.SetVisible(false);
        return _selectedImage;
    }

    public void SetSelectedImageOffset(params object?[] args) { }
    public void SetOveredImageOffset(params object?[] args) { }

    internal int? RowIndexOf(Widget child)
    {
        var i = _rows.IndexOf(child);
        return i < 0 ? null : i;
    }

    internal void OnRowClicked(int index, bool doubleClick)
    {
        if (!Enabled || !_rowsWithData.Contains(index)) return;
        _selected = index;
        UpdateHighlights();
        InvokeHandler("OnSelChanged", (double)(index + 1), doubleClick && UseDoubleClick);
    }

    /// <summary>Anchors the hover/selection images to their rows (called every frame by the renderer).</summary>
    internal void UpdateHighlights()
    {
        if (_selectedImage != null)
        {
            var show = _selected >= 0 && _selected < _rows.Count;
            _selectedImage.SetVisible(show);
            if (show) Pin(_selectedImage, _rows[_selected]);
        }
        if (_overImage != null)
        {
            var hovered = -1;
            for (var w = Root.Hover; w != null; w = w.Parent)
                if (w.Parent == this && RowIndexOf(w) is { } i) { hovered = i; break; }
            var show = hovered >= 0 && _rowsWithData.Contains(hovered) && hovered != _selected;
            _overImage.SetVisible(show);
            if (show) Pin(_overImage, _rows[hovered]);
        }
    }

    private static void Pin(Drawable d, Widget row)
    {
        if (d.Anchors.Count == 2 && ReferenceEquals(d.Anchors[0].RelativeTo, row)) return;
        d.RemoveAllAnchors();
        d.AddAnchor("TOPLEFT", row, 0.0, 0.0);
        d.AddAnchor("BOTTOMRIGHT", row, 0.0, 0.0);
    }

    /// <summary>Header buttons and rows are laid out by the list itself.</summary>
    internal UiRect? LayoutChild(LayoutElement child, UiRect rect)
    {
        if (child is not Widget w) return null;
        for (var c = 0; c < _columns.Count; c++)
        {
            if (!ReferenceEquals(_columns[c].Button, w)) continue;
            var x = rect.X + _columns.Take(c).Sum(col => col.Width);
            return new UiRect(x, rect.Y, _columns[c].Width, _headerHeight);
        }
        var r = _rows.IndexOf(w);
        if (r < 0) return null;
        var rowHeight = _rows.Count == 0 ? 0 : Math.Max(0, rect.Height - _headerHeight) / _rows.Count;
        var width = _columns.Count > 0 ? _columns.Sum(col => col.Width) : rect.Width;
        return new UiRect(rect.X, rect.Y + _headerHeight + r * rowHeight, width, rowHeight);
    }

    internal UiRect? LayoutSubItem(Widget row, Widget sub, UiRect rowRect)
    {
        var r = _rows.IndexOf(row);
        if (r < 0) return null;
        var c = _subItems[r].IndexOf(sub);
        if (c < 0 || sub.Anchors.Count > 0) return null;
        var x = rowRect.X + _columns.Take(c).Sum(col => col.Width);
        return new UiRect(x, rowRect.Y, c < _columns.Count ? _columns[c].Width : 0, rowRect.Height);
    }
}
