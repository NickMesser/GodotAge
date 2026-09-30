#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>10.0.2.13 social packet opcodes recovered from the native client.</summary>
public static class SocialOpcodes
{
    // Server -> client
    public const ushort SCExpeditionList = 0x00A;
    public const ushort SCExpeditionRoleChanged = 0x00F;
    public const ushort SCExpeditionOwnerChanged = 0x010;
    public const ushort SCExpeditionCreated = 0x015;
    public const ushort SCExpeditionDismissed = 0x017;
    public const ushort SCExpeditionMemberList = 0x023;
    public const ushort SCExpeditionMemberListEnd = 0x024;
    public const ushort SCExpeditionMemberStatusChanged = 0x025;
    public const ushort SCExpeditionInvitation = 0x02D;
    public const ushort SCUnitExpeditionChanged = 0x02F;
    public const ushort SCExpeditionDesc = 0x04B;
    public const ushort SCFriends = 0x07A;
    public const ushort SCDeleteFriend = 0x07C;
    public const ushort SCFriendStatusChanged = 0x07D;
    public const ushort SCBlockedUsers = 0x07F;
    public const ushort SCAddBlockedUser = 0x080;
    public const ushort SCDeleteBlockedUser = 0x081;
    public const ushort SCJoinedChatChannel = 0x100;
    public const ushort SCLeavedChatChannel = 0x101;
    public const ushort SCChatMessage = 0x102;
    public const ushort SCChatFailed = 0x105;
    public const ushort SCChatLocalizedMessage = 0x106;
    public const ushort SCAskToJoinTeam = 0x109;
    public const ushort SCJoinedTeam = 0x10B;
    public const ushort SCRejectedTeam = 0x10C;
    public const ushort SCLeavedTeam = 0x10D;
    public const ushort SCTeamDismissed = 0x10E;
    public const ushort SCTeamMemberJoined = 0x10F;
    public const ushort SCTeamMemberLeaved = 0x110;
    public const ushort SCTeamMemberDisconnected = 0x111;
    public const ushort SCTeamOwnerChanged = 0x112;
    public const ushort SCTeamMemberRoleChanged = 0x113;
    public const ushort SCTeamBecameRaidTeam = 0x114;
    public const ushort SCRefreshTeamMember = 0x117;
    public const ushort SCTeamRemoteMembersEx = 0x118;
    public const ushort SCCanStartTrade = 0x1A5;
    public const ushort SCCannotStartTrade = 0x1A6;
    public const ushort SCTradeStarted = 0x1A7;
    public const ushort SCTradeCanceled = 0x1A8;
    public const ushort SCTradeItemPutup = 0x1A9;
    public const ushort SCOtherTradeItemPutup = 0x1AA;
    public const ushort SCTradeMoneyPutup = 0x1AB;
    public const ushort SCOtherTradeMoneyPutup = 0x1AC;
    public const ushort SCTradeItemTookdown = 0x1AD;
    public const ushort SCOtherTradeItemTookdown = 0x1AE;
    public const ushort SCTradeOkUpdate = 0x1AF;
    public const ushort SCTradeLockUpdate = 0x1B0;
    public const ushort SCTradeMade = 0x1B1;
    public const ushort SCOneAndOneChatStart = 0x307;
    public const ushort SCOneAndOneChatAddMessage = 0x308;
    public const ushort SCFriendRequest = 0x379;
    public const ushort SCFriendAccept = 0x37A;
    public const ushort SCFriendCancel = 0x37B;

    // Client -> server
    public const ushort CSInviteToExpedition = 0x00C;
    public const ushort CSReplyExpeditionInvitation = 0x00D;
    public const ushort CSLeaveExpedition = 0x00E;
    public const ushort CSKickFromExpedition = 0x00F;
    public const ushort CSJoinUserChatChannel = 0x096;
    public const ushort CSLeaveChatChannel = 0x097;
    public const ushort CSSendChatMessage = 0x098;
    public const ushort CSInviteToTeam = 0x0B1;
    public const ushort CSReplyToJoinTeam = 0x0B3;
    public const ushort CSLeaveTeam = 0x0B4;
    public const ushort CSKickTeamMember = 0x0B5;
    public const ushort CSMakeTeamOwner = 0x0B6;
    public const ushort CSStartTrade = 0x139;
    public const ushort CSCanStartTrade = 0x13A;
    public const ushort CSCannotStartTrade = 0x13B;
    public const ushort CSCancelTrade = 0x13C;
    public const ushort CSPutupTradeItem = 0x13D;
    public const ushort CSPutupTradeMoney = 0x13E;
    public const ushort CSTakedownTradeItem = 0x13F;
    public const ushort CSTradeLock = 0x140;
    public const ushort CSTradeOk = 0x141;
    public const ushort CSDeleteFriend = 0x159;
    public const ushort CSAddBlockedUser = 0x15B;
    public const ushort CSDeleteBlockedUser = 0x15C;
    public const ushort CSFriendRequest = 0x20C;
    public const ushort CSFriendAccept = 0x20E;
}

public static class SocialChatTypes
{
    public const short Whispered = -4;
    public const short Whisper = -3;
    public const short System = -2;
    public const short Notice = -1;
    public const short Say = 0;
    public const short Shout = 1;
    public const short Trade = 2;
    public const short GroupFind = 3;
    public const short Party = 4;
    public const short Raid = 5;
    public const short Region = 6;
    public const short Guild = 7;
    public const short Family = 9;
    public const short RaidLeader = 10;
    public const short Ally = 14;
    public const short User = 15;
    public const short Global = 18;
}

public sealed record SocialChannel(ulong Handle, short Type, short SubType, uint FactionId, string Name)
{
    public static SocialChannel FromHandle(ulong handle, string name = "") => new(
        handle, unchecked((short)handle), unchecked((short)(handle >> 16)), (uint)(handle >> 32), name);
}

