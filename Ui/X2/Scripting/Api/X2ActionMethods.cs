#nullable enable
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>One-based action-bar entry. Empty entries are absent from ActionSlots.</summary>
public sealed record X2ActionEntry(string Type, string Info, string ActionBarInfo, string Tooltip,
    IReadOnlyDictionary<string, object?>? Combo = null, bool AutoRepeat = false, bool Current = false,
    double Cooldown = 0, double CooldownEx = 0);

/// <summary>A combat resource as displayed by combat_resource.lua. Time values are milliseconds.</summary>
public sealed record X2CombatResourceEntry(int GroupType, int UiType, int Ability,
    double Resource1Current, double Resource1Max, double Resource2Current = 0, double Resource2Max = 0,
    string Resource1ColorKey = "", string Resource2ColorKey = "", string? IconPath = null,
    string? Tooltip = null, bool IsDefaultResource = true, int RecoveryResourceType = 0,
    double RecoveryCycleTime = 0);

/// <summary>
/// Hero data is server-derived and varies by election season. Record fields are plain .NET dictionaries;
/// keys mirror the names consumed by the Lua hero widgets (rank, name, score, leadership, etc.).
/// Lists are one-based when converted to Lua.
/// </summary>
public sealed record X2HeroState
{
    public bool IsHero { get; init; }
    public bool IsTopLevelHero { get; init; }
    public bool IsVoter { get; init; }
    public bool IsCandidate { get; init; }
    public bool IsElectionPeriod { get; init; }
    public bool IsAlreadyVoted { get; init; }
    public bool CanAddReputation { get; init; }
    public int ClientFactionId { get; init; }
    public string ClientFactionName { get; init; } = "";
    public int FactionHeroCount { get; init; }
    public int HeroCandidateCount { get; init; }
    public double RemainTimeToAnnounceHero { get; init; }
    public IReadOnlyDictionary<string, object?>? DominionPoints { get; init; }
    public IReadOnlyDictionary<string, object?>? AbstainPeriod { get; init; }
    public IReadOnlyDictionary<string, object?>? ElectionPeriod { get; init; }
    public IReadOnlyDictionary<string, object?>? Schedule { get; init; }
    public IReadOnlyDictionary<string, object?>? MyScore { get; init; }
    public IReadOnlyDictionary<string, object?>? MyHeroBonus { get; init; }
    public IReadOnlyDictionary<string, object?>? ReputationCondition { get; init; }
    public IReadOnlyDictionary<string, object?>? VoterCondition { get; init; }
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, object?>> ActivePeriods { get; init; }
        = new Dictionary<int, IReadOnlyDictionary<string, object?>>();
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyDictionary<string, object?>>> HeroLists { get; init; }
        = new Dictionary<int, IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyDictionary<string, object?>>> RankingData { get; init; }
        = new Dictionary<int, IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
    public IReadOnlyDictionary<int, IReadOnlyList<IReadOnlyDictionary<string, object?>>> FactionScores { get; init; }
        = new Dictionary<int, IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Candidates { get; init; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> HeroFactions { get; init; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> RankFactions { get; init; } = [];
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> HeroBonus { get; init; } = [];
}

public partial interface IX2UnitData
{
    /// <summary>Persisted one-based action slots and current page, supplied by the live character state.</summary>
    IReadOnlyDictionary<int, X2ActionEntry> ActionSlots { get; }
    int ActionBarPage { get; }
    int ActionLastIndex { get; }
    /// <summary>Requests to change action bindings; server/profile changes are reflected back into ActionSlots.</summary>
    void ClearAction(int slot);
    void PlaceAction(int slot);
    void SetActionSpell(int slot, int spellId);
    void SetActionBarPage(int page);
    void SetBaseActionBarEmptySlotCount(int count);
    IReadOnlyList<X2CombatResourceEntry> CombatResources { get; }
    /// <summary>Whether the UI must refresh a group's max gauge value.</summary>
    bool CheckCombatResourceMaxPoint(int groupType);
    X2HeroState Hero { get; }
    void GiveDominionPoint(int zoneGroup);
    void RequestHeroAbstain();
    void RequestHeroElection(IReadOnlyList<int> ranks);
    void RequestHeroFactionScores(int factionId);
    void RequestHeroRankData(int factionId);
    void VoteReputation(int point);
}

public partial class NullUnitData
{
    public virtual IReadOnlyDictionary<int, X2ActionEntry> ActionSlots { get; } = new Dictionary<int, X2ActionEntry>();
    public virtual int ActionBarPage => 1;
    public virtual int ActionLastIndex => 0;
    public virtual void ClearAction(int slot) { }
    public virtual void PlaceAction(int slot) { }
    public virtual void SetActionSpell(int slot, int spellId) { }
    public virtual void SetActionBarPage(int page) { }
    public virtual void SetBaseActionBarEmptySlotCount(int count) { }
    public virtual IReadOnlyList<X2CombatResourceEntry> CombatResources { get; } = [];
    public virtual bool CheckCombatResourceMaxPoint(int groupType) => false;
    public virtual X2HeroState Hero { get; } = new();
    public virtual void GiveDominionPoint(int zoneGroup) { }
    public virtual void RequestHeroAbstain() { }
    public virtual void RequestHeroElection(IReadOnlyList<int> ranks) { }
    public virtual void RequestHeroFactionScores(int factionId) { }
    public virtual void RequestHeroRankData(int factionId) { }
    public virtual void VoteReputation(int point) { }
}

public static partial class X2UnitApi
{
    private static X2ActionEntry? Entry(IX2UnitData data, LuaArgs a)
        => data.ActionSlots.TryGetValue(a.Int(0), out var e) ? e : null;

    private static object? ToLua(object? value)
    {
        if (value is IReadOnlyDictionary<string, object?> map)
        {
            var t = new LuaTable();
            foreach (var (key, val) in map) t[key] = ToLua(val);
            return t;
        }
        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            var t = new LuaTable();
            var i = 1;
            foreach (var item in sequence) t[(double)i++] = ToLua(item);
            return t;
        }
        return value;
    }

    private static LuaTable ResourceTable(X2CombatResourceEntry r) => new()
    {
        ["groupType"] = r.GroupType, ["uiType"] = r.UiType, ["ability"] = r.Ability,
        ["resource1Current"] = r.Resource1Current, ["resource1Max"] = r.Resource1Max,
        ["resource2Current"] = r.Resource2Current, ["resource2Max"] = r.Resource2Max,
        ["resource1ColorKey"] = r.Resource1ColorKey, ["resource2ColorKey"] = r.Resource2ColorKey,
        ["iconPath"] = r.IconPath, ["tooltip"] = r.Tooltip,
        ["isDefaultResource"] = r.IsDefaultResource, ["recoveryResourceType"] = r.RecoveryResourceType,
        ["recoveryCycleTime"] = r.RecoveryCycleTime
    };

    internal static void InstallAction(X2LuaHost host, X2GameContext context, IX2UnitData data)
    {
        host.Define("X2Action", "BaseActionBarEmptySlotCount", a => { data.SetBaseActionBarEmptySlotCount(a.Int(0)); return null; });
        host.Define("X2Action", "ClearAction", a => { data.ClearAction(a.Int(0)); return null; });
        host.Define("X2Action", "GetActionBarPage", _ => data.ActionBarPage);
        host.Define("X2Action", "GetActionCooldown", a => Entry(data, a)?.Cooldown);
        host.Define("X2Action", "GetActionCooldownEx", a => Entry(data, a)?.CooldownEx);
        host.Define("X2Action", "GetActionInfo", a => Entry(data, a)?.Info ?? "");
        host.Define("X2Action", "GetActionInfoForActionBar", a => Entry(data, a)?.ActionBarInfo ?? "");
        host.Define("X2Action", "GetActionLastIndex", _ => data.ActionLastIndex);
        host.Define("X2Action", "GetActionTooltipText", a => Entry(data, a)?.Tooltip ?? "");
        host.Define("X2Action", "GetActionType", a => Entry(data, a)?.Type ?? "none");
        host.Define("X2Action", "GetComboActionInfo", a => Entry(data, a)?.Combo is { } c ? ToLua(c) : new LuaTable());
        host.Define("X2Action", "IsActionEmtpty", a => Entry(data, a) is null);
        host.Define("X2Action", "IsAutoRepeatAction", a => Entry(data, a)?.AutoRepeat ?? false);
        host.Define("X2Action", "IsCurrentAction", a => Entry(data, a)?.Current ?? false);
        host.Define("X2Action", "IsEmptySlot", a => Entry(data, a) is null);
        host.Define("X2Action", "PlaceAction", a => { data.PlaceAction(a.Int(0)); return null; });
        host.Define("X2Action", "SetActionBarPage", a => { data.SetActionBarPage(a.Int(0)); return data.ActionBarPage; });
        host.Define("X2Action", "SetActionSpell", a => { data.SetActionSpell(a.Int(0), a.Int(1)); return null; });

        host.Define("X2CombatResource", "CheckCombatResourceMaxPointByGroupType", a => data.CheckCombatResourceMaxPoint(a.Int(0)));
        host.Define("X2CombatResource", "GetCombatResourceInfo", _ =>
        {
            var t = new LuaTable();
            for (var i = 0; i < data.CombatResources.Count; i++) t[(double)(i + 1)] = ResourceTable(data.CombatResources[i]);
            return t;
        });
        host.Define("X2CombatResource", "GetCombatResourceInfoByGroupType", a =>
        {
            foreach (var r in data.CombatResources) if (r.GroupType == a.Int(0)) return ResourceTable(r);
            return null;
        });

        host.Define("X2Hero", "CanAddReputation", _ => data.Hero.CanAddReputation);
        host.Define("X2Hero", "DominionPointCount", _ => ToLua(data.Hero.DominionPoints ?? new Dictionary<string, object?> { ["personalPoint"] = 0 }));
        host.Define("X2Hero", "GetAbstainPeriod", _ => ToLua(data.Hero.AbstainPeriod));
        host.Define("X2Hero", "GetActivedHeroPeriod", a => data.Hero.ActivePeriods.TryGetValue(a.Int(0), out var v) ? ToLua(v) : null);
        host.Define("X2Hero", "GetCandidateList", _ => ToLua(data.Hero.Candidates));
        host.Define("X2Hero", "GetClientFactionID", _ => data.Hero.ClientFactionId);
        host.Define("X2Hero", "GetClientFactionName", _ => data.Hero.ClientFactionName);
        host.Define("X2Hero", "GetElectionPeriod", _ => ToLua(data.Hero.ElectionPeriod));
        host.Define("X2Hero", "GetFactionHeroCount", _ => data.Hero.FactionHeroCount);
        host.Define("X2Hero", "GetFactionScores", a => data.Hero.FactionScores.TryGetValue(a.Int(0), out var v) ? ToLua(v) : null);
        host.Define("X2Hero", "GetHeroBonus", _ => ToLua(data.Hero.HeroBonus));
        host.Define("X2Hero", "GetHeroCandidateCount", _ => data.Hero.HeroCandidateCount);
        host.Define("X2Hero", "GetHeroFactions", _ => ToLua(data.Hero.HeroFactions));
        host.Define("X2Hero", "GetHeroList", a => data.Hero.HeroLists.TryGetValue(a.Int(0), out var v) ? ToLua(v) : new LuaTable());
        host.Define("X2Hero", "GetMyHeroBonusInfo", _ => ToLua(data.Hero.MyHeroBonus));
        host.Define("X2Hero", "GetMyScore", _ => ToLua(data.Hero.MyScore ?? new Dictionary<string, object?> { ["score"] = 0, ["leadership"] = 0 }));
        host.Define("X2Hero", "GetRankFactions", _ => ToLua(data.Hero.RankFactions));
        host.Define("X2Hero", "GetRankingData", a => data.Hero.RankingData.TryGetValue(a.Int(0), out var v) ? ToLua(v) : new LuaTable());
        host.Define("X2Hero", "GetRemainTimeToAnnounceHero", _ => data.Hero.RemainTimeToAnnounceHero);
        host.Define("X2Hero", "GetReputationCondition", _ => ToLua(data.Hero.ReputationCondition ?? new Dictionary<string, object?> { ["level"] = 0, ["leadership_point"] = 0 }));
        host.Define("X2Hero", "GetScheduleInfo", _ => ToLua(data.Hero.Schedule));
        host.Define("X2Hero", "GetVoterCondition", _ => ToLua(data.Hero.VoterCondition));
        host.Define("X2Hero", "GiveDominionPoint", a => { data.GiveDominionPoint(a.Int(0)); return null; });
        host.Define("X2Hero", "IsAlreadyVoted", _ => data.Hero.IsAlreadyVoted);
        host.Define("X2Hero", "IsCandidate", _ => data.Hero.IsCandidate);
        host.Define("X2Hero", "IsElectionPeriod", _ => data.Hero.IsElectionPeriod);
        host.Define("X2Hero", "IsHero", _ => data.Hero.IsHero);
        host.Define("X2Hero", "IsTopLevelHero", _ => data.Hero.IsTopLevelHero);
        host.Define("X2Hero", "IsVoter", _ => data.Hero.IsVoter);
        host.Define("X2Hero", "RequestAbstain", _ => { data.RequestHeroAbstain(); return null; });
        host.Define("X2Hero", "RequestElection", a =>
        {
            var ranks = new List<int>();
            if (a.Table(0) is { } t)
                for (var i = 1; t.TryGetValue((double)i, out var raw); i++)
                    if (int.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out var rank)) ranks.Add(rank);
            data.RequestHeroElection(ranks);
            return null;
        });
        host.Define("X2Hero", "RequestFactionScores", a => { data.RequestHeroFactionScores(a.Int(0)); return null; });
        host.Define("X2Hero", "RequestRankData", a => { data.RequestHeroRankData(a.Int(0)); return 0; });
        host.Define("X2Hero", "VoteReputation", a => { data.VoteReputation(a.Int(0)); return null; });
    }
}
