#nullable enable
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

// Positions are Cry world metres, yaw is radians about +Z, velocity is m/s: see WorldCoords.

/// <summary>Unit type as sent in SCUnitState (BaseUnitType), plus <see cref="Doodad"/> for SCDoodadsCreated.</summary>
public enum UnitKind : byte
{
    Character = 0,
    Npc = 1,
    Slave = 2,
    Housing = 3,
    Transfer = 4,
    Mate = 5,
    Shipyard = 6,
    Doodad = 100,
    Unknown = 255,
}

/// <summary>UnitCustomModelParams extension gate: each tier includes the ones below it.</summary>
public enum AppearanceTier : byte
{
    None = 0,
    Hair = 1,
    Skin = 2,
    Face = 3,
}

/// <summary>Equipment slot indices (EquipmentItemSlot). Slots 19-25 are body parts sent as template ids only.</summary>
public enum EquipSlot : byte
{
    Head = 0, Neck = 1, Chest = 2, Waist = 3, Legs = 4, Hands = 5, Feet = 6, Arms = 7, Back = 8,
    Ear1 = 9, Ear2 = 10, Finger1 = 11, Finger2 = 12, Undershirt = 13, Underpants = 14,
    Mainhand = 15, Offhand = 16, Ranged = 17, Musical = 18,
    Face = 19, Hair = 20, Glasses = 21, Reserved = 22, Tail = 23, Body = 24, Beard = 25,
    Backpack = 26, Cosplay = 27,
}

/// <summary>One occupied equipment slot.</summary>
/// <param name="Slot">Slot index 0..33 (see <see cref="EquipSlot"/>).</param>
/// <param name="TemplateId">Item template id (items table).</param>
/// <param name="ItemId">Item instance id, 0 when the slot is sent as a template id only.</param>
/// <param name="Grade">Item grade, 0 when not sent.</param>
/// <param name="ImageTemplateId">For equipment detail blobs: the look/costume template id (0 = use TemplateId).</param>
/// <param name="DyeColor">For equipment detail blobs: dye item id or colour (0 = none).</param>
public sealed record EquipmentSlot(int Slot, uint TemplateId, ulong ItemId, byte Grade, uint ImageTemplateId, uint DyeColor,
    byte Flags = 0, int Count = 1, byte DetailType = 0, byte? Durability = null,
    ushort? EnchantScale = null, long CreateTime = 0, int LifespanMinutes = 0,
    ulong MadeUnitId = 0, byte WorldId = 0, long UnsecureTime = 0,
    long UnpackTime = 0, long ChargeUseSkillTime = 0);

/// <summary>Face tier of the appearance block (UnitCustomModelParams FaceModel).</summary>
public sealed class FaceParams
{
    public uint MovableDecalAssetId { get; init; }
    public float MovableDecalWeight { get; init; }
    public float MovableDecalScale { get; init; }
    public float MovableDecalRotate { get; init; }
    public short MovableDecalMoveX { get; init; }
    public short MovableDecalMoveY { get; init; }
    public uint[] FixedDecalAssetIds { get; init; } = [];
    public float[] FixedDecalWeights { get; init; } = [];
    public uint DiffuseMapId { get; init; }
    public uint NormalMapId { get; init; }
    public uint EyelashMapId { get; init; }
    public float NormalMapWeight { get; init; }
    public uint LipColor { get; init; }
    public uint LeftPupilColor { get; init; }
    public uint RightPupilColor { get; init; }
    public uint EyebrowColor { get; init; }
    public uint DecoColor { get; init; }

    /// <summary>Face-maker morph sliders (up to 128 bytes).</summary>
    public byte[] Modifier { get; init; } = [];
}

/// <summary>
/// UnitCustomModelParams as sent in SCUnitState and the character list. Fields above <see cref="Tier"/>
/// are left at zero. <see cref="Raw"/> always holds the exact wire bytes (tier byte included).
/// </summary>
public sealed class AppearanceParams
{
    public AppearanceTier Tier { get; init; }
    public byte[] Raw { get; init; } = [];

