#nullable enable
using System.Diagnostics;
using System.Globalization;
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Data;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// Adapts the authoritative combat reducer to the stateful X2 unit, ability, skill and action APIs.
/// The object is installed before an <see cref="OnlineSession"/> exists and attaches to one later.
/// </summary>
public sealed class X2CombatBinding : AAEmu.GodotViewer.Ui.X2.Scripting.Api.NullUnitData, IDisposable
{
    private long _lastSkillPoints;
    private bool _skillPointsKnown;

    private sealed record TimerAnchor(int TotalMs, int RemainingMs, long At)
    {
        public double Remaining => Math.Max(0, RemainingMs - Stopwatch.GetElapsedTime(At).TotalMilliseconds);
    }

    private static readonly string[] AbilityNames =
        ["general", "fight", "illusion", "adamant", "will", "death", "wild", "magic",
         "vocation", "romance", "love", "hatred", "assassin", "madness", "pleasure"];

    private readonly X2WorldBridge _world;
    private readonly IX2Events _events;
    private readonly GameData? _data;
    private readonly ClientCharacterStatCalculator? _characterStats;
    private readonly Dictionary<(uint Unit, uint Index), TimerAnchor> _buffTimers = [];
    private readonly Dictionary<(uint Unit, uint Index), int> _buffKinds = [];
    private readonly Dictionary<ushort, (uint Caster, long At)> _casts = [];
    private readonly Dictionary<(CooldownKind Kind, int Id), TimerAnchor> _cooldowns = [];
    private readonly X2ActionSlots _actionSlots = new();
    private readonly List<uint> _activeSkills = [];
    private int[] _activeAbilities = [];
    private readonly List<WeakReference<SlotWidget>> _slots = [];
    private readonly List<WeakReference<SlotWidget>> _abilitySlots = [];
    private readonly List<WeakReference<SlotWidget>> _petSlots = [];
    private readonly List<WeakReference<SlotWidget>> _modeSlots = [];
    private string _modeActionsKey = "";

    /// <summary>ui_texts lookup (category, key) supplied by the UI layer, for native slot tooltips.</summary>
    public Func<int, string, string?>? UiText { get; set; }

    private readonly Dictionary<SlotWidget, X2ActionSlots.Entry> _slotBindingEntries = [];
    private TimerAnchor? _globalCooldown;
    private OnlineSession? _session;
    private TargetingController? _targeting;
    private ClientActions? _actions;
    private UnitDeathEvent? _playerDeath;
    private long _playerDeathAt;
    private int _actionBarPage = 1;
    private int VisibleToWire(int slot) => slot is >= 1 and <= 12 && _actionBarPage > 1
        ? slot + 72 + (_actionBarPage - 2) * 12 : slot;

    public X2CombatBinding(X2WorldBridge world, IX2Events events, string gameDatabase)
    {
        _world = world;
        _events = events;
        SkillDatabasePath = gameDatabase;
        if (File.Exists(gameDatabase))
        {
            _data = new GameData(gameDatabase);
            _characterStats = new ClientCharacterStatCalculator(gameDatabase);
        }
        _world.TargetRequested += RequestTarget;
        _actionSlots.Changed += ActionChanged;
    }

    private CombatState? State => _session?.CombatState;
    /// <summary>Diagnostics: the session and bridge this binding feeds.</summary>
    internal OnlineSession? AttachedSession => _session;
    internal X2WorldBridge WorldBridge => _world;

    public void Attach(OnlineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (ReferenceEquals(_session, session)) return;
        Detach();
        _session = session;
        _actionSlots.Attach(session);
        _actions = _data == null ? null : new ClientActions(session.Client, _data);
        _world.CombatStateProvider = () => _session?.CombatState;
        _world.SessionUnitsProvider = () => _session?.VisibleUnits ?? [];
        _world.ActiveAbilitiesProvider = () => _activeAbilities;
        _world.VehicleProjectionProvider = () => _session is null ? null : new X2VehicleUiProjection(
            _session.MateSlaveState, _world.PlayerId, _session.Entered.CharacterId,
            _session.PlayerAttachedUnitId, _world.TargetId);
        session.GameEventApplied += OnGameEvent;
        session.VehicleUiStateChanged += OnVehicleUiStateChanged;
        _targeting = session.Targeting;
        if (_targeting != null)
        {
            _targeting.TargetChanged += OnTargetChanged;
            _targeting.TargetOfTargetChanged += OnTargetOfTargetChanged;
            OnTargetChanged(_targeting.Target);
            OnTargetOfTargetChanged(_targeting.TargetOfTarget);
        }
        SeedTimers();
        RefreshActionSlots();
        RefreshAbilitySlots();
        RefreshPetSlots();
    }

    private void Detach()
    {
        if (_session != null)
        {
            _session.GameEventApplied -= OnGameEvent;
            _session.VehicleUiStateChanged -= OnVehicleUiStateChanged;
        }
        if (_targeting != null)
        {
            _targeting.TargetChanged -= OnTargetChanged;
            _targeting.TargetOfTargetChanged -= OnTargetOfTargetChanged;
        }
        _targeting = null;
        _actionSlots.Detach();
        _actions = null;
        _session = null;
        _playerDeath = null;
        _playerDeathAt = 0;
        _world.CombatStateProvider = null;
        _world.SessionUnitsProvider = null;
        _world.ActiveAbilitiesProvider = null;
        _world.VehicleProjectionProvider = null;
        _buffTimers.Clear();
        _buffKinds.Clear();
        _casts.Clear();
        _cooldowns.Clear();
        _globalCooldown = null;
        _activeSkills.Clear();
        _activeAbilities = [];
        _slotBindingEntries.Clear();
        _abilitySlots.Clear();
        _petSlots.Clear();
        _actionBarPage = 1;
    }

    private void SeedTimers()
    {
        if (State is not { } state) return;
        var now = Stopwatch.GetTimestamp();
        foreach (var unit in state.Units.Values)
        {
            foreach (var buff in unit.Buffs.Values)
            {
                _buffTimers[(unit.UnitId, buff.Index)] = new TimerAnchor(buff.DurationMs, buff.RemainingMs, now);
                _buffKinds[(unit.UnitId, buff.Index)] = _data?.GetBuff(buff.BuffId)?.KindId ?? 1;
            }
            if (unit.CurrentCast is { } cast)
                _casts[cast.TimelineId] = (unit.UnitId, now);
        }
        foreach (var (key, cooldown) in state.Cooldowns)
            _cooldowns[key] = new TimerAnchor(cooldown.DurationMs, cooldown.RemainingMs, now);
        if (state.TryGetUnit(_world.PlayerId, out var player) && player!.Skills.Count > 0)
            _activeSkills.AddRange(player.Skills);
        if (state.LastCharacterDetail is { } detail && IsPlayerDetail(detail))
            _activeAbilities = AbilityIds(detail.Ability1, detail.Ability2, detail.Ability3);
        if (state.LastAbilitiesSwapped is { } swapped && swapped.UnitId == _world.PlayerId)
            _activeAbilities = swapped.NewAbilities.Where(id => id > 0).Select(id => (int)id).Distinct().Take(3).ToArray();
    }

    private void RequestTarget(uint unitId)
    {
        if (_targeting == null) return;
        if (unitId == 0) _targeting.Clear();
        else _targeting.Select(unitId);
    }

    private void OnTargetChanged(ITargetable? target) => _world.SetTarget(target?.Id ?? 0);
    private void OnTargetOfTargetChanged(ITargetable? target) => _world.SetTargetOfTarget(target?.Id ?? 0);

    private uint? Resolve(string token) => _world.Resolve(token);
    private CombatUnitState? Unit(string token) => Resolve(token) is { } id && State?.TryGetUnit(id, out var unit) == true ? unit : null;