public sealed record SocialFriend(
    ulong CharacterId, string Name, byte Race, byte Level, byte HeirLevel, int Health,
    byte Ability1, byte Ability2, byte Ability3, long WorldX, long WorldY, float WorldZ,
    uint ZoneId, uint WorldId, bool InParty, bool IsOnline, long LastWorldLeaveTime,
    uint RequestWorldId, byte Status, long FriendCreateTime);

public sealed record BlockedUser(ulong CharacterId, string Name, sbyte WorldId);

public sealed record TeamMemberSummary(
    ulong CharacterId, string Name, byte Race, byte Gender, byte Level, byte Role,
    uint ObjectId, byte HeirLevel);

public sealed record TeamSlot(ulong CharacterId, bool Connected);
public sealed record TeamMark(byte Type, ulong CharacterId, uint ObjectId);
public sealed record TeamLootRule(sbyte Method, sbyte MinimumGrade, ulong LootMaster, bool RollForBindOnPickup);

public sealed record RemoteTeamMember(
    ulong CharacterId, long ZoneId, sbyte Level, sbyte HeirLevel,
    uint Hp, uint MaxHp, uint Mp, uint MaxMp, Vector3 Position,
    sbyte Ability1, sbyte Ability2, sbyte Ability3, bool Offline, sbyte DiceBidRule);

public sealed record ExpeditionDescriptor(
    uint Id, uint MotherId, string Name, ulong OwnerId, string OwnerName, sbyte UnitOwnerType,
    byte PoliticalSystem, long CreatedTime, bool AggroLink, bool DiplomacyTarget,
    byte AllowChangeName, long RenameTime, bool IntegrationFaction);

public sealed record ExpeditionMemberSummary(
    int ExpeditionId, ulong CharacterId, bool InParty, bool IsOnline, long LastWorldLeaveTime,
    string Name, byte Level, byte HeirLevel, uint ZoneId, int FactionId,
    byte Ability1, byte Ability2, byte Ability3, byte Role,
    long WorldX, long WorldY, float WorldZ, string Memo, long TransferRequestedTime,
    uint ContributionPoint, uint WeeklyContributionPoint, uint GearScore);

/// <summary>Marker consumed by the UI-facing social state reducer.</summary>
public interface ISocialProtocolEvent { }

public abstract record SocialEvent(ushort Opcode) : ISocialProtocolEvent;

public sealed record FriendsPageEvent(uint Total, IReadOnlyList<SocialFriend> Friends)
    : SocialEvent(SocialOpcodes.SCFriends);
public sealed record FriendRequestEvent(bool IsRequest, bool Success, ushort Error, ulong CharacterId,
    uint WorldId, long RequestTime, string CharacterName) : SocialEvent(SocialOpcodes.SCFriendRequest);
public sealed record FriendAcceptedEvent(bool Success, bool IsAccept, ushort Error, SocialFriend? Friend)
    : SocialEvent(SocialOpcodes.SCFriendAccept);
public sealed record FriendDeletedEvent(bool IsRequester, ulong CharacterId, bool Success, string FriendName, ushort Error)
    : SocialEvent(SocialOpcodes.SCDeleteFriend);
public sealed record FriendStatusEvent(bool IsWaitFriend, SocialFriend Friend)
    : SocialEvent(SocialOpcodes.SCFriendStatusChanged);
public sealed record FriendCancelEvent(bool Success, bool IsReceive, ulong OwnerCharacterId, ulong CounterpartCharacterId)
    : SocialEvent(SocialOpcodes.SCFriendCancel);

public sealed record BlockedUsersPageEvent(uint Total, IReadOnlyList<BlockedUser> Users)
    : SocialEvent(SocialOpcodes.SCBlockedUsers);
public sealed record BlockedUserAddedEvent(BlockedUser User, bool Success, ushort Error)
    : SocialEvent(SocialOpcodes.SCAddBlockedUser);
public sealed record BlockedUserDeletedEvent(ulong CharacterId, bool Success, ushort Error)
    : SocialEvent(SocialOpcodes.SCDeleteBlockedUser);

public sealed record TeamInviteEvent(int TeamId, ulong InviterId, string InviterName, sbyte TeamRole, long LogEventId)
    : SocialEvent(SocialOpcodes.SCAskToJoinTeam);
public sealed record TeamInviteRejectedEvent(string Name, bool Party) : SocialEvent(SocialOpcodes.SCRejectedTeam);
public sealed record TeamJoinedEvent(
    uint TeamId, ulong OwnerId, bool IsParty, IReadOnlyList<byte> PartyCounts,
    IReadOnlyList<TeamSlot> Slots, IReadOnlyList<TeamMark> Marks, TeamLootRule LootRule,
    uint JointId, bool IsJointLeader, uint JointOrder, ulong JointType, sbyte TeamRoleType,
    IReadOnlyList<TeamMemberSummary> Members, bool IsMine) : SocialEvent(SocialOpcodes.SCJoinedTeam);
public sealed record TeamLeftEvent(int TeamId, bool Kicked, bool Dismissed) : SocialEvent(SocialOpcodes.SCLeavedTeam);
public sealed record TeamDismissedEvent(int TeamId) : SocialEvent(SocialOpcodes.SCTeamDismissed);
public sealed record TeamMemberJoinedEvent(uint TeamId, TeamMemberSummary Member, int Party)
    : SocialEvent(SocialOpcodes.SCTeamMemberJoined);
public sealed record TeamMemberLeftEvent(int TeamId, ulong MemberId, bool Kicked)
    : SocialEvent(SocialOpcodes.SCTeamMemberLeaved);
