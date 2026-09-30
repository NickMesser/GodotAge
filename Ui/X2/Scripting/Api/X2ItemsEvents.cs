#nullable enable
namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Item-window event names passed to X2GameContext.Events.Fire.</summary>
public static class X2ItemsEvents
{
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string AbilityChanged = "ABILITY_CHANGED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string AccountRestrictNotice = "ACCOUNT_RESTRICT_NOTICE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string AddedItem = "ADDED_ITEM";
    /// <summary>Handler arguments (Lua order): stringId, stringId, isChanged.</summary>
    public const string AppellationChanged = "APPELLATION_CHANGED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string AppellationGained = "APPELLATION_GAINED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string AppellationStampSet = "APPELLATION_STAMP_SET";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BagExpanded = "BAG_EXPANDED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BagItemConfirmed = "BAG_ITEM_CONFIRMED";
    /// <summary>Handler arguments (Lua order): isRealSlotShow.</summary>
    public const string BagRealIndexShow = "BAG_REAL_INDEX_SHOW";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BagTabCreated = "BAG_TAB_CREATED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BagTabRemoved = "BAG_TAB_REMOVED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BagTabSorted = "BAG_TAB_SORTED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BagTabSwitched = "BAG_TAB_SWITCHED";
    /// <summary>Handler arguments (Lua order): bagId, slotId, widget.</summary>
    public const string BagUpdate = "BAG_UPDATE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BankExpanded = "BANK_EXPANDED";
    /// <summary>Handler arguments (Lua order): isRealSlotShow.</summary>
    public const string BankRealIndexShow = "BANK_REAL_INDEX_SHOW";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BankTabCreated = "BANK_TAB_CREATED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BankTabRemoved = "BANK_TAB_REMOVED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BankTabSorted = "BANK_TAB_SORTED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BankTabSwitched = "BANK_TAB_SWITCHED";
    /// <summary>Handler arguments (Lua order): bagId, slotId.</summary>
    public const string BankUpdate = "BANK_UPDATE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BlessUthstinExtendMaxStats = "BLESS_UTHSTIN_EXTEND_MAX_STATS";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BlessUthstinItemSlotClear = "BLESS_UTHSTIN_ITEM_SLOT_CLEAR";
    /// <summary>Handler arguments (Lua order): msgapplycountlimit.</summary>
    public const string BlessUthstinItemSlotSet = "BLESS_UTHSTIN_ITEM_SLOT_SET";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string BlessUthstinUpdateStats = "BLESS_UTHSTIN_UPDATE_STATS";
    /// <summary>Handler arguments (Lua order): skilltype, incStatsKind, decStatsKind, incStatsPoint, decStatsPoint.</summary>
    public const string BlessUthstinWillApplyStats = "BLESS_UTHSTIN_WILL_APPLY_STATS";
    /// <summary>Handler arguments (Lua order): action, target.</summary>
    public const string BuffUpdate = "BUFF_UPDATE";
    /// <summary>Handler arguments (Lua order): list.</summary>
    public const string BuySpecialtyContentInfo = "BUY_SPECIALTY_CONTENT_INFO";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string ChangedAutoUseAapoint = "CHANGED_AUTO_USE_AAPOINT";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string ChangeContributionPointToStore = "CHANGE_CONTRIBUTION_POINT_TO_STORE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string ChangeVisualRaceEnded = "CHANGE_VISUAL_RACE_ENDED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string CofferInteractionEnd = "COFFER_INTERACTION_END";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string CofferInteractionStart = "COFFER_INTERACTION_START";
    /// <summary>Handler arguments (Lua order): isRealSlotShow.</summary>
    public const string CofferRealIndexShow = "COFFER_REAL_INDEX_SHOW";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string CofferTabCreated = "COFFER_TAB_CREATED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string CofferTabRemoved = "COFFER_TAB_REMOVED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string CofferTabSorted = "COFFER_TAB_SORTED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string CofferTabSwitched = "COFFER_TAB_SWITCHED";
    /// <summary>Handler arguments (Lua order): bagId, slotId.</summary>
    public const string CofferUpdate = "COFFER_UPDATE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string CrimeReported = "CRIME_REPORTED";
    /// <summary>Handler arguments (Lua order): action, target.</summary>
    public const string DebuffUpdate = "DEBUFF_UPDATE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string DyeingEnd = "DYEING_END";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string DyeingStart = "DYEING_START";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string EndHeroElectionPeriod = "END_HERO_ELECTION_PERIOD";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string EnteredLoading = "ENTERED_LOADING";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string EnteredWorld = "ENTERED_WORLD";
    /// <summary>Handler arguments (Lua order): mode.</summary>
    public const string EnterEnchantItemMode = "ENTER_ENCHANT_ITEM_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string EnterSecurityLockItemMode = "ENTER_SECURITY_LOCK_ITEM_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string EnterSecurityUnlockItemMode = "ENTER_SECURITY_UNLOCK_ITEM_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string EnterSlaveEquipChangeMode = "ENTER_SLAVE_EQUIP_CHANGE_MODE";
    /// <summary>Handler arguments (Lua order): levelEffectInfo.</summary>
    public const string EquipSlotReinforceMsgChagneLevelEffect = "EQUIP_SLOT_REINFORCE_MSG_CHAGNE_LEVEL_EFFECT";
    /// <summary>Handler arguments (Lua order): equipSlot.</summary>
    public const string EquipSlotReinforceMsgLevelUp = "EQUIP_SLOT_REINFORCE_MSG_LEVEL_UP";
    /// <summary>Handler arguments (Lua order): equipSlot.</summary>
    public const string EquipSlotReinforceUpdate = "EQUIP_SLOT_REINFORCE_UPDATE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string ExpeditionManagementRoleChanged = "EXPEDITION_MANAGEMENT_ROLE_CHANGED";
    /// <summary>Handler arguments (Lua order): logs.</summary>
    public const string GachaLootPackLog = "GACHA_LOOT_PACK_LOG";
    /// <summary>Handler arguments (Lua order): results.</summary>
    public const string GachaLootPackResult = "GACHA_LOOT_PACK_RESULT";
    /// <summary>Handler arguments (Lua order): myUnit.</summary>
    public const string HeirLevelUp = "HEIR_LEVEL_UP";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string HeroScoreUpdated = "HERO_SCORE_UPDATED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string InteractionEnd = "INTERACTION_END";
    /// <summary>Handler arguments (Lua order): invenType, slot.</summary>
    public const string InvenSlotSplit = "INVEN_SLOT_SPLIT";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string ItemLookConverted = "ITEM_LOOK_CONVERTED";
    /// <summary>Handler arguments (Lua order): num.</summary>
    public const string JuryWaitingNumber = "JURY_WAITING_NUMBER";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LeaveEnchantItemMode = "LEAVE_ENCHANT_ITEM_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LeaveGachaLootMode = "LEAVE_GACHA_LOOT_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LeaveSecurityLockItemMode = "LEAVE_SECURITY_LOCK_ITEM_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LeaveSecurityUnlockItemMode = "LEAVE_SECURITY_UNLOCK_ITEM_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LeaveSlaveEquipChangeMode = "LEAVE_SLAVE_EQUIP_CHANGE_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LeftLoading = "LEFT_LOADING";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LevelChanged = "LEVEL_CHANGED";
    /// <summary>Handler argument: setTime (whether this loot window is timed).</summary>
    public const string LootBagChanged = "LOOT_BAG_CHANGED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string LootBagClose = "LOOT_BAG_CLOSE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string MoveSpeedChange = "MOVE_SPEED_CHANGE";
    /// <summary>Handler arguments (Lua order): widget.</summary>
    public const string NpcInteractionEnd = "NPC_INTERACTION_END";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string OptionReset = "OPTION_RESET";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerAaPoint = "PLAYER_AA_POINT";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerBankAaPoint = "PLAYER_BANK_AA_POINT";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerBankMoney = "PLAYER_BANK_MONEY";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerHonorPoint = "PLAYER_HONOR_POINT";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerJuryPoint = "PLAYER_JURY_POINT";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerLivingPoint = "PLAYER_LIVING_POINT";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerMoney = "PLAYER_MONEY";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PlayerVisualRace = "PLAYER_VISUAL_RACE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string PreliminaryEquipUpdate = "PRELIMINARY_EQUIP_UPDATE";
    /// <summary>Handler arguments (Lua order): isHide, isdailyReset.</summary>
    public const string RandomShopInfo = "RANDOM_SHOP_INFO";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string RandomShopUpdate = "RANDOM_SHOP_UPDATE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string RefreshCombatResource = "REFRESH_COMBAT_RESOURCE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string RefreshStoreMerchantGoodLimitPurchase = "REFRESH_STORE_MERCHANT_GOOD_LIMIT_PURCHASE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string RemovedItem = "REMOVED_ITEM";
    /// <summary>Handler arguments (Lua order): list.</summary>
    public const string SellSpecialtyContentInfo = "SELL_SPECIALTY_CONTENT_INFO";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string SkillsReset = "SKILLS_RESET";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string SkillChanged = "SKILL_CHANGED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string SkillLearned = "SKILL_LEARNED";
    /// <summary>Handler arguments (Lua order): list, usingSlotIndex.</summary>
    public const string SkillSelectiveItem = "SKILL_SELECTIVE_ITEM";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string SkillSelectiveItemNotAvailable = "SKILL_SELECTIVE_ITEM_NOT_AVAILABLE";
    /// <summary>Handler arguments (Lua order): status.</summary>
    public const string SkillSelectiveItemReadyStatus = "SKILL_SELECTIVE_ITEM_READY_STATUS";
    /// <summary>Handler arguments (Lua order): list.</summary>
    public const string SpecialtyContentRecipeInfo = "SPECIALTY_CONTENT_RECIPE_INFO";
    /// <summary>Handler arguments (Lua order): specialtyRatioTable.</summary>
    public const string SpecialtyRatioBetweenInfo = "SPECIALTY_RATIO_BETWEEN_INFO";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string StartHeroElectionPeriod = "START_HERO_ELECTION_PERIOD";
    /// <summary>Handler arguments (Lua order): slotNumber.</summary>
    public const string StoreAddSellItem = "STORE_ADD_SELL_ITEM";
    /// <summary>Handler arguments (Lua order): itemLinkText, stackCount.</summary>
    public const string StoreBuy = "STORE_BUY";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string StoreFull = "STORE_FULL";
    /// <summary>Handler arguments (Lua order): itemLinkText, stackCount.</summary>
    public const string StoreSell = "STORE_SELL";
    /// <summary>Handler arguments (Lua order): soldItems.</summary>
    public const string StoreSoldList = "STORE_SOLD_LIST";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string StoreTradeFailed = "STORE_TRADE_FAILED";
    /// <summary>Handler arguments (Lua order): mode.</summary>
    public const string SwitchEnchantItemMode = "SWITCH_ENCHANT_ITEM_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string TargetChanged = "TARGET_CHANGED";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string ToggleChangeVisualRace = "TOGGLE_CHANGE_VISUAL_RACE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string ToggleWalk = "TOGGLE_WALK";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string TransformCombatResource = "TRANSFORM_COMBAT_RESOURCE";
    /// <summary>Handler arguments (Lua order): key, timeStamp, itemLink.</summary>
    public const string TryLootDice = "TRY_LOOT_DICE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string UiPermissionUpdate = "UI_PERMISSION_UPDATE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string UiReloaded = "UI_RELOADED";
    /// <summary>Handler arguments (Lua order): equipSlot, frame, widget.</summary>
    public const string UnitEquipmentChanged = "UNIT_EQUIPMENT_CHANGED";
    /// <summary>Handler arguments (Lua order): fired.</summary>
    public const string UpdateChangeVisualRaceWnd = "UPDATE_CHANGE_VISUAL_RACE_WND";
    /// <summary>Handler arguments (Lua order): frame, added, removed.</summary>
    public const string UpdateDurabilityStatus = "UPDATE_DURABILITY_STATUS";
    /// <summary>Handler arguments (Lua order): executeable.</summary>
    public const string UpdateDyeingExcutable = "UPDATE_DYEING_EXCUTABLE";
    /// <summary>Handler arguments (Lua order): isExcutable, isLock.</summary>
    public const string UpdateEnchantItemMode = "UPDATE_ENCHANT_ITEM_MODE";
    /// <summary>Handler arguments (Lua order): isExcutable, isLock.</summary>
    public const string UpdateGachaLootMode = "UPDATE_GACHA_LOOT_MODE";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string UpdateHeroElectionCondition = "UPDATE_HERO_ELECTION_CONDITION";
    /// <summary>No positional arguments are read by the registered item-window handler.</summary>
    public const string UpdateItemLookConvertMode = "UPDATE_ITEM_LOOK_CONVERT_MODE";
    /// <summary>Handler arguments (Lua order): refund, sellItem.</summary>
    public const string UpdateSpecialtyRatio = "UPDATE_SPECIALTY_RATIO";
}
