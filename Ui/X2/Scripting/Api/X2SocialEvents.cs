#nullable enable

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Names and Lua argument contracts for events consumed by social UI packages.</summary>
public static class X2SocialEvents
{
    /// <summary>Arguments read: <c>chatType, relation, speakerName, message, info</c>.</summary>
    public const string ChatMessage = "CHAT_MESSAGE";
    /// <summary>Arguments read: <c>message, speakerName, speakerId, self, tailType, showTime, fadeTime, currentBubbleType, qtype, forceFinished</c>.</summary>
    public const string ChatMessageQuest = "CHAT_MSG_QUEST";
    /// <summary>Arguments read: <c>message, speakerName, speakerId, self, tailType, showTime, fadeTime, hasNext, qtype, forceFinished</c>.</summary>
    public const string ChatMessageDoodad = "CHAT_MSG_DOODAD";
    /// <summary>Arguments read: <c>message</c>.</summary>
    public const string ChatEmotion = "CHAT_EMOTION";
    /// <summary>Arguments read: <c>popupText, jointable</c> from the handler's varargs array.</summary>
    public const string TeamJointChat = "TEAM_JOINT_CHAT";
    /// <summary>Arguments read: <c>show, channel, name, message, isMyMessage</c>.</summary>
    public const string MegaphoneMessage = "MEGAPHONE_MESSAGE";
    /// <summary>Arguments read: <c>channel</c>.</summary>
    public const string RequireItemToChat = "REQUIRE_ITEM_TO_CHAT";
    /// <summary>Arguments read: none.</summary>
    public const string ToggleMegaphoneChat = "TOGGLE_MEGAPHONE_CHAT";
    /// <summary>Arguments read: the widget event name only; no payload values are read.</summary>
    public const string ImeStatusChanged = "IME_STATUS_CHANGED";
    /// <summary>Arguments read: <c>channelId, targetName</c>.</summary>
    public const string OneAndOneChatStart = "ONE_AND_ONE_CHAT_START";
    /// <summary>Arguments read: <c>channelId</c>.</summary>
    public const string OneAndOneChatEnd = "ONE_AND_ONE_CHAT_END";
    /// <summary>Arguments read: <c>channelId, speakerName, message, isSpeakerGm</c>.</summary>
    public const string OneAndOneChatAddMessage = "ONE_AND_ONE_CHAT_ADD_MESSAGE";

    /// <summary>Arguments read: <c>read, mailListKind</c>.</summary>
    public const string MailInboxUpdate = "MAIL_INBOX_UPDATE";
    /// <summary>Arguments read: <c>read, mailListKind</c>.</summary>
    public const string MailSentboxUpdate = "MAIL_SENTBOX_UPDATE";
    /// <summary>Arguments read: none.</summary>
    public const string MailReturned = "MAIL_RETURNED";
    /// <summary>Arguments read: none.</summary>
    public const string MailSentSuccess = "MAIL_SENT_SUCCESS";
    /// <summary>Arguments read: <c>index</c>.</summary>
    public const string MailInboxItemTaken = "MAIL_INBOX_ITEM_TAKEN";
    /// <summary>Arguments read: none.</summary>
    public const string MailInboxMoneyTaken = "MAIL_INBOX_MONEY_TAKEN";
    /// <summary>Arguments read: <c>mailId</c>.</summary>
    public const string MailInboxAttachmentTakenAll = "MAIL_INBOX_ATTACHMENT_TAKEN_ALL";
    /// <summary>Arguments read: none.</summary>
    public const string MailInboxTaxPaid = "MAIL_INBOX_TAX_PAID";
    /// <summary>Arguments read: <c>index</c>.</summary>
    public const string MailWriteItemUpdate = "MAIL_WRITE_ITEM_UPDATE";
    /// <summary>Arguments read: <c>read</c>.</summary>
    public const string GoodsMailInboxUpdate = "GOODS_MAIL_INBOX_UPDATE";
    /// <summary>Arguments read: <c>index</c>.</summary>
    public const string GoodsMailInboxItemTaken = "GOODS_MAIL_INBOX_ITEM_TAKEN";