public sealed record TeamMemberDisconnectedEvent(uint TeamId, ulong MemberId, RemoteTeamMember Member)
    : SocialEvent(SocialOpcodes.SCTeamMemberDisconnected);
public sealed record TeamOwnerChangedEvent(int TeamId, ulong OwnerId) : SocialEvent(SocialOpcodes.SCTeamOwnerChanged);
public sealed record TeamMemberRoleChangedEvent(uint TeamId, ulong MemberId, byte Role)
    : SocialEvent(SocialOpcodes.SCTeamMemberRoleChanged);
public sealed record TeamBecameRaidEvent(int TeamId) : SocialEvent(SocialOpcodes.SCTeamBecameRaidTeam);
public sealed record TeamMemberRefreshedEvent(uint TeamId, ulong MemberId, uint ObjectId)
    : SocialEvent(SocialOpcodes.SCRefreshTeamMember);
public sealed record TeamRemoteMembersEvent(int TeamId, IReadOnlyList<RemoteTeamMember> Members)
    : SocialEvent(SocialOpcodes.SCTeamRemoteMembersEx);

public sealed record ExpeditionsPageEvent(IReadOnlyList<ExpeditionDescriptor> Expeditions)
    : SocialEvent(SocialOpcodes.SCExpeditionList);
public sealed record ExpeditionInvitationEvent(ulong InviterId, string InviterName, int FactionId, string FactionName)
    : SocialEvent(SocialOpcodes.SCExpeditionInvitation);
public sealed record ExpeditionMembersPageEvent(int ExpeditionId, IReadOnlyList<ExpeditionMemberSummary> Members)
    : SocialEvent(SocialOpcodes.SCExpeditionMemberList);
public sealed record ExpeditionMemberListEndEvent(int Total, int ExpeditionId)
    : SocialEvent(SocialOpcodes.SCExpeditionMemberListEnd);
public sealed record ExpeditionMemberStatusEvent(ExpeditionMemberSummary Member, byte Flag)
    : SocialEvent(SocialOpcodes.SCExpeditionMemberStatusChanged);
public sealed record ExpeditionOwnerChangedEvent(ulong ExpeditionId, ulong OwnerId, string OwnerName)
    : SocialEvent(SocialOpcodes.SCExpeditionOwnerChanged);
public sealed record ExpeditionRoleChangedEvent(ulong CharacterId, sbyte Role, string CharacterName)
    : SocialEvent(SocialOpcodes.SCExpeditionRoleChanged);
public sealed record ExpeditionDismissedEvent(int ExpeditionId, bool Success)
    : SocialEvent(SocialOpcodes.SCExpeditionDismissed);
public sealed record UnitExpeditionChangedEvent(uint UnitId, ulong CharacterId, string Kicker, string UnitName,
    uint OldExpeditionId, uint NewExpeditionId, bool Expelled) : SocialEvent(SocialOpcodes.SCUnitExpeditionChanged);

public sealed record ChatChannelJoinedEvent(SocialChannel Channel) : SocialEvent(SocialOpcodes.SCJoinedChatChannel);
public sealed record ChatChannelLeftEvent(SocialChannel Channel) : SocialEvent(SocialOpcodes.SCLeavedChatChannel);
public sealed record SocialChatMessageEvent(
    sbyte ClientLocale, SocialChannel Channel, uint SenderObjectId, long SenderType,
    byte LanguageType, byte SenderRace, uint FactionId, string SenderName, string Message,
    bool IsSystem, bool IsWhisper, byte[] Trailer) : SocialEvent(SocialOpcodes.SCChatMessage);
public sealed record ChatFailedEvent(ushort Error, string ChannelName) : SocialEvent(SocialOpcodes.SCChatFailed);
public sealed record ChatLocalizedMessageEvent(ulong Field1, uint ObjectId, sbyte Field3, uint Category,
    string Field5, string Field6, uint NumParams, string Param1, string Param2)
    : SocialEvent(SocialOpcodes.SCChatLocalizedMessage);
public sealed record DirectChatStartedEvent(ulong ConversationId, string TargetName)
    : SocialEvent(SocialOpcodes.SCOneAndOneChatStart);
public sealed record DirectChatMessageEvent(ulong ConversationId, string SpeakerName, string Message, bool IsSpeakerGm)
    : SocialEvent(SocialOpcodes.SCOneAndOneChatAddMessage);

public sealed record TradeRequestEvent(uint ObjectId, bool CanStart, uint? Reason)
    : SocialEvent(CanStart ? SocialOpcodes.SCCanStartTrade : SocialOpcodes.SCCannotStartTrade);
public sealed record TradeStartedEvent(uint OtherObjectId) : SocialEvent(SocialOpcodes.SCTradeStarted);
public sealed record TradeCanceledEvent(uint Reason, bool CausedByMe) : SocialEvent(SocialOpcodes.SCTradeCanceled);
public sealed record TradeItemOfferedEvent(bool Mine, byte SlotType, byte Slot, uint Amount, byte[]? SerializedItem)
    : SocialEvent(Mine ? SocialOpcodes.SCTradeItemPutup : SocialOpcodes.SCOtherTradeItemPutup);
public sealed record TradeItemRemovedEvent(bool Mine, byte SlotType, byte Slot, byte[]? SerializedItem)
    : SocialEvent(Mine ? SocialOpcodes.SCTradeItemTookdown : SocialOpcodes.SCOtherTradeItemTookdown);
public sealed record TradeMoneyOfferedEvent(bool Mine, ulong Amount)
    : SocialEvent(Mine ? SocialOpcodes.SCTradeMoneyPutup : SocialOpcodes.SCOtherTradeMoneyPutup);
public sealed record TradeLockChangedEvent(bool MyLock, bool OtherLock, bool MyWill)
    : SocialEvent(SocialOpcodes.SCTradeLockUpdate);
