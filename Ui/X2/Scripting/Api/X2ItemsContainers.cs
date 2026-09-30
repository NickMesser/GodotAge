#nullable enable
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

public static partial class X2ItemsApi
{
    // Names are the complete X2ApiData.g.cs surface for these three namespaces.
    private const string BagMethods = "CanExpand Capacity CheckPin Confirm CountBagItemByItemType CountEmptyBagSlots CountItems CreateTab CurrentTabIdx EditTab EquipBagItem Expand ExpandedSlotCount ExpansionCost ExpansionMaxSlotCount FindBagItem FindBagItemByEquipSlot FindFirstBagItemInfoByItemType FindFirstBagSlotItemByItemType GetAutoUseAAPoint GetBagCooldown GetBagItemInfo GetBagItemSoundType GetBagNumSlots GetCountInBag GetCurrency GetLinkText GetMountBagNumSlots GetMountableBagItemInfo GetMountableBagItemTooltipText GetMountableSlotItemInfo GetMountableSlotItemTooltipText GetRenewItemPreviewTooltipText GetSearchKeyword HandleClick HandleDragRecv HandleDragStart HandleSelectiveItems HandleUse HasPickedItem HasRepairAuthorityInBag IsBagSlotDimCheck IsConfirmedSlot IsExpandable IsInvalidSlot IsLocked IsSlotSearched IsSoulBoundedItem ItemDurability ItemGemStats ItemIdentifier ItemRepair ItemRepairCost ItemRequireLevel ItemStack LockSlot ManualSort Modified MoveToEmptyBankSlot MoveToEmptyCofferSlot PickupBagItemPartial RemoveAllPin RemovePin RemoveTab SetAutoUseAAPoint SetPin SetSearchKeyword SetSelectiveItem SetSelectiveTryCount SlotByIdx Slots SwitchTab Sync TabCount TabInfo TabInfos UnlockSlot UseItemByType";
    private const string BankMethods = "CanExpand Capacity Confirm CountEmptyBagSlots CountItems CreateTab CurrentTabIdx Deposit EditTab Expand ExpandedSlotCount ExpansionCost ExpansionMaxSlotCount GetBagItemInfo GetCurrency GetLinkText GetSearchKeyword HandleClick HandleDragRecv HandleDragStart HandleUse IsBankerNpc IsConfirmedSlot IsExpandable IsInvalidSlot IsLocked IsSlotSearched IsSoulBoundedItem ItemDurability ItemGemStats ItemIdentifier ItemRepair ItemRepairCost ItemRequireLevel ItemStack LockSlot ManualSort Modified MoveToEmptyBagSlot PickupBankItemPartial RemoveTab SetSearchKeyword SlotByIdx Slots SwitchTab Sync TabCount TabInfo TabInfos UnlockSlot Withdraw";
    private const string CofferMethods = "Capacity CofferWindowClosed Confirm CountEmptyBagSlots CountItems CreateTab CurrentTabIdx EditTab GetBagItemInfo GetHouseCofferName GetHouseCofferPermission GetHouseCofferSlotSize GetLinkText GetSearchKeyword HandleClick HandleDragRecv HandleDragStart HandleUse IsConfirmedSlot IsInvalidSlot IsLocked IsManikin IsMyHouseCoffer IsPrivateCoffer IsSlotSearched IsSoulBoundedItem ItemDurability ItemGemStats ItemIdentifier ItemRequireLevel ItemStack ManualSort Modified MoveToEmptyBagSlot PickupCofferItemPartial RemoveTab RetryPendedOpen SetHouseCofferPermission SetSearchKeyword SlotByIdx Slots SwitchTab Sync TabAddible TabCount TabInfo TabInfos";

    internal static void InstallContainers(X2LuaHost host, X2GameContext context, IX2ItemsData data)
    {
        Register(host, data, "X2Bag", X2ContainerKind.Bag, BagMethods);
        Register(host, data, "X2Bank", X2ContainerKind.Bank, BankMethods);
        Register(host, data, "X2Coffer", X2ContainerKind.Coffer, CofferMethods);
    }

