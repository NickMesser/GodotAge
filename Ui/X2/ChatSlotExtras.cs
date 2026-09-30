#nullable enable
using System;
using System.Collections.Generic;

namespace AAEmu.GodotViewer.Ui.X2;

/// <summary>A chat-tab definition supplied by X2Chat's persisted settings.</summary>
public sealed record ChatTabDefinition(
    int Id, string Name, IReadOnlyDictionary<int, bool> Filters,
    IReadOnlyDictionary<int, UiColor> Colors);

/// <summary>An outgoing channel offered by the native chat editor.</summary>
public sealed record ChatChannelDefinition(int ChatType, string Name, IReadOnlyList<string> Commands);

/// <summary>The composite native chat window. Lua supplies its skin and the X2Chat API supplies its tabs.</summary>
public sealed class ChatWindowWidget : MessageWidget
{
    internal ChatWindowWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }

    public double TabWidth { get; private set; }
    public double TabAreaHeight { get; private set; }
    public double MinTabWidth { get; private set; }
    public double Offset { get; private set; }
    public (double X, double Y) CaretOffset { get; private set; }
    public bool AutoResizingTabButtons { get; private set; }
    public (double Width, double Height) MinResizingExtent { get; private set; }
    public (double Width, double Height) MaxResizingExtent { get; private set; }
    public UiInsets TabAreaInset { get; private set; }
    public bool Resizing { get; private set; }
    public double SlideTimeInDragging { get; private set; }
    public bool TabSwitchAllowed { get; private set; }
    public UiInsets ResizingBorderSize { get; private set; }
    public bool AddTabButtonUsed { get; private set; }
    public object? ChatWindowId { get; private set; }
    public double MaxNotifyTime { get; private set; }
    public double NotifyBlinkingFrequency { get; private set; }

    private TextureDrawable? _lock, _caret;
    private Widget? _add, _ime, _url;
    private ChatEditWidget? _edit;
    private ChatMethodSelectorWidget? _selector;
    private MessageWidget? _message;
    private readonly List<(int Filter, string Text, UiColor Color)> _history = [];
    private readonly List<(ButtonWidget Button, ChatTabDefinition Tab)> _tabs = [];
    private readonly Dictionary<int, bool> _tabSelectionStates = [];
    private int _selectedTabId = -1;
    private int _chatType;
    private float _resizeStartX, _resizeStartY, _resizeWidth, _resizeHeight;
    private bool _resizeTop, _resizeRight;

    /// <summary>Raised after the editor has applied a channel prefix and accepted a non-empty line.</summary>
    public event Action<int, string>? ChatSubmitted;
    public IReadOnlyList<ButtonWidget> TabButtons => _tabs.Select(x => x.Button).ToArray();
    public int SelectedTabId => _selectedTabId;
    public int ActiveChatType => _chatType;

    internal override void CreateNativeChildren()
    {
        _lock = CreateImageDrawable("", "overlay");
        _caret = CreateImageDrawable("", "overlay");
        _add = Root.CreateWidget("button", "addButton", this);
        _ime = Root.CreateWidget("button", "imeToggleButton", this);
        _url = Root.CreateWidget("button", "urlButton", this);
        _edit = (ChatEditWidget)Root.CreateWidget("chatedit", "chatEdit", this);
        _selector = (ChatMethodSelectorWidget)Root.CreateWidget("chatmethodselector", "chatMethodSelector", this);
        _message = (MessageWidget)Root.CreateWidget("message", "chatMessage", this);
        _message.AddAnchor("TOPLEFT", this, 0.0, 30.0);
        _message.AddAnchor("BOTTOMRIGHT", this, 0.0, -24.0);
        _add.InitVisible(false);
        _ime.InitVisible(false);
        _url.InitVisible(false);
        _edit.InitVisible(false);
        _selector.InitVisible(false);
        _edit.SetHandler("OnEnterPressed", new NativeHandler((_, _) => { SubmitInput(); return null; }));
        _edit.SetHandler("OnEscapePressed", new NativeHandler((_, _) => { DeactivateInput(); return null; }));
        _add.SetHandler("OnClick", new NativeHandler((_, _) =>
        {
            Root.NativeCallback?.Invoke("OnClickedChatTabAddButton", [ChatWindowId]);
            return true;
        }));
    }

    public TextureDrawable GetLockNotifyDrawable() => _lock!;
    public TextureDrawable GetCaretDrawable() => _caret!;
    public Widget GetAddButton() => _add!;
    public Widget GetImeToggleButton() => _ime!;
    public Widget GetUrlButton() => _url!;
    public ChatEditWidget GetChatEdit() => _edit!;
    public ChatMethodSelectorWidget GetChatMethodSelector() => _selector!;
    public MessageWidget GetChatMessage() => _message!;
    public MessageWidget GetMessageWidget() => _message!;

    public void SetTabWidth(double value) => TabWidth = value;
    public void SetTabAreaHeight(double value)
    {
        TabAreaHeight = value;
        if (_message != null)
        {
            _message.RemoveAllAnchors();
            _message.AddAnchor("TOPLEFT", this, 0.0, value);
            _message.AddAnchor("BOTTOMRIGHT", this, 0.0, 0.0);
        }
    }
    public void SetMinTabWidth(double value) => MinTabWidth = value;
    public void SetOffset(double value) => Offset = value;
    public void SetCaretOffset(double x, double y) => CaretOffset = (x, y);
    public void UseAutoResizingTabButtonMode(bool value) => AutoResizingTabButtons = value;
    public void SetMinResizingExtent(double width, double height) => MinResizingExtent = (width, height);
    public void SetMaxResizingExtent(double width, double height) => MaxResizingExtent = (width, height);
    public void SetTabAreaInset(double left, double top, double right, double bottom) => TabAreaInset = new((float)left, (float)top, (float)right, (float)bottom);
    public void UseResizing(bool value) => Resizing = value;
    public void SetSlideTimeInDragging(double value) => SlideTimeInDragging = value;
    public void AllowTabSwitch(bool value) => TabSwitchAllowed = value;
    public void SetResizingBorderSize(double left, double top, double right, double bottom) => ResizingBorderSize = new((float)left, (float)top, (float)right, (float)bottom);
    public void UseAddTabButton(bool value) { AddTabButtonUsed = value; _add?.Show(value); LayoutTabs(); }
    public void SetChatWindowId(object? value)
    {
        ChatWindowId = value;
        var id = value switch { double d => (int)d, int i => i, _ => 0 };
        if (_edit != null)
        {
            _edit.RemoveAllAnchors();
            _edit.AddAnchor("TOPLEFT", this, "BOTTOMLEFT", 2.0, 2.0);
            _edit.SetExtent(Math.Max(1, GetWidth() - 49), 26);
        }
        if (_selector != null && _selector.GetWidth() <= 0) _selector.SetWidth(50);
        BuildTabs(Root.ChatTabs?.Invoke(id) ?? []);
        BuildChannels(Root.ChatChannels?.Invoke() ?? []);
    }
    public void SetMaxNotifyTime(double value) => MaxNotifyTime = value;
    public void SetNotifyBlinkingFreq(double value) => NotifyBlinkingFrequency = value;

    /// <summary>Rebuilds buttons and filters from the current X2Chat settings.</summary>
    public void RefreshTabs()
    {
        var id = ChatWindowId switch { double d => (int)d, int i => i, _ => 0 };
        BuildTabs(Root.ChatTabs?.Invoke(id) ?? []);
    }

    public override void SetExtent(float width, float height)
    {
        if (Resizing)
        {
            if (MinResizingExtent.Width > 0) width = Math.Max(width, (float)MinResizingExtent.Width);
            if (MinResizingExtent.Height > 0) height = Math.Max(height, (float)MinResizingExtent.Height);
            if (MaxResizingExtent.Width > 0) width = Math.Min(width, (float)MaxResizingExtent.Width);
            if (MaxResizingExtent.Height > 0) height = Math.Min(height, (float)MaxResizingExtent.Height);
        }
        base.SetExtent(width, height);
        _edit?.SetWidth(Math.Max(1, width - 49));
        LayoutTabs();
    }

    internal bool BeginResizeDrag(float x, float y)
    {
        if (!Resizing) return false;
        var rect = ScreenRect;
        var top = ResizingBorderSize.Top > 0 ? ResizingBorderSize.Top : 11;
        var right = ResizingBorderSize.Right > 0 ? ResizingBorderSize.Right : 12;
        _resizeTop = y >= rect.Y && y <= rect.Y + top;
        _resizeRight = x >= rect.Right - right && x <= rect.Right;
        if (!_resizeTop && !_resizeRight) return false;
        _resizeStartX = x;
        _resizeStartY = y;
        _resizeWidth = rect.Width;
        _resizeHeight = rect.Height;
        return true;
    }

    internal void ResizeDrag(float x, float y)
    {
        var width = _resizeRight ? _resizeWidth + x - _resizeStartX : _resizeWidth;
        var height = _resizeTop ? _resizeHeight - (y - _resizeStartY) : _resizeHeight;
        SetExtent(width, height);
    }

    /// <summary>Adds one incoming server chat line, mapping the channel enum to the client's CMF filter.</summary>
    /// <summary>The chat background opacity while the cursor is outside / over the window (X2Chat option leaveAlpha / overAlpha).</summary>
    public double LeaveAlpha { get; set; } = 0.5;
    public double OverAlpha { get; set; } = 0.7;

    /// <summary>The client tints the message background black with the leave/over alpha of the chat option.</summary>
    internal override void Tick(double ms)
    {
        base.Tick(ms);
        if (_message == null) return;
        var over = ScreenRect.Contains(Root.CursorX, Root.CursorY);
        var tint = new UiColor(0, 0, 0, (float)(over ? OverAlpha : LeaveAlpha));
        foreach (var d in _message.Drawables)
            if (d is TextureDrawable { Layer: "background" } bg) bg.SetTintValue(tint);
    }

    public void AddChatMessage(int chatType, string? sender, string? text)
        => AddFilteredMessage(ChatTypeToFilter(chatType), sender, text);

    /// <summary>Adds a line that already uses one of the client's CMF_* filter ids.</summary>
    public void AddFilteredMessage(int filter, string? sender, string? text)
    {
        var output = string.IsNullOrEmpty(sender) ? text ?? "" : $"[{sender}]: {text}";
        var tab = SelectedTab();
        var color = tab?.Colors.GetValueOrDefault(filter) ?? DefaultColor(filter);
        _history.Add((filter, output, color));
        if (_history.Count > 300) _history.RemoveRange(0, _history.Count - 300);
        if (tab == null || tab.Filters.GetValueOrDefault(filter)) _message?.AddMessage(output, color);
    }

    public void ActivateInput()
    {
        if (_edit == null || _selector == null) return;
        _edit.Show(true);
        _selector.Show(true);
        _edit.Raise();
        _selector.Raise();
        _edit.SetFocus();
    }

    public void DeactivateInput()
    {
        _edit?.Show(false);
        _selector?.Show(false);
        if (_edit != null && Root.Focused == _edit) Root.SetFocusTo(null);
    }

    private void SubmitInput()
    {
        if (_edit == null) return;
        var text = _edit.GetText().Trim();
        if (text.Length > 0)
        {
            // The chat Lua/native command layer owns slash aliases and channel prefixes. This event reports the
            // selector's current channel and the accepted editor text without reinterpreting it a second time.
            ChatSubmitted?.Invoke(_chatType, text);
            Root.ChatSubmit?.Invoke(_chatType, text);
        }
        _edit.SetText("");
        DeactivateInput();
    }

    private void BuildChannels(IReadOnlyList<ChatChannelDefinition> channels)
    {
        if (_selector == null) return;
        var list = _selector.GetList();
        foreach (var child in list.Children.Where(x => x.Id.StartsWith("channel[", StringComparison.Ordinal)).ToArray())
            Root.Destroy(child);
        list.ClearItems();
        for (var i = 0; i < channels.Count; i++)
        {
            var index = i;
            var channel = channels[i];
            list.AppendItem(channel.Name, (double)channel.ChatType);
            var row = (ButtonWidget)Root.CreateWidget("button", $"channel[{i + 1}]", list);
            row.SetText(channel.Name);
            row.AddAnchor("TOPLEFT", list, 4.0, 4.0 + i * 23.0);
            row.SetExtent(142, 23);
            row.SetHandler("OnClick", new NativeHandler((_, _) => { SelectChannel(index); return true; }));
        }
        list.SetExtent(150, Math.Min(360, Math.Max(30, channels.Count * 23 + 8)));
        _selector.SetHandler("OnClick", new NativeHandler((_, _) =>
        {
            _selector.Raise();
            list.Show(!list.Visible);
            return true;
        }));
        if (channels.Count == 0) return;
        _chatType = channels[0].ChatType;
        _selector.SetText(channels[0].Name);
        void SelectChannel(int index)
        {
            if (index < 0 || index >= channels.Count) return;
            _chatType = channels[index].ChatType;
            _selector.SetText(channels[index].Name);
            _selector.InvokeHandler("OnContentUpdated", "chat_type", null, (double)_chatType);
            list.Show(false);
            _edit?.SetFocus();
        }
        list.SetHandler("OnSelChanged", new NativeHandler((_, args) =>
        {
            var index = args.Length > 0 && args[0] is double d ? (int)d : list.GetSelectedIndex();
            SelectChannel(index - 1);
            return null;
        }));
    }

    private void BuildTabs(IReadOnlyList<ChatTabDefinition> tabs)
    {
        var previous = _selectedTabId;
        foreach (var (button, _) in _tabs) Root.Destroy(button);
        _tabs.Clear();
        _tabSelectionStates.Clear();
        foreach (var tab in tabs)
        {
            var button = (ButtonWidget)Root.CreateWidget("button", $"chatTab[{tab.Id}]", this);
            button.SetText(tab.Name);
            Root.NativeCallback?.Invoke("OnCreateChatTabButton", [button]);
            Root.NativeCallback?.Invoke("OnSetChatTabId", [button, (double)tab.Id]);
            button.SetHandler("OnClick", new NativeHandler((_, _) => { SelectTab(tab.Id); return true; }));
            _tabs.Add((button, tab));
        }
        LayoutTabs();
        if (_tabs.Count > 0) SelectTab(_tabs.Any(x => x.Tab.Id == previous) ? previous : _tabs[0].Tab.Id);
    }

    private void LayoutTabs()
    {
        var x = TabAreaInset.Left + (float)Offset;
        var height = (float)(TabAreaHeight > 0 ? TabAreaHeight : 30);
        var width = (float)(TabWidth > 0 ? TabWidth : 60);
        foreach (var (button, _) in _tabs)
        {
            button.RemoveAllAnchors();
            button.AddAnchor("TOPLEFT", this, (double)x, 0.0);
            button.SetExtent(width, height);
            x += width;
        }
        if (_add != null)
        {
            _add.RemoveAllAnchors();
            _add.AddAnchor("TOPLEFT", this, (double)x, 0.0);
            if (_add.GetWidth() <= 0 || _add.GetHeight() <= 0) _add.SetExtent(24, height);
        }
    }

    private void SelectTab(int id)
    {
        if (_tabs.All(x => x.Tab.Id != id)) return;
        _selectedTabId = id;
        foreach (var (button, tab) in _tabs)
        {
            var isChosen = tab.Id == id;
            if (!_tabSelectionStates.TryGetValue(tab.Id, out var wasChosen) || wasChosen != isChosen)
            {
                Root.NativeCallback?.Invoke("OnSelectedChatTab", [button, isChosen]);
                _tabSelectionStates[tab.Id] = isChosen;
            }
        }
        var selected = SelectedTab();
        _message?.Clear();
        if (_message != null && selected != null)
            foreach (var line in _history)
                if (selected.Filters.GetValueOrDefault(line.Filter)) _message.AddMessage(line.Text, line.Color);
    }

    private ChatTabDefinition? SelectedTab() => _tabs.FirstOrDefault(x => x.Tab.Id == _selectedTabId).Tab;

    public static int ChatTypeToFilter(int chatType) => chatType switch
    {
        0 => 2,   // CHAT_SAY -> CMF_SAY
        1 => 8,   // CHAT_ZONE -> CMF_ZONE
        2 => 9,   // CHAT_TRADE -> CMF_TRADE
        3 => 10,  // CHAT_FIND_PARTY -> CMF_FIND_PARTY
        4 => 4,   // CHAT_PARTY -> CMF_PARTY
        5 => 5,   // raid
        6 => 7,   // faction
        7 => 6,   // expedition
        9 => 50,  // family
        10 => 51, // raid command
        11 => 52, // trial
        13 => 57, // play music
        14 => 53, // race
        16 => 55, // small megaphone
        17 => 56, // squad
        18 => 60, // all-server
        19 => 77, // local-server
        -4 or -3 => 3, // whisper variants
        -1 => 11, // notice
        -2 or 8 => 12, // system
        _ => 12
    };

    private static UiColor DefaultColor(int filter) => filter switch
    {
        12 or 13 => new UiColor(1f, 0.82f, 0.2f, 1f),
        9 => new UiColor(1f, 0.65f, 0.25f, 1f),
        4 => new UiColor(0.45f, 1f, 0.65f, 1f),
        5 => new UiColor(0.75f, 0.58f, 1f, 1f),
        8 => new UiColor(0.55f, 0.78f, 1f, 1f),
        7 or 53 => new UiColor(0.35f, 0.85f, 1f, 1f),
        6 => new UiColor(0.35f, 1f, 0.65f, 1f),
        3 or 50 => new UiColor(1f, 0.55f, 0.85f, 1f),
        _ => new UiColor(0.95f, 0.95f, 0.95f, 1f)
    };
}

