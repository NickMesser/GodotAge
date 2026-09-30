#nullable enable
using System.Collections;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A keyed result returned by <see cref="IX2QuestData.TryQuery"/>.</summary>
public sealed record X2ApiTableData(IReadOnlyDictionary<string, object?> Fields);

/// <summary>A 1-based Lua array result returned by <see cref="IX2QuestData.TryQuery"/>.</summary>
public sealed record X2ApiArrayData(IReadOnlyList<object?> Items);

/// <summary>Several positional Lua return values (for bindings whose metadata records only the first result).</summary>
public sealed record X2ApiMultiData(IReadOnlyList<object?> Values);

/// <summary>A command issued by one of the quest, activity, event, tutorial, or trial windows.</summary>
public sealed record X2QuestCommand(string Table, string Method, IReadOnlyList<object?> Arguments);

/// <summary>
/// Live data boundary for the Quest API family. Query results may be primitive values, <see cref="X2ApiTableData"/>,
/// <see cref="X2ApiArrayData"/>, or nested combinations of those values. This deliberately small boundary lets the
/// network-backed host preserve the original client's irregular table shapes without introducing Godot types here.
/// Returning false asks the API to use the real client's empty/unavailable value for that binding.
/// </summary>
public interface IX2QuestData
{
    bool TryQuery(string table, string method, IReadOnlyList<object?> arguments, out object? value);

    /// <summary>
    /// Forwards a server-authoritative action. Return false when the action has no immediate result; completion is then
    /// reported through <see cref="X2QuestEvents"/>. Boolean-returning actions may return an immediate accepted value.
    /// </summary>
    bool TryExecute(X2QuestCommand command, out object? result);
}

/// <summary>Fresh-character/offline state: no quests, passes, achievements, events, tutorials, or trial records.</summary>
public class NullQuestData : IX2QuestData
{
    public static readonly NullQuestData Instance = new();
    private readonly string? _connectionString;
    private readonly string _localeColumn;

    /// <param name="gameDatabasePath">
    /// Optional decrypted game-content database. It is opened read only and only for server-independent quest metadata.
    /// Omit it before the host has configured its game-data path.
    /// </param>
    /// <param name="locale">A localized_texts column; invalid names fall back to en_us.</param>
    public NullQuestData(string? gameDatabasePath = null, string locale = "en_us")
    {
        gameDatabasePath ??= X2DbLists.DefaultDatabase;
        _connectionString = !string.IsNullOrWhiteSpace(gameDatabasePath) && File.Exists(gameDatabasePath)
            ? $"Data Source={gameDatabasePath};Mode=ReadOnly"
            : null;
        _localeColumn = locale.All(c => char.IsLetterOrDigit(c) || c == '_') ? locale : "en_us";
    }

    public virtual bool TryQuery(string table, string method, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;
        if (table == "X2Achievement")
            return X2AssignmentAchievementData.TryQuery(_connectionString, method, arguments, out value);
        if (table != "X2Quest" || _connectionString is null || arguments.Count == 0)
            return false;

        var questType = ToInt(arguments[0]);
        try
        {
            value = method switch
            {
                "GetQuestName" or "GetQuestContextMainTitle" => QuestText(questType, "name"),
                "GetQuestCategoryName" => CategoryText(questType, "name"),
                "GetQuestCategoryNameByQuestType" => QuestCategoryText(questType, "name"),
                "GetQuestCategoryTextByType" => QuestCategoryText(questType, "description"),
                "GetQuestCategoryTypeByQuestType" => QuestNumber(questType, "category_id"),
                "GetActiveQuestLevelByType" => QuestNumber(questType, "level"),
                "GetQuestContextGrade" => QuestNumber(questType, "grade_id"),
                "GetQuestContextQuestChapterIdxByType" => QuestNumber(questType, "chapter_idx"),
                "GetQuestContextQuestIdxByType" => QuestNumber(questType, "quest_idx"),
                "GetQuestContextQuestIsHideChapterIdxByType" => QuestBool(questType, "hide_chapter_index"),
                "IsRepeatableQuest" => QuestBool(questType, "repeatable"),
                "IsSelectiveQuest" => QuestBool(questType, "selective"),
                "IsExistCinema" => Scalar("SELECT EXISTS(SELECT 1 FROM quest_components WHERE quest_context_id=$id AND cinema_id IS NOT NULL AND cinema_id<>0)", questType) is not 0d,
                "GetTodayQuestInfo" => new X2ApiTableData(new Dictionary<string, object?>
                { ["title"] = QuestText(questType, "name") ?? "",
                  ["summary"] = QuestContextLine(questType, 1), ["description"] = QuestContextLine(questType, 2),
                  ["curValue"] = 0d, ["maxValue"] = ObjectiveTarget(questType), ["grade"] = QuestNumber(questType, "grade_id") }),
                "AcceptBubbleText" => AcceptBubbles(questType),
                "GetQuestContextRewardAAPoint" => SupplyAmount(questType, "QuestActSupplyAaPoint", "quest_act_supply_aa_points", "point"),
                "RewardHonorPoint" => SupplyAmount(questType, "QuestActSupplyHonorPoint", "quest_act_supply_honor_points", "point"),
                "RewardLeadershipPoint" => SupplyAmount(questType, "QuestActSupplyLeadershipPoint", "quest_act_supply_leadership_points", "point"),
                "RewardLivingPoint" => SupplyAmount(questType, "QuestActSupplyLivingPoint", "quest_act_supply_living_points", "point"),
                "RewardArchePassPoint" => SupplyAmount(questType, "QuestActSupplyArchePassPoint", "quest_act_supply_arche_pass_points", "point"),
                "RewardContributionPoint" => SupplyAmount(questType, "QuestActSupplyContributionPoint", "quest_act_supply_contribution_points", "point"),
                "RewardCrimePoint" => SupplyAmount(questType, "QuestActSupplyCrimePoint", "quest_act_supply_crime_points", "point"),
                "RewardExpeditionExp" => SupplyAmount(questType, "QuestActSupplyExpeditionExp", "quest_act_supply_expedition_exps", "point"),
                "RewardFamilyExp" => SupplyAmount(questType, "QuestActSupplyFamilyExp", "quest_act_supply_family_exps", "point"),
                "RewardLaborPower" => SupplyAmount(questType, "QuestActSupplyLp", "quest_act_supply_lps", "lp"),
                "RewardLocalLaborPower" => SupplyAmount(questType, "QuestActSupplyLocalLp", "quest_act_supply_local_lps", "local_lp"),
                "RewardActability" => SupplyActability(questType),
                "RewardResidentPoint" => SupplyResidentPoint(questType),
                _ => null,
            };
            return value is not null || method == "RewardActability";
        }
        catch (SqliteException)
        {
            value = null;
            return false;
        }
    }
    public virtual bool TryExecute(X2QuestCommand command, out object? result)
    {
        result = null;
        return false;
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        double d => checked((int)d),
        uint u => checked((int)u),
        string s when int.TryParse(s, out var i) => i,
        _ => 0,
    };

