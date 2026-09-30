#nullable enable
using System.Collections.Concurrent;
using System.Numerics;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Feeds the in-game UI from the live network session: keeps the units the client knows (player, NPCs, other players),
/// implements <see cref="IX2UnitData"/> for the X2 API families, and turns session events into UI events. Network
/// events arrive on the network thread and are applied on the UI thread in <see cref="Pump"/>.
/// </summary>
public sealed class X2WorldBridge : IX2UnitData, IDisposable
{
    private static readonly string[] RaceNames = ["none", "nuian", "fairy", "dwarf", "elf", "hariharan", "ferre", "returned", "warborn", "daru"];

    private readonly GameClient _client;
    private readonly EnteredWorldEvent _entered;
    private readonly ConcurrentQueue<GameEvent> _incoming = new();
    private readonly Dictionary<uint, X2UnitInfo> _units = [];
    private readonly Dictionary<uint, UnitSnapshot> _sessionSnapshots = [];
    private uint _target;
    private uint _targetOfTarget;

    public X2WorldBridge(GameClient client, EnteredWorldEvent entered, IX2Events events, Action<string> log)
    {
        _client = client;
        _entered = entered;
        Events = events;
        Log = log;
        if (entered.Self != null) _units[entered.UnitId] = FromSnapshot(entered.Self) with { Id = entered.UnitId, Name = entered.Name };
        else _units[entered.UnitId] = new X2UnitInfo { Id = entered.UnitId, Name = entered.Name, Type = "character" };
        client.EventRaised += OnNetworkEvent;
    }

    public IX2Events Events { get; }
    public Action<string> Log { get; }
    public GameClient Client => _client;
    /// <summary>The live reducer, installed lazily when the world viewer attaches its session.</summary>
    public Func<CombatState?>? CombatStateProvider { get; set; }
    /// <summary>Scene membership is authoritative after OnlineSession attaches. Read on the UI thread.</summary>
    public Func<IReadOnlyList<(UnitSnapshot Snapshot, Vector3 Position)>>? SessionUnitsProvider { get; set; }
    /// <summary>Authoritative active player abilities, distinct from saved skill-book sets.</summary>
    public Func<IReadOnlyList<int>>? ActiveAbilitiesProvider { get; set; }
    /// <summary>Live mate/slave token projection, installed when OnlineSession becomes available.</summary>
    public Func<X2VehicleUiProjection?>? VehicleProjectionProvider { get; set; }

    /// <summary>Raised on the UI thread for every applied network event (after the unit table is updated).</summary>
    public event Action<GameEvent>? Applied;

    private void OnNetworkEvent(GameEvent e) => _incoming.Enqueue(e);

    /// <summary>Applies queued network events; call once per frame on the UI thread.</summary>
    public void Pump()
    {
        while (_incoming.TryDequeue(out var e))
        {
            try
            {
                Apply(e);
                Applied?.Invoke(e);
            }
            catch (Exception ex)
            {
                Log($"[x2 bridge] {e.GetType().Name}: {ex.Message}");
            }
        }
        ReconcileSessionUnits();
    }

