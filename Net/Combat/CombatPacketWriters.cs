#nullable enable

using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>A complete game-channel packet body ready for <c>GameClient.SendGame</c>.</summary>
public sealed record CombatOutboundPacket(ushort Opcode, byte[] Body);

/// <summary>
/// Builders for the small, proven subset of client-to-server combat requests.
/// Bodies use <see cref="WireWriter"/> so all integer and <c>bc</c> encodings match the viewer.
/// </summary>
public static class CombatPacketWriters
{
    // C2G CSStartSkill reads: u32 skill, SkillCaster (u8 type + payload),
    // SkillCastTarget (u8 type + payload), skill-object flags u8, input direction u8.
    private const byte UnitCaster = 0;
    private const byte UnitTarget = 0;
    private const byte PositionTarget = 1;
    private const byte DoodadTarget = 4;
    private const byte NoSkillObject = 0;
    private const byte NoInputDirection = 0;

    /// <summary>CSChangeTarget: select <paramref name="targetUnitId"/>.</summary>
    public static CombatOutboundPacket SelectTarget(uint targetUnitId) =>
        Packet(CombatOpcodes.CSChangeTarget, new WireWriter().Bc(targetUnitId));

    /// <summary>CSChangeTarget with the server-recognized zero <c>bc</c> id.</summary>
    public static CombatOutboundPacket ClearTarget() => SelectTarget(0);

    /// <summary>
    /// CSStartSkill for a normal unit-caster, unit-target skill. The final two zero bytes are
    /// SkillObject.None and inputDirection.
    /// </summary>
    public static CombatOutboundPacket StartSkillOnUnit(uint skillId, uint casterUnitId, uint targetUnitId) =>
        StartSkillHeader(skillId, casterUnitId)
            .U8(UnitTarget).Bc(targetUnitId)
            .U8(NoSkillObject).U8(NoInputDirection)
            .ToPacket(CombatOpcodes.CSStartSkill);

    /// <summary>CSStartSkill with the 10.0.2.13 doodad-target body (target type 4 plus object <c>bc</c>).</summary>
    public static CombatOutboundPacket StartSkillOnDoodad(uint skillId, uint casterUnitId, uint doodadObjectId) =>
        StartSkillHeader(skillId, casterUnitId)
            .U8(DoodadTarget).Bc(doodadObjectId)
            .U8(NoSkillObject).U8(NoInputDirection)
            .ToPacket(CombatOpcodes.CSStartSkill);

    /// <summary>
    /// CSStartSkill with SkillCastPositionTarget. X and Y use AAEmu's signed fixed-point i64
    /// conversion; the target also carries all three required trailing <c>bc</c> ids.
    /// </summary>
    public static CombatOutboundPacket StartSkillOnPosition(
        uint skillId, uint casterUnitId, Vector3 position, float rotation,
        uint objectId1 = 0, uint objectId2 = 0, uint objectId3 = 0) =>
        StartSkillHeader(skillId, casterUnitId)
            .U8(PositionTarget)
            .S64(ToSkillPositionX(position.X)).S64(ToSkillPositionY(position.Y))
            .F32(position.Z).F32(rotation)
            .Bc(objectId1).Bc(objectId2).Bc(objectId3)
            .U8(NoSkillObject).U8(NoInputDirection)
            .ToPacket(CombatOpcodes.CSStartSkill);

    /// <summary>CSStartSkill targeting the caster itself.</summary>
    public static CombatOutboundPacket StartSkillOnSelf(uint skillId, uint casterUnitId) =>
        StartSkillOnUnit(skillId, casterUnitId, casterUnitId);

    /// <summary>
    /// CSStartSkill for an item caster. The fields after the caster type are the original
    /// SkillItem layout: player bc, item u64, item template u32, type1 u8, and type2 u64.
    /// </summary>
    public static CombatOutboundPacket StartItemSkillOnUnit(uint skillId, uint casterUnitId, ulong itemId,
        uint itemTemplateId, byte type1, ulong type2, uint targetUnitId) =>
        new WireWriter().U32(skillId).U8(2).Bc(casterUnitId).U64(itemId).U32(itemTemplateId).U8(type1).U64(type2)
            .U8(UnitTarget).Bc(targetUnitId)
            .U8(NoSkillObject).U8(NoInputDirection)
            .ToPacket(CombatOpcodes.CSStartSkill);

    /// <summary>CSStopCasting: timeline id, plot timeline id, then the owning character's <c>bc</c> id.</summary>
    public static CombatOutboundPacket StopCasting(ushort timelineId, ushort plotTimelineId, uint casterUnitId) =>
        Packet(CombatOpcodes.CSStopCasting,
            new WireWriter().U16(timelineId).U16(plotTimelineId).Bc(casterUnitId));

    /// <summary>Cancel uses the protocol's CSStopCasting request; AAEmu exposes no separate cast-cancel opcode.</summary>
    public static CombatOutboundPacket CancelCast(ushort timelineId, ushort plotTimelineId, uint casterUnitId) =>
        StopCasting(timelineId, plotTimelineId, casterUnitId);

