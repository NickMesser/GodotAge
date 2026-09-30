#nullable enable
namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Events registered by the economy, customization, UCC, bot and mate x2ui packages.</summary>
public static class X2EconomyEvents
{
    /// <summary>Arguments: actabilityId.</summary>
    public const string ActabilityExpertChanged = "ACTABILITY_EXPERT_CHANGED";
    /// <summary>Arguments: none.</summary>
    public const string ActabilityModifierUpdate = "ACTABILITY_MODIFIER_UPDATE";
    /// <summary>Arguments: bagId, slotId.</summary>
    public const string BagUpdate = "BAG_UPDATE";
    /// <summary>Arguments: none.</summary>
    public const string BeautyShopCloseBySystem = "BEAUTYSHOP_CLOSE_BY_SYSTEM";
    /// <summary>Arguments: none.</summary>
    public const string BlessUthstinExtendMaxStats = "BLESS_UTHSTIN_EXTEND_MAX_STATS";
    /// <summary>Arguments: none.</summary>
    public const string BlessUthstinItemSlotClear = "BLESS_UTHSTIN_ITEM_SLOT_CLEAR";
    /// <summary>Arguments: optional applyCountLimitMessage flag.</summary>
    public const string BlessUthstinItemSlotSet = "BLESS_UTHSTIN_ITEM_SLOT_SET";
    /// <summary>Arguments: none.</summary>
    public const string BlessUthstinUpdateStats = "BLESS_UTHSTIN_UPDATE_STATS";
    /// <summary>Arguments: itemType, increaseStatKind, decreaseStatKind, increasePoints, decreasePoints.</summary>
    public const string BlessUthstinWillApplyStats = "BLESS_UTHSTIN_WILL_APPLY_STATS";
    /// <summary>Arguments: updateKind, optional value.</summary>
    public const string ButlerInfoUpdated = "BUTLER_INFO_UPDATED";
    /// <summary>Arguments: interaction mode.</summary>
    public const string ButlerUiCommand = "BUTLER_UI_COMMAND";
    /// <summary>Arguments: result.</summary>
    public const string CancelCraftOrder = "CANCEL_CRAFT_ORDER";
    /// <summary>Arguments: none.</summary>
    public const string ChangeActabilityDecoNum = "CHANGE_ACTABILITY_DECO_NUM";
    /// <summary>Arguments: none.</summary>
    public const string ChangePayInfo = "CHANGE_PAY_INFO";
    /// <summary>Arguments: none.</summary>
    public const string CloseCraftOrder = "CLOSE_CRAFT_ORDER";
    /// <summary>Arguments: completion info table.</summary>
    public const string CompleteCraftOrder = "COMPLETE_CRAFT_ORDER";
    /// <summary>Arguments: remaining batch count.</summary>
    public const string CraftEnded = "CRAFT_ENDED";
    /// <summary>Arguments: result rows, total count, page.</summary>
    public const string CraftOrderEntrySearched = "CRAFT_ORDER_ENTRY_SEARCHED";
    /// <summary>Arguments: remaining batch count.</summary>
    public const string CraftStarted = "CRAFT_STARTED";
    /// <summary>Arguments: doodad id, requested count.</summary>
    public const string CraftingStart = "CRAFTING_START";
    /// <summary>Arguments: none.</summary>
    public const string CreateOriginUccItem = "CREATE_ORIGIN_UCC_ITEM";
    /// <summary>Arguments: none.</summary>
    public const string DeleteCraftOrder = "DELETE_CRAFT_ORDER";
    /// <summary>Arguments: itemName, itemGrade, isMarketPriceWindow, price history rows.</summary>
    public const string DiagonalAsr = "DIAGONAL_ASR";
    /// <summary>Arguments: none.</summary>
    public const string DiagonalLine = "DIAGONAL_LINE";
    /// <summary>Arguments: mateType.</summary>
    public const string DismissPet = "DISMISS_PET";
    /// <summary>Arguments: none.</summary>
    public const string EnteredInstantGameZone = "ENTERED_INSTANT_GAME_ZONE";
    /// <summary>Arguments: none.</summary>
    public const string EnteredLoading = "ENTERED_LOADING";
    /// <summary>Arguments: none.</summary>
    public const string ExpChanged = "EXP_CHANGED";
    /// <summary>Arguments: action, target token.</summary>
    public const string BuffUpdate = "BUFF_UPDATE";
    /// <summary>Arguments: action, target token.</summary>
    public const string DebuffUpdate = "DEBUFF_UPDATE";
    /// <summary>Arguments: none.</summary>
    public const string BuffSkillChanged = "BUFF_SKILL_CHANGED";
    /// <summary>Arguments: none.</summary>
    public const string EnteredWorld = "ENTERED_WORLD";
    /// <summary>Arguments: mateType.</summary>
    public const string FailedToSetPetAutoSkill = "FAILED_TO_SET_PET_AUTO_SKILL";
    /// <summary>Arguments: none.</summary>
    public const string GenderTransferred = "GENDER_TRANSFERED";
    /// <summary>Arguments: none; details are read with X2InGameShop:GetBuyResult.</summary>
    public const string InGameShopBuyResult = "INGAME_SHOP_BUY_RESULT";
    /// <summary>Arguments: none.</summary>
    public const string InsertCraftOrder = "INSERT_CRAFT_ORDER";
    /// <summary>Arguments: none.</summary>
    public const string InteractionEnd = "INTERACTION_END";
    /// <summary>Arguments: none.</summary>
    public const string LaborPowerChanged = "LABORPOWER_CHANGED";
    /// <summary>Arguments: none.</summary>
    public const string LeavedInstantGameZone = "LEAVED_INSTANT_GAME_ZONE";
    /// <summary>Arguments: none.</summary>
    public const string LeftLoading = "LEFT_LOADING";
    /// <summary>Arguments: unit token/id, string unit id.</summary>
    public const string LevelChanged = "LEVEL_CHANGED";
    /// <summary>Arguments: mateType, text.</summary>
    public const string MateSkillLearned = "MATE_SKILL_LEARNED";
    /// <summary>Arguments: mateType, stateIndex.</summary>
    public const string MateStateUpdate = "MATE_STATE_UPDATE";
    /// <summary>Arguments: mateType, isMyPet.</summary>
    public const string MountPet = "MOUNT_PET";
    /// <summary>Arguments: tab name.</summary>
    public const string OpenCraftOrderBoard = "OPEN_CRAFT_ORDER_BOARD";
    /// <summary>Arguments: doodadId, backgroundType, foregroundType.</summary>
    public const string OpenEmblemUploadUi = "OPEN_EMBLEM_UPLOAD_UI";
    /// <summary>Arguments: mateType.</summary>
    public const string PassengerMountPet = "PASSENGER_MOUNT_PET";
    /// <summary>Arguments: mateType.</summary>
    public const string PassengerUnmountPet = "PASSENGER_UNMOUNT_PET";
    /// <summary>Arguments: none.</summary>
    public const string PetAutoSkillChanged = "PET_AUTO_SKILL_CHANGED";
    /// <summary>Arguments: mateType.</summary>
    public const string PetFollowingMaster = "PET_FOLLOWING_MASTER";
    /// <summary>Arguments: mateType.</summary>
    public const string PetStopByMaster = "PET_STOP_BY_MASTER";
    /// <summary>Arguments: none.</summary>
    public const string PlayerAaPoint = "PLAYER_AA_POINT";
    /// <summary>Arguments: none.</summary>
    public const string PlayerMoney = "PLAYER_MONEY";
    /// <summary>Arguments: result.</summary>
    public const string PostCraftOrder = "POST_CRAFT_ORDER";
    /// <summary>Arguments: none.</summary>
    public const string PremiumFirstBuyBonus = "PREMIUM_FIRST_BUY_BONUS";
    /// <summary>Arguments: previous grade, current grade.</summary>
    public const string PremiumGradeChange = "PREMIUM_GRADE_CHANGE";
    /// <summary>Arguments: none.</summary>
    public const string PremiumPointChange = "PREMIUM_POINT_CHANGE";
    /// <summary>Arguments: error/result code.</summary>
    public const string PremiumServiceBuyResult = "PREMIUM_SERVICE_BUY_RESULT";
    /// <summary>Arguments: none.</summary>
    public const string PremiumServiceListUpdated = "PREMIUM_SERVICE_LIST_UPDATED";
    /// <summary>Arguments: result, processType.</summary>
    public const string ProcessCraftOrder = "PROCESS_CRAFT_ORDER";
    /// <summary>Arguments: none.</summary>
    public const string ResetInGameShopModelView = "RESET_INGAME_SHOP_MODELVIEW";
    /// <summary>Arguments: favorite craft data rows.</summary>
    public const string RollbackFavoriteCrafts = "ROLLBACK_FAVORITE_CRAFTS";
    /// <summary>Arguments: mateType.</summary>
    public const string SpawnPet = "SPAWN_PET";
    /// <summary>Arguments: target unit id string.</summary>
    public const string TradeCanStart = "TRADE_CAN_START";
    /// <summary>Arguments: none.</summary>
    public const string TradeCanceled = "TRADE_CANCELED";
    /// <summary>Arguments: inventory index, amount.</summary>
    public const string TradeItemPutup = "TRADE_ITEM_PUTUP";
    /// <summary>Arguments: inventory index.</summary>
    public const string TradeItemTookDown = "TRADE_ITEM_TOOKDOWN";
    /// <summary>Arguments: none.</summary>
    public const string TradeLocked = "TRADE_LOCKED";
    /// <summary>Arguments: none.</summary>
    public const string TradeMade = "TRADE_MADE";
    /// <summary>Arguments: money string.</summary>
    public const string TradeMoneyPutup = "TRADE_MONEY_PUTUP";
    /// <summary>Arguments: none.</summary>
    public const string TradeOk = "TRADE_OK";
    /// <summary>Arguments: other slot index, item type, stack count, tooltip table.</summary>
    public const string TradeOtherItemPutup = "TRADE_OTHER_ITEM_PUTUP";
    /// <summary>Arguments: other slot index.</summary>
    public const string TradeOtherItemTookDown = "TRADE_OTHER_ITEM_TOOKDOWN";
    /// <summary>Arguments: none.</summary>
    public const string TradeOtherLocked = "TRADE_OTHER_LOCKED";
    /// <summary>Arguments: money string.</summary>
    public const string TradeOtherMoneyPutup = "TRADE_OTHER_MONEY_PUTUP";
    /// <summary>Arguments: none.</summary>
    public const string TradeOtherOk = "TRADE_OTHER_OK";
    /// <summary>Arguments: targetName.</summary>
    public const string TradeStarted = "TRADE_STARTED";
    /// <summary>Arguments: none.</summary>
    public const string TradeUiToggle = "TRADE_UI_TOGGLE";
    /// <summary>Arguments: none.</summary>
    public const string TradeUnlocked = "TRADE_UNLOCKED";
    /// <summary>Arguments: none.</summary>
    public const string ToggleWalk = "TOGGLE_WALK";
    /// <summary>Arguments: none.</summary>
    public const string UiPermissionUpdate = "UI_PERMISSION_UPDATE";
    /// <summary>Arguments: none.</summary>
    public const string UiReloaded = "UI_RELOADED";
    /// <summary>Arguments: none.</summary>
    public const string UnitNameChanged = "UNIT_NAME_CHANGED";
    /// <summary>Arguments: none.</summary>
    public const string UpdateBindings = "UPDATE_BINDINGS";
    /// <summary>Arguments: mateType, isMyPet.</summary>
    public const string UnmountPet = "UNMOUNT_PET";
    /// <summary>Arguments: totalTime, remainingTime, count, question.</summary>
    public const string UpdateBotCheckInfo = "UPDATE_BOT_CHECK_INFO";
    /// <summary>Arguments: fee info table.</summary>
    public const string UpdateCraftOrderItemFee = "UPDATE_CRAFT_ORDER_ITEM_FEE";
    /// <summary>Arguments: item slot info table.</summary>
    public const string UpdateCraftOrderItemSlot = "UPDATE_CRAFT_ORDER_ITEM_SLOT";
    /// <summary>Arguments: skill key, fired flag.</summary>
    public const string UpdateCraftOrderSkill = "UPDATE_CRAFT_ORDER_SKILL";
    /// <summary>Arguments: changeKind, value2, value3.</summary>
    public const string UpdateInGameShop = "UPDATE_INGAME_SHOP";
    /// <summary>Arguments: modeKind, modeValue.</summary>
    public const string UpdateInGameShopView = "UPDATE_INGAME_SHOP_VIEW";
    /// <summary>Arguments: material info rows.</summary>
    public const string UpdateRestoreCraftOrderItemMaterial = "UPDATE_RESTORE_CRAFT_ORDER_ITEM_MATERIAL";
    /// <summary>Arguments: restored slot info table.</summary>
    public const string UpdateRestoreCraftOrderItemSlot = "UPDATE_RESTORE_CRAFT_ORDER_ITEM_SLOT";
    /// <summary>Arguments: cash sale type.</summary>
    public const string ViewCashBuyWindow = "VIEW_CASH_BUY_WINDOW";
    /// <summary>Arguments: waiting flag or request kind.</summary>
    public const string WaitReplyFromServer = "WAIT_REPLY_FROM_SERVER";

