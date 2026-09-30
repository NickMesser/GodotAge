#nullable enable
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>RGBA colour used by chat filters. Components are in the Lua widget range 0..1.</summary>
public sealed record X2ChatColor(double R, double G, double B, double A = 1d);

/// <summary>A persisted chat tab and all fields read by the client's chat option pages.</summary>
public sealed record X2ChatTab
{
    public int Id { get; init; }
    public int WindowId { get; init; }
    public string Name { get; init; } = "";
    public int FontSize { get; init; } = 15;
    public X2ChatColor BackgroundColor { get; init; } = new(0, 0, 0, 1);
    public IReadOnlyDictionary<int, bool> Filters { get; init; } = new Dictionary<int, bool>();
    public IReadOnlyDictionary<int, bool> LockedFilters { get; init; } = new Dictionary<int, bool>();
    public IReadOnlyDictionary<int, X2ChatColor> FilterColors { get; init; } = new Dictionary<int, X2ChatColor>();
    public bool Locked { get; init; }
}

/// <summary>Global chat window opacity settings.</summary>
public sealed record X2ChatOption(double LeaveAlpha = 0.5d, double OverAlpha = 0.7d);

/// <summary>Availability and optional currency/item cost of an outgoing chat channel.</summary>
public sealed record X2ChatChannel(
    int ChatType,
    string Name,
    bool Enabled = true,
    long Amount = 0,
    bool SpendMoney = false,
    uint ItemType = 0);

/// <summary>A channel shown by the separate megaphone composer.</summary>
public sealed record X2MegaphoneChannel(
    int ChatType,
    string Name,
    X2ChatColor FilterColor,
    uint SpendItemType = 0,
    int SpendItemCount = 0);

/// <summary>An input command/alias advertised to the native chat editor.</summary>
public sealed record X2ChatCommand(string Command, int ChatType);

/// <summary>A retained message in a GM one-to-one support channel.</summary>
public sealed record X2OneToOneMessage(string SpeakerName, string Message, bool IsGm = false);

/// <summary>A retained GM one-to-one support conversation.</summary>
public sealed record X2OneToOneChat(long Id, string TargetName, IReadOnlyList<X2OneToOneMessage> Messages);

public partial interface IX2SocialData
{
    IReadOnlyList<int> ChatWindowIds { get; }
    X2ChatTab? GetChatTab(int tabId);
    X2ChatTab? GetDefaultChatTab(int tabId);
    void AddChatTab(int windowId, string name);
    bool DeleteChatTab(int tabId);
    void RenameChatTab(int tabId, string name);
    bool ClearChatTab(int tabId);
    bool SetChatTabLocked(int tabId, bool locked);
    bool UpdateChatTab(X2ChatTab tab);
    void ResetChatWindows();
    X2ChatOption ChatOption { get; }
    void UpdateChatOption(X2ChatOption option);

    bool IsChatInputActive { get; }
    void OpenChatInput();
    void ActivateWhisperInput(string target, bool replyToLast);
    bool AppendChatInputLink(string kind, string value, string? secondaryValue = null);
    void DisplayChatMessage(int filter, string message);
    void DisplayCombatChatMessage(int targetFilter, int combatFilter, string message);
    bool ExpressEmotion(string text);

    IReadOnlyList<X2ChatChannel> ChatChannels { get; }
    IReadOnlyList<X2ChatCommand> ChatCommands { get; }
    IReadOnlyList<X2MegaphoneChannel> MegaphoneChannels { get; }
    bool UseMegaphoneChat { get; }
    int GetChatIconKind(ulong characterId, int chatType);
    string GetChatIcon(int iconKind);
    void CreateUserChatChannel(string channel, string password);
    void JoinUserChatChannel(string channel, string password);
    void LeaveUserChatChannel(string channel);
    void ReportChatSpammer(string targetName, string message, int chatType);
    void AcknowledgeMegaphoneItemWarning();

    IReadOnlyList<X2OneToOneChat> OneToOneChats { get; }
    void CloseOneToOneChat(long channelId);
    void SendOneToOneChat(long channelId, string message);
}