    private void ReconcileSessionUnits()
    {
        if (SessionUnitsProvider is not { } provider) return;
        var snapshots = provider();
        var seen = new HashSet<uint> { PlayerId };
        foreach (var (snapshot, position) in snapshots)
        {
            if (snapshot.UnitId == PlayerId) continue;
            seen.Add(snapshot.UnitId);
            if (_sessionSnapshots.TryGetValue(snapshot.UnitId, out var previous) && ReferenceEquals(previous, snapshot))
            {
                if (_units.TryGetValue(snapshot.UnitId, out var current) &&
                    (current.X != position.X || current.Y != position.Y || current.Z != position.Z))
                    _units[snapshot.UnitId] = current with { X = position.X, Y = position.Y, Z = position.Z };
                continue;
            }
            _sessionSnapshots[snapshot.UnitId] = snapshot;
            var info = FromSnapshot(snapshot) with { X = position.X, Y = position.Y, Z = position.Z };
            if (_units.TryGetValue(snapshot.UnitId, out var old) && previous != null &&
                old.Type == info.Type && old.TemplateId == info.TemplateId && old.CharacterId == info.CharacterId)
                info = info with { MaxHp = Math.Max(info.MaxHp, old.MaxHp), MaxMp = Math.Max(info.MaxMp, old.MaxMp) };
            var entered = !_units.ContainsKey(snapshot.UnitId);
            _units[snapshot.UnitId] = info;
            if (entered) Events.Fire("UNIT_ENTERED_SIGHT", (double)snapshot.UnitId);
        }
        foreach (var id in _units.Keys.Where(id => !seen.Contains(id)).ToArray()) RemoveUnit(id);
        foreach (var id in _sessionSnapshots.Keys.Where(id => !seen.Contains(id)).ToArray()) _sessionSnapshots.Remove(id);
    }

    private void RemoveUnit(uint id)
    {
        if (!_units.Remove(id)) return;
        Events.Fire("UNIT_LEAVED_SIGHT", (double)id);
        if (id == _target) SetTarget(0);
        if (id == _targetOfTarget) SetTargetOfTarget(0);
    }

    private void Apply(GameEvent e)
    {
        switch (e)
        {
            case UnitAppearedEvent appeared:
            {
                if (SessionUnitsProvider != null && appeared.Unit.UnitId != PlayerId) break;
                var info = FromSnapshot(appeared.Unit);
                if (_units.TryGetValue(info.Id, out var old) && old.MaxHp > info.MaxHp) info = info with { MaxHp = old.MaxHp, MaxMp = old.MaxMp };
                _units[info.Id] = info;
                Events.Fire("UNIT_ENTERED_SIGHT", (double)info.Id);
                break;
            }
            case UnitsRemovedEvent removed:
                foreach (var id in removed.UnitIds)
                {
                    if (id == PlayerId || SessionUnitsProvider != null) continue;
                    RemoveUnit(id);
                }
                break;
            case UnitPointsEvent points when _units.TryGetValue(points.UnitId, out var unit):
            {
                var updated = unit with
                {
                    Hp = points.Hp,
                    Mp = points.Mp,
                    MaxHp = Math.Max(unit.MaxHp, points.Hp),
                    MaxMp = Math.Max(unit.MaxMp, points.Mp),
                    IsDead = points.Hp <= 0 && unit.MaxHp > 0,
                };
                _units[points.UnitId] = updated;
                // The player's SCUnitDeath carries XP and durability penalties. X2CombatBinding
                // publishes that complete event; a later zero-HP points update must not replace
                // its values with a second, argument-free UNIT_DEAD event.
                if (updated.IsDead && !unit.IsDead && points.UnitId != PlayerId)
                    Events.Fire("UNIT_DEAD", points.UnitId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            }
            case ChatEvent:
                // X2UiLayer.AddChatMessage owns both native-window delivery and the CHAT_MESSAGE event so hosts using
                // that public entry point get the same single event as network-delivered lines.
                break;
            case UnitMovedEvent moved when moved.Movement.HasPosition && _units.TryGetValue(moved.Movement.UnitId, out var mover):
                _units[mover.Id] = mover with { X = moved.Movement.Position.X, Y = moved.Movement.Position.Y, Z = moved.Movement.Position.Z };
                break;
        }
    }

    private static X2UnitInfo FromSnapshot(UnitSnapshot s)
    {
        // RaceGender = 16 * gender + race (AAEmu Character.RaceGender)
        var race = s.RaceGender & 15;
        var gender = s.RaceGender >> 4;
        var isCharacter = s.Kind == UnitKind.Character;
        return new X2UnitInfo
        {
            Id = s.UnitId,
            Name = s.Name,
            Type = s.Kind switch
            {
                UnitKind.Character => "character",
                UnitKind.Npc => "npc",
                UnitKind.Mate => "mate",
                UnitKind.Slave => "slave",
                UnitKind.Housing => "housing",
                UnitKind.Transfer => "transfer",
                UnitKind.Shipyard => "shipyard",
                UnitKind.Doodad => "doodad",
                _ => "npc",
            },
            Level = s.Level,
            Hp = (long)s.Hp,
            MaxHp = (long)s.Hp,
            Mp = (long)s.Mp,
            MaxMp = (long)s.Mp,
            Race = isCharacter && race < RaceNames.Length ? RaceNames[race] : "",
            Gender = isCharacter ? (gender == 2 ? "female" : "male") : "",
            TemplateId = s.TemplateId,
            CharacterId = isCharacter ? s.DbId : 0,
            ModelId = s.ModelId,
            X = s.Position.X,
            Y = s.Position.Y,
            Z = s.Position.Z,
        } is var info && s.Kind == UnitKind.Npc && NpcTemplates.Value.TryGetValue(s.TemplateId, out var npc)
            // NPC snapshots carry no (English) name and often no level: the client takes both from the template
            ? info with
            {
                Name = string.IsNullOrEmpty(info.Name) || UiTranslator.IsForeign(info.Name) ? npc.Name : info.Name,
                Level = info.Level > 0 ? info.Level : npc.Level,
            }
            : info;
    }

    private static readonly Lazy<Dictionary<uint, (string Name, int Level)>> NpcTemplates = new(() =>
    {
        var result = new Dictionary<uint, (string, int)>();
        try
        {
            var path = Scripting.World.X2DbLists.DefaultDatabase;
            if (!System.IO.File.Exists(path)) return result;
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "select n.id, l.en_us, n.name, n.level from npcs n " +
                              "left join localized_texts l on l.tbl_name = 'npcs' and l.tbl_column_name = 'name' and l.idx = n.id";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[(uint)r.GetInt64(0)] =
                (UiTranslator.Shared.TranslateDatabaseText(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)),
                 r.IsDBNull(3) ? 0 : r.GetInt32(3));
        }
        catch (Exception) { }
        return result;
    });