    /// <summary>Arguments: attached flag.</summary>
    public const string AuctionItemAttachmentStateChanged = "AUCTION_ITEM_ATTACHMENT_STATE_CHANGED";
    /// <summary>Arguments: itemName.</summary>
    public const string AuctionItemPutUp = "AUCTION_ITEM_PUT_UP";
    /// <summary>Arguments: none.</summary>
    public const string AuctionItemSearch = "AUCTION_ITEM_SEARCH";
    /// <summary>Arguments: clearLastSearchArticle.</summary>
    public const string AuctionItemSearched = "AUCTION_ITEM_SEARCHED";
    /// <summary>Arguments: itemName, money string.</summary>
    public const string AuctionBidded = "AUCTION_BIDDED";
    /// <summary>Arguments: client callback specific; the package forwards them to ItemBoughtBySomeone.</summary>
    public const string AuctionBoughtBySomeone = "AUCTION_BOUGHT_BY_SOMEONE";
    /// <summary>Arguments: itemName.</summary>
    public const string AuctionCanceled = "AUCTION_CANCELED";
    /// <summary>Arguments: localized message.</summary>
    public const string AuctionCharacterLevelTooLow = "AUCTION_CHARACTER_LEVEL_TOO_LOW";
    /// <summary>Arguments: itemType, itemGrade, price string.</summary>
    public const string AuctionLowestPrice = "AUCTION_LOWEST_PRICE";
    /// <summary>Arguments: craftType.</summary>
    public const string AuctionPermissionByCraft = "AUCTION_PERMISSION_BY_CRAFT";
}
