#nullable enable
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Strict decoders for 10.0.2.13 server combat packets. Input is the packet body after the two-byte
/// S-to-C opcode. Layout references are AAEmu.Game/Core/Packets/G2C and X2Game.Protocol's generated
/// PacketBodies.g.cs; compound unions follow AAEmu's SkillCaster/SkillCastTarget/CastAction writers.
/// </summary>
public static class CombatPacketParsers
{
    public static IReadOnlyList<GameEvent> Parse(ushort opcode, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (opcode == CombatOpcodes.SCUnitState)
            return ParseUnitStateEvents(body);
        GameEvent? one = opcode switch
        {
            CombatOpcodes.SCUnitPoints => ReadOne(body, ParseUnitPoints),
            CombatOpcodes.SCUnitDeath => ReadOne(body, ParseUnitDeath),
            CombatOpcodes.SCNotifyResurrection => ReadOne(body, ParseResurrectionPrompt),
            CombatOpcodes.SCCharacterResurrected => ReadOne(body, ParseCharacterResurrected),
            CombatOpcodes.SCExpChanged => ReadOne(body, ParseExperience),
            CombatOpcodes.SCRecoverableExp => ReadOne(body, r =>
                new RecoverableExperienceChangedEvent(r.Bc(), r.S32(), r.S32(), r.S32())),
            CombatOpcodes.SCAbilityExpChanged => ReadOne(body, ParseAbilityExperience),
            CombatOpcodes.SCLevelChanged => ReadOne(body, ParseLevel),
            CombatOpcodes.SCSetBreath => ReadOne(body, r => new BreathChangedEvent(r.U32())),
            CombatOpcodes.SCCharDetail => ReadOne(body, ParseCharacterDetail),
            CombatOpcodes.SCBuffCreated => ReadOne(body, ParseBuffCreated),
            CombatOpcodes.SCBuffRemoved => ReadOne(body, ParseBuffRemoved),
            CombatOpcodes.SCBuffUpdated => ReadOne(body, ParseBuffUpdated),
            CombatOpcodes.SCSkillStarted => ReadOne(body, ParseSkillStarted),
            CombatOpcodes.SCSkillFired => ReadOne(body, ParseSkillFired),
            CombatOpcodes.SCSkillEnded => ReadOne(body, r => new SkillEndedEvent(r.U16())),
            CombatOpcodes.SCSkillStopped => ReadOne(body, r => new SkillStoppedEvent(r.Bc(), r.U32())),
            CombatOpcodes.SCUnitDamaged => ReadOne(body, ParseDamage),
            CombatOpcodes.SCUnitHealed => ReadOne(body, ParseHeal),
            CombatOpcodes.SCCombatText => ReadOne(body, r => new CombatTextEvent(r.Bc(), r.Bc(), r.U8())),
            CombatOpcodes.SCEnvDamage => ReadOne(body, ParseEnvironmentDamage),
            CombatOpcodes.SCCooldowns => ReadOne(body, ParseCooldowns),
            CombatOpcodes.SCSkillCooldownReset => ReadOne(body, ParseCooldownReset),
            CombatOpcodes.SCSkillCooldownReduce => ReadOne(body, ParseCooldownReduce),
            CombatOpcodes.SCChargeSkillCooldownChanged => ReadOne(body, ParseChargeCooldown),
            CombatOpcodes.SCTargetChanged => ReadOne(body, r => new TargetChangedEvent(r.Bc(), r.Bc())),
            CombatOpcodes.SCCombatEngaged => ReadOne(body, r => new CombatEngagedEvent(r.Bc(), r.Bc())),
            CombatOpcodes.SCCombatCleared => ReadOne(body, r => new CombatClearedEvent(r.Bc())),
            CombatOpcodes.SCCombatFirstHit => ReadOne(body, r => new CombatFirstHitEvent(r.Bc(), r.Bc(), r.U32())),
            CombatOpcodes.SCForceAttackSet => ReadOne(body, r => new ForceAttackChangedEvent(r.Bc(), r.Bool(), r.Bool())),
            CombatOpcodes.SCCvFCombatRelationship => ReadOne(body, r => ParseRelationships(r, false)),
            CombatOpcodes.SCFvFCombatRelationship => ReadOne(body, r => ParseRelationships(r, true)),
            CombatOpcodes.SCAiAggro => ReadOne(body, ParseAiAggro),
            CombatOpcodes.SCAggroTargetChanged => ReadOne(body, r => new AggroTargetChangedEvent(r.Bc(), r.Bc())),
            CombatOpcodes.SCAggroRemoved => ReadOne(body, r => new AggroRemovedEvent(r.Bc(), r.S8())),
            CombatOpcodes.SCAttackFaction => ReadOne(body, r => new AttackFactionChangedEvent(r.Bc(), r.S8())),
            CombatOpcodes.SCUnitModelPostureChanged => ReadOne(body, ParsePosture),
            CombatOpcodes.SCEmotionExpressed => ReadOne(body, r => new EmotionExpressedEvent(r.Bc(), r.Bc(), r.U32())),
            CombatOpcodes.SCUnitNameChanged => ReadOne(body, r => new UnitNameChangedEvent(r.Bc(), r.Str())),
            CombatOpcodes.SCUnitFactionChanged => ReadOne(body, ParseFaction),
            CombatOpcodes.SCSkillLearned => ReadOne(body, r => new SkillLearnedEvent(r.U32())),
            CombatOpcodes.SCSkillsReset => ReadOne(body, r => new SkillsResetEvent(r.Bc(), r.U8())),
            CombatOpcodes.SCUnlockLearnSkill => ReadOne(body, r => new SkillUnlockEvent(r.S16(), r.U32())),
            CombatOpcodes.SCUpdateAdditionalSkillPoint => ReadOne(body, r => new SkillPointsEvent(r.U32())),
            CombatOpcodes.SCAbilitySwapped => ReadOne(body, ParseAbilitiesSwapped),
            CombatOpcodes.SCAbilitySetAllInfo => ReadOne(body, ParseSkillBook),
            CombatOpcodes.SCAbilitySetSlotCountUpdated => ReadOne(body, r => new AbilitySetSlotCountUpdatedEvent(r.S8())),
            CombatOpcodes.SCListSkillActiveType => ReadOne(body, ParseSkillActiveTypes),
            CombatOpcodes.SCHeirSkillList => ReadOne(body, ParseHeirSkills),
            CombatOpcodes.SCActability => ReadOne(body, ParseActability),
            CombatOpcodes.SCCharacterGamePoints => ReadOne(body, ParseGamePoints),
            CombatOpcodes.SCGamePointChanged => ReadOne(body, ParseGamePointChanges),
            CombatOpcodes.SCAppellations => ReadOne(body, ParseAppellations),
            CombatOpcodes.SCAppellationGained => ReadOne(body, r => new CharacterAppellationGainedEvent(r.U32())),
            CombatOpcodes.SCAppellationChanged => ReadOne(body, ParseAppellationChanged),
            CombatOpcodes.SCGmDumpSubStat => ReadOne(body, ParseCharacterSubStats),
            CombatOpcodes.SCGmServerAttributeValue => ReadOne(body, ParseServerAttributeValues),
            CombatOpcodes.SCCombatResourcePoint => ReadOne(body, ParseCombatResourcePoint),
            CombatOpcodes.SCCombatResourceTransform => ReadOne(body, r =>
                new CombatResourceTransformEvent(r.Bc(), r.S32(), r.Bool())),
            CombatOpcodes.SCCombatResourceUnitMove => ReadOne(body, r =>
                new CombatResourceUnitMoveEvent(r.Bc(), r.S32(), r.Bool())),
            _ => null,
        };
        return one is null ? Array.Empty<GameEvent>() : new[] { one };
    }

