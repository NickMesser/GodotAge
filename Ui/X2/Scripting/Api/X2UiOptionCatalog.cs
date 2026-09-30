#nullable enable
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>
/// ui_options rows (target_type Option / Hotkey) with en_us titles and tooltips: what X2Option:GetOptionInfo and GetHotkeyInfo return in the
/// client. The option scripts take an option's title from here (e.g. graphics quality, OIT_MASTERGRAHICQUALITY 122); without it the option
/// frame gets no title widget and screen_advanced_option.lua fails indexing optionTitle.
/// </summary>
internal static class X2UiOptionCatalog
{
    public sealed record Entry(string Title, string Tooltip, string FeatureSet, bool FeatureSetCondition);

    private static readonly Lazy<(Dictionary<int, Entry> Options, Dictionary<int, Entry> Hotkeys, Dictionary<int, string> ActionNames)> Data = new(Load);

    public static Entry? Option(int id) => Data.Value.Options.GetValueOrDefault(id);
    public static Entry? Hotkey(int id) => Data.Value.Hotkeys.GetValueOrDefault(id);
    public static string? ActionName(int id) => Data.Value.ActionNames.GetValueOrDefault(id);

    private static (Dictionary<int, Entry>, Dictionary<int, Entry>, Dictionary<int, string>) Load()
    {
        var options = new Dictionary<int, Entry>();
        var hotkeys = new Dictionary<int, Entry>();
        var names = new Dictionary<int, string>();
        var path = World.X2DbLists.DefaultDatabase;
        if (!File.Exists(path)) return (options, hotkeys, names);
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            AAEmu.GodotViewer.Data.ClientEnums.Ensure(db);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT u.target_id, u.target_type, COALESCE(NULLIF(t.en_us, ''), u.title), COALESCE(NULLIF(p.en_us, ''), u.tooltip),
                           COALESCE(u.feature_set, ''), u.feature_set_condition
                    FROM ui_options u
                    LEFT JOIN localized_texts t ON t.tbl_name = 'ui_options' AND t.tbl_column_name = 'title' AND t.idx = u.id
                    LEFT JOIN localized_texts p ON p.tbl_name = 'ui_options' AND p.tbl_column_name = 'tooltip' AND p.idx = u.id
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var entry = new Entry(r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetString(4),
                        !r.IsDBNull(5) && r.GetValue(5)?.ToString() is "t" or "1" or "true");
                    var target = r.GetString(1).Equals("Hotkey", StringComparison.OrdinalIgnoreCase) ? hotkeys : options;
                    target[r.GetInt32(0)] = entry;
                }
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name FROM enum_hotkey_actions";
                using var r = cmd.ExecuteReader();
                while (r.Read()) names[r.GetInt32(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
        }
        catch (SqliteException) { }
        return (options, hotkeys, names);
    }
}