public partial class NullSocialData
{
    private readonly Dictionary<int, X2ChatTab> _chatTabs = CreateDefaultTabs();
    private X2ChatOption _chatOption = new();

    public virtual IReadOnlyList<int> ChatWindowIds => [0];
    public virtual X2ChatOption ChatOption => _chatOption;
    public virtual bool IsChatInputActive => false;
    public virtual bool UseMegaphoneChat => false;
    public virtual IReadOnlyList<X2OneToOneChat> OneToOneChats => [];

    public virtual IReadOnlyList<X2ChatChannel> ChatChannels { get; } =
    [
        new(0, "Say"), new(1, "Zone"), new(2, "Trade"), new(3, "Looking for Group"),
        new(4, "Party"), new(5, "Raid"), new(6, "Faction"), new(7, "Expedition"),
        new(9, "Family"), new(10, "Raid Command"), new(11, "Trial"), new(14, "Race"),
        new(17, "Squad"), new(18, "All Server"), new(19, "Local Server")
    ];

    public virtual IReadOnlyList<X2ChatCommand> ChatCommands { get; } =
    [
        new("/s", 0), new("/z", 1), new("/trade", 2), new("/lfg", 3),
        new("/p", 4), new("/raid", 5), new("/f", 6), new("/e", 7),
        new("/family", 9), new("/w", -3), new("/r", -5)
    ];

    public virtual IReadOnlyList<X2MegaphoneChannel> MegaphoneChannels => [];

    public virtual X2ChatTab? GetChatTab(int tabId) => _chatTabs.GetValueOrDefault(tabId);
    public virtual X2ChatTab? GetDefaultChatTab(int tabId) => CreateDefaultTabs().GetValueOrDefault(tabId);

    public virtual void AddChatTab(int windowId, string name)
    {
        var id = _chatTabs.Count == 0 ? 0 : _chatTabs.Keys.Max() + 1;
        _chatTabs[id] = CreateEmptyTab(id, windowId, name);
    }

    public virtual bool DeleteChatTab(int tabId) => tabId != 0 && _chatTabs.Remove(tabId);
    public virtual void RenameChatTab(int tabId, string name)
    {
        if (_chatTabs.TryGetValue(tabId, out var tab)) _chatTabs[tabId] = tab with { Name = name };
    }
    public virtual bool ClearChatTab(int tabId) => _chatTabs.ContainsKey(tabId);
    public virtual bool SetChatTabLocked(int tabId, bool locked)
    {
        if (!_chatTabs.TryGetValue(tabId, out var tab)) return false;
        _chatTabs[tabId] = tab with { Locked = locked };
        return true;
    }
    public virtual bool UpdateChatTab(X2ChatTab tab)
    {
        if (!_chatTabs.ContainsKey(tab.Id)) return false;
        _chatTabs[tab.Id] = tab;
        return true;
    }
    public virtual void ResetChatWindows()
    {
        _chatTabs.Clear();
        foreach (var (id, tab) in CreateDefaultTabs()) _chatTabs[id] = tab;
        _chatOption = new X2ChatOption();
    }
    public virtual void UpdateChatOption(X2ChatOption option) => _chatOption = option;

    public virtual void OpenChatInput() { }
    public virtual void ActivateWhisperInput(string target, bool replyToLast) { }
    public virtual bool AppendChatInputLink(string kind, string value, string? secondaryValue = null) => false;
    public virtual void DisplayChatMessage(int filter, string message) { }
    public virtual void DisplayCombatChatMessage(int targetFilter, int combatFilter, string message) { }
    public virtual bool ExpressEmotion(string text) => false;
    public virtual int GetChatIconKind(ulong characterId, int chatType) => 0;
    public virtual string GetChatIcon(int iconKind) => "";
    public virtual void CreateUserChatChannel(string channel, string password) { }
    public virtual void JoinUserChatChannel(string channel, string password) { }
    public virtual void LeaveUserChatChannel(string channel) { }
    public virtual void ReportChatSpammer(string targetName, string message, int chatType) { }
    public virtual void AcknowledgeMegaphoneItemWarning() { }
    public virtual void CloseOneToOneChat(long channelId) { }
    public virtual void SendOneToOneChat(long channelId, string message) { }