    private static T ReadOne<T>(byte[] body, Func<WireReader, T> read) where T : GameEvent
    {
        var r = new WireReader(body);
        var value = read(r);
        End(r);
        return value;
    }

    private static void End(WireReader r)
    {
        if (r.Remaining != 0)
            throw new WireException($"packet parser left {r.Remaining} of {r.Buffer.Length} body bytes");
    }

    private static IReadOnlyList<GameEvent> ParseUnitStateEvents(byte[] body)
    {
        // PacketParsers supplies the shared unit snapshot. This second pass walks the complete variable
        // tail, extracts the active skill lists, and fails if any byte remains unread.
        var unit = PacketParsers.ParseUnitState(body);
        if (unit.DecodeError is not null)
            throw new WireException($"SCUnitState {unit.DecodeError}");
        var (skillList, buffs) = ReadUnitStateSkillList(body);
        return new GameEvent[]
        {
            new UnitStateEvent(new UnitCombatSnapshot(unit, body.ToArray())),
            skillList,
            buffs,
        };
    }

    private static (UnitSkillListEvent Skills, UnitBuffListEvent Buffs) ReadUnitStateSkillList(byte[] body)
    {
        // Mirror the stable prefix in PacketParsers.ParseUnitState through the two pisc skill lists.
        var r = new WireReader(body);
        var unitId = r.Bc();
        r.Str(); r.S8(); r.S8(); r.Bool();
        var kind = (UnitKind)r.U8();
        switch (kind)
        {
            case UnitKind.Character: r.U64(); r.S64(); break;
            case UnitKind.Npc: r.Bc(); r.S32(); r.U64(); r.S8(); break;
            case UnitKind.Slave: r.U64(); r.S16(); r.S32(); r.U64(); r.S8(); break;
            case UnitKind.Housing: r.S16(); r.S32(); r.U16(); break;
            case UnitKind.Transfer: r.S16(); r.S32(); break;
            case UnitKind.Mate: r.S16(); r.S32(); r.U64(); break;
            case UnitKind.Shipyard: r.U64(); r.S32(); break;
            default: throw new WireException($"unknown unit type {(byte)kind}");
        }
        r.Str();
        r.Position(); r.F32(); r.S8(); r.S8(); r.S8(); r.S8();
        r.U8(); r.U8(); r.U8(); r.U8(); r.U32();
        PacketParsers.ReadEquipment(r, kind);
        PacketParsers.ReadAppearance(r);
        r.Bc(); r.U64(); r.U64();
        if (r.S8() != -1) r.Bc();
        if (r.S8() != -1) { r.Bc(); r.S32(); r.S32(); r.U32(); }
        var posture = r.U8();
        r.Bool();
        switch (posture)
        {
            case 1: r.U8(); break;
            case 4: r.U32(); r.Bool(); break;
            case 7: r.U32(); r.F32(); r.U32(); r.U8(); break;
            case 8: r.F32(); r.F32(); break;
        }
        r.S8();
        var skillCount = r.U8();
        var passiveCount = r.U8();
        r.S32(); r.U32(); r.S32(); r.Bool();
        var skills = r.Pisc(skillCount);
        var passives = r.Pisc(passiveCount);
        if (kind == UnitKind.Housing) r.F32();
        else { r.S8(); r.S8(); r.S8(); }
        r.U8(); // raceGender
        r.Pisc(4); r.Pisc(3); r.Pisc(4);

        if (kind == UnitKind.Npc)
        {
            r.U16(); // flags (native NPC shape is zero)
            r.U8();  // attack faction flags
        }
        else
        {
            var flags = r.U16();
            r.U8(); // attack faction flags
            if ((flags & (1 << 8)) != 0) { r.Bc(); r.U32(); }
            if ((flags & (1 << 7)) != 0)
                for (var i = 0; i < 4; i++) r.U16();
            if ((flags & (1 << 6)) != 0)
                for (var i = 0; i < 9; i++) r.S8();
        }

        if (kind == UnitKind.Character)
            SkipCharacterUnitStateTail(r);
        var buffs = ReadUnitStateBuffLists(r);
        End(r);
        return (new UnitSkillListEvent(unitId, skills, passives), new UnitBuffListEvent(unitId, buffs));
    }

