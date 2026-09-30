#nullable enable
using System;
using System.Collections.Generic;

namespace AAEmu.GodotViewer.Net;

/// <summary>10.0.2.13 inventory and loot opcodes handled by <see cref="InventoryProtocol"/>.</summary>
public static class ItemOpcodes
{
    public const ushort SCCharacterLaborPowerChanged = 0x070;
    public const ushort SCBmPoint = 0x071;
    public const ushort SCAddActionPoint = 0x072;
    public const ushort SCCharacterInvenInit = 0x076;
    public const ushort SCCharacterInvenContents = 0x077;
    public const ushort SCInvenExpanded = 0x078;
    public const ushort SCCharacterPrelimEquipments = 0x079;
    public const ushort SCItemTaskSuccess = 0x0BC;
    public const ushort SCItemTaskNotify = 0x0BD;
    public const ushort SCItemDetailUpdated = 0x0BE;
    public const ushort SCUnitEquipmentsChanged = 0x0BF;
    public const ushort SCUnitEquipmentIds = 0x0C1;
    public const ushort SCCofferContentsUpdate = 0x0C2;
    public const ushort SCLootableState = 0x135;
    public const ushort SCUnitLootingState = 0x136;
    public const ushort SCLootBagData = 0x137;
    public const ushort SCLootItemTook = 0x138;
    public const ushort SCLootItemFailed = 0x139;
    public const ushort SCLootDiceSummary = 0x13C;
    public const ushort SCCharacterGamePoints = 0x1CE;
    public const ushort SCGamePointInited = 0x1CF;
    public const ushort SCGamePointChanged = 0x1D0;
    public const ushort SCUpdatePremiumPoint = 0x288;
    public const ushort SCPremiumPointChanged = 0x289;
    public const ushort SCUnlockCurrencySlot = 0x28F;
}

public enum InventorySlotType : byte
{
    None = 0,
    Equipment = 1,
    Inventory = 2,
    Bank = 3,
    Trade = 4,
    Mail = 5,
    Auction = 6,
    EquipmentMate = 237,
    EquipmentSlave = 242,
    System = 255,
}

/// <summary>Character-sheet indices used by SCCharacterGamePoints (10.0.2.13).</summary>
public static class GamePointSlots
{
    public const sbyte Honor = 0;
    public const sbyte Vocation = 1;
    public const sbyte CurrentLeadership = 11;
    public const sbyte PreviousLeadership = 12;
}

public enum ItemWireAction : byte
{
    Invalid = 0, ChangeMoney = 1, ChangeBankMoney = 2, ChangeGamePoint = 3,
    AddStack = 4, Create = 5, Take = 6, Remove = 7, RemoveReservation = 8,
    SwapSlot = 9, UpdateDetail = 10, SetFlags = 11, UpdateFlags = 12,
    RemoveCrafting = 13, Seize = 14, ChangeGrade = 15, ChangeOwner = 16,
    ChangeAaPoint = 17, ChangeBankAaPoint = 18, ChangeAutoUseAaPoint = 19,
    UpdateChargeUseSkillTime = 20, ButlerItemSwap = 21,
}

public sealed record ItemSnapshot(
    uint TemplateId, ulong ItemId, byte Grade, byte Flags, int Count, byte DetailType,
    byte[] Detail, long CreateTime, int LifespanMinutes, ulong MadeUnitId, byte WorldId,
    long UnsecureTime, long UnpackTime, long ChargeUseSkillTime,
    byte? Durability = null, ushort? EnchantScale = null, uint? ImageTemplateId = null, uint? DyeColor = null);

public sealed record InventoryItemSlot(byte SlotType, int Slot, ItemSnapshot? Item);
public sealed record CofferItemSlot(byte Slot, ItemSnapshot? Item);

public interface IInventoryProtocolEvent
{
    ushort Opcode { get; }
    byte[] RawBody { get; }
}

public abstract record InventoryProtocolEvent(ushort Opcode, byte[] RawBody) : GameEvent, IInventoryProtocolEvent;
public sealed record InventoryInitEvent(uint InventorySlots, uint BankSlots, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCCharacterInvenInit, Raw);
public sealed record InventoryContentsEvent(byte SlotType, byte ChunkCount, byte StartChunk,
    IReadOnlyList<InventoryItemSlot> Slots, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCCharacterInvenContents, Raw);