public sealed record TradeConfirmChangedEvent(bool MyConfirmed, bool OtherConfirmed)
    : SocialEvent(SocialOpcodes.SCTradeOkUpdate);
public sealed record TradeCompletedEvent(byte[] RawItemTaskBody) : SocialEvent(SocialOpcodes.SCTradeMade);

/// <summary>A known packet retained losslessly because its nested native structure is not verified here.</summary>
public sealed record SocialRawBodyEvent(ushort KnownOpcode, string PacketClass, string Reason, byte[] Body)
    : SocialEvent(KnownOpcode);

/// <summary>Parses complete S2C packet bodies (the two-byte opcode is supplied separately).</summary>
public static class SocialPacketParser
{
    public static SocialEvent? Parse(ushort opcode, byte[] body)
    {
        var r = new WireReader(body);
        return opcode switch
        {
            SocialOpcodes.SCFriends => ReadFriends(r),
            SocialOpcodes.SCFriendRequest => ReadFriendRequest(r),
            SocialOpcodes.SCFriendAccept => ReadFriendAccepted(r),
            SocialOpcodes.SCDeleteFriend => ReadFriendDeleted(r),
            SocialOpcodes.SCFriendStatusChanged => new FriendStatusEvent(r.Bool(), ReadFriend(r)),
            SocialOpcodes.SCFriendCancel => new FriendCancelEvent(r.Bool(), r.Bool(), r.U64(), r.U64()),
            SocialOpcodes.SCBlockedUsers => ReadBlockedUsers(r),
            SocialOpcodes.SCAddBlockedUser => new BlockedUserAddedEvent(ReadBlocked(r), r.Bool(), r.U16()),
            SocialOpcodes.SCDeleteBlockedUser => new BlockedUserDeletedEvent(r.U64(), r.Bool(), r.U16()),
            SocialOpcodes.SCAskToJoinTeam => new TeamInviteEvent(r.S32(), r.U64(), r.Str(), r.S8(), r.S64()),
            SocialOpcodes.SCJoinedTeam => ReadJoinedTeam(r),
            SocialOpcodes.SCRejectedTeam => new TeamInviteRejectedEvent(r.Str(), r.Bool()),
            SocialOpcodes.SCLeavedTeam => new TeamLeftEvent(r.S32(), r.Bool(), r.Bool()),
            SocialOpcodes.SCTeamDismissed => new TeamDismissedEvent(r.S32()),
            SocialOpcodes.SCTeamMemberJoined => new TeamMemberJoinedEvent(r.U32(), ReadTeamMember(r), r.S32()),
            SocialOpcodes.SCTeamMemberLeaved => new TeamMemberLeftEvent(r.S32(), r.U64(), r.Bool()),
            SocialOpcodes.SCTeamMemberDisconnected => ReadDisconnectedTeamMember(r),
            SocialOpcodes.SCTeamOwnerChanged => new TeamOwnerChangedEvent(r.S32(), r.U64()),
            SocialOpcodes.SCTeamMemberRoleChanged => new TeamMemberRoleChangedEvent(r.U32(), r.U64(), r.U8()),
            SocialOpcodes.SCTeamBecameRaidTeam => new TeamBecameRaidEvent(r.S32()),
            SocialOpcodes.SCRefreshTeamMember => new TeamMemberRefreshedEvent(r.U32(), r.U64(), r.Bc()),
            SocialOpcodes.SCTeamRemoteMembersEx => ReadRemoteTeamMembers(r),
            SocialOpcodes.SCExpeditionList => ReadExpeditions(r),
            SocialOpcodes.SCExpeditionInvitation => new ExpeditionInvitationEvent(r.U64(), r.Str(), r.S32(), r.Str()),
            SocialOpcodes.SCExpeditionMemberList => ReadExpeditionMembers(r),
            SocialOpcodes.SCExpeditionMemberListEnd => new ExpeditionMemberListEndEvent(r.S32(), r.S32()),
            SocialOpcodes.SCExpeditionMemberStatusChanged => new ExpeditionMemberStatusEvent(ReadExpeditionMember(r), r.U8()),
            SocialOpcodes.SCExpeditionOwnerChanged => new ExpeditionOwnerChangedEvent(r.U64(), r.U64(), r.Str()),
            SocialOpcodes.SCExpeditionRoleChanged => new ExpeditionRoleChangedEvent(r.U64(), r.S8(), r.Str()),
            SocialOpcodes.SCExpeditionDismissed => new ExpeditionDismissedEvent(r.S32(), r.Bool()),
            SocialOpcodes.SCUnitExpeditionChanged => new UnitExpeditionChangedEvent(r.Bc(), r.U64(), r.Str(), r.Str(), r.U32(), r.U32(), r.Bool()),
            SocialOpcodes.SCExpeditionCreated => Raw(opcode, "SCExpeditionCreatedPacket", "conflicting AAEmu and generated native field order", body),
            SocialOpcodes.SCExpeditionDesc => Raw(opcode, "SCExpeditionDescPacket", "nested FactionDesc plus partially unconfirmed tail", body),
            SocialOpcodes.SCJoinedChatChannel => ReadJoinedChannel(r),
            SocialOpcodes.SCLeavedChatChannel => new ChatChannelLeftEvent(SocialChannel.FromHandle(r.U64())),
            SocialOpcodes.SCChatMessage => ReadChatMessage(r),
            SocialOpcodes.SCChatFailed => new ChatFailedEvent(r.U16(), r.Str()),
            SocialOpcodes.SCChatLocalizedMessage => new ChatLocalizedMessageEvent(r.U64(), r.Bc(), r.S8(), r.U32(), r.Str(), r.Str(), r.U32(), r.Str(), r.Str()),
            SocialOpcodes.SCOneAndOneChatStart => new DirectChatStartedEvent(r.U64(), r.Str()),
            SocialOpcodes.SCOneAndOneChatAddMessage => new DirectChatMessageEvent(r.U64(), r.Str(), r.Str(), r.Bool()),
            SocialOpcodes.SCCanStartTrade => new TradeRequestEvent(r.Bc(), true, null),
            SocialOpcodes.SCCannotStartTrade => new TradeRequestEvent(r.Bc(), false, r.U32()),
            SocialOpcodes.SCTradeStarted => new TradeStartedEvent(r.Bc()),
            SocialOpcodes.SCTradeCanceled => new TradeCanceledEvent(r.U32(), r.Bool()),
            SocialOpcodes.SCTradeItemPutup => new TradeItemOfferedEvent(true, r.U8(), r.U8(), r.U32(), null),
            SocialOpcodes.SCOtherTradeItemPutup => new TradeItemOfferedEvent(false, 0, 0, 0, r.Rest()),
            SocialOpcodes.SCTradeMoneyPutup => new TradeMoneyOfferedEvent(true, r.U64()),
            SocialOpcodes.SCOtherTradeMoneyPutup => new TradeMoneyOfferedEvent(false, r.U64()),
            SocialOpcodes.SCTradeItemTookdown => new TradeItemRemovedEvent(true, r.U8(), r.U8(), null),
            SocialOpcodes.SCOtherTradeItemTookdown => new TradeItemRemovedEvent(false, 0, 0, r.Rest()),
            SocialOpcodes.SCTradeOkUpdate => new TradeConfirmChangedEvent(r.Bool(), r.Bool()),
            SocialOpcodes.SCTradeLockUpdate => new TradeLockChangedEvent(r.Bool(), r.Bool(), r.Bool()),
            SocialOpcodes.SCTradeMade => new TradeCompletedEvent(r.Rest()),
            _ => null,
        };
    }

