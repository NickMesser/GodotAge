#nullable enable
namespace AAEmu.GodotViewer.Net;

/// <summary>One cargo/specialty quote written by AAEmu's <c>SpecialtyQuote.Write</c>.</summary>
public sealed record SpecialtyGoodsQuote(
    uint ItemTemplateId,
    ulong Refund,
    ulong RefundWithoutEvent,
    uint Ratio,
    uint Stock,
    bool CanProduce,
    byte Currency,
    sbyte Type);

public sealed record SpecialtyGoodsPageEvent(
    ushort? ZoneGroupId,
    uint? NpcTemplateId,
    IReadOnlyList<SpecialtyGoodsQuote> Quotes,
    IReadOnlyList<uint> EventIds,
    bool IsBegin,
    bool IsEnd,
    bool IsSalePriceList) : GameEvent;

public sealed record SpecialtyMarketRecord(int Ratio, long RecordedAt);

public sealed record SpecialtyRecordsEvent(
    ushort ZoneGroupId,
    uint ItemTemplateId,
    IReadOnlyList<SpecialtyMarketRecord> Records) : GameEvent;

public sealed record SpecialtyEventMessageEvent(string Message) : GameEvent;

public sealed record SpecialtyCurrentRatio(uint ItemTemplateId, uint Rate);

public sealed record SpecialtyCurrentEvent(
    ushort FromZoneGroup,
    ushort ToZoneGroup,
    IReadOnlyList<SpecialtyCurrentRatio> Ratios) : GameEvent;

/// <summary>
/// Canonical Item.Write snapshot used by the buyback list. <see cref="Raw"/> retains the complete
/// item body, including detail data which this protocol slice does not interpret.
/// </summary>
public sealed record SoldStoreItem(
    uint TemplateId,
    ulong ItemId,
    byte Grade,
    byte Flags,
    int Count,
    byte DetailType,
    byte[] DetailData,
    long CreateTime,
    int LifespanMinutes,
    ulong MadeUnitId,
    byte WorldId,
    long UnsecureTime,
    long UnpackTime,
    long ChargeUseSkillTime,
    byte[] Raw);

public sealed record SoldItemListEvent(IReadOnlyList<SoldStoreItem> Items) : GameEvent;

/// <param name="Buy">True when the rejected operation was a purchase; false for a sale.</param>
public sealed record StoreTradeFailedEvent(bool Buy) : GameEvent;

public sealed record MerchantPurchaseLimit(uint ItemTemplateId, uint BuyCount, byte PurchaseType);

public sealed record MerchantPurchaseLimitsEvent(IReadOnlyList<MerchantPurchaseLimit> Limits) : GameEvent;

public sealed record MerchantPurchaseLimitFailedEvent(
    uint ItemTemplateId,
    byte PurchaseType,
    int PurchaseLimit) : GameEvent;

public sealed record MerchantPurchaseLimitsResetEvent(sbyte PurchaseType) : GameEvent;

/// <summary>
/// NPC speech. AAEmu emits a u32 <see cref="Type"/> with no text for kinds 1/2. Captured native
/// layouts also have kinds 5/6 with a u8 type followed by text; both forms are decoded.
/// </summary>
public sealed record NpcDialogueEvent(
    short ChatType,
    short Subtype,
    uint FactionId,
    uint NpcUnitId,
    string NpcName,
    uint CharacterUnitId,
    byte Kind,
    uint? Type,
    string Message) : GameEvent;

public sealed record NpcInteractionSkillsEvent(
    uint NpcUnitId,
    uint ObjectId,
    int ExtraInfo,
    int PickId,
    byte MouseButton,
    IReadOnlyList<uint> SkillIds,
    bool Interactable,
    int ModifierKeys) : GameEvent;

public sealed record NpcInteractionEndedEvent(uint NpcUnitId) : GameEvent;

public sealed record NpcInteractionStatusEvent(uint NpcUnitId, bool Interactable) : GameEvent;

