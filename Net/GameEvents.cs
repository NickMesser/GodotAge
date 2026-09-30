#nullable enable
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Base of everything <see cref="GameClient"/> reports. Events are produced on the client's network
/// threads and queued; read them from any thread with <see cref="GameClient.TryDequeue"/>.
/// </summary>
public abstract record GameEvent
{
    public DateTime UtcTime { get; init; } = DateTime.UtcNow;
}

public enum ClientStage
{
    Disconnected,
    ConnectingLogin,
    Authenticating,
    Authenticated,
    RequestingWorld,
    ConnectingWorld,
    KeyExchange,
    CharacterSelect,
    EnteringWorld,
    InWorld,
    LeavingWorld,
}

/// <summary>Stage change or informational progress message.</summary>
public sealed record ProgressEvent(ClientStage Stage, string Message) : GameEvent;

/// <summary>A failure. <paramref name="Fatal"/> means the connection is gone.</summary>
public sealed record ErrorEvent(string Message, bool Fatal, Exception? Exception = null) : GameEvent;

public sealed record WorldServerInfo(byte Id, string Name, bool Available, byte Congestion);

/// <summary>Character summary from the login server's world list.</summary>
public sealed record LoginCharacterSummary(ulong AccountId, byte WorldId, uint CharacterId, string Name, byte Race, byte Gender);

public sealed record WorldListEvent(IReadOnlyList<WorldServerInfo> Worlds, IReadOnlyList<LoginCharacterSummary> Characters) : GameEvent;

/// <summary>Full lobby records from the World server (after the key exchange).</summary>
public sealed record CharacterListEvent(IReadOnlyList<LobbyCharacter> Characters) : GameEvent;

public sealed record CharacterCreatedEvent(LobbyCharacter Character) : GameEvent;
public sealed record CharacterCreationFailedEvent(byte Reason) : GameEvent;
public sealed record CharacterDeleteResponseEvent(CharacterDeleteResponse Response) : GameEvent;
public sealed record CharacterDeleteCancelResponseEvent(CharacterDeleteCancelResponse Response) : GameEvent;
public sealed record CharacterDeletedEvent(ulong CharacterId, string CharacterName) : GameEvent;

/// <summary>
/// The selected character is in the world. <paramref name="UnitId"/> is its object id (the id to move),
/// <paramref name="ZoneId"/> the zone key, <paramref name="WorldId"/> the game-server (shard) id.
/// </summary>
public sealed record EnteredWorldEvent(
    uint UnitId, ulong CharacterId, string Name, Vector3 Position, float Yaw, uint ZoneId, uint InstanceId, byte WorldId,
    UnitSnapshot Self, uint Experience = 0) : GameEvent;

/// <summary>A unit (or doodad) entered the area of interest, or its state was re-sent.</summary>
public sealed record UnitAppearedEvent(UnitSnapshot Unit) : GameEvent;

public sealed record UnitMovedEvent(UnitMovement Movement) : GameEvent;

public sealed record UnitAttachedEvent(uint ChildUnitId, byte Point, uint ParentUnitId, byte Reason) : GameEvent;
public sealed record UnitDetachedEvent(uint ChildUnitId, byte Reason) : GameEvent;
public sealed record UnitFlyingStateChangedEvent(uint UnitId, bool IsFlying) : GameEvent;

public sealed record UnitsRemovedEvent(IReadOnlyList<uint> UnitIds, bool Doodads) : GameEvent;

/// <summary>Server-forced position change (SCTeleportUnit) of the own character.</summary>
public sealed record TeleportedEvent(byte Reason, Vector3 Position) : GameEvent;

public sealed record UnitPointsEvent(uint UnitId, int Hp, int Mp) : GameEvent;

public sealed record ChatEvent(short ChatType, uint SenderUnitId, string SenderName, string Message) : GameEvent;

/// <summary>SCPrepareLeaveWorld: the server started the logout timer.</summary>
public sealed record LeavingWorldEvent(int DelayMs, byte Target) : GameEvent;

/// <summary>Both connections are closed.</summary>
public sealed record DisconnectedEvent(string Reason) : GameEvent;

/// <summary>Any S->C packet this client does not decode (only when <see cref="GameClientOptions.EmitUnhandledPackets"/>).</summary>
public sealed record RawPacketEvent(ushort Opcode, byte Level, byte[] Body) : GameEvent;
/// <summary>SCErrorMsg: a server error (enum_error_messages id; the client shows the ERROR_MSG ui_text for it).</summary>
public sealed record ServerErrorEvent(short Error, short Error2, uint Type, bool IsNotify) : GameEvent;
