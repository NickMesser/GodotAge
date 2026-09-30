#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Mutable item and currency view intended to be owned and updated on the client main thread.
/// Call <see cref="Apply(IInventoryProtocolEvent)"/> in dequeue order for each decoded event.
/// </summary>
public sealed class InventoryState
{
    private readonly Dictionary<(byte SlotType, int Slot), ItemSnapshot> _items = new();
    private readonly Dictionary<(uint UnitId, byte Slot), ItemSnapshot> _unitEquipment = new();
    private readonly Dictionary<(uint UnitId, int Slot), ulong> _unitEquipmentIds = new();
    private readonly Dictionary<(uint CofferId, int Slot), ItemSnapshot> _cofferItems = new();
    private readonly Dictionary<sbyte, long> _gamePoints = new();
    private readonly Dictionary<uint, int> _unitPremiumPoints = new();
    private readonly HashSet<(byte SlotType, byte Slot)> _unlockedCurrencySlots = new();
    private readonly List<ItemSnapshot> _lootItems = new();
    private uint? _localEquipmentUnitId;

    public uint InventorySlots { get; private set; }
    public uint BankSlots { get; private set; }
    public long Money { get; private set; }

    /// <summary>
    /// Sets the balance at world entry (the character record's money). Item tasks only carry ChangeMoney deltas,
    /// so without this the balance starts at 0. Applied once; later deltas add to it.
    /// </summary>
    public void SeedMoney(long balance)
    {
        if (_moneySeeded) return;
        _moneySeeded = true;
        Money += balance;
    }
    private bool _moneySeeded;
    public long BankMoney { get; private set; }
    public long AaPoints { get; private set; }
    public long BankAaPoints { get; private set; }
    public long LoyaltyPoints { get; private set; }
    public long BmPoint => LoyaltyPoints;
    public int Labor { get; private set; }
    public int LocalLabor { get; private set; }
    public int RechargedLabor { get; private set; }
    public int ActionPoints { get; private set; }
    public int PremiumPoints { get; private set; }
    public byte PremiumGrade { get; private set; }
    public byte AutoUseAaPoint { get; private set; }
    public bool Lootable { get; private set; }
    public uint LootOwnerObjectId { get; private set; }
    public IReadOnlyDictionary<(byte SlotType, int Slot), ItemSnapshot> Items => _items;
    public IReadOnlyDictionary<(uint UnitId, byte Slot), ItemSnapshot> UnitEquipment => _unitEquipment;
    public IReadOnlyDictionary<(uint UnitId, int Slot), ulong> UnitEquipmentIds => _unitEquipmentIds;
    public IReadOnlyDictionary<(uint CofferId, int Slot), ItemSnapshot> CofferItems => _cofferItems;
    public IReadOnlyDictionary<sbyte, long> GamePoints => _gamePoints;
    public IReadOnlyDictionary<uint, int> UnitPremiumPoints => _unitPremiumPoints;
    public IReadOnlySet<(byte SlotType, byte Slot)> UnlockedCurrencySlots => _unlockedCurrencySlots;
    public IReadOnlyList<ItemSnapshot> LootItems => _lootItems;
    public UnitLootingEvent? UnitLooting { get; private set; }
    public IReadOnlyList<LootDiceEntry> LastLootRolls { get; private set; } = Array.Empty<LootDiceEntry>();
    public ItemTaskNotifyEvent? LastItemTaskNotify { get; private set; }
    public LootTakenEvent? LastLootTaken { get; private set; }
    public LootFailedEvent? LastLootFailure { get; private set; }
    public string? LastItemTaskDecodeGap { get; private set; }

    public bool TryGetItem(byte slotType, int slot, out ItemSnapshot? item) =>
        _items.TryGetValue((slotType, slot), out item);

