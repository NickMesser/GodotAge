#nullable enable
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A party or raid member. MemberIndex is the native raid index (normally 1..50).</summary>
public sealed record X2TeamMember(
    uint UnitId, ulong CharacterId, string Name, int JointOrder, int MemberIndex, int Party,
    int Role = 0, bool IsOwner = false, bool IsOfficer = false, bool IsHeadMarker = false);

/// <summary>The looting settings returned as four values by X2Team:GetLootingRule().</summary>
public sealed record X2LootingRule(int Method = 0, string DistributorName = "", int MinimumGrade = 0, bool BindOnPickup = false);

/// <summary>Saved visibility for one party column in a raid frame.</summary>
public sealed record X2PartyVisibility(int JointOrder, int Party, bool Visible = true);

/// <summary>Current party/raid state. Empty Members means that the player is not grouped.</summary>
public sealed record X2TeamState
{
    public IReadOnlyList<X2TeamMember> Members { get; init; } = [];
    public bool IsRaid { get; init; }
    public bool IsJoint { get; init; }
    public bool IsSiegeRaid { get; init; }
    public int MyJointOrder { get; init; } = 1;
    public X2LootingRule LootingRule { get; init; } = new();
    public int DiceBidRule { get; init; }
    public bool CanUseAreaInvitation { get; init; }
    public bool CanAcknowledgeJointBreak { get; init; }
    public bool RefuseAreaInvitation { get; init; }
    public bool HasRecruitment { get; init; }
    public bool PartyFrameVisible { get; init; } = true;
    public bool RaidFrameVisible { get; init; } = true;
    public bool SimpleView { get; init; }
    public IReadOnlyList<X2PartyVisibility> PartyVisibility { get; init; } = [];
    public uint SummonItemType { get; init; }
    public int SummonItemRequired { get; init; }
    public int SummonItemCount { get; init; }
}

/// <summary>A member shown in the battlefield squad windows.</summary>
public sealed record X2SquadMember(
    uint UnitId, ulong CharacterId, string Name, int Level = 0, int GearScore = 0,
    int Role = 0, bool Ready = false, bool Offline = false, bool IsLeader = false,
    ulong NameCacheQueryId = 0);

/// <summary>Current battlefield squad state.</summary>
public sealed record X2SquadInfo
{
    public ulong SquadId { get; init; }
    public int FieldType { get; init; }
    public string FieldName { get; init; } = "";
    public int OpenType { get; init; }
    public bool IsLeader { get; init; }
    public bool IsJoining { get; init; }
    public bool MatchingApplied { get; init; }
    public bool CanLeave { get; init; } = true;
    public int MaximumMembers { get; init; } = 5;
    public IReadOnlyList<X2SquadMember> Members { get; init; } = [];
}

/// <summary>A saved raid roster and its cached members.</summary>
public sealed record X2RosterInfo(long Id, string Title, string RecordDate, IReadOnlyList<X2RosterMember> Members);

/// <summary>A saved roster member; Order is zero based and spans both 50-member raid halves.</summary>
public sealed record X2RosterMember(ulong Id, string Name, int Order);

/// <summary>Limits advertised by the native group-mail roster UI.</summary>
public sealed record X2RosterPolicy(int MinimumSize = 1, int CooldownMinutes = 0, int DailyUseCount = 0,
    int SaveMinimumSize = 1, int SaveCooldownRemainingSeconds = 0, int CurrentLeadershipPoints = 0,
    int RequiredLeadershipPoints = 0, int MaximumRosters = 10);

public partial interface IX2SocialData
{
    /// <summary>Latest server-owned party or raid snapshot.</summary>
    X2TeamState Team { get; }
    /// <summary>Latest battlefield squad snapshot, or null when the character has no squad.</summary>
    X2SquadInfo? Squad { get; }
    /// <summary>Saved content rosters.</summary>
    IReadOnlyList<X2RosterInfo> Rosters { get; }
    X2RosterPolicy RosterPolicy { get; }
    bool IsInstanceQueuedOrJoined { get; }
    bool IsSquadDirectMatchingAvailable(int instanceType);
    bool IsExpeditionSquadContent(int instanceType);
    /// <summary>Requests a team/raid operation; authoritative state arrives later through packets and UI events.</summary>
    void ExecuteTeamCommand(string command, IReadOnlyList<object?> arguments);
    /// <summary>Requests a squad operation and returns whether it was accepted for sending.</summary>
    bool ExecuteSquadCommand(string command, IReadOnlyList<object?> arguments);
    /// <summary>Requests a roster mutation and returns whether it was accepted for sending.</summary>
    bool ExecuteRosterCommand(string command, IReadOnlyList<object?> arguments);
}

