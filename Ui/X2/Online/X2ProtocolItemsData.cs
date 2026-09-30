#nullable enable

using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Projects the main-thread protocol inventory reducer into the stock X2 item APIs.  The accessors
/// are deliberately lazy because the X2 API family is installed before an online session exists.
/// </summary>
public sealed class X2ProtocolItemsData : NullItemsData
{
    private const int DefaultMaximumSlots = 150;

    private readonly Func<OnlineSession?> _sessionAccessor;
    private readonly Func<ClientActions?> _actionsAccessor;
    private readonly Func<InventoryState?> _inventoryStateAccessor;
    private readonly Func<uint, uint?>? _npcTemplateResolver;
    private uint _interactionNpcUnitId;
    private uint _interactionNpcTemplateId;
    private bool _repairNpcActive;
    private bool _repairMode;

    /// <summary>Remembers the NPC template supplied by the host for merchant content lookup.</summary>
    public void SetInteractionNpc(uint npcUnitId, uint npcTemplateId)
    {
        _interactionNpcUnitId = npcUnitId;
        _interactionNpcTemplateId = npcTemplateId;
    }

    /// <summary>The server's NPC interaction skill list is authoritative for repair access.</summary>
    public void SetRepairAuthority(bool enabled)
    {
        _repairNpcActive = enabled;
        if (!enabled) _repairMode = false;
    }
    private readonly string _gameDatabasePath;
    private readonly Dictionary<uint, StoreSnapshot> _storeCache = [];
    private (X2ContainerKind Kind, int Slot)? _dragSource;
    private int? _dragSplitCount;
    private X2CursorState _pickedCursor = new();
    public X2CursorState PickedCursor => _pickedCursor;
    public void ClearPickedCursor() { _dragSource = null; _dragSplitCount = null; _pickedCursor = new(); }

    /// <summary>Native slot input; slotIndex is the zero-based real index established by Lua.</summary>
    public bool HandleNativeSlotInteraction(int slotType, int slotIndex, string interaction)
    {
        if (slotIndex < 0 || slotIndex >= byte.MaxValue) return false;
        var state = _inventoryStateAccessor();
        var actions = _actionsAccessor();
        if (state is null || actions is null) return false;
        if (_repairMode && interaction == "Start" &&
            (slotType is (int)InventorySlotType.Equipment or (int)InventorySlotType.Inventory))
            return true; // Repair selection is click-only; do not let a small pointer move start an item drag.
        if (_repairMode && interaction == "Click" &&
            (slotType is (int)InventorySlotType.Equipment or (int)InventorySlotType.Inventory))
        {
            var hasItem = slotType == (int)InventorySlotType.Equipment
                ? _sessionAccessor() is { } session && PlayerEquipment(session).ContainsKey(slotIndex)
                : state.TryGetItem((byte)InventorySlotType.Inventory, slotIndex, out var repairItem) && repairItem is not null;
            if (!hasItem)
                return false;
            actions.RepairItem(checked((byte)slotType), checked((byte)slotIndex),
                state.AutoUseAaPoint != 0, slotType == (int)InventorySlotType.Inventory);
            return true;
        }
        var kind = slotType switch
        {
            2 => X2ContainerKind.Bag,
            3 => X2ContainerKind.Bank,
            _ => (X2ContainerKind)(-1),
        };
        if ((int)kind < 0) return false;
        var slot = slotIndex + 1;
        if (interaction == "Cancel") { ClearPickedCursor(); return true; }
        if (interaction == "Use")
            return SendItemsCommand(new X2ItemsCommand(kind == X2ContainerKind.Bag ? "X2Bag" : "X2Bank", "HandleUse", [slot]));
        if (interaction is "Start" or "Click" && _dragSource is null)
        {
            if (!TryItem(state, kind, slot, out var item) || item is null) return false;
            _dragSource = (kind, slot);
            _dragSplitCount = null;
            _pickedCursor = new X2CursorState("item", kind == X2ContainerKind.Bag ? slot : 0, item.Count,
                "");
            return true;
        }
        if (interaction is "Receive" or "Click")
        {
            var sent = FinishDrag(state, actions, kind, slot);
            _pickedCursor = new();
            return sent;
        }
        return false;
    }
    private ushort _lastLootItemIndex;
    private ushort _lastLootOwnerType;
    private uint _lastLootOwnerObjectId;
    private readonly List<int> _buybackCart = [];

    public X2ProtocolItemsData(string gameDatabasePath, Func<OnlineSession?> sessionAccessor,
        Func<ClientActions?> actionsAccessor, Func<uint, uint?>? npcTemplateResolver = null,
        Func<InventoryState?>? inventoryStateAccessor = null)
        : base(gameDatabasePath)
    {
        _gameDatabasePath = Path.GetFullPath(gameDatabasePath);
        _sessionAccessor = sessionAccessor ?? throw new ArgumentNullException(nameof(sessionAccessor));
        _actionsAccessor = actionsAccessor ?? throw new ArgumentNullException(nameof(actionsAccessor));
        _inventoryStateAccessor = inventoryStateAccessor ?? (() => _sessionAccessor()?.InventoryState);
        _npcTemplateResolver = npcTemplateResolver;
    }

