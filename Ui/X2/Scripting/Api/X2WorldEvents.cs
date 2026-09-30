#nullable enable

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>UI event names registered by the World-family x2ui packages.</summary>
public static class X2WorldEvents
{
    /// <summary>Arguments read by registered handlers: arg1, arg2.</summary>
    public const string ADD_GIVEN_QUEST_INFO = "ADD_GIVEN_QUEST_INFO";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string ADD_NOTIFY_QUEST_INFO = "ADD_NOTIFY_QUEST_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CHANGE_ACTABILITY_DECO_NUM = "CHANGE_ACTABILITY_DECO_NUM";
    /// <summary>Arguments read by registered handlers: optionType, infoTable.</summary>
    public const string CHANGE_OPTION = "CHANGE_OPTION";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CHANGE_ROADMAP_SIZE = "CHANGE_ROADMAP_SIZE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_BOSS_TELESCOPE_INFO = "CLEAR_BOSS_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_CARRYING_BACKPACK_SLAVE_INFO = "CLEAR_CARRYING_BACKPACK_SLAVE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_COMPLETED_QUEST_INFO = "CLEAR_COMPLETED_QUEST_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_CORPSE_INFO = "CLEAR_CORPSE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_DOODAD_INFO = "CLEAR_DOODAD_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_FISH_SCHOOL_INFO = "CLEAR_FISH_SCHOOL_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_GIVEN_QUEST_STATIC_INFO = "CLEAR_GIVEN_QUEST_STATIC_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_HOUSING_INFO = "CLEAR_HOUSING_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_MY_SLAVE_POS_INFO = "CLEAR_MY_SLAVE_POS_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_NOTIFY_QUEST_INFO = "CLEAR_NOTIFY_QUEST_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_NPC_INFO = "CLEAR_NPC_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_SHIP_TELESCOPE_INFO = "CLEAR_SHIP_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string CLEAR_TRANSFER_TELESCOPE_INFO = "CLEAR_TRANSFER_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string DELETE_PORTAL = "DELETE_PORTAL";
    /// <summary>Arguments read by registered handlers: key.</summary>
    public const string DOMINION_GUARD_TOWER_STATE_NOTICE = "DOMINION_GUARD_TOWER_STATE_NOTICE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string DOMINION_SIEGE_PARTICIPANT_COUNT_CHANGED = "DOMINION_SIEGE_PARTICIPANT_COUNT_CHANGED";
    /// <summary>Arguments read by registered handlers: info OR none.</summary>
    public const string DOMINION_SIEGE_PERIOD_CHANGED = "DOMINION_SIEGE_PERIOD_CHANGED";
    /// <summary>Arguments read by registered handlers: none OR secondHalf.</summary>
    public const string DOMINION_SIEGE_UPDATE_TIMER = "DOMINION_SIEGE_UPDATE_TIMER";
    /// <summary>Arguments read by registered handlers: tooltip.</summary>
    public const string DRAW_DOODAD_SIGN_TAG = "DRAW_DOODAD_SIGN_TAG";
    /// <summary>Arguments read by registered handlers: info.</summary>
    public const string DRAW_DOODAD_TOOLTIP = "DRAW_DOODAD_TOOLTIP";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string END_HERO_ELECTION_PERIOD = "END_HERO_ELECTION_PERIOD";
    /// <summary>Arguments read by registered handlers: zoneId.</summary>
    public const string ENTER_ANOTHER_ZONEGROUP = "ENTER_ANOTHER_ZONEGROUP";
    /// <summary>Arguments read by registered handlers: none OR type.</summary>
    public const string ENTERED_INSTANT_GAME_ZONE = "ENTERED_INSTANT_GAME_ZONE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string ENTERED_LOADING = "ENTERED_LOADING";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string ENTERED_WORLD = "ENTERED_WORLD";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string EXPEDITION_WAR_KILL_SCORE = "EXPEDITION_WAR_KILL_SCORE";
    /// <summary>Arguments read by registered handlers: related, state.</summary>
    public const string EXPEDITION_WAR_STATE = "EXPEDITION_WAR_STATE";
    /// <summary>Arguments read by registered handlers: info.</summary>
    public const string FACTION_COMPETITION_INFO = "FACTION_COMPETITION_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string FACTION_COMPETITION_RESULT = "FACTION_COMPETITION_RESULT";
    /// <summary>Arguments read by registered handlers: info.</summary>
    public const string FACTION_COMPETITION_UPDATE_POINT = "FACTION_COMPETITION_UPDATE_POINT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string GAME_SCHEDULE = "GAME_SCHEDULE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HERO_ELECTION_VOTED = "HERO_ELECTION_VOTED";
    /// <summary>Arguments read by registered handlers: text.</summary>
    public const string HIDE_ROADMAP_TOOLTIP = "HIDE_ROADMAP_TOOLTIP";
    /// <summary>Arguments read by registered handlers: index.</summary>
    public const string HIDE_SKILL_MAP_EFFECT = "HIDE_SKILL_MAP_EFFECT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HIDE_WORLDMAP_TOOLTIP = "HIDE_WORLDMAP_TOOLTIP";
    /// <summary>Arguments read by registered handlers: hType, bTax, hTax, heavyTaxHouseCount, normalTaxHouseCount, isHeavyTaxHouse, hostileTaxRate, depositString, taxType, completion.</summary>
    public const string HOUSE_BUILD_INFO = "HOUSE_BUILD_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_BUY_SUCCESS = "HOUSE_BUY_SUCCESS";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_CANCEL_SELL_SUCCESS = "HOUSE_CANCEL_SELL_SUCCESS";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_DECO_UPDATED = "HOUSE_DECO_UPDATED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_INFO_UPDATED = "HOUSE_INFO_UPDATED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_INTERACTION_END = "HOUSE_INTERACTION_END";
    /// <summary>Arguments read by registered handlers: none OR structureType, viewType.</summary>
    public const string HOUSE_INTERACTION_START = "HOUSE_INTERACTION_START";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_PERMISSION_UPDATED = "HOUSE_PERMISSION_UPDATED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_REBUILD_TAX_INFO = "HOUSE_REBUILD_TAX_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_ROTATE_CONFIRM = "HOUSE_ROTATE_CONFIRM";
    /// <summary>Arguments read by registered handlers: houseName.</summary>
    public const string HOUSE_SALE_SUCCESS = "HOUSE_SALE_SUCCESS";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_SET_SELL_FAIL = "HOUSE_SET_SELL_FAIL";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSE_SET_SELL_SUCCESS = "HOUSE_SET_SELL_SUCCESS";
    /// <summary>Arguments read by registered handlers: structureType.</summary>
    public const string HOUSE_STEP_INFO_UPDATED = "HOUSE_STEP_INFO_UPDATED";
    /// <summary>Arguments read by registered handlers: dominionTaxRate, hostileTaxRate, taxString, dueTime, prepayTime, weeksWithoutPay, weeksPrepay, isAlreadyPaid, isHeavyTaxHouse, depositString, taxType, id.</summary>
    public const string HOUSE_TAX_INFO = "HOUSE_TAX_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSING_UCC_CLOSE = "HOUSING_UCC_CLOSE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSING_UCC_ITEM_SLOT_CLEAR = "HOUSING_UCC_ITEM_SLOT_CLEAR";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSING_UCC_ITEM_SLOT_SET = "HOUSING_UCC_ITEM_SLOT_SET";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSING_UCC_LEAVE = "HOUSING_UCC_LEAVE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string HOUSING_UCC_UPDATED = "HOUSING_UCC_UPDATED";
    /// <summary>Arguments read by registered handlers: none OR zoneId.</summary>
    public const string HPW_ZONE_STATE_CHANGE = "HPW_ZONE_STATE_CHANGE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INDUN_INITAL_ROUND_INFO = "INDUN_INITAL_ROUND_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INDUN_UPDATE_ROUND_INFO = "INDUN_UPDATE_ROUND_INFO";
    /// <summary>Arguments read by registered handlers: none OR obj.</summary>
    public const string INSTANT_GAME_END = "INSTANT_GAME_END";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_EQUIPINVEN_CHANGED = "INSTANT_GAME_EQUIPINVEN_CHANGED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_EQUIPMENT_CHANGED = "INSTANT_GAME_EQUIPMENT_CHANGED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_JOIN_APPLY = "INSTANT_GAME_JOIN_APPLY";
    /// <summary>Arguments read by registered handlers: failedApply OR none.</summary>
    public const string INSTANT_GAME_JOIN_CANCEL = "INSTANT_GAME_JOIN_CANCEL";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_PICK_BUFFS = "INSTANT_GAME_PICK_BUFFS";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_READY = "INSTANT_GAME_READY";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_RETIRE = "INSTANT_GAME_RETIRE";
    /// <summary>Arguments read by registered handlers: resultState, resultRound.</summary>
    public const string INSTANT_GAME_ROUND_RESULT = "INSTANT_GAME_ROUND_RESULT";
    /// <summary>Arguments read by registered handlers: slaveCount.</summary>
    public const string INSTANT_GAME_SCORE_SLAVE_COUNT = "INSTANT_GAME_SCORE_SLAVE_COUNT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_START = "INSTANT_GAME_START";
    /// <summary>Arguments read by registered handlers: show, name, relation, gauge, maxGauge.</summary>
    public const string INSTANT_GAME_TERRITORY_GAUGE = "INSTANT_GAME_TERRITORY_GAUGE";
    /// <summary>Arguments read by registered handlers: state.</summary>
    public const string INSTANT_GAME_VISIT_COUNT_RESET = "INSTANT_GAME_VISIT_COUNT_RESET";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INSTANT_GAME_WAIT = "INSTANT_GAME_WAIT";
    /// <summary>Arguments read by registered handlers: state.</summary>
    public const string INSTANT_GLOBAL_MATCH_STATUES_UPDATE = "INSTANT_GLOBAL_MATCH_STATUES_UPDATE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string INTERACTION_END = "INTERACTION_END";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string LEAVED_INSTANT_GAME_ZONE = "LEAVED_INSTANT_GAME_ZONE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string LEFT_LOADING = "LEFT_LOADING";
    /// <summary>Arguments read by registered handlers: _, stringId OR none.</summary>
    public const string LEVEL_CHANGED = "LEVEL_CHANGED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string MAP_EVENT_CHANGED = "MAP_EVENT_CHANGED";
    /// <summary>Arguments read by registered handlers: none OR status, info.</summary>
    public const string MINI_SCOREBOARD_CHANGED = "MINI_SCOREBOARD_CHANGED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string NPC_INTERACTION_END = "NPC_INTERACTION_END";
    /// <summary>Arguments read by registered handlers: value, addedValue, npcId.</summary>
    public const string NPC_INTERACTION_START = "NPC_INTERACTION_START";
    /// <summary>Arguments read by registered handlers: visible.</summary>
    public const string NUONS_ARROW_SHOW = "NUONS_ARROW_SHOW";
    /// <summary>Arguments read by registered handlers: data.</summary>
    public const string NUONS_ARROW_UPDATE = "NUONS_ARROW_UPDATE";
    /// <summary>Arguments read by registered handlers: infos.</summary>
    public const string RAID_RECRUIT_HUD = "RAID_RECRUIT_HUD";
    /// <summary>Arguments read by registered handlers: none OR refreshEntrance.</summary>
    public const string REFRESH_SQUAD_LIST = "REFRESH_SQUAD_LIST";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string REMOVE_BOSS_TELESCOPE_INFO = "REMOVE_BOSS_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string REMOVE_CARRYING_BACKPACK_SLAVE_INFO = "REMOVE_CARRYING_BACKPACK_SLAVE_INFO";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string REMOVE_FISH_SCHOOL_INFO = "REMOVE_FISH_SCHOOL_INFO";
    /// <summary>Arguments read by registered handlers: arg1, arg2.</summary>
    public const string REMOVE_GIVEN_QUEST_INFO = "REMOVE_GIVEN_QUEST_INFO";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string REMOVE_NOTIFY_QUEST_INFO = "REMOVE_NOTIFY_QUEST_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string REMOVE_PING = "REMOVE_PING";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string REMOVE_SHIP_TELESCOPE_INFO = "REMOVE_SHIP_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string REMOVE_TRANSFER_TELESCOPE_INFO = "REMOVE_TRANSFER_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string RENAME_PORTAL = "RENAME_PORTAL";
    /// <summary>Arguments read by registered handlers: boardType.</summary>
    public const string RESIDENT_BOARD_TYPE = "RESIDENT_BOARD_TYPE";
    /// <summary>Arguments read by registered handlers: infos, rownum, filter, searchword, refresh.</summary>
    public const string RESIDENT_HOUSING_TRADE_LIST = "RESIDENT_HOUSING_TRADE_LIST";
    /// <summary>Arguments read by registered handlers: total, start, refresh, members.</summary>
    public const string RESIDENT_MEMBER_LIST = "RESIDENT_MEMBER_LIST";
    /// <summary>Arguments read by registered handlers: info.</summary>
    public const string RESIDENT_TOWNHALL = "RESIDENT_TOWNHALL";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string RESIDENT_ZONE_STATE_CHANGE = "RESIDENT_ZONE_STATE_CHANGE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string SAVE_PORTAL = "SAVE_PORTAL";
    /// <summary>Arguments read by registered handlers: data.</summary>
    public const string SELECT_SQUAD_LIST = "SELECT_SQUAD_LIST";
    /// <summary>Arguments read by registered handlers: isSameZone.</summary>
    public const string SET_DEFAULT_EXPAND_RATIO = "SET_DEFAULT_EXPAND_RATIO";
    /// <summary>Arguments read by registered handlers: isShow, arg OR isShow, arg1.</summary>
    public const string SET_EFFECT_ICON_VISIBLE = "SET_EFFECT_ICON_VISIBLE";
    /// <summary>Arguments read by registered handlers: arg.</summary>
    public const string SET_PING_MODE = "SET_PING_MODE";
    /// <summary>Arguments read by registered handlers: pick.</summary>
    public const string SET_ROADMAP_PICKABLE = "SET_ROADMAP_PICKABLE";
    /// <summary>Arguments read by registered handlers: show, instanceType.</summary>
    public const string SHOW_BANNER = "SHOW_BANNER";
    /// <summary>Arguments read by registered handlers: tooltipInfo, tooltipCount.</summary>
    public const string SHOW_ROADMAP_TOOLTIP = "SHOW_ROADMAP_TOOLTIP";
    /// <summary>Arguments read by registered handlers: show.</summary>
    public const string SHOW_SQUAD_WINDOW = "SHOW_SQUAD_WINDOW";
    /// <summary>Arguments read by registered handlers: zoneId, x, y, z.</summary>
    public const string SHOW_WORLDMAP_LOCATION = "SHOW_WORLDMAP_LOCATION";
    /// <summary>Arguments read by registered handlers: tooltipInfo, tooltipCount.</summary>
    public const string SHOW_WORLDMAP_TOOLTIP = "SHOW_WORLDMAP_TOOLTIP";
    /// <summary>Arguments read by registered handlers: code.</summary>
    public const string SIM_DOODAD_MSG = "SIM_DOODAD_MSG";
    /// <summary>Arguments read by registered handlers: info.</summary>
    public const string SKILL_MAP_EFFECT = "SKILL_MAP_EFFECT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string START_HERO_ELECTION_PERIOD = "START_HERO_ELECTION_PERIOD";
    /// <summary>Arguments read by registered handlers: remainTime.</summary>
    public const string START_SENSITIVE_OPERATION = "START_SENSITIVE_OPERATION";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string SYNC_PORTAL = "SYNC_PORTAL";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string SYS_INDUN_STAT_UPDATED = "SYS_INDUN_STAT_UPDATED";
    /// <summary>Arguments read by registered handlers: curHp, maxHp.</summary>
    public const string TARGET_NPC_HEALTH_CHANGED_FOR_DEFENCE_INFO = "TARGET_NPC_HEALTH_CHANGED_FOR_DEFENCE_INFO";
    /// <summary>Arguments read by registered handlers: target, curHp, maxHp.</summary>
    public const string TARGET_NPC_HEALTH_CHANGED_FOR_VERSUS_FACTION = "TARGET_NPC_HEALTH_CHANGED_FOR_VERSUS_FACTION";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string TEAM_MEMBERS_CHANGED = "TEAM_MEMBERS_CHANGED";
    /// <summary>Arguments read by registered handlers: addPortal, skillTypeNumber, itemTypeNumber.</summary>
    public const string TOGGLE_PORTAL_DIALOG = "TOGGLE_PORTAL_DIALOG";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string TOGGLE_ROADMAP = "TOGGLE_ROADMAP";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string TOWER_DEF_INFO_UPDATE = "TOWER_DEF_INFO_UPDATE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UI_PERMISSION_UPDATE = "UI_PERMISSION_UPDATE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UI_RELOADED = "UI_RELOADED";
    /// <summary>Arguments read by registered handlers: unitId, unitType, curHp, maxHp.</summary>
    public const string UNIT_ENTERED_SIGHT = "UNIT_ENTERED_SIGHT";
    /// <summary>Arguments read by registered handlers: unitId, unitType.</summary>
    public const string UNIT_LEAVED_SIGHT = "UNIT_LEAVED_SIGHT";
    /// <summary>Arguments read by registered handlers: none OR unitId.</summary>
    public const string UNIT_NAME_CHANGED = "UNIT_NAME_CHANGED";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_BINDINGS = "UPDATE_BINDINGS";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_BOSS_TELESCOPE_AREA = "UPDATE_BOSS_TELESCOPE_AREA";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_BOSS_TELESCOPE_INFO = "UPDATE_BOSS_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_CARRYING_BACKPACK_SLAVE_INFO = "UPDATE_CARRYING_BACKPACK_SLAVE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_COMPLETED_QUEST_INFO = "UPDATE_COMPLETED_QUEST_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_CORPSE_INFO = "UPDATE_CORPSE_INFO";
    /// <summary>Arguments read by registered handlers: info.</summary>
    public const string UPDATE_DEFENCE_INFO = "UPDATE_DEFENCE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_DOMINION_INFO = "UPDATE_DOMINION_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_DOODAD_INFO = "UPDATE_DOODAD_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_FACTION_REZ_DISTRICT = "UPDATE_FACTION_REZ_DISTRICT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_FISH_SCHOOL_AREA = "UPDATE_FISH_SCHOOL_AREA";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_FISH_SCHOOL_INFO = "UPDATE_FISH_SCHOOL_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_GIVEN_QUEST_STATIC_INFO = "UPDATE_GIVEN_QUEST_STATIC_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_HERO_ELECTION_CONDITION = "UPDATE_HERO_ELECTION_CONDITION";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_HOUSING_INFO = "UPDATE_HOUSING_INFO";
    /// <summary>Arguments read by registered handlers: info.</summary>
    public const string UPDATE_INDUN_PLAYING_INFO_BROADCASTING = "UPDATE_INDUN_PLAYING_INFO_BROADCASTING";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_INSTANCE_VISIT_COUNT = "UPDATE_INSTANCE_VISIT_COUNT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_INSTANT_GAME_KILLSTREAK = "UPDATE_INSTANT_GAME_KILLSTREAK";
    /// <summary>Arguments read by registered handlers: count.</summary>
    public const string UPDATE_INSTANT_GAME_KILLSTREAK_COUNT = "UPDATE_INSTANT_GAME_KILLSTREAK_COUNT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_INSTANT_GAME_SCORES = "UPDATE_INSTANT_GAME_SCORES";
    /// <summary>Arguments read by registered handlers: toggle, statusBoardInfo.</summary>
    public const string UPDATE_INSTANT_GAME_SHIP_STATUS_BOARD = "UPDATE_INSTANT_GAME_SHIP_STATUS_BOARD";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_INSTANT_GAME_STATE = "UPDATE_INSTANT_GAME_STATE";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_INSTANT_GAME_TARGET_NPC_INFO = "UPDATE_INSTANT_GAME_TARGET_NPC_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_INSTANT_GAME_TIME = "UPDATE_INSTANT_GAME_TIME";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_MONITOR_NPC = "UPDATE_MONITOR_NPC";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_MY_SLAVE_POS_INFO = "UPDATE_MY_SLAVE_POS_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_NPC_INFO = "UPDATE_NPC_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_PING_INFO = "UPDATE_PING_INFO";
    /// <summary>Arguments read by registered handlers: file.</summary>
    public const string UPDATE_ROADMAP_ANCHOR = "UPDATE_ROADMAP_ANCHOR";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_ROUTE_MAP = "UPDATE_ROUTE_MAP";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_SHIP_TELESCOPE_INFO = "UPDATE_SHIP_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: offensePoint, outlawPoint.</summary>
    public const string UPDATE_SIEGE_SCORE = "UPDATE_SIEGE_SCORE";
    /// <summary>Arguments read by registered handlers: reload.</summary>
    public const string UPDATE_SLAVE_EQUIPMENT_SLOT = "UPDATE_SLAVE_EQUIPMENT_SLOT";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_SQUAD = "UPDATE_SQUAD";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_TELESCOPE_AREA = "UPDATE_TELESCOPE_AREA";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_TRANSFER_TELESCOPE_AREA = "UPDATE_TRANSFER_TELESCOPE_AREA";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_TRANSFER_TELESCOPE_INFO = "UPDATE_TRANSFER_TELESCOPE_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_ZONE_INFO = "UPDATE_ZONE_INFO";
    /// <summary>Arguments read by registered handlers: level, id.</summary>
    public const string UPDATE_ZONE_LEVEL_INFO = "UPDATE_ZONE_LEVEL_INFO";
    /// <summary>Arguments read by registered handlers: none.</summary>
    public const string UPDATE_ZONE_PERMISSION = "UPDATE_ZONE_PERMISSION";
    /// <summary>Arguments read by registered handlers: states.</summary>
    public const string ZONE_SCORE_CONTENT_STATE = "ZONE_SCORE_CONTENT_STATE";
    /// <summary>Arguments read by registered handlers: kind, info.</summary>
    public const string ZONE_SCORE_UPDATED = "ZONE_SCORE_UPDATED";
}

