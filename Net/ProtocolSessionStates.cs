#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>Main-thread mail view assembled from incremental mail packets.</summary>
public sealed class MailSessionState
{
    private readonly Dictionary<long, MailHeaderRecord> _received = [];
    private readonly Dictionary<long, MailHeaderRecord> _sent = [];
    private readonly Dictionary<long, MailBodyRecord> _bodies = [];

    public IReadOnlyDictionary<long, MailHeaderRecord> Received => _received;
    public IReadOnlyDictionary<long, MailHeaderRecord> Sent => _sent;
    public IReadOnlyDictionary<long, MailBodyRecord> Bodies => _bodies;
    public MailUnreadCounts? Counts { get; private set; }
    public MailFailedEvent? LastFailure { get; private set; }

    public void Clear()
    {
        _received.Clear(); _sent.Clear(); _bodies.Clear();
        Counts = null; LastFailure = null;
    }

    public void Apply(GameEvent value)
    {
        switch (value)
        {
            case MailFailedEvent x: LastFailure = x; break;
            case MailCountsEvent x: Counts = x.Counts; break;
            case MailSentEvent x: _sent[x.Header.MailId] = x.Header; Counts = x.Counts; break;
            case MailReceivedEvent x:
                _received[x.Header.MailId] = x.Header; Counts = x.Counts;
                if (x.Body is not null) _bodies[x.Body.MailId] = x.Body;
                break;
            case MailListEntryEvent x: (x.IsSent ? _sent : _received)[x.Header.MailId] = x.Header; break;
            case MailListEndEvent x: Counts = x.Counts; break;
            case MailBodyEvent x: _bodies[x.Body.MailId] = x.Body; Counts = x.Counts; break;
            case MailReceiverOpenedEvent x: UpdateHeader(x.MailId, h => h with { OpenDate = x.OpenDate }); break;
            case MailDeletedEvent x: Remove(x.IsSent, x.MailId); Counts = x.Counts; break;
            case MailReturnedEvent x: _received[x.MailId] = x.Header; Counts = x.Counts; break;
            case MailStatusUpdatedEvent x: UpdateHeader(x.IsSent, x.MailId, h => h with { Status = x.Status }); break;
            case MailRemovedEvent x: Remove(x.IsSent, x.MailId); break;
        }
    }

    private void Remove(bool sent, long id)
    {
        (sent ? _sent : _received).Remove(id);
        _bodies.Remove(id);
    }

    private void UpdateHeader(long id, Func<MailHeaderRecord, MailHeaderRecord> update)
    {
        if (_received.TryGetValue(id, out var received)) _received[id] = update(received);
        if (_sent.TryGetValue(id, out var sent)) _sent[id] = update(sent);
    }

    private void UpdateHeader(bool sent, long id, Func<MailHeaderRecord, MailHeaderRecord> update)
    {
        var source = sent ? _sent : _received;
        if (source.TryGetValue(id, out var header)) source[id] = update(header);
    }
}

/// <summary>Main-thread NPC interaction, shop and merchant-limit view.</summary>
public sealed class ShopSessionState
{
    public NpcInteractionSkillsEvent? OpenInteraction { get; private set; }
    public IReadOnlyList<SoldStoreItem> SoldItems { get; private set; } = [];
    public IReadOnlyList<MerchantPurchaseLimit> PurchaseLimits { get; private set; } = [];
    public SpecialtyGoodsPageEvent? SpecialtyGoods { get; private set; }
    public SpecialtyRecordsEvent? SpecialtyRecords { get; private set; }
    public SpecialtyCurrentEvent? SpecialtyCurrent { get; private set; }
    public StoreTradeFailedEvent? LastTradeFailure { get; private set; }

    public void Clear() => OpenInteraction = null;

    public void Apply(GameEvent value)
    {
        switch (value)
        {
            case NpcInteractionSkillsEvent x: OpenInteraction = x; break;
            case NpcInteractionEndedEvent: OpenInteraction = null; break;
            case SoldItemListEvent x: SoldItems = x.Items; break;
            case MerchantPurchaseLimitsEvent x: PurchaseLimits = x.Limits; break;
            case MerchantPurchaseLimitsResetEvent: PurchaseLimits = []; break;
            case SpecialtyGoodsPageEvent x: SpecialtyGoods = x; break;
            case SpecialtyRecordsEvent x: SpecialtyRecords = x; break;
            case SpecialtyCurrentEvent x: SpecialtyCurrent = x; break;
            case StoreTradeFailedEvent x: LastTradeFailure = x; break;
        }
    }
}

/// <summary>Main-thread auction query and transaction view.</summary>
public sealed class AuctionSessionState
{
    private readonly Dictionary<ulong, AuctionLotRecord> _lots = [];
    private readonly Dictionary<(uint TemplateId, byte Grade), long> _lowestPrices = [];

    public IReadOnlyDictionary<ulong, AuctionLotRecord> Lots => _lots;
    public IReadOnlyDictionary<(uint TemplateId, byte Grade), long> LowestPrices => _lowestPrices;
    public AuctionSearchResultsEvent? LastSearch { get; private set; }
    public AuctionMessageEvent? LastMessage { get; private set; }
    public AuctionSoldRecordsRawEvent? LastSoldRecords { get; private set; }
    public IReadOnlyList<AuctionLimitedPriceRecord> LimitedPrices { get; private set; } = [];

    public void Apply(GameEvent value)
    {
        switch (value)
        {
            case AuctionPostedEvent x: _lots[x.Lot.LotId] = x.Lot; break;
            case AuctionSearchResultsEvent x:
                LastSearch = x;
                foreach (var lot in x.Lots) _lots[lot.LotId] = lot;
                break;
            case AuctionLowestPriceEvent x: _lowestPrices[(x.ItemTemplateId, x.Grade)] = x.Money; break;
            case AuctionCanceledEvent x: _lots.Remove(x.Lot.LotId); break;
            case AuctionMessageEvent x: LastMessage = x; break;
            case AuctionSoldRecordsRawEvent x: LastSoldRecords = x; break;
            case AuctionLimitedPricesEvent x: LimitedPrices = x.Prices; break;
        }
    }
}

public sealed class CraftSessionState
{
    public CraftFailedEvent? LastFailure { get; private set; }
    public void Apply(GameEvent value) { if (value is CraftFailedEvent failed) LastFailure = failed; }
}
