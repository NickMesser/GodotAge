#nullable enable
using System.Globalization;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A visible aura. TimeLeft is in milliseconds, as consumed by buffwindow.lua.</summary>
public sealed record X2UnitAura(int BuffId, string Icon, string Tooltip, double? TimeLeft = null,
    int Stack = 1, int TimeUnit = 1);

/// <summary>Current spell cast. Durations are in milliseconds.</summary>
public sealed record X2UnitCast(string SpellName, double CastingTime, double CurrentCastingTime,
    bool ShowTargetCastingTime = true, bool CastingUseable = false);

/// <summary>Distance in metres. OverDistance asks the UI to show ???.</summary>
public sealed record X2UnitDistance(double Distance, bool OverDistance = false);

/// <summary>Viewport pixel position and positive camera depth.</summary>
public sealed record X2UnitScreenPosition(double X, double Y, double Depth);

/// <summary>A summoned siege unit's combat resource, measured in hundredths by the Lua bar.</summary>
public sealed record X2UnitCombatResource(string Name, string IconPath, int AttachPoint = 0,
    IReadOnlyList<X2UnitCombatResource>? Children = null);

public partial interface IX2UnitData
{
    /// <summary>Returns a unit's ordered auras; kind is buff, debuff, hidden, or raid. Lua indices are one based.</summary>
    IReadOnlyList<X2UnitAura> GetUnitAuras(string token, string kind);
    X2UnitCast? GetUnitCast(string token);
    X2UnitDistance? GetUnitDistance(string token);
    /// <summary>Camera projection for name tags, frames, and chat bubbles; kind selects the client query.</summary>
    X2UnitScreenPosition? GetUnitScreenPosition(string token, string kind);
    /// <summary>Currently displayed bound slave resource and its attached child slaves.</summary>
    X2UnitCombatResource? GetUnitCombatResource();
    /// <summary>Character sheet numeric fields using the exact Lua keys; absent values default to zero.</summary>
    IReadOnlyDictionary<string, double> GetUnitStatistics(string token, bool modifiers);
    /// <summary>Extra numeric fields unavailable in X2UnitInfo, such as gear_score and fatigue.</summary>
    double GetUnitNumber(string token, string key);
    /// <summary>Extra text fields such as world name, owner name, portrait, and class.</summary>
    string? GetUnitText(string token, string key);
    /// <summary>Extra flags such as offline, force_attack, show_helmet and group membership.</summary>
    bool GetUnitFlag(string token, string key);
    /// <summary>Forwards unit requests to the session. State changes arrive via events and are never assumed here.</summary>
    void RequestUnitCommand(string command, IReadOnlyList<object?> arguments);
}

public partial class NullUnitData
{
    public virtual IReadOnlyList<X2UnitAura> GetUnitAuras(string token, string kind) => [];
    public virtual X2UnitCast? GetUnitCast(string token) => null;
    public virtual X2UnitDistance? GetUnitDistance(string token) => null;
    public virtual X2UnitScreenPosition? GetUnitScreenPosition(string token, string kind) => null;
    public virtual X2UnitCombatResource? GetUnitCombatResource() => null;
    public virtual IReadOnlyDictionary<string, double> GetUnitStatistics(string token, bool modifiers) =>
        new Dictionary<string, double>();
    public virtual double GetUnitNumber(string token, string key) => 0;
    public virtual string? GetUnitText(string token, string key) => null;
    public virtual bool GetUnitFlag(string token, string key) => false;
    public virtual void RequestUnitCommand(string command, IReadOnlyList<object?> arguments) { }
}

public static partial class X2UnitApi
{
    private static readonly string[] UnitRaceNames =
        ["none", "nuian", "fairy", "dwarf", "elf", "hariharan", "ferre", "returned", "warborn", "daru"];
    private static readonly string[] UnitAbilityNames =
        ["general", "fight", "illusion", "adamant", "will", "death", "wild", "magic", "vocation", "romance", "love", "hatred", "assassin", "madness", "pleasure"];

