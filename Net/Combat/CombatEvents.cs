#nullable enable
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

public sealed record UnitCombatSnapshot(UnitSnapshot Unit, byte[] OpaqueStateBody);
public sealed record UnitStateEvent(UnitCombatSnapshot Snapshot) : GameEvent;
public sealed record UnitSkillListEvent(uint UnitId, IReadOnlyList<uint> Skills,
    IReadOnlyList<uint> PassiveBuffs) : GameEvent;
/// <summary>The three visible buff lists embedded in SCUnitState.</summary>
public sealed record UnitBuffListEvent(uint UnitId, IReadOnlyList<CombatBuffState> Buffs) : GameEvent;
public sealed record UnitDeathEvent(uint UnitId, byte Reason, uint ResurrectionWaitMs,
    uint SpecialResurrectionWaitMs, uint AutoResurrectionWaitMs, int LostExperience,
    byte DurabilityLossRatio, uint KillerUnitId, string KillerName) : GameEvent;
public sealed record ResurrectionPromptEvent(SkillSource Source) : GameEvent;
public sealed record CharacterResurrectedEvent(uint UnitId, Vector3 Position, float Yaw) : GameEvent;
public sealed record ExperienceChangedEvent(uint UnitId, int Delta, bool AddAbilityExperience) : GameEvent;
public sealed record RecoverableExperienceChangedEvent(uint UnitId, int Recoverable,
    int Penalized, int Reason) : GameEvent;
public sealed record AbilityExperienceChangedEvent(uint UnitId, sbyte Ability, int Delta, bool ApplyAll) : GameEvent;
public sealed record UnitLevelEvent(uint UnitId, byte Level) : GameEvent;
public sealed record BreathChangedEvent(uint RemainingMs) : GameEvent;

public sealed record CharacterDetailEvent(ulong CharacterId, string Name, byte Race, uint Health, byte Level,
    byte Ability1, byte Ability2, byte Ability3, Vector3 Position, int ZoneId, long LastWorldLeaveTime,
    IReadOnlyList<EquipmentSlot> Equipment, bool Exists) : GameEvent;

public sealed record SkillSource(byte Type, uint UnitId, ulong ItemId = 0, uint ItemTemplateId = 0,
    byte ItemSourceType = 0, ulong ItemSourceData = 0, uint MountSkillTemplateId = 0);
public sealed record SkillTarget(byte Type, uint UnitId, Vector3 Position, Vector3 EndPosition,
    Vector3 Normal, float Rotation, ulong ItemId, uint ItemType, byte ItemSubType, float Pitch = 0);
public sealed record SkillCastExtra(byte Type, byte InputDirection, byte[] Payload);
public sealed record SkillCastTail(byte Flags, byte Result, ushort ResultCode, uint ResultValue, bool ResultFlag);
public sealed record CastActionInfo(byte Type, uint SkillId, ushort TimelineId, uint PlotId,
    uint EventId, uint BuffId, uint BuffOwnerId, uint BuffIndex, bool Flag1, bool Flag2);

public sealed record BuffCreatedEvent(uint UnitId, uint BuffIndex, uint BuffId, SkillSource Caster,
    ulong CasterDatabaseId, byte CasterLevel, short AbilityLevel, int ToggleSkillId, uint Stacks,
    uint Charge, int DurationMs, int TickMs) : GameEvent;
public sealed record BuffRemovedEvent(uint UnitId, uint BuffIndex) : GameEvent;
public sealed record BuffUpdatedEvent(uint UnitId, int BuffIndex, uint Stacks, uint Charge,
    int ElapsedMs, byte Reason) : GameEvent;

public sealed record SkillStartedEvent(uint SkillId, ushort TimelineId, SkillSource Caster, SkillTarget Target,
    SkillCastExtra Extra, int RealCastMs, int BaseCastMs, byte CastSynergy, SkillCastTail Tail) : GameEvent;
public sealed record SkillFiredEvent(uint SkillId, ushort TimelineId, SkillSource Caster, SkillTarget Target,
    SkillCastExtra Extra, int EffectDelayMs, int ChannelTimeMs, uint FireAnimationId,
    byte Flags, SkillCastTail Tail) : GameEvent;
public sealed record SkillEndedEvent(ushort TimelineId) : GameEvent;
public sealed record SkillStoppedEvent(uint UnitId, uint SkillId) : GameEvent;