    public override IReadOnlyList<X2UnitAura> GetUnitAuras(string token, string kind)
    {
        if (Unit(token) is not { } unit) return [];
        var result = new List<X2UnitAura>();
        foreach (var buff in unit.Buffs.Values.OrderBy(b => b.Index))
        {
            var definition = _data?.GetBuff(buff.BuffId);
            var buffKind = definition?.KindId ?? _buffKinds.GetValueOrDefault((unit.UnitId, buff.Index), 1);
            if (!MatchesAuraKind(buffKind, kind)) continue;
            var remaining = _buffTimers.TryGetValue((unit.UnitId, buff.Index), out var timer)
                ? timer.Remaining : buff.RemainingMs;
            var tooltip = definition is null ? $"Effect {buff.BuffId}" :
                string.IsNullOrWhiteSpace(definition.Description) ? definition.Name : $"{definition.Name}\n{definition.Description}";
            result.Add(new X2UnitAura(checked((int)buff.BuffId), definition?.IconPath ?? "", tooltip,
                remaining, checked((int)Math.Max(1, buff.Stacks))));
        }
        return result;
    }

    private static bool MatchesAuraKind(int value, string kind) => kind switch
    {
        "buff" => value == 1,
        "debuff" or "raid" => value == 2,
        "hidden" => value == 3,
        _ => false,
    };

    public override X2UnitCast? GetUnitCast(string token)
    {
        if (Unit(token)?.CurrentCast is not { } cast) return null;
        var name = _data?.GetSkill(cast.SkillId)?.Name ?? $"Skill {cast.SkillId}";
        var elapsed = _casts.TryGetValue(cast.TimelineId, out var anchor)
            ? Stopwatch.GetElapsedTime(anchor.At).TotalMilliseconds : 0;
        return new X2UnitCast(name, cast.CastTimeMs, Math.Min(cast.CastTimeMs, elapsed), true, false);
    }