    private static SocialRawBodyEvent Raw(ushort opcode, string name, string reason, byte[] body) =>
        new(opcode, name, reason, body.ToArray());

    private static FriendsPageEvent ReadFriends(WireReader r)
    {
        var total = r.U32();
        var count = r.U32();
        var friends = new List<SocialFriend>(CheckedCount(count, 20, "friend"));
        for (var i = 0; i < count; i++) friends.Add(ReadFriend(r));
        return new FriendsPageEvent(total, friends);
    }

    private static FriendRequestEvent ReadFriendRequest(WireReader r) =>
        new(r.Bool(), r.Bool(), r.U16(), r.U64(), r.U32(), r.S64(), r.Str());

    private static FriendAcceptedEvent ReadFriendAccepted(WireReader r)
    {
        var success = r.Bool();
        var accepted = r.Bool();
        var error = r.U16();
        return new FriendAcceptedEvent(success, accepted, error, r.Remaining > 0 ? ReadFriend(r) : null);
    }

    private static FriendDeletedEvent ReadFriendDeleted(WireReader r)
    {
        // PacketBodies.g.cs native order. AAEmu's current writer has name and success reversed.
        return new FriendDeletedEvent(r.Bool(), r.U64(), r.Bool(), r.Str(), r.U16());
    }

    private static SocialFriend ReadFriend(WireReader r) => new(
        r.U64(), r.Str(), r.U8(), r.U8(), r.U8(), r.S32(), r.U8(), r.U8(), r.U8(),
        r.S64(), r.S64(), r.F32(), r.U32(), r.U32(), r.Bool(), r.Bool(), r.S64(),
        r.U32(), r.U8(), r.S64());

    private static BlockedUsersPageEvent ReadBlockedUsers(WireReader r)
    {
        var total = r.U32();
        var count = r.U32();
        var users = new List<BlockedUser>(CheckedCount(count, 500, "blocked user"));
        for (var i = 0; i < count; i++) users.Add(ReadBlocked(r));
        return new BlockedUsersPageEvent(total, users);
    }

    private static BlockedUser ReadBlocked(WireReader r) => new(r.U64(), r.Str(), r.S8());

    private static TeamJoinedEvent ReadJoinedTeam(WireReader r)
    {
        var id = r.U32();
        var owner = r.U64();
        var isParty = r.Bool();
        var partyCounts = new byte[10];
        var memberCount = 0;
        for (var i = 0; i < partyCounts.Length; i++) memberCount += partyCounts[i] = r.U8();
        if (memberCount > 50) throw new WireException($"team header declares {memberCount} members");

        var slots = new List<TeamSlot>(50);
        for (var i = 0; i < 50; i++) slots.Add(new TeamSlot(r.U64(), r.Bool()));

        var marks = new List<TeamMark>(12);
        for (var i = 0; i < 12; i++)
        {
            var type = r.U8();
            marks.Add(type switch
            {
                1 => new TeamMark(type, r.U64(), 0),
                2 => new TeamMark(type, 0, r.Bc()),
                _ => new TeamMark(type, 0, 0),
            });
        }

        var loot = new TeamLootRule(r.S8(), r.S8(), r.U64(), r.Bool());
        var jointId = r.U32();
        var jointLeader = r.Bool();
        var jointOrder = r.U32();
        var jointType = r.U64();
        var roleType = r.S8();
        var members = new List<TeamMemberSummary>(memberCount);
        for (var i = 0; i < memberCount; i++) members.Add(ReadTeamMember(r));
        return new TeamJoinedEvent(id, owner, isParty, partyCounts, slots, marks, loot,
            jointId, jointLeader, jointOrder, jointType, roleType, members, r.Bool());
    }

    private static TeamMemberSummary ReadTeamMember(WireReader r) =>
        new(r.U64(), r.Str(), r.U8(), r.U8(), r.U8(), r.U8(), r.Bc(), r.U8());