    private static void Register(X2LuaHost host, IX2ItemsData data, string table, X2ContainerKind kind, string methods)
    {
        foreach (var name in methods.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var method = name;
            host.Define(table, method, args => InvokeContainer(data, table, kind, method, args));
        }
    }

    private static object? InvokeContainer(IX2ItemsData data, string table, X2ContainerKind kind, string method, LuaArgs a)
    {
        var state = data.GetContainer(kind);
        // Bag's first argument is bagId; only the character bag (1) is represented by this data source.
        var slot = (method is "GetBagItemInfo" or "GetBagItemSoundType") && kind == X2ContainerKind.Bag ? a.Int(1) : a.Int(0);
        var item = data.GetContainerItem(kind, slot);
        switch (method)
        {
            case "Capacity": case "GetBagNumSlots": return state.Capacity;
            case "GetMountBagNumSlots": return 0;
            case "ExpandedSlotCount": return state.ExpandedSlots;
            case "ExpansionMaxSlotCount": return state.ExpansionMaxSlots;
            case "ExpansionCost": return state.ExpansionCost;
            case "CanExpand": case "IsExpandable": return state.Capacity < state.ExpansionMaxSlots;
            case "GetCurrency": return (double)state.Currency;
            case "CountItems": return state.Items.Count;
            case "CountEmptyBagSlots": return Math.Max(0, state.Capacity - state.Items.Count);
            case "CountBagItemByItemType": case "GetCountInBag": return data.GetItemCount((uint)a.Int(0));
            case "GetBagItemInfo": return kind == X2ContainerKind.Bag && a.Int(0) != 1 || item is null ? null : CompleteItemTable(item);
            case "FindFirstBagItemInfoByItemType":
            {
                var found = state.Items.Values.FirstOrDefault(i => i.ItemType == (uint)a.Int(0));
                return found is null ? null : CompleteItemTable(found);
            }
            case "FindFirstBagSlotItemByItemType":
            {
                var foundSlot = state.Items.FirstOrDefault(p => p.Value.ItemType == (uint)a.Int(0)).Key;
                return foundSlot == 0 ? null : foundSlot;
            }
            case "FindBagItem": return FindBagItems(state, "impl1", a.Int(1), a.Int(0));
            case "FindBagItemByEquipSlot": return FindBagItems(state, "equipSlots", a.Int(1), 1);
            case "GetMountableSlotItemInfo": case "GetMountableSlotItemTooltipText": return new LuaTable();
            case "GetSearchKeyword": return state.SearchKeyword;
            case "GetAutoUseAAPoint": return state.AutoUseAaPoint;
            case "GetBagCooldown": return null; // No cooldown belongs to an empty inventory slot.
            case "GetBagItemSoundType": return item?.Fields.TryGetValue("soundType", out var sound) == true ? sound : 0;
            case "GetLinkText": return item?.Fields.TryGetValue("linkText", out var link) == true ? link : item?.Name ?? "";
            case "GetRenewItemPreviewTooltipText": return "";
            case "GetHouseCofferName": return state.HouseCofferName;
            case "GetHouseCofferSlotSize": return state.Capacity;
            case "GetHouseCofferPermission": return new LuaMulti(state.HouseCofferPermission, state.HouseCofferPermissionAvailable);
            case "IsBankerNpc": return state.IsBankerNpc;
            case "IsPrivateCoffer": return state.IsPrivateCoffer;
            case "IsMyHouseCoffer": return state.IsMyHouseCoffer;
            case "IsManikin": return state.IsManikin;
            case "HasPickedItem": case "HasRepairAuthorityInBag": return false;
            case "IsLocked": return state.LockedSlots.Contains(slot);
            case "CheckPin": return state.PinnedSlots.Contains(slot);
            case "IsSoulBoundedItem": return item?.Fields.TryGetValue("soulBound", out var bound) == true && bound is true;
            case "IsInvalidSlot": return slot < 1 || slot > state.Capacity;
            case "IsConfirmedSlot": return slot >= 1 && slot <= state.Capacity;
            case "IsBagSlotDimCheck": return new LuaMulti(false, true);
            case "IsSlotSearched": return new LuaMulti(item is not null && state.SearchKeyword.Length > 0 &&
                item.Name.Contains(state.SearchKeyword, StringComparison.OrdinalIgnoreCase), true);
            case "ItemIdentifier": return item is null ? new LuaMulti(null, null) : new LuaMulti((double)item.ItemType, item.Grade);
            case "ItemStack": return item?.Count ?? 0;
            case "ItemRequireLevel": return item?.Fields.TryGetValue("levelRequirement", out var level) == true ? level : 0;
            case "ItemDurability": return Durability(item);
            case "ItemGemStats": return item?.Fields.TryGetValue("gemStats", out var gems) == true ? gems : new LuaTable();
            case "ItemRepairCost": return item?.Fields.TryGetValue("repairCost", out var repairCost) == true ? repairCost?.ToString() ?? "0" : "0";
            case "TabCount": return state.Tabs.Count;
            case "CurrentTabIdx": return state.CurrentTab;
            case "TabInfo": return TabInfo(state, a.Int(0));
            case "TabInfos": return TabInfos(state);
            case "TabAddible": return !state.IsPrivateCoffer || state.IsMyHouseCoffer;
            case "Slots": return state.VirtualSlots.Count > 0 ? ArrayTable(state.VirtualSlots) : SlotArray(state.Capacity);
            case "SlotByIdx": return slot >= 1 && slot <= state.Capacity
                ? state.VirtualSlots.Count >= slot ? state.VirtualSlots[slot - 1] : slot : 0;
            case "Modified": return state.Modified;
            case "RemoveTab": case "SwitchTab": case "SetSearchKeyword": case "UseItemByType":
                return data.SendItemsCommand(new X2ItemsCommand(table, method, a.Values));
            case "GetMountableBagItemInfo": case "GetMountableBagItemTooltipText":
                return null; // No mount inventory exists before a mount is selected.
            default:
                // These methods request server actions (move, split, use, repair, sort, expand, money transfer,
                // pinning) or adjust presentation (tab edit). The adapter owns both; no local item mutation occurs.
                data.SendItemsCommand(new X2ItemsCommand(table, method, a.Values));
                return null;
        }
    }

