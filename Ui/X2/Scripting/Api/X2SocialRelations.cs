#nullable enable
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A transport-neutral value returned by the social session adapter.</summary>
public sealed record X2RelationValue
{
    public object? Scalar { get; init; }
    public IReadOnlyDictionary<object, X2RelationValue>? Fields { get; init; }
    public IReadOnlyList<X2RelationValue>? Returns { get; init; }

    public static X2RelationValue From(object? value) => new() { Scalar = value };
    public static X2RelationValue Table(IReadOnlyDictionary<object, X2RelationValue>? fields = null)
        => new() { Fields = fields ?? new Dictionary<object, X2RelationValue>() };
    public static X2RelationValue Array(IEnumerable<X2RelationValue> values)
    {
        var fields = new Dictionary<object, X2RelationValue>();
        var index = 1d;
        foreach (var value in values) fields[index++] = value;
        return Table(fields);
    }
    public static X2RelationValue Multi(params X2RelationValue[] values) => new() { Returns = values };
}

/// <summary>A social mutation or request passed to the network-backed host.</summary>
public sealed record X2RelationCommand(string Table, string Method, IReadOnlyList<object?> Arguments);

/// <summary>Elapsed time since a character left the world; fields are read directly by logic/time.lua.</summary>
public sealed record X2SocialElapsedTime(int Year = 0, int Month = 0, int Day = 0, int Hour = 0, int Minute = 0);

/// <summary>A friend row. GetFriendList exposes these as the ten positional FRIEND_COL values.</summary>
public sealed record X2FriendEntry(
    string Name,
    int Level,
    IReadOnlyList<int> Abilities,
    IReadOnlyList<string> Position,
    string Race,
    bool Online,
    bool InParty,
    int HeirLevel,
    uint FactionId,
    X2SocialElapsedTime? LastSeen = null);

/// <summary>A pending request row ({ id, name }). Id stays a string because character ids exceed Lua-double precision.</summary>
public sealed record X2PendingFriend(string Id, string Name);

/// <summary>A block-list row ({ unitName, name, worldName }).</summary>
public sealed record X2BlockedUser(string UnitName, string Name, string WorldName = "");

public sealed record X2FriendState(
    IReadOnlyList<X2FriendEntry> Friends,
    IReadOnlyList<X2PendingFriend> ReceivedRequests,
    IReadOnlyList<X2PendingFriend> SentRequests,
    IReadOnlyList<X2BlockedUser> BlockedUsers,
    bool IsLoaded = false,
    int FriendsPerPage = 10);

/// <summary>The keyed table consumed by both family information layouts.</summary>
public sealed record X2FamilyInfo
{
    public string Name { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public string OwnerRole { get; init; } = "";
    public string GradeName { get; init; } = "";
    public string MyTitle { get; init; } = "";
    public string MyRoleName { get; init; } = "";
    public string MyRoleIcon { get; init; } = "";
    public string Content1 { get; init; } = "";
    public string Content2 { get; init; } = "";
    public string Notice { get; init; } = "";
    public long Experience { get; init; }
    public long MaxExperience { get; init; }
    public long LivingPoint { get; init; }
    public int MemberCount { get; init; }
    public int IncreasedMemberCount { get; init; }
    public int MaxMemberCount { get; init; }
    public int MaxIncreasedMemberCount { get; init; }
    public bool IsOwner { get; init; }
    public bool IsAtMemberLimit { get; init; }
    public bool IsAtMaximumSize { get; init; }
}

public sealed record X2FamilyMember(string CharacterId, string Name, int Level, int HeirLevel,
    string Title, string Role, bool Online);

public sealed record X2FamilyState(X2FamilyInfo? Info, IReadOnlyList<X2FamilyMember> Members);

/// <summary>The GetMyExpeditionInfo fields read by the information tab.</summary>
public sealed record X2ExpeditionInfo
{
    public uint Id { get; init; }
    public string Name { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public string Notice { get; init; } = "";
    public string ContributionPoint { get; init; } = "0";
    public long DailyContributionPoint { get; init; }
    public long MaxContributionPoint { get; init; }
    public IReadOnlyList<int> Interests { get; init; } = [];
    public int Level { get; init; }
    public int MyRole { get; init; }
    public bool IsOwner { get; init; }
}

/// <summary>An expedition member row in EXPEDITION_MEMBER_COL order.</summary>
public sealed record X2ExpeditionMember(
    string Name, int Level, IReadOnlyList<int> Abilities, int Role, X2SocialElapsedTime ConnectionStatus,
    string Memo, bool Online, bool InParty, long Contribution, int HeirLevel, long WeeklyContributionPoint);

public sealed record X2ExpeditionState(X2ExpeditionInfo? Info, IReadOnlyList<X2ExpeditionMember> Members,
    bool IsLoaded = false, int MembersPerPage = 20);

public partial interface IX2SocialData
{
    /// <summary>Cached friend, request, and block-list state.</summary>
    X2FriendState FriendState { get; }

