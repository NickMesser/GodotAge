#nullable enable
namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>
/// "tab": a row of tab buttons over one page per tab, exposed to scripts as tab.selectedButton[i],
/// tab.unselectedButton[i] and tab.window[i]. The selected tab shows its page and its selected-state button.
/// </summary>
public sealed class TabWidget : Widget
{
    private readonly List<(Widget Selected, Widget Unselected, Widget Window, bool Hidden)> _tabs = [];
    private int _selected;
    private float _gap;
    private float _offset;
    private bool _allowSwitch = true;
    private int _activeCount = -1;

    internal TabWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    internal override void CreateNativeChildren() => Publish();

    public void AddSimpleTab(string? text) => AddNewTab(text);

    public void AddNewTab(string? text, object? unused = null)
    {
        var index = _tabs.Count + 1;
        var selected = Root.CreateWidget("button", $"selectedButton[{index}]", this);
        var unselected = Root.CreateWidget("button", $"unselectedButton[{index}]", this);
        var window = Root.CreateWidget("emptywidget", $"window[{index}]", this);
        selected.SetText(text);
        unselected.SetText(text);
        // The stock W_TAB skin places its content background 29 px below the button row.
        // Tab pages share that origin; placing them at y=0 lets the first page labels draw through the tabs.
        window.AddAnchor("TOPLEFT", this, 0.0, 29.0);
        window.AddAnchor("BOTTOMRIGHT", this, 0.0, 0.0);
        var tabIndex = index;
        unselected.SetHandler("OnClick", new NativeHandler((_, _) => { if (_allowSwitch) SelectTab(tabIndex); return null; }));
        _tabs.Add((selected, unselected, window, false));
        if (_selected == 0) _selected = 1;
        Publish();
        AlignTabButtons();
        Refresh();
    }

    private void Publish()
    {
        Dictionary<object, object?> Table(Func<(Widget, Widget, Widget, bool), Widget> pick)
        {
            var t = new Dictionary<object, object?>();
            for (var i = 0; i < _tabs.Count; i++) t[(double)(i + 1)] = pick(_tabs[i]);
            return t;
        }
        Root.SetScriptField(this, "selectedButton", Table(t => t.Item1));
        Root.SetScriptField(this, "unselectedButton", Table(t => t.Item2));
        Root.SetScriptField(this, "window", Table(t => t.Item3));
    }

    public void SelectTab(double index)
    {
        var i = (int)index;
        if (i < 1 || i > _tabs.Count || !IsActive(i - 1)) return;
        var previous = _selected;
        _selected = i;
        Refresh();
        if (previous != i) InvokeHandler("OnTabChanged", (double)i, (double)i);
    }

    public int GetSelectedTab() => _selected;
    public int GetTabCount() => _tabs.Count;
    public void SetGap(float gap) { _gap = gap; AlignTabButtons(); }
    public void SetOffset(float offset) { _offset = offset; AlignTabButtons(); }
    public void SetCorner(string corner) { }
    public void AllowTabSwitch(bool allow) => _allowSwitch = allow;
    public void SetActivateTabCount(double count)
    {
        _activeCount = (int)count;
        SelectFirstActiveWhenSelectionIsUnavailable();
        AlignTabButtons();
        Refresh();
    }
    public int GetActivateTabCount() => _activeCount < 0 ? _tabs.Count : _activeCount;

    public void HideTab(double index) => SetHidden((int)index, true);
    public void ShowTab(double index) => SetHidden((int)index, false);

    private void SetHidden(int index, bool hidden)
    {
        if (index < 1 || index > _tabs.Count) return;
        var t = _tabs[index - 1];
        _tabs[index - 1] = (t.Selected, t.Unselected, t.Window, hidden);
        SelectFirstActiveWhenSelectionIsUnavailable();
        AlignTabButtons();
        Refresh();
    }

    private bool IsActive(int zeroBasedIndex)
        => zeroBasedIndex >= 0 && zeroBasedIndex < _tabs.Count
           && !_tabs[zeroBasedIndex].Hidden
           && (_activeCount < 0 || zeroBasedIndex < _activeCount);

    private void SelectFirstActiveWhenSelectionIsUnavailable()
    {
        if (IsActive(_selected - 1)) return;
        _selected = 0;
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (!IsActive(i)) continue;
            _selected = i + 1;
            return;
        }
    }

    public void RemoveAllTabs()
    {
        foreach (var (s, u, w, _) in _tabs) { Root.Destroy(s); Root.Destroy(u); Root.Destroy(w); }
        _tabs.Clear();
        _selected = 0;
        Publish();
    }

    /// <summary>Lays the tab buttons out left to right from the top-left corner.</summary>
    public void AlignTabButtons()
    {
        var x = _offset;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var (s, u, _, hidden) = _tabs[i];
            if (hidden || (_activeCount >= 0 && i >= _activeCount)) continue;
            FitText(s);
            FitText(u);
            foreach (var b in new[] { s, u })
            {
                b.RemoveAllAnchors();
                b.AddAnchor("TOPLEFT", this, "TOPLEFT", (double)x, 0.0);
            }
            x += Math.Max(s.NaturalSize.W, u.NaturalSize.W) + _gap;
        }
    }

    private void Refresh()
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            var (s, u, w, _) = _tabs[i];
            var active = IsActive(i);
            var isSelected = i + 1 == _selected;
            s.Show(active && isSelected);
            u.Show(active && !isSelected);
            w.Show(active && isSelected);
        }
    }

    /// <summary>Preserves a script-selected tab size until its visible label needs more room.</summary>
    private static void FitText(Widget button)
    {
        var needed = (float)Math.Ceiling(TextLayout.PlainWidth(button, button.GetText())
            + button.TextInset.Left + button.TextInset.Right);
        if (needed > button.NaturalSize.W) button.SetWidth(needed);
    }
}

/// <summary>A handler implemented in C# (for native widget behaviour wired through the same handler table).</summary>
public sealed class NativeHandler(Func<Widget, object?[], object?> body)
{
    public object? Invoke(Widget sender, object?[] args) => body(sender, args);
}
