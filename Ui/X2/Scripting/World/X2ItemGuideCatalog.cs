#nullable enable
using AAEmu.GodotViewer.Lua;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.World;

/// <summary>
/// Installs the client-owned item guide catalog.  These rows are static game content; the
/// original client does not need a world packet before it can build and browse this window.
/// </summary>
public static class X2ItemGuideCatalog
{
    public static void Install(X2LuaHost host, string? database = null)
    {
        var catalog = Load(database ?? X2DbLists.DefaultDatabase);
        host.Define("X2ItemGuide", "GetImpls", _ => catalog.Impls);
        host.Define("X2ItemGuide", "GetCategories", _ => catalog.Categories);
        host.Define("X2ItemGuide", "GetCategoryInfos", a =>
            catalog.CategoryInfos(a.Int(0), a.Int(1), a.Int(2), a.Int(3), a.Bool(4)));
        host.Define("X2ItemGuide", "GetSpecifiedItems", a =>
            catalog.SpecifiedItems(a.Int(0), a.Int(1), a.Int(2), a.Int(3)));
        host.Define("X2ItemGuide", "GetIndunPortalInfo", _ => null, nullIsNil: true);
    }

    private static Catalog Load(string database)
    {
        try
        {
            var cs = new SqliteConnectionStringBuilder
                { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
            using var db = new SqliteConnection(cs);
            db.Open();
            return new Catalog(cs, ReadImpls(db), ReadCategories(db));
        }
        catch (SqliteException)
        {
            // Keep the API well formed when an optional content database is unavailable.
            return new Catalog(null, new LuaTable(), new LuaTable());
        }
    }

    private static LuaTable ReadImpls(SqliteConnection db)
    {
        var result = new LuaTable();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
SELECT i.id, COALESCE(NULLIF(l.en_us,''),i.name)
FROM item_guide_impls i
LEFT JOIN localized_texts l ON l.tbl_name='item_guide_impls' AND l.tbl_column_name='name' AND l.idx=i.id
WHERE i.visible IN (1,'t','true') ORDER BY i.visible_order,i.id";
        using var r = cmd.ExecuteReader();
        var n = 1;
        while (r.Read()) result[(double)n++] = new LuaTable
        {
            ["name"] = English(r.GetString(1)),
            // info.lua uses type == 1 only to enable equipment comparisons on the item tab.
            ["type"] = (double)r.GetInt32(0)
        };
        return result;
    }

    private static LuaTable ReadCategories(SqliteConnection db)
    {
        var outer = new LuaTable();
        using var impl = db.CreateCommand();
        impl.CommandText = "SELECT id FROM item_guide_impls WHERE visible IN (1,'t','true') ORDER BY visible_order,id";
        var ids = new List<int>();
        using (var ir = impl.ExecuteReader()) while (ir.Read()) ids.Add(ir.GetInt32(0));
        var implIndex = 1;
        foreach (var id in ids) outer[(double)implIndex++] = ReadCategories(db, id);
        return outer;
    }

    private static LuaTable ReadCategories(SqliteConnection db, int implId)
    {
        var result = new LuaTable();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
SELECT a.id,COALESCE(NULLIF(l.en_us,''),a.name)
FROM item_guide_a_categories a
LEFT JOIN localized_texts l ON l.tbl_name='item_guide_a_categories' AND l.tbl_column_name='name' AND l.idx=a.id
WHERE a.item_guide_impl_id=$impl AND a.visible IN (1,'t','true') ORDER BY a.visible_order,a.id";
        cmd.Parameters.AddWithValue("$impl", implId);
        var rows = new List<(int Id, string Name)>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add((r.GetInt32(0), r.GetString(1)));
        var n = 1;
        foreach (var (id, name) in rows)
        {
            var row = new LuaTable { ["aType"] = (double)id, ["name"] = English(name) };
            var children = ReadSubcategories(db, id);
            if (children.Count != 0) row["bInfos"] = children;
            result[(double)n++] = row;
        }
        return result;
    }

    private static LuaTable ReadSubcategories(SqliteConnection db, int category)
    {
        var result = new LuaTable();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
SELECT b.id,COALESCE(NULLIF(l.en_us,''),b.name)
FROM item_guide_b_categories b
LEFT JOIN localized_texts l ON l.tbl_name='item_guide_b_categories' AND l.tbl_column_name='name' AND l.idx=b.id
WHERE b.item_guide_a_category_id=$a AND b.visible IN (1,'t','true') ORDER BY b.visible_order,b.id";
        cmd.Parameters.AddWithValue("$a", category);
        using var r = cmd.ExecuteReader();
        var n = 1;
        while (r.Read()) result[(double)n++] = new LuaTable
            { ["bType"] = (double)r.GetInt32(0), ["name"] = English(r.GetString(1)) };
        return result;
    }

    private sealed class Catalog(string? connectionString, LuaTable impls, LuaTable categories)
    {
        public LuaTable Impls { get; } = impls;
        public LuaTable Categories { get; } = categories;

        public LuaTable CategoryInfos(int a, int b, int loot, int grade, bool ascending)
        {
            if (connectionString is null) return new LuaTable();
            using var db = new SqliteConnection(connectionString); db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
SELECT DISTINCT g.id,COALESCE(NULLIF(n.en_us,''),g.name),g.loot_main_category_id,
       g.loot_sub_category_id,g.show_order,g.zone_key,
       COALESCE(NULLIF(w.en_us,''),g.way_to_loot,''),COALESCE(
         (SELECT icon.filename FROM item_guide_icons gx JOIN icons icon ON icon.id=gx.icon_id
           WHERE gx.item_guide_id=g.id AND gx.item_guide_a_category_id=$a
             AND ($b=0 OR gx.item_guide_b_category_id=$b) LIMIT 1),
         (SELECT icon.filename FROM item_guide_elems ex JOIN items ix ON ix.id=ex.item_id
           JOIN icons icon ON icon.id=ix.icon_id WHERE ex.item_guide_id=g.id
             AND ex.item_guide_a_category_id=$a AND ($b=0 OR ex.item_guide_b_category_id=$b)
           ORDER BY ex.visible_order LIMIT 1),'')
FROM item_guides g
JOIN item_guide_elems e ON e.item_guide_id=g.id
LEFT JOIN localized_texts n ON n.tbl_name='item_guides' AND n.tbl_column_name='name' AND n.idx=g.id
LEFT JOIN localized_texts w ON w.tbl_name='item_guides' AND w.tbl_column_name='way_to_loot' AND w.idx=g.id
WHERE g.show IN (1,'t','true') AND e.item_guide_a_category_id=$a
 AND ($b=0 OR e.item_guide_b_category_id=$b) AND ($loot=0 OR g.loot_main_category_id=$loot)
ORDER BY g.show_order " + (ascending ? "ASC" : "DESC") + ",g.id";
            cmd.Parameters.AddWithValue("$a", a); cmd.Parameters.AddWithValue("$b", b);
            cmd.Parameters.AddWithValue("$loot", loot);
            using var r = cmd.ExecuteReader();
            // CreateDescWnd renders one ListCtrl row as a seven-icon page.  Native
            // GetCategoryInfos therefore returns an array of row arrays, rather than
            // a flat array of guide records.
            const int guidesPerRow = 7;
            var result = new LuaTable();
            LuaTable? row = null;
            var rowIndex = 0;
            var columnIndex = 0;
            while (r.Read())
            {
                if (row is null || columnIndex == guidesPerRow)
                {
                    row = new LuaTable();
                    result[(double)++rowIndex] = row;
                    columnIndex = 0;
                }
                row[(double)++columnIndex] = new LuaTable
                {
                    ["itemGuideType"] = (double)r.GetInt32(0), ["name"] = English(r.GetString(1)),
                    ["lootMainCategory"] = (double)r.GetInt32(2), ["lootSubCategory"] = (double)r.GetInt32(3),
                    ["showOrder"] = (double)r.GetInt32(4), ["enable"] = true,
                    ["zoneId"] = r.IsDBNull(5) ? 0d : (double)r.GetInt32(5),
                    ["wayToLoot"] = English(r.GetString(6)), ["iconPath"] = IconPath(r.GetString(7))
                };
            }
            return result;
        }

        public LuaTable SpecifiedItems(int a, int b, int guide, int grade)
        {
            if (connectionString is null) return new LuaTable();
            using var db = new SqliteConnection(connectionString); db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
SELECT i.id,COALESCE(NULLIF(n.en_us,''),i.name),COALESCE(icon.filename,''),e.show_craft,i.fixed_grade
FROM item_guide_elems e JOIN items i ON i.id=e.item_id
LEFT JOIN localized_texts n ON n.tbl_name='items' AND n.tbl_column_name='name' AND n.idx=i.id
LEFT JOIN icons icon ON icon.id=i.icon_id
WHERE e.item_guide_id=$guide AND e.item_guide_a_category_id=$a
 AND ($b=0 OR e.item_guide_b_category_id=$b)
 AND ($grade=0 OR i.fixed_grade=$grade OR i.fixed_grade=$grade-1)
ORDER BY e.visible_order,i.id";
            cmd.Parameters.AddWithValue("$guide", guide); cmd.Parameters.AddWithValue("$a", a);
            cmd.Parameters.AddWithValue("$b", b); cmd.Parameters.AddWithValue("$grade", grade);
            using var r = cmd.ExecuteReader();
            var result = new LuaTable(); var index = 1;
            while (r.Read()) result[(double)index++] = new LuaTable
            {
                ["itemType"] = (double)r.GetInt32(0), ["name"] = English(r.GetString(1)),
                ["icon"] = IconPath(r.GetString(2)), ["iconPath"] = IconPath(r.GetString(2)),
                ["showCraft"] = True(r.GetValue(3)), ["itemGrade"] = (double)Math.Max(0, r.GetInt32(4))
            };
            return result;
        }
    }

    private static bool True(object value) => value is long n ? n != 0 :
        string.Equals(Convert.ToString(value), "t", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);
    private static string English(string value) => UiTranslator.Shared.TranslateDatabaseText(value);
    private static string IconPath(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "";
        var path = filename.Replace('\\', '/').TrimStart('/');
        return path.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ? path : $"ui/icon/{path}";
    }
}
