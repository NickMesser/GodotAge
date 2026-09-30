#nullable enable
using System.Globalization;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A loot-bag row. <see cref="Fields"/> carries item-tooltip fields supplied by the session.</summary>
public sealed record X2LootItem(int Slot, string Name, int ItemType, int StackSize = 1,
    long? Money = null, long? AaPoint = null, int SoundType = 0,
    IReadOnlyDictionary<string, object?>? Fields = null,
    IReadOnlyDictionary<string, object?>? TooltipFields = null);

/// <summary>Currency and icon metadata returned by X2Store:GetStoreCurrency.</summary>
public sealed record X2StoreCurrency(int Currency = 0, int CoinItemType = 0,
    string CoinIcon = "", string CoinIconKey = "");

/// <summary>A merchant-good or buy-back row. Fields are named as the stock x2ui store consumes them.</summary>
public sealed record X2StoreItem(int GoodIndex, int ItemType, string Name, string Icon = "",
    int Stack = 1, int MaxStack = 1, long Cost = 0, int Currency = 0, string CurrencyName = "money",
    bool SoldOut = false, bool SinglePurchase = false, bool IsStackable = false,
    int PurchaseType = 0, int PurchaseLimit = 0, int PurchaseBuyCount = 0,
    IReadOnlyDictionary<string, object?>? Fields = null);

public sealed record X2RandomShopCounts(int FreeCount, int FreeMaximum, int ChargeCount, int ChargeMaximum);
public sealed record X2RandomShopRefresh(int RefreshCount, int RefreshMaximum, bool IsFree,
    int Currency, long NeededCost, long InventoryCount = 0, X2StoreItem? CostItem = null);
public sealed record X2StoreZoneGroup(int Id, string ContinentName, string ZoneGroupName);
public sealed record X2StoreBuff(string BuffName, int Ratio);
public sealed record X2SpecialtySkillCost(int ConsumeLaborPower);
public sealed record X2SpecialtySupply(string Label, int PriceIndex, string IconPath = "", string IconCoord = "");
public sealed record X2SpecialtyBuyConfirmation(long FinalPrice, long? BasePrice = null,
    X2SpecialtySupply? Supply = null, IReadOnlyList<int>? EventRatios = null);
public sealed record X2SpecialtyRatioRow(X2StoreItem? Item, int Ratio,
    IReadOnlyDictionary<string, object?>? Fields = null);

public sealed record X2ItemGuideImpl(string Name, int Type);
public sealed record X2ItemGuideSubcategory(int Type, string Name);
public sealed record X2ItemGuideCategory(int Type, string Name,
    IReadOnlyList<X2ItemGuideSubcategory>? Subcategories = null);
public sealed record X2ItemGuideCategoryInfo(int ItemGuideType, string Name, int LootMainCategory,
    int LootSubCategory = 0, int ShowOrder = 0, bool Enabled = true, string IconPath = "",
    double? GearScore = null, int ZoneId = 0, string WayToLoot = "",
    IReadOnlyDictionary<string, object?>? Fields = null);
public sealed record X2ItemGuideItem(int ItemType, string Name = "", string IconPath = "",
    bool ShowCraft = false, IReadOnlyDictionary<string, object?>? Fields = null);
public sealed record X2ItemGuidePortal(int PortalZoneId, double X, double Y, double Z);

public partial interface IX2ItemsData
{
    IReadOnlyList<X2LootItem> LootItems { get; }
    void CloseLoot();
    void LootItem(int slot);
    /// <summary>Requests the server's bulk-loot path for the currently opened loot owner.</summary>
    void LootAll();
    void DoLootDice(long key, bool roll);
    void RequestNextLootDice();
    void SetMaxConcurrentLootDice(int count);

