#nullable enable
using System.Collections.ObjectModel;
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>Current cast information suitable for a unit frame or cast bar.</summary>
public sealed record CombatCastState(uint SkillId, ushort TimelineId, uint TargetUnitId,
    Vector3? TargetPosition, int CastTimeMs, int BaseCastTimeMs, bool HasFired,
    int EffectDelayMs, int ChannelTimeMs);

/// <summary>A buff as currently known from create/update/remove notifications.</summary>
public sealed record CombatBuffState(uint Index, uint BuffId, uint CasterUnitId,
    uint Stacks, uint Charge, int DurationMs, int RemainingMs, int TickMs, int ElapsedMs);

/// <summary>
/// Immutable view of one unit's combat-relevant state. Maximum HP/MP remain null until the
/// character sub-stat packet supplies them.
/// </summary>
public sealed record CombatUnitState(uint UnitId, UnitKind Kind, string Name, ulong DatabaseId,
    sbyte Level, int Hp, int Mp, long ExperienceDelta, int? MaxHp, int? MaxMp, bool IsDead, bool IsInCombat,
    uint TargetUnitId, string FactionName, uint FactionId, uint MotherFactionId,
    bool IsForceAttacking, bool IsForcedAttack, byte PostureType, byte[] PostureData, CombatCastState? CurrentCast,
    IReadOnlyDictionary<uint, CombatBuffState> Buffs, IReadOnlyList<uint> Skills,
    IReadOnlyList<uint> PassiveBuffs, IReadOnlyList<AggroEntry> Threat);

/// <summary>
/// Main-thread combat state reducer. Call <see cref="Apply(GameEvent)"/> in packet/event order from
/// the viewer's main thread. The reducer deliberately uses no locks and exposes snapshot copies.
/// </summary>
public sealed class CombatState
{
    private readonly Dictionary<uint, MutableUnit> _units = [];
    private readonly Dictionary<(CooldownKind Kind, int Id), CooldownEntry> _cooldowns = [];
    private readonly Dictionary<sbyte, long> _abilityExperienceDeltas = [];
    private readonly Dictionary<int, CombatResourcePointEvent> _resources = [];
    private readonly HashSet<uint> _learnedSkills = [];
    private IReadOnlyList<SkillBookSlot> _skillBookSlots = [];
    private IReadOnlyList<CombatRelationship> _combatRelationships = [];
    private IReadOnlyList<int> _characterGamePoints = [];
    private uint _additionalSkillPoints;
    private readonly HashSet<uint> _appellations = [];

    public CombatState(uint playerUnitId = 0) => PlayerUnitId = playerUnitId;

    public uint PlayerUnitId { get; private set; }
    public bool GlobalCooldownActive { get; private set; }
    public uint BreathRemainingMs { get; private set; }
    public long ExperienceDeltaTotal { get; private set; }
    public long? PlayerExperience { get; private set; }
    public IReadOnlyList<SkillBookSlot> SkillBookSlots => _skillBookSlots.ToArray();
    public int UsableAbilitySetSlots { get; private set; } = 1;
    public IReadOnlyCollection<uint> LearnedSkills => _learnedSkills.ToArray();
    public uint AdditionalSkillPoints => _additionalSkillPoints;
    public IReadOnlyCollection<uint> Appellations => _appellations.ToArray();
    public uint ShowingAppellationId { get; private set; }
    public uint ShowingAppellationStampId { get; private set; }
    public bool SkillBookNeedsRefresh { get; private set; }
    public IReadOnlyDictionary<(CooldownKind Kind, int Id), CooldownEntry> Cooldowns =>
        new ReadOnlyDictionary<(CooldownKind Kind, int Id), CooldownEntry>(
            new Dictionary<(CooldownKind Kind, int Id), CooldownEntry>(_cooldowns));
    public IReadOnlyDictionary<sbyte, long> AbilityExperienceDeltas =>
        new ReadOnlyDictionary<sbyte, long>(new Dictionary<sbyte, long>(_abilityExperienceDeltas));
    public IReadOnlyDictionary<int, CombatResourcePointEvent> Resources =>
        new ReadOnlyDictionary<int, CombatResourcePointEvent>(
            new Dictionary<int, CombatResourcePointEvent>(_resources));
    public IReadOnlyList<CombatRelationship> CombatRelationships => _combatRelationships.ToArray();
    public IReadOnlyList<int> CharacterGamePoints => _characterGamePoints.ToArray();
    public CharacterDetailEvent? LastCharacterDetail { get; private set; }
    public CharacterSubStatsEvent? LastCharacterSubStats { get; private set; }
    public ServerAttributeValuesEvent? LastServerAttributeValues { get; private set; }
    public RecoverableExperienceChangedEvent? LastRecoverableExperience { get; private set; }
    public IReadOnlyList<GamePointChange> LastGamePointChanges { get; private set; } = [];
    public SkillsResetEvent? LastSkillsReset { get; private set; }
    public AbilitiesSwappedEvent? LastAbilitiesSwapped { get; private set; }
    public SkillUnlockEvent? LastSkillUnlock { get; private set; }
    public IReadOnlyList<SkillActiveTypeEntry> SkillActiveTypes { get; private set; } = [];
    public IReadOnlyList<HeirSkillEntry> HeirSkills { get; private set; } = [];
    public DamageEvent? LastDamage { get; private set; }
    public HealEvent? LastHeal { get; private set; }
    public CombatTextEvent? LastCombatText { get; private set; }
    public EnvironmentDamageEvent? LastEnvironmentDamage { get; private set; }
    public CombatFirstHitEvent? LastCombatFirstHit { get; private set; }
    public AggroRemovedEvent? LastAggroRemoved { get; private set; }