public partial class NullSocialData
{
    public virtual X2TeamState Team => new();
    public virtual X2SquadInfo? Squad => null;
    public virtual IReadOnlyList<X2RosterInfo> Rosters => [];
    public virtual X2RosterPolicy RosterPolicy => new();
    public virtual bool IsInstanceQueuedOrJoined => false;
    public virtual bool IsSquadDirectMatchingAvailable(int instanceType) => false;
    public virtual bool IsExpeditionSquadContent(int instanceType) => false;
    public virtual void ExecuteTeamCommand(string command, IReadOnlyList<object?> arguments) { }
    public virtual bool ExecuteSquadCommand(string command, IReadOnlyList<object?> arguments) => false;
    public virtual bool ExecuteRosterCommand(string command, IReadOnlyList<object?> arguments) => false;
}

public static partial class X2SocialApi
{
    internal static void InstallTeam(X2LuaHost host, X2GameContext context, IX2SocialData data)
    {
        object? TeamCommand(string name, LuaArgs a) { data.ExecuteTeamCommand(name, a.Values); return null; }
        bool SquadCommand(string name, LuaArgs a) => data.ExecuteSquadCommand(name, a.Values);
        object? SquadVoid(string name, LuaArgs a) { data.ExecuteSquadCommand(name, a.Values); return null; }
        X2TeamMember? MemberAt(int team, int index) => data.Team.Members.FirstOrDefault(m => m.JointOrder == team && m.MemberIndex == index);
        X2TeamMember? Me() => data.Team.Members.FirstOrDefault(m => m.UnitId == context.Units.PlayerId);

        host.Define("X2Team", "ChangeLootingRule", a => TeamCommand("ChangeLootingRule", a));
        host.Define("X2Team", "CheckCoolTimerByTimerType", _ => true);
        host.Define("X2Team", "ConvertToRaidTeam", a => TeamCommand("ConvertToRaidTeam", a));
        host.Define("X2Team", "CountTeamMembers", a => (double)data.Team.Members.Count(m => m.JointOrder == a.Int(0)));
        host.Define("X2Team", "CountTeamMembersInParty", a => (double)data.Team.Members.Count(m => m.JointOrder == a.Int(0) && m.Party == a.Int(1)));
        host.Define("X2Team", "DismissTeam", a => TeamCommand("DismissTeam", a));
        host.Define("X2Team", "FindMyRegisterSiegeZoneGroupType", _ => null);
        host.Define("X2Team", "GetCanJointBreakAck", _ => data.Team.CanAcknowledgeJointBreak);
        host.Define("X2Team", "GetCanUseAreaInvitation", _ => data.Team.CanUseAreaInvitation);
        host.Define("X2Team", "GetLinkText", _ => ""); // Native hyperlink encoding is unavailable without a recruitment packet.
        host.Define("X2Team", "GetLootingRule", _ => new LuaMulti((double)data.Team.LootingRule.Method, data.Team.LootingRule.DistributorName, (double)data.Team.LootingRule.MinimumGrade, data.Team.LootingRule.BindOnPickup));
        host.Define("X2Team", "GetMaxMembers", _ => 50d);
        host.Define("X2Team", "GetMaxParties", _ => 10d);
        host.Define("X2Team", "GetMaxPartyMembers", _ => 5d);
        host.Define("X2Team", "GetMemberIndex", a => {
            var id = context.Units.Resolve(a.Str(0) ?? "");
            var m = id is null ? null : data.Team.Members.FirstOrDefault(x => x.UnitId == id.Value);
            return m is null ? null : new LuaMulti((double)m.JointOrder, (double)m.MemberIndex);
        });
        host.Define("X2Team", "GetMemberIndexByName", a => {
            var m = data.Team.Members.FirstOrDefault(x => string.Equals(x.Name, a.Str(0), StringComparison.OrdinalIgnoreCase) && (!a.Bool(1) || x.JointOrder == data.Team.MyJointOrder));
            return m is null ? null : new LuaMulti((double)m.JointOrder, (double)m.MemberIndex);
        });
        host.Define("X2Team", "GetMyTeamJointOrder", _ => (double)(Me()?.JointOrder ?? data.Team.MyJointOrder));
        host.Define("X2Team", "GetOwnerIndex", a => (double)(data.Team.Members.FirstOrDefault(m => m.JointOrder == a.Int(0) && m.IsOwner)?.MemberIndex ?? 0));
        host.Define("X2Team", "GetPartyFrameVisible", _ => data.Team.PartyFrameVisible);
        host.Define("X2Team", "GetPartyVisible", a => data.Team.PartyVisibility.FirstOrDefault(p => p.JointOrder == a.Int(0) && p.Party == a.Int(1))?.Visible ?? true);
        host.Define("X2Team", "GetRaidFrameVisible", _ => data.Team.RaidFrameVisible);
        host.Define("X2Team", "GetRaidRecruitExpense", _ => new LuaMulti(0d, 0d));
        host.Define("X2Team", "GetRaidRecruitHeadcountList", _ => TeamNumberArray(5, 10, 20, 30, 40, 50));
        host.Define("X2Team", "GetRaidRecruitHud", _ => null);
        host.Define("X2Team", "GetRaidRecruitSubType", _ => null);
        host.Define("X2Team", "GetRaidRecruitSubTypeList", _ => new LuaTable());
        host.Define("X2Team", "GetRaidRecruitTypeList", _ => new LuaTable());
        host.Define("X2Team", "GetRefuseAreaInvitation", _ => data.Team.RefuseAreaInvitation);
        host.Define("X2Team", "GetRole", a => (double)(MemberAt(a.Int(0), a.Int(1))?.Role ?? 0));
        host.Define("X2Team", "GetSiegeRaidZoneList", _ => new LuaTable());
        host.Define("X2Team", "GetSimpleView", _ => data.Team.SimpleView);
        host.Define("X2Team", "GetSummonItem", _ => new LuaMulti(
            (double)data.Team.SummonItemType,
            // The loaded raidteammanager/manager.lua expects the native four-result form.  The older,
            // unreferenced team_summon.lua expects a superseded three-result form.
            new LuaTable { ["itemType"] = (double)data.Team.SummonItemType, ["type"] = (double)data.Team.SummonItemType },
            (double)data.Team.SummonItemRequired,
            (double)data.Team.SummonItemCount));
        host.Define("X2Team", "GetTeamDiceBidRule", _ => (double)data.Team.DiceBidRule);
        host.Define("X2Team", "GetTeamDistributorName", _ => data.Team.LootingRule.DistributorName);
        host.Define("X2Team", "GetTeamMemberName", a => MemberAt(a.Int(0), a.Int(1))?.Name ?? "");
        host.Define("X2Team", "GetTeamPlayerIndex", _ => (double)(Me()?.MemberIndex ?? 0));
        host.Define("X2Team", "GetTeamPlayerParty", _ => (double)(Me()?.Party ?? 0));
        host.Define("X2Team", "GetTeamPlayerPartyHeadIndex", _ => (double)(Me() is { Party: > 0 } me ? ((me.Party - 1) * 5) + 1 : 0));
        host.Define("X2Team", "GetTeamPlayerSlot", _ => (double)(Me() is { MemberIndex: var i } ? ((i - 1) % 5) + 1 : 0));
        host.Define("X2Team", "GetTeamPlayerTeam", _ => (double)(Me()?.JointOrder ?? 0));
        host.Define("X2Team", "GetTeamRoleType", _ => 0d);
        host.Define("X2Team", "HasMyTeamRecruit", _ => data.Team.HasRecruitment);
        host.Define("X2Team", "InviteArea", a => TeamCommand("InviteArea", a));
        host.Define("X2Team", "InviteToTeam", a => TeamCommand("InviteToTeam", a));
        host.Define("X2Team", "IsCreateAlreadySiegeRaidTeam", _ => false);
        host.Define("X2Team", "IsExistSiegeRaidTeam", _ => false);
        host.Define("X2Team", "IsHeadMarker", a => data.Team.Members.Any(m => m.MemberIndex == a.Int(0) && m.IsHeadMarker));
        host.Define("X2Team", "IsJointLeader", _ => Me()?.IsOwner == true && data.Team.IsJoint);
        host.Define("X2Team", "IsJointTeam", _ => data.Team.IsJoint);
        host.Define("X2Team", "IsMyTeamOwner", a => MemberAt(data.Team.MyJointOrder, a.Int(0))?.IsOwner == true);
        host.Define("X2Team", "IsPartyTeam", _ => data.Team.Members.Count > 0 && !data.Team.IsRaid);
        host.Define("X2Team", "IsPossibleLeaveTeam", _ => data.Team.Members.Count > 0);
        host.Define("X2Team", "IsPossibleMoveTeamMember", a => Me()?.IsOwner == true && MemberAt(data.Team.MyJointOrder, a.Int(0)) is not null);
        host.Define("X2Team", "IsRaidTeam", _ => data.Team.IsRaid);
        host.Define("X2Team", "IsSiegeRaidRecruitType", _ => false);
        host.Define("X2Team", "IsSiegeRaidTeam", _ => data.Team.IsSiegeRaid);
        host.Define("X2Team", "IsSiegeRaidTeamRecruit", _ => false);
        host.Define("X2Team", "IsTeamOfficer", a => data.Team.Members.Any(m => m.MemberIndex == a.Int(0) && m.IsOfficer));
        host.Define("X2Team", "IsTeamOwner", a => MemberAt(a.Int(0), a.Int(1))?.IsOwner == true);

        foreach (var name in new[] { "JointBreakRes", "JointCancel", "JointInfoReq", "JointOk", "KickTeamMember", "KickTeamMemberByName", "LeaveTeam", "MakeTeamOfficer", "MakeTeamOwner", "MoveTeamMember", "MoveTeamMemberToParty", "RaidApplicantAccept", "RaidApplicantAcceptReply", "RaidApplicantAdd", "RaidApplicantDel", "RaidApplicantList", "RaidApplicantReject", "RaidRecruitAdd", "RaidRecruitDel", "RaidRecruitDetail", "RaidRecruitList", "RaidRecruitOption", "RaidRecruitSeachList", "RequestAllSiegeRaidTeamInfo", "RequestSiegeRaidMasterRegisterState", "RequestSiegeRaidRecruitInfo", "RequestSiegeRaidRegisterInfo", "RequestSiegeRaidRegisterList", "RequestSiegeRaidTeamInfo", "RequestSummon", "RequestSummonNotRecv", "RequestSummonReply", "ResetCoolTimerByTimerType", "ShowSiegeRaidRegisterUI", "SiegeRaidRecruitDetail" })
        {
            var command = name;
            host.Define("X2Team", command, a => TeamCommand(command, a));
        }
        host.Define("X2Team", "SetPartyFrameVisible", a => TeamCommand("SetPartyFrameVisible", a));
        host.Define("X2Team", "SetPartyVisible", a => TeamCommand("SetPartyVisible", a));
        host.Define("X2Team", "SetRaidFrameVisible", a => TeamCommand("SetRaidFrameVisible", a));
        host.Define("X2Team", "SetRefuseAreaInvitation", a => TeamCommand("SetRefuseAreaInvitation", a));
        host.Define("X2Team", "SetRole", a => TeamCommand("SetRole", a));
        host.Define("X2Team", "SetSimpleView", a => TeamCommand("SetSimpleView", a));
        host.Define("X2Team", "SetTeamDiceBidRule", a => TeamCommand("SetTeamDiceBidRule", a));

        host.Define("X2Squad", "ApplySquadMatching", a => SquadCommand("ApplySquadMatching", a));
        host.Define("X2Squad", "CanLeaveSquad", _ => data.Squad?.CanLeave == true);
        host.Define("X2Squad", "ChangeOpenType", a => SquadCommand("ChangeOpenType", a));
        foreach (var name in new[] { "CreateSquad", "DelegateSquadLeader", "DelegateSquadLeaderByCId", "DisbandSquad", "DisbandSquadInRecruitList", "EnableLeaveSquad", "ExpelSquad", "ExpelSquadByCId", "JoinSquadByKey" })
        {
            var command = name;
            host.Define("X2Squad", command, a => SquadVoid(command, a));
        }
        host.Define("X2Squad", "EnterSquadMatching", a => SquadCommand("EnterSquadMatching", a));
        host.Define("X2Squad", "GetLinkText", _ => "");
        host.Define("X2Squad", "GetMyRoleInfo", _ => data.Squad is null ? null : new LuaTable { ["role"] = (double)(data.Squad.Members.FirstOrDefault(m => m.UnitId == context.Units.PlayerId)?.Role ?? 0) });
        host.Define("X2Squad", "GetMySquadInfo", _ => TeamSquadTable(data.Squad, context.Units.PlayerId));
        host.Define("X2Squad", "GetSquadList", a => SquadCommand("GetSquadList", a));
        host.Define("X2Squad", "GetSquadMemberListStr", _ => data.Squad is null ? "" : string.Join("\n", data.Squad.Members.Select(m => m.Name)));
        host.Define("X2Squad", "HasMySquad", _ => data.Squad is not null);
        host.Define("X2Squad", "InviteSquad", a => SquadCommand("InviteSquad", a));
        host.Define("X2Squad", "IsAllReady", _ => data.Squad is { Members.Count: > 0 } s && s.Members.All(m => m.Ready || m.IsLeader));
        host.Define("X2Squad", "IsAvailableDirectMatching", a => data.IsSquadDirectMatchingAvailable(a.Int(0)));
        host.Define("X2Squad", "IsExpeditionContents", a => data.IsExpeditionSquadContent(a.Int(0)));
        host.Define("X2Squad", "IsInstanceQueuedOrJoined", _ => data.IsInstanceQueuedOrJoined);
        host.Define("X2Squad", "IsLeader", _ => data.Squad?.IsLeader == true);
        host.Define("X2Squad", "IsReady", _ => data.Squad?.Members.FirstOrDefault(m => m.UnitId == context.Units.PlayerId)?.Ready == true);
        host.Define("X2Squad", "IsSameSquad", a => data.Squad?.Members.Any(m => m.UnitId == (uint)a.Num(0)) == true);
        host.Define("X2Squad", "JoinSquad", a => SquadCommand("JoinSquad", a));
        host.Define("X2Squad", "LeaveSquad", a => SquadCommand("LeaveSquad", a));
        host.Define("X2Squad", "ReadySquad", a => SquadCommand("ReadySquad", a));
        host.Define("X2Squad", "SetMyRole", a => SquadCommand("SetMyRole", a));

        host.Define("X2Roster", "DeleteRoster", a => data.ExecuteRosterCommand("DeleteRoster", a.Values));
        host.Define("X2Roster", "GetGroupMailSendingPolicyInfo", _ => new LuaTable { ["minSize"] = (double)data.RosterPolicy.MinimumSize, ["coolTime"] = (double)data.RosterPolicy.CooldownMinutes, ["dailyUseCnt"] = (double)data.RosterPolicy.DailyUseCount });
        host.Define("X2Roster", "GetNeedLeadershipPointInfo", _ => new LuaTable { ["curLp"] = (double)data.RosterPolicy.CurrentLeadershipPoints, ["periodLp"] = (double)data.RosterPolicy.RequiredLeadershipPoints });
        host.Define("X2Roster", "GetRosterList", _ => TeamRosterList(data.Rosters));
        host.Define("X2Roster", "GetRosterMemberList", a => TeamRosterMembers(data.Rosters.FirstOrDefault(r => r.Id == (long)a.Num(0))));
        host.Define("X2Roster", "GetRosterSaveCooltimeRemainSec", _ => (double)data.RosterPolicy.SaveCooldownRemainingSeconds);
        host.Define("X2Roster", "GetRosterSaveMemberMinSize", _ => (double)data.RosterPolicy.SaveMinimumSize);
        host.Define("X2Roster", "IsRosterEmpty", _ => data.Rosters.Count == 0);
        host.Define("X2Roster", "IsRosterFull", _ => data.Rosters.Count >= data.RosterPolicy.MaximumRosters);
        host.Define("X2Roster", "IsRosterSaveCooltime", _ => data.RosterPolicy.SaveCooldownRemainingSeconds > 0);
        host.Define("X2Roster", "IsValidLeadershipPoint", _ => data.RosterPolicy.CurrentLeadershipPoints >= data.RosterPolicy.RequiredLeadershipPoints);
        host.Define("X2Roster", "SaveRoster", a => { data.ExecuteRosterCommand("SaveRoster", a.Values); return null; });
        host.Define("X2Roster", "SetSendingRoster", a => { data.ExecuteRosterCommand("SetSendingRoster", a.Values); return null; });
    }

