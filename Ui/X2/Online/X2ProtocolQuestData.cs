#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;
using QuestWorldUnits = AAEmu.GodotViewer.Ui.X2.Scripting.World.IX2UnitData;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Quest API adapter over the main-thread protocol state. Static quest prose still comes from the
/// read-only client database; without an attached session every call retains <see cref="NullQuestData"/>
/// behavior.
/// </summary>
public sealed class X2ProtocolQuestData : NullQuestData
{
    private const byte QuestProgress = 1;
    private const byte QuestReady = 3;
    private const byte QuestCompleted = 5;
    private const byte QuestDailyCompleted = 6;

    private readonly Func<OnlineSession?> _session;
    private readonly Func<ClientActions?> _actions;
    private readonly Action<string, object?[]>? _fireEvent;
    private readonly string? _connectionString;
    private readonly string _localeColumn;
    private readonly Func<QuestWorldUnits?> _world;
    private readonly Dictionary<uint, bool> _tracking = new();
    private readonly HashSet<uint> _achievementTracking = [];
    private uint _npcObjectId;
    private IReadOnlyList<uint> _npcStartQuests = [];
    private IReadOnlyList<uint> _npcCompleteQuests = [];
    private bool _directingMode;
    private PendingQuest? _pendingQuest;

    /// <summary>Raised for the host camera when the original scripts enter or leave quest cinema.</summary>
    public event Action<bool>? DirectingModeChanged;

    private sealed record PendingQuest(uint QuestType, uint NpcObjectId, uint DoodadObjectId,
        uint SphereType, bool Completion);

    /// <summary>The host supplies server-authoritative quest options for the active NPC.</summary>
    public void SetNpcInteraction(uint npcObjectId, IReadOnlyList<uint>? startQuests,
        IReadOnlyList<uint>? completeQuests)
    {
        _npcObjectId = npcObjectId;
        _npcStartQuests = startQuests?.ToArray() ?? [];
        _npcCompleteQuests = completeQuests?.ToArray() ?? [];
        _pendingQuest = null;
        SetDirectingMode(false);
    }