    /// <summary>Current family and its members; Info is null when the player has no family.</summary>
    X2FamilyState FamilyState { get; }

    /// <summary>Current expedition and its members; Info is null when the player has no expedition.</summary>
    X2ExpeditionState ExpeditionState { get; }

    /// <summary>Returns the latest cached value for a relation API query, or null when it is unavailable.</summary>
    X2RelationValue? QueryRelation(string table, string method, IReadOnlyList<object?> arguments);

    /// <summary>Queues a relation request and optionally reports the native immediate return value.</summary>
    X2RelationValue? ExecuteRelationCommand(X2RelationCommand command);
}

public partial class NullSocialData
{
    public virtual X2FriendState FriendState { get; } = new([], [], [], []);
    public virtual X2FamilyState FamilyState { get; } = new(null, []);
    public virtual X2ExpeditionState ExpeditionState { get; } = new(null, []);
    public virtual X2RelationValue? QueryRelation(string table, string method, IReadOnlyList<object?> arguments) => null;
    public virtual X2RelationValue? ExecuteRelationCommand(X2RelationCommand command) => null;
}

public static partial class X2SocialApi
{
    private sealed record SponsorFaction(uint Id, uint MotherId, string Name, string Description, string IconPath);

    private sealed record SponsorCatalog(
        IReadOnlyDictionary<uint, uint> MotherByFaction,
        IReadOnlyList<SponsorFaction> Sponsors);

