#nullable enable
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>The three server-owned inventory containers used by the Items UI.</summary>
public enum X2ContainerKind { Bag, Bank, Coffer }

/// <summary>One live inventory instance. Fields carries optional client tooltip keys supplied by the host.</summary>
public sealed record X2InventoryItem
{
    public ulong Id { get; init; }
    public uint ItemType { get; init; }
    public int Grade { get; init; }
    public int Count { get; init; } = 1;
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public IReadOnlyDictionary<string, object?> Fields { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Client-side inventory tab; GroupTypes are item-group ids as used by the Lua filter.</summary>
public sealed record X2ContainerTab(string Description, int IconIndex, IReadOnlyList<int> GroupTypes);

/// <summary>A snapshot of a container. Slot keys are the client's 1-based real slot numbers.</summary>
public sealed record X2ContainerState
{
    public int Capacity { get; init; }
    public int ExpansionMaxSlots { get; init; }
    public int ExpandedSlots { get; init; }
    public string ExpansionCost { get; init; } = "0";
    /// <summary>UI currency selector (CURRENCY_GOLD=0, CURRENCY_AA_POINT=3, both=4), not the wallet balance.</summary>
    public int Currency { get; init; }
    public IReadOnlyDictionary<int, X2InventoryItem> Items { get; init; } = new Dictionary<int, X2InventoryItem>();
    /// <summary>Optional 1-based virtual-to-real slot order after server or tab sorting; empty means identity.</summary>
    public IReadOnlyList<int> VirtualSlots { get; init; } = [];
    public IReadOnlySet<int> LockedSlots { get; init; } = new HashSet<int>();
    public IReadOnlySet<int> PinnedSlots { get; init; } = new HashSet<int>();
    public IReadOnlyList<X2ContainerTab> Tabs { get; init; } = [];
    public int CurrentTab { get; init; } = 1;
    public string SearchKeyword { get; init; } = "";
    public bool Modified { get; init; }
    public bool IsBankerNpc { get; init; }
    public bool IsPrivateCoffer { get; init; }
    public bool IsMyHouseCoffer { get; init; }
    public bool IsManikin { get; init; }
    public string HouseCofferName { get; init; } = "";
    public int HouseCofferPermission { get; init; }
    public bool HouseCofferPermissionAvailable { get; init; }
    public bool AutoUseAaPoint { get; init; }
}

/// <summary>A requested client action. Arguments retain binding order and Lua values; the host translates to protocol commands.</summary>
public sealed record X2ItemsCommand(string Table, string Method, IReadOnlyList<object?> Arguments);

/// <summary>
/// Live inventory queries and actions required by the Items API. Partial declarations in the family files add
/// equipment, enchanting, loot and store queries without forcing unrelated network state into this contract.
/// Never treat the local snapshot as authority: only server responses change live items or money.
/// </summary>
public partial interface IX2ItemsData
{
    X2ContainerState GetContainer(X2ContainerKind kind);
    /// <summary>Returns the current item in a 1-based real slot, or null for an empty slot.</summary>
    X2InventoryItem? GetContainerItem(X2ContainerKind kind, int slot);
    /// <summary>Counts stacks of a template id in the character bag.</summary>
    int GetItemCount(uint itemType);
    /// <summary>Send an inventory request or change local tab/search presentation state.</summary>
    /// <returns>True when the request was accepted locally for dispatch; the server still decides its outcome.</returns>
    bool SendItemsCommand(X2ItemsCommand command);
}

/// <summary>Fresh character inventory with no items, NPC store, bank visit or house coffer.</summary>
public partial class NullItemsData : IX2ItemsData
{
    private readonly Dictionary<X2ContainerKind, X2ContainerState> _containers = new()
    {
        [X2ContainerKind.Bag] = new() { Capacity = 50, ExpansionMaxSlots = 150, ExpandedSlots = 60, ExpansionCost = "5000", Currency = 4, Tabs = [new X2ContainerTab("All", 1, [])] },
        [X2ContainerKind.Bank] = new() { Capacity = 50, ExpansionMaxSlots = 150, ExpandedSlots = 60, ExpansionCost = "5000", Currency = 4, Tabs = [new X2ContainerTab("All", 1, [])] },
        [X2ContainerKind.Coffer] = new(),
    };

    public virtual X2ContainerState GetContainer(X2ContainerKind kind) => _containers[kind];
    public virtual X2InventoryItem? GetContainerItem(X2ContainerKind kind, int slot)
        => _containers[kind].Items.TryGetValue(slot, out var item) ? item : null;
    public virtual int GetItemCount(uint itemType) => _containers[X2ContainerKind.Bag].Items.Values
        .Where(item => item.ItemType == itemType).Sum(item => item.Count);

    public virtual bool SendItemsCommand(X2ItemsCommand command)
    {
        var kind = command.Table switch
        {
            "X2Bag" => X2ContainerKind.Bag,
            "X2Bank" => X2ContainerKind.Bank,
            "X2Coffer" => X2ContainerKind.Coffer,
            _ => (X2ContainerKind?)null,
        };
        if (kind is null) return false; // Offline server-owned actions are unavailable.
        var state = _containers[kind.Value];
        var first = command.Arguments.Count > 0 ? command.Arguments[0] : null;
        var index = first is double d ? (int)d : 0;
        switch (command.Method)
        {
            case "SetSearchKeyword": _containers[kind.Value] = state with { SearchKeyword = first as string ?? "" }; return true;
            case "Confirm": _containers[kind.Value] = state with { Modified = false }; return true;
            case "SwitchTab" when index >= 1 && index <= state.Tabs.Count:
                _containers[kind.Value] = state with { CurrentTab = index }; return true;
            case "CreateTab":
                var groups = command.Arguments.Count > 2 && command.Arguments[2] is LuaTable table
                    ? table.Values.OfType<double>().Select(v => (int)v).ToArray() : [];
                _containers[kind.Value] = state with { Tabs = state.Tabs.Append(new X2ContainerTab(
                    command.Arguments.Count > 1 ? command.Arguments[1]?.ToString() ?? "" : "", index, groups)).ToArray(), Modified = true };
                return true;
            case "EditTab" when index > 1 && index <= state.Tabs.Count:
                var edited = state.Tabs.ToArray();
                var editedGroups = command.Arguments.Count > 3 && command.Arguments[3] is LuaTable editTable
                    ? editTable.Values.OfType<double>().Select(v => (int)v).ToArray() : [];
                edited[index - 1] = new X2ContainerTab(
                    command.Arguments.Count > 2 ? command.Arguments[2]?.ToString() ?? "" : "",
                    command.Arguments.Count > 1 && command.Arguments[1] is double icon ? (int)icon : 0,
                    editedGroups);
                _containers[kind.Value] = state with { Tabs = edited, Modified = true };
                return true;
            case "RemoveTab" when index > 1 && index <= state.Tabs.Count:
                _containers[kind.Value] = state with { Tabs = state.Tabs.Where((_, i) => i != index - 1).ToArray(), CurrentTab = 1, Modified = true }; return true;
            case "LockSlot" when index >= 1:
                _containers[kind.Value] = state with { LockedSlots = state.LockedSlots.Append(index).ToHashSet() }; return true;
            case "UnlockSlot" when index >= 1:
                _containers[kind.Value] = state with { LockedSlots = state.LockedSlots.Where(x => x != index).ToHashSet() }; return true;
            case "SetPin" when kind == X2ContainerKind.Bag && index >= 1:
                _containers[kind.Value] = state with { PinnedSlots = state.PinnedSlots.Append(index).ToHashSet() }; return true;
            case "RemovePin" when kind == X2ContainerKind.Bag && index >= 1:
                _containers[kind.Value] = state with { PinnedSlots = state.PinnedSlots.Where(x => x != index).ToHashSet() }; return true;
            case "RemoveAllPin" when kind == X2ContainerKind.Bag:
                _containers[kind.Value] = state with { PinnedSlots = new HashSet<int>() }; return true;
            case "SetAutoUseAAPoint" when kind == X2ContainerKind.Bag:
                _containers[kind.Value] = state with { AutoUseAaPoint = first is not (null or false) };
                return true;
        }
        return false;
    }
}

/// <summary>Registers every method of the fourteen item-related X2 namespaces.</summary>
public static partial class X2ItemsApi
{
    public static void Install(X2LuaHost host, X2GameContext context, IX2ItemsData data)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);
        InstallContainers(host, context, data);
        InstallCore(host, context, data);
        InstallEnchant(host, context, data);
        InstallCommerce(host, context, data);
    }

    /// <summary>Creates the shape consumed by item slots and tooltip helpers; optional Fields override defaults.</summary>
    internal static LuaTable ItemTable(X2InventoryItem item)
    {
        var result = new LuaTable
        {
            ["id"] = (double)item.Id,
            ["itemType"] = (double)item.ItemType,
            ["itemGrade"] = (double)item.Grade,
            ["stack"] = (double)item.Count,
            ["name"] = item.Name,
            ["icon"] = item.Icon,
        };
        foreach (var (key, value) in item.Fields) result[key] = ToItemValue(value);
        return result;
    }

    private static object? ToItemValue(object? value)
    {
        if (value is null or string or bool or LuaTable) return value;
        if (value is System.Collections.IDictionary map)
        {
            var table = new LuaTable();
            foreach (System.Collections.DictionaryEntry entry in map)
                if (entry.Key is not null) table[entry.Key] = ToItemValue(entry.Value);
            return table;
        }
        if (value is System.Collections.IEnumerable sequence)
        {
            var table = new LuaTable();
            var index = 1;
            foreach (var entry in sequence) table[(double)index++] = ToItemValue(entry);
            return table;
        }
        return value;
    }
}