    private static void SkipCharacterUnitStateTail(WireReader r)
    {
        for (var i = 0; i < 29; i++) { r.U32(); r.U8(); }
        var activeAbilityCount = r.U8();
        r.Bytes(activeAbilityCount);
        r.Bc(); r.S8(); r.S8();
        r.Bytes(8); // visual options: stp[6], packed flags, cosplay visual
        r.U32(); // premium grade

        var pageCount = r.S32();
        if (pageCount is < 0 or > 3)
            throw new WireException($"Bless Uthstin page count {pageCount} is outside 0..3");
        for (var page = 0; page < pageCount; page++)
            for (var value = 0; value < 7; value++) r.S32();
        r.S32(); r.S32(); r.S32(); // selected page, extended max, apply-extend count

        var slotCount = r.U32();
        if (slotCount > 64) throw new WireException($"reinforce slot count {slotCount} exceeds 64");
        for (var i = 0U; i < slotCount; i++) { r.S32(); r.U8(); r.S32(); }
        var effectCount = r.U32();
        if (effectCount > 64) throw new WireException($"reinforce effect count {effectCount} exceeds 64");
        for (var i = 0U; i < effectCount; i++) { r.U8(); r.S8(); r.U32(); }
    }

    private static IReadOnlyList<CombatBuffState> ReadUnitStateBuffLists(WireReader r)
    {
        var buffs = new List<CombatBuffState>();
        ReadUnitStateBuffList(r, 32, buffs);
        ReadUnitStateBuffList(r, 20, buffs);
        ReadUnitStateBuffList(r, 28, buffs);
        return buffs;
    }

