#nullable enable

using System.Numerics;
using System.Text;
using System.Collections.ObjectModel;

namespace AAEmu.GodotViewer.Net;

public sealed record VehicleStateEntry(int Field1, int Field2, int Field3);

public interface IVehicleProtocolEvent { }

public sealed record SlaveCreatedBody(
    uint OwnerObjectId, short TimelineId, uint SlaveObjectId, ulong UnknownId, string CreatorName);

public sealed record SlaveRemovedBody(uint OwnerObjectId, short TimelineId);
public sealed record SlaveDespawnBody(uint SlaveObjectId, bool Success);
public sealed record SlaveBoundBody(ulong MasterId, sbyte MasterWorldId, uint SlaveObjectId);
public sealed record SlaveEscapedBody(uint SlaveObjectId, long PositionX, long PositionY, float PositionZ, float Rotation);

public sealed record SlaveStateBody(
    uint ObjectId,
    short TimelineId,
    ulong Type,
    IReadOnlyList<VehicleStateEntry> Skills,
    IReadOnlyList<VehicleStateEntry> Tags,
    IReadOnlyList<VehicleStateEntry> Charges,
    string CreatorName,
    ulong OwnerId,
    int DatabaseId);

/// <summary>SCMySlave's signed X/Y values are retained in their native fixed-point wire form.</summary>
public sealed record MySlaveBody(
    uint ObjectId,
    short TimelineId,
    string Name,
    uint TemplateId,
    ulong Hp,
    ulong MaxHp,
    long PositionX,
    long PositionY,
    float PositionZ);

public sealed record MateSpawnedBody(
    short TimelineId,
    sbyte MateType,
    int Id,
    ulong ItemId,
    sbyte UserState,
    uint Experience,
    int SpawnDelayTime,
    IReadOnlyList<int> MountSkillIds);

public sealed record MateStateBody(
    uint ObjectId,
    IReadOnlyList<VehicleStateEntry> Skills,
    IReadOnlyList<VehicleStateEntry> Tags,
    IReadOnlyList<VehicleStateEntry> Charges);

public sealed record VehicleEquipmentChangeEntry(
    ItemSnapshot? FirstItem,
    ItemSnapshot? SecondItem,
    sbyte FirstSlotType,
    sbyte FirstSlot,
    sbyte SecondSlotType,
    sbyte SecondSlot,
    long ExpireTime);

public sealed record MateEquipmentChangedBody(
    ulong CharacterId,
    short TimelineId,
    int PassengerId,
    bool Bts,
    IReadOnlyList<VehicleEquipmentChangeEntry> Entries,
    bool Success);

public sealed record MateEquipmentExpiredBody(short TimelineId, sbyte Type, sbyte Index);
public sealed record MateEquipmentFlagsChangedBody(short TimelineId, sbyte Type, sbyte Index, sbyte Flags);

public sealed record SlaveEquipmentChangedBody(
    ulong CharacterId,
    short TimelineId,
    uint DatabaseSlaveId,
    bool Bts,
    IReadOnlyList<VehicleEquipmentChangeEntry> Entries,
    bool Success);

public sealed record SlaveEquipmentExpiredBody(short TimelineId, sbyte Type, sbyte Index);
public sealed record SlaveEquipmentFlagsChangedBody(short TimelineId, sbyte Type, sbyte Index, sbyte Flags);