    int StoreOpenType { get; set; }
    X2StoreCurrency StoreCurrency { get; }
    IReadOnlyList<X2StoreItem> GetStoreItems(bool exactMatch, string searchText);
    IReadOnlyList<X2StoreItem> SoldStoreItems { get; }
    bool IsRandomShopStore { get; }
    bool IsActiveDirectRandomShop { get; }
    string RandomShopName { get; }
    X2RandomShopCounts? RandomShopCounts { get; }
    X2RandomShopRefresh? RandomShopRefresh { get; }
    bool CanRefreshRandomShop { get; }
    int CheckRandomShopOpen(int requestedOpenType);
    bool IsStoreSaleBlocked(int itemType);
    int SellItemsMaximum { get; }
    int SellerShareRatio { get; }
    int PcBangRatio { get; }
    int SpecialtyRatio { get; }
    bool SpecialtyDebug { get; }
    (int CoinItemType, int BackpackType)? BackpackSellType { get; }
    IReadOnlyList<X2StoreZoneGroup> ProductionZoneGroups { get; }
    IReadOnlyList<X2StoreZoneGroup> GetSellableZoneGroups(int sourceZoneGroupId);
    IReadOnlyList<X2SpecialtyRatioRow> SpecialtyRatios { get; }
    int GetSpecialtyRatioBetween(int sourceZoneGroupId, int destinationZoneGroupId);
    int GetSpecialtyInterest(int backpackType);
    int GetBackpackSellLabor(int backpackType);
    X2SpecialtySkillCost? GetBackpackBuySkillCost(int backpackType);
    X2StoreBuff? AuctionAccountBuff { get; }
    X2StoreBuff? SpecialtyAccountBuff { get; }
    X2SpecialtyBuyConfirmation? GetSpecialtyBuyConfirmation(int itemType);
    IReadOnlyList<IReadOnlyDictionary<string, object?>> SpecialtyBuyRatioRangeTooltip { get; }
    void BuyStoreItems(IReadOnlyList<int> goodIndices, IReadOnlyList<int> stackSizes, IReadOnlyList<int> currencies);
    void SellStoreItems(IReadOnlyList<int> slots, bool equipmentSlots);
    void SplitBagItem(int oldSlot, int newSlot, int count);
    void AddStoreSellItem(int slot);
    void ClearStoreCursor();
    void RequestSoldItemList();
    void AddBuyBackItem(int index);
    void ClearBuyBackCart();
    void BuyBackItems();
    void RequestBackpackGoods(int categoryType, string name);
    void RequestBackpackGoodRecipes(int itemType);
    void BuyBackpackGoods(int itemType);
    void SellBackpackGoods();
    void PlaceItemToStoreBuyCart();
    void MakeStoreItemSearchList();
    bool RefreshRandomShop();
    void RequestZoneSpecialtyRatios();

    IReadOnlyList<X2ItemGuideImpl> ItemGuideImpls { get; }
    IReadOnlyList<IReadOnlyList<X2ItemGuideCategory>> ItemGuideCategories { get; }
    IReadOnlyList<X2ItemGuideCategoryInfo> GetItemGuideCategoryInfos(int aCategory, int bCategory, int lootIndex, int grade, bool ascending);
    IReadOnlyList<X2ItemGuideItem> GetItemGuideSpecifiedItems(int aCategory, int bCategory, int guideType, int grade);
    X2ItemGuidePortal? GetItemGuidePortal(int zoneKey);
}

