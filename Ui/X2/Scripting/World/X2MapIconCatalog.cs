#nullable enable
using Microsoft.Data.Sqlite;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Read-only client map icon metadata and the user's live Show Icons choices.</summary>
public static partial class X2MapIconCatalog
{
    public const string Atlas = "ui/map/icon/npc_icon.dds";
    private sealed record Icon(int Id, string Name, string Coord, int? Filter, int Display, bool DefaultDisplay);
    private sealed record QuestOffer(int MinLevel, int MaxLevel, int RaceMask,
        bool Repeatable, int DetailId, int Priority, HashSet<uint> Prerequisites);
    private static readonly Lazy<(Dictionary<int, Icon> Icons, Dictionary<uint, int> Services,
        Dictionary<uint, HashSet<uint>> QuestGivers, Dictionary<uint, HashSet<uint>> QuestReporters,
        Dictionary<uint, QuestOffer> Quests)> Data = new(Load);
    private static readonly object Gate = new();
    private static readonly Dictionary<int, bool> Checks = [];
    private static readonly Dictionary<int, bool> Folders = [];

    public static IReadOnlyList<int> CheckList(int filter)
    {
        var icons = Data.Value.Icons.Values;
        return icons.Where(i => filter == 4 ? i.Filter is null && i.Display > 0 : i.Filter == filter && i.Display > 0)
            .OrderByDescending(i => i.Display).ThenBy(i => i.Id).Select(i => i.Id).ToArray();
    }

    public static string IconText(int id) => Data.Value.Icons.TryGetValue(id, out var icon) ? icon.Name : "";
    public static string IconCoord(int id) => Data.Value.Icons.TryGetValue(id, out var icon) ? icon.Coord : "";

    public static bool IsChecked(int id)
    {
        lock (Gate)
        {
            if (Checks.TryGetValue(id, out var value)) return value;
            // Quest symbols are not entries in the Show Icons checklist (display=0),
            // so its default_display=false must not suppress their live markers.
            return !Data.Value.Icons.TryGetValue(id, out var icon) ||
                (icon.Display <= 0 && icon.Coord.StartsWith("quest_", StringComparison.OrdinalIgnoreCase)) ||
                icon.DefaultDisplay || icon.Display > 0;
        }
    }

    public static void SetChecked(int id, bool value) { lock (Gate) Checks[id] = value; }
    public static void SetFolder(int id, bool value) { lock (Gate) Folders[id] = value; }
    public static bool FolderChecked(int id) { lock (Gate) return Folders.GetValueOrDefault(id, true); }