    private static TeamMemberDisconnectedEvent ReadDisconnectedTeamMember(WireReader r)
    {
        var team = r.U32();
        var member = r.U64();
        return new TeamMemberDisconnectedEvent(team, member, ReadRemoteTeamMember(r));
    }

    private static TeamRemoteMembersEvent ReadRemoteTeamMembers(WireReader r)
    {
        var team = r.S32();
        var count = CheckedCount(r.S32(), 50, "remote team member");
        var members = new List<RemoteTeamMember>(count);
        for (var i = 0; i < count; i++) members.Add(ReadRemoteTeamMember(r));
        return new TeamRemoteMembersEvent(team, members);
    }

    private static RemoteTeamMember ReadRemoteTeamMember(WireReader r)
    {
        var id = r.U64();
        var zone = r.S64();
        var level = r.S8();
        var heir = r.S8();
        var points = r.Pisc(4);
        var position = r.Position();
        return new RemoteTeamMember(id, zone, level, heir, points[0], points[1], points[2], points[3],
            position, r.S8(), r.S8(), r.S8(), r.Bool(), r.S8());
    }

    private static ExpeditionsPageEvent ReadExpeditions(WireReader r)
    {
        var count = CheckedCount(r.U8(), 20, "expedition");
        var list = new List<ExpeditionDescriptor>(count);
        for (var i = 0; i < count; i++) list.Add(ReadExpedition(r));
        return new ExpeditionsPageEvent(list);
    }

    private static ExpeditionDescriptor ReadExpedition(WireReader r) => new(
        r.U32(), r.U32(), r.Str(), r.U64(), r.Str(), r.S8(), r.U8(), r.S64(), r.Bool(), r.Bool(),
        r.U8(), r.S64(), r.Bool());

    private static ExpeditionMembersPageEvent ReadExpeditionMembers(WireReader r)
    {
        var count = CheckedCount(r.U8(), 20, "expedition member");
        var expeditionId = r.S32();
        var members = new List<ExpeditionMemberSummary>(count);
        for (var i = 0; i < count; i++) members.Add(ReadExpeditionMember(r));
        return new ExpeditionMembersPageEvent(expeditionId, members);
    }

    private static ExpeditionMemberSummary ReadExpeditionMember(WireReader r) => new(
        r.S32(), r.U64(), r.Bool(), r.Bool(), r.S64(), r.Str(), r.U8(), r.U8(), r.U32(), r.S32(),
        r.U8(), r.U8(), r.U8(), r.U8(), r.S64(), r.S64(), r.F32(), r.Str(), r.S64(), r.U32(), r.U32(), r.U32());

    private static ChatChannelJoinedEvent ReadJoinedChannel(WireReader r)
    {
        var handle = r.U64();
        return new ChatChannelJoinedEvent(SocialChannel.FromHandle(handle, r.Str()));
    }

    private static SocialChatMessageEvent ReadChatMessage(WireReader r)
    {
        var locale = r.S8();
        var handle = r.U64();
        var sender = r.Bc();
        var senderType = r.S64();
        var language = r.U8();
        var race = r.U8();
        var faction = r.U32();
        var name = r.Str();
        var message = r.Str();
        var type = unchecked((short)handle);
        return new SocialChatMessageEvent(locale, SocialChannel.FromHandle(handle), sender, senderType,
            language, race, faction, name, message,
            type is SocialChatTypes.System or SocialChatTypes.Notice,
            type is SocialChatTypes.Whisper or SocialChatTypes.Whispered, r.Rest());
    }

    private static int CheckedCount(uint count, int max, string label)
    {
        if (count > max) throw new WireException($"{label} count {count} exceeds native maximum {max}");
        return (int)count;
    }

    private static int CheckedCount(int count, int max, string label)
    {
        if (count < 0 || count > max) throw new WireException($"{label} count {count} is outside 0..{max}");
        return count;
    }
}

public sealed record OutboundSocialPacket(ushort Opcode, byte[] Body);

/// <summary>Builders for verified C2S social packet bodies.</summary>
public static class SocialPacketWriter
{
    public static ulong ChatHandle(short type, short subType = 0, uint factionId = 0) =>
        (ushort)type | ((ulong)(ushort)subType << 16) | ((ulong)factionId << 32);

    public static OutboundSocialPacket SendChat(short type, string message, short subType = 0,
        uint factionId = 0, string target = "", sbyte targetWorldId = 0, sbyte clientLocale = 0,
        sbyte languageType = 0, uint ability = 0)
    {
        var body = new WireWriter().S8(clientLocale).U64(ChatHandle(type, subType, factionId))
            .Str(target).S8(targetWorldId).Str(message).S8(languageType).U32(ability);
        // Empty fixed chat-link block. Non-empty item/quest links require their native serializers.
        for (var i = 0; i < 4; i++) body.U8(0);
        return Packet(SocialOpcodes.CSSendChatMessage, body);
    }

    public static OutboundSocialPacket SendWhisper(string targetName, string message, sbyte targetWorldId = 0,
        sbyte clientLocale = 0, sbyte languageType = 0, uint ability = 0) =>
        SendChat(SocialChatTypes.Whisper, message, target: targetName, targetWorldId: targetWorldId,
            clientLocale: clientLocale, languageType: languageType, ability: ability);

    public static OutboundSocialPacket JoinUserChat(string name, string password = "", bool create = false) =>
        Packet(SocialOpcodes.CSJoinUserChatChannel, new WireWriter().Str(name).Str(password).Bool(create));

    public static OutboundSocialPacket LeaveChat(ulong handle) =>
        Packet(SocialOpcodes.CSLeaveChatChannel, new WireWriter().U64(handle));

