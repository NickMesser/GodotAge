#nullable enable
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>Current world and zone information for the original HUD's X2World/X2Map calls.</summary>
public sealed partial class X2ProtocolWorldData : NullWorldData, IDisposable
{
    private readonly GameData? _data;
    private readonly SlaveModelResolver? _slaveModels;
    private readonly IX2Events _events;
    private readonly Func<ClientActions?>? _actions;
    private readonly Dictionary<uint, (bool IsShip, string Kind)> _slaveClassifications = [];
    private int _zoneId;
    private OnlineSession? _session;
    private readonly string _gameDatabase;

    public X2ProtocolWorldData(string gameDatabase, int zoneId, IX2Events events,
        Func<ClientActions?>? actions = null)
    {
        _zoneId = zoneId;
        _gameDatabase = gameDatabase;
        _events = events;
        _actions = actions;
        if (File.Exists(gameDatabase))
        {
            _data = new GameData(gameDatabase);
            _slaveModels = new SlaveModelResolver(gameDatabase);
        }
    }

    public override X2WorldState World
    {
        get
        {
            if (_zoneId <= 0 || _data?.GetZone(_zoneId) is not { } zone)
                return new X2WorldState { ZoneId = _zoneId };
            // SCCharacterDetail and EnteredWorld carry zone_key, rather than zones.id.
            var name = string.IsNullOrWhiteSpace(zone.DisplayText) ? zone.Name : zone.DisplayText;
            var faction = zone.FactionId;
            return new X2WorldState
            {
                ZoneId = _zoneId, ZoneGroupId = zone.GroupId ?? 0, ZoneName = name,
                ZoneFaction = faction?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                Zones = [new X2ZoneInfo(_zoneId, name,
                    IsNuiaProtectedZone: faction == 148,
                    IsHariharaProtectedZone: faction == 149)]
            };
        }
    }

    public override X2VehicleState? CurrentVehicle
    {
        get
        {
            if (_session is null) return null;
            var vehicle = new X2VehicleUiProjection(_session.MateSlaveState, _session.Entered.UnitId,
                _session.Entered.CharacterId, _session.PlayerAttachedUnitId, 0, IsShip, SlaveKind).CurrentVehicle;
            if (vehicle is null) return null;
            var template = _session.VehicleTemplateOf(vehicle.UnitId);
            return vehicle with
            {
                // GetSiegeWeaponSpeed read 3.63 just after a 4.0 m/s run in the real client: metres per second
                Speed = _session.VehicleSpeed(vehicle.UnitId),
                TurnSpeed = _session.VehicleTurnRate(vehicle.UnitId),
                HasVehicleInfo = template?.Land?.WheeledSimulation == true,
                CustomizingType = template?.Customizable == true ? 1 : 0,
            };
        }
    }

    public override X2ApiFamilySnapshot SiegeWeapon
    {
        get
        {
            var results = new Dictionary<string, X2ApiResult>(StringComparer.Ordinal)
            {
                ["GetSlaveEquipSlotUIInfo"] = X2ApiResult.Scalar(null),
                ["CanRepairEquipSlot"] = X2ApiResult.Scalar(false),
                ["CanSlaveEquipPickedItem"] = X2ApiResult.Scalar(false),
                ["CanExchangeSlaveEquipment"] = X2ApiResult.Scalar(false),
            };
            if (CurrentSlave() is not { } slave || slave.MySlave is not { } mine)
                return new X2ApiFamilySnapshot(results);
            var equipment = _session!.MateSlaveState.SlaveEquipment(mine.TimelineId);
            for (var slot = 0; slot <= 32; slot++)
            {
                var item = equipment.GetValueOrDefault(slot);
                results[$"GetSlaveEquipSlotInfo|{slot}"] = item is null
                    ? X2ApiResult.Multi(false, null)
                    : X2ApiResult.Multi(true, EquipmentItem(item, slot));
            }
            return new X2ApiFamilySnapshot(results);
        }
    }