    private static int Pick(int current, int candidate) => candidate == 0 ? current : candidate;
    private static int IdFor(string coord) => Data.Value.Icons.Values.FirstOrDefault(i =>
        i.Coord.Equals(coord, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;

    private static X2MapMarker Marker(string key, float x, float y, int id, string? categoryOverride = null)
    {
        var icon = Data.Value.Icons[id];
        return new X2MapMarker(key, categoryOverride ?? icon.Filter switch { 0 => "Npc", 1 => "Doodad", 2 => "Housing", 3 => "Structure", _ => "Npc" },
            x, y, Atlas, icon.Coord, true, id);
    }

    private static (Dictionary<int, Icon>, Dictionary<uint, int>, Dictionary<uint, HashSet<uint>>, Dictionary<uint, HashSet<uint>>,
        Dictionary<uint, QuestOffer>) Load()
    {
        var icons = new Dictionary<int, Icon>();
        var services = new Dictionary<uint, int>();
        var givers = new Dictionary<uint, HashSet<uint>>(); var reporters = new Dictionary<uint, HashSet<uint>>();
        var quests = new Dictionary<uint, QuestOffer>();
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = X2DbLists.DefaultDatabase, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            AAEmu.GodotViewer.Data.ClientEnums.Ensure(db);
            using (var cmd = db.CreateCommand())
            {
                // The client database has no enum_map_symbol_types: the icons stay and are named after their atlas coordinate
                // (the same as the symbol name for most of them); the map filter uses the name as its key and text id.
                cmd.CommandText = """
                    SELECT mi.symbol_id, COALESCE(st.name, mi.coord, ''), mi.coord, mi.filter_id, mi.display, mi.default_display
                    FROM map_icons mi LEFT JOIN enum_map_symbol_types st ON st.id=mi.symbol_id
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read()) icons[r.GetInt32(0)] = new Icon(r.GetInt32(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt32(4), r.GetString(5) == "t");
            }
            var serviceColumns = new (string Column, string Coord)[]
            {
                ("skill_trainer", "npc_ability_changer"), ("merchant", "npc_store_goods"),
                ("auctioneer", "npc_store_auction_house"), ("teleporter", "portal"),
                ("stabler", "npc_stabler"), ("banker", "npc_store_bank"), ("blacksmith", "npc_store_blacksmith"),
                ("repairman", "npc_store_blacksmith"), ("trader", "npc_store_roamer"), ("specialty", "sea_trade"),
                ("ability_changer", "npc_ability_changer"), ("honor_point", "honor_point_collector"),
            };
            foreach (var (column, coord) in serviceColumns)
            {
                var iconId = icons.Values.FirstOrDefault(i => i.Coord.Equals(coord, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
                if (iconId == 0) continue;
                using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT id FROM npcs WHERE {column}='t'";
                using var r = cmd.ExecuteReader(); while (r.Read()) services[(uint)r.GetInt64(0)] = iconId;
            }
            ReadQuestNpcIds(db, "QuestActConAcceptNpc", givers);
            ReadQuestNpcIds(db, "QuestActConReportNpc", reporters);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, min_level, max_level, race, repeatable, detail_id, priority FROM quest_contexts";
                using var r = cmd.ExecuteReader();
                while (r.Read()) quests[(uint)r.GetInt64(0)] = new QuestOffer(
                    r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
                    r.GetString(4) == "t", r.GetInt32(5), r.GetInt32(6), []);
            }
            // Reward accept-component acts start the referenced next quest. Record the
            // completed source as an alternative predecessor for that next quest.
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT component.quest_context_id, dependency.quest_context_id
                    FROM quest_acts act
                    JOIN quest_components component ON component.id=act.quest_component_id
                    JOIN quest_act_con_accept_components dependency ON dependency.id=act.act_detail_id
                    WHERE act.act_detail_type='QuestActConAcceptComponent' AND act.enable='t'
                      AND component.component_kind_id=8
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var required = (uint)r.GetInt64(0);
                    var next = (uint)r.GetInt64(1);
                    if (next != required && quests.TryGetValue(next, out var offer))
                        offer.Prerequisites.Add(required);
                }
            }
        }
        catch { /* Offline/no-database UI continues with no map icons. */ }
        return (icons, services, givers, reporters, quests);
    }

    private static void ReadQuestNpcIds(SqliteConnection db, string detailType, Dictionary<uint, HashSet<uint>> result)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT npc.npc_id, component.quest_context_id
            FROM quest_acts act
            JOIN quest_components component ON component.id=act.quest_component_id
            JOIN quest_act_con_accept_npcs npc ON npc.id=act.act_detail_id
            WHERE act.act_detail_type=$type AND act.enable='t' AND npc.npc_id>0
              AND component.component_kind_id=2 AND component.hide_quest_marker='f'
              AND npc.use_alias='f'
            """;
        if (detailType == "QuestActConReportNpc")
            cmd.CommandText = cmd.CommandText
                .Replace("quest_act_con_accept_npcs", "quest_act_con_report_npcs", StringComparison.Ordinal)
                .Replace("component.component_kind_id=2", "component.component_kind_id=6", StringComparison.Ordinal);
        cmd.Parameters.AddWithValue("$type", detailType);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var npc = (uint)r.GetInt64(0); var quest = (uint)r.GetInt64(1);
            if (!result.TryGetValue(npc, out var quests)) result[npc] = quests = [];
            quests.Add(quest);
        }
    }
}