public sealed record InventoryExpandedEvent(byte SlotType, byte SlotCount, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCInvenExpanded, Raw);
public sealed record PreliminaryEquipmentEvent(IReadOnlyList<InventoryItemSlot> Slots, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCCharacterPrelimEquipments, Raw);
public sealed record ItemDetailEvent(ulong ItemId, byte SlotType, byte Slot, byte DetailType,
    byte[] Detail, byte? Durability, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCItemDetailUpdated, Raw);
public sealed record ItemTaskNotifyEvent(ulong NativeField1, string DecodeGap, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCItemTaskNotify, Raw);
public sealed record EquipmentChangedEvent(uint UnitId, bool CharacterTransform,
    IReadOnlyList<InventoryItemSlot> Slots, ulong Flags, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCUnitEquipmentsChanged, Raw);
public sealed record EquipmentIdsEvent(uint UnitId, IReadOnlyList<ulong> ItemIds, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCUnitEquipmentIds, Raw);
public sealed record CofferContentsEvent(uint CofferObjectId, byte OwnerType, long OwnerId,
    bool Opened, IReadOnlyList<CofferItemSlot> Slots, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCCofferContentsUpdate, Raw);
public sealed record CurrencySnapshotEvent(IReadOnlyList<int> GamePoints, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCCharacterGamePoints, Raw);
public sealed record CurrencyDeltaEvent(byte Kind, int Amount, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCGamePointChanged, Raw);
public sealed record CurrencyInitializedEvent(sbyte Kind, uint Value, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCGamePointInited, Raw);
public sealed record BmPointEvent(long Value, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCBmPoint, Raw);
public sealed record ActionPointDeltaEvent(int Action, int Value, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCAddActionPoint, Raw);
public sealed record PremiumPointEvent(int Value, byte OldGrade, byte Grade, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCUpdatePremiumPoint, Raw);
public sealed record UnitPremiumPointChangedEvent(uint UnitObjectId, int Point, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCPremiumPointChanged, Raw);
public sealed record CurrencySlotUnlockedEvent(byte SlotType, byte Slot, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCUnlockCurrencySlot, Raw);
public sealed record LaborPowerEvent(int Amount, int LocalAmount, int RechargedAmount,
    uint ActabilityId, uint ActabilityPoint, byte Step, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCCharacterLaborPowerChanged, Raw);

public abstract record ItemTaskChange(ItemWireAction Action, byte LogType);
public sealed record ItemAddedChange(ItemWireAction WireAction, byte Log, byte SlotType, byte Slot,
    ItemSnapshot? Item, ulong ItemId, uint TemplateId, int Count)
    : ItemTaskChange(WireAction, Log);
public sealed record ItemRemovedChange(ItemWireAction WireAction, byte Log, byte OwnerType,
    byte SlotType, byte Slot, ulong ItemId, ItemSnapshot? Item = null)
    : ItemTaskChange(WireAction, Log);
public sealed record ItemMovedChange(byte Log, byte FromSlotType, byte FromSlot,
    byte ToSlotType, byte ToSlot, ulong ItemId, ulong DisplacedItemId)
    : ItemTaskChange(ItemWireAction.SwapSlot, Log);
public sealed record ItemStackChangedChange(byte Log, byte? SlotType, byte? Slot,
    ulong ItemId, uint TemplateId, long Delta)
    : ItemTaskChange(SlotType.HasValue ? ItemWireAction.Create : ItemWireAction.AddStack, Log);
public sealed record ItemDurabilityChangedChange(byte Log, byte SlotType, byte Slot,
    ulong ItemId, byte? Durability, byte[] Detail)
    : ItemTaskChange(ItemWireAction.UpdateDetail, Log);
public sealed record ItemGradeChangedChange(byte Log, byte SlotType, byte Slot,
    ulong ItemId, byte Grade)
    : ItemTaskChange(ItemWireAction.ChangeGrade, Log);
public sealed record ItemFlagsChangedChange(ItemWireAction WireAction, byte Log, byte SlotType,
    byte Slot, ulong ItemId, byte Flags)
    : ItemTaskChange(WireAction, Log);