    private string? QuestText(int id, string column) => Localized("quest_contexts", column, id,
        $"SELECT {column} FROM quest_contexts WHERE id=$id");

    private string? CategoryText(int id, string column) => Localized("quest_categories", column, id,
        $"SELECT {column} FROM quest_categories WHERE id=$id");

    private string? QuestCategoryText(int id, string column)
    {
        var categoryId = (int)QuestNumber(id, "category_id");
        return CategoryText(categoryId, column);
    }

    private string? Localized(string table, string column, int id, string fallbackSql)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT l.{_localeColumn}, b.value
            FROM ({fallbackSql.Replace($"SELECT {column}", $"SELECT {column} AS value")}) b
            LEFT JOIN localized_texts l ON l.tbl_name=$table AND l.tbl_column_name=$column AND l.idx=$id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$column", column);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var localized = reader.IsDBNull(0) ? null : reader.GetString(0);
        var source = reader.IsDBNull(1) ? null : reader.GetString(1);
        return UiTranslator.Shared.TranslateDatabaseText(localized, source);
    }

    private double QuestNumber(int id, string column) => Scalar($"SELECT {column} FROM quest_contexts WHERE id=$id", id);

    private bool QuestBool(int id, string column)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM quest_contexts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        var raw = cmd.ExecuteScalar();
        return raw switch { long n => n != 0, bool b => b, string s => s is "t" or "true" or "1", _ => false };
    }

    private double Scalar(string sql, int id)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    private double SupplyAmount(int questType, string detailType, string detailTable, string amountColumn)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(SUM(r.{amountColumn}),0) FROM quest_components c " +
            $"JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type=$type AND a.enable<>'f' " +
            $"JOIN {detailTable} r ON r.id=a.act_detail_id WHERE c.quest_context_id=$id AND c.component_kind_id=8";
        cmd.Parameters.AddWithValue("$type", detailType);
        cmd.Parameters.AddWithValue("$id", questType);
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    private string QuestContextLine(int questType, int kind)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(NULLIF(l.{_localeColumn},''),'') FROM quest_context_texts t " +
            "LEFT JOIN localized_texts l ON l.tbl_name='quest_context_texts' AND l.tbl_column_name='text' AND l.idx=t.id " +
            "WHERE t.quest_context_id=$quest AND t.quest_context_text_kind_id=$kind LIMIT 1";
        cmd.Parameters.AddWithValue("$quest", questType);
        cmd.Parameters.AddWithValue("$kind", kind);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private double ObjectiveTarget(int questType)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT a.act_detail_type,a.act_detail_id FROM quest_components c " +
            "JOIN quest_acts a ON a.quest_component_id=c.id AND a.enable<>'f' " +
            "WHERE c.quest_context_id=$quest AND c.component_kind_id=4 ORDER BY c.id,a.id LIMIT 1";
        cmd.Parameters.AddWithValue("$quest", questType);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return 0d;
        var type = r.GetString(0);
        var detail = r.GetInt64(1);
        var (table, column) = type switch
        {
            "QuestActObjItemGather" => ("quest_act_obj_item_gathers", "count"),
            "QuestActObjItemGroupGather" => ("quest_act_obj_item_group_gathers", "count"),
            "QuestActObjItemUse" => ("quest_act_obj_item_uses", "count"),
            "QuestActObjItemGroupUse" => ("quest_act_obj_item_group_uses", "count"),
            "QuestActObjMonsterHunt" => ("quest_act_obj_monster_hunts", "count"),
            "QuestActObjMonsterGroupHunt" => ("quest_act_obj_monster_group_hunts", "count"),
            "QuestActObjMonsterContrHunt" => ("quest_act_obj_monster_contr_hunts", "count"),
            "QuestActObjMonsterContrGroupHunt" => ("quest_act_obj_monster_contr_group_hunts", "count"),
            "QuestActObjZoneKill" => ("quest_act_obj_zone_kills", "count_pk+count_npc"),
            "QuestActObjZoneMonsterHunt" => ("quest_act_obj_zone_monster_hunts", "count"),
            "QuestActObjZoneQuestComplete" => ("quest_act_obj_zone_quest_completes", "count"),
            "QuestActObjInteraction" => ("quest_act_obj_interactions", "count"),
            "QuestActObjCraft" => ("quest_act_obj_crafts", "count"),
            "QuestActObjTalk" => ("quest_act_obj_talks", "count"),
            "QuestActObjSphere" => ("quest_act_obj_spheres", "count"),
            _ => ("", ""),
        };
        if (table.Length == 0) return 1d;
        using var target = db.CreateCommand();
        target.CommandText = $"SELECT {column} FROM {table} WHERE id=$id";
        target.Parameters.AddWithValue("$id", detail);
        try { return Math.Max(1d, Convert.ToDouble(target.ExecuteScalar() ?? 1)); }
        catch (SqliteException) { return 1d; }
    }

    private X2ApiArrayData AcceptBubbles(int questType)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
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
        cmd.Parameters.AddWithValue("$quest", questType);
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

    private object? SupplyActability(int questType)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT g.id,COALESCE(NULLIF(l.{_localeColumn},''),''),SUM(r.point) FROM quest_components c " +
            "JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type='QuestActSupplyActability' AND a.enable<>'f' " +
            "JOIN quest_act_supply_actabilities r ON r.id=a.act_detail_id JOIN actability_groups g ON g.id=r.actability_group_id " +
            "LEFT JOIN localized_texts l ON l.tbl_name='actability_groups' AND l.tbl_column_name='name' AND l.idx=g.id " +
            "WHERE c.quest_context_id=$quest AND c.component_kind_id=8 GROUP BY g.id ORDER BY a.id LIMIT 1";
        cmd.Parameters.AddWithValue("$quest", questType);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new X2ApiTableData(new Dictionary<string, object?>
        { ["type"] = (double)r.GetInt32(0), ["name"] = r.GetString(1), ["point"] = (double)r.GetInt64(2) }) : null;
    }

    private object SupplyResidentPoint(int questType)
    {
        using var db = new SqliteConnection(_connectionString);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT r.zone_group_id,SUM(r.point) FROM quest_components c " +
            "JOIN quest_acts a ON a.quest_component_id=c.id AND a.act_detail_type='QuestActSupplyResidentPoint' AND a.enable<>'f' " +
            "JOIN quest_act_supply_resident_points r ON r.id=a.act_detail_id " +
            "WHERE c.quest_context_id=$quest AND c.component_kind_id=8 GROUP BY r.zone_group_id ORDER BY a.id LIMIT 1";
        cmd.Parameters.AddWithValue("$quest", questType);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new X2ApiMultiData([(double)r.GetInt32(0), (double)r.GetInt64(1)]) :
            new X2ApiMultiData([0d, 0d]);
    }
}

