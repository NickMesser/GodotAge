#nullable enable

namespace AAEmu.GodotViewer.Net;

public sealed record AuctionLotRecord(
    ulong LotId, byte Duration, ItemSnapshot? Item, byte WorldId, ulong SellerId, string SellerName,
    long StartPrice, long BuyoutPrice, ulong Asked, int ChargePercent, int DepositPercent, byte ServiceKind,
    byte BidWorldId, ulong BidderId, string BidderName, long BidPrice, long ExtraMoney, int MinStack, int MaxStack,
    byte[]? ItemBody = null);

public sealed record AuctionBidRecord(ulong LotId, byte WorldId, ulong BidderId, string BidderName, long Money, int StackSize);
public sealed record AuctionLimitedPriceRecord(uint Type, long First, long Second);
public sealed record AuctionSoldDayRecord(uint ItemTemplateId, int Day, long Minimum, long Maximum,
    long Average, int Volume, byte Grade, long LastPrice);

public sealed record AuctionPostedEvent(AuctionLotRecord Lot) : GameEvent;
public sealed record AuctionSearchResultsEvent(uint Page, IReadOnlyList<AuctionLotRecord> Lots, ushort Error, ulong ServerTime) : GameEvent;
public sealed record AuctionLowestPriceEvent(uint ItemTemplateId, byte Grade, long Money) : GameEvent;
public sealed record AuctionBidEvent(AuctionBidRecord Bid, bool IsBuyout, int ItemTemplateId) : GameEvent;
public sealed record AuctionCanceledEvent(AuctionLotRecord Lot) : GameEvent;
public sealed record AuctionMessageEvent(byte Kind, uint ItemTemplateId, long Money) : GameEvent;
public sealed record AuctionSoldRecordsRawEvent(uint ItemTemplateId, byte Grade, bool MarketPriceUi, byte[] RawDailyRows) : GameEvent;
public sealed record AuctionLimitedPricesEvent(IReadOnlyList<AuctionLimitedPriceRecord> Prices) : GameEvent;

public sealed record AuctionSearchRequest(
    string Keyword = "", bool ExactMatch = false, byte MinimumGrade = 0,
    byte CategoryA = 0, byte CategoryB = 0, byte CategoryC = 0, int Page = 0, ulong ClientId = 0,
    int Filter = 0, int ItemListCount = 0, byte WorldId = 0, sbyte MinimumItemLevel = 0,
    sbyte MaximumItemLevel = 0, long MinimumPrice = 0, long MaximumPrice = 0,
    byte SortKind = 0, byte SortOrder = 0);

/// <summary>
/// A lot as serialized in bid/cancel requests. ItemBody starts at the item's u32 template id and must
/// contain one complete Item.Write payload; retaining it avoids lossy reserialization of item details.
/// </summary>
public sealed record SerializedAuctionLot(
    ulong LotId, byte Duration, byte[] ItemBody, byte WorldId, ulong SellerId, string SellerName,
    long StartPrice, long BuyoutPrice, ulong Asked, int ChargePercent, int DepositPercent, byte ServiceKind,
    byte BidWorldId, ulong BidderId, string BidderName, long BidPrice, long ExtraMoney, int MinStack, int MaxStack);

/// <summary>Auction packet layouts for the 10.0.2.13 game protocol.</summary>
public static class AuctionProtocol
{
    public const ushort SCAuctionPosted = 0x171;
    public const ushort SCAuctionSearched = 0x172;
    public const ushort SCAuctionLowestPrice = 0x173;
    public const ushort SCAuctionBid = 0x174;
    public const ushort SCAuctionCanceled = 0x175;
    public const ushort SCAuctionMessage = 0x176;
    public const ushort SCAuctionSoldRecordSearched = 0x177;
    public const ushort SCAuctionLimitedPrice = 0x178;

    public const ushort CSAuctionPost = 0x0F8;
    public const ushort CSAuctionSearch = 0x0F9;
    public const ushort CSBidAuction = 0x0FA;
    public const ushort CSCancelAuction = 0x0FB;
    public const ushort CSAuctionMyBidList = 0x0FC;
    public const ushort CSAuctionLowestPrice = 0x0FD;
    public const ushort CSSearchAuctionSoldRecord = 0x0FE;

    public static GameEvent Parse(ushort opcode, byte[] body)
    {
        var r = new WireReader(body);
        return opcode switch
        {
            SCAuctionPosted => new AuctionPostedEvent(ReadLot(r)),
            SCAuctionSearched => ParseSearchResults(r),
            SCAuctionLowestPrice => new AuctionLowestPriceEvent(r.U32(), r.U8(), r.S64()),
            SCAuctionBid => new AuctionBidEvent(ReadBid(r), r.Bool(), r.S32()),
            SCAuctionCanceled => new AuctionCanceledEvent(ReadLot(r)),
            SCAuctionMessage => new AuctionMessageEvent(r.U8(), r.U32(), r.S64()),
            SCAuctionSoldRecordSearched => new AuctionSoldRecordsRawEvent(r.U32(), r.U8(), r.Bool(), r.Rest()),
            SCAuctionLimitedPrice => ParseLimitedPrices(r),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not an auction opcode"),
        };
    }

