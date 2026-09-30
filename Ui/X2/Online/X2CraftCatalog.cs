#nullable enable
using Microsoft.Data.Sqlite;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Read-only client craft book. AAEmu sends favorite recipes, but the ordinary book is game content.</summary>
internal sealed class X2CraftCatalog
{
    internal sealed record Category(int Type, string Name, int Parent = 0, string Description = "", bool Visible = true,
        string DecoKey = "", int RepresentedChildCount = 0);
    internal sealed record Material(int ItemType, int Amount, string Name, bool MainGrade);
    internal sealed record Product(int ItemType, int Amount, string Name, bool UseGrade, int Grade);
    internal sealed record Entry(int Type, int SkillId, string Name, int CategoryA, int CategoryB, int CategoryC,
        int RequiredDoodad, string StationName, int ActabilityLimit, int Cost, int RecommendLevel,
        bool Orderable, IReadOnlyList<Material> Materials, IReadOnlyList<Product> Products,
        int ActabilityGroup, string ActabilityName, int LaborCost, IReadOnlyList<int> StationTypes);

    public IReadOnlyList<Category> A { get; private set; } = [];
    public IReadOnlyList<Category> B { get; private set; } = [];
    public IReadOnlyList<Category> C { get; private set; } = [];
    public IReadOnlyList<Entry> Entries { get; private set; } = [];
    public IReadOnlyDictionary<int, Entry> ByType { get; private set; } = new Dictionary<int, Entry>();
    private readonly Dictionary<(int Doodad, int Phase), int> _stationPacks = new();
    private readonly Dictionary<int, HashSet<int>> _packCrafts = new();

    internal int GetStationPack(int doodad, int phase) => _stationPacks.GetValueOrDefault((doodad, phase));
    internal bool IsInPack(int pack, int craft) => _packCrafts.TryGetValue(pack, out var crafts) && crafts.Contains(craft);

    internal object?[] GetACategoryRows() => A.Where(x => x.Visible && Entries.Any(entry => entry.CategoryA == x.Type))
        .Select(x => (object?)new Dictionary<string, object?>
    {
        ["type"] = x.Type, ["name"] = x.Name, ["deco_key"] = x.DecoKey,
        ["child"] = B.Where(child => child.Parent == x.Type).Take(x.RepresentedChildCount)
            .Select(child => (object?)child.Name).ToArray(),
    }).ToArray();

    internal object?[] GetBCategoryRows(int categoryA) => B.Where(x => x.Parent == categoryA)
        .Select(x => (object?)new Dictionary<string, object?>
        { ["type"] = x.Type, ["name"] = x.Name, ["desc"] = x.Description }).ToArray();

    internal bool IsEquipmentCategory(int categoryA) => B.Any(x => x.Parent == categoryA && !string.IsNullOrEmpty(x.DecoKey));

    internal object?[] GetEquipmentBCategoryRows() => B.Where(x => !string.IsNullOrEmpty(x.DecoKey))
        .Select(x => (object?)new Dictionary<string, object?>
        { ["type"] = x.Type, ["name"] = x.Name, ["deco_key"] = x.DecoKey }).ToArray();

