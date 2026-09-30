#nullable enable
using System.Collections;
using System.Globalization;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2;
using AAEmu.GodotViewer.Ui.X2.Scripting;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Installs the economy-facing native Lua API used by the original x2ui packages.</summary>
public static class X2EconomyApi
{
    private sealed record BindingSet(string Table, string Members);

    // A member without a suffix has no result. Suffixes are the generated X2ApiData return kinds:
    // n number, b boolean, s string and t table. Every unavailable path uses the corresponding real-client empty value.
    private static readonly BindingSet[] BindingSets =
    [
        new("X2Craft", """
            CancelCraftOrder ClearCraftOrderItemSlot EndCraftingInteraction EnterCraftOrderMode ExecuteBatchCraftByType
            GetACategories:t GetActabilityGroup:t GetActabilityGroupInfoByGroupType:t GetBCategories:t GetCraftBaseInfo:t
            GetCraftMaterialInfo:t GetCraftName:s GetCraftOrderCharge:n GetCraftOrderableCount:n GetCraftProductInfo:t
            GetCraftStepInfo:t GetCraftTypeByItemType:n GetCraftingMaterialLimitCount:n GetEquipmentBCateoryInfo:t
            GetExecutableCraftCount:n GetInteratcionTargetDoodadType:n GetInteratcionTargetId:n GetLeftBatchCount:n
            GetList:t GetListByFavorite:t GetListByItemType:t GetListBySearching GetMinCraftOrderFee:s
            GetMyCraftOrderEntry:t GetMyCraftOrderInstantEntry GetMyCraftOrderSheet:t InteractionWithBoard:b
            IsCraftOrderMode:b IsEquipmnetACategory:b IsMyCraftTable:t IsVisibleACategory:b IsWorkingCraft:b
            LeaveCraftOrderMode MakeCraftOrderSheet:b ModifyFavoriteCraft:n PostCraftOrder ProcessCraftOrder
            ProcessCraftOrderInstant ReleaseInteractionWithBoard RequestCraftOrderFee ResetCraftOrder RestoreCraftOrderSheet
            SearchCraftOrder SetCraftOrderItemSlotFromPick SetRestoreCraftOrderSlot StopBatchCrafting StopCraftOrderSkill
            UpdateFavoriteCraftsToServer
            """),
        new("X2Auction", """
            AskAuctionArticle AskMarketPrice AttachItemFromBag AttachItemFromPick BidAuctionArticle CalcDeposit
            CancelAuctionArticle ClearSearchCondition DetachItem DirectPurchaseAuctionArticle EnoughByMoneyBuyAuctionItem:b
            GetAsrGoldLabels:t GetAsrVolLabels:t GetAttachedItemExist:b GetAttachedItemInfo:t GetChargeInfo:t
            GetCurrencyForBid:n GetCurrencyForFee:n GetDepositRatioValue:n GetLinkText GetLowestPrice:s
            GetMarkerPricePeriod:t GetMaxDepositValue:s GetPartitionPriceByCount:n GetPostType:n
            GetSearchedItemArticleId:s GetSearchedItemCount:n GetSearchedItemInfo:t GetSearchedItemPage:n
            GetSearchedItemPrice GetSearchedItemTotalCount:n GetSearchedSortInfo:t HasPostAuthority:b
            IsMyPutupArticle:b IsShowDirectPriceRangeEdit:b PartitionAuctionArticle PermissionCheckByCraft:b
            RequirePostAuthority:b SearchAuctionArticle SearchAuctionArticleByPage SearchDeclareSiege
            SearchMyAuctionArticles SearchMyBidList SearchRefresh SearchedListSort:b SetCurTab SetDuration SetListSort:b
            SetOpen SetPartitionBuyMinMaxCount SetPostType SetPrice SetPricePartition SetShowDirectPriceRangeEdit
            ToggleAuction
            """),
        new("X2Trade", """
            CanLock:b CancelTrade GetCurrencyForUserTrade:n GetTradeMoneyLimit:s HasEnoughExchangeFee:b
            IsOtherTradeLocked:b IsTradeLocked:b LockTrade OkTrade PutupTradeItem PutupTradeMoneyByStr RequestTrade
            StartTrade TakeDownTradeItemByInventoryIdx
            """),
        new("X2Butler", """
            AddGarden ApplyActabiltiyStats ChangeLook ChangeName ChargeLp ClearReservedHarvest ClearReservedSlot
            EnterInteractionMode ExpandHarvestSlot ExpandSpecialtyTradeSlot GetActability GetActabilityAdvantage:n
            GetActabilityPoint:n GetAttribute:n GetBindInfo:t GetButlerDesc:t GetButlerZoneGroupInfo:t GetChargeInfo:t
            GetChargeLpInfo:t GetEquipment:t GetLookItem:t GetMyButlerInfo:t GetMyGardenInfo:t GetMyHarvestInfo:t
            GetMyJobCount:t GetMySpecialtyTradeInfo:t GetMyTractorInfo:t GetProductionCostFreeChargeCount:n
            GetReservedHarvest:t GetReservedSlotItem:t GetResetWeeklyDay:s GetSellableZoneGroups:t GetSpecialties:t
            GetSpecialtyDetailInfo:t IsButlerHarvestItem:b IsEnableSpecialtyTrade:b LeaveInteractionMode
            PickupButlerEquipment RechargeProductionCost RegisterHarvest RegisterSpecialtyTrade RegisterTractor RemoveGarden
            RemoveTractor ResetActabiltiyStat SetReservedHarvest SetReservedSlot Unbind UnequipButlerEquipment:b
            UnregisterHarvest UnregisterSpecialtyTrade
            """),
        new("X2Mate", """
            AggressiveMode:b CanDismiss:b CanMount:b CanUnmount:b DismissPet:b GetAutoStartMountSkill:n
            GetCommandIconInfo:t GetMountSkill:t GetNumMountSkills:n GetPetExpToNextLevel GetSpeedInfo:n HavePassenger:b
            IsAttackablePet:b IsHavePassengerSeat:b IsMountablePet:b IsMyPet:b IsPlayerMounting:b
            IsPlayerPetExists:b IsTargetMyMate:b KickPassenger MountMate OrderAttackTarget:b PassiveMode:b PetState
            ProtectiveMode:b SetMateName StandMode:b UnMountMate
            """),
        new("X2InGameShop", """
            CheckReady:b CheckSelectedGoodsDetail:b CheckWaitingServer DeleteGoodsInCart EnterBeautyShop GenderTransfer
            GetBuyResult:t GetCartInfos:t GetFirstMainTab:n GetFirstSubTab:n GetGoods:t GetGoodsPerPage:n GetMainTabs:t
            GetSecondPriceType:n GetSelectedGoods:t GetSortFilter:t GetSubTabs:t IsInGameShopEnable:b IsSearchMode:b
            LeaveSearch LeaveSort PutSelectedGoodsInCart:n RequestBuy:b Search SelectGoods SelectMainTab SelectPage
            SelectSubTab Sort
            """),
        new("X2PremiumService", """
            BuyPremiumService GetAdvancedMembershipEndTime:n GetAncientMembershipEndTime:n GetPcbangBenefitList:t
            GetPcbangBenefitUiStyle GetPremiumBuyMax:n GetPremiumGradePoint:n GetPremiumMaxGrade:n GetPremiumPoint:n
            GetPremiumPointToGet GetPremiumServiceBenefitMenuCount:n GetPremiumServiceBenefitMenuData:t
            GetPremiumServiceBenefitMenuName:s GetPremiumServiceBuyCount:n GetPremiumServiceBuyData:t
            GetPremiumServiceBuyItemInfo:t GetPremiumSeviceEndTime:n HasAdvancedMembership:b HasAncientMembership:b
            Initialize IsPremiumNativeSite:b IsPremiumService:b IsPremiumSeviceListRequested:b OpenPremiumWarringSite
            RequestPremiumServiceList RequestRefreshCash UsePcbangBuff:b
            """),
        new("X2Customizer", """
            CanEnterBeautyShop:n DeleteCustomData:n DeleteTemporaryCustomData FindCustomSameNameFile:n
            GetBeautyShopConfigInfo:t GetCustomBodyNormalItem:t GetCustomDataInfoByIndex:t GetCustomDecoItem:t
            GetCustomEyebrowItem:t GetCustomFaceNormalItem:t GetCustomHairItem:t GetCustomHornColorItem:t
            GetCustomHornItem:t GetCustomMakeUpItem:t GetCustomPreviewClothItem:t GetCustomPupilItem:t GetCustomScarItem:t
            GetCustomSkinColorItem:t GetCustomTailItem:t GetCustomTattooItem:t GetFacePresetEyeItem:s
            GetFacePresetLipItem:s GetFacePresetNoseItem:s GetFacePresetShapeItem:s GetFaceTargetIndex:n
            GetFaceTargetMaxValue:n GetFaceTargetMinValue:n GetFaceTargetName:s GetNumCustomHair:n
            GetNumCustomHairColor:n GetNumCustomHorn:n GetNumCustomHornColor:n GetNumCustomSkinColor:n
            GetNumCustomTail:n GetNumCustomizingBodyNormal:n GetNumCustomizingDeco:n GetNumCustomizingEyebrow:n
            GetNumCustomizingFaceDiffuse:n GetNumCustomizingFaceNormal:n GetNumCustomizingMakeUp:n
            GetNumCustomizingPreviewCloth:n GetNumCustomizingPupil:n GetNumCustomizingScar:n GetNumCustomizingTattoo:n
            GetNumFaceTargets:n GetPresetCount:n GetSavedCustomDataCount:n GetTotalPresetItem:s HasTemporaryCustomData:b
            IsEnteredBeautyShop:b LoadCustomData:n LoadCustomFile LoadTemporaryCustomData:n ModifyFaceParam
            OverWriteCustomData:n SaveCustomData TerminateBeautyShop
            """),
        new("X2CustomizingUnit", """
            AddAnimationState ApplyCustomizerParamToUnit ApplyPresetParam GetCustomBodyNormal GetCustomDeco
            GetCustomEyebrow:n GetCustomEyebrowColor GetCustomFaceDiffuse:n GetCustomFaceNormal GetCustomHair:n
            GetCustomHairColor GetCustomHorn:n GetCustomHornColor:n GetCustomLipColor GetCustomMakeUp
            GetCustomPreviewCloth:n GetCustomPupil:n GetCustomPupilColor GetCustomScar:n GetCustomSkinColor:n
            GetCustomTail:n GetCustomTattoo GetCustomizingDecoColor GetCustomizingOddEyeUsable:b
            GetFaceTargetCurValue:n GetScarStatus:t GetSelectedPresetIndex:n GetTwoToneHairStatus InitCustomizerControl
            IsSmile:b ModifyFaceParamValue SetCustomizingBodyNormal SetCustomizingDeco SetCustomizingDecoColor
            SetCustomizingEyebrow SetCustomizingEyebrowColor SetCustomizingFaceDiffuse SetCustomizingFaceNormal
            SetCustomizingHair SetCustomizingHairColor SetCustomizingHairDefaultColor SetCustomizingHairTwoToneColor
            SetCustomizingHorn SetCustomizingHornColor SetCustomizingLipColor SetCustomizingMakeUp
            SetCustomizingPreviewCloth SetCustomizingPupil SetCustomizingPupilColor SetCustomizingScar
            SetCustomizingSkinColor SetCustomizingTail SetCustomizingTattoo SetSmile SetStance
            """),
        new("X2BlessUthstin", """
            ApplyStats ClearItemSlot CopyBlessUthstinPage EnterBlessUthstin ExpandBlessUthstinPage
            GetActivatedPageNumber:n GetApplyStatsItemInfo:t GetBlessUthstinIncreaseMax:t
            GetBlessUthstinInitItemInfo:t GetBlessUthstinItemInfo:t GetCashItemNeedCount:n GetCopyCost:t
            GetExpandItemInfo:t GetMaxPageCount:n GetPageCount:n GetSelectCost:t GetStatInfo:t GetTotalAppliedStats:n
            LeaveBlessUthstin SelectBlessUthstinPage SetBlessUthstinItemSlotFromPick SetPreviewPageNumber
            UseApplyStatsItem UseBlessUthstinExtendMaxStats UseBlessUthstinInitStats
            """),
        new("X2Ucc", """
            GetFgUserCount:n GetFgUserPath:s GetMakeUccConsumeInfo:t GetPatterns:t GetUccCategoryInfo:t
            GetUccUserDirectoryPath:s UploadColors UploadEmblem
            """),
        new("X2Bot", """
            Activate GetLocationFollowerCurrent:n GetLocationFollowerCurrentLoop IsActive:b IsDataShow:b
            SetControllerType SetLocationFollowerParam ShowData
            """),
    ];

