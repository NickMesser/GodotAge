#nullable enable
using Microsoft.Data.Sqlite;
using AAEmu.GodotViewer.Lua;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Marketplace menus are static client content; goods remain server authoritative.</summary>
public static class X2InGameShopApi
{
    public static void Install(X2LuaHost host, IX2EconomyData data, string? databasePath = null)
    {
        var catalog = Catalog.Load(databasePath);
        var main = catalog.SubTabs.Keys.Order().ToArray();
        var selectedMain = main.FirstOrDefault();
        var selectedSub = catalog.SubTabs.GetValueOrDefault(selectedMain)?.FirstOrDefault() ?? 0;
        var page = 1;

        object? Query(string method, params object?[] args) =>
            data.TryQuery(new X2EconomyQuery("X2InGameShop", method, args), out var value) ? value : null;
        bool Execute(string method, params object?[] args) =>
            data.Execute(new X2EconomyCommand("X2InGameShop", method, args));

        host.Define("X2InGameShop", "CheckReady", _ => Query("CheckReady") ?? true);
        host.Define("X2InGameShop", "IsInGameShopEnable", _ => Query("IsInGameShopEnable") ?? true);
        host.Define("X2InGameShop", "GetMainTabs", _ => ToLua(Query("GetMainTabs") ?? main));
        host.Define("X2InGameShop", "GetFirstMainTab", _ => Number(Query("GetFirstMainTab"), main.FirstOrDefault()));
        host.Define("X2InGameShop", "GetSubTabs", _ => ToLua(Query("GetSubTabs", selectedMain) ??
            catalog.SubTabs.GetValueOrDefault(selectedMain) ?? []));
        host.Define("X2InGameShop", "GetFirstSubTab", a =>
        {
            var requestedMain = a.Int(0);
            return Number(Query("GetFirstSubTab", requestedMain),
                catalog.SubTabs.GetValueOrDefault(requestedMain)?.FirstOrDefault() ?? 0);
        });
        host.Define("X2InGameShop", "GetSortFilter", _ => ToLua(Query("GetSortFilter") ?? catalog.SortFilters));
        host.Define("X2InGameShop", "GetGoodsPerPage", _ => Number(Query("GetGoodsPerPage"),
            selectedMain == 1 && selectedSub == 1 ? 4 : 8));
        host.Define("X2InGameShop", "GetGoods", a => ToLua(Query("GetGoods", a.Int(0), a.Int(1))));
        host.Define("X2InGameShop", "SelectMainTab", a =>
        {
            selectedMain = a.Int(0); selectedSub = a.Int(1); page = 1;
            Execute("SelectMainTab", selectedMain, selectedSub); return null;
        });
        host.Define("X2InGameShop", "SelectSubTab", a =>
        {
            selectedSub = a.Int(0); page = 1; Execute("SelectSubTab", selectedSub); return null;
        });
        host.Define("X2InGameShop", "SelectPage", a =>
        {
            page = Math.Max(1, a.Int(0)); Execute("SelectPage", page); return null;
        });
        host.Define("X2InGameShop", "CheckWaitingServer", _ => { Execute("RequestMenuList"); return null; });
    }

    private static double Number(object? value, int fallback) => value == null ? fallback : Convert.ToDouble(value);

    private static object? ToLua(object? value)
    {
        if (value is null) return null;
        if (value is string or bool || value.GetType().IsPrimitive) return value;
        if (value is System.Collections.IDictionary dictionary)
        {
            var table = new LuaTable();
            foreach (System.Collections.DictionaryEntry pair in dictionary)
                table[pair.Key] = ToLua(pair.Value);
            return table;
        }
        if (value is System.Collections.IEnumerable enumerable)
        {
            var table = new LuaTable(); var index = 1d;
            foreach (var item in enumerable) table[index++] = ToLua(item);
            return table;
        }
        return value;
    }

    private sealed record Catalog(Dictionary<int, int[]> SubTabs, IReadOnlyList<Dictionary<string, object?>> SortFilters)
    {
        public static Catalog Load(string? path)
        {
            path = string.IsNullOrWhiteSpace(path) ? ClientPaths.Database : path;
            var tabs = new Dictionary<int, SortedSet<int>>();
            var sorts = new List<Dictionary<string, object?>>();
            try
            {
                using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly"); db.Open();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = "select key from ui_texts where category_id=120 and key like 'sub_menu_%'";
                    using var rows = cmd.ExecuteReader();
                    while (rows.Read())
                    {
                        var parts = rows.GetString(0).Split('_');
                        if (parts.Length == 4 && int.TryParse(parts[2], out var m) && int.TryParse(parts[3], out var s))
                            (tabs.TryGetValue(m, out var set) ? set : tabs[m] = []).Add(s);
                    }
                }
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = @"select s.order_type,coalesce(nullif(l.en_us,''),s.name)
from ingameshop_goods_sort_orders s left join localized_texts l
on l.tbl_name='ingameshop_goods_sort_orders' and l.tbl_column_name='name' and l.idx=s.id
where s.show='t' order by s.show_order";
                    using var rows = cmd.ExecuteReader();
                    while (rows.Read()) sorts.Add(new() { ["type"] = rows.GetInt32(0), ["name"] = rows.GetString(1) });
                }
            }
            catch { /* A missing content DB leaves a valid empty marketplace. */ }
            return new(tabs.ToDictionary(x => x.Key, x => x.Value.ToArray()), sorts);
        }
    }
}