    private static AuctionSearchResultsEvent ParseSearchResults(WireReader r)
    {
        var page = r.U32();
        var count = r.U32();
        if (count > 50)
            throw new WireException($"invalid auction result count {count}");
        var lots = new List<AuctionLotRecord>((int)count);
        for (var i = 0u; i < count; i++)
            lots.Add(ReadLot(r));
        return new AuctionSearchResultsEvent(page, lots, r.U16(), r.U64());
    }

    private static AuctionLimitedPricesEvent ParseLimitedPrices(WireReader r)
    {
        var count = r.U32();
        if (count > 10)
            throw new WireException($"invalid limited price count {count}");
        var prices = new List<AuctionLimitedPriceRecord>((int)count);
        for (var i = 0u; i < count; i++)
            prices.Add(new AuctionLimitedPriceRecord(r.U32(), r.S64(), r.S64()));
        return new AuctionLimitedPricesEvent(prices);
    }

    /// <summary>The AAEmu writer always emits fourteen AuctionSoldRecord rows after the response header.</summary>
    public static IReadOnlyList<AuctionSoldDayRecord> ReadSoldDays(byte[] body)
    {
        var r = new WireReader(body);
        var rows = new List<AuctionSoldDayRecord>(14);
        for (var i = 0; i < 14; i++)
            rows.Add(new AuctionSoldDayRecord(r.U32(), r.S32(), r.S64(), r.S64(),
                r.S64(), r.S32(), r.U8(), r.S64()));
        if (r.Remaining != 0) throw new WireException($"auction sold records left {r.Remaining} trailing bytes");
        return rows;
    }

    private static AuctionLotRecord ReadLot(WireReader r)
    {
        var lotId = r.U64();
        var duration = r.U8();
        var itemStart = r.Pos;
        var item = InventoryProtocol.ReadItem(r);
        var itemBody = r.Buffer.AsSpan(itemStart, r.Pos - itemStart).ToArray();
        return new AuctionLotRecord(
            lotId, duration, item, r.U8(), r.U64(), r.Str(), r.S64(), r.S64(), r.U64(), r.S32(), r.S32(),
            r.U8(), r.U8(), r.U64(), r.Str(), r.S64(), r.S64(), r.S32(), r.S32(), itemBody);
    }

    private static AuctionBidRecord ReadBid(WireReader r) =>
        new(r.U64(), r.U8(), r.U64(), r.Str(), r.S64(), r.S32());

    public static byte[] WriteSearch(AuctionSearchRequest request) => new WireWriter()
        .Str(request.Keyword).Bool(request.ExactMatch).U8(request.MinimumGrade)
        .U8(request.CategoryA).U8(request.CategoryB).U8(request.CategoryC).S32(request.Page)
        .U64(request.ClientId).S32(request.Filter).S32(request.ItemListCount).U8(request.WorldId)
        .S8(request.MinimumItemLevel).S8(request.MaximumItemLevel).S64(request.MinimumPrice)
        .S64(request.MaximumPrice).U8(request.SortKind).U8(request.SortOrder).ToArray();

    public static byte[] WritePost(ulong itemId, long startPrice, long buyoutPrice, byte duration, int minimumStack = 1, int maximumStack = 1) =>
        new WireWriter().U64(itemId).S64(startPrice).S64(buyoutPrice).U8(duration).S32(minimumStack).S32(maximumStack).ToArray();

    public static byte[] WriteMyBidList(int page) => new WireWriter().S32(page).ToArray();
    public static byte[] WriteLowestPrice(uint templateId, byte grade) => new WireWriter().U32(templateId).U8(grade).ToArray();
    public static byte[] WriteSoldRecordSearch(uint templateId, byte grade, bool marketPriceUi) =>
        new WireWriter().U32(templateId).U8(grade).Bool(marketPriceUi).ToArray();

    public static byte[] WriteBid(SerializedAuctionLot lot, AuctionBidRecord bid)
    {
        var w = new WireWriter();
        WriteLot(w, lot);
        return WriteBid(w, bid).ToArray();
    }

    public static byte[] WriteCancel(SerializedAuctionLot lot)
    {
        var w = new WireWriter();
        WriteLot(w, lot);
        return w.ToArray();
    }

    private static void WriteLot(WireWriter w, SerializedAuctionLot lot)
    {
        w.U64(lot.LotId).U8(lot.Duration).Bytes(lot.ItemBody).U8(lot.WorldId).U64(lot.SellerId)
            .Str(lot.SellerName).S64(lot.StartPrice).S64(lot.BuyoutPrice).U64(lot.Asked)
            .S32(lot.ChargePercent).S32(lot.DepositPercent).U8(lot.ServiceKind).U8(lot.BidWorldId)
            .U64(lot.BidderId).Str(lot.BidderName).S64(lot.BidPrice).S64(lot.ExtraMoney)
            .S32(lot.MinStack).S32(lot.MaxStack);
    }

    private static WireWriter WriteBid(WireWriter w, AuctionBidRecord bid) => w
        .U64(bid.LotId).U8(bid.WorldId).U64(bid.BidderId).Str(bid.BidderName).S64(bid.Money).S32(bid.StackSize);
}