    // This list is the 118-method X2Unit surface in X2ApiData.g.cs, not the larger native binding list.
    private const string UnitMethodNames = """
        BanVoteTarget ChallengeTargetToDuel ChangeCosplayVisual Follow GetBlessUthstinInfo GetCombatRelationshipStr
        GetCombatResourceUnitInfos GetCurrentZoneGroup GetDoodadInfoById GetDoodadScreenPosition GetFactionName
        GetGenderStr GetModeActionsCount GetNpcInfo GetNpcTypeIndex GetOverHeadMarker GetOverHeadMarkerUnitId
        GetRace GetRaceStr GetScreenHeight GetScreenPosition GetShortcutSkillCount GetTargetAbilityTemplates
        GetTargetKindType GetTargetTypeString GetTargetUnitId GetTargetUnitString GetTopLevelFactionId
        GetTopLevelFactionName GetTopLevelFactionNameById GetUnitGradeById GetUnitId GetUnitInfoById
        GetUnitMateType GetUnitMateTypeById GetUnitNameById GetUnitScreenNameTagOffset GetUnitScreenPosition
        GetUnitType GetUnitTypeString GetUnitWorldPosition GetVisualRace GetVisualRaceExpiredTimeStr
        HeirLimitLevelByCharLevel ImpulseUnit IsFirstHitByMeOrMyTeam IsInGlobalWorld IsInResurrectPeaceArea
        IsLifeAlertEffect IsMe IsReporter IsVisualRaceExpired MoveUnit RefreshModeActionBar ReleaseTarget
        ReleaseWatchTarget RemoveAllOverHeadMarker RequestChangeVisualRace RequestResetVisualRace SetOverHeadMarker
        SetWatchTarget ShowHelmet ShowIpnir ShowableEquipInfo StopChangeVisualRaceSkill TargetFrameOpened
        TargetUnit UnitAttr UnitBreath UnitBuff UnitBuffCount UnitBuffTooltip UnitCastingInfo UnitClass
        UnitCombatState UnitCriticalInfo UnitDPSInfo UnitDeBuff UnitDeBuffCount UnitDeBuffTooltip
        UnitDefensivePower UnitDistance UnitFatigue UnitGearScore UnitGender UnitHealth UnitHealthBarSplit
        UnitHealthInfo UnitHealthRecovery UnitHeirIncreases UnitHeirLevel UnitHiddenBuff UnitHiddenBuffCount
        UnitHiddenBuffTooltip UnitInGroup UnitInfo UnitIsAggressiveHostile UnitIsDead UnitIsForceAttack
        UnitIsOffline UnitIsTeamMember UnitLevel UnitMagicResistPower UnitMana UnitManaInfo UnitManaRecovery
        UnitMaxHealth UnitMaxMana UnitModifierInfo UnitName UnitNameWithWorld UnitPortraitPath UnitRace
        UnitRemovableDebuff UnitRemovableDebuffCount UnitRemovableDebuffTooltip UnitTeamAuthority UnitVisualRace
        """;

    internal static void InstallUnit(X2LuaHost host, X2GameContext context, IX2UnitData data)
    {
        foreach (var method in UnitMethodNames.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = method;
            // The generated signature labels this numeric, but the stock unit-frame script
            // uses nil to mean that a token (notably watchtarget) has no unit assigned.
            host.Define("X2Unit", name, a => CallUnit(name, a, context, data), nullIsNil: name == "GetUnitId");
        }
    }

