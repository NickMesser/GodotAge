#nullable enable
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Static skill definition, with localized display fields and millisecond timings.</summary>
public sealed record X2SkillEntry(int Id, string Name, string Description, int Ability, string AbilityName,
    string IconPath, int SkillPoints, int CooldownMs, int CastingMs);

/// <summary>A row in the skill alert preferences list.</summary>
public sealed record X2SkillAlertEntry(int SkillType, string Name, string IconPath, bool Use, bool ExistSlot);

/// <summary>One origin or heir skill slot. Position and slot indices are one based.</summary>
public sealed record X2HeirSkillEntry(int Skill, string Name, int ActiveType, bool HasHeirSkill, int Position);

/// <summary>Counts shown by the heir skill reset dialog.</summary>
public sealed record X2HeirSkillCounts(int Count, int AbilityCount, int AllCount);

/// <summary>Reset quote for one of the three reset scopes (all, ability, skill).</summary>
public sealed record X2HeirResetInfo(long Cost, int Ability, string Skill);

public partial interface IX2UnitData
{
    /// <summary>Definition by skill type; may use read only content data. Null for an unknown id.</summary>
    X2SkillEntry? GetSkill(int skillType);

    /// <summary>Localized runtime tooltip override; null uses the static skill definition.</summary>
    LuaTable? GetSkillTooltip(int skillType, int itemType, int section);

    /// <summary>Rows for the requested ability, in client display order.</summary>
    IReadOnlyList<X2SkillAlertEntry> GetSkillAlertList(int ability);
    /// <summary>Remaining duration of a status buff, in milliseconds.</summary>
    double GetSkillAlertBuffRemainingMs(int buffId);
    void AddSkillAlertBlacklist(int skillType);
    void RemoveSkillAlertBlacklist(int skillType);
    void SaveSkillAlertLists(IReadOnlyList<int> blacklist, IReadOnlyList<int> whitelist);

    /// <summary>Heir data is player state; all slot indices and positions are one based.</summary>
    int HeirSkillCount { get; }
    int OriginSkillCount { get; }
    X2HeirSkillEntry? GetHeirSkill(int ability, int slotIndex, int position);
    X2HeirSkillEntry? GetOriginSkill(int ability, int slotIndex);
    X2HeirSkillCounts? GetHeirSkillCounts(int ability, int slotIndex);
    X2HeirResetInfo? GetHeirResetInfo(int resetKind, int ability, int slotIndex);
    /// <summary>Whether the origin skill has reached the step required for heir selection.</summary>
    bool CheckHeirSkillStep(int ability, int slotIndex);
    /// <summary>True when a skill type is defined as an heir skill in static content.</summary>
    bool IsHeirSkill(int skillType);
    /// <summary>Server validated changes; the ensuing state update arrives asynchronously.</summary>
    void ResetHeirSkills(int resetKind, int ability, int slotIndex);
    void ResetHeirSkillForSlot(int skillType);
    void SelectHeirSkill(int ability, int slotIndex);
    void SelectOriginSkill(int ability);
    /// <summary>Current character's currency type for heir reset costs; 0 if unavailable.</summary>
    int HeirResetCurrency { get; }
}

public partial class NullUnitData
{
    private readonly HashSet<int> _skillAlertBlacklist = [];
    private readonly Dictionary<int, X2SkillEntry?> _skillCache = [];
    private readonly object _skillLock = new();

    /// <summary>Optional path for static skill data. Defaults to the installed read only game database.</summary>
    public virtual string? SkillDatabasePath { get; set; } = ClientPaths.Database;