    private static X2ChatTab CreateEmptyTab(int id, int windowId, string name)
    {
        var filters = Enumerable.Range(1, 77).ToDictionary(x => x, _ => true);
        var locked = Enumerable.Range(1, 77).ToDictionary(x => x, _ => false);
        var colors = Enumerable.Range(1, 77).ToDictionary(x => x, _ => new X2ChatColor(1, 1, 1, 1));
        return new X2ChatTab
        {
            Id = id, WindowId = windowId, Name = name,
            Filters = filters, LockedFilters = locked, FilterColors = colors
        };
    }

    private static Dictionary<int, X2ChatTab> CreateDefaultTabs()
    {
        static X2ChatColor Color(int filter) => filter switch
        {
            12 or 13 => new(1, 0.82, 0.2), 9 => new(1, 0.65, 0.25), 4 => new(0.45, 1, 0.65),
            5 or 51 => new(0.75, 0.58, 1), 8 => new(0.55, 0.78, 1), 7 or 53 => new(0.35, 0.85, 1),
            6 => new(0.35, 1, 0.65), 3 or 50 => new(1, 0.55, 0.85), _ => new(0.95, 0.95, 0.95)
        };
        static X2ChatTab Tab(int id, string name, params int[] enabled)
        {
            var filters = Enumerable.Range(1, 77).ToDictionary(x => x,
                x => enabled.Length == 0 || enabled.Contains(x));
            return new X2ChatTab
            {
                Id = id, WindowId = 0, Name = name, Filters = filters,
                LockedFilters = Enumerable.Range(1, 77).ToDictionary(x => x, _ => false),
                FilterColors = Enumerable.Range(1, 77).ToDictionary(x => x, Color),
                BackgroundColor = new X2ChatColor(0, 0, 0, 0.62)
            };
        }
        return new Dictionary<int, X2ChatTab>
        {
            [0] = Tab(0, "All"),
            [1] = Tab(1, "Party", 4, 5, 11, 12, 51),
            [2] = Tab(2, "Trade", 9, 11, 12)
        };
    }
}