    /// <summary>Arguments read: <c>updateType, dataField</c>.</summary>
    public const string FriendListUpdate = "FRIENDLIST_UPDATE";
    /// <summary>Arguments read: <c>totalCount, memberInfos</c>.</summary>
    public const string FriendListInfo = "FRIENDLIST_INFO";
    /// <summary>Arguments read: <c>updateType</c>.</summary>
    public const string WaitFriendListUpdate = "WAIT_FRIENDLIST_UPDATE";
    /// <summary>Arguments read: none.</summary>
    public const string WaitFriendAddAlarm = "WAIT_FRIEND_ADD_ALARM";
    /// <summary>Arguments read: none.</summary>
    public const string BlockedUserUpdate = "BLOCKED_USER_UPDATE";
    /// <summary>Arguments read: none.</summary>
    public const string FamilyInfoRefresh = "FAMILY_INFO_REFRESH";

    /// <summary>Arguments read: <c>isParty, jointOrder, stringId, memberIndex</c>.</summary>
    public const string TeamMemberDisconnected = "TEAM_MEMBER_DISCONNECTED";
    /// <summary>Arguments read: <c>oldStringId, stringId</c>.</summary>
    public const string TeamMemberUnitIdChanged = "TEAM_MEMBER_UNIT_ID_CHANGED";
    /// <summary>Arguments read: <c>unitId</c> and <c>visible</c> (the second payload value is not read).</summary>
    public const string SetOverheadMark = "SET_OVERHEAD_MARK";
    /// <summary>Arguments read: <c>reason, value</c>; <c>value</c> fields include isParty, jointOrder, memberIndex, newMemberIndex, name and teamRoleType.</summary>
    public const string TeamMembersChanged = "TEAM_MEMBERS_CHANGED";
    /// <summary>Arguments read: <c>party, visible</c>.</summary>
    public const string ToggleRaidFrameParty = "TOGGLE_RAID_FRAME_PARTY";
    /// <summary>Arguments read: <c>jointOrder, memberIndex, role</c>.</summary>
    public const string TeamRoleChanged = "TEAM_ROLE_CHANGED";
    /// <summary>Arguments read: none.</summary>
    public const string LootingRuleMasterChanged = "LOOTING_RULE_MASTER_CHANGED";
    /// <summary>Arguments read: none.</summary>
    public const string LootingRuleMethodChanged = "LOOTING_RULE_METHOD_CHANGED";
    /// <summary>Arguments read: <c>simple</c>.</summary>
    public const string RaidFrameSimpleView = "RAID_FRAME_SIMPLE_VIEW";
    /// <summary>Arguments read: <c>arg1, arg2</c>.</summary>
    public const string UnitCombatStateChanged = "UNIT_COMBAT_STATE_CHANGED";
    /// <summary>Arguments read: none.</summary>
    public const string TeamJointBroken = "TEAM_JOINT_BROKEN";
    /// <summary>Arguments read: none.</summary>
    public const string TeamJointed = "TEAM_JOINTED";
    /// <summary>Arguments read: <c>name, count</c>.</summary>
    public const string TeamJointRequest = "TEAM_JOINT_REQUEST";
    /// <summary>Arguments read: <c>name, count, leader</c>.</summary>
    public const string TeamJointResponse = "TEAM_JOINT_RESPONSE";
    /// <summary>Arguments read: <c>enable</c>.</summary>
    public const string EnableTeamAreaInvitation = "ENABLE_TEAM_AREA_INVITATION";
    /// <summary>Arguments read: <c>requester, enable</c>.</summary>
    public const string TeamJointBreak = "TEAM_JOINT_BREAK";
    /// <summary>Arguments read: <c>show</c>.</summary>
    public const string TogglePartyFrame = "TOGGLE_PARTY_FRAME";
    /// <summary>Arguments read: <c>show</c>.</summary>
    public const string ToggleRaidFrame = "TOGGLE_RAID_FRAME";
    /// <summary>Arguments read: none.</summary>
    public const string ConvertToRaidTeam = "CONVERT_TO_RAID_TEAM";
    /// <summary>Arguments read: none.</summary>
    public const string TeamSummonSuggest = "TEAM_SUMMON_SUGGEST";
    /// <summary>Arguments read: none.</summary>
    public const string UpdateSquad = "UPDATE_SQUAD";
    /// <summary>Arguments read: <c>data</c>.</summary>
    public const string RaidRecruitHud = "RAID_RECRUIT_HUD";
    /// <summary>Arguments read: <c>data</c>.</summary>
    public const string RaidRecruitDetail = "RAID_RECRUIT_DETAIL";
    /// <summary>Arguments read: <c>data</c>.</summary>
    public const string RaidApplicantList = "RAID_APPLICANT_LIST";
    /// <summary>Arguments read: <c>data</c>.</summary>
    public const string SelectSquadList = "SELECT_SQUAD_LIST";
    /// <summary>Arguments read: <c>refreshEntrance</c> in the entrance window; the list view reads no payload.</summary>
    public const string RefreshSquadList = "REFRESH_SQUAD_LIST";
    /// <summary>Arguments read: <c>show</c>.</summary>
    public const string ShowSquadWindow = "SHOW_SQUAD_WINDOW";