/// <summary>
/// The native serializer proves the two leading bc ids and five-byte mouse/modifier tail. Its
/// variable binding rows are not represented by an AAEmu packet class, so they remain in
/// <see cref="RawBindings"/> until that layout has wire evidence.
/// </summary>
public sealed record WorldInteractionSkillsEvent(
    uint FirstObjectId,
    uint SecondObjectId,
    byte[] RawBindings,
    sbyte MouseButton,
    uint ModifierKeys) : GameEvent;

public sealed record WorldInteractionCanceledEvent(
    uint InteractedUnitId,
    byte UnitKind,
    int InteractedDoodadId) : GameEvent;

public sealed record DoodadInteractionCallbackEvent(uint DoodadId, int Type, int Duration) : GameEvent;

public sealed record DoodadFirstInteractionEvent(uint DoodadId, ulong Type) : GameEvent;

/// <summary>
/// Server-to-client shop, NPC dialogue, and interaction decoders for protocol 10.0.2.13.
/// Ordinary merchant inventories are client content data; the server sends the buyback list,
/// specialty pages, purchase-limit state, and failures represented here.
/// </summary>
public static class NpcShopProtocol
{
    public const ushort OpNpcInteractionSkills = 0x0AA;
    public const ushort OpNpcInteractionEnded = 0x0AB;
    public const ushort OpWorldInteractionSkills = 0x0AC;
    public const ushort OpWorldInteractionCanceled = 0x0AD;
    public const ushort OpNpcInteractionStatus = 0x0AE;
    public const ushort OpSpecialtyRatio = 0x0C5;
    public const ushort OpSpecialtyGoods = 0x0C6;
    public const ushort OpSpecialtyRecords = 0x0C7;
    public const ushort OpSpecialtyEventMessage = 0x0C8;
    public const ushort OpNpcChat = 0x103;
    public const ushort OpDoodadInteractionCallback = 0x157;
    public const ushort OpSoldItemList = 0x16F;
    public const ushort OpSpecialtyCurrent = 0x278;
    public const ushort OpStoreTradeFailed = 0x290;
    public const ushort OpDoodadFirstInteraction = 0x306;
    public const ushort OpUpdateMerchantPurchaseLimits = 0x376;
    public const ushort OpMerchantPurchaseLimitFailed = 0x377;
    public const ushort OpResetMerchantPurchaseLimits = 0x378;

    /// <summary>Dispatches every NPC/shop S2C parser by its protocol opcode.</summary>
    public static GameEvent Parse(ushort opcode, byte[] body) => opcode switch
    {
        OpSpecialtyRatio => ParseSpecialtyRatio(body),
        OpSpecialtyGoods => ParseSpecialtyGoods(body),
        OpSpecialtyRecords => ParseSpecialtyRecords(body),
        OpSpecialtyEventMessage => ParseSpecialtyEventMessage(body),
        OpNpcChat => ParseNpcChat(body),
        OpNpcInteractionSkills => ParseNpcInteractionSkills(body),
        OpNpcInteractionEnded => ParseNpcInteractionEnded(body),
        OpWorldInteractionSkills => ParseWorldInteractionSkills(body),
        OpWorldInteractionCanceled => ParseWorldInteractionCanceled(body),
        OpNpcInteractionStatus => ParseNpcInteractionStatus(body),
        OpDoodadInteractionCallback => ParseDoodadInteractionCallback(body),
        OpSoldItemList => ParseSoldItemList(body),
        OpSpecialtyCurrent => ParseSpecialtyCurrent(body),
        OpStoreTradeFailed => ParseStoreTradeFailed(body),
        OpDoodadFirstInteraction => ParseDoodadFirstInteraction(body),
        OpUpdateMerchantPurchaseLimits => ParseMerchantPurchaseLimits(body),
        OpMerchantPurchaseLimitFailed => ParseMerchantPurchaseLimitFailed(body),
        OpResetMerchantPurchaseLimits => ParseMerchantPurchaseLimitsReset(body),
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not an NPC/shop opcode"),
    };

