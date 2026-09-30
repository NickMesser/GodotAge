#nullable enable
using System.Globalization;
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Live economy data for the original x2ui.  All access is lazy so the same instance is safe to install before an
/// <see cref="OnlineSession"/> is attached; without a session or action sender it retains <see cref="NullEconomyData"/>
/// query and command behavior.
/// </summary>
public sealed partial class X2ProtocolEconomyData : NullEconomyData
{
    private const byte InventorySlot = (byte)InventorySlotType.Inventory;
    private const int GoldCurrency = 0;

    private readonly Func<OnlineSession?> _session;
    private readonly Func<ClientActions?> _actions;
    private readonly Func<uint>? _targetObjectId;
    private readonly Func<uint>? _targetDoodadType;
    private readonly Func<uint, X2InventoryItem?>? _itemDefinition;
    private readonly Func<X2CursorState?>? _pickedItem;
    private readonly Action<string, object?[]>? _fireEvent;

    private AuctionSearchRequest? _lastAuctionSearch;
    private ulong _attachedAuctionItemId;
    private int _attachedAuctionItemCount;
    private IReadOnlyDictionary<string, object?>? _attachedAuctionItem;
    private long _auctionStartPrice;
    private long _auctionBuyoutPrice;
    private byte _auctionDuration;
    private int _auctionMinimumStack = 1;
    private int _auctionMaximumStack = 1;
    private int _auctionPostType;
    private bool _showDirectPriceRange;
    private int _auctionTab;

    public X2ProtocolEconomyData(
        Func<OnlineSession?> session,
        Func<ClientActions?> actions,
        Func<uint>? targetObjectId = null,
        Func<uint>? targetDoodadType = null,
        Func<uint, X2InventoryItem?>? itemDefinition = null,
        Func<X2CursorState?>? pickedItem = null,
        Action<string, object?[]>? fireEvent = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _targetObjectId = targetObjectId;
        _targetDoodadType = targetDoodadType;
        _itemDefinition = itemDefinition;
        _pickedItem = pickedItem;
        _fireEvent = fireEvent;
    }

    public override X2EconomySnapshot Snapshot
    {
        get
        {
            var session = _session();
            if (session == null)
                return base.Snapshot with { CraftRecipes = CraftRecipes(null) };

            var inventory = session.InventoryState;
            var social = session.SocialState;
            var auction = session.AuctionState;
            var currencies = inventory.GamePoints.ToDictionary(x => (int)x.Key, x => x.Value);
            currencies[GoldCurrency] = inventory.Money;
            currencies[1] = inventory.GamePoints.GetValueOrDefault(GamePointSlots.Honor);
            currencies[2] = inventory.GamePoints.GetValueOrDefault(GamePointSlots.Vocation);
            currencies[3] = inventory.AaPoints;

            var search = auction.LastSearch;
            var articles = search?.Lots.Select((lot, i) => ToArticle(lot, i + 1, session.Entered.CharacterId)).ToArray() ?? [];
            var vehicles = new X2VehicleUiProjection(session.MateSlaveState, session.Entered.UnitId,
                session.Entered.CharacterId, session.PlayerAttachedUnitId, _targetObjectId?.Invoke() ?? 0);
            return new X2EconomySnapshot
            {
                Money = inventory.Money,
                CraftRecipes = CraftRecipes(session),
                CraftInteractionTargetId = (int)(_targetObjectId?.Invoke() ?? 0),
                CraftInteractionDoodadType = (int)(_targetDoodadType?.Invoke() ?? 0),
                Currencies = currencies,
                AuctionArticles = articles,
                AuctionPage = search == null ? 0 : checked((int)search.Page) + 1,
                // SCAuctionSearched has no server total field.  The current page size is the only authoritative total.
                AuctionTotal = search == null ? 0 : (int)search.Page + 1 + (articles.Length >= 20 ? 1 : 0),
                AuctionAttachedItem = _attachedAuctionItem,
                Trade = new X2TradeState(
                    Active: social.TradeOpen,
                    MineLocked: social.MyTradeLock,
                    OtherLocked: social.OtherTradeLock,
                    OfferedMoney: SaturatingInt64(social.MyTradeMoney),
                    MoneyLimit: long.MaxValue,
                    Currency: GoldCurrency),
                Mates = vehicles.Mates,
                Premium = inventory.PremiumGrade > 0,
                PremiumPoint = SaturatingInt32(inventory.BmPoint),
                PremiumGradePoint = inventory.PremiumPoints,
                ShopEnabled = ShopEnabled,
                ShopReady = ShopReady,
                ShopGoods = ShopGoods,
            };
        }
    }