    // ------------------------------------------------------------------ IX2UnitData

    public uint PlayerId => _entered.UnitId;
    public uint TargetId => _target;
    public uint TargetOfTargetId => _targetOfTarget;
    public IEnumerable<X2UnitInfo> All
    {
        get
        {
            return _units.Keys.Select(Get).Where(unit => unit != null).Select(unit => unit!);
        }
    }

    public X2UnitInfo? Get(uint unitId)
    {
        var result = _units.GetValueOrDefault(unitId);
        if (result == null) return null;
        if (CombatStateProvider?.Invoke() is not { } state || !state.TryGetUnit(unitId, out var combat))
            return result;
        var abilities = result.Abilities;
        if (unitId == PlayerId)
        {
            if (ActiveAbilitiesProvider?.Invoke() is { Count: > 0 } active) abilities = active;
            else if (state.LastAbilitiesSwapped is { } swapped && swapped.UnitId == PlayerId)
                abilities = swapped.NewAbilities.Where(id => id > 0).Select(id => (int)id).Distinct().Take(3).ToArray();
            else if (state.LastCharacterDetail is { } detail && combat!.DatabaseId != 0 && detail.CharacterId == combat.DatabaseId)
                abilities = new[] { (int)detail.Ability1, detail.Ability2, detail.Ability3 }.Where(id => id > 0).Distinct().Take(3).ToArray();
        }
        return result with
        {
            Name = combat!.Name.Length > 0 ? combat.Name : result.Name,
            Level = combat.Level,
            Hp = combat.Hp,
            Mp = combat.Mp,
            MaxHp = combat.MaxHp ?? Math.Max(result.MaxHp, combat.Hp),
            MaxMp = combat.MaxMp ?? Math.Max(result.MaxMp, combat.Mp),
            // the player's own faction arrives only in the character record (no unit faction packet for oneself);
            // X2Faction:GetSponsorFaction (expedition creation) and faction checks read it from here
            FactionId = combat.FactionId != 0 || unitId != PlayerId ? combat.FactionId
                : _client.Characters.FirstOrDefault(c => combat.DatabaseId != 0 ? c.Id == combat.DatabaseId : c.Name == combat.Name)?.FactionId ?? 0,
            FactionName = combat.FactionName,
            IsDead = combat.IsDead,
            InCombat = combat.IsInCombat,
            Abilities = abilities,
        };
    }