    /// <summary>Convenience overload for read-only hosts which have no online packet sender.</summary>
    public X2ProtocolItemsData(string gameDatabasePath, Func<OnlineSession?> sessionAccessor)
        : this(gameDatabasePath, sessionAccessor, static () => null)
    {
    }

    public override X2ContainerState GetContainer(X2ContainerKind kind)
    {
        var session = _sessionAccessor();
        if (session is null)
            return base.GetContainer(kind);

        var presentation = base.GetContainer(kind);
        var state = session.InventoryState;
        var snapshots = ContainerSnapshots(state, kind);
        var items = snapshots.ToDictionary(
            pair => ToUiSlot(pair.Key),
            pair => ToX2Item(pair.Value));
        var capacity = kind switch
        {
            X2ContainerKind.Bag => checked((int)state.InventorySlots),
            X2ContainerKind.Bank => checked((int)state.BankSlots),
            X2ContainerKind.Coffer => items.Count == 0 ? 0 : items.Keys.Max(),
            _ => 0,
        };

        return presentation with
        {
            Capacity = capacity,
            ExpandedSlots = capacity,
            ExpansionMaxSlots = kind == X2ContainerKind.Coffer
                ? capacity
                : Math.Max(DefaultMaximumSlots, capacity),
            Items = items,
            AutoUseAaPoint = kind == X2ContainerKind.Bag && state.AutoUseAaPoint != 0,
        };
    }

    public override X2InventoryItem? GetContainerItem(X2ContainerKind kind, int slot)
    {
        var session = _sessionAccessor();
        if (session is null)
            return base.GetContainerItem(kind, slot);
        return ContainerSnapshots(session.InventoryState, kind).TryGetValue(ToProtocolSlot(slot), out var item)
            ? ToX2Item(item)
            : null;
    }

    public override int GetItemCount(uint itemType)
    {
        var session = _sessionAccessor();
        if (session is null)
            return base.GetItemCount(itemType);
        return session.InventoryState.Items
            .Where(pair => pair.Key.SlotType == (byte)InventorySlotType.Inventory &&
                           pair.Value.TemplateId == itemType)
            .Sum(pair => pair.Value.Count);
    }

    public override IReadOnlyList<X2InventoryItem> GetEquippedItems(string unit)
    {
        var session = _sessionAccessor();
        if (session is null)
            return base.GetEquippedItems(unit);
        if (TryMateTimeline(session, unit, out var mateTimeline))
            return session.MateSlaveState.MateEquipment(mateTimeline)
                .OrderBy(pair => pair.Key)
                .Select(pair => ToX2Item(pair.Value, pair.Key))
                .ToArray();
        if (!IsPlayerUnit(unit))
            return [];

        return PlayerEquipment(session)
            .OrderBy(pair => pair.Key)
            .Select(pair => ToX2Item(pair.Value, pair.Key + 1))
            .ToArray();
    }

    public override X2InventoryItem? GetEquippedItem(string unit, int equipSlot)
    {
        var session = _sessionAccessor();
        if (session is null)
            return base.GetEquippedItem(unit, equipSlot);
        if (TryMateTimeline(session, unit, out var mateTimeline))
            return session.MateSlaveState.MateEquipment(mateTimeline).TryGetValue(equipSlot, out var mateItem)
                ? ToX2Item(mateItem, equipSlot) : null;
        if (!IsPlayerUnit(unit))
            return null;
        // X2Equipment accepts the 1-based ES_* values. InventoryState keeps the
        // 0-based wire slots used by EstablishSlot and SCUnitEquipmentsChanged.
        return equipSlot > 0 && PlayerEquipment(session).TryGetValue(equipSlot - 1, out var item)
            ? ToX2Item(item, equipSlot)
            : null;
    }

    public override IReadOnlyList<X2LootItem> LootItems
    {
        get
        {
            var session = _sessionAccessor();
            if (session is null)
                return base.LootItems;

            var result = new X2LootItem[session.InventoryState.LootItems.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var snapshot = session.InventoryState.LootItems[i];
                RememberLootOwner(snapshot, session.InventoryState.LootOwnerObjectId);
                var item = ToX2Item(snapshot);
                var fields = new Dictionary<string, object?>(item.Fields)
                {
                    ["id"] = (double)item.Id,
                    ["itemGrade"] = (double)item.Grade,
                    ["icon"] = item.Icon,
                };
                result[i] = new X2LootItem(i + 1, item.Name, checked((int)item.ItemType), item.Count,
                    SoundType: FieldInt(item, "soundType"), Fields: fields, TooltipFields: fields);
            }
            return result;
        }
    }

