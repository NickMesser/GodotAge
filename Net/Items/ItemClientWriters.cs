#nullable enable
using System;
using System.Collections.Generic;

namespace AAEmu.GodotViewer.Net;

public sealed record ItemClientPacket(ushort Opcode, byte[] Body);
public sealed record NpcBuyEntry(uint GoodId, byte Grade, int Count, byte CurrencyType);
public sealed record NpcSellEntry(byte OwnerType, byte SlotType, byte IndexType, byte Slot,
    ulong ItemId, uint Count, ulong RemoveReservationTime);

/// <summary>
/// Grounded C2S writers for 10.0.2.13. Equip and unequip use CSSwapItems with equipment as one
/// endpoint. Item activation is deliberately absent: this protocol has no CSUseItem packet; the
/// original client activates usable items through skill packets, outside this items-only module.
/// </summary>
public static class ItemClientWriters
{
    public const ushort CSDestroyItem = 0x065;
    public const ushort CSSplitBagItem = 0x066;
    public const ushort CSSwapItems = 0x067;
    public const ushort CSRepairSingleEquipment = 0x069;
    public const ushort CSRepairAllEquipments = 0x06A;
    public const ushort CSLootOpenBag = 0x0CE;
    public const ushort CSLootItem = 0x0CF;
    public const ushort CSLootCloseBag = 0x0D0;
    public const ushort CSBuyItems = 0x0F0;
    public const ushort CSSellItems = 0x0F2;
    public const ushort CSListSoldItem = 0x0F3;
    public const ushort CSInvokeItemSelectiveItemEffect = 0x1BB;

    public static ItemClientPacket Destroy(ulong itemId, byte slotType, byte slot, uint amount) =>
        Packet(CSDestroyItem, new WireWriter().U64(itemId).U8(slotType).U8(slot).U32(amount));

    public static ItemClientPacket Split(ulong fromItemId, ulong toItemId,
        byte fromSlotType, byte fromSlot, byte toSlotType, byte toSlot, int count) =>
        Packet(CSSplitBagItem, new WireWriter().U64(fromItemId).U64(toItemId)
            .U8(fromSlotType).U8(fromSlot).U8(toSlotType).U8(toSlot).S32(count));

    public static ItemClientPacket Swap(ulong fromItemId, ulong toItemId,
        byte fromSlotType, byte fromSlot, byte toSlotType, byte toSlot) =>
        Packet(CSSwapItems, new WireWriter().U64(fromItemId).U64(toItemId)
            .U8(fromSlotType).U8(fromSlot).U8(toSlotType).U8(toSlot));

    public static ItemClientPacket Move(ulong itemId, byte fromSlotType, byte fromSlot,
        byte toSlotType, byte toSlot, ulong displacedItemId = 0) =>
        Swap(itemId, displacedItemId, fromSlotType, fromSlot, toSlotType, toSlot);

    public static ItemClientPacket Equip(ulong itemId, byte bagSlot, byte equipmentSlot,
        ulong displacedItemId = 0, byte inventorySlotType = (byte)InventorySlotType.Inventory,
        byte equipmentSlotType = (byte)InventorySlotType.Equipment) =>
        Swap(itemId, displacedItemId, inventorySlotType, bagSlot, equipmentSlotType, equipmentSlot);

    public static ItemClientPacket Unequip(ulong itemId, byte equipmentSlot, byte bagSlot,
        ulong displacedItemId = 0, byte equipmentSlotType = (byte)InventorySlotType.Equipment,
        byte inventorySlotType = (byte)InventorySlotType.Inventory) =>
        Swap(itemId, displacedItemId, equipmentSlotType, equipmentSlot, inventorySlotType, bagSlot);

    public static ItemClientPacket LootAll(uint ownerObjectId, uint secondaryObjectId = 0) =>
        OpenLoot(ownerObjectId, secondaryObjectId, true);

    public static ItemClientPacket OpenLoot(uint ownerObjectId, uint secondaryObjectId, bool lootAll) =>
        Packet(CSLootOpenBag, new WireWriter().Bc(ownerObjectId).Bc(secondaryObjectId).Bool(lootAll));