    public uint? Resolve(string token)
    {
        if (token.StartsWith("playerpet", StringComparison.Ordinal))
            return VehicleProjectionProvider?.Invoke()?.ResolveMateToken(token);
        switch (token)
        {
            case "player":
                return PlayerId;
            case "target":
                return _target == 0 ? null : _target;
            case "targettarget":
                return _targetOfTarget == 0 ? null : _targetOfTarget;
        }
        return uint.TryParse(token, out var id) && Get(id) != null ? id : null;
    }

    /// <summary>
    /// The UI asks to target a unit (party frame click, X2Unit:TargetUnit). With a targeting controller attached
    /// (<see cref="TargetRequested"/> subscribed) it decides; otherwise the target changes directly.
    /// </summary>
    /// <summary>
    /// The own character's position comes from the local player controller (the server does not echo our own movement,
    /// and teleports move us too): the host calls this every frame so distances, the minimap and quest directions follow.
    /// </summary>
    public void UpdatePlayerPosition(float x, float y, float z)
    {
        if (_units.TryGetValue(PlayerId, out var me) && (me.X != x || me.Y != y || me.Z != z))
            _units[PlayerId] = me with { X = x, Y = y, Z = z };
    }

    public void RequestTarget(uint unitId)
    {
        if (TargetRequested != null) TargetRequested(unitId);
        else SetTarget(unitId);
    }

    public event Action<uint>? TargetRequested;

    /// <summary>Sets the current target (from the targeting controller or a click) and tells the UI.</summary>
    public void SetTarget(uint unitId)
    {
        if (_target == unitId) return;
        _target = unitId;
        Events.Fire("TARGET_CHANGED", unitId == 0 ? null : unitId.ToString(System.Globalization.CultureInfo.InvariantCulture), EventTargetType(unitId));
        var targetTarget = unitId != 0 && CombatStateProvider?.Invoke()?.TryGetUnit(unitId, out var selected) == true
            ? selected!.TargetUnitId : 0;
        SetTargetOfTarget(targetTarget);
    }

    /// <summary>Updates the target-of-target token and event from the controller or combat reducer.</summary>
    public void SetTargetOfTarget(uint unitId)
    {
        if (_targetOfTarget == unitId) return;
        _targetOfTarget = unitId;
        Events.Fire("TARGET_TO_TARGET_CHANGED", unitId == 0 ? null : unitId.ToString(System.Globalization.CultureInfo.InvariantCulture), EventTargetType(unitId));
    }

    // Unit-frame Lua uses "npc" for every targetable unit, including other player characters.
    private string? EventTargetType(uint unitId) => unitId != 0 && Get(unitId) != null ? "npc" : null;

    /// <summary>Updates the own character's maximum points (from stat packets), which the spawn packet does not carry.</summary>
    public void SetMaxPoints(uint unitId, long maxHp, long maxMp)
    {
        if (_units.TryGetValue(unitId, out var u)) _units[unitId] = u with { MaxHp = maxHp, MaxMp = maxMp };
    }

    public void Dispose()
    {
        CombatStateProvider = null;
        SessionUnitsProvider = null;
        ActiveAbilitiesProvider = null;
        _client.EventRaised -= OnNetworkEvent;
    }
}