public enum CombatHitType : byte
{
    Invalid = 0x00, MeleeHit = 0x01, MeleeCritical = 0x03, MeleeMiss = 0x04,
    MeleeDodge = 0x05, MeleeBlock = 0x06, MeleeParry = 0x07, RangedHit = 0x09,
    RangedMiss = 0x0A, RangedCritical = 0x0B, SpellHit = 0x0D, SpellMiss = 0x0E,
    SpellCritical = 0x0F, RangedDodge = 0x10, RangedBlock = 0x11, Immune = 0x12,
    SpellResist = 0x13, RangedParry = 0x14,
}

public sealed record DamageEvent(CastActionInfo Action, SkillSource Source, uint CasterUnitId,
    uint TargetUnitId, uint Amount, uint Absorbed, uint ManaBurn, byte CrimeState, byte HoldableId,
    uint ElementDamage, bool ShowElementEffect, uint ElementType, CombatHitType HitType,
    ushort HitFlags, byte Flags, byte Result) : GameEvent;
public sealed record HealEvent(CastActionInfo Action, SkillSource Source, uint TargetUnitId,
    byte HealType, byte HitType, long Amount, long Overheal, byte CrimeState,
    uint ElementHeal, bool ShowElementEffect, uint ElementType, byte Result) : GameEvent;
public sealed record CombatTextEvent(uint SourceUnitId, uint TargetUnitId, byte TextType) : GameEvent;
public sealed record EnvironmentDamageEvent(byte SourceType, uint TargetUnitId, uint Amount,
    byte[] SourceData) : GameEvent;

public enum CooldownKind : byte { Skill, Tag, Charge }
public sealed record CooldownEntry(CooldownKind Kind, int Id, int DurationMs, int RemainingMs);
public sealed record CooldownListEvent(IReadOnlyList<CooldownEntry> Entries) : GameEvent;
public sealed record CooldownResetEvent(uint UnitId, uint SkillId, uint TagId, bool GlobalCooldown,
    bool ResetSkillTag, bool ResetToggleSkill, bool ResetToggleSkillTag) : GameEvent;
public sealed record CooldownReduceEvent(uint UnitId, int Type, int Type2, uint Percent,
    uint Count, uint ReduceMs, bool ResetSkillTag, bool ResetToggleSkill, bool ResetToggleSkillTag) : GameEvent;
public sealed record ChargeCooldownChangedEvent(uint UnitId, int Type, uint Percent,
    uint Count, uint ReduceMs) : GameEvent;

public sealed record TargetChangedEvent(uint UnitId, uint TargetUnitId) : GameEvent;
public sealed record CombatEngagedEvent(uint UnitId, uint OtherUnitId) : GameEvent;
public sealed record CombatClearedEvent(uint UnitId) : GameEvent;
public sealed record CombatFirstHitEvent(uint VictimUnitId, uint HitterUnitId, uint HitterTeamId) : GameEvent;
public sealed record ForceAttackChangedEvent(uint UnitId, bool Enabled, bool Forced) : GameEvent;
public sealed record CombatRelationship(long Key, byte Code, byte Reason);
public sealed record CombatRelationshipsEvent(bool FactionVsFaction,
    IReadOnlyList<CombatRelationship> Relationships) : GameEvent;
public sealed record AggroEntry(uint HostileUnitId, int Value1, int Value2, int Value3, byte TopFlags);
public sealed record AiAggroEvent(uint NpcUnitId, IReadOnlyList<AggroEntry> Entries) : GameEvent;
public sealed record AggroTargetChangedEvent(uint UnitId, uint TargetUnitId) : GameEvent;
public sealed record AggroRemovedEvent(uint UnitId, sbyte Reason) : GameEvent;
public sealed record AttackFactionChangedEvent(uint UnitId, sbyte Flags) : GameEvent;

public sealed record UnitPostureChangedEvent(uint UnitId, byte PostureType, bool Looted,
    byte[] PostureData) : GameEvent;
public sealed record EmotionExpressedEvent(uint SourceUnitId, uint TargetUnitId, uint EmotionId) : GameEvent;
public sealed record UnitNameChangedEvent(uint UnitId, string Name) : GameEvent;
public sealed record UnitFactionChangedEvent(uint UnitId, string Name, uint FactionId,
    uint MotherFactionId, bool Temporary) : GameEvent;