/// <summary>Installs all 266 bindings advertised for the seven Quest-family tables by X2ApiData.g.cs.</summary>
public static class X2QuestApi
{
    private readonly record struct Binding(string Name, char Kind);

    private static readonly IReadOnlyDictionary<string, string> Specs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["X2Achievement"] = """
            AddTracingAchievement:b GetAchievementInfo:t GetAchievementMainList:t GetAchievementName:s GetAchievementSubList:t GetAchievementTracingList:t GetCategories:t GetCategoryCount:n GetSubcategoryInfo:t GetTodayAssignmentAllAcceptState:b GetTodayAssignmentCount:n GetTodayAssignmentGoal:t GetTodayAssignmentInfo:t GetTodayAssignmentInfoForChange:t GetTodayAssignmentResetCount:n GetTodayAssignmentStatus:v HandleClickTodayAssignment:v IsPossibleTodayAssignmentAllAccept:b IsTodayAssignmentQuest:b IsTracingAchievement:b RemoveTracingAchievement:v RequestTodayAssignmentAllAccept:v ResetTodayAssignment:b
            """,
        ["X2Activity"] = """
            ClaimAllRewards:v ClaimPointReward:v ClaimReward:v ClaimTaskReward:v EnterActivity:v GetActiveActivityIds:t GetActivityInfo:t GetFirstSailingActivityId:n GetPointRewards:t GetStages:t GetTasks:t HasClaimableReward:b HasSailingActivity:b IsActivityQuest:b IsHistoryTaskMet:b LeaveActivity:v RequestData:v
            """,
        ["X2ArchePass"] = """
            BuyPass:v GetArchePassInfo:t GetArchePassResetWeeklyDay:s GetArchePassRewards:t GetCategories:t GetMissionChangeCount:n GetMissionCompleteCount:n GetMyArchePassInfo:t GetMyArchePassReward:v GetMyArchePassRewards:t GetStatus:n IsCompleted:b IsFull:b IsPremiumItemTag:b NormalComplete:v RemovePass:v ResetTodayAssignment:b StartPass:v UpgradePremium:v
            """,
        ["X2EventCenter"] = """
            AddAttendance:v CheckAttendable:b GetAttendanceRewardInfos:t GetAttendedDayCount:n GetGameEventInfo:t GetGameEventInfoCount:n GetGameEventInfoTitleList:t RequestGameEventInfo:v
            """,
        ["X2Quest"] = """
            AcceptBubbleText:v AcceptDirectingQuest:v BuyChronicleInfo:v CallQuestSelectByNpc:v CallQuestTalk:v CallQuestUi:v CompleteDirectingQuest:v DeclineDirectingQuest:v EnterQuestDirectingMode:v GetActiveQuestComparedLevel:n GetActiveQuestComparedLevelByType:v GetActiveQuestContextConditionMessage:s GetActiveQuestLevel:n GetActiveQuestLevelByType:v GetActiveQuestListCount:n GetActiveQuestListName:s GetActiveQuestListObjectiveCount:n GetActiveQuestListObjectiveText:s GetActiveQuestListStatus:v GetActiveQuestListStatusByType:v GetActiveQuestObjectiveText:s GetActiveQuestTitle:v GetActiveQuestType:v GetChronicleInfoByMainKey:v GetChronicleInfoBySubTableKey:v GetChronicleInfoSagaGroupType:v GetChronicleMainKeyList:t GetChronicleNotifierCheckStatus:v GetChronicleNotifierDecalIndex:n GetChronicleNotifierQuest:v GetChronicleSubTableList:t GetFirstObjectiveCount:n GetItemSkillTargetStrByType:v GetLastQuest:n GetLinkedQuestObjectives:v GetLinkedQuestSummary:v GetMainQuestListCount:n GetMainQuestType:v GetMainQuestVecIndex:n GetMaxLimitCountInfo:t GetNpcQuestContextCountComplete:n GetNpcQuestContextCountProgress:n GetNpcQuestContextCountStart:n GetNpcQuestContextCountTalk:n GetNpcQuestContextQuestTypeComplete:n GetNpcQuestContextQuestTypeProgress:v GetNpcQuestContextQuestTypeStart:v GetNpcQuestContextQuestTypeTalk:v GetObjective:v GetObjectiveComponentCount:n GetProgressSagaQuestGroupList:t GetQuestAcceptName:s GetQuestAcceptZoneName:s GetQuestCategoryName:s GetQuestCategoryNameByQuestType:v GetQuestCategoryTextByType:v GetQuestCategoryTypeByQuestType:v GetQuestContextAcceptText:s GetQuestContextBody:v GetQuestContextGrade:n GetQuestContextItemTooltip:v GetQuestContextLeftTime:n GetQuestContextMainTitle:v GetQuestContextObjectiveCount:n GetQuestContextQuestChapterIdxByType:v GetQuestContextQuestIdxByType:v GetQuestContextQuestIsHideChapterIdxByType:v GetQuestContextReadySummary:v GetQuestContextReportText:s GetQuestContextRewardAAPoint:n GetQuestContextRewardAppellation:v GetQuestContextRewardCopper:v GetQuestContextRewardExp:v GetQuestContextRewardItemAllCount:n GetQuestContextRewardItemCount:n GetQuestContextRewardItemIconType:v GetQuestContextRewardItemName:s GetQuestContextRewardSelectiveItemAllCount:n GetQuestContextRewardSelectiveItemCount:n GetQuestContextRewardSelectiveItemIconType:v GetQuestContextRewardSelectiveItemName:s GetQuestContextRewardSummary:v GetQuestContextSelectiveItemTooltip:v GetQuestContextSummary:v GetQuestDetail:v GetQuestDirInfo:t GetQuestJournalBodyCount:n GetQuestJournalBodyText:s GetQuestJournalObjectiveCount:n GetQuestJournalObjectiveCountByType:v GetQuestJournalObjectiveText:s GetQuestJournalObjectiveTextByType:v GetQuestJournalProgTitle:v GetQuestJournalProgTitleByType:v GetQuestJournalSubTitle:v GetQuestJournalSubTitleByType:v GetQuestJournalTitle:v GetQuestLinkText:s GetQuestName:s GetQuestNotifierLimit:n GetQuestObjectiveText:s GetQuestRelatedNpcs:v GetQuestReportNpcNameByQuestType:v GetQuestReportNpcTypeByQuestType:v GetRequireInfoByPurchase:v GetScoreQuestCurrentScore:v GetScoreQuestDoneScore:v GetScoreQuestObjective:v GetTodayQuestInfo:t GetTrackingActiveQuest:n GetUseTypeQuestItems:v GetUseTypeQuestItemsByObjIndex:n GetZoneQuestVecIndex:n IsAllCompleteMainQuest:b IsChapterDone:b IsChronicleNotifierDecalFull:b IsCompleted:b IsDailyQuest:b IsExistChronicleInfo:b IsExistChronicleNotifier:b IsExistCinema:b IsExistCompleteQuestList:b IsGroupQuest:b IsHiddenQuest:b IsLetItDoneQuest:b IsLetItDoneQuestByType:b IsLivelihoodQuest:b IsMainQuest:b IsNextQuestAcceptableForDirecting:b IsOverDoneQuest:b IsOverDoneQuestByType:b IsOverProgressQuestByType:b IsProgressQuestInJournal:b IsPublicQuest:b IsQuestDirectingMode:b IsQuestMultiSelectState:b IsQuestStartItem:b IsReadyForCompleteQuest:b IsReadyQuestInJournal:b IsRepeatableQuest:b IsSagaQuest:b IsScoreQuest:b IsSelectiveQuest:b IsTodayQuest:b IsUseItemInActiveQuest:b IsWeeklyQuest:b LeaveQuestDirectingMode:v NowIsAggroComponent:v NumAcceptBubble:v ProgressBubbleText:v ProgressTalkDirectingQuest:v QuestContextDrop:v QuestContextRestart:v ReadyBubbleText:v RewardActability:v RewardArchePassPoint:v RewardContributionPoint:v RewardCrimePoint:v RewardExpeditionExp:v RewardFamilyExp:v RewardHonorPoint:v RewardLaborPower:v RewardLeadershipPoint:v RewardLivingPoint:v RewardLocalLaborPower:v RewardResidentPoint:v SetChronicleNotifierCheckStatus:v SetTrackingActiveQuest:v ToggleChronicleNotifier:v TryCompleteQuestContext:v TryProgressTalkQuestComponent:v TrySelectQuestContextToDoodad:v TrySelectQuestContextToNpc:v TryStartQuestContext:v UpdateChronicleNotifier:v
            """,
        ["X2Trial"] = """
            CancelTrial:v ChooseVerdict:v ConfirmCrimeRecords:v GetBadUserRecordsByPage:t GetClientBadUserList:b GetCrimeData:t GetCrimeRecords:v GetCrimeRecordsByPage:t GetDailyReportBadUser:n GetDailyReportBadUserMaxCount:n GetReciveBadUserListCount:n GetTrialStatus:t GetTrialType:n GetTrialVerdictRemainTime:n ReloadUI:v ReportBadUser:v ReportBadWordUser:v ReportCrime:v RequestBadUserList:v RequestJuryWaitingNumber:v SetCrimeRecordsCountPerPage:v StartReportBadUserUI:v
            """,
        ["X2Tutorial"] = "GetUiAviTable:t SetDoneTutorial:v",
    };

    // These bindings initiate state changes. All others are queries, including the oddly named Reward* accessors.
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "X2Achievement.AddTracingAchievement", "X2Achievement.HandleClickTodayAssignment", "X2Achievement.RemoveTracingAchievement", "X2Achievement.RequestTodayAssignmentAllAccept", "X2Achievement.ResetTodayAssignment",
        "X2Activity.ClaimAllRewards", "X2Activity.ClaimPointReward", "X2Activity.ClaimReward", "X2Activity.ClaimTaskReward", "X2Activity.EnterActivity", "X2Activity.LeaveActivity", "X2Activity.RequestData",
        "X2ArchePass.BuyPass", "X2ArchePass.GetMyArchePassReward", "X2ArchePass.NormalComplete", "X2ArchePass.RemovePass", "X2ArchePass.ResetTodayAssignment", "X2ArchePass.StartPass", "X2ArchePass.UpgradePremium",
        "X2EventCenter.AddAttendance", "X2EventCenter.RequestGameEventInfo",
        "X2Quest.AcceptDirectingQuest", "X2Quest.BuyChronicleInfo", "X2Quest.CallQuestSelectByNpc", "X2Quest.CallQuestTalk", "X2Quest.CallQuestUi", "X2Quest.CompleteDirectingQuest", "X2Quest.DeclineDirectingQuest", "X2Quest.EnterQuestDirectingMode", "X2Quest.LeaveQuestDirectingMode", "X2Quest.ProgressTalkDirectingQuest", "X2Quest.QuestContextDrop", "X2Quest.QuestContextRestart", "X2Quest.SetChronicleNotifierCheckStatus", "X2Quest.SetTrackingActiveQuest", "X2Quest.ToggleChronicleNotifier", "X2Quest.TryCompleteQuestContext", "X2Quest.TryProgressTalkQuestComponent", "X2Quest.TrySelectQuestContextToDoodad", "X2Quest.TrySelectQuestContextToNpc", "X2Quest.TryStartQuestContext", "X2Quest.UpdateChronicleNotifier",
        "X2Trial.CancelTrial", "X2Trial.ChooseVerdict", "X2Trial.ConfirmCrimeRecords", "X2Trial.GetCrimeRecords", "X2Trial.ReloadUI", "X2Trial.ReportBadUser", "X2Trial.ReportBadWordUser", "X2Trial.ReportCrime", "X2Trial.RequestBadUserList", "X2Trial.RequestJuryWaitingNumber", "X2Trial.SetCrimeRecordsCountPerPage", "X2Trial.StartReportBadUserUI",
        "X2Tutorial.SetDoneTutorial",
    };

    public static void Install(X2LuaHost host, X2GameContext context, IX2QuestData data)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);
        // Quest names/categories live in localized quest tables rather than ui_texts. The configured IX2QuestData
        // (including NullQuestData's optional read-only database adapter) owns those lookups; context remains part of
        // the uniform family install signature and supplies the event sink used by the host.
        _ = context;

        foreach (var (table, encoded) in Specs)
        {
            foreach (var binding in Parse(encoded))
            {
                var capturedTable = table;
                var captured = binding;
                host.Define(capturedTable, captured.Name, args => Invoke(context, data, capturedTable, captured, args));
            }
        }
    }

    private static object? Invoke(X2GameContext context, IX2QuestData data, string table, Binding binding, LuaArgs args)
    {
        var key = table + "." + binding.Name;
        if (Commands.Contains(key))
        {
            if (data.TryExecute(new X2QuestCommand(table, binding.Name, args.Values.ToArray()), out var result))
                return ToLua(result);
            return Empty(binding.Kind);
        }

        if (data.TryQuery(table, binding.Name, args.Values, out var value))
            return ToLua(value);

        // Features absent from the session (cash chronicle purchase, GM helpers, inactive trials, and similar) use
        // the same false/zero/empty/nil values exposed by an unavailable real-client subsystem.
        return Empty(table, binding);
    }

    private static object? Empty(string table, Binding binding) => (table, binding.Name) switch
    {
        // Lua call sites prove these have additional returns not represented in X2ApiData.g.cs.
        ("X2Achievement", "GetTodayAssignmentResetCount") => new LuaMulti(0d, 0d),
        ("X2Achievement", "GetTodayAssignmentStatus") => new LuaMulti(0d, 0d),
        ("X2ArchePass", "GetMissionChangeCount") => new LuaMulti(0d, 0d),
        ("X2ArchePass", "GetMissionCompleteCount") => new LuaMulti(0d, 0d),
        ("X2EventCenter", "GetAttendedDayCount") => new LuaMulti(0d, 0d),
        ("X2Quest", "IsDailyQuest") => new LuaMulti(false, null),
        // no active pass is nil (arche_pass_info.lua tests for nil before reading the fields)
        ("X2ArchePass", "GetMyArchePassInfo") => null,
        ("X2ArchePass", "GetMyArchePassRewards") => null,
        // the chronicle book reads both lists' lengths
        ("X2Quest", "GetChronicleMainKeyList") => new LuaTable { ["active"] = new LuaTable(), ["complete"] = new LuaTable() },
        ("X2Quest", "IsWeeklyQuest") => new LuaMulti(false, null),
        ("X2Quest", "IsNextQuestAcceptableForDirecting") => new LuaMulti(false, 0d),
        // The notifier measures these as Lua arrays even when the objective has no usable quest item.
        ("X2Quest", "GetUseTypeQuestItemsByObjIndex") => new LuaTable(),
        ("X2Quest", "GetUseTypeQuestItems") => new LuaTable(),
        // questcontext/common.lua reads the limits once when the world scripts load, before the live quest data is attached;
        // zero there blocks every quest from the notifier for the whole session (InsertAndSortQuestNotifyList). Use the
        // same limits as the live adapter (X2ProtocolQuestData): 10 quests, 5 today quests.
        ("X2Quest", "GetQuestNotifierLimit") => new LuaMulti(10d, 5d),
        _ => Empty(binding.Kind),
    };

    private static object? Empty(char kind) => kind switch
    {
        'n' => 0d,
        'b' => false,
        's' => "",
        't' => new LuaTable(),
        _ => null,
    };

    private static object? ToLua(object? value) => value switch
    {
        null => null,
        X2ApiTableData table => ToLuaTable(table.Fields),
        X2ApiArrayData array => ToLuaArray(array.Items),
        X2ApiMultiData multi => new LuaMulti(multi.Values.Select(ToLua).ToArray()),
        IReadOnlyDictionary<string, object?> fields => ToLuaTable(fields),
        string or bool or double or float or int or uint or long or ulong => value,
        IEnumerable sequence => ToLuaArray(sequence.Cast<object?>().ToArray()),
        _ => value,
    };

    private static LuaTable ToLuaTable(IReadOnlyDictionary<string, object?> fields)
    {
        var table = new LuaTable();
        foreach (var (key, value) in fields) table[key] = ToLua(value);
        return table;
    }

    private static LuaTable ToLuaArray(IReadOnlyList<object?> items)
    {
        var table = new LuaTable();
        for (var i = 0; i < items.Count; i++) table[(double)(i + 1)] = ToLua(items[i]);
        return table;
    }

    private static IEnumerable<Binding> Parse(string encoded)
    {
        foreach (var token in encoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = token.LastIndexOf(':');
            yield return new Binding(token[..colon], token[colon + 1]);
        }
    }
}