    /// <summary>Registers all 410 methods in the twelve economy namespaces.</summary>
    public static void Install(X2LuaHost host, X2GameContext context, IX2EconomyData data)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);

        foreach (var set in BindingSets)
        {
            foreach (var token in set.Members.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = token.LastIndexOf(':');
                var method = colon < 0 ? token : token[..colon];
                var kind = colon < 0 ? '\0' : token[colon + 1];
                if (kind == '\0' && !method.StartsWith("Get", StringComparison.Ordinal))
                {
                    // Server/engine commands are unavailable when NullEconomyData is installed; Execute returns false.
                    host.Define(set.Table, method, a =>
                    {
                        data.Execute(new X2EconomyCommand(set.Table, method, a.Values.ToArray()));
                        return null;
                    });
                }
                else
                {
                    host.Define(set.Table, method, a =>
                    {
                        if (data.TryQuery(new X2EconomyQuery(set.Table, method, a.Values.ToArray()), out var value))
                            return ToLua(value);
                        // Per-method unavailable behavior follows the return kind captured from the running client.
                        return kind == '\0' ? null : Unavailable(kind);
                    });
                }
            }
        }

        InstallCraft(host, data);
        InstallAuction(host, data);
        InstallTrade(host, data);
        InstallButler(host, data);
        InstallMate(host, data);
        InstallShop(host, data);
        InstallPremium(host, data);
        InstallCustomizer(host, data);
        InstallBlessUthstin(host, data);
        InstallUcc(host, data);
        InstallBot(host, data);
    }

    private static object? Unavailable(char kind) => kind switch
    {
        'n' => 0.0,
        'b' => false,
        's' => "",
        't' => new LuaTable(),
        _ => null,
    };

    private static object? ToLua(object? value)
    {
        if (value is null or string or bool or double or LuaTable or LuaMulti)
            return value;
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or decimal)
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (value is X2EconomyValues multi)
            return new LuaMulti(multi.Values.Select(ToLua).ToArray());
        if (value is IReadOnlyDictionary<string, object?> readOnlyMap)
        {
            var table = new LuaTable();
            foreach (var pair in readOnlyMap) table[pair.Key] = ToLua(pair.Value);
            return table;
        }
        if (value is IDictionary map)
        {
            var table = new LuaTable();
            foreach (DictionaryEntry pair in map) table[pair.Key] = ToLua(pair.Value);
            return table;
        }
        if (value is IEnumerable sequence)
        {
            var table = new LuaTable();
            var index = 1;
            foreach (var entry in sequence) table[(double)index++] = ToLua(entry);
            return table;
        }
        return value;
    }

    private static LuaTable ArrayTable<T>(IEnumerable<T> values, Func<T, object?> convert)
    {
        var table = new LuaTable();
        var i = 1;
        foreach (var value in values) table[(double)i++] = ToLua(convert(value));
        return table;
    }

    private static T? At<T>(IReadOnlyList<T> values, int oneBased) where T : class
        => oneBased > 0 && oneBased <= values.Count ? values[oneBased - 1] : null;

    private static X2CraftRecipe? Recipe(IX2EconomyData data, int craftType)
        => data.Snapshot.CraftRecipes.FirstOrDefault(x => x.CraftType == craftType);

    private static X2MateState? Mate(IX2EconomyData data, int mateType)
        => data.Snapshot.Mates.FirstOrDefault(x => x.MateType == mateType);

    private static bool Execute(IX2EconomyData data, string table, string method, LuaArgs args)
        => data.Execute(new X2EconomyCommand(table, method, args.Values.ToArray()));

    private static object? QueryOr(IX2EconomyData data, string table, string method, LuaArgs args, object? fallback)
        => data.TryQuery(new X2EconomyQuery(table, method, args.Values.ToArray()), out var value) ? ToLua(value) : fallback;

    private static long ParseInt64(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static void InstallCraft(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Craft", "GetCraftName", a => Recipe(data, a.Int(0))?.Name ?? "");
        host.Define("X2Craft", "GetCraftTypeByItemType", a =>
            (double)(data.Snapshot.CraftRecipes.FirstOrDefault(x => x.ProductItemType == a.Int(0))?.CraftType ?? 0));
        host.Define("X2Craft", "GetExecutableCraftCount", a => QueryOr(data, "X2Craft", "GetExecutableCraftCount", a,
            (double)(Recipe(data, a.Int(0))?.ExecutableCount ?? 0)));
        host.Define("X2Craft", "GetCraftOrderableCount", a =>
        {
            var recipe = Recipe(data, a.Int(0));
            // The UI reads two results: maximum count and whether grade selection is allowed.
            return QueryOr(data, "X2Craft", "GetCraftOrderableCount", a,
                new LuaMulti((double)(recipe?.ExecutableCount ?? 0), false));
        });
        host.Define("X2Craft", "GetCraftBaseInfo", a =>
        {
            var recipe = Recipe(data, a.Int(0));
            if (data.TryQuery(new X2EconomyQuery("X2Craft", "GetCraftBaseInfo", a.Values.ToArray()), out var supplied))
                return ToLua(supplied);
            if (recipe == null) return null;
            var result = new LuaTable
            {
                ["craftType"] = (double)recipe.CraftType,
                ["name"] = recipe.Name,
                ["productItemType"] = (double)recipe.ProductItemType,
            };
            if (recipe.BaseInfo != null)
                foreach (var pair in recipe.BaseInfo) result[pair.Key] = ToLua(pair.Value);
            return result;
        });
        host.Define("X2Craft", "GetCraftProductInfo", a => QueryOr(data, "X2Craft", "GetCraftProductInfo", a,
            Recipe(data, a.Int(0)) is { Products: { } p } ? ArrayTable(p, x => x) : new LuaTable()));
        host.Define("X2Craft", "GetCraftMaterialInfo", a => QueryOr(data, "X2Craft", "GetCraftMaterialInfo", a,
            Recipe(data, a.Int(0)) is { Materials: { } m } ? ArrayTable(m, x => x) : new LuaTable()));
        host.Define("X2Craft", "GetCraftStepInfo", a => QueryOr(data, "X2Craft", "GetCraftStepInfo", a, null));
        host.Define("X2Craft", "GetListByItemType", a => ArrayTable(
            data.Snapshot.CraftRecipes.Where(x => x.ProductItemType == a.Int(0)), x => (double)x.CraftType));
        host.Define("X2Craft", "GetListByFavorite", _ => ArrayTable(
            data.Snapshot.CraftRecipes.Where(x => x.Favorite), x => (double)x.CraftType));
        host.Define("X2Craft", "GetList", a =>
        {
            // Category membership is game-data specific; hosts can answer it through TryQuery.
            return QueryOr(data, "X2Craft", "GetList", a,
                data.Snapshot.CraftRecipes.Count == 0 ? null : ArrayTable(data.Snapshot.CraftRecipes, x => (double)x.CraftType));
        });
        host.Define("X2Craft", "GetListBySearching", a =>
            QueryOr(data, "X2Craft", "GetListBySearching", a, new LuaTable()));
        host.Define("X2Craft", "GetMyCraftOrderInstantEntry", a =>
            QueryOr(data, "X2Craft", "GetMyCraftOrderInstantEntry", a, null));
        host.Define("X2Craft", "IsCraftOrderMode", _ => data.Snapshot.CraftOrderMode);
        host.Define("X2Craft", "IsWorkingCraft", a => QueryOr(data, "X2Craft", "IsWorkingCraft", a, data.Snapshot.Crafting));
        host.Define("X2Craft", "GetInteratcionTargetId", a => QueryOr(data, "X2Craft", "GetInteratcionTargetId", a,
            (double)data.Snapshot.CraftInteractionTargetId));
        host.Define("X2Craft", "GetInteratcionTargetDoodadType", a => QueryOr(data, "X2Craft", "GetInteratcionTargetDoodadType", a,
            (double)data.Snapshot.CraftInteractionDoodadType));
        host.Define("X2Craft", "GetLeftBatchCount", a => QueryOr(data, "X2Craft", "GetLeftBatchCount", a,
            (double)data.Snapshot.CraftBatchRemaining));
        host.Define("X2Craft", "InteractionWithBoard", a => Execute(data, "X2Craft", "InteractionWithBoard", a));
        host.Define("X2Craft", "MakeCraftOrderSheet", a => Execute(data, "X2Craft", "MakeCraftOrderSheet", a));
        host.Define("X2Craft", "ModifyFavoriteCraft", a => Execute(data, "X2Craft", "ModifyFavoriteCraft", a) ? 0.0 : -1.0);
        host.Define("X2Craft", "IsVisibleACategory", a => QueryOr(data, "X2Craft", "IsVisibleACategory", a, false));
        host.Define("X2Craft", "IsEquipmnetACategory", a => QueryOr(data, "X2Craft", "IsEquipmnetACategory", a, false));
    }

    private static void InstallAuction(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Auction", "GetSearchedItemCount", _ => (double)data.Snapshot.AuctionArticles.Count);
        host.Define("X2Auction", "GetSearchedItemTotalCount", _ => (double)data.Snapshot.AuctionTotal);
        host.Define("X2Auction", "GetSearchedItemPage", _ => (double)data.Snapshot.AuctionPage);
        host.Define("X2Auction", "GetSearchedItemArticleId", a => At(data.Snapshot.AuctionArticles, a.Int(0))?.ArticleId ?? "");
        host.Define("X2Auction", "GetSearchedItemInfo", a =>
            ToLua(At(data.Snapshot.AuctionArticles, a.Int(0))?.Info) ?? new LuaTable());
        host.Define("X2Auction", "GetSearchedItemPrice", a =>
            QueryOr(data, "X2Auction", "GetSearchedItemPrice", a, new LuaMulti("0", "0")));
        host.Define("X2Auction", "IsMyPutupArticle", a => At(data.Snapshot.AuctionArticles, a.Int(0))?.IsMine ?? false);
        host.Define("X2Auction", "GetAttachedItemExist", _ => data.Snapshot.AuctionAttachedItem != null);
        host.Define("X2Auction", "GetAttachedItemInfo", _ => ToLua(data.Snapshot.AuctionAttachedItem));
        host.Define("X2Auction", "EnoughByMoneyBuyAuctionItem", a => data.Snapshot.Money >= ParseInt64(a.Str(0)));
        host.Define("X2Auction", "GetLowestPrice", a => QueryOr(data, "X2Auction", "GetLowestPrice", a, ""));
        host.Define("X2Auction", "GetLinkText", a => QueryOr(data, "X2Auction", "GetLinkText", a, ""));
        host.Define("X2Auction", "CalcDeposit", a =>
            QueryOr(data, "X2Auction", "CalcDeposit", a, "0"));
        host.Define("X2Auction", "HasPostAuthority", a => QueryOr(data, "X2Auction", "HasPostAuthority", a, false));
        host.Define("X2Auction", "RequirePostAuthority", a => QueryOr(data, "X2Auction", "RequirePostAuthority", a, false));
        host.Define("X2Auction", "PermissionCheckByCraft", a => QueryOr(data, "X2Auction", "PermissionCheckByCraft", a, false));
        host.Define("X2Auction", "SearchedListSort", a => Execute(data, "X2Auction", "SearchedListSort", a));
        host.Define("X2Auction", "SetListSort", a => Execute(data, "X2Auction", "SetListSort", a));
    }

    private static void InstallTrade(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Trade", "CanLock", _ => data.Snapshot.Trade.Active && !data.Snapshot.Trade.MineLocked);
        host.Define("X2Trade", "IsTradeLocked", _ => data.Snapshot.Trade.MineLocked);
        host.Define("X2Trade", "IsOtherTradeLocked", _ => data.Snapshot.Trade.OtherLocked);
        host.Define("X2Trade", "GetCurrencyForUserTrade", _ => (double)data.Snapshot.Trade.Currency);
        host.Define("X2Trade", "GetTradeMoneyLimit", _ => data.Snapshot.Trade.MoneyLimit.ToString(CultureInfo.InvariantCulture));
        host.Define("X2Trade", "HasEnoughExchangeFee", a => ParseInt64(a.Str(0)) >= ParseInt64(a.Str(1)));
    }

    private static void InstallButler(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Butler", "GetButlerDesc", a => QueryOr(data, "X2Butler", "GetButlerDesc", a,
            new LuaTable
            {
                ["equipSlots"] = new LuaTable(),
                ["attributes"] = new LuaTable(),
                ["harvestGrades"] = new LuaTable(),
                ["actabilities"] = new LuaTable(),
                ["statReset"] = new LuaTable(),
                ["levelEffects"] = new LuaTable(),
            }));
        host.Define("X2Butler", "GetMyButlerInfo", a => QueryOr(data, "X2Butler", "GetMyButlerInfo", a,
            ToLua(data.Snapshot.Butler) ?? new LuaTable
            {
                ["bindWorldName"] = null,
                ["effectLevel"] = 0.0,
                ["exp"] = 0.0,
                ["harvestGrade"] = 0.0,
                ["isFree"] = false,
                ["isMine"] = false,
                ["laborPower"] = 0.0,
                ["level"] = 0.0,
                ["maxExp"] = 0.0,
                ["maxLaborPower"] = 0.0,
                ["maxProductionCost"] = 0.0,
                ["minExp"] = 0.0,
                ["model"] = 0.0,
                ["name"] = "",
                ["remainProductionCost"] = 0.0,
                ["remainStat"] = 0.0,
                ["remainStatResetCount"] = 0.0,
                ["testModelViewOffsetZ"] = 0.0,
            }));
        host.Define("X2Butler", "GetMyTractorInfo", a => QueryOr(data, "X2Butler", "GetMyTractorInfo", a,
            new LuaTable { ["usedTractorStorageCount"] = 0.0, ["totalTractorStorageCount"] = 0.0 }));
        host.Define("X2Butler", "GetMyGardenInfo", a => QueryOr(data, "X2Butler", "GetMyGardenInfo", a,
            new LuaTable
            {
                ["usedGardenCount"] = 0.0, ["maxGardenCount"] = 0.0,
                ["gardenInfo"] = new LuaTable(), ["gardenSize"] = 0.0,
                ["usedGardenSize"] = 0.0, ["totalGardenSize"] = 0.0,
                ["usedUnderWaterGardenSize"] = 0.0, ["totalUnderWaterGardenSize"] = 0.0,
                ["isGardenActive"] = false, ["isUnderWaterGardenActive"] = false,
                ["remainProductionCost"] = 0.0, ["overworkProductionCostRatio"] = 0.0,
                ["remainLaborPower"] = 0.0,
            }));
        host.Define("X2Butler", "GetMySpecialtyTradeInfo", a => QueryOr(data, "X2Butler", "GetMySpecialtyTradeInfo", a,
            new LuaTable { ["slotDetailinfo"] = new LuaTable(), ["emptySlotCount"] = 0.0 }));
        host.Define("X2Butler", "GetMyJobCount", a => QueryOr(data, "X2Butler", "GetMyJobCount", a,
            new LuaTable { ["harvestCount"] = 0.0, ["specialtyTradeCount"] = 0.0 }));
        // Type zero means no selected specialty. The native client returns nil for that query;
        // an empty table is truthy in Lua and makes the stock view dereference productInfo.
        host.Define("X2Butler", "GetSpecialtyDetailInfo", a => a.Int(0) == 0
            ? null
            : QueryOr(data, "X2Butler", "GetSpecialtyDetailInfo", a, null));
        host.Define("X2Butler", "GetActability", a => QueryOr(data, "X2Butler", "GetActability", a,
            new LuaMulti(0.0, 0.0)));
        host.Define("X2Butler", "IsEnableSpecialtyTrade", a => QueryOr(data, "X2Butler", "IsEnableSpecialtyTrade", a, false));
        host.Define("X2Butler", "UnequipButlerEquipment", a => Execute(data, "X2Butler", "UnequipButlerEquipment", a));
    }

    private static void InstallMate(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Mate", "IsPlayerPetExists", a => Mate(data, a.Int(0))?.Exists ?? false);
        host.Define("X2Mate", "IsMountablePet", a => Mate(data, a.Int(0))?.Mountable ?? false);
        host.Define("X2Mate", "IsAttackablePet", a => Mate(data, a.Int(0))?.Attackable ?? false);
        host.Define("X2Mate", "IsHavePassengerSeat", a => Mate(data, a.Int(0))?.HasPassengerSeat ?? false);
        host.Define("X2Mate", "HavePassenger", a => Mate(data, a.Int(0))?.HasPassenger ?? false);
        host.Define("X2Mate", "IsMyPet", a => data.Snapshot.Mates.Any(x =>
            x.Exists && x.IsMine && string.Equals(x.UnitId, a.Str(0), StringComparison.Ordinal)));
        host.Define("X2Mate", "IsPlayerMounting", _ => data.Snapshot.Mates.Any(x => x.Mounted));
        host.Define("X2Mate", "CanMount", a => Mate(data, a.Int(0)) is { Exists: true, Mountable: true, Mounted: false });
        host.Define("X2Mate", "CanUnmount", _ => data.Snapshot.Mates.Any(x => x.Mounted));
        host.Define("X2Mate", "CanDismiss", a => Mate(data, a.Int(0)) is { Exists: true, Mounted: false });
        host.Define("X2Mate", "GetSpeedInfo", a => (double)(Mate(data, a.Int(0))?.Speed ?? 0));
        host.Define("X2Mate", "PetState", a => QueryOr(data, "X2Mate", "PetState", a,
            Mate(data, a.Int(0)) is { State.Length: > 0 } mate ? mate.State : null));
        host.Define("X2Mate", "GetPetExpToNextLevel", a =>
        {
            var mate = Mate(data, a.Int(0));
            var current = mate?.Experience ?? 0;
            var previous = mate?.PreviousLevelExperience ?? 0;
            var required = mate?.ExpToNextLevel ?? 0;
            var earned = Math.Max(0, current - previous);
            var percent = required > 0 ? Math.Clamp(earned * 100.0 / required, 0, 100) : 0;
            // pet.lua reads percent, currentExp, previous-level total and experience required for this level.
            return QueryOr(data, "X2Mate", "GetPetExpToNextLevel", a,
                new LuaMulti(percent, current.ToString(CultureInfo.InvariantCulture),
                    previous.ToString(CultureInfo.InvariantCulture), required.ToString(CultureInfo.InvariantCulture)));
        });
        host.Define("X2Mate", "GetNumMountSkills", a => (double)(Mate(data, a.Int(0))?.Skills?.Count ?? 0));
        host.Define("X2Mate", "GetMountSkill", a =>
        {
            var skills = Mate(data, a.Int(0))?.Skills;
            return skills == null ? new LuaTable() : ToLua(At(skills, a.Int(1))) ?? new LuaTable();
        });
        foreach (var method in new[] { "AggressiveMode", "DismissPet", "OrderAttackTarget", "PassiveMode", "ProtectiveMode", "StandMode" })
            host.Define("X2Mate", method, a => Execute(data, "X2Mate", method, a));
    }

    private static void InstallShop(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2InGameShop", "CheckReady", _ => data.Snapshot.ShopReady);
        host.Define("X2InGameShop", "IsInGameShopEnable", _ => data.Snapshot.ShopEnabled);
        host.Define("X2InGameShop", "GetGoodsPerPage", a => QueryOr(data, "X2InGameShop", "GetGoodsPerPage", a, 8.0));
        host.Define("X2InGameShop", "GetGoods", a =>
        {
            var page = Math.Max(1, a.Int(0));
            var index = Math.Max(1, a.Int(1));
            var perPage = 8;
            return ToLua(At(data.Snapshot.ShopGoods, (page - 1) * perPage + index));
        });
        host.Define("X2InGameShop", "GetCartInfos", a => QueryOr(data, "X2InGameShop", "GetCartInfos", a,
            new LuaTable { ["infos"] = ArrayTable(data.Snapshot.ShopCart, x => x) }));
        host.Define("X2InGameShop", "GetBuyResult", _ => ToLua(data.Snapshot.ShopBuyResult) ?? new LuaTable());
        host.Define("X2InGameShop", "CheckSelectedGoodsDetail", a =>
            At(data.Snapshot.ShopGoods, a.Int(0)) != null);
        host.Define("X2InGameShop", "RequestBuy", a => Execute(data, "X2InGameShop", "RequestBuy", a));
        host.Define("X2InGameShop", "PutSelectedGoodsInCart", a =>
            Execute(data, "X2InGameShop", "PutSelectedGoodsInCart", a) ? 0.0 : -1.0);
    }

    private static void InstallPremium(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2PremiumService", "IsPremiumService", _ => data.Snapshot.Premium);
        host.Define("X2PremiumService", "GetPremiumSeviceEndTime", _ => (double)data.Snapshot.PremiumEndTime);
        host.Define("X2PremiumService", "GetPremiumPoint", _ => (double)data.Snapshot.PremiumPoint);
        host.Define("X2PremiumService", "GetPremiumGradePoint", _ => (double)data.Snapshot.PremiumGradePoint);
        // The benefit tab returns early (and never installs GetContentHeight) for a zero menu count.
        // The engine-owned catalog always has its base Premium Service row, even before account data arrives.
        host.Define("X2PremiumService", "GetPremiumMaxGrade", a =>
            QueryOr(data, "X2PremiumService", "GetPremiumMaxGrade", a, 1.0));
        host.Define("X2PremiumService", "GetPremiumServiceBenefitMenuCount", a =>
            QueryOr(data, "X2PremiumService", "GetPremiumServiceBenefitMenuCount", a, 1.0));
        host.Define("X2PremiumService", "GetPremiumServiceBenefitMenuName", a =>
            QueryOr(data, "X2PremiumService", "GetPremiumServiceBenefitMenuName", a, "Premium Service"));
        host.Define("X2PremiumService", "GetPremiumServiceBenefitMenuData", a =>
            QueryOr(data, "X2PremiumService", "GetPremiumServiceBenefitMenuData", a,
                new LuaTable { ["payEnd"] = (double)data.Snapshot.PremiumEndTime }));
        host.Define("X2PremiumService", "GetPcbangBenefitUiStyle", a =>
            QueryOr(data, "X2PremiumService", "GetPcbangBenefitUiStyle", a, "normal"));
        host.Define("X2PremiumService", "GetPremiumPointToGet", a =>
            QueryOr(data, "X2PremiumService", "GetPremiumPointToGet", a, 0.0));
        host.Define("X2PremiumService", "IsPremiumSeviceListRequested", a =>
            QueryOr(data, "X2PremiumService", "IsPremiumSeviceListRequested", a, false));
        host.Define("X2PremiumService", "UsePcbangBuff", a => Execute(data, "X2PremiumService", "UsePcbangBuff", a));
    }

    private static void InstallCustomizer(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Customizer", "GetSavedCustomDataCount", _ => (double)data.Snapshot.SavedCustomData.Count);
        host.Define("X2Customizer", "GetCustomDataInfoByIndex", a =>
        {
            var item = At(data.Snapshot.SavedCustomData, a.Int(0));
            // Native fields consumed by customizing_new: name, filepath and fullname.
            return item == null ? new LuaTable() : ToLua(item);
        });
        host.Define("X2Customizer", "HasTemporaryCustomData", a =>
            QueryOr(data, "X2Customizer", "HasTemporaryCustomData", a, false));
        host.Define("X2Customizer", "IsEnteredBeautyShop", _ => data.Snapshot.BeautyShopEntered);
        // The native file-backed customizer is not available in this viewer. Its invalid-result code is 0.
        host.Define("X2Customizer", "SaveCustomData", _ => 0.0);
        host.Define("X2Customizer", "CanEnterBeautyShop", a =>
            QueryOr(data, "X2Customizer", "CanEnterBeautyShop", a, data.Snapshot.ShopEnabled ? 0.0 : -1.0));
        host.Define("X2CustomizingUnit", "GetCustomizingOddEyeUsable", a =>
            QueryOr(data, "X2CustomizingUnit", "GetCustomizingOddEyeUsable", a, false));
        host.Define("X2CustomizingUnit", "GetScarStatus", a =>
            QueryOr(data, "X2CustomizingUnit", "GetScarStatus", a, new LuaTable
            {
                ["weight"] = 0.0, ["x"] = 0.0, ["y"] = 0.0, ["scale"] = 0.3, ["rotate"] = -180.0,
            }));
        host.Define("X2CustomizingUnit", "GetCustomScar", a =>
            QueryOr(data, "X2CustomizingUnit", "GetCustomScar", a, null));
    }

    private static void InstallBlessUthstin(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2BlessUthstin", "GetBlessUthstinItemInfo", a =>
            QueryOr(data, "X2BlessUthstin", "GetBlessUthstinItemInfo", a,
                ToLua(data.Snapshot.BlessUthstin) ?? new LuaTable()));
        host.Define("X2BlessUthstin", "GetBlessUthstinIncreaseMax", a =>
            QueryOr(data, "X2BlessUthstin", "GetBlessUthstinIncreaseMax", a,
                new LuaTable { ["itemType"] = 0.0, ["has"] = 0.0, ["need"] = 0.0 }));
        host.Define("X2BlessUthstin", "GetExpandItemInfo", a =>
            QueryOr(data, "X2BlessUthstin", "GetExpandItemInfo", a,
                new LuaMulti(0.0, null, 0.0, 0.0)));
        host.Define("X2BlessUthstin", "GetPageCount", a =>
            QueryOr(data, "X2BlessUthstin", "GetPageCount", a, 0.0));
        host.Define("X2BlessUthstin", "GetMaxPageCount", a =>
            QueryOr(data, "X2BlessUthstin", "GetMaxPageCount", a, 0.0));
        host.Define("X2BlessUthstin", "GetActivatedPageNumber", a =>
            QueryOr(data, "X2BlessUthstin", "GetActivatedPageNumber", a, 0.0));
    }

    private static void InstallUcc(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Ucc", "GetPatterns", _ => ArrayTable(data.Snapshot.UccPatterns, x => x));
        host.Define("X2Ucc", "GetFgUserCount", _ => (double)data.Snapshot.UccForegroundPaths.Count);
        host.Define("X2Ucc", "GetFgUserPath", a =>
        {
            // Native returns nil when the requested user foreground slot does not exist.
            var index = a.Int(0);
            return index >= 0 && index < data.Snapshot.UccForegroundPaths.Count
                ? data.Snapshot.UccForegroundPaths[index]
                : null;
        });
        host.Define("X2Ucc", "GetUccUserDirectoryPath", a =>
            QueryOr(data, "X2Ucc", "GetUccUserDirectoryPath", a, ""));
    }

    private static void InstallBot(X2LuaHost host, IX2EconomyData data)
    {
        host.Define("X2Bot", "IsActive", _ => data.Snapshot.BotActive);
        host.Define("X2Bot", "IsDataShow", a => data.Snapshot.VisibleBotData.Contains(a.Int(0)));
        host.Define("X2Bot", "GetLocationFollowerCurrent", a =>
            QueryOr(data, "X2Bot", "GetLocationFollowerCurrent", a, 0.0));
        host.Define("X2Bot", "GetLocationFollowerCurrentLoop", a =>
            QueryOr(data, "X2Bot", "GetLocationFollowerCurrentLoop", a, 0.0));
    }
}