public sealed record SkillBookSlot(byte Ability1, byte Ability2, byte Ability3,
    IReadOnlyList<uint> Skills, IReadOnlyList<uint> PassiveBuffs, IReadOnlyList<uint> HeirSkills);
public sealed record SkillBookUpdatedEvent(byte UsedFreeActivationCount,
    IReadOnlyList<SkillBookSlot> Slots) : GameEvent;
public sealed record AbilitySetSlotCountUpdatedEvent(sbyte UsableCount) : GameEvent;
public sealed record AbilitiesSwappedEvent(uint UnitId, IReadOnlyList<byte> OldAbilities,
    IReadOnlyList<byte> NewAbilities) : GameEvent;
public sealed record SkillLearnedEvent(uint SkillId) : GameEvent;
public sealed record SkillsResetEvent(uint UnitId, byte Ability) : GameEvent;
public sealed record SkillUnlockEvent(short ErrorCode, uint SkillId) : GameEvent;
public sealed record SkillPointsEvent(uint AdditionalPoints) : GameEvent;
public sealed record SkillActiveTypeEntry(int HeirSkillType, int SkillType, byte ActiveType);
public sealed record SkillActiveTypesEvent(IReadOnlyList<SkillActiveTypeEntry> Entries) : GameEvent;
public sealed record HeirSkillEntry(int HeirSkillId, int BaseSkillId, int SuccessorSkillId,
    uint SkillLevel, sbyte Ability, sbyte ActiveType);
public sealed record HeirSkillListEvent(IReadOnlyList<HeirSkillEntry> Entries) : GameEvent;
public sealed record ActabilityEntry(uint Id, uint Points, byte Step);
public sealed record ActabilityEvent(bool Last, IReadOnlyList<ActabilityEntry> Entries) : GameEvent;
public sealed record CharacterGamePointsEvent(IReadOnlyList<int> Points) : GameEvent;
public sealed record CharacterAppellationsEvent(IReadOnlyList<(uint Id, bool Selected)> Appellations) : GameEvent;
public sealed record CharacterAppellationGainedEvent(uint AppellationId) : GameEvent;
public sealed record CharacterAppellationChangedEvent(uint UnitId, uint AppellationId, uint AppellationStampId = 0) : GameEvent;
public sealed record GamePointChange(byte Kind, int Amount);
public sealed record GamePointsChangedEvent(IReadOnlyList<GamePointChange> Changes) : GameEvent;

public enum CharacterStatField : byte
{
    MeleeDps, RangedDps, SpellDps, HealDps, Armor, ArmorPercentage, MagicResist,
    MagicResistPercentage, MoveSpeed, CastingTime, MainhandMeleeSpeed, OffhandMeleeSpeed,
    MeleeSuccessRate, MeleeCriticalRate, RangedSpeed, RangedSuccessRate, RangedCriticalRate,
    BullsEye, SpellSuccessRate, SpellCriticalRate, HealCriticalRate, MeleeParryRate,
    RangedParryRate, BlockRate, DodgeRate, Flexibility, BattleResist, HealthRegen,
    PersistentHealthRegen, ManaRegen, PersistentManaRegen, Strength, Dexterity, Stamina,
    Intelligence, Spirit, MeleeMinDps, MeleeMaxDps, RangedMinDps, RangedMaxDps,
    CastingTimeMultiplier, MagicEffectResistPercentage, GlobalCooldown, AttackAnimationSpeed,
    GearScore, BareGearScore,
}
public sealed record CharacterSubStatsEvent(string Name, IReadOnlyList<float> Values,
    ulong MaxHp, ulong MaxMp) : GameEvent;
public sealed record ServerAttributeValue(sbyte Attribute, sbyte Attribute2, ulong Value);
public sealed record ServerAttributeValuesEvent(IReadOnlyList<ServerAttributeValue> Values) : GameEvent;

public sealed record CombatResourcePointEvent(uint UnitId, int ResourceId, ulong Points,
    int UpdateTime) : GameEvent;
public sealed record CombatResourceTransformEvent(uint UnitId, int ResourceId,
    bool PreviousDefaultResourceActive) : GameEvent;
public sealed record CombatResourceUnitMoveEvent(uint UnitId, int ResourceId, bool IsMove) : GameEvent;