    /// <summary>
    /// AAEmu names the final two u16 values u1/u2 and does not interpret them. They remain explicit
    /// required parameters because neither the generated native layout nor available captures identify them.
    /// </summary>
    public static ItemClientPacket LootOne(ushort itemIndex, ushort ownerType, uint ownerObjectId,
        ushort unknown1, ushort unknown2) =>
        Packet(CSLootItem, new WireWriter().U16(itemIndex).U16(ownerType).Bc(ownerObjectId)
            .U16(unknown1).U16(unknown2));

    public static ItemClientPacket CloseLoot(ushort itemIndex, ushort ownerType,
        uint ownerObjectId, byte unknown) =>
        Packet(CSLootCloseBag, new WireWriter().U16(itemIndex).U16(ownerType).Bc(ownerObjectId).U8(unknown));

    public static ItemClientPacket Buy(uint npcObjectId, uint doodadObjectId, uint shopType,
        IReadOnlyList<NpcBuyEntry> entries, IReadOnlyList<int>? buybackSlots = null,
        bool useAaPoint = false, byte openType = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);
        buybackSlots ??= Array.Empty<int>();
        if (entries.Count > byte.MaxValue || buybackSlots.Count > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(entries), "shop request counts are u8");
        var w = new WireWriter().Bc(npcObjectId).Bc(doodadObjectId).U32(shopType)
            .U8((byte)entries.Count).U8((byte)buybackSlots.Count);
        foreach (var entry in entries)
            w.U32(entry.GoodId).U8(entry.Grade).S32(entry.Count).U8(entry.CurrencyType);
        foreach (var slot in buybackSlots) w.S32(slot);
        w.Bool(useAaPoint).U8(openType);
        return Packet(CSBuyItems, w);
    }

    public static ItemClientPacket Sell(uint npcObjectId, uint secondaryObjectId,
        IReadOnlyList<NpcSellEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(entries), "sell entry count is u8");
        var w = new WireWriter().Bc(npcObjectId).Bc(secondaryObjectId).U8((byte)entries.Count);
        foreach (var entry in entries)
            w.U8(entry.OwnerType).U8(entry.SlotType).U8(entry.IndexType).U8(entry.Slot)
                .U64(entry.ItemId).U32(entry.Count).U64(entry.RemoveReservationTime);
        return Packet(CSSellItems, w);
    }

    /// <summary>Requests the merchant buyback container. The body is the merchant's packed object id.</summary>
    public static ItemClientPacket ListSoldItems(uint npcObjectId) =>
        Packet(CSListSoldItem, new WireWriter().Bc(npcObjectId));

    public static ItemClientPacket RepairOne(byte slotType, byte slot,
        bool autoUseAaPoint = false, bool inBag = false) =>
        Packet(CSRepairSingleEquipment, new WireWriter().U8(slotType).U8(slot)
            .Bool(autoUseAaPoint).Bool(inBag));

    public static ItemClientPacket RepairAll(bool autoUseAaPoint = false, bool inBag = false) =>
        Packet(CSRepairAllEquipments, new WireWriter().Bool(autoUseAaPoint).Bool(inBag));

    /// <summary>Invokes a selective item effect. Option indices are one-based in client UI order.</summary>
    public static ItemClientPacket UseSelectiveItem(byte slotType, byte slot, uint tryCount,
        IReadOnlyList<uint> optionIndices)
    {
        ArgumentNullException.ThrowIfNull(optionIndices);
        if (optionIndices.Count > 32)
            throw new ArgumentOutOfRangeException(nameof(optionIndices), "server accepts at most 32 choices");
        var w = new WireWriter().U8(slotType).U8(slot).U32(tryCount).U32((uint)optionIndices.Count);
        foreach (var option in optionIndices)
        {
            if (option == 0)
                throw new ArgumentOutOfRangeException(nameof(optionIndices), "selective item indices are one-based");
            w.U32(option);
        }
        return Packet(CSInvokeItemSelectiveItemEffect, w);
    }

    public static ItemClientPacket UseItem() => throw new NotSupportedException(
        "10.0.2.13 has no grounded CSUseItem packet; item activation is serialized through skill packets");

    private static ItemClientPacket Packet(ushort opcode, WireWriter writer) =>
        new(opcode, writer.ToArray());
}