    private static void ReadUnitStateBuffList(WireReader r, int maximum, List<CombatBuffState> buffs)
    {
        var count = r.U8();
        if (count > maximum) throw new WireException($"unit-state buff count {count} exceeds {maximum}");
        for (var i = 0; i < count; i++)
        {
            var index = unchecked((uint)r.S32());
            var source = ReadSkillSource(r);
            r.U64(); r.U8(); r.U16(); // caster database id, level, ability level
            var clock = r.Pisc(4); // duration, elapsed, tick, tick index
            var effect = r.Pisc(4); // buff id, stack, charge, source skill id
            var duration = checked((int)Math.Min(clock[0], int.MaxValue));
            var elapsed = checked((int)Math.Min(clock[1], int.MaxValue));
            buffs.Add(new CombatBuffState(index, effect[0], source.UnitId, effect[1], effect[2],
                duration, Math.Max(0, duration - elapsed), checked((int)Math.Min(clock[2], int.MaxValue)), elapsed));
        }
    }

    private static UnitPointsEvent ParseUnitPoints(WireReader r)
    {
        var unit = r.Bc();
        var hp = checked((int)(r.U64() / 100UL));
        var mp = checked((int)(r.U64() / 100UL));
        return new UnitPointsEvent(unit, hp, mp);
    }

    private static UnitDeathEvent ParseUnitDeath(WireReader r)
    {
        var unit = r.Bc();
        var reason = r.U8();
        var wait = r.U32();
        var specialWait = r.U32();
        var autoWait = r.U32();
        var lost = r.S32();
        var durability = r.U8();
        var killer = r.Bc();
        var name = string.Empty;
        if (killer != 0)
        {
            r.U8(); r.U16(); r.U8(); r.U8(); r.U32();
            name = r.Str();
        }
        return new UnitDeathEvent(unit, reason, wait, specialWait, autoWait, lost, durability, killer, name);
    }

    private static ResurrectionPromptEvent ParseResurrectionPrompt(WireReader r) =>
        new(ReadSkillSource(r));

    private static CharacterResurrectedEvent ParseCharacterResurrected(WireReader r) =>
        new(r.Bc(), r.Position(), r.F32());

    private static ExperienceChangedEvent ParseExperience(WireReader r) =>
        new(r.Bc(), r.S32(), r.Bool());

    private static AbilityExperienceChangedEvent ParseAbilityExperience(WireReader r) =>
        new(r.Bc(), r.S8(), r.S32(), r.Bool());

    private static UnitLevelEvent ParseLevel(WireReader r) => new(r.Bc(), r.U8());

    private static CharacterDetailEvent ParseCharacterDetail(WireReader r)
    {
        var id = r.U64();
        var name = r.Str();
        var race = r.U8();
        var hp = r.U32();
        var level = r.U8();
        var a1 = r.U8(); var a2 = r.U8(); var a3 = r.U8();
        var x = r.U64(); var y = r.U64(); var z = r.F32();
        var zone = r.S32();
        var left = r.S64();
        var equipment = PacketParsers.ReadEquipment(r, UnitKind.Character);
        var exists = r.Bool();
        return new CharacterDetailEvent(id, name, race, hp, level, a1, a2, a3,
            new Vector3(((long)x >> 32) / 4096f, ((long)y >> 32) / 4096f, z), zone, left, equipment, exists);
    }

    private static BuffCreatedEvent ParseBuffCreated(WireReader r)
    {
        var source = ReadSkillSource(r);
        var casterDbId = r.U64();
        var owner = r.Bc();
        var index = r.U32();
        var buff = r.U32();
        var casterLevel = r.U8();
        var ability = r.S16();
        var toggle = r.S32();
        var stacks = r.U32();
        var data = r.Pisc(4);
        return new BuffCreatedEvent(owner, index, buff, source, casterDbId, casterLevel, ability,
            toggle, stacks, data[0], checked((int)data[1] * 10), checked((int)data[3] * 10));
    }