public partial class NullItemsData
{
    public virtual IReadOnlyList<X2LootItem> LootItems => [];
    public virtual void CloseLoot() { }
    public virtual void LootItem(int slot) { }
    public virtual void LootAll() { }
    public virtual void DoLootDice(long key, bool roll) { }
    public virtual void RequestNextLootDice() { }
    public virtual void SetMaxConcurrentLootDice(int count) { }
    public virtual int StoreOpenType { get; set; }
    public virtual X2StoreCurrency StoreCurrency { get; } = new();
    public virtual IReadOnlyList<X2StoreItem> GetStoreItems(bool exactMatch, string searchText) => [];
    public virtual IReadOnlyList<X2StoreItem> SoldStoreItems => [];
    public virtual bool IsRandomShopStore => false;
    public virtual bool IsActiveDirectRandomShop => false;
    public virtual string RandomShopName => "";
    public virtual X2RandomShopCounts? RandomShopCounts => null;
    public virtual X2RandomShopRefresh? RandomShopRefresh => null;
    public virtual bool CanRefreshRandomShop => false;
    public virtual int CheckRandomShopOpen(int requestedOpenType) => requestedOpenType;
    public virtual bool IsStoreSaleBlocked(int itemType) => false;
    public virtual int SellItemsMaximum => 10;
    public virtual int SellerShareRatio => 100;
    public virtual int PcBangRatio => 0;
    public virtual int SpecialtyRatio => 0;
    public virtual bool SpecialtyDebug => false;
    public virtual (int CoinItemType, int BackpackType)? BackpackSellType => null;
    public virtual IReadOnlyList<X2StoreZoneGroup> ProductionZoneGroups => [];
    public virtual IReadOnlyList<X2StoreZoneGroup> GetSellableZoneGroups(int sourceZoneGroupId) => [];
    public virtual IReadOnlyList<X2SpecialtyRatioRow> SpecialtyRatios => [];
    public virtual int GetSpecialtyRatioBetween(int sourceZoneGroupId, int destinationZoneGroupId) => 0;
    public virtual int GetSpecialtyInterest(int backpackType) => 0;
    public virtual int GetBackpackSellLabor(int backpackType) => 0;
    public virtual X2SpecialtySkillCost? GetBackpackBuySkillCost(int backpackType) => null;
    public virtual X2StoreBuff? AuctionAccountBuff => null;
    public virtual X2StoreBuff? SpecialtyAccountBuff => null;
    public virtual X2SpecialtyBuyConfirmation? GetSpecialtyBuyConfirmation(int itemType) => null;
    public virtual IReadOnlyList<IReadOnlyDictionary<string, object?>> SpecialtyBuyRatioRangeTooltip => [];
    public virtual void BuyStoreItems(IReadOnlyList<int> goodIndices, IReadOnlyList<int> stackSizes, IReadOnlyList<int> currencies) { }
    public virtual void SellStoreItems(IReadOnlyList<int> slots, bool equipmentSlots) { }
    public virtual void SplitBagItem(int oldSlot, int newSlot, int count) { }
    public virtual void AddStoreSellItem(int slot) { }
    public virtual void ClearStoreCursor() { }
    public virtual void RequestSoldItemList() { }
    public virtual void AddBuyBackItem(int index) { }
    public virtual void ClearBuyBackCart() { }
    public virtual void BuyBackItems() { }
    public virtual void RequestBackpackGoods(int categoryType, string name) { }
    public virtual void RequestBackpackGoodRecipes(int itemType) { }
    public virtual void BuyBackpackGoods(int itemType) { }
    public virtual void SellBackpackGoods() { }
    public virtual void PlaceItemToStoreBuyCart() { }
    public virtual void MakeStoreItemSearchList() { }
    public virtual bool RefreshRandomShop() => false;
    public virtual void RequestZoneSpecialtyRatios() { }
    public virtual IReadOnlyList<X2ItemGuideImpl> ItemGuideImpls => [];
    public virtual IReadOnlyList<IReadOnlyList<X2ItemGuideCategory>> ItemGuideCategories => [];
    public virtual IReadOnlyList<X2ItemGuideCategoryInfo> GetItemGuideCategoryInfos(int aCategory, int bCategory, int lootIndex, int grade, bool ascending) => [];
    public virtual IReadOnlyList<X2ItemGuideItem> GetItemGuideSpecifiedItems(int aCategory, int bCategory, int guideType, int grade) => [];
    public virtual X2ItemGuidePortal? GetItemGuidePortal(int zoneKey) => null;
}

public static partial class X2ItemsApi
{
    internal static void InstallCommerce(X2LuaHost host, X2GameContext context, IX2ItemsData data)
    {
        InstallLoot(host, data);
        InstallStore(host, data);
        InstallItemGuide(host, data);
    }