    private bool IsShip(uint templateId)
    {
        if (templateId == 0 || _slaveModels == null) return false;
        if (_slaveClassifications.TryGetValue(templateId, out var cached)) return cached.IsShip;
        try { return Classify(templateId).IsShip; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    private string SlaveKind(uint templateId)
    {
        if (templateId == 0 || _slaveModels == null) return "";
        if (_slaveClassifications.TryGetValue(templateId, out var cached)) return cached.Kind;
        try { return Classify(templateId).Kind; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return ""; }
    }

    private (bool IsShip, string Kind) Classify(uint templateId)
    {
        var value = (_slaveModels!.Resolve(templateId).ModelSubType == "ShipModel",
            _slaveModels.ResolveKindLocalizationKey(templateId));
        _slaveClassifications[templateId] = value;
        return value;
    }

    private MateSlaveUnitView? CurrentSlave() => _session?.MateSlaveState.Units
        .GetValueOrDefault(_session.PlayerAttachedUnitId) is { Unit.Kind: UnitKind.Slave } slave
        ? slave : null;

    public override X2WorldCommandResult Execute(X2WorldCommand command)
    {
        if (command.Table == "X2House")
            return ExecuteHouse(command);
        if (command.Table != "X2SiegeWeapon" || command.Method != "SetSiegeWeaponName" ||
            CurrentSlave() is not { MySlave: { } mine } || _actions?.Invoke() is not { } actions ||
            command.Arguments.FirstOrDefault()?.ToString() is not { Length: > 0 } name)
            return base.Execute(command);
        actions.ChangeSlaveName(mine.TimelineId, name);
        return new X2WorldCommandResult(Accepted: true);
    }

    private static IReadOnlyDictionary<string, object?> EquipmentItem(ItemSnapshot item, int slot) =>
        new Dictionary<string, object?>
        {
            ["id"] = (double)item.ItemId,
            ["itemType"] = (double)item.TemplateId,
            ["itemGrade"] = (double)item.Grade,
            ["grade"] = (double)item.Grade,
            ["stack"] = (double)item.Count,
            ["stackSize"] = (double)item.Count,
            ["equipSlot"] = (double)slot,
            ["flags"] = (double)item.Flags,
            ["name"] = $"Item {item.TemplateId}",
            ["icon"] = "",
            ["iconPath"] = "",
        };

    public void Attach(OnlineSession session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session != null)
        {
            _session.GameEventApplied -= OnGameEvent;
            DetachHousing(_session);
        }
        _session = session;
        session.GameEventApplied += OnGameEvent;
        AttachHousing(session);
        if (session.CombatState.LastCharacterDetail is { } detail && detail.ZoneId > 0)
            ChangeZone(detail.ZoneId);
    }

    private void OnGameEvent(GameEvent gameEvent)
    {
        if (gameEvent is CharacterDetailEvent { ZoneId: > 0 } detail &&
            detail.CharacterId == _session?.Entered.CharacterId)
            ChangeZone(detail.ZoneId);
    }

    private int _zoneGroup;

    private void ChangeZone(int zoneId)
    {
        if (_zoneId == zoneId) return;
        var initial = _zoneId == 0;
        if (initial) _zoneGroup = _data?.GetZone(zoneId)?.GroupId ?? zoneId;
        _zoneId = zoneId;
        // The zone at world entry is announced by LEFT_LOADING once the loading screen closes
        // (center_message_manager.lua shows the zone banner there). Firing ENTER_ANOTHER_ZONEGROUP for it
        // during loading showed the banner behind the loading screen and recorded the zone as announced,
        // so LEFT_LOADING skipped it and the banner was never seen.
        // the event is per zone group (zones.group_id): 142 -> 179 stays inside Solzreed (group 5)
        var group = _data?.GetZone(zoneId)?.GroupId ?? zoneId;
        if (!initial && group != _zoneGroup) _events.Fire(X2WorldEvents.ENTER_ANOTHER_ZONEGROUP, (double)group);
        _zoneGroup = group;
    }

    public void Dispose()
    {
        if (_session != null)
        {
            _session.GameEventApplied -= OnGameEvent;
            DetachHousing(_session);
        }
        _session = null;
        _data?.Dispose();
    }
}