public sealed record ItemCurrencyChangedChange(ItemWireAction WireAction, byte Log,
    long Delta, byte? Kind = null)
    : ItemTaskChange(WireAction, Log);
public sealed record ItemAutoUseAaPointChangedChange(byte Log, byte Value)
    : ItemTaskChange(ItemWireAction.ChangeAutoUseAaPoint, Log);
public sealed record ItemCraftingRemovedChange(byte Log, ulong ItemId)
    : ItemTaskChange(ItemWireAction.RemoveCrafting, Log);

public sealed record ItemTaskResultEvent(byte OwnerType, byte TaskType,
    IReadOnlyList<ItemTaskChange> Changes, IReadOnlyList<ulong> ForceRemovedItemIds,
    long TrailingType, int LockItemSlotKey, bool QueryResult, ulong Flags,
    string? DecodeGap, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCItemTaskSuccess, Raw);

public sealed record LootableEvent(ushort OwnerType, uint OwnerObjectId, bool HasLoot, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCLootableState, Raw);
public sealed record LootBagEvent(IReadOnlyList<ItemSnapshot> Items, bool LootAll, bool? AutoLoot,
    string? DecodeGap, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCLootBagData, Raw);
public sealed record LootTakenEvent(uint TemplateId, ushort ItemIndex, ushort OwnerType,
    uint OwnerObjectId, uint Count, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCLootItemTook, Raw);
public sealed record LootFailedEvent(ushort ClientValue, ushort Error, long ItemId,
    int OwnerType, uint OwnerObjectId, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCLootItemFailed, Raw);
public sealed record UnitLootingEvent(uint UnitObjectId, byte State, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCUnitLootingState, Raw);
public sealed record LootDiceEntry(ulong CharacterId, sbyte Roll);
public sealed record LootDiceSummaryEvent(ushort ItemIndex, ushort OwnerType, uint OwnerObjectId,
    IReadOnlyList<LootDiceEntry> Rolls, byte[] Raw)
    : InventoryProtocolEvent(ItemOpcodes.SCLootDiceSummary, Raw);

/// <summary>
/// Pure packet-body parsers. Bodies start after the opcode. Layouts follow AAEmu's G2C writers;
/// parsers retain the full body so later native-layout corrections do not discard evidence.
/// </summary>
public static class InventoryProtocol
{
    private const int EquipmentSlotCount = 34;