    private static void InstallLoot(X2LuaHost host, IX2ItemsData data)
    {
        host.Define("X2Loot", "CloseLoot", _ => { data.CloseLoot(); return null; });
        host.Define("X2Loot", "DoDiceAction", a => { data.DoLootDice((long)a.Num(0), a.Bool(1)); return null; });
        host.Define("X2Loot", "GetLootingBagItemInfo", a => LootTable(ItemAt(data.LootItems, a.Int(0)), false));
        host.Define("X2Loot", "GetLootingBagItemTooltipText", a => LootTable(ItemAt(data.LootItems, a.Int(0)), true));
        host.Define("X2Loot", "GetLootItemSoundType", a => (double)(ItemAt(data.LootItems, a.Int(0))?.SoundType ?? 0));
        host.Define("X2Loot", "GetNumLootItems", _ => (double)data.LootItems.Count);
        host.Define("X2Loot", "LootAll", _ => { data.LootAll(); return null; });
        host.Define("X2Loot", "LootItem", a => { data.LootItem(a.Int(0)); return null; });
        host.Define("X2Loot", "RequestNextDiceItem", _ => { data.RequestNextLootDice(); return null; });
        host.Define("X2Loot", "SetMaxConcurrentNotifyDiceItem", a => { data.SetMaxConcurrentLootDice(a.Int(0)); return null; });
    }