public static partial class X2SocialApi
{
    internal static void InstallChat(X2LuaHost host, X2GameContext context, IX2SocialData data)
    {
        var urlText = "";
        var urlAddress = "";

        host.Root.ChatTabs = windowId => data.ChatWindowIds.Contains(windowId)
            ? Enumerable.Range(0, 256).Select(data.GetChatTab).Where(t => t?.WindowId == windowId).Select(t =>
                new ChatTabDefinition(t!.Id, t.Name, t.Filters,
                    t.FilterColors.ToDictionary(x => x.Key, x => new UiColor((float)x.Value.R, (float)x.Value.G, (float)x.Value.B, (float)x.Value.A))))
                .ToArray()
            : [];
        host.Root.ChatChannels = () => data.ChatChannels.Where(c => c.Enabled).Select(c =>
            new ChatChannelDefinition(c.ChatType, c.Name,
                data.ChatCommands.Where(x => x.ChatType == c.ChatType).Select(x => x.Command).ToArray())).ToArray();
        void RefreshWindows()
        {
            foreach (var window in host.Root.AllWidgets().OfType<ChatWindowWidget>()) window.RefreshTabs();
        }

        host.Define("X2Chat", "ActivateWhisperChatInput", a =>
        {
            var value = a[0];
            data.ActivateWhisperInput(value as string ?? "", value is bool b && b);
            return null;
        });
        host.Define("X2Chat", "AddCraftLinkToActiveChatInput", a => data.AppendChatInputLink("craft", a.Str(0) ?? a.Num(0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        host.Define("X2Chat", "AddItemLinkToActiveChatInput", a => data.AppendChatInputLink("item", a.Str(0) ?? ""));
        host.Define("X2Chat", "AddNewChatTabByUser", a => { data.AddChatTab(a.Int(0), a.Str(1) ?? ""); RefreshWindows(); return null; });
        host.Define("X2Chat", "AddQuestLinkToActiveChatInput", a => data.AppendChatInputLink("quest", a.Str(0) ?? ""));
        host.Define("X2Chat", "AddRaidRecruitLinkToActiveChatInput", a => data.AppendChatInputLink("raidRecruit", a.Str(0) ?? ""));
        host.Define("X2Chat", "AddSquadRecruitLinkToActiveChatInput", a => data.AppendChatInputLink("squadRecruit", a.Str(0) ?? ""));
        host.Define("X2Chat", "AddUrlLinkToActiveChatInput", _ => data.AppendChatInputLink("url", urlText, urlAddress));
        host.Define("X2Chat", "AllChatWindowIds", _ => ChatArray(data.ChatWindowIds, x => (object?)(double)x));
        host.Define("X2Chat", "ClearChatContentByUser", a => data.ClearChatTab(a.Int(0)));
        host.Define("X2Chat", "CreateUserChatChannel", a => { data.CreateUserChatChannel(a.Str(0) ?? "", a.Str(1) ?? ""); return null; });
        host.Define("X2Chat", "DeleteChatTabByUser", a => { var result = data.DeleteChatTab(a.Int(0)); if (result) RefreshWindows(); return result; });
        host.Define("X2Chat", "DispatchChatMessage", a =>
        {
            var filter = a.Int(0);
            var message = a.Str(1) ?? "";
            data.DisplayChatMessage(filter, message);
            foreach (var window in host.Root.AllWidgets().OfType<ChatWindowWidget>())
                window.AddFilteredMessage(filter, null, message);
            return null;
        });
        host.Define("X2Chat", "DispatchCombatChatMessage", a => { data.DisplayCombatChatMessage(a.Int(0), a.Int(1), a.Str(2) ?? ""); return null; });
        host.Define("X2Chat", "ExpressEmotion", a => data.ExpressEmotion(a.Str(0) ?? ""));
        host.Define("X2Chat", "GetChatChannelInfo", a => data.ChatChannels.FirstOrDefault(x => x.ChatType == a.Int(0)) is { } c ? ChatChannelTable(c) : null);
        host.Define("X2Chat", "GetChatChannelName", a => data.ChatChannels.FirstOrDefault(x => x.ChatType == a.Int(0))?.Name ?? "");
        host.Define("X2Chat", "GetChatCommands", _ => ChatArray(data.ChatCommands, ChatCommandTable));
        host.Define("X2Chat", "GetChatIcon", a => data.GetChatIcon(a.Int(0)));
        host.Define("X2Chat", "GetChatIconKind", a => (double)data.GetChatIconKind((ulong)Math.Max(0, a.Num(0)), a.Int(1)));
        host.Define("X2Chat", "GetChatOption", _ => ChatOptionTable(data.ChatOption));
        host.Define("X2Chat", "GetChatTabInfoTable", a => data.GetChatTab(a.Int(0)) is { } tab ? ChatTabTable(tab) : null);
        host.Define("X2Chat", "GetDefaultChatTabInfoTable", a => data.GetDefaultChatTab(a.Int(0)) is { } tab ? ChatTabTable(tab) : null);
        host.Define("X2Chat", "GetMegaphoneChannelInfos", _ => ChatArray(data.MegaphoneChannels, ChatMegaphoneTable));
        host.Define("X2Chat", "GetUrlTextAddr", _ => new LuaTable { ["text"] = urlText, ["addr"] = urlAddress });
        host.Define("X2Chat", "InitChatWindow", _ => { data.ResetChatWindows(); RefreshWindows(); return null; });
        host.Define("X2Chat", "IsActivatedChatInput", _ => data.IsChatInputActive);
        host.Define("X2Chat", "IsEnableChatChannel", a => data.ChatChannels.FirstOrDefault(x => x.ChatType == a.Int(0))?.Enabled ?? false);
        host.Define("X2Chat", "IsLockedChatWindowByChatTabId", a => data.GetChatTab(a.Int(0))?.Locked ?? false);
        host.Define("X2Chat", "JoinUserChatChannel", a => { data.JoinUserChatChannel(a.Str(0) ?? "", a.Str(1) ?? ""); return null; });
        host.Define("X2Chat", "LeaveUserChatChannel", a => { data.LeaveUserChatChannel(a.Str(0) ?? ""); return null; });
        host.Define("X2Chat", "LockChatWindowByTabId", a => data.SetChatTabLocked(a.Int(0), a.Bool(1)));
        host.Define("X2Chat", "OpenChat", _ => { data.OpenChatInput(); return null; });
        host.Define("X2Chat", "RenameChatTabByUser", a => { data.RenameChatTab(a.Int(0), a.Str(1) ?? ""); RefreshWindows(); return null; });
        host.Define("X2Chat", "ReportSpammer", a => { data.ReportChatSpammer(a.Str(0) ?? "", a.Str(1) ?? "", a.Int(2)); return null; });
        host.Define("X2Chat", "SetMegaphoneWarningMsgState", _ => { data.AcknowledgeMegaphoneItemWarning(); return null; });
        host.Define("X2Chat", "SetUrlTextAddr", a =>
        {
            var text = (a.Str(0) ?? "").Trim();
            var address = (a.Str(1) ?? "").Trim();
            if (text.Length == 0 || address.Length == 0 ||
                !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
            urlText = text;
            urlAddress = address;
            return true;
        });
        host.Define("X2Chat", "UpdateChatOption", a =>
        {
            var t = a.Table(0);
            if (t != null) data.UpdateChatOption(new X2ChatOption(ChatNumber(t, "leaveAlpha", data.ChatOption.LeaveAlpha), ChatNumber(t, "overAlpha", data.ChatOption.OverAlpha)));
            return null;
        });
        host.Define("X2Chat", "UpdateChatTabFilter", a =>
        {
            if (data.GetChatTab(a.Int(0)) is not { } old) return null;
            data.UpdateChatTab(old with
            {
                Filters = ChatReadBools(a.Table(1), old.Filters),
                FilterColors = ChatReadColors(a.Table(2), old.FilterColors)
            });
            RefreshWindows();
            return null;
        });
        host.Define("X2Chat", "UpdateChatTabInfo", a =>
        {
            var id = a.Int(0);
            var result = data.GetChatTab(id) is { } old && a.Table(1) is { } table && data.UpdateChatTab(ChatReadTab(id, table, old));
            if (result) RefreshWindows();
            return result;
        });
        host.Define("X2Chat", "UseMegaphone", _ => data.UseMegaphoneChat);

        host.Define("X2OneAndOneChat", "GetChatDataList", _ => ChatArray(data.OneToOneChats, ChatOneToOneTable));
        host.Define("X2OneAndOneChat", "OnChatClosed", a => { data.CloseOneToOneChat((long)a.Num(0)); return null; });
        host.Define("X2OneAndOneChat", "OnChatEntered", a => { data.SendOneToOneChat((long)a.Num(0), a.Str(1) ?? ""); return null; });
    }

    private static LuaTable ChatTabTable(X2ChatTab tab) => new()
    {
        ["id"] = (double)tab.Id,
        ["windowId"] = (double)tab.WindowId,
        ["name"] = tab.Name,
        ["fontSize"] = (double)tab.FontSize,
        ["bgColor"] = ChatColorTable(tab.BackgroundColor),
        ["filters"] = ChatMap(tab.Filters, x => x),
        ["filterLocked"] = ChatMap(tab.LockedFilters, x => x),
        ["filterColors"] = ChatMap(tab.FilterColors, ChatColorTable)
    };

    private static LuaTable ChatOptionTable(X2ChatOption option) => new() { ["leaveAlpha"] = option.LeaveAlpha, ["overAlpha"] = option.OverAlpha };
    private static LuaTable ChatColorTable(X2ChatColor color) => new() { ["r"] = color.R, ["g"] = color.G, ["b"] = color.B, ["a"] = color.A, [1d] = color.R, [2d] = color.G, [3d] = color.B, [4d] = color.A };
    private static LuaTable ChatChannelTable(X2ChatChannel c) => new() { ["chatType"] = (double)c.ChatType, ["name"] = c.Name, ["amount"] = (double)c.Amount, ["spendMoney"] = c.SpendMoney, ["itemType"] = (double)c.ItemType };
    private static LuaTable ChatCommandTable(X2ChatCommand c) => new() { ["command"] = c.Command, ["chatType"] = (double)c.ChatType };
    private static LuaTable ChatMegaphoneTable(X2MegaphoneChannel c) => new() { ["chatType"] = (double)c.ChatType, ["name"] = c.Name, ["filterColor"] = ChatColorTable(c.FilterColor), ["spendItemType"] = (double)c.SpendItemType, ["spendItemCount"] = (double)c.SpendItemCount };
    private static LuaTable ChatOneToOneTable(X2OneToOneChat chat) => new() { ["id"] = (double)chat.Id, ["targetName"] = chat.TargetName, ["messages"] = ChatArray(chat.Messages, ChatOneToOneMessageTable) };
    private static LuaTable ChatOneToOneMessageTable(X2OneToOneMessage message) => new() { ["speakerName"] = message.SpeakerName, ["message"] = message.Message, ["isGm"] = message.IsGm };

    private static LuaTable ChatArray<T>(IEnumerable<T> values, Func<T, object?> convert)
    {
        var result = new LuaTable();
        var index = 1d;
        foreach (var value in values) result[index++] = convert(value);
        return result;
    }

    private static LuaTable ChatMap<T>(IReadOnlyDictionary<int, T> values, Func<T, object?> convert)
    {
        var result = new LuaTable();
        foreach (var (key, value) in values) result[(double)key] = convert(value);
        return result;
    }

    private static X2ChatTab ChatReadTab(int id, LuaTable table, X2ChatTab old) => old with
    {
        Name = ChatString(table, "name", old.Name),
        FontSize = (int)ChatNumber(table, "fontSize", old.FontSize),
        BackgroundColor = ChatReadColor(ChatValueTable(table, "bgColor"), old.BackgroundColor),
        Filters = ChatReadBools(ChatValueTable(table, "filters"), old.Filters),
        LockedFilters = ChatReadBools(ChatValueTable(table, "filterLocked"), old.LockedFilters),
        FilterColors = ChatReadColors(ChatValueTable(table, "filterColors"), old.FilterColors)
    };

    private static IReadOnlyDictionary<int, bool> ChatReadBools(LuaTable? table, IReadOnlyDictionary<int, bool> fallback)
    {
        if (table == null) return fallback;
        var result = fallback.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (key, value) in table)
            if (ChatKeyInt(key) is { } i && value is bool b) result[i] = b;
        return result;
    }

    private static IReadOnlyDictionary<int, X2ChatColor> ChatReadColors(LuaTable? table, IReadOnlyDictionary<int, X2ChatColor> fallback)
    {
        if (table == null) return fallback;
        var result = fallback.ToDictionary(x => x.Key, x => x.Value);
        foreach (var (key, value) in table)
            if (ChatKeyInt(key) is { } i && value is LuaTable color) result[i] = ChatReadColor(color, result.GetValueOrDefault(i, new X2ChatColor(1, 1, 1, 1)));
        return result;
    }

    private static X2ChatColor ChatReadColor(LuaTable? table, X2ChatColor fallback) => table == null ? fallback : new(
        ChatNumber(table, "r", ChatNumber(table, 1d, fallback.R)), ChatNumber(table, "g", ChatNumber(table, 2d, fallback.G)),
        ChatNumber(table, "b", ChatNumber(table, 3d, fallback.B)), ChatNumber(table, "a", ChatNumber(table, 4d, fallback.A)));

    private static int? ChatKeyInt(object key) => key switch { double d => (int)d, int i => i, _ => null };
    private static LuaTable? ChatValueTable(LuaTable table, object key) => table.TryGetValue(key, out var value) ? value as LuaTable : null;
    private static string ChatString(LuaTable table, object key, string fallback) => table.TryGetValue(key, out var value) && value is string s ? s : fallback;
    private static double ChatNumber(LuaTable table, object key, double fallback) => table.TryGetValue(key, out var value) ? value switch
    {
        double d => d, float f => f, int i => i, long l => l, _ => fallback
    } : fallback;
}