public sealed class ChatEditWidget : EditBoxWidget
{
    internal ChatEditWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent, false) { }
    /// <summary>Installed by the live UI host. Returns true after the line was sent.</summary>
    public static Func<string, bool>? SubmitLine { get; set; }
    internal bool Submit()
    {
        var line = GetText().Trim();
        if (line.Length == 0 || SubmitLine?.Invoke(line) != true) return false;
        SetText("");
        return true;
    }
    public UiInsets PrefixInset { get; private set; }
    public void SetPrefixInset(double left, double top, double right, double bottom)
        => PrefixInset = new((float)left, (float)top, (float)right, (float)bottom);
}

public sealed class ChatMethodSelectorWidget : ButtonWidget
{
    private ListBoxWidget? _list;
    internal ChatMethodSelectorWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent) { }
    internal override void CreateNativeChildren()
    {
        _list = (ListBoxWidget)Root.CreateWidget("listbox", "list", this);
        _list.InitVisible(false);
    }
    public ListBoxWidget GetList() => _list!;
    public void SetAutoClipChar(bool value) { }
}

/// <summary>Native slot bindings and script-visible state, with rendering inherited from SlotWidget.</summary>
public sealed class NativeSlotWidget : SlotWidget
{
    public string IconLayer { get; private set; } = "artwork";
    public bool DefaultClickDisabled { get; private set; }
    public void ChangeIconLayer(string layer)
    {
        IconLayer = layer;
        if (NativeFields.TryGetValue("icon", out var value) && value is TextureDrawable icon) icon.SetLayer(layer);
    }
    public void DisableDefaultClick() => DefaultClickDisabled = true;
    private readonly HashSet<string> _clickButtons = new(StringComparer.OrdinalIgnoreCase) { "LeftButton" };
    private object? _tooltip;
    private (string? Texture, string? Key, string? Color)? _cooldownMask;
    private string _boundType = "none";