    public override IReadOnlyList<X2StoreItem> GetStoreItems(bool exactMatch, string searchText)
    {
        var session = _sessionAccessor();
        if (session is null)
            return base.GetStoreItems(exactMatch, searchText);

        var store = CurrentStore(session);
        if (store is null || string.IsNullOrWhiteSpace(searchText))
            return store?.Items ?? [];
        return store.Items.Where(item => exactMatch
                ? item.Name.Equals(searchText, StringComparison.OrdinalIgnoreCase)
                : item.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public override X2StoreCurrency StoreCurrency
    {
        get
        {
            var session = _sessionAccessor();
            return session is null ? base.StoreCurrency : CurrentStore(session)?.Currency ?? base.StoreCurrency;
        }
    }

    public override IReadOnlyList<X2StoreItem> SoldStoreItems
    {
        get
        {
            var session = _sessionAccessor();
            if (session is null)
                return base.SoldStoreItems;
            return session.ShopState.SoldItems
                .Select((sold, index) => ToX2StoreItem(sold, index, session.ShopState.PurchaseLimits))
                .ToArray();
        }
    }

    /// <summary>Builds the exact row array consumed by store.lua's STORE_SOLD_LIST handler.</summary>
    public LuaTable BuildSoldHistoryRows()
    {
        var result = new LuaTable();
        var index = 1;
        foreach (var item in SoldStoreItems)
            result[(double)index++] = X2StoreSoldRows.Build(item);
        return result;
    }

    public override void BuyStoreItems(IReadOnlyList<int> goodIndices, IReadOnlyList<int> stackSizes,
        IReadOnlyList<int> currencies)
    {
        var session = _sessionAccessor();
        var actions = _actionsAccessor();
        var interaction = session?.ShopState.OpenInteraction;
        Godot.GD.Print($"[x2 store] buy goods [{string.Join(' ', goodIndices)}] stacks [{string.Join(' ', stackSizes)}] currencies [{string.Join(' ', currencies)}] " +
                       $"npc {interaction?.NpcUnitId.ToString() ?? "none"} object {interaction?.ObjectId.ToString() ?? "-"}");
        if (session is null || actions is null || interaction is null || goodIndices.Count == 0 ||
            goodIndices.Count != stackSizes.Count || goodIndices.Count != currencies.Count)
            return;

        var entries = new List<NpcBuyEntry>(goodIndices.Count);
        for (var i = 0; i < goodIndices.Count; i++)
        {
            if (goodIndices[i] <= 0 || stackSizes[i] <= 0 || currencies[i] is < 0 or > byte.MaxValue)
                return;
            var itemType = checked((uint)goodIndices[i]);
            var offer = CurrentStore(session)?.Items.FirstOrDefault(item => item.GoodIndex == goodIndices[i]);
            var grade = checked((byte)Math.Clamp(
                offer?.Fields?.GetValueOrDefault("itemGrade") is double value
                    ? checked((int)value)
                    : GetItemInfo(itemType)?.Grade ?? 0, 0, byte.MaxValue));
            entries.Add(new NpcBuyEntry(itemType, grade, stackSizes[i], checked((byte)currencies[i])));
        }

        actions.Buy(interaction.NpcUnitId, interaction.ObjectId, 0, entries,
            useAaPoint: currencies.Any(currency => currency is 3 or 4),
            openType: checked((byte)Math.Clamp(StoreOpenType, 0, byte.MaxValue)));
    }

    public override void SellStoreItems(IReadOnlyList<int> slots, bool equipmentSlots)
    {
        var session = _sessionAccessor();
        var actions = _actionsAccessor();
        var interaction = session?.ShopState.OpenInteraction;
        if (session is null || actions is null || interaction is null || slots.Count == 0)
            return;

        var sourceType = equipmentSlots ? (byte)InventorySlotType.Equipment : (byte)InventorySlotType.Inventory;
        var entries = new List<NpcSellEntry>(slots.Count);
        foreach (var uiSlot in slots.Distinct())
        {
            if (uiSlot < 1)
                return;
            ItemSnapshot? item;
            var protocolSlot = equipmentSlots ? uiSlot : ToProtocolSlot(uiSlot);
            if (equipmentSlots)
                item = PlayerEquipment(session).GetValueOrDefault(protocolSlot);
            else
                session.InventoryState.TryGetItem(sourceType, protocolSlot, out item);
            if (item is null || protocolSlot > byte.MaxValue || item.Count <= 0)
                return;
            entries.Add(new NpcSellEntry(0, sourceType, 0, checked((byte)protocolSlot),
                item.ItemId, checked((uint)item.Count), unchecked((ulong)item.UnsecureTime)));
        }
        actions.Sell(interaction.NpcUnitId, interaction.ObjectId, entries);
        // AAEmu does not push SCSoldItemList after CSSellItems. Game-channel requests are ordered,
        // so refresh after the server has moved these items into the buyback container.
        actions.ListSoldItems(interaction.NpcUnitId);
    }

    public override void RequestSoldItemList()
    {
        var session = _sessionAccessor();
        var actions = _actionsAccessor();
        var interaction = session?.ShopState.OpenInteraction;
        if (actions is not null && interaction is not null)
            actions.ListSoldItems(interaction.NpcUnitId);
    }

    public override void AddBuyBackItem(int index)
    {
        if (index >= 0 && !_buybackCart.Contains(index))
            _buybackCart.Add(index);
    }

    public override void ClearBuyBackCart() => _buybackCart.Clear();

    public override void BuyBackItems()
    {
        var session = _sessionAccessor();
        var actions = _actionsAccessor();
        var interaction = session?.ShopState.OpenInteraction;
        if (session is null || actions is null || interaction is null || _buybackCart.Count == 0)
            return;
        actions.Buy(interaction.NpcUnitId, interaction.ObjectId, 0, [], _buybackCart.ToArray(),
            useAaPoint: false, openType: checked((byte)Math.Clamp(StoreOpenType, 0, byte.MaxValue)));
        // Refresh the server-owned container after the ordered buyback request removes its slots.
        actions.ListSoldItems(interaction.NpcUnitId);
        _buybackCart.Clear();
    }

    public override void LootItem(int slot)
    {
        var session = _sessionAccessor();
        var actions = _actionsAccessor();
        if (session is null || actions is null || slot < 1 || slot > session.InventoryState.LootItems.Count)
            return;

        var item = session.InventoryState.LootItems[slot - 1];
        RememberLootOwner(item, session.LootBagOwnerId is > 0 and var opened ? opened : session.InventoryState.LootOwnerObjectId);
        Godot.GD.Print($"[x2 loot] take {slot}: item {item.ItemId:X} t{item.TemplateId}x{item.Count} -> index {_lastLootItemIndex} ownerType {_lastLootOwnerType} owner {_lastLootOwnerObjectId}");
        actions.LootItem(_lastLootItemIndex, _lastLootOwnerType, _lastLootOwnerObjectId,
            _lastLootItemIndex, 0);
    }

    /// <summary>
    /// Uses the protocol's bulk-loot request for the owner announced by SCLootableState.  The stock
    /// loot window still mirrors the original client and submits each visible row individually;
    /// this entry point is for callers which request the server-side bulk operation directly.
    /// </summary>
    public override void LootAll()
    {
        var state = _sessionAccessor()?.InventoryState;
        var actions = _actionsAccessor();
        if (state is null || actions is null)
            return;
        var ownerObjectId = _sessionAccessor()?.LootBagOwnerId is > 0 and var opened ? opened : state.LootOwnerObjectId;
        if (ownerObjectId == 0 && state.LootItems.Count > 0)
            ownerObjectId = unchecked((uint)(state.LootItems[0].ItemId >> 32));
        Godot.GD.Print($"[x2 loot] take all: owner {state.LootOwnerObjectId} -> {ownerObjectId}, items [{string.Join(' ', state.LootItems.Select(i => $"{i.ItemId:X}:{i.TemplateId}x{i.Count}"))}]");
        if (ownerObjectId != 0)
            actions.LootAll(ownerObjectId);
    }

    public override void CloseLoot()
    {
        var state = _sessionAccessor()?.InventoryState;
        var actions = _actionsAccessor();
        if (state is null || actions is null)
            return;
        if (state.LootItems.Count > 0)
            RememberLootOwner(state.LootItems[0], state.LootOwnerObjectId);
        if (_lastLootOwnerObjectId != 0)
            actions.CloseLoot(_lastLootItemIndex, _lastLootOwnerType, _lastLootOwnerObjectId, 0);
    }

    public override void SetRepairMode(bool enabled) => _repairMode = enabled && _repairNpcActive;

    public override void RepairAllEquipment()
    {
        if (!_repairNpcActive) return;
        _actionsAccessor()?.RepairAll(_inventoryStateAccessor()?.AutoUseAaPoint != 0, inBag: false);
    }

    public override bool UseEquippedItem(uint itemType)
    {
        var session = _sessionAccessor();
        if (session is null)
            return base.UseEquippedItem(itemType);
        var item = PlayerEquipment(session).Values.FirstOrDefault(value => value.TemplateId == itemType);
        return item is not null && UseItem(session, item);
    }

    public override void SplitBagItem(int oldSlot, int newSlot, int count)
    {
        var session = _sessionAccessor();
        var actions = _actionsAccessor();
        if (session is null || actions is null || count <= 0 ||
            !TryItem(session.InventoryState, X2ContainerKind.Bag, oldSlot, out var source))
            return;
        TryItem(session.InventoryState, X2ContainerKind.Bag, newSlot, out var destination);
        actions.SplitItem(source!.ItemId, destination?.ItemId ?? 0,
            (byte)InventorySlotType.Inventory, ToByteSlot(oldSlot),
            (byte)InventorySlotType.Inventory, ToByteSlot(newSlot), count);
    }

    public override bool SendItemsCommand(X2ItemsCommand command)
    {
        // Tab, search, lock and pin operations remain client-side presentation state.
        if (base.SendItemsCommand(command))
            return true;

        var session = _sessionAccessor();
        var actions = _actionsAccessor();
        if (session is null || actions is null || !TryContainer(command.Table, out var kind))
            return false;

        var first = IntArgument(command.Arguments, 0);
        switch (command.Method)
        {
            case "HandleDragStart":
                if (first < 1) return false;
                _dragSource = (kind, first);
                _dragSplitCount = null;
                _pickedCursor = new X2CursorState("item", kind == X2ContainerKind.Bag ? first : 0,
                    GetContainerItem(kind, first)?.Count ?? 0, GetContainerItem(kind, first)?.Icon ?? "");
                return true;
            case "HandleClick":
                return HandleNativeSlotInteraction(kind == X2ContainerKind.Bag ? 2 : 3, first - 1, "Click");
            case "PickupBagItemPartial" when kind == X2ContainerKind.Bag:
                return StartSplit(kind, IntArgument(command.Arguments, 1), IntArgument(command.Arguments, 2));
            case "PickupBankItemPartial" when kind == X2ContainerKind.Bank:
            case "PickupCofferItemPartial" when kind == X2ContainerKind.Coffer:
                return StartSplit(kind, first, IntArgument(command.Arguments, 1));
            case "HandleDragRecv":
                var received = FinishDrag(session.InventoryState, actions, kind, first);
                _pickedCursor = new();
                return received;
            case "HandleUse" when kind == X2ContainerKind.Bag:
                return TryItem(session.InventoryState, kind, first, out var used) && used is not null &&
                       UseItem(session, used);
            case "HandleUse" when kind == X2ContainerKind.Bank:
            case "MoveToEmptyBagSlot" when kind == X2ContainerKind.Bank:
                return MoveToFirstEmpty(session.InventoryState, actions, kind, first, X2ContainerKind.Bag);
            case "MoveToEmptyBankSlot" when kind == X2ContainerKind.Bag:
                return MoveToFirstEmpty(session.InventoryState, actions, kind, first, X2ContainerKind.Bank);
            case "EquipBagItem" when kind == X2ContainerKind.Bag:
                return EquipBagItem(session, actions, first, BoolArgument(command.Arguments, 1));
            case "UseItemByType" when kind == X2ContainerKind.Bag:
            {
                var itemType = unchecked((uint)Math.Max(0, first));
                var item = ContainerSnapshots(session.InventoryState, kind).Values
                    .FirstOrDefault(value => value.TemplateId == itemType);
                return item is not null && UseItem(session, item);
            }
            case "ItemRepair":
                if (!_repairNpcActive || first < 1 || kind == X2ContainerKind.Coffer) return false;
                actions.RepairItem(SlotType(kind), ToByteSlot(first),
                    session.InventoryState.AutoUseAaPoint != 0, kind == X2ContainerKind.Bag);
                return true;
            default:
                return false;
        }
    }

    private bool FinishDrag(InventoryState state, ClientActions actions, X2ContainerKind destinationKind,
        int destinationSlot)
    {
        var source = _dragSource;
        var splitCount = _dragSplitCount;
        _dragSource = null;
        _dragSplitCount = null;
        if (source is null || destinationSlot < 1 || source.Value.Kind == X2ContainerKind.Coffer ||
            destinationKind == X2ContainerKind.Coffer ||
            !TryItem(state, source.Value.Kind, source.Value.Slot, out var sourceItem) || sourceItem is null)
            return false;
        if (source.Value.Kind == destinationKind && source.Value.Slot == destinationSlot) return true;

        TryItem(state, destinationKind, destinationSlot, out var destinationItem);
        if (splitCount is > 0)
            actions.SplitItem(sourceItem.ItemId, destinationItem?.ItemId ?? 0,
                SlotType(source.Value.Kind), ToByteSlot(source.Value.Slot),
                SlotType(destinationKind), ToByteSlot(destinationSlot), splitCount.Value);
        else if (destinationItem is not null)
            actions.SwapItems(sourceItem.ItemId, destinationItem.ItemId,
                SlotType(source.Value.Kind), ToByteSlot(source.Value.Slot),
                SlotType(destinationKind), ToByteSlot(destinationSlot));
        else
            actions.MoveItem(sourceItem.ItemId, SlotType(source.Value.Kind), ToByteSlot(source.Value.Slot),
                SlotType(destinationKind), ToByteSlot(destinationSlot));
        return true;
    }

    private bool StartSplit(X2ContainerKind kind, int slot, int count)
    {
        if (slot < 1 || count <= 0)
            return false;
        _dragSource = (kind, slot);
        _dragSplitCount = count;
        return true;
    }

    private bool MoveToFirstEmpty(InventoryState state, ClientActions actions, X2ContainerKind sourceKind,
        int sourceSlot, X2ContainerKind destinationKind)
    {
        if (!TryItem(state, sourceKind, sourceSlot, out var source) || source is null)
            return false;
        var capacity = destinationKind == X2ContainerKind.Bag
            ? checked((int)state.InventorySlots)
            : checked((int)state.BankSlots);
        var destinationItems = ContainerSnapshots(state, destinationKind);
        var destinationSlot = Enumerable.Range(0, capacity).FirstOrDefault(slot => !destinationItems.ContainsKey(slot), -1);
        if (destinationSlot < 0 || destinationSlot > byte.MaxValue)
            return false;
        actions.MoveItem(source.ItemId, SlotType(sourceKind), ToByteSlot(sourceSlot),
            SlotType(destinationKind), checked((byte)destinationSlot));
        return true;
    }

    private bool EquipBagItem(OnlineSession session, ClientActions actions, int bagSlot, bool auxiliary)
    {
        var state = session.InventoryState;
        if (!TryItem(state, X2ContainerKind.Bag, bagSlot, out var source) || source is null)
            return false;
        var definition = GetItemInfo(source.TemplateId);
        var equipSlot = FieldInt(definition, auxiliary ? "auxEquipSlot" : "defaultEquipSlot", -1);
        if (equipSlot < 0 || equipSlot > byte.MaxValue)
            return false;
        PlayerEquipment(session).TryGetValue(equipSlot, out var displaced);
        actions.EquipItem(source.ItemId, ToByteSlot(bagSlot), checked((byte)equipSlot), displaced?.ItemId ?? 0);
        return true;
    }

    private bool UseItem(OnlineSession session, ItemSnapshot item)
    {
        var actions = _actionsAccessor();
        if (actions is null)
            return false;
        try
        {
            // The X2 item API supplies no explicit target.  The original UI's HandleUse path targets
            // the local character; the two unrecovered SkillItem discriminator fields remain zero.
            actions.UseItem(new ItemSkillCast(session.Entered.UnitId, item.ItemId, item.TemplateId,
                0, 0, session.Entered.UnitId));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private X2InventoryItem ToX2Item(ItemSnapshot snapshot, int? equipSlot = null)
    {
        return X2ItemProjection.ToX2Item(base.GetItemInfo(snapshot.TemplateId), new X2ItemInstance(
            snapshot.TemplateId, snapshot.ItemId, snapshot.Grade, snapshot.Flags, snapshot.Count,
            snapshot.DetailType, snapshot.CreateTime, snapshot.LifespanMinutes, snapshot.MadeUnitId,
            snapshot.UnsecureTime, snapshot.UnpackTime, snapshot.ChargeUseSkillTime,
            snapshot.Durability, snapshot.EnchantScale, snapshot.ImageTemplateId, snapshot.DyeColor), equipSlot);
    }

    private X2StoreItem ToX2StoreItem(SoldStoreItem sold, int index,
        IReadOnlyList<MerchantPurchaseLimit> limits)
    {
        var definition = GetItemInfo(sold.TemplateId);
        var fields = definition is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(definition.Fields);
        fields["id"] = (double)sold.ItemId;
        fields["itemGrade"] = (double)sold.Grade;
        fields["stack"] = (double)sold.Count;
        fields["flags"] = (double)sold.Flags;
        fields["detailType"] = (double)sold.DetailType;
        var limit = limits.FirstOrDefault(value => value.ItemTemplateId == sold.TemplateId);
        var refund = FieldLong(definition, "money_refund");
        return new X2StoreItem(index, checked((int)sold.TemplateId),
            definition?.Name ?? $"Item {sold.TemplateId}", definition?.Icon ?? "", sold.Count,
            FieldInt(definition, "maxStack", 1), refund, 0, "money",
            IsStackable: FieldInt(definition, "maxStack", 1) > 1,
            PurchaseType: limit?.PurchaseType ?? 0,
            PurchaseLimit: 0,
            PurchaseBuyCount: checked((int)(limit?.BuyCount ?? 0)),
            Fields: fields);
    }

    private StoreSnapshot? CurrentStore(OnlineSession session)
    {
        var interaction = session.ShopState.OpenInteraction;
        if (interaction is null)
            return null;
        var npcTemplate = interaction.NpcUnitId == _interactionNpcUnitId && _interactionNpcTemplateId != 0
            ? _interactionNpcTemplateId : _npcTemplateResolver?.Invoke(interaction.NpcUnitId) ?? 0;
        if (npcTemplate == 0) return null;
        if (_storeCache.TryGetValue(npcTemplate, out var cached))
            return WithLivePurchaseCounts(cached, session.ShopState.PurchaseLimits);

        try
        {
            cached = ReadStore(npcTemplate);
        }
        catch (SqliteException)
        {
            cached = StoreSnapshot.Empty;
        }
        catch (IOException)
        {
            cached = StoreSnapshot.Empty;
        }
        _storeCache[npcTemplate] = cached;
        return WithLivePurchaseCounts(cached, session.ShopState.PurchaseLimits);
    }

    private StoreSnapshot ReadStore(uint npcTemplate)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _gameDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.item_id, g.grade_id, g.cost, g.purchase_type_id, g.purchase_limit,
                   p.kind_id, p.item_point_id, COALESCE(p.item_point_icon, ''),
                   COALESCE(p.item_point_icon_key, ''), i.fixed_grade,
                   CASE WHEN i.gradable IN ('t', 'true', '1', 1) THEN 1 ELSE 0 END,
                   COALESCE(ip.price, 0)
              FROM merchants m
              JOIN merchant_packs p ON p.id = m.merchant_pack_id
              JOIN merchant_goods g ON g.merchant_pack_id = p.id
              JOIN items i ON i.id = g.item_id
              LEFT JOIN item_prices ip
                ON ip.item_id = g.item_id
               AND ip.currency_id = CASE p.kind_id
                     WHEN 0 THEN 0 WHEN 1 THEN 1 WHEN 3 THEN 2 WHEN 6 THEN 5 WHEN 7 THEN 5 ELSE -1 END
             WHERE m.npc_id = $npc
               AND g.enable IN ('t', 'true', '1', 1)
             ORDER BY g.view_order, g.id
            """;
        command.Parameters.AddWithValue("$npc", (long)npcTemplate);
        using var reader = command.ExecuteReader();
        var items = new List<X2StoreItem>();
        var seen = new HashSet<(int ItemType, int Grade)>();
        X2StoreCurrency currency = new();
        while (reader.Read())
        {
            var itemType = reader.GetInt32(0);
            var fixedGrade = reader.GetInt32(9);
            var grade = fixedGrade >= 0 ? fixedGrade : reader.GetInt32(10) != 0 ? reader.GetInt32(1) : 0;
            if (!seen.Add((itemType, grade)))
                continue;
            var kind = reader.GetInt32(5);
            var currencyType = kind switch { 0 => 0, 1 => 1, 3 => 2, 6 or 7 => 5, _ => -1 };
            if (currencyType < 0)
                continue;
            var overrideCost = reader.GetInt64(2);
            var cost = kind == 7 ? overrideCost : overrideCost > 0 ? overrideCost : reader.GetInt64(11);
            if (cost < 0)
                continue;
            var definition = GetItemInfo(checked((uint)itemType));
            var fields = definition is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(definition.Fields);
            fields["itemGrade"] = (double)grade;
            fields[$"{StoreCurrencyName(currencyType)}_price"] = cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add(new X2StoreItem(itemType, itemType, definition?.Name ?? $"Item {itemType}",
                definition?.Icon ?? "", 1, FieldInt(definition, "maxStack", 1), cost, currencyType,
                StoreCurrencyName(currencyType), false, false, FieldInt(definition, "maxStack", 1) > 1,
                reader.GetInt32(3), reader.GetInt32(4), 0, fields));
            currency = new X2StoreCurrency(currencyType, reader.GetInt32(6), reader.GetString(7), reader.GetString(8));
        }
        return new StoreSnapshot(items, currency);
    }

    private static StoreSnapshot WithLivePurchaseCounts(StoreSnapshot store,
        IReadOnlyList<MerchantPurchaseLimit> limits) => store with
    {
        Items = store.Items.Select(item => item with
        {
            PurchaseBuyCount = checked((int)(limits.FirstOrDefault(limit =>
                limit.ItemTemplateId == (uint)item.ItemType)?.BuyCount ?? 0)),
        }).ToArray(),
    };

    private static string StoreCurrencyName(int currency) => currency switch
    {
        0 => "money",
        1 => "honor_point",
        2 => "living_point",
        5 => "item",
        _ => $"currency_{currency}",
    };

    private sealed record StoreSnapshot(IReadOnlyList<X2StoreItem> Items, X2StoreCurrency Currency)
    {
        public static readonly StoreSnapshot Empty = new([], new());
    }

    private static Dictionary<int, ItemSnapshot> PlayerEquipment(OnlineSession session)
    {
        // The local equipment container is seeded from SCUnitState and then maintained by the same
        // SCItemTaskSuccess moves as the bag. Unit equipment views/ids serve appearance replication;
        // merging them here can resurrect an item after the owner's unequip task removed its slot.
        return session.InventoryState.Items
            .Where(pair => pair.Key.SlotType == (byte)InventorySlotType.Equipment)
            .ToDictionary(pair => pair.Key.Slot, pair => pair.Value);
    }

    private static Dictionary<int, ItemSnapshot> ContainerSnapshots(InventoryState state, X2ContainerKind kind)
    {
        if (kind == X2ContainerKind.Coffer)
        {
            if (state.CofferItems.Count == 0)
                return [];
            var cofferId = state.CofferItems.Keys.Last().CofferId;
            return state.CofferItems
                .Where(pair => pair.Key.CofferId == cofferId)
                .ToDictionary(pair => pair.Key.Slot, pair => pair.Value);
        }
        var slotType = SlotType(kind);
        return state.Items
            .Where(pair => pair.Key.SlotType == slotType)
            .ToDictionary(pair => pair.Key.Slot, pair => pair.Value);
    }

    private static bool TryItem(InventoryState state, X2ContainerKind kind, int uiSlot,
        out ItemSnapshot? item)
    {
        item = null;
        return uiSlot >= 1 && ContainerSnapshots(state, kind).TryGetValue(ToProtocolSlot(uiSlot), out item);
    }

    private void RememberLootOwner(ItemSnapshot snapshot, uint fallbackOwner)
    {
        _lastLootItemIndex = unchecked((ushort)snapshot.ItemId);
        _lastLootOwnerType = unchecked((ushort)(snapshot.ItemId >> 16));
        _lastLootOwnerObjectId = unchecked((uint)(snapshot.ItemId >> 32));
        if (_lastLootOwnerObjectId == 0)
            _lastLootOwnerObjectId = fallbackOwner;
    }

    private static bool TryContainer(string table, out X2ContainerKind kind)
    {
        kind = table switch
        {
            "X2Bag" => X2ContainerKind.Bag,
            "X2Bank" => X2ContainerKind.Bank,
            "X2Coffer" => X2ContainerKind.Coffer,
            _ => (X2ContainerKind)(-1),
        };
        return (int)kind >= 0;
    }

    private static bool IsPlayerUnit(string unit) =>
        string.IsNullOrEmpty(unit) || unit.Equals("player", StringComparison.OrdinalIgnoreCase);

    private static bool TryMateTimeline(OnlineSession session, string unit, out short timelineId)
    {
        timelineId = 0;
        var projection = new X2VehicleUiProjection(session.MateSlaveState, session.Entered.UnitId,
            session.Entered.CharacterId, session.PlayerAttachedUnitId);
        uint? objectId = unit.StartsWith("playerpet", StringComparison.Ordinal)
            ? projection.ResolveMateToken(unit)
            : uint.TryParse(unit, out var parsed) ? parsed : null;
        if (objectId is not { } id ||
            !projection.Mates.Any(mate => mate.Exists && mate.UnitId == id.ToString()) ||
            session.MateSlaveState.Units.GetValueOrDefault(id)?.MateSpawned is not { } mate)
            return false;
        timelineId = mate.TimelineId;
        return true;
    }

    private static int ToUiSlot(int protocolSlot) => protocolSlot + 1;
    private static int ToProtocolSlot(int uiSlot) => uiSlot - 1;
    private static byte ToByteSlot(int uiSlot) => checked((byte)ToProtocolSlot(uiSlot));

    private static byte SlotType(X2ContainerKind kind) => kind switch
    {
        X2ContainerKind.Bag => (byte)InventorySlotType.Inventory,
        X2ContainerKind.Bank => (byte)InventorySlotType.Bank,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "coffer has no grounded CSSwapItems slot type"),
    };

    private static int IntArgument(IReadOnlyList<object?> arguments, int index, int fallback = 0) =>
        index >= 0 && index < arguments.Count && arguments[index] is { } value
            ? value switch
            {
                double number => checked((int)number),
                float number => checked((int)number),
                int number => number,
                long number => checked((int)number),
                _ => fallback,
            }
            : fallback;

    private static bool BoolArgument(IReadOnlyList<object?> arguments, int index) =>
        index >= 0 && index < arguments.Count && arguments[index] is not (null or false);

    private static int FieldInt(X2InventoryItem? item, string key, int fallback = 0)
    {
        if (item is null || !item.Fields.TryGetValue(key, out var value) || value is null)
            return fallback;
        return value switch
        {
            double number => checked((int)number),
            float number => checked((int)number),
            int number => number,
            long number => checked((int)number),
            _ => fallback,
        };
    }

    private static long FieldLong(X2InventoryItem? item, string key, long fallback = 0)
    {
        if (item is null || !item.Fields.TryGetValue(key, out var value) || value is null)
            return fallback;
        return value switch
        {
            string text when long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
            double number => checked((long)number),
            int number => number,
            long number => number,
            _ => fallback,
        };
    }
}