    private static void InstallStore(X2LuaHost host, IX2ItemsData data)
    {
        host.Define("X2Store", "AddBuyBackItemList", a => { data.AddBuyBackItem(a.Int(0)); return null; });
        host.Define("X2Store", "BuyBackItem", _ => { data.BuyBackItems(); return null; });
        host.Define("X2Store", "BuyBackPackGoods", a => { data.BuyBackpackGoods(a.Int(0)); return null; });
        host.Define("X2Store", "BuyStoreItemWithStack", a => { data.BuyStoreItems(IntArray(a.Table(0)), IntArray(a.Table(1)), IntArray(a.Table(2))); return true; });
        host.Define("X2Store", "CanRandomShopStoreRefresh", _ => data.CanRefreshRandomShop);
        host.Define("X2Store", "CheckRandomShopStoreOpen", a => (double)data.CheckRandomShopOpen(a.Int(0)));
        host.Define("X2Store", "ClearBuyBackCartList", _ => { data.ClearBuyBackCart(); return null; });
        host.Define("X2Store", "ClearCursorOnStoreClose", _ => { data.ClearStoreCursor(); return null; });
        host.Define("X2Store", "GetAccountBuffInfoUsingAuctionConfig", _ => BuffTable(data.AuctionAccountBuff));
        host.Define("X2Store", "GetAccountBuffInfoUsingSpecialityConfig", _ => BuffTable(data.SpecialtyAccountBuff));
        host.Define("X2Store", "GetBackPackBuySkillSpent", a => data.GetBackpackBuySkillCost(a.Int(0)) is { } x ? new LuaTable { ["consumeLp"] = (double)x.ConsumeLaborPower } : null);
        host.Define("X2Store", "GetBackPackGoodCategories", _ => new LuaTable());
        host.Define("X2Store", "GetBackPackGoodRecipes", a => { data.RequestBackpackGoodRecipes(a.Int(0)); return null; });
        host.Define("X2Store", "GetBackPackSellLabor", a => (double)data.GetBackpackSellLabor(a.Int(0)));
        host.Define("X2Store", "GetBackpackSellType", _ => data.BackpackSellType is { } x ? new LuaMulti((double)x.CoinItemType, (double)x.BackpackType) : new LuaMulti(null, null));
        host.Define("X2Store", "GetCurrencyStr", a => CurrencyName(a.Int(0)));
        host.Define("X2Store", "GetPCBangRatio", _ => (double)data.PcBangRatio);
        host.Define("X2Store", "GetProductionZoneGroups", _ => ArrayTable(data.ProductionZoneGroups, ZoneTable));
        host.Define("X2Store", "GetRandomShopStoreName", _ => data.RandomShopName.Length == 0 ? null : data.RandomShopName);
        host.Define("X2Store", "GetRandomShopStoreRefreshCount", _ => RandomCountsTable(data.RandomShopCounts));
        host.Define("X2Store", "GetRandomShopStoreRefreshInfo", _ => RandomRefreshTable(data.RandomShopRefresh));
        host.Define("X2Store", "GetSellItemsMaxCount", _ => (double)data.SellItemsMaximum);
        host.Define("X2Store", "GetSellableZoneGroups", a => ArrayTable(data.GetSellableZoneGroups(a.Int(0)), ZoneTable));
        host.Define("X2Store", "GetSellerShareRatio", _ => (double)data.SellerShareRatio);
        host.Define("X2Store", "GetSoldHistoryItemList", _ => ArrayTable(data.SoldStoreItems, StoreItemTable));
        host.Define("X2Store", "GetSpecialtyBuyConfirmContent", a => ConfirmationTable(data.GetSpecialtyBuyConfirmation(a.Int(0))));
        host.Define("X2Store", "GetSpecialtyBuyRatioRangeTooltip", _ => DictionariesTable(data.SpecialtyBuyRatioRangeTooltip));
        host.Define("X2Store", "GetSpecialtyDebug", _ => data.SpecialtyDebug);
        host.Define("X2Store", "GetSpecialtyInterest", a => (double)data.GetSpecialtyInterest(a.Int(0)));
        host.Define("X2Store", "GetSpecialtyRatio", _ => (double)data.SpecialtyRatio);
        host.Define("X2Store", "GetSpecialtyRatioBetween", a => (double)data.GetSpecialtyRatioBetween(a.Int(0), a.Int(1)));
        host.Define("X2Store", "GetStoreCurrency", _ => new LuaMulti((double)data.StoreCurrency.Currency, (double)data.StoreCurrency.CoinItemType, data.StoreCurrency.CoinIcon, data.StoreCurrency.CoinIconKey));
        host.Define("X2Store", "GetStoreNpcItemList", a => { var xs = data.GetStoreItems(a.Bool(1), a.Str(2) ?? ""); return a.Bool(0) ? (object)(double)xs.Count : ArrayTable(xs, StoreItemTable); });
        host.Define("X2Store", "GetStoreOpenType", _ => (double)data.StoreOpenType);
        host.Define("X2Store", "GetZoneSpecialtyRatio", _ => { data.RequestZoneSpecialtyRatios(); return null; });
        host.Define("X2Store", "IsActiveDirectRandomShop", _ => data.IsActiveDirectRandomShop);
        host.Define("X2Store", "IsBlockItemSell", a => data.IsStoreSaleBlocked(a.Int(0)));
        host.Define("X2Store", "IsRandomShopStore", _ => data.IsRandomShopStore);
        host.Define("X2Store", "ListBackPackGoods", a => { data.RequestBackpackGoods(a.Int(0), a.Str(1) ?? ""); return null; });
        host.Define("X2Store", "MakeItemSearchList", _ => { data.MakeStoreItemSearchList(); return null; });
        host.Define("X2Store", "PlaceItemToStoreBuyCart", _ => { data.PlaceItemToStoreBuyCart(); return null; });
        host.Define("X2Store", "RandomShopStoreRefresh", _ => data.RefreshRandomShop());
        host.Define("X2Store", "SellBackPackGoods", _ => { data.SellBackpackGoods(); return null; });
        host.Define("X2Store", "SellStoreItem", a => { data.SellStoreItems(IntArray(a.Table(0)), a.Bool(2)); return null; });
        host.Define("X2Store", "SetStoreOpenType", a => { data.StoreOpenType = a.Int(0); return null; });
        host.Define("X2Store", "SoldItemList", _ => { data.RequestSoldItemList(); return null; });
        host.Define("X2Store", "SplitBagItem", a => { data.SplitBagItem(a.Int(0), a.Int(1), a.Int(2)); return null; });
        // The client answers with STORE_ADD_SELL_ITEM(bagSlot); store.lua's handler appends the slot to the sell list.
        host.Define("X2Store", "StoreAddSellItemToBagSlot", a =>
        {
            data.AddStoreSellItem(a.Int(0));
            host.Root.DispatchEvent("STORE_ADD_SELL_ITEM", (double)a.Int(0));
            return null;
        });
    }