    public static InventoryProtocolEvent Parse(ushort opcode, byte[] body) => opcode switch
    {
        ItemOpcodes.SCCharacterInvenInit => ParseInventoryInit(body),
        ItemOpcodes.SCCharacterInvenContents => ParseInventoryContents(body),
        ItemOpcodes.SCInvenExpanded => ParseInventoryExpanded(body),
        ItemOpcodes.SCCharacterPrelimEquipments => ParsePreliminaryEquipment(body),
        ItemOpcodes.SCItemTaskSuccess => ParseItemTaskResult(body),
        ItemOpcodes.SCItemTaskNotify => ParseItemTaskNotify(body),
        ItemOpcodes.SCItemDetailUpdated => ParseItemDetail(body),
        ItemOpcodes.SCUnitEquipmentsChanged => ParseEquipmentChanged(body),
        ItemOpcodes.SCUnitEquipmentIds => ParseEquipmentIds(body),
        ItemOpcodes.SCCofferContentsUpdate => ParseCofferContents(body),
        ItemOpcodes.SCBmPoint => ParseBmPoint(body),
        ItemOpcodes.SCAddActionPoint => ParseActionPoint(body),
        ItemOpcodes.SCCharacterGamePoints => ParseCurrencySnapshot(body),
        ItemOpcodes.SCGamePointInited => ParseCurrencyInitialized(body),
        ItemOpcodes.SCGamePointChanged => ParseCurrencyDelta(body),
        ItemOpcodes.SCCharacterLaborPowerChanged => ParseLaborPower(body),
        ItemOpcodes.SCUpdatePremiumPoint => ParsePremiumPoint(body),
        ItemOpcodes.SCPremiumPointChanged => ParseUnitPremiumPointChanged(body),
        ItemOpcodes.SCUnlockCurrencySlot => ParseCurrencySlotUnlocked(body),
        ItemOpcodes.SCLootableState => ParseLootable(body),
        ItemOpcodes.SCUnitLootingState => ParseUnitLooting(body),
        ItemOpcodes.SCLootBagData => ParseLootBag(body),
        ItemOpcodes.SCLootItemTook => ParseLootTaken(body),
        ItemOpcodes.SCLootItemFailed => ParseLootFailed(body),
        ItemOpcodes.SCLootDiceSummary => ParseLootDiceSummary(body),
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), $"unsupported item opcode 0x{opcode:X3}"),
    };

    public static InventoryInitEvent ParseInventoryInit(byte[] body)
    {
        var r = new WireReader(body);
        return new InventoryInitEvent(r.U32(), r.U32(), body);
    }

    public static InventoryContentsEvent ParseInventoryContents(byte[] body)
    {
        var r = new WireReader(body);
        var type = r.U8();
        var chunks = r.U8();
        var start = r.U8();
        var count = checked(chunks * 10);
        var slots = new List<InventoryItemSlot>(count);
        for (var i = 0; i < count; i++)
            slots.Add(new InventoryItemSlot(type, start * 10 + i, ReadItem(r)));
        EnsureEnd(r, nameof(ParseInventoryContents));
        return new InventoryContentsEvent(type, chunks, start, slots, body);
    }

    public static InventoryExpandedEvent ParseInventoryExpanded(byte[] body)
    {
        var r = new WireReader(body);
        return new InventoryExpandedEvent(r.U8(), r.U8(), body);
    }

    public static PreliminaryEquipmentEvent ParsePreliminaryEquipment(byte[] body)
    {
        var r = new WireReader(body);
        var count = checked((int)r.U32());
        var slots = new List<InventoryItemSlot>(count);
        for (var i = 0; i < count; i++)
        {
            var slot = r.S8();
            slots.Add(new InventoryItemSlot((byte)InventorySlotType.Equipment, slot, ReadItem(r)));
        }
        EnsureEnd(r, nameof(ParsePreliminaryEquipment));
        return new PreliminaryEquipmentEvent(slots, body);
    }

    public static ItemDetailEvent ParseItemDetail(byte[] body)
    {
        var r = new WireReader(body);
        var id = r.U64();
        var type = r.U8();
        var slot = r.U8();
        var detailType = r.U8();
        var detail = ReadItemDetail(r, detailType, out var durability, out _, out _, out _);
        EnsureEnd(r, nameof(ParseItemDetail));
        return new ItemDetailEvent(id, type, slot, detailType, detail, durability, body);
    }

    public static EquipmentChangedEvent ParseEquipmentChanged(byte[] body)
    {
        var r = new WireReader(body);
        var id = r.Bc();
        var count = r.U8();
        var transform = r.Bool();
        var slots = new List<InventoryItemSlot>(count);
        for (var i = 0; i < count; i++)
        {
            var slot = r.S8();
            slots.Add(new InventoryItemSlot((byte)InventorySlotType.Equipment, slot, ReadItem(r)));
        }
        var flags = r.U64();
        EnsureEnd(r, nameof(ParseEquipmentChanged));
        return new EquipmentChangedEvent(id, transform, slots, flags, body);
    }

    public static EquipmentIdsEvent ParseEquipmentIds(byte[] body)
    {
        var r = new WireReader(body);
        var id = r.Bc();
        var ids = new ulong[EquipmentSlotCount];
        for (var i = 0; i < ids.Length; i++) ids[i] = r.U64();
        EnsureEnd(r, nameof(ParseEquipmentIds));
        return new EquipmentIdsEvent(id, ids, body);
    }

    public static CofferContentsEvent ParseCofferContents(byte[] body)
    {
        var r = new WireReader(body);
        var objectId = r.Bc(); var ownerType = r.U8(); var ownerId = r.S64(); var opened = r.Bool();
        var count = r.U8();
        var slots = new List<CofferItemSlot>(count);
        for (var i = 0; i < count; i++)
        {
            var slot = r.U8();
            slots.Add(new CofferItemSlot(slot, ReadItem(r)));
        }
        EnsureEnd(r, nameof(ParseCofferContents));
        return new CofferContentsEvent(objectId, ownerType, ownerId, opened, slots, body);
    }

    public static CurrencySnapshotEvent ParseCurrencySnapshot(byte[] body)
    {
        var r = new WireReader(body);
        var points = new int[14];
        for (var i = 0; i < points.Length; i++) points[i] = r.S32();
        EnsureEnd(r, nameof(ParseCurrencySnapshot));
        return new CurrencySnapshotEvent(points, body);
    }

    public static CurrencyDeltaEvent ParseCurrencyDelta(byte[] body)
    {
        var r = new WireReader(body);
        var count = r.U8();
        if (count != 1) throw new WireException($"single-delta parser received {count} entries");
        var result = new CurrencyDeltaEvent(r.U8(), r.S32(), body);
        EnsureEnd(r, nameof(ParseCurrencyDelta));
        return result;
    }

    public static CurrencyInitializedEvent ParseCurrencyInitialized(byte[] body)
    {
        var r = new WireReader(body);
        var result = new CurrencyInitializedEvent(r.S8(), r.U32(), body);
        EnsureEnd(r, nameof(ParseCurrencyInitialized));
        return result;
    }

    public static BmPointEvent ParseBmPoint(byte[] body)
    {
        var r = new WireReader(body); var result = new BmPointEvent(r.S64(), body);
        EnsureEnd(r, nameof(ParseBmPoint)); return result;
    }

    public static ActionPointDeltaEvent ParseActionPoint(byte[] body)
    {
        var r = new WireReader(body); var result = new ActionPointDeltaEvent(r.S32(), r.S32(), body);
        EnsureEnd(r, nameof(ParseActionPoint)); return result;
    }

    public static PremiumPointEvent ParsePremiumPoint(byte[] body)
    {
        var r = new WireReader(body); var result = new PremiumPointEvent(r.S32(), r.U8(), r.U8(), body);
        EnsureEnd(r, nameof(ParsePremiumPoint)); return result;
    }

    public static UnitPremiumPointChangedEvent ParseUnitPremiumPointChanged(byte[] body)
    {
        var r = new WireReader(body);
        var result = new UnitPremiumPointChangedEvent(r.Bc(), r.S32(), body);
        EnsureEnd(r, nameof(ParseUnitPremiumPointChanged)); return result;
    }

    public static CurrencySlotUnlockedEvent ParseCurrencySlotUnlocked(byte[] body)
    {
        var r = new WireReader(body); var result = new CurrencySlotUnlockedEvent(r.U8(), r.U8(), body);
        EnsureEnd(r, nameof(ParseCurrencySlotUnlocked)); return result;
    }

    public static LaborPowerEvent ParseLaborPower(byte[] body)
    {
        var r = new WireReader(body);
        var amount = r.S32(); var local = r.S32(); var recharged = r.S32();
        var pair = r.Pisc(2);
        var result = new LaborPowerEvent(amount, local, recharged, pair[0], pair[1], r.U8(), body);
        EnsureEnd(r, nameof(ParseLaborPower));
        return result;
    }

    public static ItemTaskResultEvent ParseItemTaskResult(byte[] body)
    {
        var r = new WireReader(body);
        var owner = r.U8();
        var taskType = r.U8();
        var count = r.U8();
        var changes = new List<ItemTaskChange>(count);
        string? gap = null;
        try
        {
            for (var i = 0; i < count; i++) changes.Add(ReadTask(r));
        }
        catch (UnsupportedTaskActionException e)
        {
            // ItemTask entries are not length-prefixed. Once an unknown action occurs, task and trailer
            // boundaries cannot be recovered safely; keep the complete body instead of guessing.
            gap = e.Message;
            return new ItemTaskResultEvent(owner, taskType, changes, Array.Empty<ulong>(), 0, 0, false, 0, gap, body);
        }
        var forceCount = r.U8();
        var removed = new ulong[forceCount];
        for (var i = 0; i < removed.Length; i++) removed[i] = r.U64();
        var trailing = r.S64();
        var lockKey = r.S32();
        var query = r.Bool();
        var flags = r.U64();
        EnsureEnd(r, nameof(ParseItemTaskResult));
        return new ItemTaskResultEvent(owner, taskType, changes, removed, trailing, lockKey, query, flags, gap, body);
    }

    public static ItemTaskNotifyEvent ParseItemTaskNotify(byte[] body)
    {
        var r = new WireReader(body);
        var result = new ItemTaskNotifyEvent(r.U64(),
            "native serializer names this sole u64 Field1; its semantics are not recovered", body);
        EnsureEnd(r, nameof(ParseItemTaskNotify));
        return result;
    }

    private static ItemTaskChange ReadTask(WireReader r)
    {
        var action = (ItemWireAction)r.U8();
        var log = r.U8();
        switch (action)
        {
            case ItemWireAction.ChangeMoney:
            case ItemWireAction.ChangeBankMoney:
            case ItemWireAction.ChangeAaPoint:
            case ItemWireAction.ChangeBankAaPoint:
                return new ItemCurrencyChangedChange(action, log, r.S64());
            case ItemWireAction.ChangeGamePoint:
            {
                var kind = r.U8();
                return new ItemCurrencyChangedChange(action, log, r.S32(), kind);
            }
            case ItemWireAction.AddStack:
            {
                var template = r.U32();
                return new ItemStackChangedChange(log, null, null, 0, template, r.S64());
            }
            case ItemWireAction.Create:
            {
                var type = r.U8(); var slot = r.U8(); var id = r.U64(); var amount = r.S32(); var template = r.U32();
                return new ItemStackChangedChange(log, type, slot, id, template, amount);
            }
            case ItemWireAction.Take:
            case ItemWireAction.Remove:
            {
                var type = r.U8(); var slot = r.U8(); var item = ReadItem(r);
                return action == ItemWireAction.Take
                    ? new ItemAddedChange(action, log, type, slot, item, item?.ItemId ?? 0, item?.TemplateId ?? 0, item?.Count ?? 0)
                    : new ItemRemovedChange(action, log, 0, type, slot, item?.ItemId ?? 0, item);
            }
            case ItemWireAction.SwapSlot:
            {
                var fromType = r.U8(); var fromSlot = r.U8(); var toType = r.U8(); var toSlot = r.U8();
                var fromId = r.U64(); var toId = r.U64(); r.S32();
                return new ItemMovedChange(log, fromType, fromSlot, toType, toSlot, fromId, toId);
            }
            case ItemWireAction.UpdateDetail:
            {
                var type = r.U8(); var slot = r.U8(); var id = r.U64(); var size = r.S16();
                if (size < 0) throw new WireException($"negative item detail size {size}");
                var detail = r.Bytes(size);
                return new ItemDurabilityChangedChange(log, type, slot, id,
                    detail.Length > 0 ? detail[0] : null, detail);
            }
            case ItemWireAction.SetFlags:
            {
                var type = r.U8(); var slot = r.U8(); var id = r.U64(); var bits = r.U8(); r.U64();
                return new ItemFlagsChangedChange(action, log, type, slot, id, bits);
            }
            case ItemWireAction.UpdateFlags:
            {
                r.U8(); var type = r.U8(); var slot = r.U8(); var id = r.U64();
                var bits = r.U8(); r.U8(); r.Bool(); r.Bool(); r.Bool(); r.S64(); r.S64();
                return new ItemFlagsChangedChange(action, log, type, slot, id, bits);
            }
            case ItemWireAction.RemoveCrafting:
                return new ItemCraftingRemovedChange(log, r.U64());
            case ItemWireAction.Seize:
            {
                r.U8(); var type = r.U8(); var slot = r.U8(); var id = r.U64();
                return new ItemRemovedChange(action, log, 0, type, slot, id);
            }
            case ItemWireAction.ChangeGrade:
            {
                var type = r.U8(); var slot = r.U8(); var id = r.U64(); var grade = r.U8();
                return new ItemGradeChangedChange(log, type, slot, id, grade);
            }
            case ItemWireAction.ChangeAutoUseAaPoint:
                return new ItemAutoUseAaPointChangedChange(log, r.U8());
            default:
                throw new UnsupportedTaskActionException($"item action {(byte)action} has no grounded boundary/layout; raw packet retained");
        }
    }

    public static LootableEvent ParseLootable(byte[] body)
    {
        var r = new WireReader(body);
        var iid = r.U64();
        var result = new LootableEvent((ushort)(iid >> 16), (uint)(iid >> 32), r.Bool(), body);
        EnsureEnd(r, nameof(ParseLootable));
        return result;
    }

    public static LootBagEvent ParseLootBag(byte[] body)
    {
        var r = new WireReader(body);
        var count = r.U8();
        var items = new List<ItemSnapshot>(count);
        for (var i = 0; i < count; i++)
            items.Add(ReadItem(r) ?? throw new WireException("loot entry used the empty-item sentinel"));
        var lootAll = r.Bool();
        bool? autoLoot = null;
        string? gap = null;
        if (r.Remaining == 1)
        {
            autoLoot = r.Bool();
            gap = "native generated body includes autoLoot; current AAEmu writer omits it";
        }
        else if (r.Remaining != 0)
            throw new WireException($"loot bag has {r.Remaining} unexplained trailing bytes");
        return new LootBagEvent(items, lootAll, autoLoot, gap, body);
    }

    public static UnitLootingEvent ParseUnitLooting(byte[] body)
    {
        var r = new WireReader(body); var result = new UnitLootingEvent(r.Bc(), r.U8(), body);
        EnsureEnd(r, nameof(ParseUnitLooting)); return result;
    }

    public static LootTakenEvent ParseLootTaken(byte[] body)
    {
        var r = new WireReader(body);
        var template = checked((uint)r.U64()); var iid = r.U64(); var count = r.U32();
        EnsureEnd(r, nameof(ParseLootTaken));
        return new LootTakenEvent(template, (ushort)iid, (ushort)(iid >> 16), (uint)(iid >> 32), count, body);
    }

    public static LootFailedEvent ParseLootFailed(byte[] body)
    {
        var r = new WireReader(body);
        var result = new LootFailedEvent(r.U16(), r.U16(), r.S64(), r.S32(), r.Bc(), body);
        EnsureEnd(r, nameof(ParseLootFailed));
        return result;
    }

    public static LootDiceSummaryEvent ParseLootDiceSummary(byte[] body)
    {
        var r = new WireReader(body);
        var index = r.U16(); var ownerType = r.U16(); var ownerId = r.Bc(); r.U8();
        var count = r.S32();
        if (count < 0) throw new WireException($"negative loot dice entry count {count}");
        var rolls = new List<LootDiceEntry>(count);
        for (var i = 0; i < count; i++) rolls.Add(new LootDiceEntry(r.U64(), r.S8()));
        EnsureEnd(r, nameof(ParseLootDiceSummary));
        return new LootDiceSummaryEvent(index, ownerType, ownerId, rolls, body);
    }

    public static ItemSnapshot? ReadItem(WireReader r)
    {
        var template = r.U32();
        if (template == 0) return null;
        var id = r.U64(); var grade = r.U8(); var flags = r.U8(); var count = r.S32(); var detailType = r.U8();
        var detail = ReadItemDetail(r, detailType, out var durability, out var enchant, out var image, out var dye);
        return new ItemSnapshot(template, id, grade, flags, count, detailType, detail,
            r.S64(), r.S32(), r.U64(), r.U8(), r.S64(), r.S64(), r.S64(), durability, enchant, image, dye);
    }

    private static byte[] ReadItemDetail(WireReader r, byte detailType, out byte? durability,
        out ushort? enchantScale, out uint? image, out uint? dye)
    {
        durability = null; enchantScale = null; image = null; dye = null;
        var start = r.Pos;
        if (detailType == 1)
        {
            durability = r.U8(); r.U16(); r.S64(); enchantScale = r.U16(); r.U16(); r.S64(); r.U8(); r.U8();
            var gems = r.Pisc(18); image = gems[0]; dye = gems[2];
        }
        else
            r.Bytes(DetailLength(detailType));
        return r.Buffer.AsSpan(start, r.Pos - start).ToArray();
    }

    private static int DetailLength(byte type) => type switch
    {
        2 => 33, 3 => 20, 4 => 9, 5 or 11 => 24, 6 or 7 => 16,
        8 or 14 => 8, 9 => 4, 10 => 12, 12 => 10, 13 => 13, _ => 0,
    };

    private static void EnsureEnd(WireReader r, string parser)
    {
        if (r.Remaining != 0) throw new WireException($"{parser} left {r.Remaining} bytes");
    }

    private sealed class UnsupportedTaskActionException(string message) : Exception(message);
}
