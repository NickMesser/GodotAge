#nullable enable
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Client achievement catalogue; no completion is inferred from static game data.</summary>
internal static class X2AssignmentAchievementData
{
    public static bool TryQuery(string? connection, string method, IReadOnlyList<object?> args, out object? value)
    {
        value = null;
        if (method is "GetTodayAssignmentAllAcceptState" or "IsPossibleTodayAssignmentAllAccept" or
            "IsTodayAssignmentQuest" or "IsTracingAchievement")
        { value = false; return true; }
        if (method is "GetTodayAssignmentResetCount" or "GetTodayAssignmentStatus")
        { value = new X2ApiMultiData([0d, 0d]); return true; }
        if (method == "GetAchievementTracingList")
        { value = new X2ApiArrayData([]); return true; }
        if (method == "GetTodayAssignmentGoal")
        { value = new X2ApiTableData(new Dictionary<string, object?>()); return true; }
        if (method == "GetCategoryCount")
            value = new X2ApiMultiData([0d, 0d]);
        if (method is "GetAchievementTracingList" or "GetAchievementSubList" or "GetAchievementMainList" or "GetCategories")
            value = new X2ApiArrayData([]);
        if (connection is null) return value is not null;
        try
        {
            using var db = new SqliteConnection(connection);
            db.Open();
            var first = args.Count > 0 ? Num(args[0]) : 0;
            var second = args.Count > 1 ? Num(args[1]) : 0;
            switch (method)
            {
                case "GetTodayAssignmentCount": value = TodayCount(db, first); break;
                case "GetTodayAssignmentInfo":
                case "GetTodayAssignmentInfoForChange": value = TodayInfo(db, first, second); break;
                case "GetCategories": value = new X2ApiArrayData(Categories(db, first)); break;
                case "GetAchievementMainList": value = new X2ApiArrayData(args.Count > 2 && Num(args[2]) == 2
                    ? [] : AchievementIds(db, first, second, 0)); break;
                case "GetAchievementSubList": value = new X2ApiArrayData(args.Count > 1 && Num(args[1]) == 2 ? [] :
                    AchievementIds(db, 0, 0, first).Select(id => (object?)new X2ApiTableData(
                        new Dictionary<string, object?> { ["key"] = id })).ToArray()); break;
                case "GetSubcategoryInfo": value = Subcategory(db, first); break;
                case "GetAchievementInfo": value = Achievement(db, first); break;
                case "GetAchievementName": value = Text(db, "achievements", "name", first); break;
                case "GetCategoryCount":
                {
                    var category = second;
                    var subcategory = args.Count > 2 ? Num(args[2]) : 0;
                    var kind = first;
                    using var c = db.CreateCommand();
                    c.CommandText = "SELECT count(*) FROM achievements a JOIN achievement_sub_categories s ON s.id=a.achievement_sub_category_id " +
                        "JOIN achievement_categories c ON c.id=s.achievement_category_id WHERE c.achievement_kind_id=$kind " +
                        "AND ($cat=0 OR c.id=$cat) AND ($sub=0 OR s.id=$sub) AND COALESCE(a.is_hidden,'f') NOT IN ('t','1')";
                    c.Parameters.AddWithValue("$kind", kind);
                    c.Parameters.AddWithValue("$cat", category);
                    c.Parameters.AddWithValue("$sub", subcategory);
                    var filter = args.Count > 3 ? Num(args[3]) : 1;
                    value = new X2ApiMultiData([0d, filter is 2 or 4 ? 0d : Convert.ToDouble(c.ExecuteScalar() ?? 0)]);
                    break;
                }
            }
            return value is not null;
        }
        catch (SqliteException) { return value is not null; }
    }