    // Hair tier
    public byte Race { get; init; }
    public byte Gender { get; init; }
    public long VisualRaceExpiredTime { get; init; }
    public byte VisualRace { get; init; }
    public byte VisualGender { get; init; }
    public uint HairColor { get; init; }
    public uint HornColor { get; init; }
    public uint DefaultHairColor { get; init; }
    public uint TwoToneHairColor { get; init; }
    public float TwoToneFirstWidth { get; init; }
    public float TwoToneSecondWidth { get; init; }

    // Skin tier
    public uint SkinColor { get; init; }
    public uint BodyDiffuseMap { get; init; }
    public uint BodyNormalMap { get; init; }
    public float BodyWeight { get; init; }

    // Face tier
    public FaceParams? Face { get; init; }
}

/// <summary>Everything decoded from one SCUnitState (or one SCDoodadsCreated entry).</summary>
public sealed class UnitSnapshot
{
    /// <summary>Object id ("bc", 24 bits); the key used by every later movement/removal packet.</summary>
    public uint UnitId { get; init; }
    public UnitKind Kind { get; init; }
    public string Name { get; init; } = "";

    /// <summary>NPC/Slave/House/Transfer/Mate/Shipyard/Doodad template id; 0 for characters.</summary>
    public uint TemplateId { get; init; }

    /// <summary>Doodads: the current function group (phase) id, which selects the phase's model; 0 otherwise.</summary>
    public uint PhaseId { get; init; }

    /// <summary>Character id for players (characters.id), slave/shipyard db id for those kinds, else 0.</summary>
    public ulong DbId { get; init; }

    /// <summary>Owner character id for NPCs/Mates/Slaves when sent, else 0.</summary>
    public ulong OwnerId { get; init; }
    public string MasterName { get; init; } = "";

    /// <summary>Slave/mate timeline id from the SCUnitState identity union; null for other kinds.</summary>
    public short? TimelineId { get; init; }

    /// <summary>Current character/mate/slave attachment from UnitState relation data; zero when unattached.</summary>
    public uint AttachedToUnitId { get; init; }
    public sbyte AttachedPoint { get; init; } = -1;

    /// <summary>
    /// Doodads only: when non-zero (or <see cref="AttachPoint"/> &gt; 0) <see cref="Position"/> and
    /// <see cref="Yaw"/> are relative to this parent unit instead of world coordinates.
    /// </summary>
    public uint ParentUnitId { get; init; }

    /// <summary>Doodads only: attach point on the parent, -1 when not attached.</summary>
    public sbyte AttachPoint { get; init; } = -1;

    /// <summary>Model (actor model) id, see the <c>models</c> table. Doodads carry 0.</summary>
    public uint ModelId { get; init; }
    public Vector3 Position { get; init; }

    /// <summary>Radians about +Z; valid when <see cref="HasYaw"/>.</summary>
    public float Yaw { get; init; }
    public bool HasYaw { get; init; }
    public float Scale { get; init; } = 1f;
    public sbyte Level { get; init; }
    public ulong Hp { get; init; }
    public ulong Mp { get; init; }

    /// <summary>16 * gender + race.</summary>
    public byte RaceGender { get; init; }
    public IReadOnlyList<EquipmentSlot> Equipment { get; init; } = [];
    public AppearanceParams? Appearance { get; init; }

    /// <summary>The full packet body (after the opcode), for anything not decoded here.</summary>
    public byte[] RawBody { get; init; } = [];

    /// <summary>Null when every section decoded; otherwise where decoding stopped (earlier fields are valid).</summary>
    public string? DecodeError { get; init; }
}