    public static OutboundSocialPacket InviteParty(string targetName, int teamId = 0, sbyte teamRole = 1, sbyte worldId = 0) =>
        Packet(SocialOpcodes.CSInviteToTeam, new WireWriter().S32(teamId).S8(teamRole).Str(targetName).S8(worldId));

    public static OutboundSocialPacket ReplyPartyInvite(int teamId, bool party, ulong inviterId,
        string inviterName, bool accept, sbyte teamRole = 1, bool isArea = false, long logEventId = 0) =>
        Packet(SocialOpcodes.CSReplyToJoinTeam, new WireWriter().S32(teamId).Bool(party).U64(inviterId)
            .Bool(!accept).Str(inviterName).Bool(isArea).S8(teamRole).S64(logEventId));

    public static OutboundSocialPacket LeaveParty(int teamId) =>
        Packet(SocialOpcodes.CSLeaveTeam, new WireWriter().S32(teamId));

    public static OutboundSocialPacket KickPartyMember(int teamId, ulong memberId) =>
        Packet(SocialOpcodes.CSKickTeamMember, new WireWriter().S32(teamId).U64(memberId));

    public static OutboundSocialPacket MakePartyLeader(int teamId, ulong memberId) =>
        Packet(SocialOpcodes.CSMakeTeamOwner, new WireWriter().S32(teamId).U64(memberId));

    public static OutboundSocialPacket AddFriend(string targetName) =>
        Packet(SocialOpcodes.CSFriendRequest, new WireWriter().Str(targetName));

    public static OutboundSocialPacket AcceptFriend(ulong requesterCharacterId) =>
        Packet(SocialOpcodes.CSFriendAccept, new WireWriter().U64(requesterCharacterId));

    public static OutboundSocialPacket RemoveFriend(string friendName) =>
        Packet(SocialOpcodes.CSDeleteFriend, new WireWriter().Str(friendName));

    public static OutboundSocialPacket AddBlockedUser(string name, sbyte worldId = 0) =>
        Packet(SocialOpcodes.CSAddBlockedUser, new WireWriter().Str(name).S8(worldId));

    public static OutboundSocialPacket RemoveBlockedUser(ulong characterId) =>
        Packet(SocialOpcodes.CSDeleteBlockedUser, new WireWriter().U64(characterId));

    public static OutboundSocialPacket RequestTrade(uint targetObjectId) =>
        Packet(SocialOpcodes.CSStartTrade, new WireWriter().Bc(targetObjectId));

    public static OutboundSocialPacket AcceptTrade(uint requesterObjectId) =>
        Packet(SocialOpcodes.CSCanStartTrade, new WireWriter().Bc(requesterObjectId));

    public static OutboundSocialPacket RejectTrade(uint requesterObjectId, uint reason) =>
        Packet(SocialOpcodes.CSCannotStartTrade, new WireWriter().Bc(requesterObjectId).U32(reason));

    public static OutboundSocialPacket CancelTrade(uint reason = 0) =>
        Packet(SocialOpcodes.CSCancelTrade, new WireWriter().U32(reason));

    public static OutboundSocialPacket OfferTradeItem(byte slotType, byte slot, uint amount) =>
        Packet(SocialOpcodes.CSPutupTradeItem, new WireWriter().U8(slotType).U8(slot).U32(amount));

    public static OutboundSocialPacket RemoveTradeItem(byte slotType, byte slot) =>
        Packet(SocialOpcodes.CSTakedownTradeItem, new WireWriter().U8(slotType).U8(slot));

    public static OutboundSocialPacket OfferTradeMoney(ulong amount) =>
        Packet(SocialOpcodes.CSPutupTradeMoney, new WireWriter().U64(amount));

    public static OutboundSocialPacket LockTrade(bool locked = true) =>
        Packet(SocialOpcodes.CSTradeLock, new WireWriter().Bool(locked));

    public static OutboundSocialPacket ConfirmTrade() =>
        Packet(SocialOpcodes.CSTradeOk, new WireWriter());

    public static OutboundSocialPacket InviteToExpedition(string name) =>
        Packet(SocialOpcodes.CSInviteToExpedition, new WireWriter().Str(name));

    public static OutboundSocialPacket LeaveExpedition() =>
        Packet(SocialOpcodes.CSLeaveExpedition, new WireWriter());

    private static OutboundSocialPacket Packet(ushort opcode, WireWriter body) => new(opcode, body.ToArray());
}

/// <summary>Mutable UI-facing social state. Call <see cref="Apply"/> from the main thread.</summary>
public sealed class SocialState
{
    public Dictionary<ulong, SocialFriend> Friends { get; } = [];
    public Dictionary<ulong, BlockedUser> BlockedUsers { get; } = [];
    public Dictionary<ulong, TeamMemberSummary> TeamMembers { get; } = [];
    public Dictionary<ulong, RemoteTeamMember> RemoteTeamMembers { get; } = [];
    public Dictionary<uint, ExpeditionDescriptor> Expeditions { get; } = [];
    public Dictionary<ulong, ExpeditionMemberSummary> ExpeditionMembers { get; } = [];
    public Dictionary<ulong, SocialChannel> ChatChannels { get; } = [];
    public List<SocialChatMessageEvent> ChatMessages { get; } = [];
    public List<TradeItemOfferedEvent> TradeItems { get; } = [];

    public int TeamId { get; private set; }
    public ulong TeamOwnerId { get; private set; }
    public bool TeamIsRaid { get; private set; }
    public uint CurrentExpeditionId { get; private set; }
    public bool TradeOpen { get; private set; }
    public uint TradeObjectId { get; private set; }
    public ulong MyTradeMoney { get; private set; }
    public ulong OtherTradeMoney { get; private set; }
    public bool MyTradeLock { get; private set; }
    public bool OtherTradeLock { get; private set; }
    public bool MyTradeConfirmed { get; private set; }
    public bool OtherTradeConfirmed { get; private set; }