    private static IReadOnlyList<object?> Categories(SqliteConnection db, int kind)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM achievement_categories WHERE achievement_kind_id=$kind ORDER BY id";
        cmd.Parameters.AddWithValue("$kind", kind);
        var ids = new List<int>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt32(0));
        var result = new List<object?>();
        foreach (var id in ids)
        {
            using var sub = db.CreateCommand();
            sub.CommandText = "SELECT id FROM achievement_sub_categories WHERE achievement_category_id=$id ORDER BY id";
            sub.Parameters.AddWithValue("$id", id);
            var subIds = new List<int>();
            using (var r = sub.ExecuteReader()) while (r.Read()) subIds.Add(r.GetInt32(0));
            if (kind == 1)
            {
                foreach (var s in subIds)
                    result.Add(new X2ApiTableData(new Dictionary<string, object?>
                    { ["subCategoryType"] = (double)s, ["name"] = Text(db, "achievement_sub_categories", "name", s),
                      ["isHeirLevelCategory"] = false }));
                continue;
            }
            result.Add(new X2ApiTableData(new Dictionary<string, object?>
            {
                ["categoryType"] = (double)id, ["name"] = Text(db, "achievement_categories", "name", id),
                ["subCategories"] = new X2ApiArrayData(subIds.Select(s => (object?)new X2ApiTableData(new Dictionary<string, object?>
                { ["subCategoryType"] = (double)s, ["name"] = Text(db, "achievement_sub_categories", "name", s),
                  ["isHeirLevelCategory"] = false })).ToArray())
            }));
        }
        return result;
    }

    private static IReadOnlyList<object?> AchievementIds(SqliteConnection db, int kind, int sub, int parent)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = parent > 0
            ? "SELECT id FROM achievements WHERE parent_achievement_id=$parent AND COALESCE(is_hidden,'f') NOT IN ('t','1') ORDER BY priority,id"
            : "SELECT a.id FROM achievements a JOIN achievement_sub_categories s ON s.id=a.achievement_sub_category_id " +
              "JOIN achievement_categories c ON c.id=s.achievement_category_id WHERE c.achievement_kind_id=$kind " +
              "AND ($sub=0 OR s.id=$sub OR ($sub>=1000 AND c.id=$sub-1000)) " +
              "AND COALESCE(a.parent_achievement_id,0)=0 AND COALESCE(a.is_hidden,'f') NOT IN ('t','1') ORDER BY a.priority,a.id";
        cmd.Parameters.AddWithValue("$parent", parent);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$sub", sub);
        var ids = new List<object?>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add((double)r.GetInt32(0));
        return ids;
    }

    private static object? Subcategory(SqliteConnection db, int id)
    {
        if (id >= 1000)
        {
            var categoryId = id - 1000;
            return new X2ApiTableData(new Dictionary<string, object?>
            { ["name"] = Text(db, "achievement_categories", "name", categoryId), ["desc"] = "",
              ["completedCount"] = 0d, ["totalCount"] = CountInCategory(db, categoryId),
              ["isHeirLevelCategory"] = false, ["rewardAchievementType"] = null });
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT desc,reward_achievement_id FROM achievement_sub_categories WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var reward = r.IsDBNull(1) ? 0 : r.GetInt32(1);
        var desc = r.IsDBNull(0) ? "" : r.GetString(0);
        return new X2ApiTableData(new Dictionary<string, object?>
        { ["name"] = Text(db, "achievement_sub_categories", "name", id), ["desc"] = Text(db, "achievement_sub_categories", "desc", id, desc),
          ["completedCount"] = 0d, ["totalCount"] = CountInSubcategory(db, id),
          ["isHeirLevelCategory"] = false, ["rewardAchievementType"] = reward > 0 ? (double)reward : null });
    }

    private static object? Achievement(SqliteConnection db, int id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT a.summary,a.description,a.complete_num,a.icon_id,a.achievement_sub_category_id,a.item_id,a.item_num,a.appellation_id,c.achievement_kind_id " +
            "FROM achievements a JOIN achievement_sub_categories s ON s.id=a.achievement_sub_category_id " +
            "JOIN achievement_categories c ON c.id=s.achievement_category_id WHERE a.id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var summary = r.IsDBNull(0) ? "" : r.GetString(0);
        var description = r.IsDBNull(1) ? "" : r.GetString(1);
        var count = r.IsDBNull(2) ? 1 : Math.Max(1, r.GetInt32(2));
        var icon = r.IsDBNull(3) ? 0 : r.GetInt32(3);
        var sub = r.IsDBNull(4) ? 0 : r.GetInt32(4);
        var item = r.IsDBNull(5) ? 0 : r.GetInt32(5);
        var amount = r.IsDBNull(6) ? 0 : r.GetInt32(6);
        var title = r.IsDBNull(7) ? 0 : r.GetInt32(7);
        var reward = new Dictionary<string, object?>();
        if (item > 0) reward["item"] = new X2ApiTableData(new Dictionary<string, object?>
            { ["itemType"] = (double)item, ["count"] = (double)amount });
        if (title > 0) reward["appellation"] = new X2ApiTableData(new Dictionary<string, object?>
            { ["name"] = Text(db, "appellations", "name", title), ["iconPath"] = "" });
        var childCount = AchievementIds(db, 0, 0, id).Count;
        var fields = new Dictionary<string, object?>
        {
            ["type"] = (double)id, ["achievementKind"] = (double)r.GetInt32(8), ["subCategoryType"] = (double)sub,
            ["name"] = Text(db, "achievements", "name", id),
            ["summary"] = Text(db, "achievements", "summary", id, summary),
            ["desc"] = Text(db, "achievements", "description", id, description),
            ["iconPath"] = Icon(db, icon), ["complete"] = false,
            ["completeNum"] = (double)(count == 1 ? Math.Max(count, childCount) : count), ["current"] = 0d, ["canProgress"] = true,
            ["tracing"] = false, ["reward"] = reward.Count > 0 ? new X2ApiTableData(reward) : null,
            ["objectives"] = Objectives(db, id),
        };
        if (childCount > 0)
        {
            fields["totalSubCount"] = (double)childCount;
            fields["completeSubCount"] = 0d;
        }
        return new X2ApiTableData(fields);
    }

    private static X2ApiArrayData Objectives(SqliteConnection db, int achievementId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT o.id,o.record_id,COALESCE(r.kind_id,0),COALESCE(r.value1,0),COALESCE(r.value2,0) " +
            "FROM achievement_objectives o LEFT JOIN char_records r ON r.id=o.record_id " +
            "WHERE o.achievement_id=$id ORDER BY o.id";
        cmd.Parameters.AddWithValue("$id", achievementId);
        var rows = new List<object?>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(new X2ApiTableData(new Dictionary<string, object?>
        { ["id"] = (double)reader.GetInt32(0), ["recordId"] = (double)reader.GetInt32(1),
          ["kindId"] = (double)reader.GetInt32(2), ["value1"] = (double)reader.GetInt32(3),
          ["value2"] = (double)reader.GetInt32(4), ["current"] = 0d }));
        return new X2ApiArrayData(rows);
    }

    private static double TodayCount(SqliteConnection db, int kind)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM today_quest_steps WHERE sort_id=$kind";
        cmd.Parameters.AddWithValue("$kind", kind);
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0);
    }

    private static object? TodayInfo(SqliteConnection db, int kind, int index)
    {
        if (index <= 0) return null;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,real_step,name,description,icon_id,level_min,item_id,item_num FROM today_quest_steps " +
            "WHERE sort_id=$kind ORDER BY real_step,id LIMIT 1 OFFSET $offset";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$offset", index - 1);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var id = r.GetInt32(0);
        var step = r.GetInt32(1);
        var name = r.IsDBNull(2) ? "" : r.GetString(2);
        var description = r.IsDBNull(3) ? "" : r.GetString(3);
        var icon = r.IsDBNull(4) ? 0 : r.GetInt32(4);
        var levelMin = r.GetInt32(5);
        var itemId = r.IsDBNull(6) ? 0 : r.GetInt32(6);
        var itemNum = r.GetInt32(7);
        return new X2ApiTableData(new Dictionary<string, object?>
        { ["stepId"] = (double)id, ["realStep"] = (double)step, ["title"] = Text(db, "today_quest_steps", "name", id, name),
          ["description"] = Text(db, "today_quest_steps", "description", id, description),
          ["desc"] = Text(db, "today_quest_steps", "description", id, description),
          ["iconPath"] = Icon(db, icon), ["questType"] = 0d,
          ["status"] = 0d, ["satisfy"] = false, ["sort"] = (double)kind,
          ["requireLevel"] = 0d, ["levelMin"] = (double)levelMin,
          ["requireItem"] = (double)itemId, ["requireItemCount"] = (double)itemNum });
    }

    private static double CountInSubcategory(SqliteConnection db, int id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM achievements WHERE achievement_sub_category_id=$id AND COALESCE(is_hidden,'f') NOT IN ('t','1')";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0);
    }
    private static double CountInCategory(SqliteConnection db, int id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM achievements a JOIN achievement_sub_categories s ON s.id=a.achievement_sub_category_id " +
            "WHERE s.achievement_category_id=$id AND COALESCE(a.is_hidden,'f') NOT IN ('t','1')";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0);
    }
    private static string Icon(SqliteConnection db, int id)
    {
        if (id <= 0) return "";
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT filename FROM icons WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static string Text(SqliteConnection db, string table, string column, int id, string fallback = "")
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT en_us FROM localized_texts WHERE tbl_name=$table AND tbl_column_name=$column AND idx=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$column", column);
        cmd.Parameters.AddWithValue("$id", id);
        var translated = Convert.ToString(cmd.ExecuteScalar());
        return string.IsNullOrWhiteSpace(translated) ? fallback : translated;
    }
    private static int Num(object? value) => value switch
    { int n => n, uint n => checked((int)n), double n => (int)n, _ => int.TryParse(Convert.ToString(value), out var n) ? n : 0 };
}
