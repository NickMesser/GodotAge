#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>Scrollable, pooled main/sub rows for the client's Lua dynamic-list callbacks.</summary>
public sealed class DynamicListWidget : Widget
{
    private sealed class Entry(object? key, IDictionary<object, object?>? children)
    {
        public object? Key = key;
        public IDictionary<object, object?>? Children = children;
        public bool Open;
    }

    private readonly List<Entry> _data = [];
    private readonly List<Entry> _pageData = [];
    private readonly List<Widget> _mainPool = [];
    private readonly List<Widget> _subPool = [];
    private Widget? _content;
    private object? _mainLayout, _mainData, _subLayout, _subData;
    private float _viewHeight, _mainHeight = 24, _subHeight = 20, _mainGap, _subGap;
    private float _currentHeight, _scrollMax;
    private double _min, _max;
    private bool _childTouch = true;
    private object? _selectedKey;
    private int _selectedDepth;
    private TextureDrawable? _overed;

    internal DynamicListWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public Widget? Content => _content;
    public IReadOnlyList<Widget> MainPool => _mainPool;
    public IReadOnlyList<Widget> SubPool => _subPool;
    public float CurrentHeight => _currentHeight;
    public float ScrollMax => _scrollMax;
    public bool ChildTouchEnabled => _childTouch;
    public object? BackgroundType { get; private set; }

    internal override void CreateNativeChildren()
    {
        _content = Root.CreateWidget("emptywidget", "content", this);
        _content.AddAnchor("TOPLEFT", this, 0.0, 0.0);
        _content.AddAnchor("TOPRIGHT", this, 0.0, 0.0);
    }

    public void InitFunc(object? mainLayout, object? mainData, object? subLayout, object? subData)
    { _mainLayout = mainLayout; _mainData = mainData; _subLayout = subLayout; _subData = subData; }
    public void InitBgType(object? type) => BackgroundType = type;
    public void InitHeight(float viewHeight, float mainHeight, float subHeight)
    { _viewHeight = Math.Max(0, viewHeight); _mainHeight = Math.Max(1, mainHeight); _subHeight = Math.Max(1, subHeight); UpdateView(); }
    public void SetGaps(float mainGap, float subGap)
    { _mainGap = Math.Max(0, mainGap); _subGap = Math.Max(0, subGap); UpdateView(); }
    public void EnableChildTouch(bool enable)
    {
        _childTouch = enable;
        foreach (var w in _mainPool.Concat(_subPool)) w.EnablePick(enable, true);
    }
    public void InitCreateWidgetPool()
    {
        var capacity = Math.Clamp((int)Math.Ceiling((_viewHeight > 0 ? _viewHeight : GetHeight()) / Math.Min(_mainHeight, _subHeight)) + 2, 1, 256);
        EnsurePool(_mainPool, capacity, "main", _mainLayout);
        EnsurePool(_subPool, capacity, "sub", _subLayout);
        UpdateView();
    }
    private void EnsurePool(List<Widget> pool, int count, string name, object? layout)
    {
        if (_content == null) return;
        while (pool.Count < count)
        {
            var row = Root.CreateWidget("emptywidget", $"{name}[{pool.Count + 1}]", _content);
            row.Show(false);
            row.EnablePick(_childTouch);
            pool.Add(row);
            Call(layout, row, (double)pool.Count);
        }
    }
    private object? Call(object? function, params object?[] args) => function == null ? null : Root.FunctionInvoker?.Invoke(function, args);