    private static void InstallItemGuide(X2LuaHost host, IX2ItemsData data)
    {
        host.Define("X2ItemGuide", "GetCategories", _ => ArrayTable(data.ItemGuideCategories, xs => ArrayTable(xs, GuideCategoryTable)));
        host.Define("X2ItemGuide", "GetCategoryInfos", a => ArrayTable(data.GetItemGuideCategoryInfos(a.Int(0), a.Int(1), a.Int(2), a.Int(3), a.Bool(4)), GuideCategoryInfoTable));
        host.Define("X2ItemGuide", "GetImpls", _ => ArrayTable(data.ItemGuideImpls, x => new LuaTable { ["name"] = x.Name, ["type"] = (double)x.Type }));
        host.Define("X2ItemGuide", "GetIndunPortalInfo", a => data.GetItemGuidePortal(a.Int(0)) is { } x ? new LuaTable { ["portal_zone_id"] = (double)x.PortalZoneId, ["x"] = x.X, ["y"] = x.Y, ["z"] = x.Z } : null);
        host.Define("X2ItemGuide", "GetSpecifiedItems", a => ArrayTable(data.GetItemGuideSpecifiedItems(a.Int(0), a.Int(1), a.Int(2), a.Int(3)), GuideItemTable));
    }

    private static X2LootItem? ItemAt(IReadOnlyList<X2LootItem> items, int oneBased) => oneBased > 0 && oneBased <= items.Count ? items[oneBased - 1] : null;
    private static LuaTable? LootTable(X2LootItem? x, bool tooltip)
    {
        if (x is null) return null;
        var t = CopyFields(tooltip ? x.TooltipFields ?? x.Fields : x.Fields);
        t["name"] = x.Name; t["itemType"] = (double)x.ItemType; t["stackSize"] = (double)x.StackSize;
        if (x.Money is { } money) { t["money"] = money.ToString(CultureInfo.InvariantCulture); if (tooltip) t["isMoney"] = true; }
        if (x.AaPoint is { } point) { t["aapoint"] = point.ToString(CultureInfo.InvariantCulture); if (tooltip) t["isAAPoint"] = true; }
        return t;
    }

    private static LuaTable StoreItemTable(X2StoreItem x)
    {
        var t = CopyFields(x.Fields);
        t["goodIndex"] = (double)x.GoodIndex; t["itemType"] = (double)x.ItemType; t["name"] = x.Name;
        t["icon"] = x.Icon; t["stack"] = (double)x.Stack; t["maxStack"] = (double)x.MaxStack;
        t["cost"] = x.Cost.ToString(CultureInfo.InvariantCulture); t["currency"] = (double)x.Currency;
        t["currencyStr"] = x.CurrencyName; t["soldout"] = x.SoldOut; t["singless"] = x.SinglePurchase;
        t["isStackable"] = x.IsStackable; t["purchaseType"] = (double)x.PurchaseType;
        t["purchaseLimit"] = (double)x.PurchaseLimit; t["purchaseBuyCount"] = (double)x.PurchaseBuyCount;
        return t;
    }

    private static LuaTable GuideCategoryTable(X2ItemGuideCategory x)
    {
        var t = new LuaTable { ["aType"] = (double)x.Type, ["name"] = x.Name };
        if (x.Subcategories is { } subs) t["bInfos"] = ArrayTable(subs, y => new LuaTable { ["bType"] = (double)y.Type, ["name"] = y.Name });
        return t;
    }