    /// <summary>
    /// Seeds a unit's equipment from the full item views embedded in SCUnitState. AAEmu's character
    /// inventory snapshot contains bag and bank slots only, so this supplies the local character's
    /// initial equipment. Later SCItemTaskSuccess moves maintain the same equipment container, while
    /// SCUnitEquipmentsChanged is synchronized into it when received.
    /// </summary>
    public void SeedUnitEquipment(uint unitId, IReadOnlyList<EquipmentSlot> equipment)
    {
        _localEquipmentUnitId = unitId;
        RemoveContainer((byte)InventorySlotType.Equipment);
        foreach (var key in _unitEquipmentIds.Keys.Where(key => key.UnitId == unitId).ToArray())
            _unitEquipmentIds.Remove(key);

        foreach (var view in equipment)
        {
            if (view.Slot is < 0 or > byte.MaxValue || view.TemplateId == 0)
                continue;
            var slot = checked((byte)view.Slot);
            var item = new ItemSnapshot(
                view.TemplateId, view.ItemId, view.Grade, view.Flags, view.Count, view.DetailType, [],
                view.CreateTime, view.LifespanMinutes, view.MadeUnitId, view.WorldId,
                view.UnsecureTime, view.UnpackTime, view.ChargeUseSkillTime,
                Durability: view.Durability, EnchantScale: view.EnchantScale,
                ImageTemplateId: view.ImageTemplateId == 0 ? null : view.ImageTemplateId,
                DyeColor: view.DyeColor == 0 ? null : view.DyeColor);
            _items[((byte)InventorySlotType.Equipment, slot)] = item;
            _unitEquipmentIds[(unitId, view.Slot)] = view.ItemId;
        }
    }

    public void ClearForCharacterLoad()
    {
        _items.Clear();
        _unitEquipment.Clear();
        _unitEquipmentIds.Clear();
        _cofferItems.Clear();
        _gamePoints.Clear();
        _unitPremiumPoints.Clear();
        _unlockedCurrencySlots.Clear();
        _lootItems.Clear();
        _localEquipmentUnitId = null;
        InventorySlots = BankSlots = 0;
        Money = BankMoney = AaPoints = BankAaPoints = LoyaltyPoints = 0;
        _moneySeeded = false;
        Labor = LocalLabor = RechargedLabor = ActionPoints = PremiumPoints = 0;
        PremiumGrade = AutoUseAaPoint = 0;
        Lootable = false;
        LootOwnerObjectId = 0;
        UnitLooting = null;
        LastLootRolls = Array.Empty<LootDiceEntry>();
        LastItemTaskNotify = null;
        LastLootTaken = null;
        LastLootFailure = null;
        LastItemTaskDecodeGap = null;
    }