    /// <summary>Arguments read: none.</summary>
    public const string ExpeditionManagementPolicyChanged = "EXPEDITION_MANAGEMENT_POLICY_CHANGED";
    /// <summary>Arguments read: none.</summary>
    public const string ExpeditionManagementUpdated = "EXPEDITION_MANAGEMENT_UPDATED";
    /// <summary>Arguments read: <c>totalCount, startIndex, memberInfos</c>.</summary>
    public const string ExpeditionManagementMembersInfo = "EXPEDITION_MANAGEMENT_MEMBERS_INFO";
    /// <summary>Arguments read: none.</summary>
    public const string ExpeditionManagementRoleChanged = "EXPEDITION_MANAGEMENT_ROLE_CHANGED";
    /// <summary>Arguments read: none.</summary>
    public const string ExpeditionManagementMemberNameChanged = "EXPEDITION_MANAGEMENT_MEMBER_NAME_CHANGED";
    /// <summary>Arguments read: <c>expedition, buff, before, after</c>.</summary>
    public const string ExpeditionBuffChange = "EXPEDITION_BUFF_CHANGE";
    /// <summary>Arguments read: none.</summary>
    public const string ExpeditionInfoClear = "EXPEDITION_INFO_CLEAR";
    /// <summary>Arguments read: <c>_, _2, taxString, dueTime, _3, weeksWithoutPay, weeksPrepay, isAlreadyPaid, isHeavyTaxHouse, _4, taxType, id</c>.</summary>
    public const string HouseTaxInfo = "HOUSE_TAX_INFO";
    /// <summary>Arguments read: <c>infos</c>.</summary>
    public const string ExpeditionManagementApplicants = "EXPEDITION_MANAGEMENT_APPLICANTS";
    /// <summary>Arguments read: <c>charId</c>.</summary>
    public const string ExpeditionManagementApplicantAccept = "EXPEDITION_MANAGEMENT_APPLICANT_ACCEPT";
    /// <summary>Arguments read: <c>charId</c>.</summary>
    public const string ExpeditionManagementApplicantReject = "EXPEDITION_MANAGEMENT_APPLICANT_REJECT";
    /// <summary>Arguments read: <c>total, perPageItemCount, infos</c>.</summary>
    public const string ExpeditionManagementRecruitments = "EXPEDITION_MANAGEMENT_RECRUITMENTS";
    /// <summary>Arguments read: <c>info</c>.</summary>
    public const string ExpeditionManagementRecruitmentAdd = "EXPEDITION_MANAGEMENT_RECRUITMENT_ADD";
    /// <summary>Arguments read: <c>expeditionId</c>.</summary>
    public const string ExpeditionManagementRecruitmentDelete = "EXPEDITION_MANAGEMENT_RECRUITMENT_DEL";
    /// <summary>Arguments read: <c>expeditionId</c>.</summary>
    public const string ExpeditionManagementApplicantAdd = "EXPEDITION_MANAGEMENT_APPLICANT_ADD";
    /// <summary>Arguments read: <c>expeditionId</c>.</summary>
    public const string ExpeditionManagementApplicantDelete = "EXPEDITION_MANAGEMENT_APPLICANT_DEL";
    /// <summary>Arguments read: <c>isExpedition, oldName, newName</c>.</summary>
    public const string FactionRenamed = "FACTION_RENAMED";
    /// <summary>Arguments read: <c>addPortal, interactionDoodad, expeditionOwner</c>.</summary>
    public const string OpenExpeditionPortalList = "OPEN_EXPEDITION_PORTAL_LIST";
    /// <summary>Arguments read: none.</summary>
    public const string UpdateExpeditionPortal = "UPDATE_EXPEDITION_PORTAL";
    /// <summary>Arguments read: none.</summary>
    public const string ExpeditionSummonSuggest = "EXPEDITION_SUMMON_SUGGEST";
    /// <summary>Arguments read: <c>_, state, declarer, defendant, winner</c>.</summary>
    public const string ExpeditionWarState = "EXPEDITION_WAR_STATE";
    /// <summary>Arguments read: <c>toggle</c>.</summary>
    public const string ExpeditionWarKillScore = "EXPEDITION_WAR_KILL_SCORE";
    /// <summary>Arguments read: <c>errorMsg, param</c>.</summary>
    public const string ExpeditionWarDeclarationFailed = "EXPEDITION_WAR_DECLARATION_FAILED";
    /// <summary>Arguments read: none.</summary>
    public const string ExpeditionWarSetProtectDate = "EXPEDITION_WAR_SET_PROTECT_DATE";
    /// <summary>Arguments read: <c>type</c>.</summary>
    public const string ExpeditionHistory = "EXPEDITION_HISTORY";
    /// <summary>Arguments read: <c>data</c>.</summary>
    public const string RaidRecruitList = "RAID_RECRUIT_LIST";
    /// <summary>Arguments read: <c>infos</c>.</summary>
    public const string ExpeditionImmigrationList = "EXPEDITION_IMMIGRATION_LIST";
    /// <summary>Arguments read: <c>count</c>.</summary>
    public const string UpdateExpeditionTodayAssignmentResetCount = "UPDATE_EXPEDITION_TODAY_ASSIGNMENT_RESET_COUNT";
    /// <summary>Arguments read: <c>target, curHp, maxHp</c>.</summary>
    public const string GuardtowerHealthChanged = "GUARDTOWER_HEALTH_CHANGED";
    /// <summary>Arguments read: <c>unitId, unitType, curHp, maxHp</c>.</summary>
    public const string UnitEnteredSight = "UNIT_ENTERED_SIGHT";
    /// <summary>Arguments read: <c>unitId, unitType</c>.</summary>
    public const string UnitLeavedSight = "UNIT_LEAVED_SIGHT";
    /// <summary>Arguments read: <c>zoneGroupType</c>.</summary>
    public const string DominionSiegeParticipantCountChanged = "DOMINION_SIEGE_PARTICIPANT_COUNT_CHANGED";
    /// <summary>Arguments read: <c>browser</c>.</summary>
    public const string WebBrowserEscEvent = "WEB_BROWSER_ESC_EVENT";