    public void ClearData() { _data.Clear(); _currentHeight = 0; UpdateView(); }
    public void PushData(object? mainKey, IDictionary<object, object?>? children = null) { _data.Add(new Entry(mainKey, children)); UpdateView(); }
    public void InsertData(double index, object? mainKey, IDictionary<object, object?>? children = null)
    { _data.Insert(Math.Clamp((int)index - 1, 0, _data.Count), new Entry(mainKey, children)); UpdateView(); }
    public void EraseData(double index)
    { var i = (int)index - 1; if (i >= 0 && i < _data.Count) { _data.RemoveAt(i); UpdateView(); } }
    public void UpdateData(object? mainKey, IDictionary<object, object?>? children)
    { var e = _data.FirstOrDefault(x => Equals(x.Key, mainKey)); if (e != null) { e.Children = children; UpdateView(); } }
    public int? GetMainIndex(object? mainKey)
    { var i = _data.FindIndex(x => Equals(x.Key, mainKey)); return i < 0 ? null : i + 1; }
    public int? GetMainIndexByKey(object? mainKey) => GetMainIndex(mainKey);
    public int? FindIndex(object? mainKey)
    { var i = _pageData.FindIndex(x => Equals(x.Key, mainKey)); return i < 0 ? null : i + 1; }
    public Dictionary<object, object?>? FindData(double index)
    {
        var i = (int)index - 1;
        return i < 0 || i >= _pageData.Count ? null : new() { ["main"] = _pageData[i].Key, ["subList"] = _pageData[i].Children ?? new Dictionary<object, object?>() };
    }
    public void ClearPageData() { _pageData.Clear(); ClearData(); }
    public void PushPageData(object? mainKey, IDictionary<object, object?>? children = null, bool forceUpdate = false)
    { _pageData.Add(new Entry(mainKey, children)); if (forceUpdate) { _data.Add(new Entry(mainKey, children)); UpdateView(); } }
    public void InsertPageData(double index, object? mainKey, IDictionary<object, object?>? children = null, bool forceUpdate = false)
    { _pageData.Insert(Math.Clamp((int)index - 1, 0, _pageData.Count), new Entry(mainKey, children)); if (forceUpdate) InsertData(index, mainKey, children); }
    public void DeletePageData(double index, bool forceUpdate = false)
    { var i = (int)index - 1; if (i < 0 || i >= _pageData.Count) return; var key = _pageData[i].Key; _pageData.RemoveAt(i); if (forceUpdate && GetMainIndex(key) is { } j) EraseData(j); }
    public void UpdateDatas(object? mainKey, IDictionary<object, object?>? children) => UpdateData(mainKey, children);
    public void OpenBySubItemInfo(object? mainKey, double depth, object? subKey)
    { var e = _data.FirstOrDefault(x => Equals(x.Key, mainKey)); if (e != null) { e.Open = true; UpdateView(); } }
    public void SetSelectedItemInfo(object? key, double depth) { _selectedKey = key; _selectedDepth = (int)depth; UpdateView(); }
    public void SaveCurrentPage() { }
    public void LoadCurrentPage() => UpdateView();
    public void SaveItemList() { }
    public void LoadItemList() => UpdateView();
    public void MoveIndexScroll(double index, float anchorHeight = 0, bool open = false) => MoveIndex(index - 1, anchorHeight, open);
    public void MoveIndex(double index, float anchorHeight = 0, bool open = false)
    {
        var i = Math.Clamp((int)index, 0, Math.Max(0, _data.Count - 1));
        if (open && _data.Count > i) _data[i].Open = true;
        var h = 0f;
        for (var n = 0; n < i; n++) h += _mainHeight + _mainGap + (_data[n].Open ? CountChildren(_data[n].Children) * (_subHeight + _subGap) : 0);
        MoveHeight(h - anchorHeight);
    }
    public void MoveHeight(double height) { _currentHeight = Math.Clamp((float)height, 0, _scrollMax); UpdateView(); }
    public float GetCurrentHeight() => _currentHeight;
    public float GetScrollMaxValue() => _scrollMax;
    public void SetMinMaxValues(double min, double max) { _min = min; _max = Math.Max(min, max); }
    public LuaMulti GetMinMaxValues() => new(_min, _max);
    public TextureDrawable CreateOveredImage(string layer = "overlay")
    { _overed = CreateImageDrawable("", layer); _overed.Show(false); return _overed; }

    public void UpdateView()
    {
        var viewport = _viewHeight > 0 ? _viewHeight : GetHeight();
        var fullHeight = _data.Sum(e => _mainHeight + _mainGap + (e.Open ? CountChildren(e.Children) * (_subHeight + _subGap) : 0));
        _scrollMax = Math.Max(0, fullHeight - viewport);
        _currentHeight = Math.Clamp(_currentHeight, 0, _scrollMax);
        if (_content == null) return;
        _content.SetHeight(Math.Max(viewport, fullHeight));
        var mainUsed = 0; var subUsed = 0; var y = -_currentHeight;
        foreach (var e in _data)
        {
            if (y + _mainHeight >= 0 && y <= viewport && mainUsed < _mainPool.Count)
            {
                var row = _mainPool[mainUsed++]; Place(row, y, _mainHeight);
                Call(_mainData, row, e.Key, e.Open, _overed, (double)CountChildren(e.Children), Equals(e.Key, _selectedKey) && _selectedDepth == 0);
            }
            y += _mainHeight + _mainGap;
            if (!e.Open || e.Children == null) continue;
            foreach (var child in OrderedChildren(e.Children))
            {
                if (y + _subHeight >= 0 && y <= viewport && subUsed < _subPool.Count)
                {
                    var row = _subPool[subUsed++]; Place(row, y, _subHeight);
                    var key = child is IDictionary<object, object?> info && info.TryGetValue("key", out var k) ? k : null;
                    Call(_subData, row, child, Equals(key, _selectedKey) && _selectedDepth > 0);
                }
                y += _subHeight + _subGap;
            }
        }
        foreach (var row in _mainPool.Skip(mainUsed).Concat(_subPool.Skip(subUsed))) row.Show(false);
        InvokeHandler("OnDynamicListUpdatedView");
    }
    private void Place(Widget row, float y, float height)
    {
        row.RemoveAllAnchors();
        row.AddAnchor("TOPLEFT", _content!, 0.0, (double)y);
        row.AddAnchor("TOPRIGHT", _content!, 0.0, (double)y);
        row.SetHeight(height);
        row.Show(true);
    }
    private static IEnumerable<object?> OrderedChildren(IDictionary<object, object?>? table)
        => table == null ? [] : table.Keys.OfType<double>().OrderBy(x => x).Select(x => table[x]);
    private static int CountChildren(IDictionary<object, object?>? table) => table?.Keys.OfType<double>().Count() ?? 0;
}