    private static LuaTable? Durability(X2InventoryItem? item)
    {
        if (item?.Fields.TryGetValue("durability", out var current) != true ||
            !item.Fields.TryGetValue("maxDurability", out var max)) return null;
        return new LuaTable { ["current"] = current, ["max"] = max };
    }

    private static object TabInfo(X2ContainerState state, int index)
    {
        if (index < 1 || index > state.Tabs.Count) return new LuaMulti(null, null, null);
        var tab = state.Tabs[index - 1];
        return new LuaMulti(tab.Description, tab.IconIndex, ArrayTable(tab.GroupTypes));
    }

    private static LuaMulti TabInfos(X2ContainerState state)
        => new(ArrayTable(state.Tabs.Select(tab => tab.Description)), ArrayTable(state.Tabs.Select(tab => tab.IconIndex)));

    private static LuaTable SlotArray(int capacity) => ArrayTable(Enumerable.Range(1, capacity));

    private static LuaTable FindBagItems(X2ContainerState state, string field, int expected, int bagId)
    {
        var result = new LuaTable();
        if (bagId != 1) return result;
        var index = 1;
        foreach (var (slot, item) in state.Items.OrderBy(pair => pair.Key))
        {
            if (!item.Fields.TryGetValue(field, out var value))
            {
                // A single equipSlot is also accepted; the host can supply either the scalar or equipSlots.
                if (field != "equipSlots" || !item.Fields.TryGetValue("equipSlot", out value)) continue;
            }
            var matches = value switch
            {
                int n => n == expected,
                double n => (int)n == expected,
                IEnumerable<int> numbers => numbers.Contains(expected),
                IEnumerable<double> numbers => numbers.Any(n => (int)n == expected),
                _ => false,
            };
            if (!matches) continue;
            var row = CompleteItemTable(item);
            row["slot"] = (double)slot;
            result[(double)index++] = row;
        }
        return result;
    }

    private static LuaTable ArrayTable<T>(IEnumerable<T> values)
    {
        var table = new LuaTable();
        var i = 1;
        foreach (var value in values) table[(double)i++] = value;
        return table;
    }
}