    /// <summary>SCSpecialtyRatioPacket (0x0C5), AAEmu G2C writer order.</summary>
    public static SpecialtyGoodsPageEvent ParseSpecialtyRatio(byte[] body)
    {
        var r = new WireReader(body);
        var zoneGroupId = r.U16();
        var npcTemplateId = r.U32();
        var page = ReadSpecialtyPage(r, true, zoneGroupId, npcTemplateId);
        RequireConsumed(r, nameof(ParseSpecialtyRatio));
        return page;
    }

    /// <summary>SCSpecialtyGoodsPacket (0x0C6), AAEmu G2C writer order.</summary>
    public static SpecialtyGoodsPageEvent ParseSpecialtyGoods(byte[] body)
    {
        var r = new WireReader(body);
        var page = ReadSpecialtyPage(r, false, null, null);
        RequireConsumed(r, nameof(ParseSpecialtyGoods));
        return page;
    }

    private static SpecialtyGoodsPageEvent ReadSpecialtyPage(
        WireReader r, bool salePrices, ushort? zoneGroupId, uint? npcTemplateId)
    {
        var quoteCount = ReadCount(r.U32(), 20, "specialty quote");
        var eventCount = ReadCount(r.U32(), 50, "specialty event");
        var isBegin = r.Bool();
        var isEnd = r.Bool();
        var quotes = new List<SpecialtyGoodsQuote>(quoteCount);
        for (var i = 0; i < quoteCount; i++)
        {
            quotes.Add(new SpecialtyGoodsQuote(
                r.U32(), r.U64(), r.U64(), r.U32(), r.U32(), r.Bool(), r.U8(), r.S8()));
        }
        var eventIds = new List<uint>(eventCount);
        for (var i = 0; i < eventCount; i++)
            eventIds.Add(r.U32());
        return new SpecialtyGoodsPageEvent(
            zoneGroupId, npcTemplateId, quotes, eventIds, isBegin, isEnd, salePrices);
    }

    /// <summary>SCSpecialtyRecordsPacket (0x0C7), matching the current AAEmu writer.</summary>
    public static SpecialtyRecordsEvent ParseSpecialtyRecords(byte[] body)
    {
        var r = new WireReader(body);
        var count = ReadCount(r.U32(), 256, "specialty history");
        var zone = r.U16();
        var item = r.U32();
        var rows = new List<SpecialtyMarketRecord>(count);
        for (var i = 0; i < count; i++)
            rows.Add(new SpecialtyMarketRecord(r.S32(), r.S64()));
        RequireConsumed(r, nameof(ParseSpecialtyRecords));
        return new SpecialtyRecordsEvent(zone, item, rows);
    }

    public static SpecialtyEventMessageEvent ParseSpecialtyEventMessage(byte[] body)
    {
        var r = new WireReader(body);
        var result = new SpecialtyEventMessageEvent(r.Str());
        RequireConsumed(r, nameof(ParseSpecialtyEventMessage));
        return result;
    }

    /// <summary>SCSpecialtyCurrentPacket (0x278).</summary>
    public static SpecialtyCurrentEvent ParseSpecialtyCurrent(byte[] body)
    {
        var r = new WireReader(body);
        var count = ReadCount(r.S32(), 128, "current specialty ratio");
        var from = r.U16();
        var to = r.U16();
        var rows = new List<SpecialtyCurrentRatio>(count);
        for (var i = 0; i < count; i++)
            rows.Add(new SpecialtyCurrentRatio(r.U32(), r.U32()));
        RequireConsumed(r, nameof(ParseSpecialtyCurrent));
        return new SpecialtyCurrentEvent(from, to, rows);
    }

    /// <summary>SCSoldItemListPacket (0x16F): u32 count followed by canonical Item bodies.</summary>
    public static SoldItemListEvent ParseSoldItemList(byte[] body)
    {
        var r = new WireReader(body);
        var count = ReadCount(r.U32(), 12, "sold item");
        var items = new List<SoldStoreItem>(count);
        for (var i = 0; i < count; i++)
            items.Add(ReadSoldItem(r));
        RequireConsumed(r, nameof(ParseSoldItemList));
        return new SoldItemListEvent(items);
    }