    public override X2UnitDistance? GetUnitDistance(string token)
    {
        if (_session == null) return base.GetUnitDistance(token);
        if (Resolve(token) is not { } id || _world.Get(id) is not { } unit || _world.Get(_world.PlayerId) is not { } player)
            return null;
        var dx = unit.X - player.X;
        var dy = unit.Y - player.Y;
        var dz = unit.Z - player.Z;
        return new X2UnitDistance(Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }

    public override IReadOnlyDictionary<string, double> GetUnitStatistics(string token, bool modifiers)
    {
        if (_session == null) return base.GetUnitStatistics(token, modifiers);
        if (Resolve(token) != _world.PlayerId) return new Dictionary<string, double>();
        // The character Lua script indexes these keys directly, including keys for attributes
        // that SCCharSubStats does not carry. Keep those at zero until formulas or a matching packet exist.
        var result = CharacterStatisticKeys.ToDictionary(key => key, _ => 0d);
        if (modifiers) return result;
        if (State?.TryGetUnit(_world.PlayerId, out var player) == true && player != null)
        {
            var unit = _world.Get(_world.PlayerId);
            var equipment = _session.InventoryState.Items
                .Where(pair => pair.Key.SlotType == (byte)InventorySlotType.Equipment)
                .Where(pair => pair.Value.TemplateId != 0)
                .Select(pair => new ClientEquippedItem(checked((uint)pair.Value.TemplateId),
                    pair.Value.Grade, pair.Key.Slot)).ToArray();
            var buffs = player.Buffs.Values.Select(buff => (buff.BuffId, buff.Stacks))
                .Concat(player.PassiveBuffs.Select(id => (id, 1u))).ToArray();
            var race = unit?.Race switch
            {
                "nuian" => (byte)1, "fairy" => (byte)2, "dwarf" => (byte)3, "elf" => (byte)4,
                "hariharan" => (byte)5, "ferre" => (byte)6, "returned" => (byte)7, "warborn" => (byte)8,
                "daru" => (byte)9, _ => (byte)0,
            };
            var gender = unit?.Gender == "female" ? (byte)1 : (byte)0;
            foreach (var pair in _characterStats?.Calculate(player.Level, 0, race, gender,
                         _activeAbilities, equipment, buffs) ?? new Dictionary<string, double>())
                if (result.ContainsKey(pair.Key)) result[pair.Key] = pair.Value;
        }
        if (State?.LastCharacterSubStats is { } stats)
        {
            double V(CharacterStatField field) => (int)field < stats.Values.Count ? stats.Values[(int)field] : 0;
            result["max_health"] = stats.MaxHp;
            result["max_mana"] = stats.MaxMp;
            foreach (var (key, field) in CharacterStatisticFields)
                result[key] = V(field);
        }
        // percent of the base run speed (buffs move it); 100 while the calculator has nothing
        if (result["move_speed_rate"] == 0) result["move_speed_rate"] = 100;
        return result;
    }

    private static readonly string[] CharacterStatisticKeys =
        ("max_health max_mana armor armor_percentage attack_anim_speed attack_anim_speed_mul " +
         "backattack_melee_damage_mul backattack_ranged_damage_mul backattack_spell_damage_mul " +
         "battle_resist battle_resist_rate block_rate bulls_eye bulls_eye_rate casting_time " +
         "detect_stealth_range detect_stealth_range_mul dex dodge_rate drop_rate_mul exp_mul " +
         "flexibility flexibility_bonus flexibility_ratio gear_score heal_critical_bonus " +
         "heal_critical_rate heal_damage_mul heal_damage_mul_anti_npc heal_dps heal_mul " +
         "heal_mul_only_heal health_regen ignore_armor ignore_shield_chance incoming_damage_mul_anti_npc " +
         "incoming_heal_mul incoming_melee_damage_add_anti_npc incoming_melee_damage_mul " +
         "incoming_melee_damage_val incoming_ranged_damage_add_anti_npc incoming_ranged_damage_mul " +
         "incoming_ranged_damage_val incoming_spell_damage_add_anti_npc incoming_spell_damage_mul " +
         "incoming_spell_damage_val int loot_gold_mul magic_penetration magic_resist " +
         "magic_resist_percentage mana_regen melee_critical_bonus melee_critical_rate " +
         "melee_damage_mul melee_damage_mul_anti_npc melee_damage_mul_anti_pc melee_dps " +
         "melee_max_dps melee_min_dps melee_parry_rate melee_success_rate move_speed " +
         "move_speed_rate persistent_health_regen persistent_mana_regen ranged_critical_bonus " +
         "ranged_critical_rate ranged_damage_mul ranged_damage_mul_anti_npc ranged_damage_mul_anti_pc " +
         "ranged_dps ranged_max_dps ranged_min_dps ranged_speed ranged_success_rate " +
         "spell_critical_bonus spell_critical_rate spell_damage_mul spell_damage_mul_anti_npc " +
         "spell_damage_mul_anti_pc spell_dps spell_success_rate spi sta str " +
         "casting_time_mul global_cooldown_mul ignore_shield_bonus ignore_shield_bonus_mul " +
         "magic_effect_resist_percentage mainhand_melee_speed offhand_melee_speed ranged_parry_rate")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static readonly IReadOnlyDictionary<string, CharacterStatField> CharacterStatisticFields =
        new Dictionary<string, CharacterStatField>
        {
            ["melee_dps"] = CharacterStatField.MeleeDps,
            ["ranged_dps"] = CharacterStatField.RangedDps,
            ["spell_dps"] = CharacterStatField.SpellDps,
            ["heal_dps"] = CharacterStatField.HealDps,
            ["armor"] = CharacterStatField.Armor,
            ["armor_percentage"] = CharacterStatField.ArmorPercentage,
            ["magic_resist"] = CharacterStatField.MagicResist,
            ["magic_resist_percentage"] = CharacterStatField.MagicResistPercentage,
            ["move_speed"] = CharacterStatField.MoveSpeed,
            ["casting_time"] = CharacterStatField.CastingTime,
            ["attack_anim_speed"] = CharacterStatField.AttackAnimationSpeed,
            ["attack_anim_speed_mul"] = CharacterStatField.AttackAnimationSpeed,
            ["melee_success_rate"] = CharacterStatField.MeleeSuccessRate,
            ["melee_critical_rate"] = CharacterStatField.MeleeCriticalRate,
            ["ranged_speed"] = CharacterStatField.RangedSpeed,
            ["ranged_success_rate"] = CharacterStatField.RangedSuccessRate,
            ["ranged_critical_rate"] = CharacterStatField.RangedCriticalRate,
            ["bulls_eye"] = CharacterStatField.BullsEye,
            ["spell_success_rate"] = CharacterStatField.SpellSuccessRate,
            ["spell_critical_rate"] = CharacterStatField.SpellCriticalRate,
            ["heal_critical_rate"] = CharacterStatField.HealCriticalRate,
            ["melee_parry_rate"] = CharacterStatField.MeleeParryRate,
            ["block_rate"] = CharacterStatField.BlockRate,
            ["dodge_rate"] = CharacterStatField.DodgeRate,
            ["flexibility"] = CharacterStatField.Flexibility,
            ["battle_resist"] = CharacterStatField.BattleResist,
            ["health_regen"] = CharacterStatField.HealthRegen,
            ["persistent_health_regen"] = CharacterStatField.PersistentHealthRegen,
            ["mana_regen"] = CharacterStatField.ManaRegen,
            ["persistent_mana_regen"] = CharacterStatField.PersistentManaRegen,
            ["str"] = CharacterStatField.Strength,
            ["dex"] = CharacterStatField.Dexterity,
            ["sta"] = CharacterStatField.Stamina,
            ["int"] = CharacterStatField.Intelligence,
            ["spi"] = CharacterStatField.Spirit,
            ["melee_min_dps"] = CharacterStatField.MeleeMinDps,
            ["melee_max_dps"] = CharacterStatField.MeleeMaxDps,
            ["ranged_min_dps"] = CharacterStatField.RangedMinDps,
            ["ranged_max_dps"] = CharacterStatField.RangedMaxDps,
            ["gear_score"] = CharacterStatField.GearScore,
        };

    public override double GetUnitNumber(string token, string key)
    {
        if (_session == null) return base.GetUnitNumber(token, key);
        if (key == "breath" && Resolve(token) == _world.PlayerId) return (State?.BreathRemainingMs ?? 0) / 1000.0;
        if (key == "gear_score") return GetUnitStatistics(token, false).GetValueOrDefault("gear_score");
        // shortcut skills are the special skill set some zones grant (client-driven dungeons, mounts), not the skill book
        if (key == "shortcut_skill_count") return 0;
        // X2Unit:GetModeActionsCount: the vehicle mode bar (mount skills + dismount + slave info) while driving
        if (key == "mode_actions_count" && Resolve(token) == _world.PlayerId) return _session.VehicleModeActions().Count;
        return 0;
    }

    public override string? GetUnitText(string token, string key)
    {
        if (_session == null) return base.GetUnitText(token, key);
        var unit = Unit(token);
        var factionId = unit?.FactionId ?? 0;
        if (factionId == 0 && Resolve(token) == _world.PlayerId)
            factionId = _world.Client.Characters.FirstOrDefault(c => c.Id == _session.Entered.CharacterId)?.FactionId ?? 0;
        return key switch
        {
            "class" => Resolve(token) is { } id && _world.Get(id)?.Abilities is { Count: > 0 } abilities
                ? string.Join(" / ", abilities.Select(a => a < AbilityNames.Length ? AbilityNames[a] : a.ToString(CultureInfo.InvariantCulture)))
                : null,
            "faction_name" => factionId > 0
                ? _data?.GetText("system_factions", "name", factionId) ?? unit?.FactionName
                : unit?.FactionName,
            "combat_relationship" => GetUnitFlag(token, "hostile") ? "hostile" : "friendly",
            "mate_type" => Resolve(token) is { } mateId
                ? new X2VehicleUiProjection(_session.MateSlaveState, _world.PlayerId,
                    _session.Entered.CharacterId, _session.PlayerAttachedUnitId, _world.TargetId).MateTypeByUnitId(mateId)
                    .ToString(CultureInfo.InvariantCulture)
                : "0",
            _ => null,
        };
    }

    public override bool GetUnitFlag(string token, string key)
    {
        if (_session == null) return base.GetUnitFlag(token, key);
        if (Unit(token) is not { } unit) return key == "offline";
        if (key == "force_attack") return unit.IsForceAttacking || unit.IsForcedAttack;
        if (key is "hostile" or "aggressive_hostile")
        {
            if (unit.UnitId == _world.PlayerId) return false;
            var player = State?.TryGetUnit(_world.PlayerId, out var p) == true ? p : null;
            var from = (int)(player?.FactionId is > 0 ? player.FactionId : player?.MotherFactionId ?? 0);
            var to = (int)(unit.FactionId is > 0 ? unit.FactionId : unit.MotherFactionId);
            return from > 0 && to > 0 && _data?.GetFactionRelation(from, to) == 1;
        }
        return false;
    }

    public override X2PlayerState Player
    {
        get
        {
            if (_session is null) return base.Player;
            var player = State?.TryGetUnit(_world.PlayerId, out var value) == true ? value : null;
            var inventory = _session.InventoryState;
            var level = Math.Max(1, player is not null ? (int)player.Level
                : State?.LastCharacterDetail is { } detail ? detail.Level : 1);
            var levelFloor = _data?.GetLevelExperience(level) ?? 0;
            var nextFloor = _data?.GetLevelExperience(level + 1) ?? 0;
            var levelSpan = Math.Max(0, nextFloor - levelFloor);
            var current = State?.PlayerExperience is { } totalExperience
                ? Math.Clamp(totalExperience - levelFloor, 0, levelSpan)
                : 0;
            var results = base.Player.Results.ToDictionary(pair => pair.Key, pair => pair.Value);
            var vehicles = new X2VehicleUiProjection(_session.MateSlaveState, _world.PlayerId,
                _session.Entered.CharacterId, _session.PlayerAttachedUnitId, _world.TargetId);
            results["HasSlaveUnit"] = vehicles.OwnedSlaveUnitIds.Count > 0;
            results["IsBoundSlave"] = vehicles.CurrentVehicle != null;
            var appellations = State?.Appellations ?? [];
            results["OwnedAppellationIds"] = appellations;
            results["GetAppellationCount"] = (double)(appellations.Count + 1); // includes the empty title slot
            var appellationId = State?.ShowingAppellationId ?? 0;
            if (appellationId > 0)
            {
                var name = _data?.GetText("appellations", "name", appellationId) ?? "";
                results["GetShowingAppellation"] = new object?[] { (double)appellationId, name };
            }
            if (_playerDeath is { } death)
            {
                var elapsed = Math.Max(0, Stopwatch.GetElapsedTime(_playerDeathAt).TotalMilliseconds);
                results["GetResurrectionInfo"] = new Dictionary<string, object?>
                {
                    ["canFreeRez"] = false,
                    ["canSpecialRez"] = false,
                    ["nowFreeCnt"] = 0d,
                    ["maxFreeCnt"] = 0d,
                    ["rezWaitingTime"] = (double)death.ResurrectionWaitMs,
                    ["specialRezWaitingTime"] = (double)death.SpecialResurrectionWaitMs,
                    ["autoRezWaitingTime"] = (double)death.AutoResurrectionWaitMs,
                    ["elapsed"] = elapsed,
                };
            }
            if (State?.LastRecoverableExperience is { } recoverable)
            {
                var recoverablePercent = levelSpan > 0 ? recoverable.Recoverable * 100.0 / levelSpan : 0;
                results["GetRecoverableExp"] = new object?[] { (double)recoverable.Recoverable, recoverablePercent };
            }
            return base.Player with
            {
                BreathSeconds = checked((int)((State?.BreathRemainingMs ?? 0) / 1000)),
                InCombat = player?.IsInCombat ?? false,
                Experience = current,
                ExperienceToNextLevel = levelSpan,
                ExperiencePercent = levelSpan > 0 ? current * 100.0 / levelSpan : 0,
                LaborPower = inventory.Labor,
                LocalLaborPower = inventory.LocalLabor,
                // The labor packet has no cap. Current amounts provide a safe lower bound for bars.
                MaxLaborPower = Math.Max(base.Player.MaxLaborPower, inventory.Labor),
                MaxLocalLaborPower = Math.Max(base.Player.MaxLocalLaborPower, inventory.LocalLabor),
                LoyaltyPoints = inventory.LoyaltyPoints,
                HonorPoint = checked((int)inventory.GamePoints.GetValueOrDefault(GamePointSlots.Honor)),
                VocationPoint = checked((int)inventory.GamePoints.GetValueOrDefault(GamePointSlots.Vocation)),
                Results = results,
            };
        }
    }

    public override void RequestPlayerAction(string method, params object?[] arguments)
    {
        if (_session is not null && method == "ChangeAppellation" && arguments.Length > 0)
        {
            var id = Convert.ToUInt32(arguments[0], CultureInfo.InvariantCulture);
            if (id == 0 || State?.Appellations.Contains(id) == true)
                _session.Client.SendChangeAppellation(id);
            return;
        }
        if (_session is null || !method.Equals("Resurrect", StringComparison.Ordinal)) return;
        var kind = arguments.Length == 0 ? 0 : Convert.ToInt32(arguments[0], CultureInfo.InvariantCulture);
        // RK_NORMAL=0 and RK_SPECIAL=2 both use the server's return-point path. RK_IN_PLACE=1 is
        // the sole true value read by CSResurrectCharacter.
        _session.Client.SendGame(CombatPacketWriters.Resurrect(kind == 1));
    }

    private int[] ActiveAbilities()
    {
        return _activeAbilities;
    }

    /// <summary>
    /// The character's three active abilities from the lobby record, until the server sends a character detail or an
    /// ability swap (the class name, skill window and skill tree read them).
    /// </summary>
    public void SeedActiveAbilities(IReadOnlyList<int>? abilities)
    {
        if (abilities == null || _activeAbilities.Length > 0) return;
        // enum_abilities: 1-14 and 28-29 are the combat abilities (others are placeholders or unset bytes)
        _activeAbilities = abilities.Where(id => id is >= 1 and <= 14 or 28 or 29).Distinct().Take(3).ToArray();
        if (_activeAbilities.Length > 0) _events.Fire("ABILITY_CHANGED");
    }

    private static int[] AbilityIds(byte first, byte second, byte third) =>
        new[] { (int)first, second, third }.Where(id => id > 0).Distinct().Take(3).ToArray();

    private bool IsPlayerDetail(CharacterDetailEvent detail) =>
        _session?.Entered.CharacterId == detail.CharacterId;

    private IReadOnlyList<uint> KnownSkills()
    {
        return _activeSkills;
    }

    public override X2AbilityState AbilityState
    {
        get
        {
            if (_session == null) return base.AbilityState;
            var active = ActiveAbilities();
            var skills = KnownSkills();
            var used = skills.Sum(id => Math.Max(0, GetSkill(checked((int)id))?.SkillPoints ?? 0));
            var available = checked((int)(State?.AdditionalSkillPoints ?? 0));
            var playerLevel = State?.TryGetUnit(_world.PlayerId, out var player) == true ? Math.Max(1, (int)player!.Level) : 1;
            var saved = State?.SkillBookSlots.Select(slot => new X2SavedAbilitySet(
                new[] { (int)slot.Ability1, slot.Ability2, slot.Ability3 },
                slot.Skills.Select(id => checked((int)id)).ToArray(),
                slot.PassiveBuffs.Select(id => checked((int)id)).ToArray())).ToArray() ?? [];
            return new X2AbilityState
            {
                Active = active, View = active, SavedSets = saved,
                UsableSavedSetCount = State?.UsableAbilitySetSlots ?? 1,
                CurrentSavedSetIndex = active.Length == 0 ? -1 : Array.FindIndex(saved,
                    set => set.Abilities.Where(id => id > 0).SequenceEqual(active)),
                UsedSkillPoints = used, TotalSkillPoints = used + available,
                Progress = active.ToDictionary(a => a, a => new X2AbilityProgress(Level: playerLevel,
                    TotalSkillPoints: used + available, UsedSkillPoints: used)),
            };
        }
    }

    public override X2SkillEntry? GetSkill(int skillType)
    {
        if (_session == null) return base.GetSkill(skillType);
        var skill = _data?.GetSkill(skillType);
        var staticEntry = base.GetSkill(skillType);
        return skill is null ? base.GetSkill(skillType) : new X2SkillEntry(checked((int)skill.Id), skill.Name,
            skill.Description, skill.AbilityId, skill.AbilityId >= 0 && skill.AbilityId < AbilityNames.Length ? AbilityNames[skill.AbilityId] : "",
            skill.IconPath ?? "", staticEntry?.SkillPoints ?? 0, skill.CooldownMilliseconds, skill.CastingMilliseconds);
    }

    public override double GetSkillAlertBuffRemainingMs(int buffId)
    {
        if (State is not { } state) return 0;
        foreach (var unit in state.Units.Values)
            foreach (var buff in unit.Buffs.Values)
                if (buff.BuffId == buffId && _buffTimers.TryGetValue((unit.UnitId, buff.Index), out var timer))
                    return timer.Remaining;
        return 0;
    }

    public override IReadOnlyDictionary<int, X2ActionEntry> ActionSlots
    {
        get
        {
            if (_session == null) return base.ActionSlots;
            var result = new Dictionary<int, X2ActionEntry>();
            for (var slot = 1; slot <= 72; slot++)
            {
                var action = _actionSlots.Get(VisibleToWire(slot));
                if (action.IsEmpty || action.Id == 0) continue;
                var id = action.Id.ToString(CultureInfo.InvariantCulture);
                if (action.Type is X2ActionSlots.Kind.Skill or X2ActionSlots.Kind.RidePetSpell)
                {
                    var skill = _data?.GetSkill(checked((long)action.Id));
                    var cooldown = Cooldown(CooldownKind.Skill, checked((int)action.Id));
                    result[slot] = new X2ActionEntry("skill", id, id, skill?.Description ?? skill?.Name ?? $"Skill {id}",
                        Cooldown: cooldown.Remaining, CooldownEx: cooldown.Total);
                }
                else if (action.Type is X2ActionSlots.Kind.ItemType or X2ActionSlots.Kind.ItemId)
                {
                    var item = FindItem(action);
                    var definition = _data?.GetItem(item?.TemplateId ?? (action.Type == X2ActionSlots.Kind.ItemType ? (long)action.Id : 0));
                    result[slot] = new X2ActionEntry("item", id, id, definition?.Name ?? $"Item {id}");
                }
            }
            return result;
        }
    }

    public override int ActionBarPage => _session == null ? base.ActionBarPage : _actionBarPage;
    public override int ActionLastIndex => ActionSlots.Count == 0 ? 0 : ActionSlots.Keys.Max();
    public override void ClearAction(int slot) => _actionSlots.Set(VisibleToWire(slot), default);
    public override void SetActionSpell(int slot, int spellId)
    {
        if (spellId > 0) _actionSlots.Set(VisibleToWire(slot), new(X2ActionSlots.Kind.Skill, checked((uint)spellId)));
        else ClearAction(slot);
    }
    public override void PlaceAction(int slot) { }
    public override void SetActionBarPage(int page)
    {
        if (_session == null) return;
        _actionBarPage = Math.Clamp(page, 1, 5);
        _events.Fire(X2UnitEvents.ActionBarPageChanged, (double)_actionBarPage);
        ActionChanged();
    }

    public bool UseActionSlot(int slot)
    {
        if (_session == null) return false;
        var action = _actionSlots.Get(VisibleToWire(slot));
        if (action.Type is X2ActionSlots.Kind.Skill or X2ActionSlots.Kind.RidePetSpell)
            return action.Id <= uint.MaxValue && _session.UseSkill((uint)action.Id);
        if (FindItem(action) is not { } item || _actions == null) return false;
        if (_session.TryUseSummonSlaveItem(item)) return true;
        try
        {
            _actions.UseItem(new ItemSkillCast(_session.Entered.UnitId, item.ItemId, item.TemplateId,
                0, 0, _session.Entered.UnitId));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    private ItemSnapshot? FindItem(X2ActionSlots.Entry action)
    {
        if (_session == null) return null;
        return action.Type switch
        {
            X2ActionSlots.Kind.ItemId => _session.InventoryState.Items.Values.FirstOrDefault(i => i.ItemId == action.Id),
            X2ActionSlots.Kind.ItemType => _session.InventoryState.Items.Values.FirstOrDefault(i => i.TemplateId == action.Id),
            _ => null,
        };
    }

    public bool DropSlot(SlotWidget source, SlotWidget? destination)
    {
        if (destination != null && !IsActionSlot(destination)) destination = null;
        if (IsActionSlot(source))
        {
            var from = VisibleToWire((int)source.SlotIndex);
            if (destination == null) return _actionSlots.Set(from, default);
            return _actionSlots.Swap(from, VisibleToWire((int)destination.SlotIndex));
        }
        if (destination == null) return false;
        X2ActionSlots.Entry action = default;
        if (source.SlotType == "2" && _session != null &&
            _session.InventoryState.TryGetItem(2, (int)source.SlotIndex, out var item) && item != null)
            action = new(X2ActionSlots.Kind.ItemType, item.TemplateId);
        else if (source.ContentKind is "skill" or "skillslot" &&
                 source.ContentArgs.ElementAtOrDefault(source.ContentKind == "skillslot" ? 1 : 0) is { } raw &&
                 uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out var skill))
            action = new(X2ActionSlots.Kind.Skill, skill);
        return !action.IsEmpty && _actionSlots.Set(VisibleToWire((int)destination.SlotIndex), action);
    }

    public void BindSlot(SlotWidget slot)
    {
        if (slot.SlotType is "239" or "248")
        {
            if (!_petSlots.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, slot)))
                _petSlots.Add(new WeakReference<SlotWidget>(slot));
            RefreshPetSlot(slot);
            return;
        }
        if (slot.SlotType == "246")
        {
            if (!_modeSlots.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, slot)))
                _modeSlots.Add(new WeakReference<SlotWidget>(slot));
            RefreshModeSlot(slot);
            return;
        }
        if (slot.SlotType == "243")
        {
            if (!_abilitySlots.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, slot)))
                _abilitySlots.Add(new WeakReference<SlotWidget>(slot));
            RefreshAbilitySlot(slot);
            return;
        }
        if (!IsActionSlot(slot)) return;
        if (!_slots.Any(reference => reference.TryGetTarget(out var target) && ReferenceEquals(target, slot)))
            _slots.Add(new WeakReference<SlotWidget>(slot));
        RefreshSlot(slot);
    }

    private void OnVehicleUiStateChanged(GameEvent _)
    {
        RefreshPetSlots();
        var actions = _session?.VehicleModeActions() ?? [];
        var key = string.Join(",", actions.Select(action => action.Type + action.SkillId + action.Function));
        if (key == _modeActionsKey) return;
        _modeActionsKey = key;
        for (var i = _modeSlots.Count - 1; i >= 0; i--)
        {
            if (!_modeSlots[i].TryGetTarget(out var slot) || slot.Destroyed) _modeSlots.RemoveAt(i);
            else RefreshModeSlot(slot);
        }
    }

    private const int TooltipTextCategory = 33; // TOOLTIP_TEXT
    private const string SlaveInfoIcon = "Game/ui/icon/icon_skill_info_view.dds";
    private const uint UnbindSkillIcon = 35837; // the dismount function shows the "내리기" skill's icon

    /// <summary>
    /// ISLOT_MODE_ACTION (246) slots, as the original fills them while the player drives a slave (captured tooltips):
    /// slave skills bind as "slave_skill" with a siege_weapon_skill tooltip; the dismount and slave-info entries bind as
    /// "function" with { tootipText = TOOLTIP_TEXT unbind / showSlaveInfo }.
    /// </summary>
    private void RefreshModeSlot(SlotWidget slot)
    {
        var actions = _session?.VehicleModeActions() ?? [];
        var index = checked((int)slot.SlotIndex) - 1;
        var action = index >= 0 && index < actions.Count ? actions[index] : null;
        slot.SetCount(0);
        slot.SetCooldown(0, 0);
        if (slot is not NativeSlotWidget native)
        {
            slot.SetIcon(null);
            return;
        }
        if (action == null)
        {
            slot.SetIcon(null);
            native.SetLiveBoundType(false);
            native.SetTooltip(null);
        }
        else if (action.Type == "slave_skill")
        {
            var skill = _data?.GetSkill(action.SkillId);
            slot.SetIcon(skill?.IconPath);
            native.SetLiveBoundType("slave_skill");
            native.SetTooltip(new LuaTable
            {
                ["tipType"] = "siege_weapon_skill", ["type"] = (double)action.SkillId,
                ["name"] = skill?.Name ?? $"Skill {action.SkillId}",
                ["description"] = skill?.Description ?? "", ["path"] = skill?.IconPath ?? "",
                ["skillUsable"] = 1d, ["learnLevel"] = 1d, ["show"] = false,
            });
        }
        else
        {
            slot.SetIcon(action.Function == "unbind" ? _data?.GetSkill(UnbindSkillIcon)?.IconPath : SlaveInfoIcon);
            native.SetLiveBoundType("function");
            native.SetTooltip(new LuaTable
            {
                ["tootipText"] = UiText?.Invoke(TooltipTextCategory, action.Function) ?? action.Function,
            });
        }
        native.InvokeHandler("OnContentUpdated", "action_binded", action != null);
        native.InvokeHandler("OnContentUpdated", "learned", action != null);
        native.InvokeHandler("OnContentUpdated", "can_use", action != null);
    }

    /// <summary>A mode action slot was clicked or its hotkey (mode_action_bar_button) pressed.</summary>
    public bool UseModeActionSlot(int slotIndex)
    {
        var actions = _session?.VehicleModeActions() ?? [];
        if (_session == null || slotIndex < 1 || slotIndex > actions.Count) return false;
        var action = actions[slotIndex - 1];
        switch (action.Type, action.Function)
        {
            case ("function", "unbind"):
                return _session.RequestVehicleUnmount();
            case ("function", "showSlaveInfo"):
                _events.Fire("SHOW_SLAVE_INFO"); // mode_action.lua toggles slaveInfoFrame on it
                return true;
            default:
                return _session.UseVehicleSkill(action.SkillId);
        }
    }

    private void RefreshPetSlots()
    {
        for (var i = _petSlots.Count - 1; i >= 0; i--)
        {
            if (!_petSlots[i].TryGetTarget(out var slot) || slot.Destroyed) _petSlots.RemoveAt(i);
            else RefreshPetSlot(slot);
        }
    }

    private void RefreshPetSlot(SlotWidget slot)
    {
        var mateType = slot.SlotType == "248" ? X2VehicleUiProjection.RideMateType :
            X2VehicleUiProjection.BattleMateType;
        var index = Math.Max(0, checked((int)slot.SlotIndex) - 1);
        var skillId = _session?.MateSlaveState.Units.Values
            .FirstOrDefault(unit => unit.Unit.Kind == UnitKind.Mate &&
                unit.Unit.OwnerId == _session.Entered.CharacterId && unit.MateSpawned?.MateType == mateType)
            ?.MateSpawned?.MountSkillIds.ElementAtOrDefault(index) ?? 0;
        var skill = skillId > 0 ? _data?.GetSkill(skillId) : null;
        slot.SetIcon(skill?.IconPath);
        slot.SetCount(0);
        slot.SetCooldown(0, 0);
        if (slot is not NativeSlotWidget native) return;
        if (skillId > 0) native.SetLiveBoundType("skill");
        else native.SetLiveBoundType(false);
        native.SetTooltip(skillId <= 0 ? null : new LuaTable
        {
            ["tipType"] = "skill", ["type"] = (double)skillId,
            ["name"] = skill?.Name ?? $"Skill {skillId}",
            ["description"] = skill?.Description ?? "", ["iconPath"] = skill?.IconPath ?? "",
            ["learn"] = "master", ["showReq"] = false,
        });
        native.InvokeHandler("OnContentUpdated", "action_binded", skillId > 0);
        native.InvokeHandler("OnContentUpdated", "learned", skillId > 0);
        native.InvokeHandler("OnContentUpdated", "can_use", false);
    }

    private static bool IsActionSlot(SlotWidget slot) => slot.SlotType is "254" or "action";

    private void RefreshAbilitySlots()
    {
        _abilitySlots.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in _abilitySlots)
            if (reference.TryGetTarget(out var slot)) RefreshAbilitySlot(slot);
    }

    private void RefreshAbilitySlot(SlotWidget slot)
    {
        if (_session == null || slot.SlotType != "243") return;
        var index = checked((int)slot.SlotIndex);
        var (abilityIndex, position, passive) = index switch
        {
            >= 11 and <= 25 => (0, index - 10, false),
            >= 31 and <= 45 => (1, index - 30, false),
            >= 51 and <= 65 => (2, index - 50, false),
            >= 71 and <= 79 => (0, index - 70, true),
            >= 81 and <= 89 => (1, index - 80, true),
            >= 91 and <= 99 => (2, index - 90, true),
            _ => (-1, 0, false),
        };
        var abilities = ActiveAbilities();
        var ability = abilityIndex >= 0 && abilityIndex < abilities.Length ? abilities[abilityIndex] : 0;
        var skillId = !passive ? X2UnitApi.SkillAt(ability, position) : 0;
        var buffId = passive ? X2UnitApi.PassiveBuffAt(ability, position) : 0;
        slot.SetAbilityViewContent(skillId, buffId);
        var skill = skillId > 0 ? GetSkill(skillId) : null;
        var buff = buffId > 0 ? _data?.GetBuff((uint)buffId) : null;
        slot.SetIcon(skill?.IconPath ?? buff?.IconPath);
        if (slot is NativeSlotWidget native)
        {
            native.SetLiveBoundType(skillId > 0 || buffId > 0);
            native.SetTooltip(skill?.Description ?? buff?.Description);
            native.InvokeHandler("OnContentUpdated", "learned", skillId > 0 && KnownSkills().Contains((uint)skillId));
            native.InvokeHandler("OnContentUpdated", "visibleType", skillId > 0 && KnownSkills().Contains((uint)skillId) ? 1d : 2d);
        }
    }

    private void RefreshSlot(SlotWidget slot)
    {
        if (!IsActionSlot(slot)) return;
        var index = checked((int)slot.SlotIndex);
        var binding = _actionSlots.Get(VisibleToWire(index));
        if (!ActionSlots.TryGetValue(index, out var action))
        {
            slot.SetIcon(null);
            slot.SetCount(0);
            slot.SetCooldown(0, 0);
            if (slot is NativeSlotWidget empty)
            {
                var changed = !_slotBindingEntries.TryGetValue(slot, out var oldEntry) || !oldEntry.IsEmpty;
                _slotBindingEntries[slot] = default;
                empty.SetLiveBoundType(false);
                empty.SetTooltip(null);
                if (changed) empty.InvokeHandler("OnContentUpdated", "action_binded", false);
            }
            return;
        }
        var isSkill = binding.Type is X2ActionSlots.Kind.Skill or X2ActionSlots.Kind.RidePetSpell;
        var item = isSkill ? null : FindItem(binding);
        var skill = isSkill ? _data?.GetSkill(checked((long)binding.Id)) : null;
        var definition = isSkill ? null : _data?.GetItem(item?.TemplateId ?? (binding.Type == X2ActionSlots.Kind.ItemType ? (long)binding.Id : 0));
        slot.SetIcon(isSkill ? skill?.IconPath : definition?.IconPath);
        slot.SetCount(item?.Count ?? 0);
        if (slot is NativeSlotWidget native)
        {
            var entry = isSkill ? GetSkill(checked((int)binding.Id)) : null;
            native.SetTooltip(entry is null ? definition is null ? null : new LuaTable
            {
                ["tipType"] = "item", ["type"] = (double)definition.Id, ["name"] = definition.Name,
                ["iconPath"] = definition.IconPath,
            } : new LuaTable
            {
                ["tipType"] = "skill", ["type"] = (double)entry.Id, ["name"] = entry.Name,
                ["description"] = entry.Description, ["ability"] = entry.AbilityName,
                ["abilityName"] = entry.AbilityName, ["iconPath"] = entry.IconPath,
                ["skillPoints"] = (double)entry.SkillPoints, ["skillLevel"] = 1d,
                ["cooldownTime"] = (double)entry.CooldownMs, ["castingTime"] = (double)entry.CastingMs,
                ["learn"] = "master", ["showReq"] = false,
            });
            var changed = !_slotBindingEntries.TryGetValue(slot, out var oldEntry) || oldEntry != binding;
            _slotBindingEntries[slot] = binding;
            native.SetLiveBoundType(isSkill ? "skill" : "item");
            if (changed)
            {
                native.InvokeHandler("OnContentUpdated", "action_binded", true);
                native.InvokeHandler("OnContentUpdated", "learned", true);
                native.InvokeHandler("OnContentUpdated", "can_use", true);
                native.InvokeHandler("OnContentUpdated", "can_use_level", true, 0d);
                native.InvokeHandler("OnContentUpdated", "hotkey_activated", true);
            }
            native.InvokeHandler("OnContentUpdated", "stack_count_changed", (double)(item?.Count ?? 0));
            if (isSkill) native.InvokeHandler("OnContentUpdated", action.Cooldown > 0 ? "skill_cooldown_start" : "skill_cooldown_done", action.CooldownEx);
        }
        slot.SetCooldown(action.Cooldown, action.CooldownEx);
    }

    private void RefreshActionSlots()
    {
        for (var i = _slots.Count - 1; i >= 0; i--)
        {
            if (!_slots[i].TryGetTarget(out var slot) || slot.Destroyed)
            {
                if (slot != null) _slotBindingEntries.Remove(slot);
                _slots.RemoveAt(i);
            }
            else RefreshSlot(slot);
        }
    }

    public void RefreshInventorySlots() => RefreshActionSlots();

    private void ActionChanged()
    {
        RefreshActionSlots();
        _events.Fire("ACTIONBAR_UPDATE");
        _events.Fire(X2UnitEvents.UpdateShortcutSkills);
    }

    private (double Remaining, double Total) Cooldown(CooldownKind kind, int id)
    {
        if (_cooldowns.TryGetValue((kind, id), out var timer))
        {
            if (_globalCooldown is { } global && global.Remaining > timer.Remaining)
                return (global.Remaining, global.TotalMs);
            return (timer.Remaining, timer.TotalMs);
        }
        if (_globalCooldown is { } onlyGlobal && onlyGlobal.Remaining > 0)
            return (onlyGlobal.Remaining, onlyGlobal.TotalMs);
        return (0, 0);
    }

    private int GlobalCooldownMilliseconds()
    {
        if (State?.LastCharacterSubStats is not { } stats ||
            (int)CharacterStatField.GlobalCooldown >= stats.Values.Count) return 1000;
        var value = stats.Values[(int)CharacterStatField.GlobalCooldown];
        return value <= 0 ? 1000 : checked((int)Math.Round(value <= 10 ? value * 1000 : value));
    }

    private string UnitToken(uint id)
    {
        if (id == _world.PlayerId) return "player";
        if (id == _world.TargetId) return "target";
        if (id == _world.TargetOfTargetId) return "targettarget";
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<string> CastTokens(uint id)
    {
        var tokens = new List<string>(3);
        if (id == _world.PlayerId) tokens.Add("player");
        if (id == _world.TargetId) tokens.Add("target");
        if (id == _world.TargetOfTargetId) tokens.Add("targettarget");
        if (tokens.Count == 0) tokens.Add(id.ToString(CultureInfo.InvariantCulture));
        return tokens;
    }

    private void FireCast(string eventName, uint caster)
    {
        foreach (var token in CastTokens(caster))
            _events.Fire(eventName, token);
    }

    private string AuraToken(uint id) => id == _world.PlayerId ? "character" : _world.Get(id)?.Type ?? UnitToken(id);

    private void OnGameEvent(GameEvent gameEvent)
    {
        var now = Stopwatch.GetTimestamp();
        switch (gameEvent)
        {
            case UnitPointsEvent points:
                FirePoints(points.UnitId, points.Hp, points.Mp);
                break;
            case UnitStateEvent state:
                FirePoints(state.Snapshot.Unit.UnitId, checked((int)state.Snapshot.Unit.Hp), checked((int)state.Snapshot.Unit.Mp));
                break;
            case UnitDeathEvent death:
                if (death.UnitId == _world.PlayerId)
                {
                    _playerDeath = death;
                    _playerDeathAt = now;
                }
                _events.Fire(X2UnitEvents.UnitDead, death.UnitId.ToString(CultureInfo.InvariantCulture),
                    (double)death.LostExperience, (double)death.DurabilityLossRatio);
                FireCast(X2UnitEvents.SpellcastStop, death.UnitId);
                break;
            case ResurrectionPromptEvent prompt:
            {
                var name = prompt.Source.UnitId == _world.PlayerId
                    ? null
                    : _world.Get(prompt.Source.UnitId)?.Name;
                _events.Fire("PLAYER_RESURRECTION", name);
                break;
            }
            case CharacterResurrectedEvent resurrected:
                if (resurrected.UnitId == _world.PlayerId)
                {
                    _playerDeath = null;
                    _playerDeathAt = 0;
                    _events.Fire("PLAYER_RESURRECTED");
                }
                FirePoints(resurrected.UnitId, State?.TryGetUnit(resurrected.UnitId, out var risen) == true ? risen!.Hp : 0,
                    State?.TryGetUnit(resurrected.UnitId, out var risenMana) == true ? risenMana!.Mp : 0);
                break;
            case RecoverableExperienceChangedEvent recoverable:
                _events.Fire("RECOVERABLE_EXP", recoverable.UnitId.ToString(CultureInfo.InvariantCulture),
                    (double)recoverable.Recoverable);
                break;
            case UnitLevelEvent level:
                _events.Fire(X2UnitEvents.LevelChanged, (double)level.Level, level.UnitId.ToString(CultureInfo.InvariantCulture));
                _events.Fire("UNIT_LEVEL_CHANGED", UnitToken(level.UnitId), (double)level.Level);
                break;
            case ExperienceChangedEvent experience:
                _events.Fire(X2UnitEvents.ExpChanged, experience.UnitId.ToString(CultureInfo.InvariantCulture),
                    (double)experience.Delta, experience.Delta.ToString(CultureInfo.InvariantCulture));
                break;
            case AbilityExperienceChangedEvent abilityExperience:
                _events.Fire(X2UnitEvents.AbilityExpChanged, abilityExperience.UnitId.ToString(CultureInfo.InvariantCulture));
                _events.Fire(X2UnitEvents.PlayerAbilityLevelChanged);
                break;
            case CharacterDetailEvent detail when IsPlayerDetail(detail):
                _activeAbilities = AbilityIds(detail.Ability1, detail.Ability2, detail.Ability3);
                _events.Fire(X2UnitEvents.AbilityChanged);
                RefreshAbilitySlots();
                _events.Fire(X2UnitEvents.UnitNameChanged, _world.PlayerId.ToString(CultureInfo.InvariantCulture));
                _events.Fire(X2UnitEvents.LevelChanged, (double)detail.Level, _world.PlayerId.ToString(CultureInfo.InvariantCulture));
                if (State?.TryGetUnit(_world.PlayerId, out var detailed) == true) FirePoints(_world.PlayerId, detailed!.Hp, detailed.Mp);
                break;
            case CharacterDetailEvent:
                break;
            case CharacterSubStatsEvent stats:
                _world.SetMaxPoints(_world.PlayerId, checked((long)stats.MaxHp), checked((long)stats.MaxMp));
                if (State?.TryGetUnit(_world.PlayerId, out var player) == true) FirePoints(_world.PlayerId, player!.Hp, player.Mp);
                break;
            case CharacterAppellationsEvent:
                _events.Fire("APPELLATION_GAINED");
                break;
            case CharacterAppellationGainedEvent:
                _events.Fire("APPELLATION_GAINED");
                break;
            case CharacterAppellationChangedEvent changed when changed.UnitId == _world.PlayerId:
                _events.Fire("APPELLATION_CHANGED", changed.UnitId.ToString(CultureInfo.InvariantCulture));
                break;
            case BuffCreatedEvent created:
            {
                var definition = _data?.GetBuff(created.BuffId);
                _buffTimers[(created.UnitId, created.BuffIndex)] = new TimerAnchor(created.DurationMs, created.DurationMs, now);
                _buffKinds[(created.UnitId, created.BuffIndex)] = definition?.KindId ?? 1;
                FireAura(created.UnitId, definition?.KindId ?? 1, "added");
                break;
            }
            case UnitBuffListEvent snapshot:
                foreach (var buff in snapshot.Buffs)
                {
                    _buffTimers[(snapshot.UnitId, buff.Index)] = new TimerAnchor(buff.DurationMs, buff.RemainingMs, now);
                    _buffKinds[(snapshot.UnitId, buff.Index)] = _data?.GetBuff(buff.BuffId)?.KindId ?? 1;
                }
                FireAura(snapshot.UnitId, 1, "changed");
                FireAura(snapshot.UnitId, 2, "changed");
                break;
            case BuffUpdatedEvent updated:
            {
                var key = (updated.UnitId, unchecked((uint)updated.BuffIndex));
                if (State?.TryGetUnit(updated.UnitId, out var unit) == true && unit!.Buffs.TryGetValue(key.Item2, out var buff))
                    _buffTimers[key] = new TimerAnchor(buff.DurationMs, buff.RemainingMs, now);
                FireAura(updated.UnitId, _buffKinds.GetValueOrDefault(key, 1), "changed");
                break;
            }
            case BuffRemovedEvent removed:
            {
                var key = (removed.UnitId, removed.BuffIndex);
                FireAura(removed.UnitId, _buffKinds.GetValueOrDefault(key, 1), "removed");
                _buffTimers.Remove(key);
                _buffKinds.Remove(key);
                break;
            }
            case SkillStartedEvent started:
                _casts[started.TimelineId] = (started.Caster.UnitId, now);
                var spellName = _data?.GetSkill(started.SkillId)?.Name ?? $"Skill {started.SkillId}";
                foreach (var token in CastTokens(started.Caster.UnitId))
                    _events.Fire(X2UnitEvents.SpellcastStart, spellName, (double)started.RealCastMs, token, false);
                if (started.Caster.UnitId == _world.PlayerId)
                {
                    var globalMs = GlobalCooldownMilliseconds();
                    if (globalMs > 0) _globalCooldown = new TimerAnchor(globalMs, globalMs, now);
                    FireCooldowns();
                }
                break;
            case SkillFiredEvent fired:
                FireCast(X2UnitEvents.SpellcastSucceeded, fired.Caster.UnitId);
                break;
            case SkillEndedEvent ended:
                if (_casts.Remove(ended.TimelineId, out var endedCast))
                    FireCast(X2UnitEvents.SpellcastStop, endedCast.Caster);
                break;
            case SkillStoppedEvent stopped:
                foreach (var key in _casts.Where(pair => pair.Value.Caster == stopped.UnitId).Select(pair => pair.Key).ToArray()) _casts.Remove(key);
                FireCast(X2UnitEvents.SpellcastStop, stopped.UnitId);
                break;
            case CooldownListEvent list:
                _cooldowns.Clear();
                foreach (var cooldown in list.Entries)
                    _cooldowns[(cooldown.Kind, cooldown.Id)] = new TimerAnchor(cooldown.DurationMs, cooldown.RemainingMs, now);
                FireCooldowns();
                break;
            case CooldownResetEvent reset when reset.UnitId == 0 || reset.UnitId == _world.PlayerId:
                if (reset.SkillId != 0) _cooldowns.Remove((CooldownKind.Skill, checked((int)reset.SkillId)));
                if (reset.TagId != 0) _cooldowns.Remove((CooldownKind.Tag, checked((int)reset.TagId)));
                if (reset.GlobalCooldown) _globalCooldown = null;
                FireCooldowns();
                break;
            case CooldownReduceEvent reduce when reduce.UnitId == 0 || reduce.UnitId == _world.PlayerId:
                foreach (var key in _cooldowns.Keys.ToArray())
                {
                    var timer = _cooldowns[key];
                    _cooldowns[key] = new TimerAnchor(timer.TotalMs, checked((int)Math.Max(0, timer.Remaining - reduce.ReduceMs)), now);
                }
                FireCooldowns();
                break;
            case TargetChangedEvent target:
                if (target.UnitId == _world.PlayerId) _world.SetTarget(target.TargetUnitId);
                if (target.UnitId == _world.TargetId) _world.SetTargetOfTarget(target.TargetUnitId);
                break;
            case AggroTargetChangedEvent target when target.UnitId == _world.TargetId:
                _world.SetTargetOfTarget(target.TargetUnitId);
                break;
            case CombatEngagedEvent engaged:
                FireCombat(engaged.UnitId, true);
                FireCombat(engaged.OtherUnitId, true);
                break;
            case CombatClearedEvent cleared:
                FireCombat(cleared.UnitId, false);
                break;
            case SkillBookUpdatedEvent:
                _events.Fire(X2UnitEvents.AbilityChanged);
                _events.Fire("SKILL_CHANGED");
                break;
            case AbilitySetSlotCountUpdatedEvent:
                _events.Fire(X2UnitEvents.AbilityChanged);
                break;
            case UnitSkillListEvent skills when skills.UnitId == _world.PlayerId:
                _activeSkills.Clear();
                _activeSkills.AddRange(skills.Skills.Distinct());
                _events.Fire("SKILL_CHANGED");
                _events.Fire(X2UnitEvents.AbilityChanged);
                ActionChanged();
                RefreshAbilitySlots();
                break;
            case SkillLearnedEvent learned:
                if (!_activeSkills.Contains(learned.SkillId)) _activeSkills.Add(learned.SkillId);
                _events.Fire(X2UnitEvents.SkillLearned);
                _events.Fire("SKILL_CHANGED");
                ActionChanged();
                RefreshAbilitySlots();
                var learnedSlot = KnownSkills().ToList().IndexOf(learned.SkillId) + 1;
                if (learnedSlot is > 0 and <= 12) _events.Fire(X2UnitEvents.ActionBarAutoRegistered, (double)learnedSlot);
                break;
            case SkillsResetEvent resetSkills when resetSkills.UnitId == _world.PlayerId:
                if (resetSkills.Ability == 0) _activeSkills.Clear();
                else _activeSkills.RemoveAll(id => _data?.GetSkill(id)?.AbilityId == resetSkills.Ability);
                _events.Fire(X2UnitEvents.SkillsReset);
                _events.Fire("SKILL_CHANGED");
                ActionChanged();
                RefreshAbilitySlots();
                break;
            case AbilitiesSwappedEvent swapped when swapped.UnitId == _world.PlayerId:
                _activeAbilities = swapped.NewAbilities.Where(id => id > 0).Select(id => (int)id).Distinct().Take(3).ToArray();
                _events.Fire(X2UnitEvents.AbilityChanged);
                _events.Fire("SKILL_CHANGED");
                ActionChanged();
                RefreshAbilitySlots();
                break;
            case SkillUnlockEvent unlock when unlock.ErrorCode == 0:
                _events.Fire(X2UnitEvents.SkillUpgraded, (double)unlock.SkillId);
                _events.Fire("SKILL_CHANGED");
                break;
            case SkillPointsEvent points:
                _events.Fire(X2UnitEvents.UpdateSkillPoint);
                // the "skill upgrades" notice is for points gained, not for the login snapshot or spending
                if (points.AdditionalPoints > _lastSkillPoints && _skillPointsKnown)
                    _events.Fire(X2UnitEvents.NewSkillPoint, (double)(points.AdditionalPoints - _lastSkillPoints));
                _lastSkillPoints = points.AdditionalPoints;
                _skillPointsKnown = true;
                break;
            case SkillActiveTypesEvent:
                _events.Fire(X2UnitEvents.UpdateSkillActiveType);
                break;
            case HeirSkillListEvent:
                _events.Fire(X2UnitEvents.HeirSkillUpdate);
                break;
            case ForceAttackChangedEvent changed:
                _events.Fire(X2UnitEvents.ForceAttackChanged, changed.UnitId.ToString(CultureInfo.InvariantCulture), changed.Enabled);
                break;
            case UnitNameChangedEvent changed:
                _events.Fire(X2UnitEvents.UnitNameChanged, changed.UnitId.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    private void FirePoints(uint unitId, int hp, int mp)
    {
        var unit = State?.TryGetUnit(unitId, out var value) == true ? value : null;
        var token = UnitToken(unitId);
        _events.Fire("UNIT_HEALTH_CHANGED", token, (double)hp, (double)(unit?.MaxHp ?? Math.Max(0, hp)));
        _events.Fire("UNIT_MANA_CHANGED", token, (double)mp, (double)(unit?.MaxMp ?? Math.Max(0, mp)));
    }

    private void FireAura(uint unitId, int kind, string action)
        => _events.Fire(kind == 2 ? "DEBUFF_UPDATE" : "BUFF_UPDATE", action, AuraToken(unitId));

    private void FireCombat(uint unitId, bool inCombat)
        => _events.Fire(X2UnitEvents.UnitCombatStateChanged, inCombat, unitId.ToString(CultureInfo.InvariantCulture));

    private void FireCooldowns()
    {
        RefreshActionSlots();
        foreach (var ((kind, id), timer) in _cooldowns)
            if (kind == CooldownKind.Skill) _events.Fire("SKILL_COOLDOWN", (double)id, timer.Remaining, (double)timer.TotalMs);
        _events.Fire("ACTIONBAR_UPDATE");
        _events.Fire(X2UnitEvents.UpdateShortcutSkills);
    }

    public void Dispose()
    {
        _world.TargetRequested -= RequestTarget;
        Detach();
        _actionSlots.Changed -= ActionChanged;
        _actionSlots.Dispose();
        _data?.Dispose();
        _characterStats?.Dispose();
    }
}