public sealed record SlaveCreatedEvent(SlaveCreatedBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveRemovedEvent(SlaveRemovedBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveDespawnEvent(SlaveDespawnBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveBoundEvent(SlaveBoundBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveStateEvent(SlaveStateBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record MySlaveEvent(MySlaveBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveEscapedEvent(SlaveEscapedBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveEquipmentChangedEvent(SlaveEquipmentChangedBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveEquipmentExpiredEvent(SlaveEquipmentExpiredBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record SlaveEquipmentFlagsChangedEvent(SlaveEquipmentFlagsChangedBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record MateSpawnedEvent(MateSpawnedBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record MateStateEvent(MateStateBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record MateEquipmentChangedEvent(MateEquipmentChangedBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record MateEquipmentExpiredEvent(MateEquipmentExpiredBody Body) : GameEvent, IVehicleProtocolEvent;
public sealed record MateEquipmentFlagsChangedEvent(MateEquipmentFlagsChangedBody Body) : GameEvent, IVehicleProtocolEvent;

/// <summary>Recovered 10.0.2.13 slave, vehicle, and mate packet layouts.</summary>
public static class VehiclesProtocol
{
    public const ushort SCSlaveCreated = 0x08E;
    public const ushort SCSlaveRemoved = 0x08F;
    public const ushort SCSlaveDespawn = 0x090;
    public const ushort SCSlaveBound = 0x091;
    public const ushort SCMySlave = 0x092;
    public const ushort SCEscapeSlave = 0x093;
    public const ushort SCSlaveEquipmentExpired = 0x094;
    public const ushort SCSlaveEquipmentChanged = 0x095;
    public const ushort SCSlaveEquipmentFlagsChanged = 0x096;
    public const ushort SCMateSpawned = 0x16A;
    public const ushort SCMateEquipmentChanged = 0x16B;
    public const ushort SCMateEquipmentExpired = 0x16C;
    public const ushort SCMateEquipmentFlagsChanged = 0x16D;
    public const ushort SCSlaveState = 0x21B;
    public const ushort SCMateState = 0x21C;

    private const int CreatorNameMaxBytes = 0x80;
    private const int SlaveNameMaxBytes = 0x400;
    private const int MateSkillSlots = 10;
    private const byte MaxMateEquipmentChanges = 2;
    private const byte MaxSlaveEquipmentChanges = 3;

    public static GameEvent Parse(ushort opcode, byte[] body)
    {
        var r = new WireReader(body);
        GameEvent result = opcode switch
        {
            SCSlaveCreated => new SlaveCreatedEvent(ReadSlaveCreated(r)),
            SCSlaveRemoved => new SlaveRemovedEvent(new SlaveRemovedBody(r.Bc(), r.S16())),
            SCSlaveDespawn => new SlaveDespawnEvent(new SlaveDespawnBody(r.Bc(), r.Bool())),
            SCSlaveBound => new SlaveBoundEvent(new SlaveBoundBody(r.U64(), r.S8(), r.Bc())),
            SCMySlave => new MySlaveEvent(ReadMySlave(r)),
            SCEscapeSlave => new SlaveEscapedEvent(
                new SlaveEscapedBody(r.Bc(), r.S64(), r.S64(), r.F32(), r.F32())),
            SCSlaveEquipmentChanged => new SlaveEquipmentChangedEvent(ReadSlaveEquipmentChanged(r)),
            SCSlaveEquipmentExpired => new SlaveEquipmentExpiredEvent(
                new SlaveEquipmentExpiredBody(r.S16(), r.S8(), r.S8())),
            SCSlaveEquipmentFlagsChanged => new SlaveEquipmentFlagsChangedEvent(
                new SlaveEquipmentFlagsChangedBody(r.S16(), r.S8(), r.S8(), r.S8())),
            SCSlaveState => new SlaveStateEvent(ReadSlaveState(r)),
            SCMateSpawned => new MateSpawnedEvent(ReadMateSpawned(r)),
            SCMateState => new MateStateEvent(ReadMateState(r)),
            SCMateEquipmentChanged => new MateEquipmentChangedEvent(ReadMateEquipmentChanged(r)),
            SCMateEquipmentExpired => new MateEquipmentExpiredEvent(
                new MateEquipmentExpiredBody(r.S16(), r.S8(), r.S8())),
            SCMateEquipmentFlagsChanged => new MateEquipmentFlagsChangedEvent(
                new MateEquipmentFlagsChangedBody(r.S16(), r.S8(), r.S8(), r.S8())),
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "not a vehicle protocol opcode"),
        };
        EnsureEnd(r, $"vehicle packet 0x{opcode:X3}");
        return result;
    }

    private static SlaveCreatedBody ReadSlaveCreated(WireReader r) => new(
        r.Bc(), r.S16(), r.Bc(), r.U64(), ReadBoundedString(r, CreatorNameMaxBytes, "creatorName"));

    private static SlaveStateBody ReadSlaveState(WireReader r)
    {
        var objectId = r.Bc();
        var timelineId = r.S16();
        var type = r.U64();
        var skills = ReadStateEntries(r, "slave skills");
        var tags = ReadStateEntries(r, "slave tags");
        var charges = ReadStateEntries(r, "slave charges");
        return new SlaveStateBody(objectId, timelineId, type, skills, tags, charges,
            ReadBoundedString(r, CreatorNameMaxBytes, "creatorName"), r.U64(), r.S32());
    }

    private static MySlaveBody ReadMySlave(WireReader r) => new(
        r.Bc(), r.S16(), ReadBoundedString(r, SlaveNameMaxBytes, "slaveName"), r.U32(),
        r.U64(), r.U64(), r.S64(), r.S64(), r.F32());

    private static MateSpawnedBody ReadMateSpawned(WireReader r)
    {
        var timelineId = r.S16();
        var mateType = r.S8();
        var id = r.S32();
        var itemId = r.U64();
        var userState = r.S8();
        var experience = r.U32();
        var spawnDelayTime = r.S32();
        var skills = new int[MateSkillSlots];
        for (var i = 0; i < skills.Length; i++)
            skills[i] = r.S32();
        return new MateSpawnedBody(
            timelineId, mateType, id, itemId, userState, experience, spawnDelayTime, skills);
    }

    private static MateStateBody ReadMateState(WireReader r) => new(
        r.Bc(),
        ReadStateEntries(r, "mate skills"),
        ReadStateEntries(r, "mate tags"),
        ReadStateEntries(r, "mate charges"));

    private static MateEquipmentChangedBody ReadMateEquipmentChanged(WireReader r)
    {
        var characterId = r.U64();
        var timelineId = r.S16();
        var passengerId = r.S32();
        var bts = r.Bool();
        var count = r.U8();
        if (count > MaxMateEquipmentChanges)
            throw new WireException($"mate equipment entry count {count} exceeds {MaxMateEquipmentChanges}");

        var entries = ReadEquipmentEntries(r, count);

        return new MateEquipmentChangedBody(characterId, timelineId, passengerId, bts, entries, r.Bool());
    }

    private static SlaveEquipmentChangedBody ReadSlaveEquipmentChanged(WireReader r)
    {
        var characterId = r.U64();
        var timelineId = r.S16();
        var databaseSlaveId = r.U32();
        var bts = r.Bool();
        var count = r.U8();
        if (count > MaxSlaveEquipmentChanges)
            throw new WireException($"slave equipment entry count {count} exceeds {MaxSlaveEquipmentChanges}");
        return new SlaveEquipmentChangedBody(
            characterId, timelineId, databaseSlaveId, bts, ReadEquipmentEntries(r, count), r.Bool());
    }

    private static IReadOnlyList<VehicleEquipmentChangeEntry> ReadEquipmentEntries(WireReader r, byte count)
    {
        var entries = new List<VehicleEquipmentChangeEntry>(count);
        for (var i = 0; i < count; i++)
        {
            entries.Add(new VehicleEquipmentChangeEntry(
                InventoryProtocol.ReadItem(r), InventoryProtocol.ReadItem(r),
                r.S8(), r.S8(), r.S8(), r.S8(), r.S64()));
        }
        return entries;
    }

    private static IReadOnlyList<VehicleStateEntry> ReadStateEntries(WireReader r, string name)
    {
        var count = r.U32();
        const int entrySize = sizeof(int) * 3;
        if (count > (uint)(r.Remaining / entrySize))
            throw new WireException($"{name} count {count} exceeds the remaining packet body");

        var entries = new List<VehicleStateEntry>((int)count);
        for (var i = 0u; i < count; i++)
            entries.Add(new VehicleStateEntry(r.S32(), r.S32(), r.S32()));
        return entries;
    }

    private static string ReadBoundedString(WireReader r, int maximumBytes, string field)
    {
        var length = r.U16();
        if (length > maximumBytes)
            throw new WireException($"{field} byte length {length} exceeds {maximumBytes}");
        return Encoding.UTF8.GetString(r.Bytes(length)).TrimEnd('\0');
    }

    private static void EnsureEnd(WireReader r, string parser)
    {
        if (r.Remaining != 0)
            throw new WireException($"{parser} left {r.Remaining} bytes");
    }
}

public sealed class VehiclePacketParserFamily : IPacketParserFamily
{
    private static readonly HashSet<ushort> Owned =
    [
        VehiclesProtocol.SCSlaveCreated,
        VehiclesProtocol.SCSlaveRemoved,
        VehiclesProtocol.SCSlaveDespawn,
        VehiclesProtocol.SCSlaveBound,
        VehiclesProtocol.SCMySlave,
        VehiclesProtocol.SCEscapeSlave,
        VehiclesProtocol.SCSlaveEquipmentExpired,
        VehiclesProtocol.SCSlaveEquipmentChanged,
        VehiclesProtocol.SCSlaveEquipmentFlagsChanged,
        VehiclesProtocol.SCSlaveState,
        VehiclesProtocol.SCMateSpawned,
        VehiclesProtocol.SCMateEquipmentChanged,
        VehiclesProtocol.SCMateEquipmentExpired,
        VehiclesProtocol.SCMateEquipmentFlagsChanged,
        VehiclesProtocol.SCMateState,
    ];

    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (!Owned.Contains(opcode))
        {
            events = [];
            return false;
        }

        events = [VehiclesProtocol.Parse(opcode, body)];
        return true;
    }
}

/// <summary>
/// One mate/slave unit joined only by its SCUnitState object id. Combat values come from the
/// main-thread <see cref="CombatState"/> entry for that same object id.
/// </summary>
public sealed record MateSlaveUnitView(
    UnitSnapshot Unit,
    CombatUnitState? Combat,
    SlaveCreatedBody? SlaveCreated,
    SlaveStateBody? SlaveState,
    MySlaveBody? MySlave,
    MateStateBody? MateState,
    MateSpawnedBody? MateSpawned)
{
    public sbyte Level => Combat?.Level ?? Unit.Level;
    public ulong Hp => Combat is null ? Unit.Hp : (ulong)Math.Max(0, Combat.Hp);
    public ulong? MaxHp => MySlave?.MaxHp ??
        (Combat?.MaxHp is { } maxHp ? (ulong)Math.Max(0, maxHp) : null);
    public IReadOnlyList<uint> Skills => Combat?.Skills ?? Array.Empty<uint>();
}

/// <summary>
/// Main-thread mate/slave reducer. Timeline-only mate announcements remain separate until another
/// packet supplies an explicit object id; the reducer never joins them through item or database ids.
/// </summary>
public sealed class MateSlaveState
{
    private readonly Dictionary<uint, UnitSnapshot> _unitSnapshots = [];
    private readonly Dictionary<uint, MateSlaveUnitView> _units = [];
    private readonly Dictionary<uint, SlaveCreatedBody> _slaveCreated = [];
    private readonly Dictionary<uint, SlaveStateBody> _slaveStates = [];
    private readonly Dictionary<uint, MySlaveBody> _mySlavesByObjectId = [];
    private readonly Dictionary<short, MySlaveBody> _mySlavesByTimelineId = [];
    private readonly Dictionary<uint, MateStateBody> _mateStates = [];
    private readonly Dictionary<short, MateSpawnedBody> _matesByTimelineId = [];
    private readonly Dictionary<uint, Vector3> _positions = [];
    private readonly Dictionary<short, Dictionary<int, ItemSnapshot>> _slaveEquipment = [];
    private readonly Dictionary<short, Dictionary<int, ItemSnapshot>> _mateEquipment = [];

    public IReadOnlyDictionary<uint, MateSlaveUnitView> Units => Copy(_units);
    public IReadOnlyDictionary<uint, MySlaveBody> MySlavesByObjectId => Copy(_mySlavesByObjectId);
    public IReadOnlyDictionary<short, MySlaveBody> MySlavesByTimelineId => Copy(_mySlavesByTimelineId);
    public IReadOnlyDictionary<short, MateSpawnedBody> MatesByTimelineId => Copy(_matesByTimelineId);
    public IReadOnlyDictionary<int, ItemSnapshot> SlaveEquipment(short timelineId) =>
        _slaveEquipment.TryGetValue(timelineId, out var slots) ? Copy(slots) : Empty<int, ItemSnapshot>();
    public IReadOnlyDictionary<int, ItemSnapshot> MateEquipment(short timelineId) =>
        _mateEquipment.TryGetValue(timelineId, out var slots) ? Copy(slots) : Empty<int, ItemSnapshot>();
    public Vector3 Position(uint objectId) => _positions.GetValueOrDefault(objectId);
    public IReadOnlyDictionary<short, SlaveEquipmentChangedBody> SlaveEquipmentByTimelineId { get; private set; }
        = new ReadOnlyDictionary<short, SlaveEquipmentChangedBody>(new Dictionary<short, SlaveEquipmentChangedBody>());
    public IReadOnlyDictionary<short, MateEquipmentChangedBody> MateEquipmentByTimelineId { get; private set; }
        = new ReadOnlyDictionary<short, MateEquipmentChangedBody>(new Dictionary<short, MateEquipmentChangedBody>());
    public SlaveEquipmentExpiredBody? LastSlaveEquipmentExpired { get; private set; }
    public SlaveEquipmentFlagsChangedBody? LastSlaveEquipmentFlagsChanged { get; private set; }
    public MateEquipmentExpiredBody? LastMateEquipmentExpired { get; private set; }
    public MateEquipmentFlagsChangedBody? LastMateEquipmentFlagsChanged { get; private set; }

    public void Clear()
    {
        _unitSnapshots.Clear();
        _units.Clear();
        _slaveCreated.Clear();
        _slaveStates.Clear();
        _mySlavesByObjectId.Clear();
        _mySlavesByTimelineId.Clear();
        _mateStates.Clear();
        _matesByTimelineId.Clear();
        _positions.Clear();
        _slaveEquipment.Clear();
        _mateEquipment.Clear();
        SlaveEquipmentByTimelineId = Empty<short, SlaveEquipmentChangedBody>();
        MateEquipmentByTimelineId = Empty<short, MateEquipmentChangedBody>();
        LastSlaveEquipmentExpired = null;
        LastSlaveEquipmentFlagsChanged = null;
        LastMateEquipmentExpired = null;
        LastMateEquipmentFlagsChanged = null;
    }

    public void Apply(GameEvent value, CombatState combatState)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(combatState);
        switch (value)
        {
            case UnitAppearedEvent appeared:
                Track(appeared.Unit);
                break;
            case UnitStateEvent state:
                Track(state.Snapshot.Unit);
                break;
            case UnitMovedEvent moved when moved.Movement.HasPosition && _unitSnapshots.ContainsKey(moved.Movement.UnitId):
                _positions[moved.Movement.UnitId] = moved.Movement.Position;
                break;
            case UnitsRemovedEvent removed:
                foreach (var objectId in removed.UnitIds) RemoveObject(objectId);
                break;
            case SlaveCreatedEvent created:
                _slaveCreated[created.Body.SlaveObjectId] = created.Body;
                break;
            case SlaveRemovedEvent removed:
                RemoveSlaveTimeline(removed.Body.OwnerObjectId, removed.Body.TimelineId);
                break;
            case SlaveDespawnEvent despawned when despawned.Body.Success:
                RemoveObject(despawned.Body.SlaveObjectId);
                break;
            case SlaveStateEvent state:
                _slaveStates[state.Body.ObjectId] = state.Body;
                break;
            case MySlaveEvent mine:
                _mySlavesByObjectId[mine.Body.ObjectId] = mine.Body;
                _mySlavesByTimelineId[mine.Body.TimelineId] = mine.Body;
                _positions[mine.Body.ObjectId] = new Vector3(
                    (mine.Body.PositionX >> 32) / 4096f,
                    (mine.Body.PositionY >> 32) / 4096f,
                    mine.Body.PositionZ);
                break;
            case SlaveEscapedEvent escaped:
                _positions[escaped.Body.SlaveObjectId] = new Vector3(
                    (escaped.Body.PositionX >> 32) / 4096f,
                    (escaped.Body.PositionY >> 32) / 4096f,
                    escaped.Body.PositionZ);
                break;
            case MateStateEvent state:
                _mateStates[state.Body.ObjectId] = state.Body;
                break;
            case MateSpawnedEvent spawned:
                _matesByTimelineId[spawned.Body.TimelineId] = spawned.Body;
                break;
            case SlaveEquipmentChangedEvent equipment:
                SlaveEquipmentByTimelineId = With(SlaveEquipmentByTimelineId, equipment.Body.TimelineId, equipment.Body);
                ApplyEquipment(_slaveEquipment, equipment.Body.TimelineId, equipment.Body.Entries, equipment.Body.Success);
                break;
            case MateEquipmentChangedEvent equipment:
                MateEquipmentByTimelineId = With(MateEquipmentByTimelineId, equipment.Body.TimelineId, equipment.Body);
                ApplyEquipment(_mateEquipment, equipment.Body.TimelineId, equipment.Body.Entries, equipment.Body.Success);
                break;
            case SlaveEquipmentExpiredEvent expired:
                LastSlaveEquipmentExpired = expired.Body;
                RemoveEquipment(_slaveEquipment, expired.Body.TimelineId, expired.Body.Index);
                break;
            case SlaveEquipmentFlagsChangedEvent flags:
                LastSlaveEquipmentFlagsChanged = flags.Body;
                ApplyEquipmentFlags(_slaveEquipment, flags.Body.TimelineId, flags.Body.Index, flags.Body.Flags);
                break;
            case MateEquipmentExpiredEvent expired:
                LastMateEquipmentExpired = expired.Body;
                RemoveEquipment(_mateEquipment, expired.Body.TimelineId, expired.Body.Index);
                break;
            case MateEquipmentFlagsChangedEvent flags:
                LastMateEquipmentFlagsChanged = flags.Body;
                ApplyEquipmentFlags(_mateEquipment, flags.Body.TimelineId, flags.Body.Index, flags.Body.Flags);
                break;
        }

        RefreshViews(combatState);
    }

    private void Track(UnitSnapshot unit)
    {
        if (unit.Kind is UnitKind.Mate or UnitKind.Slave)
        {
            _unitSnapshots[unit.UnitId] = unit;
            _positions[unit.UnitId] = unit.Position;
        }
    }

    private void RemoveObject(uint objectId)
    {
        _unitSnapshots.Remove(objectId);
        _units.Remove(objectId);
        _slaveCreated.Remove(objectId);
        _slaveStates.Remove(objectId);
        _mateStates.Remove(objectId);
        _positions.Remove(objectId);
        if (_mySlavesByObjectId.Remove(objectId, out var mine))
            _mySlavesByTimelineId.Remove(mine.TimelineId);
    }

    private void RemoveSlaveTimeline(uint ownerObjectId, short timelineId)
    {
        var objectIds = _slaveCreated
            .Where(pair => pair.Value.OwnerObjectId == ownerObjectId && pair.Value.TimelineId == timelineId)
            .Select(pair => pair.Key)
            .ToList();
        if (_mySlavesByTimelineId.TryGetValue(timelineId, out var mine) && !objectIds.Contains(mine.ObjectId))
            objectIds.Add(mine.ObjectId);
        foreach (var objectId in objectIds)
            RemoveObject(objectId);
    }

    private void RefreshViews(CombatState combatState)
    {
        foreach (var (objectId, unit) in _unitSnapshots)
        {
            combatState.TryGetUnit(objectId, out var combat);
            _units[objectId] = new MateSlaveUnitView(
                unit, combat,
                _slaveCreated.GetValueOrDefault(objectId),
                _slaveStates.GetValueOrDefault(objectId),
                _mySlavesByObjectId.GetValueOrDefault(objectId),
                _mateStates.GetValueOrDefault(objectId),
                unit.TimelineId is { } timelineId ? _matesByTimelineId.GetValueOrDefault(timelineId) : null);
        }
    }

    private static IReadOnlyDictionary<TKey, TValue> Copy<TKey, TValue>(Dictionary<TKey, TValue> source)
        where TKey : notnull => new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(source));

    private static IReadOnlyDictionary<TKey, TValue> Empty<TKey, TValue>() where TKey : notnull =>
        new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>());

    private static IReadOnlyDictionary<TKey, TValue> With<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> source, TKey key, TValue value) where TKey : notnull
    {
        var copy = new Dictionary<TKey, TValue>();
        foreach (var pair in source)
            copy[pair.Key] = pair.Value;
        copy[key] = value;
        return new ReadOnlyDictionary<TKey, TValue>(copy);
    }

    private static void ApplyEquipment(Dictionary<short, Dictionary<int, ItemSnapshot>> all, short timelineId,
        IReadOnlyList<VehicleEquipmentChangeEntry> entries, bool success)
    {
        if (!success) return;
        if (!all.TryGetValue(timelineId, out var slots))
            all[timelineId] = slots = [];
        foreach (var entry in entries)
        {
            // The packet echoes pre-move contents. After the swap, each location contains the item
            // originally carried by the other location.
            if (entry.FirstSlotType != (sbyte)InventorySlotType.Inventory)
                SetEquipment(slots, entry.FirstSlot, entry.SecondItem);
            if (entry.SecondSlotType != (sbyte)InventorySlotType.Inventory)
                SetEquipment(slots, entry.SecondSlot, entry.FirstItem);
        }
    }

    private static void SetEquipment(Dictionary<int, ItemSnapshot> slots, int slot, ItemSnapshot? item)
    {
        if (item is null) slots.Remove(slot);
        else slots[slot] = item;
    }

    private static void RemoveEquipment(Dictionary<short, Dictionary<int, ItemSnapshot>> all,
        short timelineId, int slot)
    {
        if (all.TryGetValue(timelineId, out var slots)) slots.Remove(slot);
    }

    private static void ApplyEquipmentFlags(Dictionary<short, Dictionary<int, ItemSnapshot>> all,
        short timelineId, int slot, sbyte flags)
    {
        if (all.TryGetValue(timelineId, out var slots) && slots.TryGetValue(slot, out var item))
            slots[slot] = item with { Flags = unchecked((byte)flags) };
    }
}

/// <summary>
/// Exact C2S packet bodies recovered for slaves, mates, boarding, and movement.
/// The protocol has no <c>CSSummonMate</c> or <c>CSBoardSlave</c> opcode. Mate summoning uses the
/// normal item-skill path. Slave placement also has the distinct <c>CSSpawnSlave</c> request.
/// </summary>
public static class VehiclePacketWriters
{
    public const ushort CSSpawnSlave = 0x05B;
    public const ushort CSDespawnSlave = 0x05C;
    public const ushort CSBindSlave = 0x05E;
    public const ushort CSDiscardSlave = 0x05F;
    public const ushort CSChangeSlaveTarget = 0x060;
    public const ushort CSChangeSlaveName = 0x061;
    public const ushort CSRepairSlaveItems = 0x062;
    public const ushort CSChangeSlaveEquipment = 0x064;
    public const ushort CSBoardingTransfer = 0x09D;
    public const ushort CSMoveUnit = 0x0C8;
    public const ushort CSRemoveMate = 0x0E5;
    public const ushort CSChangeMateTarget = 0x0E6;
    public const ushort CSChangeMateName = 0x0E7;
    public const ushort CSMountMate = 0x0E8;
    public const ushort CSUnMountMate = 0x0E9;
    public const ushort CSChangeMateEquipment = 0x0EA;
    public const ushort CSChangeMateUserState = 0x0EB;
    public const ushort CSRepairPetItems = 0x0F6;

    private const byte VehicleMoveType = 2;
    private const byte ShipRequestMoveType = 5;
    private const byte HasScTypeAndPhase = 0x10;
    private const int MaximumWheelVelocities = 0x12;
    private const byte MaximumMateEquipmentChanges = 2;
    private const byte MaximumSlaveEquipmentChanges = 3;

    /// <summary>
    /// Writes CSSpawnSlave. X/Y are the caller-supplied native fixed-point values; slot type and
    /// slot occur on the wire exactly when <paramref name="itemId"/> is nonzero.
    /// </summary>
    public static CombatOutboundPacket SpawnSlave(
        uint slaveTemplateId, long positionX, long positionY, float positionZ, float yaw,
        ulong itemId, byte? slotType, byte? slot, bool hideSpawnEffect)
    {
        var hasItem = itemId != 0;
        if (hasItem != slotType.HasValue || hasItem != slot.HasValue)
            throw new ArgumentException("slotType and slot must both be supplied exactly when itemId is nonzero.");
        var w = new WireWriter().U32(slaveTemplateId).S64(positionX).S64(positionY)
            .F32(positionZ).F32(yaw).U64(itemId);
        if (hasItem)
            w.U8(slotType!.Value).U8(slot!.Value);
        return Packet(CSSpawnSlave, w.Bool(hideSpawnEffect));
    }

    public static CombatOutboundPacket DespawnSlave(uint slaveObjectId) =>
        Packet(CSDespawnSlave, new WireWriter().Bc(slaveObjectId));

    public static CombatOutboundPacket RemoveMate(short timelineId) =>
        Packet(CSRemoveMate, new WireWriter().S16(timelineId));

    public static CombatOutboundPacket MountMate(short timelineId, byte attachPoint, byte reason) =>
        Packet(CSMountMate, new WireWriter().S16(timelineId).U8(attachPoint).U8(reason));

    public static CombatOutboundPacket UnMountMate(short timelineId, byte attachPoint, byte reason) =>
        Packet(CSUnMountMate, new WireWriter().S16(timelineId).U8(attachPoint).U8(reason));

    public static CombatOutboundPacket BindSlave(short targetTimelineId, int skillType) =>
        Packet(CSBindSlave, new WireWriter().S16(targetTimelineId).S32(skillType));

    public static CombatOutboundPacket DiscardSlave(short timelineId) =>
        Packet(CSDiscardSlave, new WireWriter().S16(timelineId));

    public static CombatOutboundPacket BoardingTransfer(short timelineId, byte attachPoint) =>
        Packet(CSBoardingTransfer, new WireWriter().S16(timelineId).U8(attachPoint));

    public static CombatOutboundPacket ChangeSlaveTarget(uint targetObjectId, uint slaveObjectId) =>
        Packet(CSChangeSlaveTarget, new WireWriter().Bc(targetObjectId).Bc(slaveObjectId));

    public static CombatOutboundPacket ChangeMateTarget(short timelineId, uint targetObjectId) =>
        Packet(CSChangeMateTarget, new WireWriter().S16(timelineId).Bc(targetObjectId));

    public static CombatOutboundPacket ChangeSlaveName(short timelineId, string name) =>
        Packet(CSChangeSlaveName, WriteString(new WireWriter().S16(timelineId), name));

    public static CombatOutboundPacket ChangeMateName(short timelineId, string name) =>
        Packet(CSChangeMateName, WriteString(new WireWriter().S16(timelineId), name));

    public static CombatOutboundPacket ChangeMateUserState(short timelineId, sbyte userState) =>
        Packet(CSChangeMateUserState, new WireWriter().S16(timelineId).S8(userState));

    public static CombatOutboundPacket RepairSlaveItems(uint npcObjectId) =>
        Packet(CSRepairSlaveItems, new WireWriter().Bc(npcObjectId));

    public static CombatOutboundPacket RepairPetItems(uint npcObjectId) =>
        Packet(CSRepairPetItems, new WireWriter().Bc(npcObjectId));

    public static CombatOutboundPacket ChangeSlaveEquipment(
        ulong characterId, short timelineId, uint databaseSlaveId, bool bts,
        IReadOnlyList<VehicleEquipmentChangeEntry> entries) =>
        WriteEquipmentChange(CSChangeSlaveEquipment, characterId, timelineId, databaseSlaveId, bts,
            entries, MaximumSlaveEquipmentChanges);

    public static CombatOutboundPacket ChangeMateEquipment(
        ulong owningCharacterId, short timelineId, uint passengerCharacterId, bool bts,
        IReadOnlyList<VehicleEquipmentChangeEntry> entries) =>
        WriteEquipmentChange(CSChangeMateEquipment, owningCharacterId, timelineId, passengerCharacterId, bts,
            entries, MaximumMateEquipmentChanges);

    /// <summary>
    /// Writes CSMoveUnit type 2. Every common-header value, vehicle-body value, wheel velocity,
    /// and the packet's trailing <paramref name="extraFlags"/> byte is supplied by the caller.
    /// </summary>
    public static CombatOutboundPacket MoveVehicle(
        uint objectId,
        uint time,
        byte flags,
        uint? scType,
        byte? phase,
        Vector3 position,
        short velocityX,
        short velocityY,
        short velocityZ,
        short rotationX,
        short rotationY,
        short rotationZ,
        float angularVelocityX,
        float angularVelocityY,
        float angularVelocityZ,
        float steering,
        byte throttle,
        IReadOnlyList<float> wheelAngularVelocities,
        sbyte extraFlags)
    {
        ArgumentNullException.ThrowIfNull(wheelAngularVelocities);
        if (wheelAngularVelocities.Count > MaximumWheelVelocities)
            throw new ArgumentOutOfRangeException(nameof(wheelAngularVelocities), wheelAngularVelocities.Count,
                $"the native client allows at most {MaximumWheelVelocities} wheel velocities");

        var w = WriteMovementHeader(objectId, VehicleMoveType, time, flags, scType, phase)
            .Position(position)
            .S16(velocityX).S16(velocityY).S16(velocityZ)
            .S16(rotationX).S16(rotationY).S16(rotationZ)
            .F32(angularVelocityX).F32(angularVelocityY).F32(angularVelocityZ)
            .F32(steering).U8(throttle)
            .U8((byte)wheelAngularVelocities.Count);
        foreach (var wheelAngularVelocity in wheelAngularVelocities)
            w.F32(wheelAngularVelocity);
        w.S8(extraFlags);
        return Packet(CSMoveUnit, w);
    }

    /// <summary>
    /// Writes CSMoveUnit type 5. Throttle and steering are preserved as their exact signed wire
    /// bytes; this method does not derive one control from the other or schedule repeated sends.
    /// </summary>
    public static CombatOutboundPacket MoveShipRequest(
        uint objectId,
        uint time,
        byte flags,
        uint? scType,
        byte? phase,
        sbyte throttle,
        sbyte steering,
        sbyte extraFlags)
    {
        var w = WriteMovementHeader(objectId, ShipRequestMoveType, time, flags, scType, phase)
            .S8(throttle).S8(steering).S8(extraFlags);
        return Packet(CSMoveUnit, w);
    }

    private static WireWriter WriteMovementHeader(
        uint objectId, byte moveType, uint time, byte flags, uint? scType, byte? phase)
    {
        var hasScTypeAndPhase = (flags & HasScTypeAndPhase) != 0;
        if (hasScTypeAndPhase != scType.HasValue || hasScTypeAndPhase != phase.HasValue)
        {
            throw new ArgumentException(
                "scType and phase must both be supplied exactly when movement flag 0x10 is set.");
        }

        var w = new WireWriter().Bc(objectId).U8(moveType).U32(time).U8(flags);
        if (hasScTypeAndPhase)
            w.U32(scType!.Value).U8(phase!.Value);
        return w;
    }

    private static CombatOutboundPacket WriteEquipmentChange(
        ushort opcode, ulong characterId, short timelineId, uint secondaryId, bool bts,
        IReadOnlyList<VehicleEquipmentChangeEntry> entries, byte maximumCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > maximumCount)
            throw new ArgumentOutOfRangeException(nameof(entries), entries.Count,
                $"packet allows at most {maximumCount} equipment exchanges");
        var w = new WireWriter().U64(characterId).S16(timelineId).U32(secondaryId).Bool(bts).U8((byte)entries.Count);
        foreach (var entry in entries)
        {
            WriteItem(w, entry.FirstItem);
            WriteItem(w, entry.SecondItem);
            w.S8(entry.FirstSlotType).S8(entry.FirstSlot).S8(entry.SecondSlotType).S8(entry.SecondSlot)
                .S64(entry.ExpireTime);
        }
        return Packet(opcode, w);
    }

    private static void WriteItem(WireWriter w, ItemSnapshot? item)
    {
        if (item is null)
        {
            w.U32(0);
            return;
        }
        w.U32(item.TemplateId).U64(item.ItemId).U8(item.Grade).U8(item.Flags).S32(item.Count)
            .U8(item.DetailType).Bytes(item.Detail)
            .S64(item.CreateTime).S32(item.LifespanMinutes).U64(item.MadeUnitId).U8(item.WorldId)
            .S64(item.UnsecureTime).S64(item.UnpackTime).S64(item.ChargeUseSkillTime);
    }

    private static WireWriter WriteString(WireWriter w, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "UTF-8 string exceeds the wire's u16 length");
        return w.Str(value);
    }

    private static CombatOutboundPacket Packet(ushort opcode, WireWriter body) => new(opcode, body.ToArray());
}