    private static SoldStoreItem ReadSoldItem(WireReader r)
    {
        var start = r.Pos;
        var templateId = r.U32();
        if (templateId == 0)
            return new SoldStoreItem(0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0, 0, Slice(r, start));

        var itemId = r.U64();
        var grade = r.U8();
        var flags = r.U8();
        var count = r.S32();
        var detailType = r.U8();
        var detailStart = r.Pos;
        if (detailType == 1)
        {
            r.U8();
            r.U16();
            r.S64();
            r.U16();
            r.U16();
            r.S64();
            r.U8();
            r.U8();
            r.Pisc(18);
        }
        else
        {
            r.Bytes(DetailBodyLength(detailType));
        }
        var detail = r.Buffer.AsSpan(detailStart, r.Pos - detailStart).ToArray();
        var createTime = r.S64();
        var lifespan = r.S32();
        var madeUnitId = r.U64();
        var worldId = r.U8();
        var unsecureTime = r.S64();
        var unpackTime = r.S64();
        var chargeUseSkillTime = r.S64();
        return new SoldStoreItem(
            templateId, itemId, grade, flags, count, detailType, detail, createTime, lifespan,
            madeUnitId, worldId, unsecureTime, unpackTime, chargeUseSkillTime, Slice(r, start));
    }

    private static int DetailBodyLength(byte detailType) => detailType switch
    {
        2 => 33,
        3 => 20,
        4 => 9,
        5 or 11 => 24,
        6 or 7 => 16,
        8 or 14 => 8,
        9 => 4,
        10 => 12,
        12 => 10,
        13 => 13,
        _ => 0,
    };

    public static StoreTradeFailedEvent ParseStoreTradeFailed(byte[] body)
    {
        var r = new WireReader(body);
        var result = new StoreTradeFailedEvent(r.Bool());
        RequireConsumed(r, nameof(ParseStoreTradeFailed));
        return result;
    }

    public static MerchantPurchaseLimitsEvent ParseMerchantPurchaseLimits(byte[] body)
    {
        var r = new WireReader(body);
        var count = ReadCount(r.U32(), 4096, "merchant purchase limit");
        var rows = new List<MerchantPurchaseLimit>(count);
        for (var i = 0; i < count; i++)
            rows.Add(new MerchantPurchaseLimit(r.U32(), r.U32(), r.U8()));
        RequireConsumed(r, nameof(ParseMerchantPurchaseLimits));
        return new MerchantPurchaseLimitsEvent(rows);
    }

    public static MerchantPurchaseLimitFailedEvent ParseMerchantPurchaseLimitFailed(byte[] body)
    {
        var r = new WireReader(body);
        var result = new MerchantPurchaseLimitFailedEvent(r.U32(), r.U8(), r.S32());
        RequireConsumed(r, nameof(ParseMerchantPurchaseLimitFailed));
        return result;
    }

    public static MerchantPurchaseLimitsResetEvent ParseMerchantPurchaseLimitsReset(byte[] body)
    {
        var r = new WireReader(body);
        var result = new MerchantPurchaseLimitsResetEvent(r.S8());
        RequireConsumed(r, nameof(ParseMerchantPurchaseLimitsReset));
        return result;
    }

    /// <summary>SCNpcChatMessagePacket (0x103).</summary>
    public static NpcDialogueEvent ParseNpcChat(byte[] body)
    {
        var r = new WireReader(body);
        var chatType = r.S16();
        var subtype = r.S16();
        var faction = r.U32();
        var npc = r.Bc();
        var npcName = r.Str();
        var character = r.Bc();
        var kind = r.U8();
        uint? type = null;
        string message;
        if (kind is 1 or 2)
        {
            type = r.U32();
            message = string.Empty;
        }
        else
        {
            if (kind is 5 or 6)
                type = r.U8();
            message = r.Str();
        }
        RequireConsumed(r, nameof(ParseNpcChat));
        return new NpcDialogueEvent(
            chatType, subtype, faction, npc, npcName, character, kind, type, message);
    }

