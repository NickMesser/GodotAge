#nullable enable

using System.Globalization;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Pure projection of the vehicle protocol reducer into the shapes consumed by x2ui.  It deliberately
/// has no OnlineSession dependency so protocol replay tools can use the exact same mapping as the live UI.
/// </summary>
public sealed class X2VehicleUiProjection
{
    public const int RideMateType = 1;
    public const int BattleMateType = 2;

    private readonly MateSlaveState _state;
    private readonly uint _playerUnitId;
    private readonly ulong _playerCharacterId;
    private readonly uint _mountedUnitId;
    private readonly uint _targetUnitId;
    private readonly Func<uint, bool>? _isShipTemplate;
    private readonly Func<uint, string>? _slaveKind;

    public X2VehicleUiProjection(MateSlaveState state, uint playerUnitId = 0, ulong playerCharacterId = 0,
        uint mountedUnitId = 0, uint targetUnitId = 0, Func<uint, bool>? isShipTemplate = null,
        Func<uint, string>? slaveKind = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _playerUnitId = playerUnitId;
        _playerCharacterId = playerCharacterId;
        _mountedUnitId = mountedUnitId;
        _targetUnitId = targetUnitId;
        _isShipTemplate = isShipTemplate;
        _slaveKind = slaveKind;
    }

    public IReadOnlyList<X2MateState> Mates => _state.MatesByTimelineId.Values
        // Timeline announcements are retained by the reducer. Prefer the active owned unit and expose
        // one stable Lua row per mate type when an old summon and a new summon are both present.
        .GroupBy(mate => mate.MateType)
        .OrderBy(group => group.Key)
        .Select(group => group
            .OrderByDescending(mate => HasOwnedUnit(mate.TimelineId))
            .ThenByDescending(mate => mate.TimelineId)
            .First())
        .Select(ProjectMate)
        .ToArray();

    /// <summary>Resolves both legacy playerpet and the current playerpet1/playerpet2 tokens.</summary>
    public uint? ResolveMateToken(string token)
    {
        if (!TryMateType(token, out var requestedType)) return null;
        var matches = OwnMateUnits()
            .Where(unit => requestedType == 0 || unit.MateSpawned!.MateType == requestedType)
            .OrderBy(unit => unit.MateSpawned!.MateType == RideMateType ? 0 : 1)
            .ThenBy(unit => unit.Unit.UnitId)
            .ToArray();
        return matches.Length == 0 ? null : matches[0].Unit.UnitId;
    }

    public int MateTypeByUnitId(uint unitId) => _state.Units.TryGetValue(unitId, out var unit)
        ? unit.MateSpawned?.MateType ?? 0 : 0;

    public IReadOnlyList<uint> OwnedSlaveUnitIds => _state.Units.Values
        .Where(unit => unit.Unit.Kind == UnitKind.Slave && IsOwnSlave(unit))
        .Select(unit => unit.Unit.UnitId).OrderBy(id => id).ToArray();

    public X2VehicleState? CurrentVehicle
    {
        get
        {
            if (_mountedUnitId == 0 || !_state.Units.TryGetValue(_mountedUnitId, out var unit) ||
                unit.Unit.Kind != UnitKind.Slave || !IsOwnSlave(unit))
                return null;
            var mine = unit.MySlave;
            var templateId = mine?.TemplateId ?? unit.Unit.TemplateId;
            var hp = unit.Combat is null ? mine?.Hp ?? unit.Unit.Hp : (ulong)Math.Max(0, unit.Combat.Hp);
            var maxHp = mine?.MaxHp ?? (unit.Combat?.MaxHp is { } maximum
                ? (ulong)Math.Max(0, maximum) : Math.Max(hp, unit.Unit.Hp));
            var isShip = _isShipTemplate?.Invoke(templateId) == true;
            var name = mine?.Name;
            if (string.IsNullOrWhiteSpace(name)) name = unit.Unit.Name;
            var position = _state.Position(unit.Unit.UnitId);
            return new X2VehicleState(unit.Unit.UnitId, name ?? "", isShip, 0, 0,
                hp, maxHp, TemplateId: templateId, Kind: _slaveKind?.Invoke(templateId) ?? "",
                X: position.X, Y: position.Y, Z: position.Z);
        }
    }

    public bool IsTargetMyMate => _targetUnitId != 0 && OwnMateUnits().Any(unit => unit.Unit.UnitId == _targetUnitId);

    private X2MateState ProjectMate(MateSpawnedBody mate)
    {
        var unit = OwnMateUnits().FirstOrDefault(candidate => candidate.MateSpawned!.TimelineId == mate.TimelineId);
        var skills = mate.MountSkillIds.Where(id => id > 0).Select(id =>
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["tipType"] = "skill", ["type"] = id, ["skillType"] = id,
                ["id"] = id, ["learned"] = true,
            }).ToArray();
        var state = mate.UserState switch
        {
            1 => "aggressive", 2 => "protective", 3 => "passive", 4 => "idle", _ => "",
        };
        return new X2MateState(mate.MateType,
            UnitId: unit?.Unit.UnitId.ToString(CultureInfo.InvariantCulture) ?? "",
            Exists: unit != null, IsMine: unit != null,
            Mountable: mate.MateType == RideMateType,
            Mounted: unit != null && unit.Unit.UnitId == _mountedUnitId,
            Attackable: mate.MateType == BattleMateType,
            TemplateId: unit?.Unit.TemplateId ?? 0,
            Experience: Math.Max(0, (long)mate.Experience + (unit?.Combat?.ExperienceDelta ?? 0)),
            // SCMateSpawned carries the accumulated value but no level threshold. Preserve the value
            // losslessly; a later content-backed threshold provider can fill the final bar denominator.
            ExpToNextLevel: 0,
            Skills: skills, State: state);
    }

    private bool HasOwnedUnit(short timelineId) => OwnMateUnits()
        .Any(candidate => candidate.MateSpawned!.TimelineId == timelineId);

    private IEnumerable<MateSlaveUnitView> OwnMateUnits() =>
        _state.Units.Values.Where(unit => unit.Unit.Kind == UnitKind.Mate && unit.MateSpawned != null &&
            (_playerCharacterId == 0 || unit.Unit.OwnerId == _playerCharacterId));

    private bool IsOwnSlave(MateSlaveUnitView unit) => unit.MySlave != null ||
        unit.SlaveCreated?.OwnerObjectId == _playerUnitId ||
        (_playerCharacterId != 0 && unit.SlaveState?.OwnerId == _playerCharacterId);

    private static bool TryMateType(string token, out int mateType)
    {
        mateType = 0;
        if (string.Equals(token, "playerpet", StringComparison.Ordinal)) return true;
        const string prefix = "playerpet";
        return token.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(token.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out mateType) &&
            mateType is RideMateType or BattleMateType;
    }
}