/// <summary>Lobby character record (SCCharacterList / SCCharacterState).</summary>
public sealed class LobbyCharacter
{
    public ulong Id { get; init; }
    public string Name { get; init; } = "";
    public byte Race { get; init; }
    public byte Gender { get; init; }
    public byte Level { get; init; }
    /// <summary>Absolute experience from the SCCharacterState world-entry tail.</summary>
    public uint Experience { get; init; }
    public uint Hp { get; init; }
    public uint Mp { get; init; }
    public uint ZoneId { get; init; }
    public uint FactionId { get; init; }
    public string FactionName { get; init; } = "";
    public uint ExpeditionId { get; init; }
    public Vector3 Position { get; init; }
    public IReadOnlyList<EquipmentSlot> Equipment { get; init; } = [];
    public AppearanceParams? Appearance { get; init; }
    public byte[] Raw { get; init; } = [];
}

/// <summary>Movement kind (MoveTypeEnum).</summary>
public enum MoveKind : byte
{
    Default = 0,
    Unit = 1,
    Vehicle = 2,
    Vehicle3 = 3,
    Ship = 4,
    ShipRequest = 5,
    Transfer = 6,
}

/// <summary>One decoded movement record (SCUnitMovements / SCOneUnitMovement / CSMoveUnit body).</summary>
public sealed class UnitMovement
{
    public uint UnitId { get; init; }
    public MoveKind Kind { get; init; }

    /// <summary>Sender's physics clock, milliseconds.</summary>
    public uint Time { get; init; }

    /// <summary>MoveTypeFlags: 0x02 moving, 0x04 stopping, 0x06 jump, 0x08 combat, 0x40 standing on object.</summary>
    public byte Flags { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Velocity { get; init; }
    public float Yaw { get; init; }
    public bool HasPosition { get; init; } = true;

    /// <summary>
    /// Vehicle, ship and transfer bodies: the full orientation (Cry axes) the sender reported, decoded from the three
    /// short quaternion parts (w implied, non-negative). Identity for unit bodies, which carry only a heading.
    /// </summary>
    public Quaternion Rotation { get; init; } = Quaternion.Identity;

    /// <summary>Vehicle and ship bodies: angular velocity (rad/s, Cry axes).</summary>
    public Vector3 AngularVelocity { get; init; }

    /// <summary>Vehicle bodies: steering as the driver's client reported it (-1..1, positive turns left).</summary>
    public float Steering { get; init; }

    // Unit kind only
    public sbyte DeltaX { get; init; }
    public sbyte DeltaY { get; init; }
    public sbyte DeltaZ { get; init; }
    public sbyte Stance { get; init; }
    public byte Alertness { get; init; }
    public ushort ActorFlags { get; init; }

    /// <summary>
    /// CSMoveUnit body for a Unit movement exactly as the real client lays it out: bc id, type 1, time,
    /// flags, position, velocity shorts, rotation (0, 0, heading), delta input, stance, alertness,
    /// actor flags, and a trailing zero byte. Actor-flag optional blocks (0x20/0x40/0x80/0x100) are not written.
    /// </summary>
    public byte[] ToCsMoveUnitBody()
    {
        if ((ActorFlags & 0x81E0) != 0)
            throw new NotSupportedException($"actor flags 0x{ActorFlags:X} need optional blocks this writer does not produce");
        return new WireWriter()
            .Bc(UnitId)
            .U8((byte)MoveKind.Unit)
            .U32(Time)
            .U8(Flags)
            .Position(Position)
            .S16(WorldCoords.VelocityToWire(Velocity.X))
            .S16(WorldCoords.VelocityToWire(Velocity.Y))
            .S16(WorldCoords.VelocityToWire(Velocity.Z))
            .S8(0).S8(0).S8(WorldCoords.YawToHeading(Yaw))
            .S8(DeltaX).S8(DeltaY).S8(DeltaZ)
            .S8(Stance)
            .U8(Alertness)
            .U16(ActorFlags)
            .U8(0)
            .ToArray();
    }
}