    public IReadOnlyDictionary<uint, CombatUnitState> Units
    {
        get
        {
            var copy = new Dictionary<uint, CombatUnitState>(_units.Count);
            foreach (var (id, unit) in _units)
                copy.Add(id, Snapshot(unit));
            return new ReadOnlyDictionary<uint, CombatUnitState>(copy);
        }
    }

    public bool TryGetUnit(uint unitId, out CombatUnitState? state)
    {
        if (_units.TryGetValue(unitId, out var unit))
        {
            state = Snapshot(unit);
            return true;
        }
        state = null;
        return false;
    }

    public void SetPlayerUnitId(uint unitId) => PlayerUnitId = unitId;

    /// <summary>Apply one immutable network event in receive order.</summary>
    public void Apply(GameEvent gameEvent)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        switch (gameEvent)
        {
            case EnteredWorldEvent entered:
                PlayerUnitId = entered.UnitId;
                PlayerExperience = entered.Experience;
                Seed(entered.Self);
                break;
            case UnitsRemovedEvent removed:
                foreach (var unitId in removed.UnitIds) _units.Remove(unitId);
                break;
            case UnitAppearedEvent appeared:
                Seed(appeared.Unit);
                break;
            case UnitStateEvent unitState:
                Seed(unitState.Snapshot.Unit);
                break;
            case UnitSkillListEvent skills:
                Get(skills.UnitId).Skills = skills.Skills.ToArray();
                Get(skills.UnitId).PassiveBuffs = skills.PassiveBuffs.ToArray();
                if (skills.UnitId == PlayerUnitId)
                {
                    _learnedSkills.Clear();
                    foreach (var skill in skills.Skills) _learnedSkills.Add(skill);
                    SkillBookNeedsRefresh = false;
                }
                break;
            case UnitBuffListEvent buffs:
            {
                var unit = Get(buffs.UnitId);
                unit.Buffs.Clear();
                foreach (var buff in buffs.Buffs) unit.Buffs[buff.Index] = buff;
                break;
            }
            case UnitPointsEvent points:
                Get(points.UnitId).Hp = points.Hp;
                Get(points.UnitId).Mp = points.Mp;
                break;
            case UnitDeathEvent death:
            {
                var unit = Get(death.UnitId);
                unit.IsDead = true;
                unit.Hp = 0;
                unit.CurrentCast = null;
                break;
            }
            case DamageEvent damage:
                LastDamage = damage;
                break;
            case HealEvent heal:
                LastHeal = heal;
                break;
            case CombatTextEvent combatText:
                LastCombatText = combatText;
                break;
            case EnvironmentDamageEvent environmentDamage:
                LastEnvironmentDamage = environmentDamage;
                break;
            case CharacterResurrectedEvent resurrected:
            {
                var unit = Get(resurrected.UnitId);
                unit.IsDead = false;
                unit.Position = resurrected.Position;
                unit.Yaw = resurrected.Yaw;
                break;
            }
            case UnitLevelEvent level:
                Get(level.UnitId).Level = checked((sbyte)level.Level);
                break;
            case BreathChangedEvent breath:
                BreathRemainingMs = breath.RemainingMs;
                break;
            case ExperienceChangedEvent experience:
                ExperienceDeltaTotal += experience.Delta;
                Get(experience.UnitId).ExperienceDelta += experience.Delta;
                if (experience.UnitId == PlayerUnitId && PlayerExperience.HasValue)
                    PlayerExperience = Math.Max(0, PlayerExperience.Value + experience.Delta);
                break;
            case RecoverableExperienceChangedEvent recoverable:
                LastRecoverableExperience = recoverable;
                break;
            case CharacterDetailEvent detail:
                LastCharacterDetail = detail;
                foreach (var unit in _units.Values)
                {
                    if (unit.DatabaseId != detail.CharacterId) continue;
                    unit.Name = detail.Name;
                    unit.Level = checked((sbyte)detail.Level);
                    unit.Hp = checked((int)detail.Health);
                    break;
                }
                break;
            case CharacterSubStatsEvent subStats:
                LastCharacterSubStats = subStats;
                if (PlayerUnitId != 0)
                {
                    var player = Get(PlayerUnitId);
                    player.MaxHp = ClampToInt(subStats.MaxHp);
                    player.MaxMp = ClampToInt(subStats.MaxMp);
                }
                break;
            case ServerAttributeValuesEvent attributes:
                LastServerAttributeValues = attributes;
                break;
            case AbilityExperienceChangedEvent abilityExperience:
                _abilityExperienceDeltas[abilityExperience.Ability] =
                    _abilityExperienceDeltas.GetValueOrDefault(abilityExperience.Ability) + abilityExperience.Delta;
                break;
            case BuffCreatedEvent created:
            {
                var unit = Get(created.UnitId);
                unit.Buffs[created.BuffIndex] = new CombatBuffState(created.BuffIndex, created.BuffId,
                    created.Caster.UnitId, created.Stacks, created.Charge, created.DurationMs,
                    created.DurationMs, created.TickMs, 0);
                break;
            }
            case BuffUpdatedEvent updated:
            {
                var unit = Get(updated.UnitId);
                var index = unchecked((uint)updated.BuffIndex);
                unit.Buffs.TryGetValue(index, out var old);
                var elapsed = Math.Max(0, updated.ElapsedMs);
                var remaining = old is null ? 0 : Math.Max(0, old.DurationMs - elapsed);
                unit.Buffs[index] = new CombatBuffState(index, old?.BuffId ?? 0, old?.CasterUnitId ?? 0,
                    updated.Stacks, updated.Charge, old?.DurationMs ?? 0, remaining,
                    old?.TickMs ?? 0, elapsed);
                break;
            }
            case BuffRemovedEvent removed:
                Get(removed.UnitId).Buffs.Remove(removed.BuffIndex);
                break;
            case SkillStartedEvent started:
                Get(started.Caster.UnitId).CurrentCast = new CombatCastState(started.SkillId,
                    started.TimelineId, started.Target.UnitId,
                    started.Target.Type is 1 or 2 or 5 ? started.Target.Position : null,
                    started.RealCastMs, started.BaseCastMs, false, 0, 0);
                break;
            case SkillFiredEvent fired:
            {
                var unit = Get(fired.Caster.UnitId);
                var previous = unit.CurrentCast;
                unit.CurrentCast = new CombatCastState(fired.SkillId, fired.TimelineId, fired.Target.UnitId,
                    fired.Target.Type is 1 or 2 or 5 ? fired.Target.Position : null,
                    previous?.CastTimeMs ?? 0, previous?.BaseCastTimeMs ?? 0, true,
                    fired.EffectDelayMs, fired.ChannelTimeMs);
                break;
            }
            case SkillEndedEvent ended:
                foreach (var unit in _units.Values)
                    if (unit.CurrentCast?.TimelineId == ended.TimelineId) unit.CurrentCast = null;
                break;
            case SkillStoppedEvent stopped:
            {
                var unit = Get(stopped.UnitId);
                if (unit.CurrentCast is { } cast && (stopped.SkillId == 0 || cast.SkillId == stopped.SkillId))
                    unit.CurrentCast = null;
                break;
            }
            case TargetChangedEvent target:
                Get(target.UnitId).TargetUnitId = target.TargetUnitId;
                break;
            case CombatEngagedEvent engaged:
                Get(engaged.UnitId).IsInCombat = true;
                Get(engaged.OtherUnitId).IsInCombat = true;
                break;
            case CombatFirstHitEvent firstHit:
                LastCombatFirstHit = firstHit;
                break;
            case CombatClearedEvent cleared:
                Get(cleared.UnitId).IsInCombat = false;
                break;
            case CombatRelationshipsEvent relationships:
                _combatRelationships = relationships.Relationships.ToArray();
                break;
            case AiAggroEvent aggro:
                Get(aggro.NpcUnitId).Threat = aggro.Entries.ToArray();
                break;
            case AggroTargetChangedEvent aggroTarget:
                Get(aggroTarget.UnitId).TargetUnitId = aggroTarget.TargetUnitId;
                break;
            case AggroRemovedEvent aggroRemoved:
                LastAggroRemoved = aggroRemoved;
                break;
            case ForceAttackChangedEvent forceAttack:
                Get(forceAttack.UnitId).IsForceAttacking = forceAttack.Enabled;
                Get(forceAttack.UnitId).IsForcedAttack = forceAttack.Forced;
                break;
            case UnitPostureChangedEvent posture:
            {
                var unit = Get(posture.UnitId);
                unit.PostureType = posture.PostureType;
                unit.PostureData = posture.PostureData.ToArray();
                break;
            }
            case UnitNameChangedEvent name:
                Get(name.UnitId).Name = name.Name;
                break;
            case UnitFactionChangedEvent faction:
            {
                var unit = Get(faction.UnitId);
                unit.FactionName = faction.Name;
                unit.FactionId = faction.FactionId;
                unit.MotherFactionId = faction.MotherFactionId;
                break;
            }
            case CooldownListEvent cooldowns:
                _cooldowns.Clear();
                foreach (var entry in cooldowns.Entries)
                    _cooldowns[(entry.Kind, entry.Id)] = entry;
                break;
            case CooldownResetEvent reset:
                ApplyCooldownReset(reset);
                break;
            case CooldownReduceEvent reduce:
                LastCooldownReduction = reduce;
                break;
            case ChargeCooldownChangedEvent charge:
                LastChargeCooldownChange = charge;
                break;
            case SkillBookUpdatedEvent book:
                _skillBookSlots = book.Slots.ToArray();
                break;
            case AbilitySetSlotCountUpdatedEvent slots:
                UsableAbilitySetSlots = Math.Clamp((int)slots.UsableCount, 0, 5);
                break;
            case SkillLearnedEvent learned:
                _learnedSkills.Add(learned.SkillId);
                if (PlayerUnitId != 0)
                    Get(PlayerUnitId).Skills = Get(PlayerUnitId).Skills.Append(learned.SkillId).Distinct().ToArray();
                break;
            case SkillsResetEvent resetSkills:
                if (resetSkills.UnitId == PlayerUnitId)
                {
                    LastSkillsReset = resetSkills;
                    SkillBookNeedsRefresh = true;
                }
                break;
            case AbilitiesSwappedEvent swapped:
                if (swapped.UnitId == PlayerUnitId)
                {
                    LastAbilitiesSwapped = swapped;
                    SkillBookNeedsRefresh = true;
                }
                break;
            case SkillUnlockEvent unlock:
                LastSkillUnlock = unlock;
                break;
            case SkillActiveTypesEvent activeTypes:
                SkillActiveTypes = activeTypes.Entries.ToArray();
                break;
            case HeirSkillListEvent heirSkills:
                HeirSkills = heirSkills.Entries.ToArray();
                break;
            case SkillPointsEvent skillPoints:
                _additionalSkillPoints = skillPoints.AdditionalPoints;
                break;
            case CharacterGamePointsEvent gamePoints:
                _characterGamePoints = gamePoints.Points.ToArray();
                break;
            case CharacterAppellationsEvent appellations:
                _appellations.Clear();
                foreach (var (id, _) in appellations.Appellations) _appellations.Add(id);
                ShowingAppellationId = appellations.Appellations.FirstOrDefault(x => x.Selected).Id;
                break;
            case CharacterAppellationGainedEvent gained:
                _appellations.Add(gained.AppellationId);
                break;
            case CharacterAppellationChangedEvent changed when changed.UnitId == PlayerUnitId:
                ShowingAppellationId = changed.AppellationId;
                ShowingAppellationStampId = changed.AppellationStampId;
                break;
            case GamePointsChangedEvent gamePointChanges:
            {
                LastGamePointChanges = gamePointChanges.Changes.ToArray();
                var updatedPoints = _characterGamePoints.ToArray();
                foreach (var change in gamePointChanges.Changes)
                {
                    if (change.Kind < updatedPoints.Length)
                        updatedPoints[change.Kind] = change.Amount;
                }
                _characterGamePoints = updatedPoints;
                break;
            }
            case CombatResourcePointEvent resource:
                _resources[resource.ResourceId] = resource;
                break;
            case CombatResourceTransformEvent transform:
                LastResourceTransform = transform;
                break;
            case CombatResourceUnitMoveEvent resourceMove:
                LastResourceMove = resourceMove;
                break;
        }
    }

    public CooldownReduceEvent? LastCooldownReduction { get; private set; }
    public CooldownResetEvent? LastCooldownReset { get; private set; }
    public ChargeCooldownChangedEvent? LastChargeCooldownChange { get; private set; }
    public CombatResourceTransformEvent? LastResourceTransform { get; private set; }
    public CombatResourceUnitMoveEvent? LastResourceMove { get; private set; }

    private void ApplyCooldownReset(CooldownResetEvent reset)
    {
        if (reset.UnitId != 0 && reset.UnitId != PlayerUnitId)
            return;
        if (reset.GlobalCooldown)
            GlobalCooldownActive = false;
        if (reset.SkillId != 0)
            _cooldowns.Remove((CooldownKind.Skill, checked((int)reset.SkillId)));
        if (reset.TagId != 0)
            _cooldowns.Remove((CooldownKind.Tag, checked((int)reset.TagId)));
        LastCooldownReset = reset;
    }

    private void Seed(UnitSnapshot snapshot)
    {
        var unit = Get(snapshot.UnitId);
        unit.Kind = snapshot.Kind;
        unit.Name = snapshot.Name;
        unit.DatabaseId = snapshot.DbId;
        unit.Level = snapshot.Level;
        unit.Hp = ClampToInt(snapshot.Hp);
        unit.Mp = ClampToInt(snapshot.Mp);
        unit.Position = snapshot.Position;
        unit.Yaw = snapshot.Yaw;
    }

    private MutableUnit Get(uint unitId)
    {
        if (!_units.TryGetValue(unitId, out var unit))
            _units.Add(unitId, unit = new MutableUnit(unitId));
        return unit;
    }

    private static int ClampToInt(ulong value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static CombatUnitState Snapshot(MutableUnit unit) => new(unit.UnitId, unit.Kind,
        unit.Name, unit.DatabaseId, unit.Level, unit.Hp, unit.Mp, unit.ExperienceDelta, unit.MaxHp, unit.MaxMp,
        unit.IsDead, unit.IsInCombat, unit.TargetUnitId, unit.FactionName, unit.FactionId,
        unit.MotherFactionId, unit.IsForceAttacking, unit.IsForcedAttack, unit.PostureType,
        unit.PostureData.ToArray(), unit.CurrentCast,
        new ReadOnlyDictionary<uint, CombatBuffState>(new Dictionary<uint, CombatBuffState>(unit.Buffs)), unit.Skills.ToArray(),
        unit.PassiveBuffs.ToArray(), unit.Threat.ToArray());

    private sealed class MutableUnit(uint unitId)
    {
        public uint UnitId { get; } = unitId;
        public UnitKind Kind { get; set; } = UnitKind.Unknown;
        public string Name { get; set; } = string.Empty;
        public ulong DatabaseId { get; set; }
        public sbyte Level { get; set; }
        public int Hp { get; set; }
        public int Mp { get; set; }
        public long ExperienceDelta { get; set; }
        public int? MaxHp { get; set; }
        public int? MaxMp { get; set; }
        public bool IsDead { get; set; }
        public bool IsInCombat { get; set; }
        public uint TargetUnitId { get; set; }
        public string FactionName { get; set; } = string.Empty;
        public uint FactionId { get; set; }
        public uint MotherFactionId { get; set; }
        public bool IsForceAttacking { get; set; }
        public bool IsForcedAttack { get; set; }
        public Vector3 Position { get; set; }
        public float Yaw { get; set; }
        public byte PostureType { get; set; }
        public byte[] PostureData { get; set; } = [];
        public CombatCastState? CurrentCast { get; set; }
        public Dictionary<uint, CombatBuffState> Buffs { get; } = [];
        public IReadOnlyList<uint> Skills { get; set; } = [];
        public IReadOnlyList<uint> PassiveBuffs { get; set; } = [];
        public IReadOnlyList<AggroEntry> Threat { get; set; } = [];
    }
}