    /// <summary>Key text for a slot (slot type, slot index) in the client's binding format, e.g. "SHIFT-1"; set by the host.</summary>
    public static Func<int, int, string?>? HotkeyResolver { get; set; }
    /// <summary>Extra look/paper info of the bound skill or item (skins, papers); none for plain skills.</summary>
    public object? GetExtraInfo() => null;

    public string GetHotKey(double slotType, double index) => HotkeyResolver?.Invoke((int)slotType, (int)index) ?? "";

    internal NativeSlotWidget(UiRoot root, string type, string id, Widget? parent) : base(root, type, id, parent)
    {
        var icon = CreateIconDrawable("artwork");
        icon.AddAnchor("TOPLEFT", this, 0.0, 0.0);
        icon.AddAnchor("BOTTOMRIGHT", this, 0.0, 0.0);
        icon.SetVisible(false);
        NativeFields["icon"] = icon;
    }

    public (string? Texture, string? Key, string? Color)? CoolDownMask => _cooldownMask;
    public string GetBindedType() => _boundType;
    public object? GetTooltip(object? slotType = null, object? index = null)
        => _tooltip ?? Root.ScriptFieldReader?.Invoke(this, "info");
    public void SetTooltip(object? tooltip) => _tooltip = tooltip;
    public void SetCoolDown(double remainingMs, double totalMs) => SetCooldown(remainingMs, totalMs);
    public void SetCoolDownMask(string? texture, string? key = null, string? color = null)
        => _cooldownMask = (texture, key, color);
    public void RegisterForClicks(string button, bool clickable = true)
    {
        if (clickable) _clickButtons.Add(button);
        else _clickButtons.Remove(button);
    }
    internal bool AcceptsClick(string button) => _clickButtons.Contains(button);

    public new void EstablishSlot(object? slotType, double slotIndex)
    {
        _boundType = slotType?.ToString() ?? "none";
        base.EstablishSlot(slotType, slotIndex);
    }
    public void EstablishVirtualSlot(object? slotType, double slotIndex, object? virtualSlot = null)
    {
        EstablishSlot(slotType, slotIndex);
    }
    public new void EstablishItem(params object?[] args) { base.EstablishItem(args); _boundType = "item"; }
    public new void EstablishSkill(params object?[] args) { base.EstablishSkill(args); _boundType = "skill"; }
    public new void EstablishSkillSlot(params object?[] args) { base.EstablishSkillSlot(args); _boundType = "skill"; }
    public new void ReleaseSlot() { base.ReleaseSlot(); _boundType = "none"; _tooltip = null; }
    internal void SetLiveBoundType(bool skill) => _boundType = skill ? "skill" : "none";
    internal void SetLiveBoundType(string type) => _boundType = type;
}
