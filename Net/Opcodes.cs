#nullable enable
namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Wire opcodes used by this client (10.0.2.13). Game opcodes agree between AAEmu's CSOffsets/SCOffsets
/// and the table recovered from x2game-dev.dll (X2Game.Protocol *.g.cs).
/// </summary>
public static class Opcodes
{
    /// <summary>SCErrorMsgPacket (AAEmu SCOffsets.SCErrorMsgPacket): short error1, short error2, uint type, bool isNotify.</summary>
    public const ushort SCErrorMsg = 0x14D;
    // Login (:1237) client -> login
    public const ushort CARequestWebAuth = 0x002;
    public const ushort CAListWorld = 0x008;
    public const ushort CAEnterWorld = 0x009;
    public const ushort CAPong = 0x014;

    // Login -> client
    public const ushort ACJoinResponse = 0x000;
    public const ushort ACAuthResponse = 0x003;
    public const ushort ACWorldList = 0x008;
    public const ushort ACWorldQueue = 0x009;
    public const ushort ACWorldCookie = 0x00A;
    public const ushort ACEnterWorldDenied = 0x00B;
    public const ushort ACLoginDenied = 0x00C;
    public const ushort ACPing = 0x010;

    // World (:1239) proxy channel, level 2 (both directions)
    public const ushort ChangeState = 0x000;
    public const ushort FinishState = 0x001;
    public const ushort SetGameType = 0x00F;
    public const ushort Ping = 0x012;
    public const ushort Pong = 0x013;

    // World client -> server (level 1 before the key exchange, level 5 after)
    public const ushort X2EnterWorld = 0x000;
    public const ushort CSLeaveWorld = 0x001;
    public const ushort CSAesXorKey = 0x047; // named CS_PACKET_LIST_CHARACTER in the client table
    public const ushort CSRefreshInCharacterList = 0x048;
    public const ushort CSCreateCharacter = 0x049;
    public const ushort CSDeleteCharacter = 0x04B;
    public const ushort CSSelectCharacter = 0x04C;
    public const ushort CSCheckRaceCongestion = 0x04D;
    public const ushort CSSpawnCharacter = 0x04E;
    public const ushort CSCancelCharacterDelete = 0x04F;
    public const ushort CSNotifyInGame = 0x051;
    public const ushort CSNotifyInGameCompleted = 0x052;
    public const ushort CSSendChatMessage = 0x098;
    public const ushort CSMoveUnit = 0x0C8;
    public const ushort CSSetLpManageCharacter = 0x150;
    public const ushort CSBroadcastOpenEquipInfo = 0x170;
    public const ushort CSRestrictCheck = 0x172;

    // World server -> client
    public const ushort X2EnterWorldResponse = 0x000;
    public const ushort SCPrepareLeaveWorld = 0x002;
    public const ushort SCLeaveWorldGranted = 0x003;
    public const ushort SCLeaveWorldCanceled = 0x004;
    public const ushort SCCreateCharacterResponse = 0x063;
    public const ushort SCDeleteCharacterResponse = 0x064;
    public const ushort SCCharacterDeleted = 0x066;
    public const ushort SCCancelCharacterDeleteResponse = 0x067;
    public const ushort SCCharacterCreationFailed = 0x068;
    public const ushort SCCharacterList = 0x069;
    public const ushort SCCheckRaceCongestionResponse = 0x06A;
    public const ushort SCCharacterState = 0x06C;
    public const ushort SCUnitState = 0x097;
    public const ushort SCUnitsRemoved = 0x098;
    public const ushort SCUnitMovements = 0x099;
    public const ushort SCOneUnitMovement = 0x09A;
    public const ushort SCTeleportUnit = 0x0A2;
    public const ushort SCUnitAttached = 0x0A5;
    public const ushort SCUnitDetached = 0x0A6;
    public const ushort SCUnitFlyingStateChanged = 0x0A9;
    public const ushort SCUnitPoints = 0x0EF;
    public const ushort SCChatMessage = 0x102;
    public const ushort SCDoodadCreated = 0x14E;
    public const ushort SCDoodadRemoved = 0x14F;
    public const ushort SCDoodadsCreated = 0x154;
    public const ushort SCDoodadsRemoved = 0x155;
    public const ushort SCResultRestrictCheck = 0x24A;
    public const ushort SCShowCurrentWorld = 0x354;
    public const ushort SCWorldLevelInfo = 0x38A;
    public const ushort SCSystemFeatureStateList = 0x393;
}