    public X2CraftCatalog(string database)
    {
        if (!File.Exists(database)) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        var names = Names(db);
        var a = new List<Category>();
        var b = new List<Category>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "select id,name,visible,btn_deco_key,represented_child_count from craft_a_categories order by ui_order,id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) a.Add(new Category(r.GetInt32(0), En(names, "craft_a_categories", "name", r.GetInt32(0), r.GetString(1)),
                Visible: r.GetString(2) == "t", DecoKey: r.IsDBNull(3) ? "" : r.GetString(3),
                RepresentedChildCount: r.IsDBNull(4) ? 0 : r.GetInt32(4)));
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "select id,name,craft_a_category_id,desc,btn_deco_key from craft_b_categories order by ui_order,id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                var description = r.IsDBNull(3) ? "" : r.GetString(3);
                b.Add(new Category(id, En(names, "craft_b_categories", "name", id, r.GetString(1)),
                    r.GetInt32(2), En(names, "craft_b_categories", "desc", id, description),
                    DecoKey: r.IsDBNull(4) ? "" : r.GetString(4)));
            }
        }
        A = a;
        B = b;
        var parentB = new Dictionary<int, int>();
        var c = new List<Category>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "select id,name,craft_b_category_id from craft_c_categories order by ui_order,id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                parentB[id] = r.GetInt32(2);
                c.Add(new Category(id, En(names, "craft_c_categories", "name", id, r.GetString(1)), r.GetInt32(2)));
            }
        }
        C = c;
        var parentC = new Dictionary<int, int>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "select id,craft_c_category_id from craft_d_categories";
            using var r = cmd.ExecuteReader();
            while (r.Read()) parentC[r.GetInt32(0)] = r.GetInt32(1);
        }
        var parentA = b.ToDictionary(x => x.Type, x => x.Parent);
        var materials = new Dictionary<int, List<Material>>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "select m.craft_id,m.item_id,m.amount,m.main_grade,coalesce(i.name,'') from craft_materials m left join items i on i.id=m.item_id order by m.id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var craft = r.GetInt32(0);
                if (!materials.TryGetValue(craft, out var list)) materials[craft] = list = [];
                var item = r.GetInt32(1);
                list.Add(new Material(item, r.IsDBNull(2) ? 0 : r.GetInt32(2), En(names, "items", "name", item, r.GetString(4)),
                    !r.IsDBNull(3) && r.GetString(3) == "t"));
            }
        }
        var products = new Dictionary<int, List<Product>>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "select p.craft_id,p.item_id,p.amount,p.use_grade,p.item_grade_id,coalesce(i.name,'') from craft_products p left join items i on i.id=p.item_id order by p.id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var craft = r.GetInt32(0);
                if (!products.TryGetValue(craft, out var list)) products[craft] = list = [];
                var item = r.GetInt32(1);
                list.Add(new Product(item, r.IsDBNull(2) ? 0 : r.GetInt32(2), En(names, "items", "name", item, r.GetString(5)),
                    !r.IsDBNull(3) && r.GetString(3) == "t", r.IsDBNull(4) ? 0 : r.GetInt32(4)));
            }
        }
        var stations = new Dictionary<int, (int Type, string Name)>();
        var stationTypes = new Dictionary<int, HashSet<int>>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                select pc.craft_id,g.doodad_almighty_id,coalesce(d.name,''),g.id,p.craft_pack_id
                from craft_pack_crafts pc join doodad_func_craft_packs p on p.craft_pack_id=pc.craft_pack_id
                join doodad_funcs f on f.actual_func_type='DoodadFuncCraftPack' and f.actual_func_id=p.id
                join doodad_func_groups g on g.id=f.doodad_func_group_id
                join doodad_almighties d on d.id=g.doodad_almighty_id
                order by g.doodad_almighty_id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var craft = r.GetInt32(0);
                var doodad = r.GetInt32(1);
                var phase = r.GetInt32(3);
                var pack = r.GetInt32(4);
                _stationPacks[(doodad, phase)] = pack;
                if (!_packCrafts.TryGetValue(pack, out var packCrafts)) _packCrafts[pack] = packCrafts = [];
                packCrafts.Add(craft);
                if (!stationTypes.TryGetValue(craft, out var types)) stationTypes[craft] = types = [];
                types.Add(doodad);
                if (!stations.ContainsKey(craft))
                    stations[craft] = (doodad, En(names, "doodad_almighties", "name", doodad, r.GetString(2)));
            }
        }
        // A craft is available in the ordinary book only when an actual station pack offers it.
        var entries = new List<Entry>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                select distinct c.id,coalesce(c.skill_id,0),c.title,coalesce(c.craft_c_category_id,0),coalesce(c.craft_d_category_id,0),
                  coalesce(c.req_doodad_id,0),coalesce(d.name,''),c.actability_limit,c.cost,c.recommend_level,c.orderable,
                  coalesce(s.actability_group_id,0),coalesce(a.name,''),coalesce(s.consume_lp,0)
                from crafts c join craft_pack_crafts pc on pc.craft_id=c.id
                left join doodad_almighties d on d.id=c.req_doodad_id
                left join skills s on s.id=c.skill_id
                left join actability_groups a on a.id=s.actability_group_id
                where c.enable='t' order by c.visible_order,c.id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt32(0);
                if (!products.TryGetValue(id, out var output) || output.Count == 0) continue;
                var catC = r.GetInt32(3);
                if (catC == 0 && parentC.TryGetValue(r.GetInt32(4), out var fromD)) catC = fromD;
                var catB = parentB.GetValueOrDefault(catC);
                var catA = parentA.GetValueOrDefault(catB);
                var requiredDoodad = r.GetInt32(5);
                var station = requiredDoodad != 0
                    ? En(names, "doodad_almighties", "name", requiredDoodad, r.GetString(6))
                    : stations.GetValueOrDefault(id).Name ?? "";
                if (requiredDoodad == 0) requiredDoodad = stations.GetValueOrDefault(id).Type;
                entries.Add(new Entry(id, r.GetInt32(1), En(names, "crafts", "title", id, r.GetString(2)), catA, catB, catC,
                    requiredDoodad, station,
                    r.IsDBNull(7) ? 0 : r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetString(10) == "t",
                    materials.GetValueOrDefault(id) ?? [], output, r.GetInt32(11),
                    En(names, "actability_groups", "name", r.GetInt32(11), r.GetString(12)), r.GetInt32(13),
                    requiredDoodad != 0 && r.GetInt32(5) != 0 ? [requiredDoodad] :
                        stationTypes.GetValueOrDefault(id)?.ToArray() ?? []));
            }
        }
        Entries = entries;
        ByType = entries.ToDictionary(x => x.Type);
    }

    private static Dictionary<(string Table, string Column, int Id), string> Names(SqliteConnection db)
    {
        var result = new Dictionary<(string, string, int), string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "select tbl_name,tbl_column_name,idx,en_us from localized_texts where en_us is not null and en_us != '' and tbl_name in ('crafts','items','craft_a_categories','craft_b_categories','craft_c_categories','doodad_almighties','actability_groups')";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[(r.GetString(0), r.GetString(1), r.GetInt32(2))] = r.GetString(3);
        return result;
    }

    private static string En(Dictionary<(string Table, string Column, int Id), string> names,
        string table, string column, int id, string fallback) =>
        names.TryGetValue((table, column, id), out var name) ? name : UiTranslator.Shared.Translate(fallback);
}