    private static object? CallUnit(string method, LuaArgs a, X2GameContext context, IX2UnitData data)
    {
        var token = a.Str(0) ?? "";
        X2UnitInfo? Unit(string t) => context.Units.Resolve(t) is uint id ? context.Units.Get(id) : null;
        var unit = Unit(token);
        double Extra(string key) => data.GetUnitNumber(token, key);
        bool Flag(string key) => data.GetUnitFlag(token, key);
        string? Text(string key) => data.GetUnitText(token, key);
        object? Command()
        {
            data.RequestUnitCommand(method, a.Values.Select((_, i) => a[i]).ToArray());
            return null;
        }
        object? Auras(string kind, bool count, bool tooltip)
        {
            var list = data.GetUnitAuras(token, kind);
            if (count) return (double)list.Count;
            var index = a.Int(1) - 1;
            if (index < 0 || index >= list.Count) return null;
            var aura = list[index];
            if (tooltip) return aura.Tooltip;
            return new LuaTable
            {
                ["buff_id"] = (double)aura.BuffId, ["path"] = aura.Icon,
                ["timeLeft"] = aura.TimeLeft, ["timeUnit"] = (double)aura.TimeUnit,
                ["stack"] = (double)aura.Stack
            };
        }
        object? Stats(bool modifiers)
        {
            if (unit == null) return null;
            var table = new LuaTable();
            foreach (var key in UnitStatisticKeys.Split(' ', StringSplitOptions.RemoveEmptyEntries)) table[key] = 0.0;
            foreach (var pair in data.GetUnitStatistics(token, modifiers)) table[pair.Key] = pair.Value;
            return table;
        }
        switch (method)
        {
            case "UnitName": return unit?.Name;
            case "UnitNameWithWorld": return unit == null ? null : unit.Name + (Text("world_name") is { Length: > 0 } w ? $"@{w}" : "");
            case "UnitLevel": return unit == null ? null : (double)unit.Level;
            case "UnitHeirLevel": return unit == null ? null : (double)unit.HeirLevel;
            case "UnitHealth": return unit == null ? null : (double)unit.Hp;
            case "UnitMaxHealth": return unit == null ? null : (double)unit.MaxHp;
            case "UnitMana": return unit == null ? null : (double)unit.Mp;
            case "UnitMaxMana": return unit == null ? null : (double)unit.MaxMp;
            case "UnitHealthInfo": return unit == null ? null : new LuaMulti((double)unit.Hp, (double)unit.MaxHp,
                unit.MaxHp == 0 ? 0.0 : Math.Round(100.0 * unit.Hp / unit.MaxHp));
            case "UnitManaInfo": return unit == null ? null : new LuaMulti((double)unit.Mp, (double)unit.MaxMp,
                unit.MaxMp == 0 ? 0.0 : Math.Round(100.0 * unit.Mp / unit.MaxMp));
            case "UnitHealthBarSplit": return unit == null ? null : Extra("health_bar_split");
            case "UnitGender": return unit?.Gender == "female" ? 2.0 : unit == null ? null : 1.0;
            case "UnitRace": return unit == null ? null : (double)Math.Max(0, Array.IndexOf(UnitRaceNames, unit.Race));
            case "UnitVisualRace": return unit == null ? null : (double)Math.Max(0, Array.IndexOf(UnitRaceNames, unit.Race));
            case "UnitCombatState": return unit?.InCombat ?? false;
            case "UnitIsDead": return unit?.IsDead ?? false;
            case "UnitIsOffline": return unit == null || Flag("offline");
            case "UnitIsTeamMember": return Flag("team_member");
            case "UnitIsForceAttack": return Flag("force_attack");
            case "UnitIsAggressiveHostile": return Flag("aggressive_hostile");
            case "UnitInGroup": return Flag($"group_{a.Str(1)}");
            case "UnitTeamAuthority": return Extra("team_authority");
            case "UnitClass": return Text("class");
            case "UnitPortraitPath": return Text("portrait_path");
            case "UnitBreath": return Extra("breath");
            case "UnitFatigue": return Extra("fatigue");
            case "UnitGearScore": return a.Bool(1) ? Extra("gear_score").ToString("N0", CultureInfo.InvariantCulture) : Extra("gear_score").ToString(CultureInfo.InvariantCulture);
            case "UnitHealthRecovery": return Extra("health_recovery");
            case "UnitManaRecovery": return Extra("mana_recovery");
            case "UnitDefensivePower": return Extra("defensive_power");
            case "UnitMagicResistPower": return Extra("magic_resist_power");
            case "UnitCriticalInfo": return Stats(false);
            case "UnitDPSInfo": return Stats(false);
            case "UnitInfo": return Stats(false);
            case "UnitModifierInfo": return Stats(true);
            case "UnitHeirIncreases": return Stats(true);
            case "UnitAttr": return a.Str(1) is string key ? (object?)data.GetUnitNumber(token, key) : null;
            case "UnitDistance": return data.GetUnitDistance(token) is { } distance
                ? new LuaTable { ["distance"] = distance.Distance, ["over_distance"] = distance.OverDistance } : null;
            case "UnitCastingInfo": return data.GetUnitCast(token) is { } cast ? new LuaTable
            {
                ["spellName"] = cast.SpellName, ["castingTime"] = cast.CastingTime,
                ["currCastingTime"] = cast.CurrentCastingTime,
                ["showTargetCastingTime"] = cast.ShowTargetCastingTime,
                ["castingUseable"] = cast.CastingUseable
            } : null;
            case "UnitBuff": return Auras("buff", false, false);
            case "UnitBuffCount": return Auras("buff", true, false);
            case "UnitBuffTooltip": return Auras("buff", false, true);
            case "UnitDeBuff": return Auras("debuff", false, false);
            case "UnitDeBuffCount": return Auras("debuff", true, false);
            case "UnitDeBuffTooltip": return Auras("debuff", false, true);
            case "UnitHiddenBuff": return Auras("hidden", false, false);
            case "UnitHiddenBuffCount": return Auras("hidden", true, false);
            case "UnitHiddenBuffTooltip": return Auras("hidden", false, true);
            case "UnitRemovableDebuff": return Auras("raid", false, false);
            case "UnitRemovableDebuffCount": return Auras("raid", true, false);
            case "UnitRemovableDebuffTooltip": return Auras("raid", false, true);
            case "GetUnitId": return context.Units.Resolve(token) is uint id ? id.ToString(CultureInfo.InvariantCulture) : null;
            case "GetUnitNameById": return uint.TryParse(token, out var namedId) ? context.Units.Get(namedId)?.Name : null;
            case "GetUnitInfoById": return uint.TryParse(token, out var infoId) && context.Units.Get(infoId) is { } byId ? UnitInfoTable(byId, data, token) : new LuaTable();
            case "GetUnitGradeById": return uint.TryParse(token, out var gradeId) && context.Units.Get(gradeId) != null ? data.GetUnitNumber(token, "grade") : null;
            case "GetUnitType": return unit?.Type;
            case "GetUnitTypeString": return unit?.Type ?? "";
            case "GetUnitMateType": return double.TryParse(Text("mate_type"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var mateType) ? mateType : 0d;
            case "GetUnitMateTypeById": return double.TryParse(Text("mate_type"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var mateTypeById) ? mateTypeById : 0d;
            case "GetFactionName": return Text("faction_name") ?? unit?.FactionName;
            case "GetTopLevelFactionId": return unit == null ? null : (double)unit.FactionId;
            case "GetTopLevelFactionName": return Text("faction_name") ?? unit?.FactionName;
            case "GetTopLevelFactionNameById": return Text("faction_name") ?? "";
            case "GetTargetUnitId": return context.Units.TargetId == 0 ? null : context.Units.TargetId.ToString(CultureInfo.InvariantCulture);
            case "GetTargetUnitString": return context.Units.TargetId == 0 ? null : "target";
            case "GetTargetTypeString": return context.Units.Get(context.Units.TargetId)?.Type;
            case "GetTargetKindType": return Text("kind_type");
            case "GetTargetAbilityTemplates": return AbilityTemplates(unit ?? new X2UnitInfo());
            case "GetNpcInfo": return Text("npc_info");
            case "GetNpcTypeIndex": return Extra("npc_type_index");
            case "GetCombatRelationshipStr": return Text("combat_relationship") ?? DefaultRelationship(unit, context);
            case "GetOverHeadMarker": return Extra("overhead_marker");
            case "GetOverHeadMarkerUnitId": return Extra($"overhead_marker_unit_{a.Int(0)}");
            case "GetRace": return context.Units.Get(context.Units.PlayerId) is { } player ? (double)Math.Max(0, Array.IndexOf(UnitRaceNames, player.Race)) : null;
            case "GetVisualRace": return context.Units.Get(context.Units.PlayerId) is { } visual ? (double)Math.Max(0, Array.IndexOf(UnitRaceNames, visual.Race)) : null;
            case "GetRaceStr": return a.Int(0) is var race && race >= 0 && race < UnitRaceNames.Length ? UnitRaceNames[race] : "none";
            case "GetGenderStr": return a.Int(0) == 2 ? "female" : "male";
            case "GetVisualRaceExpiredTimeStr": return null;
            case "IsVisualRaceExpired": return false;
            case "IsMe": return context.Units.Resolve(token) == context.Units.PlayerId && context.Units.PlayerId != 0;
            case "IsReporter": return Flag("reporter");
            case "IsFirstHitByMeOrMyTeam": return Flag("first_hit_by_me_or_team");
            case "IsInGlobalWorld": return data.GetUnitFlag("player", "global_world");
            case "IsInResurrectPeaceArea": return data.GetUnitFlag("player", "resurrect_peace_area");
            case "IsLifeAlertEffect": return a.Num(1) > 0 && a.Num(0) / a.Num(1) < 0.2;
            case "GetModeActionsCount": return data.GetUnitNumber("player", "mode_actions_count");
            case "GetShortcutSkillCount": return data.GetUnitNumber("player", "shortcut_skill_count");
            case "HeirLimitLevelByCharLevel": return data.GetUnitNumber("player", "heir_limit_level");
            case "RefreshModeActionBar": return Command();
            case "TargetUnit": context.Units.RequestTarget(context.Units.Resolve(token) ?? 0); return null;
            case "ReleaseTarget": context.Units.RequestTarget(0); return null;
            case "GetBlessUthstinInfo": return BlessUthstin(data, token);
            case "GetCombatResourceUnitInfos": return CombatResources(data);
            case "GetCurrentZoneGroup": return data.GetUnitNumber("player", "zone_group");
            case "GetDoodadInfoById": return Text("doodad_name") is string doodadName
                ? new LuaTable { ["name"] = doodadName, ["type"] = "doodad",
                    ["faction"] = Text("doodad_faction") ?? "neutral" } : null;
            case "GetDoodadScreenPosition": return ScreenPosition(data, token, "doodad");
            case "GetScreenHeight": return data.GetUnitNumber(token, "screen_height");
            case "GetScreenPosition": return ScreenPosition(data, token, "screen");
            case "GetUnitScreenNameTagOffset": return ScreenPosition(data, token, "name_tag");
            case "GetUnitScreenPosition": return ScreenPosition(data, token, "unit");
            case "GetUnitWorldPosition": return unit is null ? null : new LuaTable { ["x"] = (double)unit.X, ["y"] = (double)unit.Y, ["z"] = (double)unit.Z };
            case "ShowableEquipInfo": return unit?.Type == "character";
            case "TargetFrameOpened": return null;
            // UI commands are sent to the session; their effects are reflected only after authoritative events.
            case "BanVoteTarget": case "ChallengeTargetToDuel": case "ChangeCosplayVisual": case "Follow":
            case "ImpulseUnit": case "MoveUnit": case "ReleaseWatchTarget": case "RemoveAllOverHeadMarker":
            case "RequestChangeVisualRace": case "RequestResetVisualRace": case "SetOverHeadMarker":
            case "SetWatchTarget": case "ShowHelmet": case "ShowIpnir": case "StopChangeVisualRaceSkill":
                return Command();
            default: return null;
        }
    }

    /// <summary>Without live combat relations: oneself and one's own faction are friendly, other characters neutral, other units hostile.</summary>
    private static string DefaultRelationship(X2UnitInfo? unit, X2GameContext context)
    {
        if (unit == null) return "neutral";
        if (unit.Id == context.Units.PlayerId) return "friendly";
        var me = context.Units.Get(context.Units.PlayerId);
        if (me != null && unit.FactionId != 0 && unit.FactionId == me.FactionId) return "friendly";
        return unit.Type == "character" ? "neutral" : "hostile";
    }

    private static LuaTable AbilityTemplates(X2UnitInfo unit)
    {
        var result = new LuaTable();
        for (var i = 0; i < 3; i++)
        {
            var id = i < unit.Abilities.Count ? unit.Abilities[i] : 0;
            result[(double)(i + 1)] = new LuaTable
            {
                ["index"] = (double)id,
                ["name"] = id >= 0 && id < UnitAbilityNames.Length ? UnitAbilityNames[id] : "invalid ability"
            };
        }
        return result;
    }

    private static LuaTable UnitInfoTable(X2UnitInfo unit, IX2UnitData data, string token) => new()
    {
        ["name"] = unit.Name, ["unit_id"] = (double)unit.Id, ["type"] = unit.Type,
        ["faction"] = data.GetUnitFlag(token, "hostile") ? "hostile" : "friendly",
        ["owner_name"] = data.GetUnitText(token, "owner_name"),
        ["house_category"] = data.GetUnitText(token, "house_category")
    };

    private static LuaTable BlessUthstin(IX2UnitData data, string token)
    {
        var result = new LuaTable();
        foreach (var key in new[] { "str", "dex", "int", "spi", "sta" })
        {
            result["default_" + key] = data.GetUnitNumber(token, "uthstin_default_" + key);
            result["applied_" + key] = data.GetUnitNumber(token, "uthstin_applied_" + key);
        }
        foreach (var key in new[] { "totalappliedStats", "MaxStats", "applyCount", "applySpecialCount", "extendMaxStatsLimit", "applyCountLimit" })
            result[key] = data.GetUnitNumber(token, "uthstin_" + key);
        return result;
    }

    private static LuaTable CombatResources(IX2UnitData data)
    {
        var table = new LuaTable();
        if (data.GetUnitCombatResource() is not { } resource) return table;
        var bound = new LuaTable { ["name"] = resource.Name, ["iconPath"] = resource.IconPath };
        var children = new LuaTable();
        if (resource.Children is { } entries)
            for (var i = 0; i < entries.Count; i++)
                children[(double)(i + 1)] = new LuaTable
                {
                    ["name"] = entries[i].Name,
                    ["iconPath"] = entries[i].IconPath,
                    ["attachPoint"] = (double)entries[i].AttachPoint
                };
        bound["childSlaveInfos"] = children;
        table["boundslave"] = bound;
        return table;
    }

    private static object? ScreenPosition(IX2UnitData data, string token, string kind) =>
        data.GetUnitScreenPosition(token, kind) is { } position
            ? new LuaMulti(position.X, position.Y, position.Depth) : null;

    // Character sheet keys observed in characterinfo/character_info.lua and detail panes.
    private const string UnitStatisticKeys = "anti_miss armor armor_percentage attack_anim_speed attack_anim_speed_mul backattack_melee_damage_mul backattack_ranged_damage_mul backattack_spell_damage_mul battle_resist battle_resist_rate block_mul block_rate bulls_eye bulls_eye_rate casting_time detect_stealth_range detect_stealth_range_mul dex dodge_mul dodge_rate drop_rate_mul exp_mul flexibility flexibility_bonus flexibility_ratio gear_score heal_critical_bonus heal_critical_mul heal_critical_rate heal_damage_mul heal_damage_mul_anti_npc heal_dps heal_mul heal_mul_only_heal health_regen ignore_armor ignore_shield_bonus_mul ignore_shield_chance incoming_damage_mul incoming_damage_mul_anti_npc incoming_heal_mul incoming_melee_damage_add_anti_npc incoming_melee_damage_mul incoming_melee_damage_val incoming_ranged_damage_add_anti_npc incoming_ranged_damage_mul incoming_ranged_damage_val incoming_siege_damage_mul incoming_spell_damage_add_anti_npc incoming_spell_damage_mul incoming_spell_damage_val int loot_gold_mul magic_penetration magic_resist magic_resist_percentage mana_regen max_health max_mana melee_attack_speed_mul melee_critical_bonus melee_critical_mul melee_critical_rate melee_damage_mul melee_damage_mul_anti_npc melee_damage_mul_anti_pc melee_dps melee_max_dps melee_min_dps melee_parry_mul melee_parry_rate melee_success_rate move_speed move_speed_rate persistent_health_regen persistent_mana_regen ranged_attack_speed_mul ranged_critical_bonus ranged_critical_mul ranged_critical_rate ranged_damage_mul ranged_damage_mul_anti_npc ranged_damage_mul_anti_pc ranged_dps ranged_max_dps ranged_min_dps ranged_speed ranged_success_rate spell_critical_bonus spell_critical_mul spell_critical_rate spell_damage_critical_bonus spell_damage_critical_mul spell_damage_mul spell_damage_mul_anti_npc spell_damage_mul_anti_pc spell_dps spell_success_rate spi sta str";
}