    public void Apply(IInventoryProtocolEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        switch (e)
        {
            case InventoryInitEvent init:
                ClearForCharacterLoad();
                InventorySlots = init.InventorySlots;
                BankSlots = init.BankSlots;
                break;
            case InventoryContentsEvent contents:
                if (contents.StartChunk == 0)
                    RemoveContainer(contents.SlotType);
                foreach (var slot in contents.Slots)
                    SetSlot(_items, slot.SlotType, slot.Slot, slot.Item);
                break;
            case InventoryExpandedEvent expanded:
                if (expanded.SlotType == (byte)InventorySlotType.Bank)
                    BankSlots = expanded.SlotCount;
                else if (expanded.SlotType == (byte)InventorySlotType.Inventory)
                    InventorySlots = expanded.SlotCount;
                break;
            case PreliminaryEquipmentEvent preliminary:
                // Before world entry this is the only equipment source available. Once SCUnitState has
                // seeded the local owner, AAEmu's empty main/offhand preview must not erase that state.
                if (_localEquipmentUnitId is not null)
                    break;
                RemoveContainer((byte)InventorySlotType.Equipment);
                foreach (var slot in preliminary.Slots)
                    SetSlot(_items, slot.SlotType, slot.Slot, slot.Item);
                break;
            case EquipmentChangedEvent changed:
                foreach (var slot in changed.Slots)
                {
                    if (slot.Slot is < 0 or > byte.MaxValue)
                        continue;
                    var key = (changed.UnitId, (byte)slot.Slot);
                    if (slot.Item is { TemplateId: not 0 } item)
                        _unitEquipment[key] = item;
                    else
                        _unitEquipment.Remove(key);
                    if (_localEquipmentUnitId == changed.UnitId)
                        SetSlot(_items, (byte)InventorySlotType.Equipment, slot.Slot, slot.Item);
                }
                break;
            case EquipmentIdsEvent ids:
                for (var slot = 0; slot < ids.ItemIds.Count; slot++)
                    _unitEquipmentIds[(ids.UnitId, slot)] = ids.ItemIds[slot];
                break;
            case CofferContentsEvent coffer:
                foreach (var key in _cofferItems.Keys.Where(k => k.CofferId == coffer.CofferObjectId).ToArray())
                    _cofferItems.Remove(key);
                foreach (var slot in coffer.Slots)
                    if (slot.Item is { } item)
                        _cofferItems[(coffer.CofferObjectId, slot.Slot)] = item;
                break;
            case ItemDetailEvent detail:
                UpdateItemById(detail.ItemId, item => item with
                {
                    DetailType = detail.DetailType,
                    Detail = detail.Detail,
                    Durability = detail.Durability,
                }, detail.SlotType, detail.Slot);
                break;
            case ItemTaskResultEvent task:
                LastItemTaskDecodeGap = task.DecodeGap;
                foreach (var change in task.Changes)
                    ApplyItemTaskChange(change);
                foreach (var id in task.ForceRemovedItemIds)
                    RemoveItemId(id);
                break;
            case ItemTaskNotifyEvent notify:
                LastItemTaskNotify = notify;
                break;
            case CurrencySnapshotEvent points:
                _gamePoints.Clear();
                for (var i = 0; i < points.GamePoints.Count; i++)
                    _gamePoints[(sbyte)i] = points.GamePoints[i];
                break;
            case CurrencyInitializedEvent point:
                _gamePoints[point.Kind] = point.Value;
                break;
            case CurrencyDeltaEvent delta:
                _gamePoints[(sbyte)delta.Kind] = _gamePoints.GetValueOrDefault((sbyte)delta.Kind) + delta.Amount;
                break;
            case BmPointEvent point:
                LoyaltyPoints = point.Value;
                break;
            case LaborPowerEvent labor:
                Labor = labor.Amount;
                LocalLabor = labor.LocalAmount;
                RechargedLabor = labor.RechargedAmount;
                break;
            case ActionPointDeltaEvent action:
                ActionPoints = action.Value;
                break;
            case PremiumPointEvent premium:
                PremiumPoints = premium.Value;
                PremiumGrade = premium.Grade;
                break;
            case UnitPremiumPointChangedEvent premium:
                _unitPremiumPoints[premium.UnitObjectId] = premium.Point;
                break;
            case CurrencySlotUnlockedEvent unlocked:
                _unlockedCurrencySlots.Add((unlocked.SlotType, unlocked.Slot));
                break;
            case LootableEvent lootable:
                LootOwnerObjectId = lootable.OwnerObjectId;
                Lootable = lootable.HasLoot;
                break;
            case LootBagEvent bag:
                _lootItems.Clear();
                _lootItems.AddRange(bag.Items);
                break;
            case LootTakenEvent taken:
                LastLootTaken = taken;
                if (taken.ItemIndex < _lootItems.Count)
                {
                    var item = _lootItems[taken.ItemIndex];
                    var remaining = Math.Max(0L, (long)item.Count - taken.Count);
                    if (remaining == 0)
                        _lootItems.RemoveAt(taken.ItemIndex);
                    else
                        _lootItems[taken.ItemIndex] = item with { Count = checked((int)remaining) };
                }
                break;
            case LootFailedEvent failed:
                LastLootFailure = failed;
                break;
            case UnitLootingEvent looting:
                UnitLooting = looting;
                break;
            case LootDiceSummaryEvent dice:
                LastLootRolls = dice.Rolls;
                break;
        }
    }

    private void ApplyItemTaskChange(ItemTaskChange change)
    {
        switch (change)
        {
            case ItemAddedChange added:
                var addedItem = added.Item ?? NewItem(added.TemplateId, added.ItemId, added.Count);
                if (addedItem.TemplateId != 0)
                    _items[(added.SlotType, added.Slot)] = addedItem;
                break;
            case ItemRemovedChange removed:
                _items.Remove((removed.SlotType, removed.Slot));
                if (removed.ItemId != 0)
                    RemoveItemId(removed.ItemId);
                break;
            case ItemMovedChange moved:
                Move(moved);
                break;
            case ItemStackChangedChange stack:
                ApplyStack(stack);
                break;
            case ItemDurabilityChangedChange durability:
                UpdateItemById(durability.ItemId, item => item with
                {
                    Durability = durability.Durability,
                    Detail = durability.Detail,
                }, durability.SlotType, durability.Slot);
                break;
            case ItemGradeChangedChange grade:
                UpdateItemById(grade.ItemId, item => item with { Grade = grade.Grade }, grade.SlotType, grade.Slot);
                break;
            case ItemFlagsChangedChange flags:
                UpdateItemById(flags.ItemId, item => item with { Flags = flags.Flags }, flags.SlotType, flags.Slot);
                break;
            case ItemCurrencyChangedChange currency:
                ApplyCurrency(currency);
                break;
            case ItemAutoUseAaPointChangedChange autoUse:
                AutoUseAaPoint = autoUse.Value;
                break;
            case ItemCraftingRemovedChange crafting:
                RemoveItemId(crafting.ItemId);
                break;
        }
    }

