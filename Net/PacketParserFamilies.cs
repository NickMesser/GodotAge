#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>Queue-compatible envelope for the social model's protocol events.</summary>
public sealed record SocialProtocolGameEvent(SocialEvent Value) : GameEvent;

/// <summary>Default ordered S2C parser families for the live client.</summary>
public static class PacketParserFamilies
{
    public static IReadOnlyList<IPacketParserFamily> CreateDefault() =>
    [
        new EnvironmentPacketParserFamily(),
        new HousingPacketParserFamily(),
        new InventoryPacketParserFamily(),
        new QuestPacketParserFamily(),
        new AssignmentPacketParserFamily(),
        new MailPacketParserFamily(),
        new SocialPacketParserFamily(),
        new NpcShopPacketParserFamily(),
        new CraftingPacketParserFamily(),
        new AuctionPacketParserFamily(),
        new VehiclePacketParserFamily(),
        new NonOverlappingCombatPacketParserFamily(),
    ];
}

internal static class ParserFamilyResult
{
    public static IReadOnlyList<GameEvent> One(GameEvent value) => [value];
}

public sealed class InventoryPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode is not (ItemOpcodes.SCCharacterLaborPowerChanged or ItemOpcodes.SCBmPoint or
            ItemOpcodes.SCAddActionPoint or ItemOpcodes.SCCharacterInvenInit or
            ItemOpcodes.SCCharacterInvenContents or ItemOpcodes.SCInvenExpanded or
            ItemOpcodes.SCCharacterPrelimEquipments or ItemOpcodes.SCItemTaskSuccess or
            ItemOpcodes.SCItemTaskNotify or ItemOpcodes.SCItemDetailUpdated or
            ItemOpcodes.SCUnitEquipmentsChanged or ItemOpcodes.SCUnitEquipmentIds or
            ItemOpcodes.SCCofferContentsUpdate or ItemOpcodes.SCLootableState or
            ItemOpcodes.SCUnitLootingState or ItemOpcodes.SCLootBagData or
            ItemOpcodes.SCLootItemTook or ItemOpcodes.SCLootItemFailed or
            ItemOpcodes.SCLootDiceSummary or ItemOpcodes.SCCharacterGamePoints or
            ItemOpcodes.SCGamePointInited or ItemOpcodes.SCGamePointChanged or
            ItemOpcodes.SCUpdatePremiumPoint or ItemOpcodes.SCPremiumPointChanged or
            ItemOpcodes.SCUnlockCurrencySlot))
        {
            events = [];
            return false;
        }
        events = ParserFamilyResult.One(InventoryProtocol.Parse(opcode, body));
        return true;
    }
}

public sealed class QuestPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode is not (QuestProtocol.SCQuests or QuestProtocol.SCCompletedQuests or
            QuestProtocol.SCDoodadQuestAccept or QuestProtocol.SCQuestContextFailed or
            QuestProtocol.SCQuestContextStarted or QuestProtocol.SCQuestContextUpdated or
            QuestProtocol.SCQuestContextCompleted or QuestProtocol.SCQuestContextReset or
            QuestProtocol.SCQuestContextResetBulk or QuestProtocol.SCDoodadCompleteQuest or
            QuestProtocol.SCQuestList))
        {
            events = [];
            return false;
        }
        events = ParserFamilyResult.One((GameEvent)QuestProtocol.Parse(opcode, body));
        return true;
    }
}

public sealed class MailPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode < MailProtocol.SCMailFailed || opcode > MailProtocol.SCMailRemovedFromAccountBox)
        {
            events = [];
            return false;
        }
        events = ParserFamilyResult.One(MailProtocol.Parse(opcode, body));
        return true;
    }
}

public sealed class SocialPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        var parsed = SocialPacketParser.Parse(opcode, body);
        events = parsed is null ? [] : ParserFamilyResult.One(new SocialProtocolGameEvent(parsed));
        return parsed is not null;
    }
}

public sealed class NpcShopPacketParserFamily : IPacketParserFamily
{
    private static readonly HashSet<ushort> Owned =
    [
        NpcShopProtocol.OpNpcInteractionSkills, NpcShopProtocol.OpNpcInteractionEnded,
        NpcShopProtocol.OpWorldInteractionSkills, NpcShopProtocol.OpWorldInteractionCanceled,
        NpcShopProtocol.OpNpcInteractionStatus, NpcShopProtocol.OpSpecialtyRatio,
        NpcShopProtocol.OpSpecialtyGoods, NpcShopProtocol.OpSpecialtyRecords,
        NpcShopProtocol.OpSpecialtyEventMessage, NpcShopProtocol.OpNpcChat,
        NpcShopProtocol.OpDoodadInteractionCallback, NpcShopProtocol.OpSoldItemList,
        NpcShopProtocol.OpSpecialtyCurrent, NpcShopProtocol.OpStoreTradeFailed,
        NpcShopProtocol.OpDoodadFirstInteraction, NpcShopProtocol.OpUpdateMerchantPurchaseLimits,
        NpcShopProtocol.OpMerchantPurchaseLimitFailed, NpcShopProtocol.OpResetMerchantPurchaseLimits,
    ];

    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (!Owned.Contains(opcode))
        {
            events = [];
            return false;
        }
        events = ParserFamilyResult.One(NpcShopProtocol.Parse(opcode, body));
        return true;
    }
}

public sealed class CraftingPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode != CraftingProtocol.SCCraftFailed)
        {
            events = [];
            return false;
        }
        events = ParserFamilyResult.One(CraftingProtocol.ParseCraftFailed(body));
        return true;
    }
}

public sealed class AuctionPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode < AuctionProtocol.SCAuctionPosted || opcode > AuctionProtocol.SCAuctionLimitedPrice)
        {
            events = [];
            return false;
        }
        events = ParserFamilyResult.One(AuctionProtocol.Parse(opcode, body));
        return true;
    }
}

/// <summary>
/// Combat adapter for the default chain. Items exclusively owns 0x1CE-0x1D0 so currency state has one source.
/// </summary>
public sealed class NonOverlappingCombatPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode is >= ItemOpcodes.SCCharacterGamePoints and <= ItemOpcodes.SCGamePointChanged)
        {
            events = [];
            return false;
        }
        events = CombatPacketParsers.Parse(opcode, body);
        return events.Count != 0;
    }
}