    private static BuffRemovedEvent ParseBuffRemoved(WireReader r) => new(r.Bc(), r.U32());
    private static BuffUpdatedEvent ParseBuffUpdated(WireReader r) =>
        new(r.Bc(), r.S32(), r.U32(), r.U32(), r.S32(), r.U8());

    private static SkillStartedEvent ParseSkillStarted(WireReader r)
    {
        var skill = r.U32();
        var tl = r.U16();
        var caster = ReadSkillSource(r);
        var target = ReadSkillTarget(r);
        var extra = ReadSkillExtra(r);
        var real = checked(r.U16() * 10);
        var baseTime = checked(r.U16() * 10);
        var synergy = r.U8();
        var tail = ReadSkillTail(r);
        return new SkillStartedEvent(skill, tl, caster, target, extra, real, baseTime, synergy, tail);
    }

    private static SkillFiredEvent ParseSkillFired(WireReader r)
    {
        var tl = r.U16();
        var caster = ReadSkillSource(r);
        var target = ReadSkillTarget(r);
        var extra = ReadSkillExtra(r);
        var delay = checked(r.U16() * 10 - 100);
        var channel = checked(r.U16() * 10 - 100);
        var tail = ReadSkillTail(r);
        var ids = r.Pisc(2);
        var flags = r.U8();
        return new SkillFiredEvent(ids[0], tl, caster, target, extra, delay, channel, ids[1], flags, tail);
    }

    private static DamageEvent ParseDamage(WireReader r)
    {
        var action = ReadCastAction(r);
        var source = ReadSkillSource(r);
        var caster = r.Bc();
        var target = r.Bc();
        var crime = r.U8();
        var damage = r.Pisc(2);
        var secondary = r.Pisc(3);
        var holdable = r.U8();
        var elementDamage = r.U32();
        var show = r.Bool();
        var elementType = r.U32();
        var hitFlags = r.U16();
        var flags = r.U8();
        if ((flags & 0x10) != 0)
            throw new WireException("SCUnitDamaged debug block layout is not available");
        var result = r.U8();
        return new DamageEvent(action, source, caster, target, damage[0], damage[1], secondary[2], crime,
            holdable, elementDamage, show, elementType, (CombatHitType)(hitFlags & 0x1F), hitFlags, flags, result);
    }

    private static HealEvent ParseHeal(WireReader r)
    {
        var action = ReadCastAction(r);
        var source = ReadSkillSource(r);
        return new HealEvent(action, source, r.Bc(), r.U8(), r.U8(), r.S64(), r.S64(), r.U8(),
            r.U32(), r.Bool(), r.U32(), r.U8());
    }

    private static EnvironmentDamageEvent ParseEnvironmentDamage(WireReader r)
    {
        var source = r.U8();
        var target = r.Bc();
        var amount = r.U32();
        var start = r.Pos;
        switch (source)
        {
            case 0 or 2: break; // Falling / drowning have no tail.
            case 1: r.U32(); break; // EnvSource.Gimmick
            case 3: r.S64(); r.S64(); r.F32(); r.F32(); r.U8(); break; // collision
            default: throw new WireException($"unknown environment damage source {source}");
        }
        return new EnvironmentDamageEvent(source, target, amount,
            r.Buffer.AsSpan(start, r.Pos - start).ToArray());
    }

    private static CooldownListEvent ParseCooldowns(WireReader r)
    {
        var entries = new List<CooldownEntry>();
        ReadCooldownBucket(r, CooldownKind.Skill, entries);
        ReadCooldownBucket(r, CooldownKind.Tag, entries);
        ReadCooldownBucket(r, CooldownKind.Charge, entries);
        return new CooldownListEvent(entries);
    }

    private static void ReadCooldownBucket(WireReader r, CooldownKind kind, List<CooldownEntry> entries)
    {
        var count = r.U32();
        if (count > 150)
            throw new WireException($"{kind} cooldown count {count} exceeds native maximum 150");
        for (var i = 0U; i < count; i++)
            entries.Add(new CooldownEntry(kind, r.S32(), r.S32(), r.S32()));
    }