    public virtual X2SkillEntry? GetSkill(int skillType)
    {
        if (skillType <= 0) return null;
        lock (_skillLock)
        {
            if (_skillCache.TryGetValue(skillType, out var cached)) return cached;
            X2SkillEntry? entry = null;
            if (SkillDatabasePath is { Length: > 0 } path && File.Exists(path))
            {
                try
                {
                    using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                    db.Open();
                    AAEmu.GodotViewer.Data.ClientEnums.Ensure(db);
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = """
                        SELECT s.id,
                          COALESCE(ln.en_us, s.name), COALESCE(ld.en_us, s.desc),
                          s.ability_id, COALESCE(a.name, ''), COALESCE(i.filename, ''),
                          s.skill_points, s.cooldown_time, s.casting_time
                        FROM skills s
                        LEFT JOIN localized_texts ln ON ln.tbl_name='skills' AND ln.tbl_column_name='name' AND ln.idx=s.id
                        LEFT JOIN localized_texts ld ON ld.tbl_name='skills' AND ld.tbl_column_name='desc' AND ld.idx=s.id
                        LEFT JOIN enum_abilities a ON a.id=s.ability_id
                        LEFT JOIN icons i ON i.id=s.icon_id
                        WHERE s.id=$id LIMIT 1
                        """;
                    cmd.Parameters.AddWithValue("$id", skillType);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read()) entry = new X2SkillEntry(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt32(3), reader.GetString(4), IconPath(reader.GetString(5)), reader.GetInt32(6),
                        reader.GetInt32(7), reader.GetInt32(8));
                }
                catch (SqliteException) { /* Optional content database is unavailable. */ }
            }
            return _skillCache[skillType] = entry;
        }
    }

    public virtual LuaTable? GetSkillTooltip(int skillType, int itemType, int section) => null;
    public virtual IReadOnlyList<X2SkillAlertEntry> GetSkillAlertList(int ability)
    {
        if (ability <= 0 || SkillDatabasePath is not { Length: > 0 } path || !File.Exists(path)) return [];
        var rows = new List<X2SkillAlertEntry>();
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT s.id, COALESCE(ln.en_us, s.name), COALESCE(i.filename, '')
                FROM skill_alert_conditions sac
                JOIN skills s ON s.id=sac.skill_id
                LEFT JOIN localized_texts ln ON ln.tbl_name='skills' AND ln.tbl_column_name='name' AND ln.idx=s.id
                LEFT JOIN icons i ON i.id=s.icon_id
                WHERE s.ability_id=$ability ORDER BY s.id
                """;
            cmd.Parameters.AddWithValue("$ability", ability);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int skillType = reader.GetInt32(0);
                rows.Add(new X2SkillAlertEntry(skillType, reader.GetString(1), IconPath(reader.GetString(2)),
                    !_skillAlertBlacklist.Contains(skillType), false));
            }
        }
        catch (SqliteException) { /* Optional content database is unavailable. */ }
        return rows;
    }
    public virtual double GetSkillAlertBuffRemainingMs(int buffId) => 0;
    public virtual void AddSkillAlertBlacklist(int skillType) => _skillAlertBlacklist.Add(skillType);
    public virtual void RemoveSkillAlertBlacklist(int skillType) => _skillAlertBlacklist.Remove(skillType);
    public virtual void SaveSkillAlertLists(IReadOnlyList<int> blacklist, IReadOnlyList<int> whitelist)
    {
        foreach (var id in blacklist) _skillAlertBlacklist.Add(id);
        foreach (var id in whitelist) _skillAlertBlacklist.Remove(id);
    }
    public virtual int HeirSkillCount => 0;
    public virtual int OriginSkillCount => 0;
    public virtual X2HeirSkillEntry? GetHeirSkill(int ability, int slotIndex, int position) => null;
    public virtual X2HeirSkillEntry? GetOriginSkill(int ability, int slotIndex) => null;
    public virtual X2HeirSkillCounts? GetHeirSkillCounts(int ability, int slotIndex) => null;
    public virtual X2HeirResetInfo? GetHeirResetInfo(int resetKind, int ability, int slotIndex) => null;
    public virtual bool CheckHeirSkillStep(int ability, int slotIndex) => false;
    public virtual bool IsHeirSkill(int skillType)
    {
        if (skillType <= 0 || SkillDatabasePath is not { Length: > 0 } path || !File.Exists(path)) return false;
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM heir_skills WHERE skill_id=$id)";
            cmd.Parameters.AddWithValue("$id", skillType);
            return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
        }
        catch (SqliteException) { return false; }
    }
    public virtual void ResetHeirSkills(int resetKind, int ability, int slotIndex) { }
    public virtual void ResetHeirSkillForSlot(int skillType) { }
    public virtual void SelectHeirSkill(int ability, int slotIndex) { }
    public virtual void SelectOriginSkill(int ability) { }
    public virtual int HeirResetCurrency => 0;

    private static string IconPath(string filename) => filename.Length == 0 ? "" : "ui/icon/" + filename;
}

public static partial class X2UnitApi
{
    internal static void InstallSkill(X2LuaHost host, X2GameContext context, IX2UnitData data)
    {
        host.Define("X2Skill", "Info", a => SkillInfo(data.GetSkill(a.Int(0))));
        host.Define("X2Skill", "GetSkillTooltip", a =>
        {
            int id = a.Int(0);
            var tip = data.GetSkillTooltip(id, a.Int(1), a.Int(2)) ?? SkillTooltip(data.GetSkill(id));
            // tooltip.lua tests heirSkillName ~= 0 (then indexes locale.common.heirSkills by it) and isRaceSkill: 0 / false for
            // ordinary skills, never nil
            if (!tip.ContainsKey("heirSkillName")) tip["heirSkillName"] = 0d;
            if (!tip.ContainsKey("isRaceSkill")) tip["isRaceSkill"] = false;
            return tip;
        });

        host.Define("X2SkillAlert", "AddSkillToBlackList", a => { data.AddSkillAlertBlacklist(a.Int(0)); return null; });
        host.Define("X2SkillAlert", "RemoveSkillFromBlackList", a => { data.RemoveSkillAlertBlacklist(a.Int(0)); return null; });
        host.Define("X2SkillAlert", "SaveSkillBlackList", a =>
        {
            data.SaveSkillAlertLists(ReadIntArray(a.Table(0)), ReadIntArray(a.Table(1)));
            return null;
        });
        host.Define("X2SkillAlert", "GetAbilitySkillList", a =>
        {
            var result = new LuaTable();
            var rows = data.GetSkillAlertList(a.Int(0));
            for (int i = 0; i < rows.Count; i++)
                result[(double)(i + 1)] = new LuaTable
                {
                    ["skillType"] = rows[i].SkillType, ["name"] = rows[i].Name,
                    ["iconPath"] = rows[i].IconPath, ["use"] = rows[i].Use,
                    ["existSlot"] = rows[i].ExistSlot
                };
            return result;
        });
        host.Define("X2SkillAlert", "GetBuffRemainTime", a => data.GetSkillAlertBuffRemainingMs(a.Int(0)));
        host.Define("X2SkillAlert", "GetTooltip", a =>
            data.GetSkillTooltip(a.Int(0), 0, 0) ?? SkillInfo(data.GetSkill(a.Int(0))));

        host.Define("X2HeirSkill", "CheckHeirSkillStep", a =>
            data.CheckHeirSkillStep(a.Int(0), a.Int(1)));
        host.Define("X2HeirSkill", "FindHeirSkill", a => HeirSkill(data.GetHeirSkill(a.Int(0), a.Int(1), a.Int(2))));
        host.Define("X2HeirSkill", "GetCurrencyForHeirSkillsReset", _ => data.HeirResetCurrency);
        host.Define("X2HeirSkill", "GetHeirSkillCount", _ => data.HeirSkillCount);
        host.Define("X2HeirSkill", "GetHeirSkillInfoTable", a =>
        {
            var counts = data.GetHeirSkillCounts(a.Int(0), a.Int(1));
            return counts is null ? null : new LuaTable
                { ["count"] = counts.Count, ["abilityCount"] = counts.AbilityCount, ["allCount"] = counts.AllCount };
        });
        host.Define("X2HeirSkill", "GetHeirSkillPos", a => data.GetOriginSkill(a.Int(0), a.Int(1))?.Position);
        host.Define("X2HeirSkill", "GetOriginSkillCount", _ => data.OriginSkillCount);
        host.Define("X2HeirSkill", "GetResetSkillInfo", a =>
        {
            var quote = data.GetHeirResetInfo(a.Int(0), a.Int(1), a.Int(2));
            return quote is null ? null : new LuaTable
                { ["cost"] = (double)quote.Cost, ["ability"] = quote.Ability, ["skill"] = quote.Skill };
        });
        host.Define("X2HeirSkill", "GetSelectedOriginSkillInfo", a =>
        {
            var entry = data.GetOriginSkill(a.Int(0), a.Int(1));
            return entry is null ? null : new LuaTable { ["skill"] = entry.Skill, ["name"] = entry.Name };
        });
        host.Define("X2HeirSkill", "IsHeirSkill", a => data.IsHeirSkill(a.Int(0)));
        host.Define("X2HeirSkill", "ResetHeirSkill", a =>
            { data.ResetHeirSkills(a.Int(0), a.Int(1), a.Int(2)); return null; });
        host.Define("X2HeirSkill", "ResetHeirSkillForSlot", a =>
            { data.ResetHeirSkillForSlot(a.Int(0)); return null; });
        host.Define("X2HeirSkill", "SetHeirSkill", a =>
            { data.SelectHeirSkill(a.Int(0), a.Int(1)); return null; });
        host.Define("X2HeirSkill", "SetOriginSkill", a =>
            { data.SelectOriginSkill(a.Int(0)); return null; });
    }

    private static LuaTable? SkillInfo(X2SkillEntry? skill) => skill is null ? null : new LuaTable
    {
        ["type"] = skill.Id, ["name"] = skill.Name, ["description"] = skill.Description,
        ["ability"] = skill.Ability, ["abilityName"] = skill.AbilityName,
        ["iconPath"] = skill.IconPath, ["skillPoints"] = skill.SkillPoints, ["skillLevel"] = 1,
        ["cooldownTime"] = skill.CooldownMs, ["castingTime"] = skill.CastingMs
    };

    private static LuaTable SkillTooltip(X2SkillEntry? skill) => SkillInfo(skill) ?? new LuaTable();

    private static LuaTable? HeirSkill(X2HeirSkillEntry? entry) => entry is null ? null : new LuaTable
    {
        ["skill"] = entry.Skill, ["name"] = entry.Name, ["activeType"] = entry.ActiveType,
        ["hasHeirSkill"] = entry.HasHeirSkill, ["pos"] = entry.Position
    };

    private static IReadOnlyList<int> ReadIntArray(LuaTable? table)
    {
        if (table is null) return [];
        var values = new List<int>();
        for (int i = 1; table.TryGetValue((double)i, out var value); i++)
            if (value is double number) values.Add((int)number);
        return values;
    }
}