    private static LuaTable TeamNumberArray(params int[] values)
    {
        var table = new LuaTable();
        for (var i = 0; i < values.Length; i++) table[(double)(i + 1)] = (double)values[i];
        return table;
    }

    private static LuaTable? TeamSquadTable(X2SquadInfo? squad, uint playerId)
    {
        if (squad is null) return null;
        var t = new LuaTable {
            ["squadId"] = (double)squad.SquadId, ["fieldType"] = (double)squad.FieldType,
            ["fieldName"] = squad.FieldName, ["openType"] = (double)squad.OpenType,
            ["isLeader"] = squad.IsLeader, ["isJoining"] = squad.IsJoining,
            ["matchingApplied"] = squad.MatchingApplied, ["curMemberCount"] = (double)squad.Members.Count,
            ["maxMemberCount"] = (double)squad.MaximumMembers,
            ["isAllReady"] = squad.Members.Count > 0 && squad.Members.All(m => m.Ready || m.IsLeader),
            ["isReady"] = squad.Members.FirstOrDefault(m => m.UnitId == playerId)?.Ready ?? false
        };
        var members = new LuaTable();
        for (var i = 0; i < squad.Members.Count; i++) {
            var m = TeamSquadMemberTable(squad.Members[i]);
            members[(double)(i + 1)] = m;
            t[(double)(i + 1)] = m; // The mini view iterates the result directly; the full view reads memberInfo.
        }
        t["memberInfo"] = members;
        return t;
    }