    private void ApplyStack(ItemStackChangedChange change)
    {
        if (change.SlotType is { } type && change.Slot is { } slot && _items.TryGetValue((type, slot), out var item))
        {
            var count = Math.Max(0, item.Count + change.Delta);
            if (count == 0) _items.Remove((type, slot));
            else _items[(type, slot)] = item with { Count = checked((int)count) };
            return;
        }

        if (change.SlotType is { } newType && change.Slot is { } newSlot)
        {
            if (change.Delta > 0 && change.TemplateId != 0)
                _items[(newType, newSlot)] = NewItem(change.TemplateId, change.ItemId, checked((int)change.Delta));
            return;
        }

        var existing = _items.FirstOrDefault(kv => kv.Value.TemplateId == change.TemplateId);
        if (existing.Value is { } matching)
        {
            var count = Math.Max(0, matching.Count + change.Delta);
            if (count == 0) _items.Remove(existing.Key);
            else _items[existing.Key] = matching with { Count = checked((int)count) };
        }
    }

    private void Move(ItemMovedChange change)
    {
        var from = (change.FromSlotType, (int)change.FromSlot);
        var to = (change.ToSlotType, (int)change.ToSlot);
        _items.TryGetValue(from, out var source);
        _items.TryGetValue(to, out var displaced);
        source ??= FindById(change.ItemId);
        if (source is not null)
            _items[to] = source;
        else
            _items.Remove(to);
        if (change.DisplacedItemId != 0 && displaced is not null)
            _items[from] = displaced;
        else
            _items.Remove(from);
    }

    private void ApplyCurrency(ItemCurrencyChangedChange change)
    {
        switch (change.Action)
        {
            case ItemWireAction.ChangeMoney: Money += change.Delta; break;
            case ItemWireAction.ChangeBankMoney: BankMoney += change.Delta; break;
            case ItemWireAction.ChangeAaPoint: AaPoints += change.Delta; break;
            case ItemWireAction.ChangeBankAaPoint: BankAaPoints += change.Delta; break;
            case ItemWireAction.ChangeGamePoint when change.Kind is { } kind:
                _gamePoints[(sbyte)kind] = _gamePoints.GetValueOrDefault((sbyte)kind) + change.Delta;
                break;
        }
    }

    private void UpdateItemById(ulong id, Func<ItemSnapshot, ItemSnapshot> update,
        byte slotType, byte slot)
    {
        if (_items.TryGetValue((slotType, slot), out var atSlot) && (id == 0 || atSlot.ItemId == id))
        {
            _items[(slotType, slot)] = update(atSlot);
            return;
        }
        var found = _items.FirstOrDefault(pair => pair.Value.ItemId == id);
        if (found.Value is not null)
            _items[found.Key] = update(found.Value);
    }

    private ItemSnapshot? FindById(ulong id) =>
        _items.Values.FirstOrDefault(item => item.ItemId == id);

    private void RemoveItemId(ulong id)
    {
        foreach (var key in _items.Where(pair => pair.Value.ItemId == id).Select(pair => pair.Key).ToArray())
            _items.Remove(key);
    }

    private void RemoveContainer(byte slotType)
    {
        foreach (var key in _items.Keys.Where(key => key.SlotType == slotType).ToArray())
            _items.Remove(key);
    }

    private static void SetSlot<T>(IDictionary<(T Container, int Slot), ItemSnapshot> destination,
        T container, int slot, ItemSnapshot? item) where T : notnull
    {
        if (item is null || item.TemplateId == 0)
            destination.Remove((container, slot));
        else
            destination[(container, slot)] = item;
    }

    private static ItemSnapshot NewItem(uint template, ulong id, int count) =>
        new(template, id, 0, 0, count, 0, Array.Empty<byte>(), 0, 0, 0, 0, 0, 0, 0);
}
