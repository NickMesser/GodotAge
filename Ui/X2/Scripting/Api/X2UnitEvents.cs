#nullable enable

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>
/// Event names consumed by the unit-frame, skill, ability, action-bar, combat-resource,
/// and hero UI packages.  Arguments are in the order delivered after the Lua event name.
/// </summary>
public static class X2UnitEvents
{
    /// <summary>No arguments. The player's equipped ability selection changed.</summary>
    public const string AbilityChanged = "ABILITY_CHANGED";
    /// <summary><c>stringId</c>: unit id whose ability experience changed.</summary>
    public const string AbilityExpChanged = "ABILITY_EXP_CHANGED";
    /// <summary>No arguments. The active ability-set changed.</summary>
    public const string AbilitySetChanged = "ABILITY_SET_CHANGED";
    /// <summary>No arguments. The number of usable ability-set slots changed.</summary>
    public const string AbilitySetUsableSlotCountChanged = "ABILITY_SET_USABLE_SLOT_COUNT_CHANGED";
    /// <summary><c>actabilityId</c>: vocation/actability numeric id whose expert state changed.</summary>
    public const string ActabilityExpertChanged = "ACTABILITY_EXPERT_CHANGED";
    /// <summary><c>actabilityId</c>: vocation/actability numeric id whose expert grade changed.</summary>
    public const string ActabilityExpertGradeChanged = "ACTABILITY_EXPERT_GRADE_CHANGED";
    /// <summary>No arguments. The actability expert-slot capacity changed.</summary>
    public const string ActabilityExpertExpanded = "ACTABILITY_EXPERT_EXPANDED";
    /// <summary>No arguments. Actability modifiers changed.</summary>
    public const string ActabilityModifierUpdate = "ACTABILITY_MODIFIER_UPDATE";
    /// <summary>No arguments. Refresh all actability rows.</summary>
    public const string ActabilityRefreshAll = "ACTABILITY_REFRESH_ALL";
    /// <summary><c>page</c>: one-based action-bar page.</summary>
    public const string ActionBarPageChanged = "ACTION_BAR_PAGE_CHANGED";
    /// <summary><c>slotIndex</c>: action-bar slot that was automatically registered.</summary>
    public const string ActionBarAutoRegistered = "ACTION_BAR_AUTO_REGISTERED";
    /// <summary>No arguments. An item was added; the hero daily-assignment view refreshes.</summary>
    public const string AddedItem = "ADDED_ITEM";
    /// <summary>No arguments. The actability decoration count changed.</summary>
    public const string ChangeActabilityDecoNum = "CHANGE_ACTABILITY_DECO_NUM";
    /// <summary>No arguments. The player's language changed.</summary>
    public const string ChangeMyLanguage = "CHANGE_MY_LANGUAGE";
    /// <summary><c>optionType</c>: option id; <c>infoTable</c>: Lua table whose <c>display</c> field is read.</summary>
    public const string ChangeOption = "CHANGE_OPTION";
    /// <summary><c>mateType</c>: pet mate-type id to dismiss.</summary>
    public const string DismissPet = "DISMISS_PET";
    /// <summary>No arguments. The player stopped diving.</summary>
    public const string DiveEnd = "DIVE_END";
    /// <summary>No arguments. The player began diving.</summary>
    public const string DiveStart = "DIVE_START";
    /// <summary><c>unitId</c>: target unit id whose dominion guard tooltip changed.</summary>
    public const string DominionGuardTowerUpdateTooltip = "DOMINION_GUARD_TOWER_UPDATE_TOOLTIP";
    /// <summary>No arguments. A duel ended.</summary>
    public const string EndedDuel = "ENDED_DUEL";
    /// <summary>No arguments. Loading began.</summary>
    public const string EnteredLoading = "ENTERED_LOADING";
    /// <summary>No arguments. The player entered an instant-game zone.</summary>
    public const string EnteredInstantGameZone = "ENTERED_INSTANT_GAME_ZONE";
    /// <summary>The player entered the world. Unit-frame player handler additionally reads <c>firstEnteredWorld</c> (boolean).</summary>
    public const string EnteredWorld = "ENTERED_WORLD";
    /// <summary><c>stringId</c>: unit id whose normal experience changed.</summary>
    public const string ExpChanged = "EXP_CHANGED";
    /// <summary><c>show</c>: whether to show the hero-election window.</summary>
    public const string HeroElection = "HERO_ELECTION";
    /// <summary><c>unitId</c>: unit id; <c>on</c>: force-attack boolean.</summary>
    public const string ForceAttackChanged = "FORCE_ATTACK_CHANGED";
    /// <summary>No arguments. Heir level changed.</summary>
    public const string HeirLevelUp = "HEIR_LEVEL_UP";
    /// <summary>No arguments. Heir skill data changed.</summary>
    public const string HeirSkillUpdate = "HEIR_SKILL_UPDATE";
    /// <summary><c>factionId</c>: faction whose aggregate hero score changed.</summary>
    public const string HeroAllScoreUpdated = "HERO_ALL_SCORE_UPDATED";
    /// <summary><c>factionId</c>: faction whose hero ranking data arrived.</summary>
    public const string HeroRankDataRetrieved = "HERO_RANK_DATA_RETRIEVED";
    /// <summary>No arguments. Hero season data changed.</summary>
    public const string HeroSeasonUpdated = "HERO_SEASON_UPDATED";
    /// <summary>No arguments. An instant game ended.</summary>
    public const string InstantGameEnd = "INSTANT_GAME_END";
    /// <summary>No arguments. The player retired from an instant game.</summary>
    public const string InstantGameRetire = "INSTANT_GAME_RETIRE";
    /// <summary>No arguments. An NPC interaction ended; closes the hero-election window.</summary>
    public const string InteractionEnd = "INTERACTION_END";
    /// <summary>No arguments. The player left an instant-game zone.</summary>
    public const string LeavedInstantGameZone = "LEAVED_INSTANT_GAME_ZONE";
    /// <summary>No arguments. Loading completed or was left.</summary>
    public const string LeftLoading = "LEFT_LOADING";
    /// <summary>First argument is ignored; <c>stringId</c> is the unit id whose level changed.</summary>
    public const string LevelChanged = "LEVEL_CHANGED";
    /// <summary>No arguments. The looting master changed.</summary>
    public const string LootingRuleMasterChanged = "LOOTING_RULE_MASTER_CHANGED";
    /// <summary>No arguments. The looting method changed.</summary>
    public const string LootingRuleMethodChanged = "LOOTING_RULE_METHOD_CHANGED";
    /// <summary>No arguments. Mail inbox contents changed.</summary>
    public const string MailInboxUpdate = "MAIL_INBOX_UPDATE";
    /// <summary><c>point</c>: number of newly available skill points (the notifier defaults a missing value to one).</summary>
    public const string NewSkillPoint = "NEW_SKILL_POINT";
    /// <summary>No arguments. The player ability level changed.</summary>
    public const string PlayerAbilityLevelChanged = "PLAYER_ABILITY_LEVEL_CHANGED";
    /// <summary>No arguments. The player's visual race changed.</summary>
    public const string PlayerVisualRace = "PLAYER_VISUAL_RACE";
    /// <summary><c>qType</c>: quest type; <c>status</c>: updated quest status.</summary>
    public const string QuestContextUpdated = "QUEST_CONTEXT_UPDATED";
    /// <summary><c>resetBar</c>: boolean; <c>groupType</c>, <c>resourceType</c>, <c>point</c>: combat-resource ids/value.</summary>
    public const string RefreshCombatResource = "REFRESH_COMBAT_RESOURCE";
    /// <summary><c>resourceType</c>: resource id; <c>nowTime</c>: current time; <c>show</c>: display boolean.</summary>
    public const string RefreshCombatResourceUpdateTime = "REFRESH_COMBAT_RESOURCE_UPDATE_TIME";
    /// <summary>No arguments. An item was removed; the hero daily-assignment view refreshes.</summary>
    public const string RemovedItem = "REMOVED_ITEM";
    /// <summary><c>unitId</c>: marked unit; <c>index</c>: marker index; <c>visible</c>: marker visibility.</summary>
    public const string SetOverheadMark = "SET_OVERHEAD_MARK";
    /// <summary>No arguments. Open the current unit's hidden-buff window.</summary>
    public const string ShowHiddenBuff = "SHOW_HIDDEN_BUFF";
    /// <summary><c>statusBuffType</c>: alert category; <c>buffId</c>: buff id; <c>remainTime</c>: remaining time in milliseconds; <c>name</c>: display name.</summary>
    public const string SkillAlertAdd = "SKILL_ALERT_ADD";
    /// <summary><c>statusBuffType</c>: alert category to remove.</summary>
    public const string SkillAlertRemove = "SKILL_ALERT_REMOVE";
    /// <summary>No arguments. A skill was learned.</summary>
    public const string SkillLearned = "SKILL_LEARNED";
    /// <summary><c>skillType</c>: upgraded skill id read by the skill notification handler.</summary>
    public const string SkillUpgraded = "SKILL_UPGRADED";
    /// <summary>No arguments. Skills were reset.</summary>
    public const string SkillsReset = "SKILLS_RESET";
    /// <summary><c>recvAbility</c>: received special-ability identifier or data value.</summary>
    public const string SpecialAbilityLearned = "SPECIAL_ABILITY_LEARNED";
    /// <summary><c>spellName</c>, <c>castingTime</c>, <c>caster</c>, <c>castingUseable</c>. Casting bars use all four.</summary>
    public const string SpellcastStart = "SPELLCAST_START";
    /// <summary><c>caster</c>: casting unit token or id.</summary>
    public const string SpellcastStop = "SPELLCAST_STOP";
    /// <summary><c>caster</c>: casting unit token or id.</summary>
    public const string SpellcastSucceeded = "SPELLCAST_SUCCEEDED";
    /// <summary>No arguments. A duel started.</summary>
    public const string StartedDuel = "STARTED_DUEL";
    /// <summary><c>mateType</c>: pet mate-type id whose unit was spawned.</summary>
    public const string SpawnPet = "SPAWN_PET";
    /// <summary><c>targetType</c>: unit token assigned to the spawned slave frame.</summary>
    public const string SpawnSlave = "SPAWN_SLAVE";
    /// <summary><c>stringId</c>: selected target id; <c>targetType</c>: target kind string.</summary>
    public const string TargetChanged = "TARGET_CHANGED";
    /// <summary><c>stringId</c>: target-of-target id; <c>targetType</c>: target kind string.</summary>
    public const string TargetToTargetChanged = "TARGET_TO_TARGET_CHANGED";
    /// <summary>No arguments. A team joint was broken.</summary>
    public const string TeamJointBroken = "TEAM_JOINT_BROKEN";
    /// <summary><c>isJointable</c>: whether the current target can be joined.</summary>
    public const string TeamJointTarget = "TEAM_JOINT_TARGET";
    /// <summary>No arguments. A team joint was made.</summary>
    public const string TeamJointed = "TEAM_JOINTED";
    /// <summary><c>isParty</c>: boolean; <c>jointOrder</c>: number; <c>stringId</c>: member unit id; <c>memberIndex</c>: number.</summary>
    public const string TeamMemberDisconnected = "TEAM_MEMBER_DISCONNECTED";
    /// <summary><c>oldStringId</c>: former member unit id; <c>stringId</c>: replacement id.</summary>
    public const string TeamMemberUnitIdChanged = "TEAM_MEMBER_UNIT_ID_CHANGED";
    /// <summary><c>reason</c>: change reason string; <c>value</c>: reason-specific value.</summary>
    public const string TeamMembersChanged = "TEAM_MEMBERS_CHANGED";
    /// <summary>No arguments. The team was converted to a raid and the party frame is rebuilt.</summary>
    public const string ConvertToRaidTeam = "CONVERT_TO_RAID_TEAM";
    /// <summary><c>show</c>: whether the raid party frame is visible.</summary>
    public const string TogglePartyFrame = "TOGGLE_PARTY_FRAME";
    /// <summary><c>groupType</c>: combat-resource group id.</summary>
    public const string TransformCombatResource = "TRANSFORM_COMBAT_RESOURCE";
    /// <summary>No arguments. UI permission data changed.</summary>
    public const string UiPermissionUpdate = "UI_PERMISSION_UPDATE";
    /// <summary><c>inCombat</c>: boolean; <c>unitId</c>: unit whose combat state changed.</summary>
    public const string UnitCombatStateChanged = "UNIT_COMBAT_STATE_CHANGED";
    /// <summary><c>stringId</c>: dead unit id. The next two arguments are ignored by the player casting bar.</summary>
    public const string UnitDead = "UNIT_DEAD";
    /// <summary>No arguments. Equipped items changed.</summary>
    public const string UnitEquipmentChanged = "UNIT_EQUIPMENT_CHANGED";
    /// <summary><c>unitId</c>: unit whose name changed.</summary>
    public const string UnitNameChanged = "UNIT_NAME_CHANGED";
    /// <summary><c>unitId</c>: unit whose ability tooltip data changed.</summary>
    public const string UnitframeAbilityUpdate = "UNITFRAME_ABILITY_UPDATE";
    /// <summary><c>unitId</c>: unit whose housing tooltip data changed.</summary>
    public const string UpdateHousingTooltip = "UPDATE_HOUSING_TOOLTIP";
    /// <summary>No arguments. Key bindings changed.</summary>
    public const string UpdateBindings = "UPDATE_BINDINGS";
    /// <summary>No arguments. Rebuild shortcut-skill/action-bar slots.</summary>
    public const string UpdateShortcutSkills = "UPDATE_SHORTCUT_SKILLS";
    /// <summary>No arguments. The active skill category changed.</summary>
    public const string UpdateSkillActiveType = "UPDATE_SKILL_ACTIVE_TYPE";
    /// <summary>No arguments. Refresh spendable skill points.</summary>
    public const string UpdateSkillPoint = "UPDATE_SKILL_POINT";
    /// <summary>No arguments. Refresh today's hero assignment.</summary>
    public const string UpdateTodayAssignment = "UPDATE_TODAY_ASSIGNMENT";
    /// <summary><c>stringId</c>: watched-target unit id, or nil to hide the watch-target frame.</summary>
    public const string WatchTargetChanged = "WATCH_TARGET_CHANGED";
}