    /// <summary>
    /// Basic auto-attack is a normal CSStartSkill request (AAEmu recognizes skill IDs 2, 3, and 4);
    /// there is no dedicated auto-attack packet.
    /// </summary>
    public static CombatOutboundPacket StartAutoAttack(uint autoAttackSkillId, uint casterUnitId, uint targetUnitId)
    {
        if (autoAttackSkillId is not (2 or 3 or 4))
            throw new ArgumentOutOfRangeException(nameof(autoAttackSkillId), autoAttackSkillId,
                "AAEmu recognizes only basic attack skills 2, 3, and 4 as auto-attacks.");
        return StartSkillOnUnit(autoAttackSkillId, casterUnitId, targetUnitId);
    }

    /// <summary>Stops auto-attack through its active CSStopCasting timeline.</summary>
    public static CombatOutboundPacket StopAutoAttack(ushort timelineId, ushort plotTimelineId, uint casterUnitId) =>
        StopCasting(timelineId, plotTimelineId, casterUnitId);

    /// <summary>CSLearnSkill: u32 skill template id.</summary>
    public static CombatOutboundPacket LearnSkill(uint skillId) =>
        Packet(CombatOpcodes.CSLearnSkill, new WireWriter().U32(skillId));

    /// <summary>CSResetSkills: ability type byte followed by the client ausp boolean.</summary>
    public static CombatOutboundPacket ResetSkills(byte abilityType, bool ausp) =>
        Packet(CombatOpcodes.CSResetSkills, new WireWriter().U8(abilityType).Bool(ausp));

    /// <summary>CSInteractNPC: NPC <c>bc</c> id and whether this interaction changed selection.</summary>
    public static CombatOutboundPacket InteractNpc(uint npcUnitId, bool targetChanged) =>
        Packet(CombatOpcodes.CSInteractNpc, new WireWriter().Bc(npcUnitId).Bool(targetChanged));

    /// <summary>CSInteractNPCEnd ends the current NPC talk/interaction.</summary>
    public static CombatOutboundPacket EndNpcInteraction(uint npcUnitId) =>
        Packet(CombatOpcodes.CSInteractNpcEnd, new WireWriter().Bc(npcUnitId));

    /// <summary>
    /// CSStartInteraction: NPC and picked-object <c>bc</c> ids, then interaction context from
    /// the client pick operation.
    /// </summary>
    public static CombatOutboundPacket StartNpcInteraction(
        uint npcUnitId, uint objectId, int extraInfo, int pickId, sbyte mouseButton, int modifierKeys) =>
        Packet(CombatOpcodes.CSStartInteraction,
            new WireWriter().Bc(npcUnitId).Bc(objectId).S32(extraInfo).S32(pickId)
                .S8(mouseButton).S32(modifierKeys));

    /// <summary>CSSelectInteractionEx: target <c>bc</c>, interactionEx and var1.</summary>
    public static CombatOutboundPacket SelectNpcInteractionOption(uint npcUnitId, int interactionEx, int var1) =>
        Packet(CombatOpcodes.CSSelectInteractionEx,
            new WireWriter().Bc(npcUnitId).S32(interactionEx).S32(var1));

    /// <summary>CSExpressEmotion: expressing character, optional target, and express_texts id.</summary>
    public static CombatOutboundPacket ExpressEmotion(uint characterUnitId, uint targetUnitId, uint emotionId) =>
        Packet(CombatOpcodes.CSExpressEmotion,
            new WireWriter().Bc(characterUnitId).Bc(targetUnitId).U32(emotionId));

    /// <summary>CSCharDetail: request a character sheet by name.</summary>
    public static CombatOutboundPacket RequestCharacterDetail(string characterName)
    {
        ArgumentNullException.ThrowIfNull(characterName);
        return Packet(CombatOpcodes.CSCharDetail, new WireWriter().Str(characterName));
    }

    /// <summary>CSUnlockLearnSkill carries the client's signed 32-bit type selector.</summary>
    public static CombatOutboundPacket UnlockLearnSkill(int typeValue) =>
        Packet(CombatOpcodes.CSUnlockLearnSkill, new WireWriter().S32(typeValue));

    /// <summary>CSResurrectCharacter accepts in-place resurrection when <paramref name="inPlace"/> is true.</summary>
    public static CombatOutboundPacket Resurrect(bool inPlace) =>
        Packet(CombatOpcodes.CSResurrectCharacter, new WireWriter().Bool(inPlace));

    private static WireWriter StartSkillHeader(uint skillId, uint casterUnitId) =>
        new WireWriter().U32(skillId).U8(UnitCaster).Bc(casterUnitId);

    // AAEmu Helpers.ConvertLongX/Y(float): ((long)(coordinate * 4096)) << 32.
    private static long ToSkillPositionX(float x) => ((long)(x * 4096f)) << 32;
    private static long ToSkillPositionY(float y) => ((long)(y * 4096f)) << 32;

    private static CombatOutboundPacket Packet(ushort opcode, WireWriter body) => new(opcode, body.ToArray());

    private static CombatOutboundPacket ToPacket(this WireWriter body, ushort opcode) => Packet(opcode, body);
}
