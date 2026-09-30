#nullable enable
using AAEmu.GodotViewer.Lua;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.World;

/// <summary>
/// Static client lists the x2ui scripts build windows from (HUD right icon menus, farm groups, raid recruit types,
/// instance kinds). The client reads them from its game database; so do we, with the en_us names from localized_texts.
/// Installed after the API families so these definitions win over their empty defaults.
/// </summary>
public static class X2DbLists
{
    public static string DefaultDatabase => ClientPaths.Database;

    public static void Install(X2LuaHost host, string? database)
    {
        database ??= DefaultDatabase;
        var hudMenus = Load(database, HudRightIconMenus);
        var farmGroups = Load(database, db => Rows(db, "farm_groups", "select id, name from farm_groups order by id",
            r => new LuaTable { ["type"] = (double)r.GetInt64(0), ["name"] = En(db, "farm_groups", r.GetInt64(0), r.GetString(1)) }));
        var recruitTypes = Load(database, db => Rows(db, "raid_recruit_types", "select id, name, icon_key from raid_recruit_types where visible = 't' order by id",
            r => new LuaTable { ["type"] = (double)r.GetInt64(0), ["name"] = En(db, "raid_recruit_types", r.GetInt64(0), r.GetString(1)), ["iconKey"] = r.GetString(2) }));
        var instanceKinds = Load(database, db => Rows(db, "instance_ui_kinds", "select id, name, list_button_path, show_honor_store from instance_ui_kinds order by id",
            r => new LuaTable
            {
                ["type"] = (double)r.GetInt64(0), ["name"] = En(db, "instance_ui_kinds", r.GetInt64(0), r.GetString(1)),
                ["listButtonPath"] = r.GetString(2), ["showHonorStore"] = r.GetString(3) == "t",
            }));
        var instancesByKind = LoadInstancesByKind(database);

        host.Define("X2", "GetHudRightIconMenus", _ => Copy(hudMenus));
        host.Define("X2", "GetFarmGroups", _ => Copy(farmGroups));
        host.Define("X2Team", "GetRaidRecruitTypeList", _ => Copy(recruitTypes));
        host.Define("X2BattleField", "GetInstanceUiKindList", _ => Copy(instanceKinds));
        host.Define("X2BattleField", "GetInstanceListByKind", a =>
            instancesByKind.TryGetValue(a.Int(0), out var rows) ? Copy(rows) : new LuaTable());

        // siege schedule (tab_siege_info.lua reads <schedule>_week/_hour/_min for every step)
        host.Define("X2Dominion", "GetSiegeTimeInfo", _ =>
        {
            var t = new LuaTable();
            foreach (var step in new[] { "hero_volunteer", "apply_siege_raid", "declare_siege", "siege", "siege_end" })
            {
                t[step + "_week"] = "sunday";
                t[step + "_hour"] = 0d;
                t[step + "_min"] = 0d;
            }
            return t;
        });
    }

    private static List<LuaTable> HudRightIconMenus(SqliteConnection db)
    {
        var features = new Dictionary<long, List<string>>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "select ui_content_type_id, feature_set from ui_content_info_feature_sets order by id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!features.TryGetValue(r.GetInt64(0), out var list)) features[r.GetInt64(0)] = list = [];
                list.Add(r.GetString(1));
            }
        }
        return Rows(db, "ui_hud_right_icon_menus",
            "select ui_content_type_id, button_style, visible_order, creatable_when_feature_set_all_on from ui_hud_right_icon_menus order by visible_order",
            r =>
            {
                var t = new LuaTable
                {
                    ["uiContentType"] = (double)r.GetInt64(0), ["buttonStyle"] = r.GetString(1),
                    ["visibleOrder"] = (double)r.GetInt64(2), ["creatableWhenFeatureSetAllOn"] = r.GetString(3) == "t",
                };
                if (features.TryGetValue(r.GetInt64(0), out var list)) t["featureSet"] = Array(list.Cast<object?>());
                return t;
            });
    }

    private static List<LuaTable> Load(string database, Func<SqliteConnection, List<LuaTable>> read)
    {
        try
        {
            if (!File.Exists(database)) return [];
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            return read(db);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static Dictionary<int, List<LuaTable>> LoadInstancesByKind(string database)
    {
        var result = new Dictionary<int, List<LuaTable>>();
        try
        {
            if (!File.Exists(database)) return result;
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                select i.id, i.instance_ui_kind_id, coalesce(nullif(l.en_us, ''), i.name),
                       i.ui_key, i.show_festival_icon
                from instances i
                left join localized_texts l on l.tbl_name = 'instances'
                     and l.tbl_column_name = 'name' and l.idx = i.id
                where i.show_ui = 't' order by i.instance_ui_kind_id, i.ui_order, i.id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = r.GetInt32(1);
                if (!result.TryGetValue(kind, out var rows)) result[kind] = rows = [];
                rows.Add(new LuaTable
                {
                    ["type"] = (double)r.GetInt64(0),
                    ["name"] = UiTranslator.Shared.Translate(r.GetString(2)),
                    ["uiKey"] = r.GetString(3),
                    ["showFestivalIcon"] = r.GetString(4) == "t",
                    ["globalField"] = false,
                    ["globalArena"] = false,
                });
            }
        }
        catch (Exception)
        {
            return new Dictionary<int, List<LuaTable>>();
        }
        return result;
    }

    private static List<LuaTable> Rows(SqliteConnection db, string table, string sql, Func<SqliteDataReader, LuaTable> map)
    {
        var result = new List<LuaTable>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(map(r));
        return result;
    }

    /// <summary>The en_us text for a database name column, falling back to the shared translator.</summary>
    private static string En(SqliteConnection db, string table, long idx, string fallback)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "select en_us from localized_texts where tbl_name = $t and tbl_column_name = 'name' and idx = $i";
        cmd.Parameters.AddWithValue("$t", table);
        cmd.Parameters.AddWithValue("$i", idx);
        return cmd.ExecuteScalar() is string s && s.Length > 0 ? s : UiTranslator.Shared.Translate(fallback);
    }

    private static LuaTable Array(IEnumerable<object?> values)
    {
        var t = new LuaTable();
        var i = 1;
        foreach (var v in values) t[(double)i++] = v;
        return t;
    }

    // scripts may modify what they get, so each call hands out fresh tables
    private static LuaTable Copy(List<LuaTable> rows) => Array(rows.Select(r => (object?)CopyTable(r)));

    private static LuaTable CopyTable(LuaTable t)
    {
        var c = new LuaTable();
        foreach (var (k, v) in t) c[k] = v is LuaTable inner ? CopyTable(inner) : v;
        return c;
    }
}