/// <summary>UI events consumed by Quest-family x2ui packages. Argument lists come from the Lua handlers.</summary>
public static class X2QuestEvents
{
    /// <summary>(questType)</summary> public const string StartQuestContext = "START_QUEST_CONTEXT";
    /// <summary>(questType, useDirectingMode, npcId)</summary> public const string StartQuestContextNpc = "START_QUEST_CONTEXT_NPC";
    /// <summary>(questType, useDirectingMode, doodadId)</summary> public const string StartQuestContextDoodad = "START_QUEST_CONTEXT_DOODAD";
    /// <summary>(questType, sphereType)</summary> public const string StartQuestContextSphere = "START_QUEST_CONTEXT_SPHERE";
    /// <summary>(doodadId)</summary> public const string StartTalkQuestContext = "START_TALK_QUEST_CONTEXT";
    /// <summary>(questType, useDirectingMode, npcId)</summary> public const string CompleteQuestContextNpc = "COMPLETE_QUEST_CONTEXT_NPC";
    /// <summary>(questType, useDirectingMode, doodadId)</summary> public const string CompleteQuestContextDoodad = "COMPLETE_QUEST_CONTEXT_DOODAD";
    /// <summary>(questType, useDirectingMode, npcId, doodadId)</summary> public const string ProgressTalkQuestContext = "PROGRESS_TALK_QUEST_CONTEXT";
    /// <summary>(targetNpc, questType, useDirectingMode, targetId, interactionValue)</summary>
    public const string MultiQuestContextSelect = "MULTI_QUEST_CONTEXT_SELECT";
    /// <summary>(questList table)</summary>
    public const string MultiQuestContextSelectList = "MULTI_QUEST_CONTEXT_SELECT_LIST";
    /// <summary>(questType, status string)</summary> public const string QuestContextUpdated = "QUEST_CONTEXT_UPDATED";
    /// <summary>(objectiveText)</summary> public const string QuestContextObjectiveEvent = "QUEST_CONTEXT_OBJECTIVE_EVENT";
    /// <summary>(objectiveText, condition)</summary> public const string QuestContextConditionEvent = "QUEST_CONTEXT_CONDITION_EVENT";
    /// <summary>(errorNumber, questType, detail?, isCommon?)</summary> public const string QuestErrorInfo = "QUEST_ERROR_INFO";
    /// <summary>(questType)</summary> public const string QuestQuickCloseEvent = "QUEST_QUICK_CLOSE_EVENT";
    /// <summary>(message, author, authorId, self, tailType, showTime, fadeTime, bubbleType, questType, forceFinished)</summary>
    public const string ChatMsgQuest = "CHAT_MSG_QUEST";
    /// <summary>(hot-key index)</summary> public const string QuestDirectingModeHotKey = "QUEST_DIRECTING_MODE_HOT_KEY";
    /// <summary>No arguments.</summary> public const string QuestDirectingModeEnd = "QUEST_DIRECTING_MODE_END";
    /// <summary>(action string; handlers use "owner_changed")</summary> public const string Dominion = "DOMINION";
    /// <summary>(unused unit argument, unit string id)</summary> public const string LevelChanged = "LEVEL_CHANGED";
    /// <summary>(id, pages table)</summary> public const string TutorialEvent = "TUTORIAL_EVENT";
    /// <summary>No arguments.</summary> public const string TutorialHideFromOption = "TUTORIAL_HIDE_FROM_OPTION";
    /// <summary>(point)</summary> public const string ArchePassUpdatePoint = "ARCHE_PASS_UPDATE_POINT";
    /// <summary>(tier)</summary> public const string ArchePassUpdateTier = "ARCHE_PASS_UPDATE_TIER";
    /// <summary>(complete boolean)</summary> public const string ArchePassUpdateRewardItem = "ARCHE_PASS_UPDATE_REWARD_ITEM";
    /// <summary>(passType, allDone boolean in the detail view)</summary> public const string ArchePassCompleted = "ARCHE_PASS_COMPLETED";
    /// <summary>(passType)</summary> public const string ArchePassStarted = "ARCHE_PASS_STARTED";
    /// <summary>(passType)</summary> public const string ArchePassBuy = "ARCHE_PASS_BUY";
    /// <summary>(passType)</summary> public const string ArchePassOwned = "ARCHE_PASS_OWNED";
    /// <summary>(passType)</summary> public const string ArchePassExpired = "ARCHE_PASS_EXPIRED";
    /// <summary>(passType)</summary> public const string ArchePassDropped = "ARCHE_PASS_DROPPED";
    /// <summary>(passType)</summary> public const string ArchePassReseted = "ARCHE_PASS_RESETED";
    /// <summary>No arguments.</summary> public const string ArchePassLoaded = "ARCHE_PASS_LOADED";
    /// <summary>No arguments.</summary> public const string ArchePassMissionChanged = "ARCHE_PASS_MISSION_CHANGED";
    /// <summary>No arguments.</summary> public const string ArchePassMissionCompleted = "ARCHE_PASS_MISSION_COMPLETED";
    /// <summary>No arguments.</summary> public const string ArchePassUpgradePremium = "ARCHE_PASS_UPGRADE_PREMIUM";
    /// <summary>(stepName?)</summary> public const string StartTodayAssignment = "START_TODAY_ASSIGNMENT";
    /// <summary>No arguments.</summary> public const string UpdateTodayAssignment = "UPDATE_TODAY_ASSIGNMENT";
    /// <summary>(remaining reset count)</summary> public const string UpdateTodayAssignmentResetCount = "UPDATE_TODAY_ASSIGNMENT_RESET_COUNT";
    /// <summary>No arguments.</summary>
    public const string AchievementUpdate = "ACHIEVEMENT_UPDATE";
    /// <summary>No arguments.</summary> public const string AccountAttendanceLoaded = "ACCOUNT_ATTENDANCE_LOADED";
    /// <summary>No arguments.</summary> public const string AccountAttendanceAdded = "ACCOUNT_ATTENDANCE_ADDED";
    /// <summary>No arguments.</summary> public const string SurveyFormUpdate = "SURVEY_FORM_UPDATE";
    /// <summary>No arguments.</summary> public const string GameEventInfoRequested = "GAME_EVENT_INFO_REQUESTED";
    /// <summary>No arguments.</summary> public const string GameEventInfoListUpdated = "GAME_EVENT_INFO_LIST_UPDATED";
    /// <summary>No arguments.</summary> public const string GameEventEmpty = "GAME_EVENT_EMPTY";
    /// <summary>(state, currentJury, remainingTime, maximumWaitTime) in trial_status.lua; (state, juryCount, remainingTime) in crime_records.lua.</summary> public const string TrialStatus = "TRIAL_STATUS";
    /// <summary>(state, remaining-time table)</summary> public const string TrialTimer = "TRIAL_TIMER";
    /// <summary>(count, total)</summary> public const string JuryOkCount = "JURY_OK_COUNT";
    /// <summary>No arguments.</summary> public const string TrialClosed = "TRIAL_CLOSED";
    /// <summary>No arguments.</summary> public const string TrialCanceled = "TRIAL_CANCELED";
    /// <summary>(count, total, sentenceType, sentenceTime)</summary> public const string RulingStatus = "RULING_STATUS";
    /// <summary>No arguments.</summary> public const string RulingClosed = "RULING_CLOSED";
    /// <summary>(targetId, targetName, currentBountyMoney, showCenter)</summary> public const string SetBountyDone = "SET_BOUNTY_DONE";
    /// <summary>(trialState)</summary> public const string ShowCrimeRecords = "SHOW_CRIME_RECORDS";
    /// <summary>(p1, p2, p3, p4, p5)</summary> public const string ShowVerdicts = "SHOW_VERDICTS";
    /// <summary>(doodadName, locationName)</summary> public const string ReportCrime = "REPORT_CRIME";
    /// <summary>(doodadId)</summary> public const string ToggleBountyBulletin = "TOGGLE_BOUNTY_BULLETIN";
    /// <summary>(juryCount, total, sentenceTime)</summary> public const string ShowDependantWaitJury = "SHOW_DEPENDANT_WAIT_JURY";
    /// <summary>(order, sentenceTime)</summary> public const string ShowDependantWaitTrial = "SHOW_DEPENDANT_WAIT_TRIAL";
    /// <summary>(factionId)</summary> public const string HeroRankDataRetrieved = "HERO_RANK_DATA_RETRIEVED";
    /// <summary>(factionId)</summary> public const string HeroAllScoreUpdated = "HERO_ALL_SCORE_UPDATED";
    /// <summary>No arguments.</summary> public const string HeroSeasonUpdated = "HERO_SEASON_UPDATED";
    /// <summary>(show boolean)</summary> public const string HeroElection = "HERO_ELECTION";
    /// <summary>(info table or nil)</summary> public const string UpdateChronicleInfo = "UPDATE_CHRONICLE_INFO";
    /// <summary>(initial?, mainKey?)</summary> public const string UpdateChronicleNotifier = "UPDATE_CHRONICLE_NOTIFIER";
    /// <summary>No arguments.</summary> public const string InitChronicleInfo = "INIT_CHRONICLE_INFO";
    /// <summary>No arguments.</summary> public const string QuestNotifierStart = "QUEST_NOTIFIER_START";
    /// <summary>No arguments.</summary> public const string EnteredWorld = "ENTERED_WORLD";
    /// <summary>No arguments.</summary> public const string LeftLoading = "LEFT_LOADING";
    /// <summary>No arguments.</summary> public const string UiReloaded = "UI_RELOADED";
    /// <summary>No arguments.</summary> public const string UiPermissionUpdate = "UI_PERMISSION_UPDATE";
    /// <summary>No arguments.</summary> public const string InteractionEnd = "INTERACTION_END";
    /// <summary>No arguments.</summary> public const string NpcInteractionEnd = "NPC_INTERACTION_END";
    /// <summary>(equipment slot)</summary> public const string UnitEquipmentChanged = "UNIT_EQUIPMENT_CHANGED";
    /// <summary>No arguments.</summary> public const string AddedItem = "ADDED_ITEM";
    /// <summary>No arguments.</summary> public const string RemovedItem = "REMOVED_ITEM";
    /// <summary>No arguments.</summary> public const string BadUserListUpdate = "BAD_USER_LIST_UPDATE";
    /// <summary>No arguments.</summary> public const string QuestTaskReady = "QUEST_TASK_READY";
    /// <summary>No arguments.</summary> public const string QuestHiddenReady = "QUEST_HIDDEN_READY";
    /// <summary>No arguments.</summary> public const string QuestHiddenComplete = "QUEST_HIDDEN_COMPLETE";
    /// <summary>No arguments.</summary> public const string QuestLeftTimeUpdated = "QUEST_LEFT_TIME_UPDATED";
    /// <summary>No arguments.</summary> public const string EndQuestChatBubble = "END_QUEST_CHAT_BUBBLE";
    /// <summary>No arguments.</summary> public const string FolderStateChanged = "FOLDER_STATE_CHANGED";
    /// <summary>No arguments.</summary> public const string LeavedInstantGameZone = "LEAVED_INSTANT_GAME_ZONE";
    /// <summary>No arguments.</summary> public const string InstantGameStart = "INSTANT_GAME_START";
}
