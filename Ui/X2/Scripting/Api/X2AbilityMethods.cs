#nullable enable
using Microsoft.Data.Sqlite;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Live skill-tree, proficiency, and saved class state. The host supplies this from the session.</summary>
public sealed record X2AbilityState
{
    public IReadOnlyList<int> Active { get; init; } = [];
    public IReadOnlyList<int> View { get; init; } = [];
    public IReadOnlyDictionary<int, X2AbilityProgress> Progress { get; init; } = new Dictionary<int, X2AbilityProgress>();
    public IReadOnlyList<X2SavedAbilitySet> SavedSets { get; init; } = [];
    public int UsableSavedSetCount { get; init; }
    public int CurrentSavedSetIndex { get; init; } = -1;
    public int TotalSkillPoints { get; init; }
    public int UsedSkillPoints { get; init; }
    /// <summary>Ability id to the skill ids shown by the skill book, in display order.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> ActiveSkillIds { get; init; } = new Dictionary<int, IReadOnlyList<int>>();
    /// <summary>Slot-kind constants (ACTIVE_SKILL_1, PASSIVE_SKILL_1, etc.) to visible slot counts.</summary>
    public IReadOnlyDictionary<int, int> SlotCounts { get; init; } = new Dictionary<int, int>();
    /// <summary>Absolute skill-book slot index to SAT_* state.</summary>
    public IReadOnlyDictionary<int, int> SlotActiveTypes { get; init; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, string> SlotNames { get; init; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, X2ActabilityProgress> Actabilities { get; init; } = new Dictionary<int, X2ActabilityProgress>();
}

public sealed record X2AbilityProgress(int Level = 1, double LevelPercent = 0, long Exp = 0,
    long NextLevelTotalExp = 0, int TotalSkillPoints = 0, int UsedSkillPoints = 0);
public sealed record X2SavedAbilitySet(IReadOnlyList<int> Abilities, IReadOnlyList<int> ActiveSkills,
    IReadOnlyList<int> PassiveBuffs, int TotalSkillPoints = 0, int UsedSkillPoints = 0);
public sealed record X2ActabilityProgress(int Type, string Name, int Grade, long Point,
    long ModifyPoint = 0, long AccumulatedPoint = 0);

public partial interface IX2UnitData
{
    /// <summary>Current character skill-tree state. Empty before the player data arrives.</summary>
    X2AbilityState AbilityState { get; }
    /// <summary>Send an ability operation to the host. The resulting state is delivered by server events.</summary>
    void RequestAbility(string operation, params object?[] arguments);
}

public partial class NullUnitData
{
    public virtual X2AbilityState AbilityState => new();
    public virtual void RequestAbility(string operation, params object?[] arguments) { }
}

public static partial class X2UnitApi
{
    // enum_abilities in compact.sqlite3, ids 0..14. The next four ids are reserved slots.
    private static readonly string[] AbilityNames =
    ["general", "fight", "illusion", "adamant", "will", "death", "wild", "magic",
     "vocation", "romance", "love", "hatred", "assassin", "madness", "pleasure"];

    private sealed record AbilityCatalog(
        IReadOnlyDictionary<int, IReadOnlyList<int>> Skills,
        IReadOnlyDictionary<int, IReadOnlyList<int>> Passives,
        IReadOnlyDictionary<int, (int Ability, int Buff, int Points)> PassiveInfo);

    // Static game definitions are read only once and never used as character state.
    private static readonly Lazy<AbilityCatalog> StaticAbilityCatalog = new(LoadAbilityCatalog);

    internal static int SkillAt(int ability, int position) => position > 0 &&
        StaticAbilityCatalog.Value.Skills.TryGetValue(ability, out var skills) && position <= skills.Count
            ? skills[position - 1] : 0;

    internal static int PassiveBuffAt(int ability, int position) => position > 0 &&
        StaticAbilityCatalog.Value.Passives.TryGetValue(ability, out var buffs) && position <= buffs.Count &&
        StaticAbilityCatalog.Value.PassiveInfo.TryGetValue(buffs[position - 1], out var info)
            ? info.Buff : 0;

    private static AbilityCatalog LoadAbilityCatalog()
    {
        var skills = new Dictionary<int, IReadOnlyList<int>>();
        var passives = new Dictionary<int, IReadOnlyList<int>>();
        var passiveInfo = new Dictionary<int, (int, int, int)>();
        try
        {
            var database = ClientPaths.Database;
            if (!File.Exists(database)) return new(skills, passives, passiveInfo);
            using var db = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
            db.Open();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, ability_id FROM skills WHERE ability_id BETWEEN 1 AND 14 AND show = 't' ORDER BY ability_id, ability_level, req_points, id";
                using var rows = cmd.ExecuteReader();
                var temp = new Dictionary<int, List<int>>();
                while (rows.Read())
                {
                    var ability = rows.GetInt32(1);
                    if (!temp.TryGetValue(ability, out var ids)) temp[ability] = ids = [];
                    ids.Add(rows.GetInt32(0));
                }
                foreach (var (id, ids) in temp) skills[id] = ids;
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, ability_id, buff_id, skill_points FROM passive_buffs WHERE ability_id BETWEEN 1 AND 14 ORDER BY ability_id, req_points, id";
                using var rows = cmd.ExecuteReader();
                var temp = new Dictionary<int, List<int>>();
                while (rows.Read())
                {
                    var id = rows.GetInt32(0); var ability = rows.GetInt32(1);
                    if (!temp.TryGetValue(ability, out var ids)) temp[ability] = ids = [];
                    ids.Add(id);
                    passiveInfo[id] = (ability, rows.GetInt32(2), rows.GetInt32(3));
                }
                foreach (var (id, ids) in temp) passives[id] = ids;
            }
        }
        catch (SqliteException)
        {
            // The UI can still open before an external content database is installed.
        }
        return new(skills, passives, passiveInfo);
    }

    internal static void InstallAbility(X2LuaHost host, X2GameContext context, IX2UnitData data)
    {
        const string ns = "X2Ability";
        X2AbilityState S() => data.AbilityState;
        int[] Active() => (S().Active.Count > 0 ? S().Active :
            context.Units.Get(context.Units.PlayerId)?.Abilities ?? []).Where(x => x > 0 && x < 15).Take(3).ToArray();
        LuaTable Info(int id)
        {
            var p = S().Progress.GetValueOrDefault(id) ?? new X2AbilityProgress();
            return new LuaTable { ["type"] = (double)id, ["level"] = (double)p.Level,
                ["levelPercent"] = p.LevelPercent, ["exp"] = (double)p.Exp,
                ["nextLevelTotalExp"] = (double)p.NextLevelTotalExp };
        }
        LuaTable List(IEnumerable<int> ids)
        {
            var t = new LuaTable(); var index = 1d;
            foreach (var id in ids) t[index++] = Info(id);
            return t;
        }
        LuaTable Numbers(IEnumerable<int> ids)
        {
            var t = new LuaTable(); var index = 1d;
            foreach (var id in ids) t[index++] = (double)id;
            return t;
        }
        IReadOnlyList<int> Skills(int ability) => S().ActiveSkillIds.GetValueOrDefault(ability)
            ?? StaticAbilityCatalog.Value.Skills.GetValueOrDefault(ability) ?? [];
        IReadOnlyList<int> Passives(int ability) => StaticAbilityCatalog.Value.Passives.GetValueOrDefault(ability) ?? [];
        int View(int index) => index is >= 1 and <= 3
            ? (S().View.Count >= index ? S().View[index - 1] : Active().ElementAtOrDefault(index - 1)) : 0;
        object? Request(string operation, LuaArgs a)
        {
            data.RequestAbility(operation, a.Values.Select((_, i) => a[i]).ToArray());
            return null;
        }

        // Class selection, learned classes, and progression.
        host.Define(ns, "GetCombatAbilityMax", _ => 15d);
        host.Define(ns, "GetAbilityStr", a => a.Int(0) is int id && id >= 0 && id < AbilityNames.Length ? AbilityNames[id] : "");
        host.Define(ns, "FindAbilityIndexForStr", a => (double)Array.IndexOf(AbilityNames, a.Str(0) ?? ""));
        host.Define(ns, "GetAllCombatAbility", _ => List(Enumerable.Range(1, 14)));
        host.Define(ns, "GetActiveAbility", _ => List(Active()));
        host.Define(ns, "GetActiveAbilityForSkillAlert", _ => List(Active()));
        host.Define(ns, "NumActiveAbility", _ => (double)Active().Length);
        host.Define(ns, "IsActiveAbility", a => Active().Contains(a.Int(0)));
        // returns a table: tab_heir.lua reads info.active
        host.Define(ns, "IsAcitveAbilityForHeir", a => new LuaTable { ["active"] = Active().Contains(a.Int(0)) });
        host.Define(ns, "GetAbilityFromView", a => View(a.Int(0)) is int id && id > 0 ? (double)id : null);
        host.Define(ns, "SetAbilityToView", a => { data.RequestAbility("SetAbilityToView", a.Int(0), a.Int(1)); return null; });
        host.Define(ns, "ResetAbilityView", a => Request("ResetAbilityView", a));
        host.Define(ns, "GetAbilityInfo", a => Info(a.Int(0)));
        host.Define(ns, "GetAbilityLevel", a => { var id = Array.IndexOf(AbilityNames, a.Str(0) ?? ""); return id < 0 ? 0d : (double)(S().Progress.GetValueOrDefault(id)?.Level ?? 1); });
        host.Define(ns, "GetAbilitySkillReqPoint", _ => 0d);
        host.Define(ns, "GetRecommendAbility", a => List(Enumerable.Range(1, 14).Where(x => x != a.Int(0) && !Active().Contains(x))));
        host.Define(ns, "IsSpecialAbility", a => a.Str(0) is "hatred" or "assassin" or "madness" or "pleasure");
        host.Define(ns, "IsLearnedSpecialAbility", a => Active().Contains(a.Int(0)));
        host.Define(ns, "GetMutationSkillInfo", _ => new LuaTable());
        host.Define(ns, "GetSpecialAbilityLearnItemInfo", _ => null);
        host.Define(ns, "SetSelectSpecialAbility", a => Request("SetSelectSpecialAbility", a));
        host.Define(ns, "LearnSpecialAbility", a => Request("LearnSpecialAbility", a));
        host.Define(ns, "LearnAbility", a => Request("LearnAbility", a));
        host.Define(ns, "SwapAbility", a => Request("SwapAbility", a));
        host.Define(ns, "CanBuyAbilityChange", _ => "0");
        host.Define(ns, "GetAbilityChangeCost", _ => "0");
        host.Define(ns, "GetCurrencyForAbilityChange", _ => 0d);
        host.Define(ns, "IsAbilityChanger", _ => false);
        host.Define(ns, "IsSkillTrainer", _ => false);
        host.Define(ns, "GetSkillPoint", a => { var p = S().Progress.GetValueOrDefault(a.Int(0)); return a.Count == 0 || a.Int(0) <= 0 ? new LuaMulti((double)S().TotalSkillPoints, (double)S().UsedSkillPoints) : new LuaMulti((double)(p?.TotalSkillPoints ?? S().TotalSkillPoints), (double)(p?.UsedSkillPoints ?? S().UsedSkillPoints)); });
        host.Define(ns, "GetNumSkills", _ => (double)S().UsedSkillPoints);
        host.Define(ns, "GetNumSkillsByAbility", a => (double)Skills(a.Int(0)).Count);
        host.Define(ns, "GetResetSkillsCost", _ => 0d);
        host.Define(ns, "GetCurrencyForSkillsReset", _ => 0d);
        host.Define(ns, "AskResetSkills", a => Request("AskResetSkills", a));
        host.Define(ns, "ResetSkills", a => Request("ResetSkills", a));
        host.Define(ns, "CancelPlayerBuff", a => Request("CancelPlayerBuff", a));

        // Saved class sets use zero-based command slots, but Lua arrays are one based.
        host.Define(ns, "GetMaxAbilitySetCount", _ => 5d);
        host.Define(ns, "GetSavedAbilitySets", _ => { var sets = new LuaTable(); var i = 1d; foreach (var set in S().SavedSets) sets[i++] = Numbers(set.Abilities); return new LuaMulti(sets, (double)S().UsableSavedSetCount); });
        host.Define(ns, "GetIndexCurrentJobInSavedJobList", _ => (double)S().CurrentSavedSetIndex);
        host.Define(ns, "GetSavedSkillSet", a => S().SavedSets.ElementAtOrDefault(a.Int(0) - 1) is { } set ? Numbers(set.ActiveSkills) : new LuaTable());
        host.Define(ns, "GetSavedPassiveBuffSet", a => S().SavedSets.ElementAtOrDefault(a.Int(0) - 1) is { } set ? Numbers(set.PassiveBuffs) : new LuaTable());
        host.Define(ns, "GetSkillPointInSavedSkillSet", a => S().SavedSets.ElementAtOrDefault(a.Int(0) - 1) is { } set ? new LuaMulti((double)set.TotalSkillPoints, (double)set.UsedSkillPoints) : new LuaMulti(0d, 0d));
        host.Define(ns, "GetAbilitySetFreeActivationCountInfo", _ => new LuaMulti(0d, 0d));
        host.Define(ns, "GetAbilitySetChangeCost", _ => 0d);
        host.Define(ns, "GetCurrencyForAbilitySetChange", _ => 0d);
        host.Define(ns, "GetExpandAbilitySetSlotInfo", _ => new LuaTable());
        foreach (var command in new[] { "ActiveAbilitySet", "DeleteAbilitySet", "RequestExpandAbilitySetSlot", "SaveAbilitySet", "SelectAbilitySetIndex" })
            host.Define(ns, command, a => Request(command, a));

        // Skill and buff catalogs are supplied by the session when known. An empty list is valid before entry.
        host.Define(ns, "GetAbilityActiveSkills", a => Numbers(Skills(a.Int(0))));
        host.Define(ns, "GetBuffInfo", _ => new LuaTable());
        host.Define(ns, "GetPassiveBuffInfo", a => StaticAbilityCatalog.Value.PassiveInfo.TryGetValue(a.Int(0), out var p)
            ? new LuaTable { ["buffType"] = (double)p.Buff, ["abilityName"] = AbilityNames[p.Ability], ["skillPoints"] = (double)p.Points }
            : new LuaTable());
        host.Define(ns, "GetBuffTooltip", _ => new LuaTable { ["name"] = "", ["description"] = "" });
        host.Define(ns, "GetSpellBookSkillByAbility", a => Skills(a.Int(0)) is { } ids && a.Int(1) >= 1 && a.Int(1) <= ids.Count
            ? new LuaTable { ["skillId"] = (double)ids[a.Int(1) - 1] } : null);
        host.Define(ns, "GetSynergySkills", _ => new LuaTable());
        host.Define(ns, "GetUnitStatusList", _ => new LuaTable());
        host.Define(ns, "GetUnitStatusSkills", _ => new LuaTable());
        host.Define(ns, "GetAbilitySlotCount", a =>
        {
            var kind = a.Int(0);
            var slot = kind switch { 10 or 70 => 1, 30 or 80 => 2, 50 or 90 => 3, _ => 0 };
            var ability = View(slot);
            return (double)(S().SlotCounts.TryGetValue(kind, out var count) ? count :
                kind is 10 or 30 or 50 ? Skills(ability).Count : kind is 70 or 80 or 90 ? Passives(ability).Count : 0);
        });
        host.Define(ns, "GetAbilitySlotActiveType", a => (double)S().SlotActiveTypes.GetValueOrDefault(a.Int(0), 2));
        host.Define(ns, "GetAbilitySlotName", a => S().SlotNames.GetValueOrDefault(a.Int(0)) ?? "");
        host.Define(ns, "GetRaceSkillCount", _ => 0d);
        host.Define(ns, "GetRaceSkillType", _ => null);

        // Proficiency is absent for a fresh character. The session can populate it as progress arrives.
        LuaTable Act(X2ActabilityProgress p) => new() { ["type"] = (double)p.Type, ["name"] = p.Name,
            ["grade"] = (double)p.Grade, ["point"] = (double)p.Point, ["modifyPoint"] = (double)p.ModifyPoint,
            ["accumulatedPoint"] = (double)p.AccumulatedPoint };
        host.Define(ns, "GetMyActabilityInfo", a => Act(S().Actabilities.GetValueOrDefault(a.Int(0)) ?? new X2ActabilityProgress(a.Int(0), "", 0, 0)));
        host.Define(ns, "GetAllMyActabilityInfos", _ => { var t = new LuaTable(); var i = 1d; foreach (var p in S().Actabilities.Values.OrderBy(x => x.Type)) t[i++] = Act(p); return t; });
        host.Define(ns, "GetActabilityViewInfo", _ => new LuaTable());
        host.Define(ns, "GetActabilityViewGroupName", _ => "");
        host.Define(ns, "GetActabilityCountByGrade", _ => 0d);
        host.Define(ns, "GetMinActabilityPoint", _ => 0d);
        host.Define(ns, "GetMaxActabilityPoint", _ => 0d);
        host.Define(ns, "GetRemainCountToNextGrade", _ => 0d);
        host.Define(ns, "GetMaxGrade", _ => 0d);
        host.Define(ns, "GetGradeInfo", _ => new LuaTable { ["name"] = "", ["colorString"] = "FFFFFFFF" });
        host.Define(ns, "GetExpertCount", _ => 0d);
        host.Define(ns, "GetExpertMaxCount", _ => 0d);
        host.Define(ns, "GetIntensifiedExpertCount", _ => new LuaTable());
        host.Define(ns, "GetExpandExpertInfo", _ => new LuaTable());
        host.Define(ns, "GetDownGradeItemInfo", _ => new LuaTable());
        host.Define(ns, "CanUpgradeExpert", _ => false);
        host.Define(ns, "CanDowngradeExpert", _ => false);
        host.Define(ns, "CanExpandExpert", _ => false);
        host.Define(ns, "IsLanguageActability", _ => false);
        host.Define(ns, "GetMyLangauge", _ => null);
        foreach (var command in new[] { "UpgradeExpert", "DowngradeExpert", "ExpandExpert" })
            host.Define(ns, command, a => Request(command, a));

        // Client-only heir-level dialog hooks: no equivalent feature exists in this host yet.
        host.Define(ns, "CanHeirLevelUp", _ => false);
        host.Define(ns, "IsMaxCharHeirLevel", _ => false);
        host.Define(ns, "HeirLevelUpItemInfo", _ => null);
        host.Define(ns, "NeedHeirLevelUpItem", _ => false);
        host.Define(ns, "AskHeirLevelUp", a => Request("AskHeirLevelUp", a));
    }
}