    public X2ProtocolQuestData(
        string? gameDatabasePath,
        Func<OnlineSession?> session,
        Func<ClientActions?> actions,
        string locale = "en_us",
        Action<string, object?[]>? fireEvent = null,
        Func<QuestWorldUnits?>? world = null)
        : base(gameDatabasePath, locale)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _fireEvent = fireEvent;
        _world = world ?? (() => null);
        _connectionString = !string.IsNullOrWhiteSpace(gameDatabasePath) && File.Exists(gameDatabasePath)
            ? $"Data Source={gameDatabasePath};Mode=ReadOnly"
            : null;
        _localeColumn = locale.All(c => char.IsLetterOrDigit(c) || c == '_') ? locale : "en_us";
    }

    public override bool TryQuery(string table, string method, IReadOnlyList<object?> arguments, out object? value)
    {
        var session = _session();
        if (table == "X2Achievement")
        {
            if (method == "IsTracingAchievement")
            { value = arguments.Count > 1 && _achievementTracking.Contains(unchecked((uint)ToInt(arguments[1]))); return true; }
            if (method == "GetAchievementTracingList")
            {
                var kind = arguments.Count > 0 ? ToInt(arguments[0]) : 0;
                value = new X2ApiArrayData(_achievementTracking.Where(id => AchievementKind(id) == kind)
                    .Select(x => (object?)(double)x).ToArray());
                return true;
            }
            if (session is not null && method is "GetTodayAssignmentAllAcceptState" or "IsPossibleTodayAssignmentAllAccept")
            { value = session.TodayAssignmentState.EntriesByStepId.Values.Any(e => e.Status == 1); return true; }
            if (session is not null && method == "IsTodayAssignmentQuest" && arguments.Count > 0)
            { value = session.TodayAssignmentState.EntriesByStepId.Values.Any(e => e.QuestContextId == ToInt(arguments[0])); return true; }
            var completedOnly = session is not null && method is "GetAchievementMainList" or "GetAchievementSubList" &&
                arguments.Count > (method == "GetAchievementMainList" ? 2 : 1) &&
                ToInt(arguments[method == "GetAchievementMainList" ? 2 : 1]) == 2;
            var queryArgs = completedOnly ? arguments.Take(arguments.Count - 1).Append((object?)1d).ToArray() : arguments;
            var found = base.TryQuery(table, method, queryArgs, out value);
            if (session is null || !found) return found;
            if (method == "GetAchievementInfo" && value is X2ApiTableData info && arguments.Count > 0)
            {
                var id = unchecked((uint)ToInt(arguments[0]));
                var fields = info.Fields.ToDictionary(x => x.Key, x => x.Value);
                var completed = session.AchievementState.CompletedIds.Contains(id);
                fields["complete"] = completed;
                fields["tracing"] = _achievementTracking.Contains(id);
                fields["current"] = session.AchievementState.ProgressById.TryGetValue(id, out var progress)
                    ? (double)progress.Amount : 0d;
                if (fields.TryGetValue("totalSubCount", out var total) && ToInt(total) > 0 &&
                    base.TryQuery("X2Achievement", "GetAchievementSubList", [(double)id, 1d], out var children) &&
                    children is X2ApiArrayData childList)
                    fields["completeSubCount"] = (double)childList.Items.Count(item => item is X2ApiTableData child &&
                        child.Fields.TryGetValue("key", out var key) &&
                        session.AchievementState.CompletedIds.Contains(unchecked((uint)ToInt(key))));
                value = new X2ApiTableData(fields);
            }
            else if (method == "GetSubcategoryInfo" && value is X2ApiTableData subcategory && arguments.Count > 0)
            {
                var fields = subcategory.Fields.ToDictionary(x => x.Key, x => x.Value);
                var sub = ToInt(arguments[0]);
                fields["completedCount"] = (double)CategoryAchievementIds(0, sub >= 1000 ? sub - 1000 : 0,
                    sub >= 1000 ? 0 : sub).Count(x => session.AchievementState.CompletedIds.Contains(x));
                value = new X2ApiTableData(fields);
            }
            else if (method == "GetCategoryCount" && value is X2ApiMultiData count)
            {
                var kind = arguments.Count > 0 ? ToInt(arguments[0]) : 0;
                var cat = arguments.Count > 1 ? ToInt(arguments[1]) : 0;
                var sub = arguments.Count > 2 ? ToInt(arguments[2]) : 0;
                var filter = arguments.Count > 3 ? ToInt(arguments[3]) : 1;
                var ids = CategoryAchievementIds(kind, cat, sub);
                var selected = ids.Where(x => filter switch
                {
                    2 => session.AchievementState.CompletedIds.Contains(x),
                    3 => !session.AchievementState.CompletedIds.Contains(x),
                    4 => _achievementTracking.Contains(x),
                    _ => true,
                }).ToArray();
                var done = selected.Count(x => session.AchievementState.CompletedIds.Contains(x));
                value = new X2ApiMultiData([(double)done, (double)selected.Length]);
            }
            else if (method is "GetAchievementMainList" or "GetAchievementSubList" && value is X2ApiArrayData list)
            {
                var filter = arguments.Count > (method == "GetAchievementMainList" ? 2 : 1)
                    ? ToInt(arguments[method == "GetAchievementMainList" ? 2 : 1]) : 1;
                value = new X2ApiArrayData(list.Items.Where(item =>
                {
                    var id = item is X2ApiTableData row && row.Fields.TryGetValue("key", out var key) ? ToInt(key) : ToInt(item);
                    var completed = session.AchievementState.CompletedIds.Contains(unchecked((uint)id));
                    return filter switch
                    {
                        2 => completed,
                        3 => !completed,
                        4 => _achievementTracking.Contains(unchecked((uint)id)),
                        _ => true,
                    };
                }).ToArray());
            }
            else if (method is "GetTodayAssignmentInfo" or "GetTodayAssignmentInfoForChange" && value is X2ApiTableData today)
            {
                var fields = today.Fields.ToDictionary(x => x.Key, x => x.Value);
                var step = ToInt(fields["stepId"]);
                if (session.TodayAssignmentState.EntriesByStepId.TryGetValue(step, out var entry))
                {
                    fields["status"] = (double)entry.Status;
                    fields["questType"] = (double)entry.QuestContextId;
                    fields["satisfy"] = true;
                }
                value = new X2ApiTableData(fields);
            }
            else if (method == "GetTodayAssignmentResetCount" && arguments.Count > 0 &&
                session.TodayAssignmentState.ResetCountsByType.TryGetValue(unchecked((uint)ToInt(arguments[0])), out var resets))
                value = new X2ApiMultiData([(double)resets, (double)resets]);
            return true;
        }
        if (session is null || table != "X2Quest")
            return base.TryQuery(table, method, arguments, out value);

        var quests = session.QuestState.ActiveQuests.Values.ToArray();
        var first = arguments.Count == 0 ? 0 : ToInt(arguments[0]);
        var second = arguments.Count < 2 ? 0 : ToInt(arguments[1]);
        var byIndex = At(quests, first);
        var byType = ByType(quests, first);

        value = method switch
        {
            "GetActiveQuestListCount" => (double)quests.Length,
            "GetNpcQuestContextCountStart" => (double)_npcStartQuests.Count,
            "GetNpcQuestContextCountComplete" => (double)_npcCompleteQuests.Count,
            "GetNpcQuestContextCountProgress" or "GetNpcQuestContextCountTalk" => 0d,
            "GetNpcQuestContextQuestTypeStart" => Number(AtId(_npcStartQuests, first)),
            "GetNpcQuestContextQuestTypeComplete" => Number(AtId(_npcCompleteQuests, first == 0 ? 1 : first)),
            "IsQuestDirectingMode" => _directingMode,
            "IsQuestMultiSelectState" => _npcStartQuests.Count + _npcCompleteQuests.Count > 1,
            "GetActiveQuestType" => Number(byIndex?.TemplateId),
            "GetMainQuestListCount" => (double)MainQuests(quests).Count,
            "GetMainQuestType" => Number(At(MainQuests(quests), first)?.TemplateId),
            "GetMainQuestVecIndex" => (double)QuestIndex(quests, first, mainOnly: true),
            "GetZoneQuestVecIndex" => (double)QuestIndex(quests, first, mainOnly: false),
            "GetActiveQuestListName" or "GetQuestJournalTitle" => QuestName(byIndex),
            "GetActiveQuestTitle" => byType?.Status == QuestReady ? $"[Complete] {QuestName(byType)}" : QuestName(byType),
            "GetActiveQuestListStatus" => Number(byIndex?.Status),
            "GetActiveQuestListStatusByType" => Number(byType?.Status),
            "GetActiveQuestLevel" => QuestNumber(byIndex?.TemplateId, "level"),
            "GetActiveQuestLevelByType" => QuestNumber(byType?.TemplateId, "level"),
            "GetActiveQuestComparedLevel" or "GetActiveQuestComparedLevelByType" => 0d,
            "GetActiveQuestListObjectiveCount" or "GetQuestJournalObjectiveCount" => (double)Objectives(session, byIndex).Count,
            "GetQuestJournalObjectiveCountByType" or "GetQuestContextObjectiveCount" or "GetFirstObjectiveCount" =>
                (double)Objectives(session, byType).Count,
            "GetActiveQuestListObjectiveText" or "GetQuestJournalObjectiveText" => ObjectiveText(session, byIndex, second),
            "GetActiveQuestObjectiveText" or "GetQuestJournalObjectiveTextByType" or "GetQuestObjectiveText" =>
                ObjectiveText(session, byType, second),
            "GetQuestJournalBodyCount" => string.IsNullOrEmpty(QuestContextText(byIndex?.TemplateId, 2)) ? 0d : 1d,
            "GetQuestJournalBodyText" => first > 0 && second == 1 ? QuestContextText(byIndex?.TemplateId, 2) : null,
            "GetQuestJournalSubTitle" => QuestNameText(byIndex?.TemplateId, 1),
            "GetQuestJournalSubTitleByType" => QuestNameText(byType?.TemplateId, 1),
            "GetQuestJournalProgTitle" => QuestNameText(byIndex?.TemplateId, 2),
            "GetQuestJournalProgTitleByType" => QuestNameText(byType?.TemplateId, 2),
            "GetQuestContextSummary" => QuestContextText(QuestType(first), 1),
            "GetQuestContextBody" => QuestContextText(QuestType(first), 2),
            "GetQuestContextAcceptText" => QuestContextText(QuestType(first), 3),
            "GetQuestContextReportText" => QuestContextText(QuestType(first), 4),
            "GetQuestContextReadySummary" => ComponentText(QuestType(first), componentKind: 6),
            "GetTodayQuestInfo" => TodayQuestInfo(session, byType, QuestType(first)),
            "AcceptBubbleText" => AcceptBubbleText(QuestType(first)),
            "GetQuestContextRewardExp" => RewardAmount(QuestType(first), "QuestActSupplyExp", "quest_act_supply_exps", "exp"),
            "GetQuestContextRewardCopper" => RewardAmount(QuestType(first), "QuestActSupplyCopper", "quest_act_supply_coppers", "amount"),
            "GetQuestContextRewardAAPoint" => RewardAmount(QuestType(first), "QuestActSupplyAaPoint", "quest_act_supply_aa_points", "point"),
            "RewardHonorPoint" => RewardAmount(QuestType(first), "QuestActSupplyHonorPoint", "quest_act_supply_honor_points", "point"),
            "RewardLeadershipPoint" => RewardAmount(QuestType(first), "QuestActSupplyLeadershipPoint", "quest_act_supply_leadership_points", "point"),
            "RewardLivingPoint" => RewardAmount(QuestType(first), "QuestActSupplyLivingPoint", "quest_act_supply_living_points", "point"),
            "RewardArchePassPoint" => RewardAmount(QuestType(first), "QuestActSupplyArchePassPoint", "quest_act_supply_arche_pass_points", "point"),
            "RewardContributionPoint" => RewardAmount(QuestType(first), "QuestActSupplyContributionPoint", "quest_act_supply_contribution_points", "point"),
            "RewardCrimePoint" => RewardAmount(QuestType(first), "QuestActSupplyCrimePoint", "quest_act_supply_crime_points", "point"),
            "RewardExpeditionExp" => RewardAmount(QuestType(first), "QuestActSupplyExpeditionExp", "quest_act_supply_expedition_exps", "point"),
            "RewardFamilyExp" => RewardAmount(QuestType(first), "QuestActSupplyFamilyExp", "quest_act_supply_family_exps", "point"),
            "RewardLaborPower" => RewardAmount(QuestType(first), "QuestActSupplyLp", "quest_act_supply_lps", "lp"),
            "RewardLocalLaborPower" => RewardAmount(QuestType(first), "QuestActSupplyLocalLp", "quest_act_supply_local_lps", "local_lp"),
            "RewardActability" => RewardActability(QuestType(first)),
            "RewardResidentPoint" => RewardResidentPoint(QuestType(first)),
            "GetQuestContextRewardItemAllCount" => (double)RewardItems(QuestType(first), false).Count,
            "GetQuestContextRewardSelectiveItemAllCount" => (double)RewardItems(QuestType(first), true).Count,
            "GetQuestContextRewardItemCount" => Number(AtReward(RewardItems(QuestType(first), false), second)?.Count),
            "GetQuestContextRewardSelectiveItemCount" => Number(AtReward(RewardItems(QuestType(first), true), second)?.Count),
            "GetQuestContextRewardItemName" => AtReward(RewardItems(QuestType(first), false), second)?.Name,
            "GetQuestContextRewardSelectiveItemName" => AtReward(RewardItems(QuestType(first), true), second)?.Name,
            "GetQuestContextRewardItemIconType" => Number(AtReward(RewardItems(QuestType(first), false), second)?.ItemId),
            "GetQuestContextRewardSelectiveItemIconType" => Number(AtReward(RewardItems(QuestType(first), true), second)?.ItemId),
            "GetQuestContextItemTooltip" => RewardTooltip(AtReward(RewardItems(QuestType(first), false), second)),
            "GetQuestContextSelectiveItemTooltip" => RewardTooltip(AtReward(RewardItems(QuestType(first), true), second)),
            "GetQuestContextLeftTime" => Number(byType?.LeftTimeMilliseconds),
            "GetObjectiveComponentCount" => (double)ComponentCount(QuestType(first), 4),
            "GetObjective" => ObjectiveTable(session, byType, second),
            "GetLastQuest" => quests.Length == 0 ? 0d : (double)quests[^1].TemplateId,
            "GetTrackingActiveQuest" => TrackingQuest(quests, first),
            "GetQuestReportNpcTypeByQuestType" => Number(ReportNpcType(QuestType(first))),
            "GetQuestReportNpcNameByQuestType" => ReportNpcName(QuestType(first)),
            "GetQuestDirInfo" => QuestDirection(session, byType),
            "GetQuestNotifierLimit" => new X2ApiMultiData(new object?[] { 10d, 5d }),
            "IsProgressQuestInJournal" => byType?.Status == QuestProgress,
            "IsReadyQuestInJournal" or "IsReadyForCompleteQuest" => byType?.Status == QuestReady,
            "IsCompleted" => IsCompleted(session.QuestState, unchecked((uint)first)),
            "IsLetItDoneQuest" => QuestBool(byIndex?.TemplateId, "let_it_done"),
            "IsLetItDoneQuestByType" => QuestBool(QuestType(first), "let_it_done"),
            "IsMainQuest" => QuestNumber(QuestType(first), "chapter_idx") > 0,
            "IsHiddenQuest" => false,
            "IsTodayQuest" or "IsPublicQuest" or "IsSagaQuest" or "IsGroupQuest" => false,
            // The quest wire status 3 means Ready. OverDone is a separate quest marker (marker state 8)
            // and is not present in QuestWireRecord; a ready quest must retain its normal completion copy.
            "IsOverProgressQuestByType" or "IsOverDoneQuest" or "IsOverDoneQuestByType" => false,
            "GetScoreQuestCurrentScore" => Number(byType?.Objectives.FirstOrDefault()),
            "GetScoreQuestDoneScore" => QuestNumber(QuestType(first), "score"),
            "IsScoreQuest" => QuestNumber(QuestType(first), "score") > 0,
            _ => null,
        };

        // GetQuestDirInfo uses nil to mean that the quest currently has no
        // resolvable world target. Treat that nil as a handled live result;
        // falling through would let the metadata fallback manufacture {},
        // whose missing angle/dist fields break the notifier's arithmetic.
        if (value is not null || method == "GetQuestDirInfo")
            return true;
        return base.TryQuery(table, method, arguments, out value);
    }

    public override bool TryExecute(X2QuestCommand command, out object? result)
    {
        result = null;
        var session = _session();
        var actions = _actions();
        if (command.Table == "X2Achievement")
        {
            var achievementArgs = command.Arguments;
            var id = achievementArgs.Count > 1 ? unchecked((uint)ToInt(achievementArgs[1])) : 0;
            if (command.Method == "AddTracingAchievement")
            { result = id > 0 && _achievementTracking.Add(id); _fireEvent?.Invoke(X2QuestEvents.AchievementUpdate, []); return true; }
            if (command.Method == "RemoveTracingAchievement")
            { _achievementTracking.Remove(id); _fireEvent?.Invoke(X2QuestEvents.AchievementUpdate, []); return true; }
            if (session is null) return false;
            if (command.Method == "HandleClickTodayAssignment" && achievementArgs.Count > 1)
            {
                // Lua passes a realStep rather than the display index. Resolve it against the
                // server's step id, then choose Unlock/Accept from the authoritative status.
                var stepId = TodayStepId(id);
                var status = stepId > 0 && session.TodayAssignmentState.EntriesByStepId.TryGetValue(stepId, out var entry)
                    ? entry.Status : (sbyte)0;
                if (status is 0 or 1)
                    session.Client.SendRequestTodayAssignment(id, status == 0 ? (sbyte)1 : (sbyte)2);
                return true;
            }
            if (command.Method == "ResetTodayAssignment" && achievementArgs.Count > 1)
            { session.Client.SendResetTodayAssignment(id, 0); result = true; return true; }
            if (command.Method == "RequestTodayAssignmentAllAccept" && achievementArgs.Count > 0)
            {
                if (!session.TodayAssignmentState.EntriesByStepId.Values.Any(e => e.Status == 1)) return false;
                // The server's empty-list form selects all eligible realStep values itself.
                session.Client.SendAcceptAllTodayAssignment(unchecked((sbyte)ToInt(achievementArgs[0])), []);
                return true;
            }
            return false;
        }
        if (session is null || command.Table != "X2Quest")
            return base.TryExecute(command, out result);

        var args = command.Arguments;
        switch (command.Method)
        {
            case "EnterQuestDirectingMode":
                SetDirectingMode(true);
                return true;
            case "LeaveQuestDirectingMode":
            case "DeclineDirectingQuest":
                SetDirectingMode(false);
                _pendingQuest = null;
                return true;
            case "AcceptDirectingQuest" when actions is not null && _pendingQuest is { Completion: false } start:
                actions.AcceptQuest(start.QuestType, start.NpcObjectId, start.DoodadObjectId, start.SphereType);
                _pendingQuest = null;
                return true;
            case "CompleteDirectingQuest" when actions is not null && _pendingQuest is { Completion: true } complete:
                actions.CompleteQuest(complete.QuestType, complete.NpcObjectId, complete.DoodadObjectId, Arg(args, 0));
                _pendingQuest = null;
                return true;
            case "CallQuestSelectByNpc":
            {
                var npc = UArg(args, 0);
                if (npc == 0) npc = _npcObjectId;
                if (npc == 0 || npc != _npcObjectId || _npcStartQuests.Count + _npcCompleteQuests.Count < 2)
                    return false;
                var interactionValue = args.Count > 1 ? args[1]?.ToString() ?? "start" : "start";
                _fireEvent?.Invoke(X2QuestEvents.MultiQuestContextSelect,
                    [true, 0d, true, (double)npc, interactionValue]);
                return true;
            }
            case "TrySelectQuestContextToNpc":
            {
                var npc = UArg(args, 1);
                if (npc == 0 || npc != _npcObjectId) return false;
                var rows = new LuaTable
                {
                    ["completes"] = QuestSelectionRows(_npcCompleteQuests),
                    ["gives"] = QuestSelectionRows(_npcStartQuests),
                };
                _fireEvent?.Invoke(X2QuestEvents.MultiQuestContextSelectList, [rows]);
                return true;
            }
            case "SetTrackingActiveQuest":
            {
                var quests = session.QuestState.ActiveQuests.Values.ToArray();
                var index = Arg(args, 0);
                var quest = ByType(quests, index) ?? At(quests, index);
                if (quest is not null)
                    _tracking[quest.TemplateId] = args.Count < 2 || (args[1] switch
                    {
                        bool enabled => enabled,
                        _ => Arg(args, 1) != 0,
                    });
                return true;
            }
            case "QuestContextDrop" when actions is not null:
            {
                var quest = At(session.QuestState.ActiveQuests.Values.ToArray(), Arg(args, 0));
                if (quest is null) return false;
                actions.DropQuest(quest.TemplateId);
                return true;
            }
            case "TryStartQuestContext" when actions is not null:
                // Lua: who, quest type, sphere type, doodad object id, NPC object id.
                if (_directingMode)
                {
                    _pendingQuest = new PendingQuest(UArg(args, 1), UArg(args, 4), UArg(args, 3), UArg(args, 2), false);
                    FireQuestBubble(_pendingQuest.QuestType, componentKind: 2);
                }
                else
                    actions.AcceptQuest(UArg(args, 1), UArg(args, 4), UArg(args, 3), UArg(args, 2));
                return true;
            case "CallQuestUi":
            {
                var questType = UArg(args, 1);
                var npc = UArg(args, 2);
                if (npc == 0) npc = _npcObjectId;
                var completing = Arg(args, 0) == 2;
                if (questType == 0 || npc == 0 || npc != _npcObjectId ||
                    !(completing ? _npcCompleteQuests : _npcStartQuests).Contains(questType)) return false;
                var eventName = completing ? "COMPLETE_QUEST_CONTEXT_NPC" : "START_QUEST_CONTEXT_NPC";
                _fireEvent?.Invoke(eventName, [(double)questType, true, (double)npc]);
                return true;
            }
            case "TryCompleteQuestContext" when actions is not null:
                // Lua: who, quest type, doodad object id, NPC object id, selected reward (1 based).
                if (_directingMode)
                {
                    _pendingQuest = new PendingQuest(UArg(args, 1), UArg(args, 3), UArg(args, 2), 0, true);
                    FireQuestBubble(_pendingQuest.QuestType, componentKind: 6);
                }
                else
                    actions.CompleteQuest(UArg(args, 1), UArg(args, 3), UArg(args, 2), Arg(args, 4));
                return true;
            case "TryProgressTalkQuestComponent" when actions is not null:
            {
                // The wire layout has one target object field. Prefer the NPC and use the doodad when no NPC exists.
                var target = UArg(args, 3);
                if (target == 0) target = UArg(args, 4);
                actions.TalkQuest(target, UArg(args, 0), UArg(args, 1), UArg(args, 2));
                return true;
            }
            default:
                return base.TryExecute(command, out result);
        }
    }

    private void SetDirectingMode(bool enabled)
    {
        if (_directingMode == enabled) return;
        _directingMode = enabled;
        DirectingModeChanged?.Invoke(enabled);
    }

    private LuaTable QuestSelectionRows(IReadOnlyList<uint> questTypes)
    {
        var rows = new LuaTable();
        for (var i = 0; i < questTypes.Count; i++)
            rows[(double)(i + 1)] = new LuaTable
            {
                ["qtype"] = (double)questTypes[i],
                // quest_context_directing.lua accepts only QUEST_MARK_ORDER_* (1..7);
                // zero deliberately suppresses its stateMark drawable.
                ["order"] = (double)QuestMarkOrder(questTypes[i]),
            };
        return rows;
    }

    private int QuestMarkOrder(uint questType)
    {
        // quest_contexts.detail_id follows the client's QuestDetail enum.  The order
        // values are QUEST_MARK_ORDER_MAIN..NORMAL from constants.g and select the
        // matching colour/icon family in quest_context_directing.lua.
        var detail = (int)QuestNumber(questType, "detail_id");
        return detail switch
        {
            2 => 1,             // main
            3 => 2,             // saga / chronicle
            7 or 12 or 13 => 3, // daily, daily group, today
            15 => 4,            // weekly
            10 => 5,            // daily hunt
            8 or 11 => 6,       // livelihood, daily livelihood
            _ => 7,             // normal and remaining quest families
        };
    }

    private sealed record RewardItem(uint ItemId, uint Count, uint GradeId, string Name, string Icon);

    private void FireQuestBubble(uint questType, int componentKind)
    {
        if (_connectionString is null || _fireEvent is null || questType == 0) return;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT b.id, COALESCE(NULLIF(l.{_localeColumn}, ''), b.speech),
                   COALESCE(NULLIF(nl.{_localeColumn}, ''), n.name, '')
            FROM quest_components c
            JOIN quest_chat_bubbles b ON b.quest_component_id=c.id AND b.is_start='t' AND b.enable<>'f'
            LEFT JOIN localized_texts l ON l.tbl_name='quest_chat_bubbles' AND l.tbl_column_name='speech' AND l.idx=b.id
              AND l.{_localeColumn}<>'' AND instr(upper(l.{_localeColumn}),'DO NOT TRANSLATE')=0
              AND upper(ltrim(l.{_localeColumn})) NOT LIKE 'TEST:%'
            LEFT JOIN npcs n ON n.id=b.npc_id
            LEFT JOIN localized_texts nl ON nl.tbl_name='npcs' AND nl.tbl_column_name='name' AND nl.idx=n.id
              AND nl.{_localeColumn}<>'' AND instr(upper(nl.{_localeColumn}),'DO NOT TRANSLATE')=0
              AND upper(ltrim(nl.{_localeColumn})) NOT LIKE 'TEST:%'
            WHERE c.quest_context_id=$quest AND c.component_kind_id=$kind
            ORDER BY c.id, b.id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$quest", questType);
        cmd.Parameters.AddWithValue("$kind", componentKind);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return;
        var bubbleId = reader.GetInt64(0);
        var speech = UiTranslator.Shared.TranslateDatabaseText(reader.GetString(1));
        var author = UiTranslator.Shared.TranslateDatabaseText(reader.GetString(2));
        speech = Regex.Replace(speech, @"^/[A-Za-z_]+\s+", "");
        var world = _world();
        var playerName = world?.Get(world.PlayerId)?.Name;
        if (!string.IsNullOrEmpty(playerName))
            speech = speech.Replace("@PC_NAME(0)", playerName, StringComparison.Ordinal);
        _fireEvent(X2QuestEvents.ChatMsgQuest,
            [speech, author, 0d, false, 0d, 5000d, 500d, (double)bubbleId, (double)questType, true]);
    }

    private static RewardItem? AtReward(IReadOnlyList<RewardItem> items, int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= items.Count ? items[oneBasedIndex - 1] : null;

    private static X2ApiTableData? RewardTooltip(RewardItem? item) => item is null ? null :
        new X2ApiTableData(new Dictionary<string, object?>
        {
            ["itemType"] = (double)item.ItemId,
            ["name"] = item.Name,
            ["grade"] = (double)item.GradeId,
            ["itemGrade"] = (double)item.GradeId,
            ["icon"] = item.Icon,
        });

    private IReadOnlyList<RewardItem> RewardItems(uint? questType, bool selective)
    {
        if (questType is null || _connectionString is null) return [];
        using var db = Open();
        using var cmd = db.CreateCommand();
        var detailType = selective ? "QuestActSupplySelectiveItem" : "QuestActSupplyItem";
        var detailTable = selective ? "quest_act_supply_selective_items" : "quest_act_supply_items";
        cmd.CommandText = $"""
            SELECT r.item_id, r.count, r.grade_id,
                   COALESCE(NULLIF(l.{_localeColumn}, ''), i.name), COALESCE(ic.filename, '')
            FROM quest_components c
            JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type=$type AND a.enable<>'f'
            JOIN {detailTable} r ON r.id=a.act_detail_id
            JOIN items i ON i.id=r.item_id
            LEFT JOIN icons ic ON ic.id=i.icon_id
            LEFT JOIN localized_texts l ON l.tbl_name='items' AND l.tbl_column_name='name' AND l.idx=i.id
              AND l.{_localeColumn}<>'' AND instr(upper(l.{_localeColumn}),'DO NOT TRANSLATE')=0
              AND upper(ltrim(l.{_localeColumn})) NOT LIKE 'TEST:%'
            WHERE c.quest_context_id=$quest AND c.component_kind_id=8 ORDER BY a.id
            """;
        cmd.Parameters.AddWithValue("$quest", questType.Value);
        cmd.Parameters.AddWithValue("$type", detailType);
        using var reader = cmd.ExecuteReader();
        var rows = new List<RewardItem>();
        while (reader.Read())
            rows.Add(new RewardItem((uint)reader.GetInt64(0), (uint)reader.GetInt64(1),
                (uint)reader.GetInt64(2), UiTranslator.Shared.TranslateDatabaseText(reader.GetString(3)), ItemIconPath(reader.GetString(4))));
        return rows;
    }

    private static string ItemIconPath(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "";
        var path = filename.Replace('\\', '/').TrimStart('/');
        return path.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ? path : $"ui/icon/{path}";
    }

    private double RewardAmount(uint? questType, string detailType, string detailTable, string amountColumn)
    {
        if (questType is null || _connectionString is null) return 0;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(SUM(r.{amountColumn}), 0)
            FROM quest_components c
            JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type=$type AND a.enable<>'f'
            JOIN {detailTable} r ON r.id=a.act_detail_id
            WHERE c.quest_context_id=$quest AND c.component_kind_id=8
            """;
        cmd.Parameters.AddWithValue("$quest", questType.Value);
        cmd.Parameters.AddWithValue("$type", detailType);
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private object? TodayQuestInfo(OnlineSession session, QuestWireRecord? quest, uint? questType)
    {
        if (questType is null) return null;
        var componentId = quest is not null && session.QuestState.Progress.TryGetValue(quest.InstanceId, out var progress)
            ? progress.CurrentComponentId : 0;
        if (componentId == 0 || quest?.Status == QuestReady)
            componentId = FirstComponent(questType.Value, 4);
        var targets = componentId == 0 ? [] : ObjectiveTargets(componentId);
        var maximum = targets.Count > 0 ? Math.Max(1u, targets[0]) : 0u;
        var current = quest?.Status == QuestReady ? maximum : quest?.Objectives.FirstOrDefault() ?? 0;
        var name = base.TryQuery("X2Quest", "GetQuestName", [questType.Value], out var result)
            ? result as string ?? "" : "";
        var summary = QuestContextText(questType, 1) ?? "";
        return new X2ApiTableData(new Dictionary<string, object?>
        {
            ["title"] = name, ["summary"] = summary, ["description"] = QuestContextText(questType, 2) ?? "",
            ["curValue"] = (double)current, ["maxValue"] = (double)maximum,
            ["grade"] = QuestNumber(questType, "grade_id"),
        });
    }

    private object? RewardActability(uint? questType)
    {
        if (questType is null || _connectionString is null) return null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT g.id, COALESCE(NULLIF(l.{_localeColumn},''),g.name),SUM(r.point)
            FROM quest_components c JOIN quest_acts a ON a.quest_component_id=c.id
              AND a.act_detail_type='QuestActSupplyActability' AND a.enable<>'f'
            JOIN quest_act_supply_actabilities r ON r.id=a.act_detail_id
            JOIN actability_groups g ON g.id=r.actability_group_id
            LEFT JOIN localized_texts l ON l.tbl_name='actability_groups' AND l.tbl_column_name='name' AND l.idx=g.id
            WHERE c.quest_context_id=$quest AND c.component_kind_id=8 GROUP BY g.id ORDER BY a.id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$quest", questType.Value);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new X2ApiTableData(new Dictionary<string, object?>
        { ["name"] = UiTranslator.Shared.TranslateDatabaseText(r.GetString(1)), ["point"] = (double)r.GetInt64(2),
          ["type"] = (double)r.GetInt32(0) });
    }

    private object RewardResidentPoint(uint? questType)
    {
        if (questType is null || _connectionString is null) return new X2ApiMultiData([0d, 0d]);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT r.zone_group_id,SUM(r.point) FROM quest_components c " +
            "JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type='QuestActSupplyResidentPoint' AND a.enable<>'f' " +
            "JOIN quest_act_supply_resident_points r ON r.id=a.act_detail_id " +
            "WHERE c.quest_context_id=$quest AND c.component_kind_id=8 GROUP BY r.zone_group_id ORDER BY a.id LIMIT 1";
        cmd.Parameters.AddWithValue("$quest", questType.Value);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new X2ApiMultiData([(double)r.GetInt32(0), (double)r.GetInt64(1)]) :
            new X2ApiMultiData([0d, 0d]);
    }

    private object AcceptBubbleText(uint? questType)
    {
        if (questType is null || _connectionString is null) return new X2ApiArrayData([]);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT b.id,b.next_bubble,b.is_start,b.chat_bubble_kind_id,
                   COALESCE(NULLIF(s.{_localeColumn},''),b.speech),
                   COALESCE(NULLIF(cs.{_localeColumn},''),NULLIF(b.change_speaker_name,''),
                            NULLIF(nl.{_localeColumn},''),n.name,'')
            FROM quest_components c JOIN quest_chat_bubbles b ON b.quest_component_id=c.id AND b.enable<>'f'
            LEFT JOIN localized_texts s ON s.tbl_name='quest_chat_bubbles' AND s.tbl_column_name='speech' AND s.idx=b.id
            LEFT JOIN localized_texts cs ON cs.tbl_name='quest_chat_bubbles' AND cs.tbl_column_name='change_speaker_name' AND cs.idx=b.id
            LEFT JOIN npcs n ON n.id=b.npc_id
            LEFT JOIN localized_texts nl ON nl.tbl_name='npcs' AND nl.tbl_column_name='name' AND nl.idx=n.id
            WHERE c.quest_context_id=$quest AND c.component_kind_id=2 ORDER BY c.id,b.id
            """;
        cmd.Parameters.AddWithValue("$quest", questType.Value);
        var records = new Dictionary<int, (int Next, bool Start, double Kind, string Text, string Who)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) records[r.GetInt32(0)] = (r.IsDBNull(1) ? 0 : r.GetInt32(1),
                !r.IsDBNull(2) && r.GetValue(2).ToString() is "t" or "1", r.IsDBNull(3) ? 3d : (double)r.GetInt32(3),
                UiTranslator.Shared.TranslateDatabaseText(r.GetString(4)),
                UiTranslator.Shared.TranslateDatabaseText(r.GetString(5)));
        var lines = new List<object?>();
        var visited = new HashSet<int>();
        foreach (var start in records.Where(x => x.Value.Start).Select(x => x.Key))
        {
            var id = start;
            while (id > 0 && records.TryGetValue(id, out var line) && visited.Add(id))
            {
                lines.Add(new X2ApiTableData(new Dictionary<string, object?>
                { ["kind"] = line.Kind, ["text"] = line.Text, ["who"] = line.Who }));
                id = line.Next;
            }
        }
        return new X2ApiArrayData(lines);
    }

    private IReadOnlyList<ObjectiveLine> Objectives(OnlineSession session, QuestWireRecord? quest)
    {
        if (quest is null || _connectionString is null)
            return Array.Empty<ObjectiveLine>();

        // A ready quest's current component is the report action, not an objective component.
        // The native journal supplies the report NPC as its single objective in this state.
        if (quest.Status == QuestReady && ReportNpcName(quest.TemplateId) is { Length: > 0 } npc)
            return [new ObjectiveLine($"Report to {npc}", 0, 1, false)];

        var componentId = session.QuestState.Progress.TryGetValue(quest.InstanceId, out var progress)
            ? progress.CurrentComponentId
            : 0;
        if (componentId == 0)
            componentId = FirstComponent(quest.TemplateId, 4);
        if (componentId == 0)
            return Array.Empty<ObjectiveLine>();

        var summary = LocalizedComponentText(componentId) ?? "Quest objective";
        var targets = ObjectiveTargets(componentId);
        var count = targets.Count;
        if (count == 0 && quest.Status == QuestProgress)
            count = Math.Max(1, quest.Objectives.TakeWhile((_, i) => i == 0 || quest.Objectives[i] != 0).Count());
        if (count == 0)
            return Array.Empty<ObjectiveLine>();

        var lines = new List<ObjectiveLine>(count);
        for (var i = 0; i < count; i++)
        {
            var current = i < quest.Objectives.Count ? quest.Objectives[i] : 0;
            var target = i < targets.Count ? Math.Max(1, targets[i]) : Math.Max(1u, current);
            var suffix = target > 1 || current > 0 ? $" ({current}/{target})" : "";
            lines.Add(new ObjectiveLine(summary + suffix, current, target, current >= target));
        }
        return lines;
    }

    private object? ObjectiveText(OnlineSession session, QuestWireRecord? quest, int oneBasedIndex)
    {
        var lines = Objectives(session, quest);
        return oneBasedIndex > 0 && oneBasedIndex <= lines.Count ? lines[oneBasedIndex - 1].Text : null;
    }

    private object? ObjectiveTable(OnlineSession session, QuestWireRecord? quest, int oneBasedIndex)
    {
        var lines = Objectives(session, quest);
        if (oneBasedIndex <= 0 || oneBasedIndex > lines.Count) return null;
        var line = lines[oneBasedIndex - 1];
        return new X2ApiTableData(new Dictionary<string, object?>
        {
            ["text"] = line.Text,
            ["count"] = (double)line.Current,
            ["target"] = (double)line.Target,
            ["completed"] = line.Completed,
        });
    }

    private double TrackingQuest(IReadOnlyList<QuestWireRecord> quests, int questTypeOrIndex)
    {
        if (questTypeOrIndex > 0)
        {
            var quest = ByType(quests, questTypeOrIndex) ?? At(quests, questTypeOrIndex);
            return quest is not null && _tracking.GetValueOrDefault(quest.TemplateId, true) ? 1d : 0d;
        }
        for (var i = 0; i < quests.Count; i++)
            if (_tracking.GetValueOrDefault(quests[i].TemplateId, true)) return i + 1;
        return 0d;
    }

    private uint? ReportNpcType(uint? questType)
    {
        if (questType is null || _connectionString is null) return null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT r.npc_id FROM quest_components c
            JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type='QuestActConReportNpc' AND a.enable<>'f'
            JOIN quest_act_con_report_npcs r ON r.id=a.act_detail_id
            WHERE c.quest_context_id=$id AND c.component_kind_id=6 ORDER BY c.id, a.id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", questType.Value);
        return cmd.ExecuteScalar() is long id && id > 0 ? checked((uint)id) : null;
    }

    private string? ReportNpcName(uint? questType)
    {
        var npcType = ReportNpcType(questType);
        if (npcType is null) return null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(NULLIF(l.{_localeColumn}, ''), n.name) FROM npcs n
            LEFT JOIN localized_texts l ON l.tbl_name='npcs' AND l.tbl_column_name='name' AND l.idx=n.id
              AND l.{_localeColumn}<>'' AND instr(upper(l.{_localeColumn}),'DO NOT TRANSLATE')=0
              AND upper(ltrim(l.{_localeColumn})) NOT LIKE 'TEST:%'
            WHERE n.id=$id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", npcType.Value);
        return UiTranslator.Shared.TranslateDatabaseText(cmd.ExecuteScalar() as string);
    }

    private object? QuestDirection(OnlineSession session, QuestWireRecord? quest)
    {
        if (quest is null) return null;
        var world = _world();
        if (world is null) return null;
        var npcType = ReportNpcType(quest.TemplateId);
        if (npcType is null) return null;
        var player = session.Player.CryPosition;
        var objectIds = new HashSet<uint>([quest.ObjectId, quest.ObjectId2, quest.ObjectId3]);
        objectIds.Remove(0);
        // Prefer the quest packet's target object when it is streamed. The template fallback covers snapshots
        // whose object fields are zero (as AAEmu currently writes them) and quests with several target copies.
        var npc = world.All.Where(u => u.Type == "npc" && u.TemplateId == npcType.Value && objectIds.Contains(u.Id))
            .OrderBy(u => Math.Pow(u.X - player.X, 2) + Math.Pow(u.Y - player.Y, 2)).FirstOrDefault()
            ?? world.All.Where(u => u.Type == "npc" && u.TemplateId == npcType.Value)
            .OrderBy(u => Math.Pow(u.X - player.X, 2) + Math.Pow(u.Y - player.Y, 2)).FirstOrDefault();
        if (npc is null) return null;
        var east = npc.X - player.X;
        var north = npc.Y - player.Y;
        // Cry heading is counterclockwise from north (+Y), so an eastward target
        // has a negative bearing. Keep the result in the notifier's 0..360 range.
        var angle = (Math.Atan2(-east, north) - session.Player.Heading) * 180 / Math.PI;
        angle = (angle % 360 + 360) % 360;
        return new X2ApiTableData(new Dictionary<string, object?>
        {
            ["dist"] = (double)Math.Round(Math.Sqrt(east * east + north * north)),
            ["angle"] = angle,
            ["height"] = (double)(npc.Z - player.Z),
        });
    }

    private List<uint> ObjectiveTargets(uint componentId)
    {
        if (_connectionString is null) return new List<uint>();
        var targets = new List<uint>();
        var details = new List<(long Id, string Type)>();
        using var db = Open();
        using (var acts = db.CreateCommand())
        {
            acts.CommandText = "SELECT act_detail_id,act_detail_type FROM quest_acts WHERE quest_component_id=$id AND enable<>'f' ORDER BY id";
            acts.Parameters.AddWithValue("$id", componentId);
            using var rows = acts.ExecuteReader();
            while (rows.Read())
                details.Add((rows.GetInt64(0), rows.GetString(1)));
        }
        foreach (var detail in details)
        {
            var table = DetailTable(detail.Type);
            targets.Add(table is null ? 1u : ReadTarget(db, table, detail.Id));
        }
        return targets;
    }

    private static string? DetailTable(string type) => type switch
    {
        "QuestActObjItemGather" => "quest_act_obj_item_gathers",
        "QuestActObjItemGroupGather" => "quest_act_obj_item_group_gathers",
        "QuestActObjItemUse" => "quest_act_obj_item_uses",
        "QuestActObjItemGroupUse" => "quest_act_obj_item_group_uses",
        "QuestActObjMonsterHunt" => "quest_act_obj_monster_hunts",
        "QuestActObjMonsterGroupHunt" => "quest_act_obj_monster_group_hunts",
        "QuestActObjMonsterContrHunt" => "quest_act_obj_monster_contr_hunts",
        "QuestActObjMonsterContrGroupHunt" => "quest_act_obj_monster_contr_group_hunts",
        "QuestActObjZoneKill" => "quest_act_obj_zone_kills",
        "QuestActObjZoneMonsterHunt" => "quest_act_obj_zone_monster_hunts",
        "QuestActObjZoneQuestComplete" => "quest_act_obj_zone_quest_completes",
        "QuestActObjInteraction" => "quest_act_obj_interactions",
        "QuestActObjCraft" => "quest_act_obj_crafts",
        "QuestActObjTalk" => "quest_act_obj_talks",
        "QuestActObjSphere" => "quest_act_obj_spheres",
        _ => null,
    };

    private static uint ReadTarget(SqliteConnection db, string table, long id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {(table == "quest_act_obj_zone_kills" ? "count_pk+count_npc" : "count")} FROM {table} WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        try { return Convert.ToUInt32(cmd.ExecuteScalar() ?? 1, CultureInfo.InvariantCulture); }
        catch (SqliteException) { return 1; }
    }

    private string? QuestName(QuestWireRecord? quest)
    {
        if (quest is null) return null;
        return base.TryQuery("X2Quest", "GetQuestName", new object?[] { quest.TemplateId }, out var value)
            ? value as string
            : null;
    }

    private string? QuestContextText(uint? questType, int kind)
    {
        if (questType is null || _connectionString is null) return null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(NULLIF(l.{_localeColumn}, ''), t.text)
            FROM quest_context_texts t
            LEFT JOIN localized_texts l ON l.tbl_name='quest_context_texts' AND l.tbl_column_name='text' AND l.idx=t.id
              AND l.{_localeColumn}<>'' AND instr(upper(l.{_localeColumn}),'DO NOT TRANSLATE')=0
              AND upper(ltrim(l.{_localeColumn})) NOT LIKE 'TEST:%'
            WHERE t.quest_context_id=$id AND t.quest_context_text_kind_id=$kind LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", questType.Value);
        cmd.Parameters.AddWithValue("$kind", kind);
        return UiTranslator.Shared.TranslateDatabaseText(cmd.ExecuteScalar() as string);
    }

    private string? QuestNameText(uint? questType, int kind)
    {
        if (questType is null || _connectionString is null) return null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(NULLIF(l.{_localeColumn}, ''), n.name)
            FROM quest_names n
            LEFT JOIN localized_texts l ON l.tbl_name='quest_names' AND l.tbl_column_name='name' AND l.idx=n.id
              AND l.{_localeColumn}<>'' AND instr(upper(l.{_localeColumn}),'DO NOT TRANSLATE')=0
              AND upper(ltrim(l.{_localeColumn})) NOT LIKE 'TEST:%'
            WHERE n.quest_context_id=$id AND n.quest_name_kind_id=$kind LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", questType.Value);
        cmd.Parameters.AddWithValue("$kind", kind);
        return UiTranslator.Shared.TranslateDatabaseText(cmd.ExecuteScalar() as string);
    }

    private string? ComponentText(uint? questType, int componentKind)
    {
        if (questType is null) return null;
        var componentId = FirstComponent(questType.Value, componentKind);
        return componentId == 0 ? null : LocalizedComponentText(componentId);
    }

    private string? LocalizedComponentText(uint componentId)
    {
        if (_connectionString is null) return null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(NULLIF(l.{_localeColumn}, ''), t.text)
            FROM quest_component_texts t
            LEFT JOIN localized_texts l ON l.tbl_name='quest_component_texts' AND l.tbl_column_name='text' AND l.idx=t.id
              AND l.{_localeColumn}<>'' AND instr(upper(l.{_localeColumn}),'DO NOT TRANSLATE')=0
              AND upper(ltrim(l.{_localeColumn})) NOT LIKE 'TEST:%'
            WHERE t.quest_component_id=$id AND t.quest_component_text_kind_id=4 AND t.enable<>'f' LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", componentId);
        return UiTranslator.Shared.TranslateDatabaseText(cmd.ExecuteScalar() as string);
    }

    private uint FirstComponent(uint questType, int componentKind)
    {
        if (_connectionString is null) return 0;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM quest_components WHERE quest_context_id=$id AND component_kind_id=$kind ORDER BY id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", questType);
        cmd.Parameters.AddWithValue("$kind", componentKind);
        return Convert.ToUInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private int ComponentCount(uint? questType, int componentKind)
    {
        if (questType is null || _connectionString is null) return 0;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM quest_components WHERE quest_context_id=$id AND component_kind_id=$kind";
        cmd.Parameters.AddWithValue("$id", questType.Value);
        cmd.Parameters.AddWithValue("$kind", componentKind);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private double QuestNumber(uint? questType, string column)
    {
        if (questType is null || _connectionString is null) return 0;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM quest_contexts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", questType.Value);
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private bool QuestBool(uint? questType, string column)
    {
        if (questType is null || _connectionString is null) return false;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM quest_contexts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", questType.Value);
        return cmd.ExecuteScalar() switch
        {
            bool value => value,
            long value => value != 0,
            string value => value is "t" or "true" or "1",
            _ => false,
        };
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static QuestWireRecord? At(IReadOnlyList<QuestWireRecord> quests, int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= quests.Count ? quests[oneBasedIndex - 1] : null;

    private static QuestWireRecord? ByType(IEnumerable<QuestWireRecord> quests, int questType) =>
        quests.FirstOrDefault(q => q.TemplateId == unchecked((uint)questType));

    private IReadOnlyList<QuestWireRecord> MainQuests(IEnumerable<QuestWireRecord> quests) =>
        quests.Where(q => QuestNumber(q.TemplateId, "chapter_idx") > 0).ToArray();

    private int QuestIndex(IReadOnlyList<QuestWireRecord> quests, int questType, bool mainOnly)
    {
        if (questType <= 0) return 0;
        var vectorIndex = 0;
        for (var i = 0; i < quests.Count; i++)
        {
            var quest = quests[i];
            var isMain = QuestNumber(quest.TemplateId, "chapter_idx") > 0;
            if (isMain != mainOnly) continue;
            vectorIndex++;
            if (quest.TemplateId == unchecked((uint)questType)) return vectorIndex;
        }
        return 0;
    }

    private static uint? QuestType(int value) => value > 0 ? unchecked((uint)value) : null;

    private static uint? AtId(IReadOnlyList<uint> ids, int oneBasedIndex) =>
        oneBasedIndex > 0 && oneBasedIndex <= ids.Count ? ids[oneBasedIndex - 1] : null;

    private static bool IsCompleted(QuestState state, uint questType)
    {
        var active = state.ActiveQuests.Values.FirstOrDefault(q => q.TemplateId == questType);
        if (active?.Status is QuestCompleted or QuestDailyCompleted) return true;
        var blockId = questType / 64;
        var bit = (int)(questType % 64);
        return state.CompletedBlocks.TryGetValue(blockId, out var block)
            && bit / 8 < block.Bits.Length
            && (block.Bits[bit / 8] & (1 << (bit % 8))) != 0;
    }

    private static double Number(uint? value) => value ?? 0;
    private static double Number(int? value) => value ?? 0;
    private static double Number(byte? value) => value ?? 0;

    private static int Arg(IReadOnlyList<object?> args, int index) => index < args.Count ? ToInt(args[index]) : 0;
    private static uint UArg(IReadOnlyList<object?> args, int index) => unchecked((uint)Math.Max(0, Arg(args, index)));

    private int AchievementKind(uint id)
    {
        if (_connectionString is null) return 0;
        try
        {
            using var db = new SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT c.achievement_kind_id FROM achievements a " +
                "JOIN achievement_sub_categories s ON s.id=a.achievement_sub_category_id " +
                "JOIN achievement_categories c ON c.id=s.achievement_category_id WHERE a.id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch (SqliteException) { return 0; }
    }

    private IReadOnlyList<uint> CategoryAchievementIds(int kind, int category, int subcategory)
    {
        if (_connectionString is null) return [];
        try
        {
            using var db = new SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT a.id FROM achievements a " +
                "JOIN achievement_sub_categories s ON s.id=a.achievement_sub_category_id " +
                "JOIN achievement_categories c ON c.id=s.achievement_category_id " +
                "WHERE ($kind=0 OR c.achievement_kind_id=$kind) AND ($cat=0 OR c.id=$cat) " +
                "AND ($sub=0 OR s.id=$sub) AND COALESCE(a.is_hidden,'f') NOT IN ('t','1')";
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$sub", subcategory);
            var result = new List<uint>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(unchecked((uint)reader.GetInt32(0)));
            return result;
        }
        catch (SqliteException) { return []; }
    }

    private int TodayStepId(uint realStep)
    {
        if (_connectionString is null) return 0;
        try
        {
            using var db = new SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id FROM today_quest_steps WHERE real_step=$step LIMIT 1";
            cmd.Parameters.AddWithValue("$step", realStep);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch (SqliteException) { return 0; }
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        uint u when u <= int.MaxValue => (int)u,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        ulong u when u <= int.MaxValue => (int)u,
        double d when d is >= int.MinValue and <= int.MaxValue => (int)d,
        float f when f is >= int.MinValue and <= int.MaxValue => (int)f,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => 0,
    };

    private sealed record ObjectiveLine(string Text, uint Current, uint Target, bool Completed);
}