    private static LuaTable TeamSquadMemberTable(X2SquadMember m) => new() {
        ["unitId"] = (double)m.UnitId, ["cId"] = (double)m.CharacterId, ["name"] = m.Name,
        ["nameCacheQueryId"] = (double)m.NameCacheQueryId, ["level"] = (double)m.Level,
        ["gearScore"] = (double)m.GearScore, ["role"] = (double)m.Role, ["ready"] = m.Ready,
        ["isReady"] = m.Ready, ["offline"] = m.Offline, ["isLeader"] = m.IsLeader
    };

    private static LuaTable TeamRosterList(IReadOnlyList<X2RosterInfo> rosters)
    {
        var t = new LuaTable();
        for (var i = 0; i < rosters.Count; i++) t[(double)(i + 1)] = new LuaTable {
            ["id"] = (double)rosters[i].Id, ["title"] = rosters[i].Title, ["recordDate"] = rosters[i].RecordDate
        };
        return t;
    }

    private static LuaTable? TeamRosterMembers(X2RosterInfo? roster)
    {
        if (roster is null) return null;
        var members = new LuaTable();
        for (var i = 0; i < roster.Members.Count; i++) members[(double)(i + 1)] = new LuaTable {
            ["id"] = (double)roster.Members[i].Id, ["name"] = roster.Members[i].Name, ["order"] = (double)roster.Members[i].Order
        };
        return new LuaTable { ["isCached"] = true, ["members"] = members };
    }
}