    private static LuaTable GuideCategoryInfoTable(X2ItemGuideCategoryInfo x)
    {
        var t = CopyFields(x.Fields);
        t["itemGuideType"] = (double)x.ItemGuideType; t["name"] = x.Name; t["lootMainCategory"] = (double)x.LootMainCategory;
        t["lootSubCategory"] = (double)x.LootSubCategory; t["showOrder"] = (double)x.ShowOrder; t["enable"] = x.Enabled;
        t["iconPath"] = x.IconPath; t["zoneId"] = (double)x.ZoneId; t["wayToLoot"] = x.WayToLoot;
        if (x.GearScore is { } score) t["gearScore"] = score;
        return t;
    }

    private static LuaTable GuideItemTable(X2ItemGuideItem x)
    {
        var t = CopyFields(x.Fields); t["itemType"] = (double)x.ItemType; t["name"] = x.Name;
        t["icon"] = x.IconPath; t["iconPath"] = x.IconPath; t["showCraft"] = x.ShowCraft; return t;
    }

    private static LuaTable? BuffTable(X2StoreBuff? x) => x is null ? null : new LuaTable { ["buffName"] = x.BuffName, ["ratio"] = (double)x.Ratio };
    private static LuaTable ZoneTable(X2StoreZoneGroup x) => new() { ["id"] = (double)x.Id, ["continentName"] = x.ContinentName, ["zoneGroupName"] = x.ZoneGroupName };
    private static LuaTable? RandomCountsTable(X2RandomShopCounts? x) => x is null ? null : new LuaTable { ["freeCnt"] = (double)x.FreeCount, ["freeMax"] = (double)x.FreeMaximum, ["chargeCnt"] = (double)x.ChargeCount, ["chargeMax"] = (double)x.ChargeMaximum };
    private static LuaTable? RandomRefreshTable(X2RandomShopRefresh? x) => x is null ? null : new LuaTable { ["refreshCnt"] = (double)x.RefreshCount, ["refreshMax"] = (double)x.RefreshMaximum, ["isFree"] = x.IsFree, ["currency"] = (double)x.Currency, ["needCost"] = x.NeededCost.ToString(CultureInfo.InvariantCulture), ["hasInven"] = x.InventoryCount.ToString(CultureInfo.InvariantCulture), ["itemInfo"] = x.CostItem is null ? null : StoreItemTable(x.CostItem) };
    private static LuaTable? ConfirmationTable(X2SpecialtyBuyConfirmation? x)
    {
        if (x is null) return null;
        var t = new LuaTable { ["finalPrice"] = x.FinalPrice.ToString(CultureInfo.InvariantCulture) };
        if (x.BasePrice is { } bp) t["basePrice"] = bp.ToString(CultureInfo.InvariantCulture);
        if (x.Supply is { } s) t["supply"] = new LuaTable { ["label"] = s.Label, ["priceIndex"] = (double)s.PriceIndex, ["iconPath"] = s.IconPath, ["iconCoord"] = s.IconCoord };
        if (x.EventRatios is { } ratios) t["events"] = ArrayTable(ratios, n => (object)(double)n);
        return t;
    }

    // shop_currency_types: Money=0, Honor=1, VocationBadges=2, ItemPoint=5.
    private static string CurrencyName(int currency) => currency switch { 0 => "money", 1 => "honor_point", 2 => "living_point", 5 => "item", _ => $"currency_{currency}" };
    private static IReadOnlyList<int> IntArray(LuaTable? table)
    {
        if (table is null) return [];
        var result = new List<int>();
        for (var i = 1; table.TryGetValue((double)i, out var value); i++) result.Add(Convert.ToInt32(value, CultureInfo.InvariantCulture));
        return result;
    }

    private static LuaTable CopyFields(IReadOnlyDictionary<string, object?>? fields)
    {
        var t = new LuaTable(); if (fields is null) return t;
        foreach (var pair in fields) t[pair.Key] = pair.Value; return t;
    }

    private static LuaTable DictionariesTable(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) => ArrayTable(rows, CopyFields);
    private static LuaTable ArrayTable<T>(IReadOnlyList<T> values, Func<T, object?> convert)
    {
        var t = new LuaTable(); for (var i = 0; i < values.Count; i++) t[(double)(i + 1)] = convert(values[i]); return t;
    }
}
