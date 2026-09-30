#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Combat and unit-state opcodes for the 10.0.2.13 client.
/// Kept in a separate class so the viewer's existing <c>Net/Opcodes.cs</c> stays untouched.
/// Values are cross-checked against X2Game.Protocol generated enums and AAEmu's SCOffsets/CSOffsets.
/// </summary>
public static class CombatOpcodes
{
    // Server -> client
    public const ushort SCNotifyResurrection = 0x06D;
    public const ushort SCCharacterResurrected = 0x06E;
    public const ushort SCForceAttackSet = 0x06F;
    public const ushort SCCharacterState = 0x06C;
    public const ushort SCCooldowns = 0x075;
    public const ushort SCCharDetail = 0x07E;
    public const ushort SCUnitFactionChanged = 0x02E;
    public const ushort SCUnitState = 0x097;
    public const ushort SCUnitsRemoved = 0x098;
    public const ushort SCUnitNameChanged = 0x0A0;
    public const ushort SCUnitDeath = 0x0A1;
    public const ushort SCTargetChanged = 0x0B0;
    public const ushort SCCombatEngaged = 0x0B1;
    public const ushort SCCombatCleared = 0x0B2;
    public const ushort SCCvFCombatRelationship = 0x0B3;
    public const ushort SCFvFCombatRelationship = 0x0B4;
    public const ushort SCCombatFirstHit = 0x0B5;
    public const ushort SCSkillStarted = 0x0D7;
    public const ushort SCSkillFired = 0x0D8;
    public const ushort SCSkillEnded = 0x0D9;
    public const ushort SCSkillStopped = 0x0DA;
    public const ushort SCUnitDamaged = 0x0DD;
    public const ushort SCUnitHealed = 0x0DE;
    public const ushort SCCombatText = 0x0E1;
    public const ushort SCSkillCooldownReset = 0x0E2;
    public const ushort SCEnvDamage = 0x0E8;
    public const ushort SCBuffCreated = 0x0EB;
    public const ushort SCBuffRemoved = 0x0EC;
    public const ushort SCBuffUpdated = 0x0EE;
    public const ushort SCUnitPoints = 0x0EF;
    public const ushort SCExpChanged = 0x13D;
    public const ushort SCAbilityExpChanged = 0x13E;
    public const ushort SCRecoverableExp = 0x13F;
    public const ushort SCLevelChanged = 0x140;
    public const ushort SCUnitModelPostureChanged = 0x142;
    public const ushort SCSkillLearned = 0x143;
    public const ushort SCSkillsReset = 0x145;
    public const ushort SCUnlockLearnSkill = 0x146;
    public const ushort SCAbilitySwapped = 0x147;
    public const ushort SCAbilitySetAllInfo = 0x149;
    public const ushort SCAbilitySetSlotCountUpdated = 0x14B;
    public const ushort SCEmotionExpressed = 0x16E;
    public const ushort SCActability = 0x17B;
    public const ushort SCCharacterGamePoints = 0x1CE;
    public const ushort SCSetBreath = 0x22A;
    public const ushort SCAggroTargetChanged = 0x22B;
    public const ushort SCAiAggro = 0x23F;
    public const ushort SCListSkillActiveType = 0x2E1;
    public const ushort SCHeirSkillList = 0x2F4;
    public const ushort SCSkillCooldownReduce = 0x304;
    public const ushort SCChargeSkillCooldownChanged = 0x31D;
    public const ushort SCAttackFaction = 0x327;
    public const ushort SCCombatResourcePoint = 0x35B;
    public const ushort SCCombatResourceTransform = 0x35C;
    public const ushort SCCombatResourceUnitMove = 0x35D;
    public const ushort SCUpdateAdditionalSkillPoint = 0x381;
    public const ushort SCAggroRemoved = 0x22C;
    public const ushort SCGamePointChanged = 0x1D0;
    public const ushort SCAppellations = 0x213;
    public const ushort SCAppellationGained = 0x214;
    public const ushort SCAppellationChanged = 0x215;
    public const ushort SCGmDumpSubStat = 0x250;
    public const ushort SCGmServerAttributeValue = 0x366;

    // Client -> server
    public const ushort CSChangeTarget = 0x054;
    public const ushort CSResurrectCharacter = 0x082;
    public const ushort CSStartSkill = 0x086;
    public const ushort CSStopCasting = 0x088;
    public const ushort CSInteractNpc = 0x09B;
    public const ushort CSInteractNpcEnd = 0x09C;
    public const ushort CSStartInteraction = 0x09E;
    public const ushort CSSelectInteractionEx = 0x0A1;
    public const ushort CSLearnSkill = 0x0D2;
    public const ushort CSResetSkills = 0x0D4;
    public const ushort CSUnlockLearnSkill = 0x0D9;
    public const ushort CSExpressEmotion = 0x0EE;
    public const ushort CSCharDetail = 0x15A;
}