    private static CooldownResetEvent ParseCooldownReset(WireReader r) =>
        new(r.Bc(), r.U32(), r.U32(), r.Bool(), r.Bool(), r.Bool(), r.Bool());

    private static CooldownReduceEvent ParseCooldownReduce(WireReader r) =>
        new(r.Bc(), r.S32(), r.S32(), r.U32(), r.U32(), r.U32(), r.Bool(), r.Bool(), r.Bool());

    private static ChargeCooldownChangedEvent ParseChargeCooldown(WireReader r) =>
        new(r.Bc(), r.S32(), r.U32(), r.U32(), r.U32());

    private static CombatRelationshipsEvent ParseRelationships(WireReader r, bool factionVsFaction)
    {
        var count = r.U8();
        var items = new List<CombatRelationship>(count);
        for (var i = 0; i < count; i++)
            items.Add(new CombatRelationship(r.S64(), r.U8(), r.U8()));
        return new CombatRelationshipsEvent(factionVsFaction, items);
    }

    private static AiAggroEvent ParseAiAggro(WireReader r)
    {
        var npc = r.Bc();
        var count = r.U32();
        if (count > 100)
            throw new WireException($"aggro count {count} exceeds native maximum 100");
        var entries = new List<AggroEntry>((int)count);
        for (var i = 0U; i < count; i++)
            entries.Add(new AggroEntry(r.Bc(), r.S32(), r.S32(), r.S32(), r.U8()));
        return new AiAggroEvent(npc, entries);
    }

    private static UnitPostureChangedEvent ParsePosture(WireReader r)
    {
        var unit = r.Bc();
        var type = r.U8();
        var looted = r.Bool();
        var start = r.Pos;
        switch (type)
        {
            case 0: break;
            case 1: r.U8(); break;
            case 4: r.U32(); r.Bool(); break;
            case 7: r.U32(); r.F32(); r.U32(); r.U8(); break;
            case 8: r.F32(); r.F32(); break;
            default: throw new WireException($"unknown model posture type {type}");
        }
        return new UnitPostureChangedEvent(unit, type, looted,
            r.Buffer.AsSpan(start, r.Pos - start).ToArray());
    }

    private static UnitFactionChangedEvent ParseFaction(WireReader r) =>
        new(r.Bc(), r.Str(), r.U32(), r.U32(), r.Bool());