    internal static void InstallRelations(X2LuaHost host, X2GameContext context, IX2SocialData data,
        string? gameDatabase)
    {
        var sponsorFactions = LoadSponsorFactions(gameDatabase ?? X2DbLists.DefaultDatabase);
        static object? ToLua(X2RelationValue value)
        {
            if (value.Returns is { } returns)
                return new LuaMulti(returns.Select(ToLua).ToArray());
            if (value.Fields is not { } fields) return value.Scalar;
            var table = new LuaTable();
            foreach (var (key, child) in fields) table[key] = ToLua(child);
            return table;
        }

        static IReadOnlyList<object?> Args(LuaArgs args) => args.Values.ToArray();

        void Query(string table, string method, object? unavailable)
            => host.Define(table, method, a => data.QueryRelation(table, method, Args(a)) is { } v ? ToLua(v) : unavailable);

        void Command(string table, string method, object? unavailable = null)
            => host.Define(table, method, a => data.ExecuteRelationCommand(new(table, method, Args(a))) is { } v ? ToLua(v) : unavailable);

        var empty = new LuaTable();

        static LuaTable RelationArray(IEnumerable<object?> values)
        {
            var table = new LuaTable();
            var index = 1d;
            foreach (var value in values) table[index++] = value;
            return table;
        }

        static LuaTable RelationElapsed(X2SocialElapsedTime time) => new()
        {
            ["year"] = (double)time.Year, ["month"] = (double)time.Month, ["day"] = (double)time.Day,
            ["hour"] = (double)time.Hour, ["minute"] = (double)time.Minute
        };

        static LuaTable RelationFriend(X2FriendEntry friend) => RelationArray(new object?[]
        {
            friend.Name, (double)friend.Level, RelationArray(friend.Abilities.Select(x => (object?)(double)x)),
            friend.Online ? RelationArray(friend.Position) : RelationElapsed(friend.LastSeen ?? new()),
            friend.Race, friend.Online, friend.InParty, (double)friend.HeirLevel, (double)friend.FactionId, false
        });

        static LuaTable RelationFamilyMember(X2FamilyMember member) => new()
        {
            ["charId"] = member.CharacterId, ["name"] = member.Name, ["level"] = (double)member.Level,
            ["heirLevel"] = (double)member.HeirLevel, ["title"] = member.Title, ["role"] = member.Role,
            ["online"] = member.Online
        };

        static LuaTable RelationFamilyInfo(X2FamilyInfo info) => new()
        {
            ["name"] = info.Name, ["ownerName"] = info.OwnerName, ["ownerRole"] = info.OwnerRole,
            ["gradeName"] = info.GradeName, ["myTitle"] = info.MyTitle, ["myRoleName"] = info.MyRoleName,
            ["myRoleIcon"] = info.MyRoleIcon, ["content1"] = info.Content1, ["content2"] = info.Content2,
            ["notice"] = info.Notice, ["exp"] = (double)info.Experience, ["maxExp"] = (double)info.MaxExperience,
            ["livingPoint"] = (double)info.LivingPoint, ["memberCount"] = (double)info.MemberCount,
            ["incMemberCount"] = (double)info.IncreasedMemberCount, ["maxMemberCount"] = (double)info.MaxMemberCount,
            ["maxIncMemberCount"] = (double)info.MaxIncreasedMemberCount
        };

        static LuaTable RelationExpeditionInfo(X2ExpeditionInfo info) => new()
        {
            ["id"] = (double)info.Id, ["name"] = info.Name, ["ownerName"] = info.OwnerName,
            ["notice"] = info.Notice, ["contributionPoint"] = info.ContributionPoint,
            ["dailyContributionPoint"] = (double)info.DailyContributionPoint,
            ["maxContributionPoint"] = (double)info.MaxContributionPoint,
            ["interest"] = RelationArray(info.Interests.Select(x => (object?)(double)x)),
            ["level"] = (double)info.Level, ["myRole"] = (double)info.MyRole
        };

        static LuaTable RelationExpeditionMember(X2ExpeditionMember member) => RelationArray(new object?[]
        {
            member.Name, (double)member.Level, RelationArray(member.Abilities.Select(x => (object?)(double)x)),
            (double)member.Role, RelationElapsed(member.ConnectionStatus), member.Memo, member.Online, member.InParty,
            (double)member.Contribution, (double)member.HeirLevel, (double)member.WeeklyContributionPoint, false
        });

        // X2Friend (21). Lists are positional in the native API. The bridge supplies the exact row fields.
        host.Define("X2Friend", "GetFriendList", a => RelationArray(data.FriendState.Friends
            .Where(friend => a.Bool(0) || friend.Online).Select(RelationFriend)));
        host.Define("X2Friend", "GetWaitFriendList", _ => new LuaTable
        {
            ["receiveList"] = RelationArray(data.FriendState.ReceivedRequests.Select(x => (object?)new LuaTable { ["id"] = x.Id, ["name"] = x.Name })),
            ["requestList"] = RelationArray(data.FriendState.SentRequests.Select(x => (object?)new LuaTable { ["id"] = x.Id, ["name"] = x.Name }))
        });
        host.Define("X2Friend", "GetFriendsPerPage", _ => (double)data.FriendState.FriendsPerPage);
        host.Define("X2Friend", "GetFriendCount", _ => new LuaMulti((double)data.FriendState.Friends.Count(x => x.Online), (double)data.FriendState.Friends.Count));
        host.Define("X2Friend", "GetBlockList", _ => new LuaMulti(RelationArray(data.FriendState.BlockedUsers.Select(x => (object?)new LuaTable
        {
            ["unitName"] = x.UnitName, ["name"] = x.Name, ["worldName"] = x.WorldName
        })), (double)data.FriendState.BlockedUsers.Count));
        host.Define("X2Friend", "IsBlockedUser", a => data.FriendState.BlockedUsers.Any(x => string.Equals(x.UnitName, a.Str(0), StringComparison.OrdinalIgnoreCase)));
        host.Define("X2Friend", "IsExistWaitFriendToReceive", _ => data.FriendState.ReceivedRequests.Count > 0);
        host.Define("X2Friend", "IsFriendListLoaded", _ => data.FriendState.IsLoaded);
        host.Define("X2Friend", "IsMyFriend", a => data.FriendState.Friends.Any(x => string.Equals(x.Name, a.Str(0), StringComparison.OrdinalIgnoreCase)));
        host.Define("X2Friend", "IsRequestFriend", a => data.FriendState.SentRequests.Any(x => string.Equals(x.Name, a.Str(0), StringComparison.OrdinalIgnoreCase)));
        foreach (var name in new[] { "AcceptFriend", "CancelFriend", "RejectFriend" }) Command("X2Friend", name, false);
        foreach (var name in new[] { "AcceptReceiveList", "BlockUser", "CancelRequestList", "DeleteFriend", "FriendRequest", "RejectReceiveList", "UnblockUser" })
            Command("X2Friend", name);
        host.Define("X2Friend", "RequestFriendList", a =>
        {
            var result = data.ExecuteRelationCommand(new("X2Friend", "RequestFriendList", Args(a)));
            var members = data.FriendState.Friends.Where(x => a.Bool(0) || x.Online).Select(RelationFriend).ToArray();
            context.Events.Fire("FRIENDLIST_INFO", (double)members.Length, RelationArray(members));
            return result is null ? null : ToLua(result);
        });

        // X2Family (28). A fresh character is not in a family, so dependent records are empty/unavailable.
        foreach (var name in new[] { "GetChangeNameItem", "GetEffect", "GetRoleList" }) Query("X2Family", name, empty);
        // static family rules shown on the family guide tab
        host.Define("X2Family", "GetFamilyGuideInfo", _ => new LuaTable
        {
            ["joinItemName"] = "", ["slashCommand"] = "/family", ["minMemberCount"] = 2d, ["maxMemberCount"] = 8d,
            ["roleChangePeriod"] = 604800d, ["increaseItemName"] = "", ["leaveDescExpPercent"] = 0d,
        });
        host.Define("X2Family", "GetInfo", _ => data.FamilyState.Info is { } info ? RelationFamilyInfo(info) : null);
        foreach (var name in new[] { "GetMemberList", "GetMembers" })
            host.Define("X2Family", name, a => RelationArray(data.FamilyState.Members.Where(x => a.Bool(0) || x.Online).Select(RelationFamilyMember)));
        host.Define("X2Family", "GetMemberCount", _ => new LuaMulti((double)data.FamilyState.Members.Count(x => x.Online), (double)data.FamilyState.Members.Count));
        Query("X2Family", "GetRenameFamilyPeriod", 0d);
        host.Define("X2Family", "IsFamily", _ => data.FamilyState.Info is not null);
        host.Define("X2Family", "IsLimitFamily", _ => data.FamilyState.Info?.IsAtMemberLimit ?? false);
        host.Define("X2Family", "IsMaxFamily", _ => data.FamilyState.Info?.IsAtMaximumSize ?? false);
        host.Define("X2Family", "IsMyFamily", a => data.FamilyState.Members.Any(x => string.Equals(x.Name, a.Str(0), StringComparison.OrdinalIgnoreCase)));
        host.Define("X2Family", "IsOwner", _ => data.FamilyState.Info?.IsOwner ?? false);
        foreach (var name in new[] { "ChangeMemberRole", "ChangeName", "ChangeOwner", "ChangeTitle", "Invite", "Leave", "OpenIncreaseMember", "OpenJoin", "OpenKick", "OpenLeave", "SetName", "ShowMembers" }) Command("X2Family", name);
        foreach (var name in new[] { "SetNotice", "UpdateTodayAssignment" }) Command("X2Family", name, false);

        // X2Faction (95). Query defaults match X2ApiData.g.cs; unavailable expedition records remain empty.
        foreach (var name in new[] { "CanDeclareExpeditionWar", "CanInviteExpedition", "EnoughExpeditionPublicAssignmentChangeCost", "IsProtectedZone" }) Query("X2Faction", name, false);
        host.Define("X2Faction", "IsExpedInfoLoaded", _ => data.ExpeditionState.IsLoaded);
        host.Define("X2Faction", "IsMyExpeditionMember", a => data.ExpeditionState.Members.Any(x => string.Equals(x.Name, a.Str(0), StringComparison.OrdinalIgnoreCase)));
        host.Define("X2Faction", "IsMyRoleExpeditionOwner", _ => data.ExpeditionState.Info?.IsOwner ?? false);
        foreach (var name in new[] { "GetDisplayPortalTime", "GetEnemyExpedition", "GetExpeditionBuffAndGrade", "GetExpeditionHouseId", "GetExpeditionMaxLevel", "GetExpeditionPortalLimit", "GetExpeditionSummonLimit", "GetMyExpeditionProtectionTime", "GetMyTopLevelFaction", "GetMyTopLevelFactionFromExpedition", "GetRemainTimeExpeditionWar" }) Query("X2Faction", name, 0d);
        host.Define("X2Faction", "GetExpeditionMemberCount", _ => new LuaMulti((double)data.ExpeditionState.Members.Count(x => x.Online), (double)data.ExpeditionState.Members.Count));
        host.Define("X2Faction", "GetExpeditionMembersPerPage", _ => (double)data.ExpeditionState.MembersPerPage);
        host.Define("X2Faction", "GetExpeditionMyRole", _ => (double)(data.ExpeditionState.Info?.MyRole ?? 0));
        host.Define("X2Faction", "GetMyExpeditionId", _ => (double)(data.ExpeditionState.Info?.Id ?? 0));
        host.Define("X2Faction", "GetMyExpeditionLevel", _ => (double)(data.ExpeditionState.Info?.Level ?? 0));
        foreach (var name in new[] { "GetAllExpeditionRolePolicies", "GetExpeditionHouseInfo", "GetExpeditionInstanceHistoryMemberInfos", "GetExpeditionLevelInfo", "GetExpeditionMyRolePolicy", "GetExpeditionRecruitmentPeriod", "GetExpeditionRecruitmentPeriodCost", "GetFactionInfo", "GetFactionList", "GetHeroDropoutComebackRequestInfo" }) Query("X2Faction", name, empty);
        host.Define("X2Faction", "GetSponsorFaction", _ =>
        {
            var factionId = context.Units.Get(context.Units.PlayerId)?.FactionId ?? 0;
            if (factionId == 0) return new LuaTable();
            var catalog = sponsorFactions;
            var motherId = catalog.MotherByFaction.GetValueOrDefault(factionId, factionId);
            return RelationArray(catalog.Sponsors.Where(x => x.MotherId == motherId).Select(x => (object?)new LuaTable
            {
                ["factionId"] = (double)x.Id,
                ["name"] = x.Name,
                ["desc"] = x.Description,
                ["iconPath"] = x.IconPath,
            }));
        });
        host.Define("X2Faction", "GetMyExpeditionInfo", _ => data.ExpeditionState.Info is { } info ? RelationExpeditionInfo(info) : null);
        foreach (var name in new[] { "GetExpeditionPublicAssignmentChangeCost", "GetExpeditionWarState", "GetFactionName", "GetMyExpeditionContributionPoint", "GetMyExpeditionOwnerName" }) Query("X2Faction", name, "");
        foreach (var name in new[] { "GetExpeditionBuffAllGradeDesc", "GetExpeditionBuffs", "GetExpeditionHistory", "GetExpeditionPortals", "GetExpeditionPublicQuestResetWeeklyDay", "GetExpeditionRecord", "GetExpeditionRolePolicy", "GetExpeditionSummonItem", "GetExpeditionWarKillScore", "GetFactionKickInactiveDay", "GetInterFactionState", "GetRenameExpeditionItem", "GetRenameExpeditionPeriod", "GetSiegeAuctionBidCurrency", "GetTopLevelFaction" }) Query("X2Faction", name, null);
        foreach (var name in new[] { "ChangeExpeditionMemberRole", "ChangeExpeditionOwner", "ChangeExpeditionRolePolicy", "CheckExpeditionExpNextDay", "CreateExpedition", "DeclareExpeditionWar", "DeleteExpeditionPortal", "DismissExpedition", "InviteToExpedition", "KickFromExpedition", "LeaveExpedition", "RenameExpedition", "RenameExpeditionPortal", "RequestCancelProtection", "RequestDeclarationMoney", "RequestExpeditionApplicantAccept", "RequestExpeditionApplicantAdd", "RequestExpeditionApplicantDel", "RequestExpeditionApplicantReject", "RequestExpeditionApplicantsGet", "RequestExpeditionBuffUp", "RequestExpeditionHistory", "RequestExpeditionHouseInfo", "RequestExpeditionMyRecruitmentsGet", "RequestExpeditionRecruitmentAdd", "RequestExpeditionRecruitmentDel", "RequestExpeditionRecruitmentsGet", "RequestExpeditionSummon", "RequestExpeditionSummonNotRecv", "RequestExpeditionSummonReply", "RequestExpeditionWarKillScore", "RequestIssuanceOfMobilizationOrder", "RequestMobilizationOrder", "RequestMobilizationOrderNotRecv", "SaveExpeditionPortal", "SetExpeditionLevelUp", "SetExpeditionNotice", "SetMyExpeditionInterest", "TeleportExpeditionPortal" }) Command("X2Faction", name);
        host.Define("X2Faction", "RequestExpeditionMembers", a =>
        {
            var result = data.ExecuteRelationCommand(new("X2Faction", "RequestExpeditionMembers", Args(a)));
            var all = a.Bool(0);
            var start = Math.Max(1, a.Int(1, 1));
            var visible = data.ExpeditionState.Members.Where(x => all || x.Online).ToArray();
            var page = visible.Skip(start - 1).Take(data.ExpeditionState.MembersPerPage).Select(RelationExpeditionMember);
            // Native event uses a zero-based start; expedition_management.lua adds one before filling the page.
            context.Events.Fire("EXPEDITION_MANAGEMENT_MEMBERS_INFO", (double)visible.Length, (double)(start - 1), RelationArray(page));
            return result is null ? null : ToLua(result);
        });

        // X2Nation (19). Color adjustment calls are forwarded because their drawable policy is host-owned.
        foreach (var name in new[] { "CanDiplomacy", "CanGetRelationCount" }) Query("X2Nation", name, false);
        Query("X2Nation", "DiplomacyDialogTimeout", 0d);
        foreach (var name in new[] { "GetDominionList", "GetDominionListAll", "GetNationBaseInfo", "GetNationImmigrateInfo", "GetNationList", "GetNationalDominionInfo", "GetPowerGrade", "GetRelationHistoryList", "GetRelationList" }) Query("X2Nation", name, empty);
        Query("X2Nation", "GetRelationCount", null);
        foreach (var name in new[] { "AdjustDomionRelation", "RequestDiplomacy", "ResponseDiplomacy", "SetDominionColors", "SetDominionCustomColor", "SetNationRelationColors" }) Command("X2Nation", name);

        // X2Rank (20). Empty tables suppress rank rows and rewards until snapshots arrive.
        foreach (var name in new[] { "BuildRankTabInfo", "GetMetaInfo", "GetPersonalData", "GetRankDivisions", "GetRankRewardDivisions", "GetRankRewards", "GetRankSeasonInformation", "GetRankSeasonOffDate", "GetRankTabCodes", "GetRewardSnapshot", "GetSnapshot" }) Query("X2Rank", name, empty);
        Query("X2Rank", "GetRankKind", 0d);
        foreach (var name in new[] { "HasRankReward", "IsRankRatingOnly", "IsRankerQueriable" }) Query("X2Rank", name, false);
        Query("X2Rank", "GetGamePointDetail", null);
        foreach (var name in new[] { "RequestPersonalData", "RequestRankerAppearance", "RequestRewardSnapshot", "RequestSnapshot" }) Command("X2Rank", name);
    }