    /// <summary>SCNpcInteractionSkillListPacket (0x0AA), matching AAEmu's full writer.</summary>
    public static NpcInteractionSkillsEvent ParseNpcInteractionSkills(byte[] body)
    {
        var r = new WireReader(body);
        var npc = r.Bc();
        var obj = r.Bc();
        var extra = r.S32();
        var pick = r.S32();
        var mouse = r.U8();
        var count = ReadCount(r.S32(), 10, "NPC interaction skill");
        var skills = new List<uint>(count);
        for (var i = 0; i < count; i++)
            skills.Add(r.U32());
        var interactable = r.Bool();
        var modifiers = r.S32();
        RequireConsumed(r, nameof(ParseNpcInteractionSkills));
        return new NpcInteractionSkillsEvent(
            npc, obj, extra, pick, mouse, skills, interactable, modifiers);
    }

    public static NpcInteractionEndedEvent ParseNpcInteractionEnded(byte[] body)
    {
        var r = new WireReader(body);
        var result = new NpcInteractionEndedEvent(r.Bc());
        RequireConsumed(r, nameof(ParseNpcInteractionEnded));
        return result;
    }

    public static NpcInteractionStatusEvent ParseNpcInteractionStatus(byte[] body)
    {
        var r = new WireReader(body);
        var result = new NpcInteractionStatusEvent(r.Bc(), r.Bool());
        RequireConsumed(r, nameof(ParseNpcInteractionStatus));
        return result;
    }

    public static WorldInteractionSkillsEvent ParseWorldInteractionSkills(byte[] body)
    {
        var r = new WireReader(body);
        var first = r.Bc();
        var second = r.Bc();
        if (r.Remaining < 5)
            throw new WireException("world interaction binding list is missing its mouse/modifier tail");
        var bindings = r.Bytes(r.Remaining - 5);
        var mouse = r.S8();
        var modifiers = r.U32();
        return new WorldInteractionSkillsEvent(first, second, bindings, mouse, modifiers);
    }

    public static WorldInteractionCanceledEvent ParseWorldInteractionCanceled(byte[] body)
    {
        var r = new WireReader(body);
        var result = new WorldInteractionCanceledEvent(r.Bc(), r.U8(), r.S32());
        RequireConsumed(r, nameof(ParseWorldInteractionCanceled));
        return result;
    }

    public static DoodadInteractionCallbackEvent ParseDoodadInteractionCallback(byte[] body)
    {
        var r = new WireReader(body);
        var result = new DoodadInteractionCallbackEvent(r.Bc(), r.S32(), r.S32());
        RequireConsumed(r, nameof(ParseDoodadInteractionCallback));
        return result;
    }

    public static DoodadFirstInteractionEvent ParseDoodadFirstInteraction(byte[] body)
    {
        var r = new WireReader(body);
        var result = new DoodadFirstInteractionEvent(r.Bc(), r.U64());
        RequireConsumed(r, nameof(ParseDoodadFirstInteraction));
        return result;
    }

    private static int ReadCount(uint count, int maximum, string label)
    {
        if (count > maximum)
            throw new WireException($"{label} count {count} exceeds protocol maximum {maximum}");
        return checked((int)count);
    }

    private static int ReadCount(int count, int maximum, string label)
    {
        if (count < 0 || count > maximum)
            throw new WireException($"{label} count {count} is outside 0..{maximum}");
        return count;
    }

    private static byte[] Slice(WireReader r, int start) =>
        r.Buffer.AsSpan(start, r.Pos - start).ToArray();

    private static void RequireConsumed(WireReader r, string parser)
    {
        if (r.Remaining != 0)
            throw new WireException($"{parser} left {r.Remaining} trailing bytes");
    }
}