    private static AbilitiesSwappedEvent ParseAbilitiesSwapped(WireReader r)
    {
        var unit = r.Bc();
        var oldAbilities = new byte[3];
        var newAbilities = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            oldAbilities[i] = r.U8();
            newAbilities[i] = r.U8();
        }
        return new AbilitiesSwappedEvent(unit, oldAbilities, newAbilities);
    }

    private static SkillBookUpdatedEvent ParseSkillBook(WireReader r)
    {
        var usedFree = r.U8();
        var slots = new List<SkillBookSlot>(5);
        for (var slot = 0; slot < 5; slot++)
        {
            var skillCount = ReadCount(r, 36, "skill");
            var a1 = r.U8(); var a2 = r.U8(); var a3 = r.U8();
            var skills = ReadU32Values(r, skillCount);
            var passives = ReadU32List(r, 33, "passive buff");
            var heirs = ReadU32List(r, 128, "heir skill");
            slots.Add(new SkillBookSlot(a1, a2, a3, skills, passives, heirs));
        }
        return new SkillBookUpdatedEvent(usedFree, slots);
    }

    private static IReadOnlyList<uint> ReadU32List(WireReader r, int maximum, string label)
    {
        var count = ReadCount(r, maximum, label);
        return ReadU32Values(r, count);
    }

    private static int ReadCount(WireReader r, int maximum, string label)
    {
        var count = r.U32();
        if (count > maximum)
            throw new WireException($"{label} count {count} exceeds maximum {maximum}");
        return (int)count;
    }

    private static IReadOnlyList<uint> ReadU32Values(WireReader r, int count)
    {
        var values = new uint[count];
        for (var i = 0; i < values.Length; i++) values[i] = r.U32();
        return values;
    }

    private static SkillActiveTypesEvent ParseSkillActiveTypes(WireReader r)
    {
        var count = r.U32();
        if (count > 200) throw new WireException($"skill active type count {count} exceeds 200");
        var entries = new List<SkillActiveTypeEntry>((int)count);
        for (var i = 0U; i < count; i++)
            entries.Add(new SkillActiveTypeEntry(r.S32(), r.S32(), r.U8()));
        return new SkillActiveTypesEvent(entries);
    }

    private static HeirSkillListEvent ParseHeirSkills(WireReader r)
    {
        var count = r.U32();
        if (count > 128) throw new WireException($"heir skill count {count} exceeds 128");
        var entries = new List<HeirSkillEntry>((int)count);
        for (var i = 0U; i < count; i++)
            entries.Add(new HeirSkillEntry(r.S32(), r.S32(), r.S32(), r.U32(), r.S8(), r.S8()));
        return new HeirSkillListEvent(entries);
    }

    private static ActabilityEvent ParseActability(WireReader r)
    {
        var last = r.Bool();
        var count = r.U8();
        if (count > 100) throw new WireException($"actability count {count} exceeds 100");
        var entries = new List<ActabilityEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var pair = r.Pisc(2);
            entries.Add(new ActabilityEntry(pair[0], pair[1], r.U8()));
        }
        return new ActabilityEvent(last, entries);
    }

    private static CharacterGamePointsEvent ParseGamePoints(WireReader r)
    {
        var points = new int[14];
        for (var i = 0; i < points.Length; i++) points[i] = r.S32();
        return new CharacterGamePointsEvent(points);
    }

    private static GamePointsChangedEvent ParseGamePointChanges(WireReader r)
    {
        var count = r.U8();
        var changes = new List<GamePointChange>(count);
        for (var i = 0; i < count; i++)
            changes.Add(new GamePointChange(r.U8(), r.S32()));
        return new GamePointsChangedEvent(changes);
    }

    private static CharacterAppellationsEvent ParseAppellations(WireReader r)
    {
        var count = r.U32();
        if (count > 1024) throw new WireException($"appellation count {count} exceeds 1024");
        var entries = new List<(uint Id, bool Selected)>((int)count);
        for (var i = 0; i < count; i++)
            entries.Add((unchecked((uint)r.S32()), r.S8() == 1));
        return new CharacterAppellationsEvent(entries);
    }

    private static CharacterAppellationChangedEvent ParseAppellationChanged(WireReader r)
    {
        var unitId = r.Bc();
        var id = r.U32();
        var stampId = r.U32();
        return new CharacterAppellationChangedEvent(unitId, id, stampId);
    }

    private static CharacterSubStatsEvent ParseCharacterSubStats(WireReader r)
    {
        var name = r.Str();
        var values = new float[46];
        for (var i = 0; i < 42; i++) values[i] = r.F32();
        var maxHp = r.U64();
        var maxMp = r.U64();
        for (var i = 42; i < values.Length; i++) values[i] = r.F32();
        return new CharacterSubStatsEvent(name, values, maxHp, maxMp);
    }

    private static ServerAttributeValuesEvent ParseServerAttributeValues(WireReader r)
    {
        var count = r.U32();
        if (count > 4096) throw new WireException($"server attribute count {count} exceeds 4096");
        var values = new ServerAttributeValue[(int)count];
        for (var i = 0; i < values.Length; i++)
            values[i] = new ServerAttributeValue(r.S8(), r.S8(), r.U64());
        return new ServerAttributeValuesEvent(values);
    }

    private static CombatResourcePointEvent ParseCombatResourcePoint(WireReader r) =>
        new(r.Bc(), r.S32(), r.U64(), r.S32());

    private static SkillSource ReadSkillSource(WireReader r)
    {
        var type = r.U8();
        var unit = r.Bc();
        return type switch
        {
            0 or 1 or 4 => new SkillSource(type, unit),
            2 => new SkillSource(type, unit, r.U64(), r.U32(), r.U8(), r.U64()),
            3 => new SkillSource(type, unit, MountSkillTemplateId: r.U32()),
            _ => throw new WireException($"unknown SkillCaster type {type}"),
        };
    }

    private static SkillTarget ReadSkillTarget(WireReader r)
    {
        var type = r.U8();
        return type switch
        {
            0 => EmptyTarget(type, r.Bc()),
            1 => PositionTarget(type, r, withRotation: true, withObjectIds: true),
            2 => Position2Target(type, r),
            3 => ItemTarget(type, r),
            4 => EmptyTarget(type, r.Bc()),
            5 => PositionTarget(type, r, withRotation: true, withObjectIds: false),
            _ => throw new WireException($"unknown SkillCastTarget type {type}"),
        };
    }

    private static SkillTarget EmptyTarget(byte type, uint unit) =>
        new(type, unit, default, default, default, 0, 0, 0, 0);

    private static SkillTarget PositionTarget(byte type, WireReader r, bool withRotation, bool withObjectIds)
    {
        var pos = ReadLongPosition(r);
        var angle = withRotation ? r.F32() : 0;
        uint unit = 0;
        if (withObjectIds)
        {
            unit = r.Bc();
            r.Bc(); r.Bc();
        }
        return new SkillTarget(type, unit, pos, default, default,
            type == 1 ? angle : 0, 0, 0, 0, type == 5 ? angle : 0);
    }

    private static SkillTarget Position2Target(byte type, WireReader r)
    {
        var pos = ReadLongPosition(r);
        var end = new Vector3(r.F32(), r.F32(), r.F32());
        var normal = new Vector3(r.F32(), r.F32(), r.F32());
        return new SkillTarget(type, 0, pos, end, normal, 0, 0, 0, 0);
    }

    private static SkillTarget ItemTarget(byte type, WireReader r) =>
        new(type, r.Bc(), default, default, default, 0, r.U64(), r.U32(), r.U8());

    private static Vector3 ReadLongPosition(WireReader r) =>
        new((r.S64() >> 32) / 4096f, (r.S64() >> 32) / 4096f, r.F32());

    private static SkillCastExtra ReadSkillExtra(WireReader r)
    {
        var flag = r.U8();
        var type = (byte)(flag & 0x3F);
        var start = r.Pos;
        switch (type)
        {
            case 0: break;
            case 1: r.U8(); r.S32(); r.S64(); r.S64(); r.F32(); r.S32(); break;
            case 2: r.S32(); r.Str(); break;
            case 3: r.Str(); break;
            case 4: r.S64(); r.S64(); r.F32(); break;
            case 5: r.S32(); break;
            case 6: r.Str(); break;
            case 7: r.U64(); r.Bool(); break;
            case 8:
                var bytes = r.U16();
                if ((bytes & 7) != 0) throw new WireException($"skill material block length {bytes} is not u64 aligned");
                r.Bytes(bytes); r.Bool();
                break;
            case 26: r.S32(); break;
            default: throw new WireException($"unsupported SC SkillCastExtra type {type}");
        }
        var payload = r.Buffer.AsSpan(start, r.Pos - start).ToArray();
        return new SkillCastExtra(type, r.U8(), payload);
    }

    private static SkillCastTail ReadSkillTail(WireReader r)
    {
        var flags = r.U8();
        byte result = 0;
        ushort code = 0;
        uint value = 0;
        var resultFlag = true;
        if ((flags & 1) != 0) result = r.U8();
        if ((flags & 2) != 0) code = r.U16();
        if ((flags & 4) != 0) value = r.U32();
        if ((flags & 8) != 0) resultFlag = r.Bool();
        if ((flags & 0xF0) != 0) throw new WireException($"unknown SkillCastTail flags 0x{flags:X2}");
        return new SkillCastTail(flags, result, code, value, resultFlag);
    }

    private static CastActionInfo ReadCastAction(WireReader r)
    {
        var type = r.U8();
        return type switch
        {
            0 => new CastActionInfo(type, r.U32(), r.U16(), 0, 0, 0, 0, 0, false, false),
            1 => ReadPlotAction(type, r),
            2 => new CastActionInfo(type, 0, 0, 0, 0, r.U32(), r.Bc(), r.U32(), r.Bool(), r.Bool()),
            3 => new CastActionInfo(type, 0, 0, (uint)r.S32(), 0, (uint)r.S32(), r.Bc(), 0, false, false),
            4 => new CastActionInfo(type, 0, 0, (uint)r.S32(), 0, 0, r.Bc(), 0, false, false),
            _ => throw new WireException($"unknown CastAction type {type}"),
        };
    }

    private static CastActionInfo ReadPlotAction(byte type, WireReader r)
    {
        var plot = r.U32();
        var tl = r.U16();
        var evt = r.U32();
        var skill = r.U32();
        return new CastActionInfo(type, skill, tl, plot, evt, 0, 0, 0, false, false);
    }
}