    private static SponsorCatalog LoadSponsorFactions(string database)
    {
        var mothers = new Dictionary<uint, uint>();
        var sponsors = new List<SponsorFaction>();
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                select f.id, f.mother_id, f.show_create_expedition,
                       coalesce(nullif(n.en_us, ''), f.name),
                       coalesce(nullif(d.en_us, ''), f.desc_when_use_create_expedition),
                       coalesce(i.filename, '')
                from system_factions f
                left join localized_texts n on n.tbl_name = 'system_factions'
                     and n.tbl_column_name = 'name' and n.idx = f.id
                left join localized_texts d on d.tbl_name = 'system_factions'
                     and d.tbl_column_name = 'desc_when_use_create_expedition' and d.idx = f.id
                left join icons i on i.id = f.icon_id
                order by f.id
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = checked((uint)reader.GetInt64(0));
                var mother = checked((uint)reader.GetInt64(1));
                mothers[id] = mother == 0 ? id : mother;
                if (reader.GetString(2) != "t") continue;
                var filename = reader.GetString(5);
                sponsors.Add(new SponsorFaction(id, mother, reader.GetString(3), reader.GetString(4),
                    filename.Length == 0 ? "" : "ui/icon/" + filename));
            }
        }
        catch (Exception)
        {
            // Keep the native empty-list behavior when the configured client database is unavailable.
        }
        return new SponsorCatalog(mothers, sponsors);
    }
}