    /// <summary>Arguments read: <c>name, factionName</c>.</summary>
    public const string FactionRelationAccepted = "FACTION_RELATION_ACCEPTED";
    /// <summary>Arguments read: <c>name</c>.</summary>
    public const string FactionRelationDenied = "FACTION_RELATION_DENIED";
    /// <summary>Arguments read: <c>name, factionName</c>.</summary>
    public const string FactionRelationRequested = "FACTION_RELATION_REQUESTED";
    /// <summary>Arguments read: none.</summary>
    public const string FactionRelationChanged = "FACTION_RELATION_CHANGED";
    /// <summary>Arguments read: none.</summary>
    public const string FactionRelationHistory = "FACTION_RELATION_HISTORY";
    /// <summary>Arguments read: none.</summary>
    public const string FactionRelationCount = "FACTION_RELATION_COUNT";
    /// <summary>Arguments read: <c>param</c>.</summary>
    public const string NationRelrationVote = "NATION_RELRATION_VOTE";
    /// <summary>Arguments read: <c>nationInfo</c>.</summary>
    public const string NationInfo = "NATION_INFO";
    /// <summary>Arguments read: <c>nationNoticeInfo</c>.</summary>
    public const string NationInfoSet = "NATION_INFO_SET";
    /// <summary>Arguments read: none.</summary>
    public const string NationMemberRefresh = "NATION_MEMBER_REFRESH";
    /// <summary>Arguments read: <c>zoneGroupType, force</c>.</summary>
    public const string NationDominion = "NATION_DOMINION";
    /// <summary>Arguments read: <c>info</c>.</summary>
    public const string SiegeRaidTeamInfo = "SIEGE_RAID_TEAM_INFO";
    /// <summary>Arguments read: <c>teamInfos</c>.</summary>
    public const string AllSiegeRaidTeamInfos = "ALL_SIEGE_RAID_TEAM_INFOS";
    /// <summary>Arguments read: <c>siegeInfo</c>.</summary>
    public const string NextSiegeInfo = "NEXT_SIEGE_INFO";
    /// <summary>Arguments read: <c>info</c>.</summary>
    public const string DominionSiegePeriodChanged = "DOMINION_SIEGE_PERIOD_CHANGED";
    /// <summary>Arguments read: <c>secondHalf</c>.</summary>
    public const string DominionSiegeUpdateTimer = "DOMINION_SIEGE_UPDATE_TIMER";
    /// <summary>Arguments read: <c>key, name, factionName</c>.</summary>
    public const string DominionGuardTowerStateNotice = "DOMINION_GUARD_TOWER_STATE_NOTICE";
    /// <summary>Arguments read: <c>action, zoneGroupName, expeditionName</c>.</summary>
    public const string Dominion = "DOMINION";
    /// <summary>Arguments read: <c>info</c>.</summary>
    public const string FactionCompetitionInfo = "FACTION_COMPETITION_INFO";
    /// <summary>Arguments read: <c>info</c>.</summary>
    public const string FactionCompetitionUpdatePoint = "FACTION_COMPETITION_UPDATE_POINT";
    /// <summary>Arguments read: none.</summary>
    public const string FactionCompetitionResult = "FACTION_COMPETITION_RESULT";