    public void ClearForCharacterLoad()
    {
        Friends.Clear();
        BlockedUsers.Clear();
        TeamMembers.Clear();
        RemoteTeamMembers.Clear();
        Expeditions.Clear();
        ExpeditionMembers.Clear();
        ChatChannels.Clear();
        ChatMessages.Clear();
        TradeItems.Clear();
        ClearTeam();
        CurrentExpeditionId = 0;
        ResetTrade(false, 0);
    }

    public void Apply(SocialEvent e)
    {
        switch (e)
        {
            case FriendsPageEvent x:
                foreach (var friend in x.Friends) Friends[friend.CharacterId] = friend;
                break;
            case FriendAcceptedEvent { Success: true, Friend: not null } x:
                Friends[x.Friend.CharacterId] = x.Friend;
                break;
            case FriendDeletedEvent { Success: true } x:
                Friends.Remove(x.CharacterId);
                break;
            case FriendStatusEvent x:
                Friends[x.Friend.CharacterId] = x.Friend;
                break;
            case BlockedUsersPageEvent x:
                foreach (var user in x.Users) BlockedUsers[user.CharacterId] = user;
                break;
            case BlockedUserAddedEvent { Success: true } x:
                BlockedUsers[x.User.CharacterId] = x.User;
                break;
            case BlockedUserDeletedEvent { Success: true } x:
                BlockedUsers.Remove(x.CharacterId);
                break;
            case TeamJoinedEvent x:
                TeamId = (int)x.TeamId;
                TeamOwnerId = x.OwnerId;
                TeamIsRaid = !x.IsParty;
                TeamMembers.Clear();
                foreach (var member in x.Members) TeamMembers[member.CharacterId] = member;
                break;
            case TeamLeftEvent:
            case TeamDismissedEvent:
                ClearTeam();
                break;
            case TeamMemberJoinedEvent x:
                TeamMembers[x.Member.CharacterId] = x.Member;
                break;
            case TeamMemberLeftEvent x:
                TeamMembers.Remove(x.MemberId);
                RemoteTeamMembers.Remove(x.MemberId);
                break;
            case TeamOwnerChangedEvent x:
                TeamOwnerId = x.OwnerId;
                break;
            case TeamMemberRoleChangedEvent x when TeamMembers.TryGetValue(x.MemberId, out var member):
                TeamMembers[x.MemberId] = member with { Role = x.Role };
                break;
            case TeamBecameRaidEvent:
                TeamIsRaid = true;
                break;
            case TeamMemberDisconnectedEvent x:
                RemoteTeamMembers[x.MemberId] = x.Member;
                break;
            case TeamRemoteMembersEvent x:
                foreach (var member in x.Members) RemoteTeamMembers[member.CharacterId] = member;
                break;
            case ExpeditionsPageEvent x:
                foreach (var expedition in x.Expeditions) Expeditions[expedition.Id] = expedition;
                break;
            case ExpeditionMembersPageEvent x:
                foreach (var member in x.Members) ExpeditionMembers[member.CharacterId] = member;
                break;
            case ExpeditionMemberStatusEvent x:
                ExpeditionMembers[x.Member.CharacterId] = x.Member;
                break;
            case ExpeditionOwnerChangedEvent x when Expeditions.TryGetValue((uint)x.ExpeditionId, out var expedition):
                Expeditions[(uint)x.ExpeditionId] = expedition with { OwnerId = x.OwnerId, OwnerName = x.OwnerName };
                break;
            case UnitExpeditionChangedEvent x:
                CurrentExpeditionId = x.NewExpeditionId;
                if (x.NewExpeditionId == 0) ExpeditionMembers.Remove(x.CharacterId);
                break;
            case ChatChannelJoinedEvent x:
                ChatChannels[x.Channel.Handle] = x.Channel;
                break;
            case ChatChannelLeftEvent x:
                ChatChannels.Remove(x.Channel.Handle);
                break;
            case SocialChatMessageEvent x:
                ChatMessages.Add(x);
                break;
            case TradeStartedEvent x:
                ResetTrade(true, x.OtherObjectId);
                break;
            case TradeCanceledEvent:
            case TradeCompletedEvent:
                ResetTrade(false, 0);
                break;
            case TradeItemOfferedEvent x:
                TradeItems.Add(x);
                break;
            case TradeItemRemovedEvent x when x.Mine:
                TradeItems.RemoveAll(item => item.Mine && item.SlotType == x.SlotType && item.Slot == x.Slot);
                break;
            case TradeMoneyOfferedEvent { Mine: true } x:
                MyTradeMoney = x.Amount;
                break;
            case TradeMoneyOfferedEvent x:
                OtherTradeMoney = x.Amount;
                break;
            case TradeLockChangedEvent x:
                MyTradeLock = x.MyLock;
                OtherTradeLock = x.OtherLock;
                break;
            case TradeConfirmChangedEvent x:
                MyTradeConfirmed = x.MyConfirmed;
                OtherTradeConfirmed = x.OtherConfirmed;
                break;
        }
    }

    private void ClearTeam()
    {
        TeamId = 0;
        TeamOwnerId = 0;
        TeamIsRaid = false;
        TeamMembers.Clear();
        RemoteTeamMembers.Clear();
    }

    private void ResetTrade(bool open, uint objectId)
    {
        TradeOpen = open;
        TradeObjectId = objectId;
        TradeItems.Clear();
        MyTradeMoney = OtherTradeMoney = 0;
        MyTradeLock = OtherTradeLock = false;
        MyTradeConfirmed = OtherTradeConfirmed = false;
    }
}
