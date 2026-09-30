#nullable enable
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Read-only title catalogue. Ownership and selection always come from the session.</summary>
internal static class X2AssignmentPlayerData
{
    private const int PageSize = 50; // APPELLATION_LIST_PER_PAGE in the client Lua constants.
    internal static string CleanFormatting(string value)
    {
        // Native |nc;...|r and |ni;...|r mark emphasis, not literal text.
        value = Regex.Replace(value, @"\|n[ci];([^|]*)\|r", "$1");
        value = Regex.Replace(value, @"\|c[0-9A-Fa-f]{8}", "");
        return value.Replace("|r", "", StringComparison.Ordinal);
    }
    public static bool TryQuery(string method, IReadOnlyList<object?> args, string? path,
        IReadOnlyDictionary<string, object?> live, out object? value)
    {
        value = null;
        if (method == "GetAppellationMyLevelInfo")
        {
            // The server currently has no appellation experience packet. A level-one record is
            // sufficient for the native UI, including its progress-bar arithmetic.
            value = LevelInfo(path);
            return true;
        }
        if (method is "GetAppellationMyStamp" or "GetAppellationStampInfos" or
            "GetAppellationStampInfo")
        {
            value = method == "GetAppellationMyStamp" ? null : Array.Empty<object>();
            return true;
        }
        if (method == "GetEffectAppellation")
        {
            var selected = live.TryGetValue("GetShowingAppellation", out var raw) && raw is object?[] row && row.Length > 0
                ? ToInt(row[0]) : 0;
            value = selected > 0 ? Appellation(path, selected, true) : EmptyTitle();
            return true;
        }
        if (method == "GetShowingAppellation")
        {
            var selected = live.TryGetValue(method, out var raw) && raw is object?[] row && row.Length > 0
                ? ToInt(row[0]) : 0;
            value = selected > 0 ? Appellation(path, selected, true) : EmptyTitle();
            return true;
        }
        if (method is not ("GetAppellations" or "GetAppellationsCount" or "GetAppellationRouteInfo" or "GetUnitAppellationRouteList" or "GetAppellationBuffInfoByLevels" or
            "GetAppellationStampInfo" or "GetAppellationChangeItemInfo" or "GetStampChangeItemInfo"))
            return false;
        if (method is "GetAppellationStampInfo" or "GetAppellationChangeItemInfo" or "GetStampChangeItemInfo")
            return true;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            value = method == "GetAppellationsCount" ? 1d : method == "GetAppellations" ? new object?[] { EmptyTitle() } :
                method is "GetUnitAppellationRouteList" or "GetAppellationBuffInfoByLevels" ? Array.Empty<object>() : null;
            return true;
        }
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            AAEmu.GodotViewer.Data.ClientEnums.Ensure(db);
            if (method == "GetAppellationBuffInfoByLevels")
            {
                using var buffs = db.CreateCommand();
                buffs.CommandText = "SELECT COALESCE(NULLIF(t.en_us,''),'') FROM appellation_levels l " +
                    "LEFT JOIN localized_texts t ON t.tbl_name='buffs' AND t.tbl_column_name='desc' AND t.idx=l.buff_id ORDER BY l.app_lv";
                var items = new List<object?>();
                using var buffReader = buffs.ExecuteReader();
                while (buffReader.Read()) items.Add(CleanFormatting(buffReader.GetString(0)));
                value = items.ToArray();
                return true;
            }
            if (method == "GetUnitAppellationRouteList")
            {
                using var routes = db.CreateCommand();
                routes.CommandText = "SELECT id,name FROM enum_unit_appellation_routes WHERE id<>4 ORDER BY id";
                var items = new List<object?>();
                using var routeReader = routes.ExecuteReader();
                while (routeReader.Read()) items.Add(new Dictionary<string, object?>
                { ["key"] = (double)routeReader.GetInt32(0), ["value"] = routeReader.GetString(1) });
                value = items.ToArray();
                return true;
            }
            if (method == "GetAppellationRouteInfo")
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT a.route_kind_id,a.route_type,COALESCE(NULLIF(t.en_us,''),''),a.route_popup " +
                    "FROM appellations a LEFT JOIN localized_texts t ON t.tbl_name='appellations' " +
                    "AND t.tbl_column_name='route_desc' AND t.idx=a.id WHERE a.id=$id";
                cmd.Parameters.AddWithValue("$id", args.Count > 0 ? ToInt(args[0]) : 0);
                using var r = cmd.ExecuteReader();
                if (r.Read()) value = new Dictionary<string, object?> { ["kind"] = r.IsDBNull(0) ? 5d : (double)r.GetInt64(0),
                    ["type"] = r.IsDBNull(1) ? 0d : (double)r.GetInt64(1),
                    ["routeDesc"] = r.GetString(2), ["routePopup"] = !r.IsDBNull(3) && IsTrue(r.GetValue(3)) };
                return true;
            }
            var filter = args.Count > 0 ? ToInt(args[0]) : 6;
            var keyword = args.Count > 1 ? Convert.ToString(args[1]) ?? "" : "";
            using var list = db.CreateCommand();
            list.CommandText = "SELECT a.id, COALESCE(NULLIF(t.en_us,''),a.name), COALESCE(a.grade_id,1),NULLIF(b.en_us,'') " +
                "FROM appellations a LEFT JOIN localized_texts t ON t.tbl_name='appellations' AND t.tbl_column_name='name' AND t.idx=a.id " +
                "LEFT JOIN localized_texts b ON b.tbl_name='buffs' AND b.tbl_column_name='desc' AND b.idx=a.buff_id " +
                "WHERE ($filter=6 OR a.route_kind_id=$filter) " +
                "AND ($term='' OR COALESCE(t.en_us,a.name) LIKE '%'||$term||'%') ORDER BY a.order_index,a.id";
            list.Parameters.AddWithValue("$term", keyword);
            list.Parameters.AddWithValue("$filter", filter);
            var rows = new List<object?>();
            if (filter == 6 && keyword.Length == 0) rows.Add(EmptyTitle());
            var owned = live.TryGetValue("OwnedAppellationIds", out var ids) && ids is IEnumerable<uint> typed
                ? typed.ToHashSet() : new HashSet<uint>();
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                rows.Add(new object?[] { (double)id, reader.IsDBNull(1) ? "" : reader.GetString(1),
                    (double)reader.GetInt32(2), owned.Contains((uint)id) ? 1d : 0d, (double)id,
                    reader.IsDBNull(3) ? null : new Dictionary<string, object?> { ["description"] = CleanFormatting(reader.GetString(3)) } });
            }
            if (method == "GetAppellationsCount") value = (double)rows.Count;
            else
            {
                var page = args.Count > 2 ? Math.Max(1, ToInt(args[2])) : 1;
                value = rows.Skip((page - 1) * PageSize).Take(PageSize).ToArray();
            }
            return true;
        }
        catch (SqliteException) { value = method == "GetAppellationsCount" ? 1d : new object?[] { EmptyTitle() }; return true; }
    }

    private static object?[] EmptyTitle() => [0d, "", 1d, 1d, 0d, null];
    private static object LevelInfo(string? path)
    {
        var maxLevel = 8d;
        var maxExp = 1d;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT MAX(app_lv), MIN(CASE WHEN app_lv=2 THEN exp END) FROM appellation_levels";
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    maxLevel = r.IsDBNull(0) ? maxLevel : r.GetInt32(0);
                    maxExp = r.IsDBNull(1) ? maxExp : Math.Max(1, r.GetInt32(1));
                }
            }
            catch (SqliteException) { }
        }
        return new Dictionary<string, object?> { ["level"] = 1d, ["maxlevel"] = maxLevel,
            ["minExp"] = 0d, ["maxExp"] = maxExp, ["exp"] = 0d };
    }
    private static object?[] Appellation(string? path, int id, bool owned)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return EmptyTitle();
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(NULLIF(t.en_us,''),a.name),COALESCE(a.grade_id,1),NULLIF(b.en_us,'') " +
            "FROM appellations a LEFT JOIN localized_texts t ON t.tbl_name='appellations' AND t.tbl_column_name='name' AND t.idx=a.id " +
            "LEFT JOIN localized_texts b ON b.tbl_name='buffs' AND b.tbl_column_name='desc' AND b.idx=a.buff_id WHERE a.id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? [(double)id, r.GetString(0), (double)r.GetInt32(1), owned ? 1d : 0d, (double)id,
            r.IsDBNull(2) ? null : new Dictionary<string, object?> { ["description"] = CleanFormatting(r.GetString(2)) }] : EmptyTitle();
    }
    private static bool IsTrue(object? value) => value is true || value is long n && n != 0 || value is string s && s is "t" or "true" or "1";
    private static int ToInt(object? value) => value switch
    {
        int n => n, uint n => checked((int)n), double n => (int)n,
        _ => int.TryParse(Convert.ToString(value), out var n) ? n : 0
    };
}
