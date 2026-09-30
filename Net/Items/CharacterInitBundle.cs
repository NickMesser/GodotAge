#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>The semantic client surface populated by a login-time server packet.</summary>
public enum CharacterInitSurface
{
    Inventory,
    Bank,
    Equipment,
    Currency,
    Quests,
    Mail,
    Friends,
    BlockedUsers,
    PartyAndRaid,
    Expedition,
    ChatChannels,
}

/// <summary>
/// A packet in the character entry burst and the data surface it initializes. The surface names are
/// semantic mappings from the packet payload and server call site; this is not a recovered Lua callback map.
/// </summary>
public sealed record CharacterInitPacketBinding(
    ushort Opcode,
    string Packet,
    CharacterInitSurface Surface,
    string EntryPhase,
    string Payload);

/// <summary>
/// Packet map for the inventory/social portions of character entry. Opcode values are the 10.0.2.13
/// server offsets; SCQuests (0x132) is currently named by AAEmu's SCOffsets table rather than the
/// generated client opcode enum.
/// </summary>
public static class CharacterInitBundle
{
    private static readonly CharacterInitPacketBinding[] BindingsInternal =
    [
        new(0x076, "SCCharacterInvenInitPacket", CharacterInitSurface.Inventory, "character select",
            "inventory and bank slot capacities"),
        new(0x077, "SCCharacterInvenContentsPacket", CharacterInitSurface.Inventory, "character select",
            "fragmented inventory item contents; SlotType distinguishes inventory from bank/warehouse"),
        new(0x077, "SCCharacterInvenContentsPacket", CharacterInitSurface.Bank, "character select",
            "fragmented bank/warehouse item contents"),
        new(0x079, "SCCharacterPrelimEquipmentsPacket", CharacterInitSurface.Equipment, "character select",
            "preliminary equipped-item view sent after inventory contents"),
        new(0x1CE, "SCCharacterGamePointsPacket", CharacterInitSurface.Currency, "character select",
            "fourteen game-point slots including honor, vocation, and leadership values"),
        new(0x070, "SCCharacterLaborPowerChangedPacket", CharacterInitSurface.Currency, "character entry",
            "labor totals and recharge values"),
        new(0x1CF, "SCGamePointInitedPacket", CharacterInitSurface.Currency, "character entry",
            "initial value for one typed game point"),
        new(0x132, "SCQuestsPacket", CharacterInitSurface.Quests, "character select / world entry",
            "active quest records; sent in chunks of up to 20"),
        new(0x133, "SCCompletedQuestsPacket", CharacterInitSurface.Quests, "character select / world entry",
            "completed-quest bitset blocks"),
        new(0x15B, "SCCountTotalMailPacket", CharacterInitSurface.Mail, "character select",
            "mailbox unread counts"),
        new(0x07A, "SCFriendsPacket", CharacterInitSurface.Friends, "character select",
            "friend rows, paged by the server"),
        new(0x07F, "SCBlockedUsersPacket", CharacterInitSurface.BlockedUsers, "character select",
            "blocked-user rows, paged by the server"),
        new(0x10B, "SCJoinedTeamPacket", CharacterInitSurface.PartyAndRaid, "character entry",
            "current team or raid snapshot and member list"),
        new(0x00A, "SCExpeditionListPacket", CharacterInitSurface.Expedition, "character entry",
            "expedition/guild list"),
        new(0x100, "SCJoinedChatChannelPacket", CharacterInitSurface.ChatChannels, "character entry",
            "joined chat channel handle and display name"),
    ];

    public static IReadOnlyList<CharacterInitPacketBinding> Packets => BindingsInternal;

    public static IReadOnlyList<CharacterInitPacketBinding> ForSurface(CharacterInitSurface surface) =>
        BindingsInternal.Where(binding => binding.Surface == surface).ToArray();
}