    /// <summary>Arguments read: <c>rankType, divisionId</c>.</summary>
    public const string RankSnapshots = "RANK_SNAPSHOTS";
    /// <summary>Arguments read: <c>rankType, divisionId</c>.</summary>
    public const string RankRewardSnapshots = "RANK_REWARD_SNAPSHOTS";
    /// <summary>Arguments read: none.</summary>
    public const string RankPersonalData = "RANK_PERSONAL_DATA";
    /// <summary>Arguments read: <c>charID</c>.</summary>
    public const string RankRankerAppearance = "RANK_RANKER_APPEARANCE";

    /// <summary>Arguments read: <c>updateInfo</c>.</summary>
    public const string UpdateContentRosterWindow = "UPDATE_CONTENT_ROSTER_WINDOW";
    /// <summary>Arguments read: <c>rosterId</c>.</summary>
    public const string UpdateRosterMemberInfo = "UPDATE_ROSTER_MEMBER_INFO";

    /// <summary>Arguments read: none.</summary>
    public const string EnteredWorld = "ENTERED_WORLD";
    /// <summary>Arguments read: none.</summary>
    public const string LeftLoading = "LEFT_LOADING";
    /// <summary>Arguments read: none.</summary>
    public const string InteractionEnd = "INTERACTION_END";
    /// <summary>Arguments read: none.</summary>
    public const string NpcInteractionEnd = "NPC_INTERACTION_END";
    /// <summary>Arguments read: none.</summary>
    public const string UiPermissionUpdate = "UI_PERMISSION_UPDATE";
    /// <summary>Arguments read: none.</summary>
    public const string BagUpdate = "BAG_UPDATE";
    /// <summary>Arguments read: none.</summary>
    public const string PlayerLivingPoint = "PLAYER_LIVING_POINT";
    /// <summary>Arguments read: none.</summary>
    public const string UpdateTodayAssignment = "UPDATE_TODAY_ASSIGNMENT";
    /// <summary>Arguments read: <c>_, stringId</c>.</summary>
    public const string LevelChanged = "LEVEL_CHANGED";
    /// <summary>Arguments read: <c>qType, status</c>.</summary>
    public const string QuestContextUpdated = "QUEST_CONTEXT_UPDATED";
    /// <summary>Arguments read: none.</summary>
    public const string AddedItem = "ADDED_ITEM";
    /// <summary>Arguments read: none.</summary>
    public const string RemovedItem = "REMOVED_ITEM";
}