    public override bool TryQuery(X2EconomyQuery query, out object? value)
    {
        value = null;
        if (query.Table == "X2Craft" && TryQueryCraft(query, out value)) return true;
        if (query.Table == "X2InGameShop" && TryQueryShop(query, out value)) return true;
        var session = _session();
        if (session == null)
            return base.TryQuery(query, out value);

        if (query.Table == "X2Auction")
        {
            switch (query.Method)
            {
                case "GetSearchedItemPrice":
                    if (ArticleAt(session, Int(query.Arguments, 0)) is { } priced)
                    {
                        value = new X2EconomyValues([Money(Math.Max(priced.StartPrice, priced.BidPrice)), Money(priced.BuyoutPrice)]);
                        return true;
                    }
                    return false;
                case "GetLowestPrice":
                {
                    var key = ((uint)Math.Max(0, Long(query.Arguments, 0)), (byte)Math.Clamp(Int(query.Arguments, 1), 0, 255));
                    value = session.AuctionState.LowestPrices.TryGetValue(key, out var price) ? Money(price) : "0";
                    return true;
                }
                case "GetLinkText":
                    value = ArticleAt(session, Int(query.Arguments, 0)) is { Item: { } linkItem }
                        ? _itemDefinition?.Invoke(linkItem.TemplateId)?.Name ?? "" : "";
                    return true;
                case "GetMarkerPricePeriod":
                    value = Enumerable.Range(0, 14).Select(index => {
                        var day = DateTime.UtcNow.Date.AddDays(index - 13);
                        return new Dictionary<string, object?> { ["month"] = day.Month, ["day"] = day.Day };
                    }).ToArray();
                    return true;
                case "GetAsrGoldLabels":
                case "GetAsrVolLabels":
                {
                    var eventRows = session.AuctionState.LastSoldRecords;
                    var rows = eventRows == null ? [] : AuctionProtocol.ReadSoldDays(eventRows.RawDailyRows);
                    var top = query.Method == "GetAsrGoldLabels"
                        ? rows.Select(row => Math.Max(row.Maximum, row.Average)).DefaultIfEmpty(0).Max()
                        : rows.Select(row => (long)row.Volume).DefaultIfEmpty(0).Max();
                    value = Enumerable.Range(0, 5).Select(index => top * (4 - index) / 4).ToArray();
                    return true;
                }
                case "GetPostType":
                    value = _auctionPostType;
                    return true;
                case "GetChargeInfo":
                    value = new Dictionary<string, object?> {
                        ["defaultRatio"] = "2%", ["chargeRate"] = 2,
                        ["gapChargeToDefaultRate"] = 0, ["useChargeByItem"] = false,
                    };
                    return true;
                case "GetDepositRatioValue":
                    value = Int(query.Arguments, 0) switch { 1 => 5, 2 => 10, 3 => 15, _ => 20 };
                    return true;
                case "GetMaxDepositValue":
                    value = "1000000";
                    return true;
                case "CalcDeposit":
                {
                    var price = Math.Max(0, Long(query.Arguments, 1));
                    var rate = Int(query.Arguments, 2) switch { 1 => 5, 2 => 10, 3 => 15, _ => 20 };
                    value = Math.Min(1000000, price / 1000 * rate + price % 1000 * rate / 1000)
                        .ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                case "HasPostAuthority":
                case "RequirePostAuthority":
                    value = true;
                    return true;
                case "PermissionCheckByCraft":
                {
                    // This UI callback reports that a craft consumed the bag item selected for listing.
                    // The craft book and attachment are both client-side state; there is no auction packet for it.
                    var recipe = CraftBook.Value.ByType.GetValueOrDefault(Int(query.Arguments, 0));
                    var attachedType = _attachedAuctionItem == null ? 0 : Convert.ToInt32(
                        _attachedAuctionItem.GetValueOrDefault("itemType") ?? 0, CultureInfo.InvariantCulture);
                    value = _attachedAuctionItemId != 0 && recipe != null &&
                        recipe.Materials.Any(material => material.ItemType == attachedType);
                    return true;
                }
                case "IsShowDirectPriceRangeEdit":
                    value = _showDirectPriceRange;
                    return true;
                case "GetSearchedSortInfo":
                    value = new X2EconomyValues([
                        (int)(_lastAuctionSearch?.SortKind ?? 0),
                        (_lastAuctionSearch?.SortOrder ?? 0) != 0,
                    ]);
                    return true;
                case "GetCurrencyForBid":
                case "GetCurrencyForFee":
                    value = GoldCurrency;
                    return true;
                case "GetPartitionPriceByCount":
                {
                    var total = Math.Max(0, Long(query.Arguments, 0));
                    var stack = Math.Max(1, Long(query.Arguments, 1));
                    var count = Math.Max(0, Long(query.Arguments, 2));
                    value = total / stack * count + total % stack * count / stack;
                    return true;
                }
            }
        }

        if (query.Table == "X2Mate")
        {
            var vehicles = new X2VehicleUiProjection(session.MateSlaveState, session.Entered.UnitId,
                session.Entered.CharacterId, session.PlayerAttachedUnitId, _targetObjectId?.Invoke() ?? 0);
            switch (query.Method)
            {
                case "IsTargetMyMate":
                    value = vehicles.IsTargetMyMate;
                    return true;
                case "GetAutoStartMountSkill":
                    value = 0;
                    return true;
                case "GetCommandIconInfo":
                    value = CommandIcons();
                    return true;
            }
        }

        return base.TryQuery(query, out value);
    }

    public override bool Execute(X2EconomyCommand command)
    {
        var session = _session();
        var actions = _actions();
        if (command.Table == "X2Craft" && ExecuteCraft(command, session, actions)) return true;
        if (command.Table == "X2InGameShop" && session != null && actions != null &&
            ExecuteShop(command, session, actions)) return true;
        if (session == null || actions == null)
            return base.Execute(command);

        if (command.Table == "X2Trade")
            return ExecuteTrade(session, actions, command);
        if (command.Table == "X2Auction")
            return ExecuteAuction(session, actions, command);
        if (command.Table == "X2Mate")
            return ExecuteMate(session, actions, command);
        return base.Execute(command);
    }

    private bool ExecuteMate(OnlineSession session, ClientActions actions, X2EconomyCommand command)
    {
        MateSpawnedBody? Mate(int type) => session.MateSlaveState.Units.Values
            .Where(unit => unit.Unit.Kind == UnitKind.Mate &&
                unit.Unit.OwnerId == session.Entered.CharacterId && unit.MateSpawned?.MateType == type)
            .Select(unit => unit.MateSpawned).FirstOrDefault();
        var type = Int(command.Arguments, 0);
        switch (command.Method)
        {
            case "DismissPet" when Mate(type) is { } dismissed:
                actions.DismissMate(dismissed.TimelineId);
                return true;
            case "MountMate" when Mate(type) is { } mounted:
                actions.MountMate(mounted.TimelineId, 1, 0);
                return true;
            case "UnMountMate":
            {
                var mountedUnit = session.MateSlaveState.Units.GetValueOrDefault(session.PlayerAttachedUnitId);
                if (mountedUnit?.MateSpawned is not { } current) return false;
                actions.UnmountMate(current.TimelineId, 1, 0);
                return true;
            }
            case "SetMateName" when Mate(type) is { } named:
                actions.ChangeMateName(named.TimelineId, String(command.Arguments, 1));
                return true;
            case "OrderAttackTarget" when Mate(type) is { } attacker && _targetObjectId?.Invoke() is > 0 and var target:
                actions.ChangeMateTarget(attacker.TimelineId, target);
                return true;
            case "AggressiveMode" when Mate(type) is { } aggressive:
                actions.ChangeMateUserState(aggressive.TimelineId, 1);
                return true;
            case "ProtectiveMode" when Mate(type) is { } protective:
                actions.ChangeMateUserState(protective.TimelineId, 2);
                return true;
            case "PassiveMode" when Mate(type) is { } passive:
                actions.ChangeMateUserState(passive.TimelineId, 3);
                return true;
            case "StandMode" when Mate(type) is { } idle:
                actions.ChangeMateUserState(idle.TimelineId, 4);
                return true;
            default:
                return false;
        }
    }

    private static IReadOnlyDictionary<int, object?> CommandIcons()
    {
        static Dictionary<int, object?> Row() => Enumerable.Range(1, 5)
            .ToDictionary(index => index, _ => (object?)"");
        return new Dictionary<int, object?> { [1] = Row(), [2] = Row() };
    }

    private bool ExecuteTrade(OnlineSession session, ClientActions actions, X2EconomyCommand command)
    {
        switch (command.Method)
        {
            case "RequestTrade" when _targetObjectId?.Invoke() is > 0 and var target:
                actions.RequestTrade(target);
                return true;
            case "StartTrade":
                if (UInt(command.Arguments, 0) is not { } requester) return false;
                actions.AcceptTrade(requester);
                return true;
            case "CancelTrade":
                actions.CancelTrade(Unsigned(command.Arguments, 0));
                return true;
            case "PutupTradeItem":
            {
                var uiSlot = Int(command.Arguments, 0);
                if (uiSlot is < 1 or > 256) return false;
                var slot = checked((byte)(uiSlot - 1));
                var amount = Unsigned(command.Arguments, 1);
                if (amount == 0 && session.InventoryState.TryGetItem(InventorySlot, slot, out var item) && item != null)
                    amount = (uint)Math.Max(0, item.Count);
                if (amount == 0) return false;
                actions.OfferTradeItem(InventorySlot, slot, amount);
                return true;
            }
            case "TakeDownTradeItemByInventoryIdx":
                if (Byte(command.Arguments, 0) is not { } removedSlot) return false;
                actions.RemoveTradeItem(InventorySlot, removedSlot);
                return true;
            case "PutupTradeMoneyByStr":
                actions.OfferTradeMoney(ULong(command.Arguments, 0));
                return true;
            case "LockTrade":
                actions.LockTrade();
                return true;
            case "OkTrade":
                actions.ConfirmTrade();
                return true;
            default:
                return false;
        }
    }

    private bool ExecuteAuction(OnlineSession session, ClientActions actions, X2EconomyCommand command)
    {
        switch (command.Method)
        {
            case "SearchAuctionArticle":
                _lastAuctionSearch = SearchRequest(command.Arguments);
                _fireEvent?.Invoke(X2EconomyEvents.AuctionItemSearch, []);
                actions.SearchAuction(_lastAuctionSearch);
                return true;
            case "SearchMyAuctionArticles":
                _lastAuctionSearch = new AuctionSearchRequest(Page: ServerPage(command.Arguments, 0),
                    ClientId: session.Entered.CharacterId);
                _fireEvent?.Invoke(X2EconomyEvents.AuctionItemSearch, []);
                actions.SearchAuction(_lastAuctionSearch);
                return true;
            case "SearchMyBidList":
                _lastAuctionSearch = null;
                _fireEvent?.Invoke(X2EconomyEvents.AuctionItemSearch, []);
                actions.SearchMyAuctionBids(ServerPage(command.Arguments, 0));
                return true;
            case "AskMarketPrice":
                actions.SearchAuctionSoldRecords(Unsigned(command.Arguments, 0),
                    (byte)Math.Clamp(Int(command.Arguments, 1), 0, 255), Bool(command.Arguments, 2));
                return true;
            case "SearchDeclareSiege":
                _lastAuctionSearch = new AuctionSearchRequest(Page: ServerPage(command.Arguments, 0), CategoryA: 11);
                _fireEvent?.Invoke(X2EconomyEvents.AuctionItemSearch, []);
                actions.SearchAuction(_lastAuctionSearch);
                return true;
            case "SearchAuctionArticleByPage":
                if (_lastAuctionSearch == null) return false;
                _lastAuctionSearch = _lastAuctionSearch with { Page = ServerPage(command.Arguments, 0) };
                _fireEvent?.Invoke(X2EconomyEvents.AuctionItemSearch, []);
                actions.SearchAuction(_lastAuctionSearch);
                return true;
            case "SearchRefresh":
                if (_auctionTab == 2) actions.SearchMyAuctionBids(session.AuctionState.LastSearch is { } bidPage ? (int)bidPage.Page : 1);
                else if (_lastAuctionSearch != null) actions.SearchAuction(_lastAuctionSearch);
                else return false;
                return true;
            case "SetCurTab":
                _auctionTab = Int(command.Arguments, 0);
                return true;
            case "SetOpen":
            case "ToggleAuction":
                return true;
            case "SetListSort":
            case "SearchedListSort":
                _lastAuctionSearch = (_lastAuctionSearch ?? new AuctionSearchRequest()) with
                {
                    SortKind = (byte)Math.Clamp(Int(command.Arguments, 0), 0, 255),
                    SortOrder = Bool(command.Arguments, 1) ? (byte)1 : (byte)0,
                };
                actions.SearchAuction(_lastAuctionSearch);
                return true;
            case "AttachItemFromBag":
            {
                var uiSlot = Int(command.Arguments, 0);
                if (uiSlot is < 1 or > 256) return false;
                var slot = checked((byte)(uiSlot - 1));
                if (!session.InventoryState.TryGetItem(InventorySlot, slot, out var item) || item == null)
                    return false;
                Attach(item, slot);
                return true;
            }
            case "AttachItemFromPick":
            {
                var bagSlot = _pickedItem?.Invoke()?.BagSlot ?? 0;
                if (bagSlot is < 1 or > 256) return false;
                var slot = checked((byte)(bagSlot - 1));
                if (!session.InventoryState.TryGetItem(InventorySlot, slot, out var item) || item == null) return false;
                Attach(item, slot);
                return true;
            }
            case "DetachItem":
                _attachedAuctionItemId = 0;
                _attachedAuctionItemCount = 0;
                _attachedAuctionItem = null;
                _fireEvent?.Invoke(X2EconomyEvents.AuctionItemAttachmentStateChanged, [false]);
                return true;
            case "SetPrice":
                _auctionStartPrice = Math.Max(0, Long(command.Arguments, 0));
                _auctionBuyoutPrice = Math.Max(0, Long(command.Arguments, 1));
                return true;
            case "SetPricePartition":
                _auctionStartPrice = 0;
                _auctionBuyoutPrice = Math.Max(0, Long(command.Arguments, 0));
                _auctionMaximumStack = Math.Max(1, Int(command.Arguments, 1));
                return true;
            case "SetDuration":
                _auctionDuration = (byte)Math.Clamp(Int(command.Arguments, 0) - 1, 0, 3);
                return true;
            case "SetPartitionBuyMinMaxCount":
                _auctionMinimumStack = Math.Max(1, Int(command.Arguments, 0));
                _auctionMaximumStack = Math.Max(_auctionMinimumStack, Int(command.Arguments, 1));
                return true;
            case "SetPostType":
                _auctionPostType = Int(command.Arguments, 0);
                return true;
            case "SetShowDirectPriceRangeEdit":
                _showDirectPriceRange = Bool(command.Arguments, 0);
                return true;
            case "AskAuctionArticle":
                if (_attachedAuctionItemId == 0) return false;
                actions.PostAuction(_attachedAuctionItemId, _auctionStartPrice, _auctionBuyoutPrice,
                    _auctionDuration, _auctionMinimumStack, _auctionMaximumStack);
                return true;
            case "CancelAuctionArticle":
                if (LotById(session, command.Arguments, 0) is not { } cancelLot ||
                    ToSerialized(cancelLot) is not { } cancelBody) return false;
                actions.CancelAuction(cancelBody);
                return true;
            case "BidAuctionArticle":
            case "DirectPurchaseAuctionArticle":
            case "PartitionAuctionArticle":
            {
                if (LotById(session, command.Arguments, 0) is not { } lot ||
                    ToSerialized(lot) is not { } body) return false;
                var money = command.Method == "BidAuctionArticle"
                    ? Math.Max(0, Long(command.Arguments, 1)) : lot.BuyoutPrice;
                var stack = command.Method == "PartitionAuctionArticle"
                    ? Math.Clamp(Int(command.Arguments, 2), 1, Math.Max(1, lot.Item?.Count ?? 1))
                    : Math.Max(1, lot.Item?.Count ?? 1);
                actions.BidAuction(body, new AuctionBidRecord(lot.LotId, 0, session.Entered.CharacterId,
                    session.Entered.Name, money, stack));
                return true;
            }
            case "ClearSearchCondition":
                _lastAuctionSearch = null;
                return true;
            default:
                return false;
        }
    }

    private void Attach(ItemSnapshot item, byte slot)
    {
        _attachedAuctionItemId = item.ItemId;
        _attachedAuctionItemCount = item.Count;
        _auctionMinimumStack = 1;
        _auctionMaximumStack = Math.Max(1, item.Count);
        _attachedAuctionItem = ItemInfo(item, new Dictionary<string, object?>
        {
            ["inventoryIdx"] = (int)slot + 1,
        });
        _fireEvent?.Invoke(X2EconomyEvents.AuctionItemAttachmentStateChanged, [true]);
        _actions()?.RequestAuctionLowestPrice(item.TemplateId, item.Grade);
    }

    public void OnAuctionPosted()
    {
        _attachedAuctionItemId = 0;
        _attachedAuctionItemCount = 0;
        _attachedAuctionItem = null;
        RefreshAuctionView();
    }

    /// <summary>Invalidate the listing preview when a bag task consumes or changes its instance.</summary>
    public void ValidateAuctionAttachment()
    {
        var session = _session();
        if (session == null || _attachedAuctionItemId == 0) return;
        var item = session.InventoryState.Items
            .Where(pair => pair.Key.SlotType == InventorySlot)
            .Select(pair => pair.Value)
            .FirstOrDefault(value => value.ItemId == _attachedAuctionItemId);
        if (item != null && item.Count == _attachedAuctionItemCount) return;
        _attachedAuctionItemId = 0;
        _attachedAuctionItemCount = 0;
        _attachedAuctionItem = null;
        _fireEvent?.Invoke(X2EconomyEvents.AuctionItemAttachmentStateChanged, [false]);
    }

    public void RefreshAuctionView()
    {
        var actions = _actions();
        if (actions == null) return;
        if (_auctionTab == 2)
            actions.SearchMyAuctionBids(_session()?.AuctionState.LastSearch is { } page ? (int)page.Page : 1);
        else if (_lastAuctionSearch != null)
            actions.SearchAuction(_lastAuctionSearch);
    }

    private X2AuctionArticle ToArticle(AuctionLotRecord lot, int index, ulong myId) => new(
        lot.LotId.ToString(CultureInfo.InvariantCulture),
        ItemInfo(lot.Item, new Dictionary<string, object?>
        {
            ["idx"] = index,
            ["articleId"] = lot.LotId.ToString(CultureInfo.InvariantCulture),
            ["sellerName"] = lot.SellerName,
            ["seller"] = lot.SellerName,
            ["bidderName"] = lot.BidderName,
            ["bidPriceStr"] = Money(Math.Max(lot.StartPrice, lot.BidPrice)),
            ["directPriceStr"] = Money(lot.BuyoutPrice),
            ["depositStr"] = Money(Deposit(lot.BuyoutPrice, (int)lot.Duration + 1)),
            ["myBidMoney"] = Money(lot.BidPrice),
            ["leftTime"] = Math.Max(0, (lot.Duration switch { 0 => 6, 1 => 12, 2 => 24, _ => 48 }) * 3600L
                - Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)lot.Asked)),
            ["minStack"] = lot.MinStack,
            ["maxStack"] = lot.MaxStack,
            ["bidState"] = lot.BidderId == 0 ? 0 : lot.BidderId == myId ? 1 : 2,
            ["duration"] = (int)lot.Duration,
        }),
        lot.SellerId == myId);

    private IReadOnlyDictionary<string, object?> ItemInfo(
        ItemSnapshot? item,
        Dictionary<string, object?> extra)
    {
        var definition = item == null ? null : _itemDefinition?.Invoke(item.TemplateId);
        if (definition != null)
            foreach (var field in definition.Fields) extra[field.Key] = field.Value;
        extra["itemType"] = item == null ? 0u : item.TemplateId;
        extra["itemGrade"] = item == null ? 0 : (int)item.Grade;
        extra["stackCount"] = item == null ? 0 : item.Count;
        extra["stack"] = item == null ? 0 : item.Count;
        extra["isStackable"] = item is { Count: > 1 };
        extra["itemId"] = item == null ? "0" : item.ItemId.ToString(CultureInfo.InvariantCulture);
        // Static name/icon/tooltip fields continue to be resolved by the item API and client database.
        extra["name"] = definition?.Name ?? "";
        extra["gradeColor"] = extra.GetValueOrDefault("gradeColor") ?? "FFFFFFFF";
        extra["gold_refund"] = extra.GetValueOrDefault("gold_refund") ?? 0;
        extra["level_requirement"] = extra.GetValueOrDefault("level_requirement") ?? 1;
        return extra;
    }

    private static AuctionLotRecord? LotById(OnlineSession session, IReadOnlyList<object?> args, int index)
    {
        var id = ULong(args, index);
        return id == 0 ? null : session.AuctionState.Lots.GetValueOrDefault(id);
    }

    private static SerializedAuctionLot? ToSerialized(AuctionLotRecord lot)
    {
        if (lot.ItemBody == null) return null;
        return new SerializedAuctionLot(lot.LotId, lot.Duration, lot.ItemBody, lot.WorldId,
            lot.SellerId, lot.SellerName, lot.StartPrice, lot.BuyoutPrice, lot.Asked,
            lot.ChargePercent, lot.DepositPercent, lot.ServiceKind, lot.BidWorldId,
            lot.BidderId, lot.BidderName, lot.BidPrice, lot.ExtraMoney, lot.MinStack, lot.MaxStack);
    }

    private static AuctionLotRecord? ArticleAt(OnlineSession session, int oneBased)
    {
        var lots = session.AuctionState.LastSearch?.Lots;
        return lots != null && oneBased > 0 && oneBased <= lots.Count ? lots[oneBased - 1] : null;
    }

    private static AuctionSearchRequest SearchRequest(IReadOnlyList<object?> args) => new(
        Keyword: String(args, 6),
        ExactMatch: Bool(args, 5),
        MinimumGrade: (byte)Math.Clamp(Int(args, 3), 0, 255),
        CategoryA: (byte)Math.Clamp(Int(args, 4) / 10000, 0, 255),
        CategoryB: (byte)Math.Clamp(Int(args, 4) / 100 % 100, 0, 255),
        CategoryC: (byte)Math.Clamp(Int(args, 4) % 100, 0, 255),
        Page: ServerPage(args, 0),
        MinimumItemLevel: (sbyte)Math.Clamp(Int(args, 1), sbyte.MinValue, sbyte.MaxValue),
        MaximumItemLevel: (sbyte)Math.Clamp(Int(args, 2), sbyte.MinValue, sbyte.MaxValue),
        MinimumPrice: Math.Max(0, Long(args, 7)),
        MaximumPrice: Math.Max(0, Long(args, 8)));

    private static int ServerPage(IReadOnlyList<object?> args, int index) => Math.Max(0, Int(args, index) - 1);

    private static long Deposit(long price, int duration)
    {
        var rate = duration switch { 1 => 5, 2 => 10, 3 => 15, _ => 20 };
        return Math.Min(1000000, Math.Max(0, price) / 1000 * rate
            + Math.Max(0, price) % 1000 * rate / 1000);
    }

    private static long SaturatingInt64(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
    private static int SaturatingInt32(long value) => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    private static string Money(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static object? Arg(IReadOnlyList<object?> args, int index) =>
        index >= 0 && index < args.Count ? args[index] : null;

    private static string String(IReadOnlyList<object?> args, int index) =>
        Arg(args, index)?.ToString() ?? "";

    private static int Int(IReadOnlyList<object?> args, int index)
    {
        var value = Long(args, index);
        return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
    }

    private static long Long(IReadOnlyList<object?> args, int index)
    {
        var value = Arg(args, index);
        if (value is string text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        try { return value == null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception) { return 0; }
    }

    private static ulong ULong(IReadOnlyList<object?> args, int index)
    {
        var value = Arg(args, index);
        if (value is string text && ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        try { return value == null ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
        catch (Exception) { return 0; }
    }

    private static uint? UInt(IReadOnlyList<object?> args, int index)
    {
        var value = ULong(args, index);
        return value is > 0 and <= uint.MaxValue ? (uint)value : null;
    }

    private static uint Unsigned(IReadOnlyList<object?> args, int index)
    {
        var value = ULong(args, index);
        return value > uint.MaxValue ? uint.MaxValue : (uint)value;
    }

    private static byte? Byte(IReadOnlyList<object?> args, int index)
    {
        var value = Long(args, index);
        return value is >= byte.MinValue and <= byte.MaxValue ? (byte)value : null;
    }

    private static bool Bool(IReadOnlyList<object?> args, int index)
    {
        var value = Arg(args, index);
        if (value is bool flag) return flag;
        if (value is string text && bool.TryParse(text, out var parsed)) return parsed;
        return Long(args, index) != 0;
    }
}
