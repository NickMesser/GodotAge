#nullable enable
using System.Numerics;

namespace AAEmu.GodotViewer.Net;

/// <summary>
/// Decoders for the S->C bodies this client understands. Each follows the AAEmu server writer named
/// in its comment (the server's packet classes are the layout spec).
/// </summary>
public static class PacketParsers
{
    private const int EquipSlotCount = 34;

    // ---------------------------------------------------------------- items / equipment

    /// <summary>Item.Write: template, id, grade, flags, count, detailType, details, then trailing times.</summary>
    public static EquipmentSlot ReadItem(WireReader r, int slot)
    {
        var templateId = r.U32();
        if (templateId == 0)
            return new EquipmentSlot(slot, 0, 0, 0, 0, 0);
        var id = r.U64();
        var grade = r.U8();
        var flags = r.U8();
        var count = r.S32();
        var detailType = r.U8();
        uint image = 0, dye = 0;
        byte? durability = null;
        ushort? enchantScale = null;
        if (detailType == 1)
        {
            // EquipItem.WriteDetails
            durability = r.U8();
            r.U16();  // chargeCount
            r.S64();  // chargeStartTime
            enchantScale = r.U16();
            r.U16();  // evolveChance
            r.S64();  // chargeProcTime
            r.U8();   // mappingFailBonus
            r.U8();   // elementLevel
            var gems = r.Pisc(18);
            image = gems[0];
            dye = gems[2];
        }
        else
        {
            r.Bytes(DetailBodyLength(detailType));
        }
        var createTime = r.S64();
        var lifespanMins = r.S32();
        var madeUnitId = r.U64();
        var worldId = r.U8();
        var unsecureTime = r.S64();
        var unpackTime = r.S64();
        var chargeUseSkillTime = r.S64();
        return new EquipmentSlot(slot, templateId, id, grade, image, dye,
            flags, count, detailType, durability, enchantScale, createTime,
            lifespanMins, madeUnitId, worldId, unsecureTime, unpackTime, chargeUseSkillTime);
    }

    private static int DetailBodyLength(byte detailType) => detailType switch
    {
        2 => 33,
        3 => 20,
        4 => 9,
        5 or 11 => 24,
        6 or 7 => 16,
        8 or 14 => 8,
        9 => 4,
        10 => 12,
        12 => 10,
        13 => 13,
        _ => 0,
    };

    /// <summary>
    /// EquipmentSerializer.Write: u64 occupancy mask over 34 slots, then each occupied slot in the form the
    /// unit type selects, then (characters only) a u64 per-slot synthesis mask.
    /// </summary>
    public static List<EquipmentSlot> ReadEquipment(WireReader r, UnitKind mode)
    {
        var list = new List<EquipmentSlot>();
        var valid = r.U64();
        for (var i = 0; i < EquipSlotCount; i++)
        {
            if ((valid & (1UL << i)) == 0)
                continue;
            if (i is >= 19 and <= 25 && mode != UnitKind.Slave)
            {
                list.Add(new EquipmentSlot(i, r.U32(), 0, 0, 0, 0));
            }
            else if (mode == UnitKind.Npc)
            {
                if (i == 27 || i is >= 31 and <= 33)
                    list.Add(ReadItem(r, i));
                else
                {
                    var t = r.U32();
                    var id = r.U64();
                    var grade = r.U8();
                    list.Add(new EquipmentSlot(i, t, id, grade, 0, 0));
                }
            }
            else if (mode is UnitKind.Character or UnitKind.Slave or UnitKind.Housing or UnitKind.Mate)
            {
                list.Add(ReadItem(r, i));
            }
            // Transfer / Shipyard: no normal-slot form on the wire.
        }
        if (mode == UnitKind.Character)
            r.U64(); // synthesis activation mask
        return list;
    }

    // ---------------------------------------------------------------- appearance

    /// <summary>UnitCustomModelParams (tier byte, then cumulative hair / skin / face sections).</summary>
    public static AppearanceParams ReadAppearance(WireReader r)
    {
        var start = r.Pos;
        var tier = (AppearanceTier)r.U8();
        if (tier < AppearanceTier.Hair)
            return new AppearanceParams { Tier = tier, Raw = Slice(r, start) };

        byte race = r.U8(), gender = r.U8();
        var vrExpire = r.S64();
        byte vRace = r.U8(), vGender = r.U8();
        uint hair = r.U32(), horn = r.U32(), defHair = r.U32(), twoTone = r.U32();
        float tw1 = r.F32(), tw2 = r.F32();
        uint skin = 0, bodyDiffuse = 0, bodyNormal = 0;
        float bodyWeight = 0;
        FaceParams? face = null;
        if (tier >= AppearanceTier.Skin)
        {
            skin = r.U32();
            bodyDiffuse = r.U32();
            bodyNormal = r.U32();
            bodyWeight = r.F32();
        }
        if (tier >= AppearanceTier.Face)
        {
            var movId = r.U32();
            float movW = r.F32(), movS = r.F32(), movR = r.F32();
            short movX = r.S16(), movY = r.S16();
            var decalIds = r.Pisc(6);
            var maps = r.Pisc(3);
            var weights = new float[6];
            for (var i = 0; i < 6; i++)
                weights[i] = r.F32();
            face = new FaceParams
            {
                MovableDecalAssetId = movId, MovableDecalWeight = movW, MovableDecalScale = movS,
                MovableDecalRotate = movR, MovableDecalMoveX = movX, MovableDecalMoveY = movY,
                FixedDecalAssetIds = decalIds, FixedDecalWeights = weights,
                DiffuseMapId = maps[0], NormalMapId = maps[1], EyelashMapId = maps[2],
                NormalMapWeight = r.F32(),
                LipColor = r.U32(), LeftPupilColor = r.U32(), RightPupilColor = r.U32(),
                EyebrowColor = r.U32(), DecoColor = r.U32(),
                Modifier = r.Blob(),
            };
        }
        return new AppearanceParams
        {
            Tier = tier, Raw = Slice(r, start),
            Race = race, Gender = gender, VisualRaceExpiredTime = vrExpire, VisualRace = vRace, VisualGender = vGender,
            HairColor = hair, HornColor = horn, DefaultHairColor = defHair, TwoToneHairColor = twoTone,
            TwoToneFirstWidth = tw1, TwoToneSecondWidth = tw2,
            SkinColor = skin, BodyDiffuseMap = bodyDiffuse, BodyNormalMap = bodyNormal, BodyWeight = bodyWeight,
            Face = face,
        };
    }

    private static byte[] Slice(WireReader r, int start) => r.Buffer.AsSpan(start, r.Pos - start).ToArray();

    // ---------------------------------------------------------------- SCUnitState

    /// <summary>
    /// SCUnitState (0x097), UnitStateWireSerializer: identity, placement, equipment, appearance, relation
    /// (target, hp, mp, attachment, bonding, posture), then gameplay up to heading and raceGender. The
    /// packed state, flags, character tail and buffs after that are kept only in <see cref="UnitSnapshot.RawBody"/>.
    /// </summary>
    public static UnitSnapshot ParseUnitState(byte[] body)
    {
        var r = new WireReader(body);
        uint unitId = 0, templateId = 0, modelId = 0;
        ulong dbId = 0, ownerId = 0, hp = 0, mp = 0;
        string name = "", master = "";
        var kind = UnitKind.Unknown;
        Vector3 pos = default;
        float scale = 1f, yaw = 0f;
        var hasYaw = false;
        sbyte level = 0;
        byte raceGender = 0;
        uint attachedToUnitId = 0;
        short? timelineId = null;
        sbyte attachedPoint = -1;
        List<EquipmentSlot> equipment = [];
        AppearanceParams? appearance = null;
        string? error = null;
        var section = "identity";
        try
        {
            unitId = r.Bc();
            name = r.Str();
            r.S8(); // worldId
            r.S8(); // regionId
            r.Bool(); // isInGlobalWorld
            kind = (UnitKind)r.U8();
            switch (kind)
            {
                case UnitKind.Character:
                    dbId = r.U64();
                    r.S64();
                    break;
                case UnitKind.Npc:
                    r.Bc();
                    templateId = unchecked((uint)r.S32());
                    ownerId = r.U64();
                    r.S8();
                    break;
                case UnitKind.Slave:
                    dbId = r.U64();
                    timelineId = r.S16();
                    templateId = unchecked((uint)r.S32());
                    ownerId = r.U64();
                    r.S8();
                    break;
                case UnitKind.Housing:
                    r.S16();
                    templateId = unchecked((uint)r.S32());
                    r.U16();
                    break;
                case UnitKind.Transfer:
                    r.S16();
                    templateId = unchecked((uint)r.S32());
                    break;
                case UnitKind.Mate:
                    timelineId = r.S16();
                    templateId = unchecked((uint)r.S32());
                    ownerId = r.U64();
                    break;
                case UnitKind.Shipyard:
                    dbId = r.U64();
                    templateId = unchecked((uint)r.S32());
                    break;
                default:
                    throw new WireException($"unknown unit type {(byte)kind}");
            }
            master = r.Str();

            section = "placement";
            pos = r.Position();
            scale = r.F32();
            level = r.S8();
            r.S8(); // heirLevel
            r.S8(); r.S8(); // level block
            r.U8(); r.U8(); r.U8(); r.U8(); // slot selectors
            modelId = r.U32();

            section = "equipment";
            equipment = ReadEquipment(r, kind);

            section = "appearance";
            appearance = ReadAppearance(r);

            section = "relation";
            r.Bc(); // target
            hp = r.U64();
            mp = r.U64();
            attachedPoint = r.S8();
            if (attachedPoint != -1)
                attachedToUnitId = r.Bc();
            if (r.S8() != -1)
            {
                r.Bc(); r.S32(); r.S32(); r.U32(); // bonding doodad, space, spot, kind
            }
            var posture = r.U8();
            r.Bool(); // isLooted
            switch (posture)
            {
                case 1: r.U8(); break;
                case 4: r.U32(); r.Bool(); break;
                case 7: r.U32(); r.F32(); r.U32(); r.U8(); break;
                case 8: r.F32(); r.F32(); break;
            }

            section = "gameplay";
            r.S8(); // active weapon
            var skillCount = r.U8();
            var passiveCount = r.U8();
            r.S32(); // unit state type
            r.U32(); // appellation stamp
            r.S32(); // vehicle dyeing
            r.Bool(); // temp faction
            r.Pisc(skillCount);
            r.Pisc(passiveCount);
            if (kind == UnitKind.Housing)
            {
                yaw = r.F32();
            }
            else
            {
                r.S8(); r.S8();
                yaw = WorldCoords.HeadingToYaw(r.S8());
            }
            hasYaw = true;
            raceGender = r.U8();
        }
        catch (WireException e)
        {
            error = $"{section}: {e.Message}";
        }

        return new UnitSnapshot
        {
            UnitId = unitId, Kind = kind, Name = name, TemplateId = templateId, DbId = dbId, OwnerId = ownerId,
            TimelineId = timelineId,
            MasterName = master, ModelId = modelId, Position = pos, Yaw = yaw, HasYaw = hasYaw, Scale = scale,
            Level = level, Hp = hp / 100, Mp = mp / 100, RaceGender = raceGender, Equipment = equipment,
            Appearance = appearance, AttachedToUnitId = attachedToUnitId, AttachedPoint = attachedPoint,
            RawBody = body, DecodeError = error,
        };
    }

    // ---------------------------------------------------------------- doodads

    /// <summary>
    /// SCDoodadCreated (0x14E): one Doodad.Write body. The server sends this for doodads bound to a slave (a rowboat's
    /// helm, seat and lantern, a farm wagon's backpack boxes), whose position is relative to the parent slave.
    /// </summary>
    public static UnitSnapshot ParseDoodadCreated(byte[] body)
    {
        var list = new List<UnitSnapshot>();
        var r = new WireReader(body);
        ReadDoodad(r, body, list);
        return list[0];
    }

    /// <summary>SCDoodadRemoved (0x14F): bc id, bool (false deletes the doodad).</summary>
    public static uint ParseDoodadRemoved(byte[] body) => new WireReader(body).Bc();

    /// <summary>SCDoodadsCreated (0x154): u8 count, then Doodad.Write per doodad.</summary>
    public static List<UnitSnapshot> ParseDoodadsCreated(byte[] body)
    {
        var r = new WireReader(body);
        var list = new List<UnitSnapshot>();
        var count = r.U8();
        for (var i = 0; i < count; i++)
            ReadDoodad(r, body, list);
        return list;
    }

    private static void ReadDoodad(WireReader r, byte[] body, List<UnitSnapshot> list)
    {
        {
            var start = r.Pos;
            var id = r.Bc();
            var ids = r.Pisc(4); // template, func group, goods item template, 0
            r.U8(); // flags
            r.Bc(); // owner obj
            var parent = r.Bc();
            var attach = r.U8();
            var pos = r.Position();
            short qx = r.S16(), qy = r.S16(), qz = r.S16();
            var scale = r.F32();
            var ownerId = r.S64();
            r.S64(); // item template id
            r.U32();
            r.U32(); // time left
            r.U64(); // plant time
            r.S32(); // family
            r.S32(); // puzzle group
            r.U8();  // owner type
            r.U32(); // owner db id
            r.S32(); // data
            r.S32(); // data2
            r.U64(); // now
            if (ids[2] != 0)
            {
                r.U64(); r.S64(); r.U16(); // physical goods
            }
            r.S64(); r.S64();
            list.Add(new UnitSnapshot
            {
                UnitId = id, Kind = UnitKind.Doodad, TemplateId = ids[0], PhaseId = ids[1], OwnerId = (ulong)ownerId, Position = pos,
                Yaw = WorldCoords.YawFromShortQuaternion(qx, qy, qz), HasYaw = true, Scale = scale,
                RawBody = body.AsSpan(start, r.Pos - start).ToArray(),
                // Doodad.Write sends parent-local coordinates when attached (attach point > 0 or a parent id).
                ParentUnitId = parent != 0 || (sbyte)attach > 0 ? parent : 0,
                AttachPoint = (sbyte)attach,
            });
        }
    }

    /// <summary>SCDoodadsRemoved (0x155): u16 count, bool last, then (bc id, bool) per doodad.</summary>
    public static List<uint> ParseDoodadsRemoved(byte[] body)
    {
        var r = new WireReader(body);
        var count = r.U16();
        r.Bool();
        var list = new List<uint>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(r.Bc());
            r.Bool();
        }
        return list;
    }

    // ---------------------------------------------------------------- movement

    /// <summary>MoveType body after the type byte (MoveType subclasses' Read).</summary>
    public static UnitMovement ReadMovement(WireReader r, uint unitId, MoveKind kind)
    {
        var time = r.U32();
        var flags = r.U8();
        if ((flags & 0x10) != 0)
        {
            r.U32(); // scType
            r.U8();  // phase
        }
        switch (kind)
        {
            case MoveKind.Unit:
            {
                var pos = r.Position();
                short vx = r.S16(), vy = r.S16(), vz = r.S16();
                r.S8(); r.S8();
                var heading = r.S8();
                sbyte dx = r.S8(), dy = r.S8(), dz = r.S8();
                var stance = r.S8();
                var alert = r.U8();
                var actor = r.U16();
                if ((actor & 0x80) != 0)
                    r.U16();
                if ((actor & 0x20) != 0)
                {
                    r.U8(); r.U16(); r.U16(); r.Position(); r.S8(); r.S8(); r.S8();
                }
                if ((actor & 0x60) != 0)
                    r.U32();
                if ((actor & 0x40) != 0 || (actor & 0x8000) != 0)
                    r.U32();
                if ((actor & 0x100) != 0)
                    r.U32();
                return new UnitMovement
                {
                    UnitId = unitId, Kind = kind, Time = time, Flags = flags, Position = pos,
                    Velocity = WorldCoords.VelocityFromWire(vx, vy, vz), Yaw = WorldCoords.HeadingToYaw(heading),
                    DeltaX = dx, DeltaY = dy, DeltaZ = dz, Stance = stance, Alertness = alert, ActorFlags = actor,
                };
            }
            case MoveKind.Default:
            {
                var pos = r.Position();
                short vx = r.S16(), vy = r.S16(), vz = r.S16();
                r.S16(); r.S16();
                var heading = (sbyte)r.S16();
                return new UnitMovement
                {
                    UnitId = unitId, Kind = kind, Time = time, Flags = flags, Position = pos,
                    Velocity = WorldCoords.VelocityFromWire(vx, vy, vz), Yaw = WorldCoords.HeadingToYaw(heading),
                };
            }
            case MoveKind.Vehicle or MoveKind.Vehicle3 or MoveKind.Ship:
            {
                var pos = r.Position();
                short vx = r.S16(), vy = r.S16(), vz = r.S16();
                short qx = r.S16(), qy = r.S16(), qz = r.S16();
                var angular = new Vector3(r.F32(), r.F32(), r.F32());
                var steering = 0f;
                if (kind == MoveKind.Ship)
                {
                    r.S8(); r.S8(); r.U8(); r.U16(); r.Bool(); // steering, throttle, rpm, zone, stuck
                }
                else
                {
                    steering = r.F32();
                    r.U8(); // throttle
                    var wheels = r.U8();
                    for (var i = 0; i < wheels; i++)
                        r.F32();
                }
                // Both bodies quantize velocity against 30 m/s: the real client's farm wagon cruising at a measured
                // 4.0 m/s (0.44 m per 110 ms packet) reports vel.y = 4369 = 4.0 * 32767 / 30.
                const float scale = 30f;
                return new UnitMovement
                {
                    UnitId = unitId, Kind = kind, Time = time, Flags = flags, Position = pos,
                    Velocity = new Vector3(vx, vy, vz) * (scale / short.MaxValue),
                    Yaw = WorldCoords.YawFromShortQuaternion(qx, qy, qz),
                    Rotation = WorldCoords.QuaternionFromShorts(qx, qy, qz),
                    AngularVelocity = angular, Steering = steering,
                };
            }
            case MoveKind.ShipRequest:
                r.S8(); r.S8();
                return new UnitMovement { UnitId = unitId, Kind = kind, Time = time, Flags = flags, HasPosition = false };
            case MoveKind.Transfer:
            {
                var pos = r.Position();
                short vx = r.S16(), vy = r.S16(), vz = r.S16();
                short qx = r.S16(), qy = r.S16(), qz = r.S16();
                r.F32(); r.F32(); r.F32();
                r.S32(); r.S32(); r.F32(); r.Bool();
                return new UnitMovement
                {
                    UnitId = unitId, Kind = kind, Time = time, Flags = flags, Position = pos,
                    Velocity = new Vector3(vx, vy, vz) * (50f / 32767f),
                    Yaw = WorldCoords.YawFromShortQuaternion(qx, qy, qz),
                };
            }
            default:
                throw new WireException($"unknown move type {(byte)kind}");
        }
    }

    /// <summary>SCUnitMovements (0x099): u16 count, then (bc id, u8 type, body) per unit.</summary>
    public static List<UnitMovement> ParseUnitMovements(byte[] body)
    {
        var r = new WireReader(body);
        var count = r.U16();
        var list = new List<UnitMovement>(count);
        for (var i = 0; i < count; i++)
        {
            var id = r.Bc();
            var kind = (MoveKind)r.U8();
            list.Add(ReadMovement(r, id, kind));
        }
        return list;
    }

    /// <summary>SCOneUnitMovement (0x09A) and the CSMoveUnit body: bc id, u8 type, body.</summary>
    public static UnitMovement ParseOneUnitMovement(byte[] body, int offset = 0)
    {
        var r = new WireReader(body, offset, body.Length - offset);
        var id = r.Bc();
        var kind = (MoveKind)r.U8();
        return ReadMovement(r, id, kind);
    }

    /// <summary>SCUnitsRemoved (0x098): u16 count, then bc ids.</summary>
    public static List<uint> ParseUnitsRemoved(byte[] body)
    {
        var r = new WireReader(body);
        var count = r.U16();
        var list = new List<uint>(count);
        for (var i = 0; i < count; i++)
            list.Add(r.Bc());
        return list;
    }

    // ---------------------------------------------------------------- characters

    /// <summary>Character.WriteLobby1013: the lobby record shared by SCCharacterList and SCCharacterState.</summary>
    public static LobbyCharacter ReadLobbyCharacter(WireReader r)
    {
        var start = r.Pos;
        var id = r.S64();
        var name = r.Str();
        byte race = r.U8(), gender = r.U8(), level = r.U8();
        r.S64(); // heirExp
        uint hp = r.U32(), mp = r.U32(), zone = r.U32(), faction = r.U32();
        var factionName = r.Str();
        var expedition = r.U32();
        r.U32(); // family
        var equipment = ReadEquipment(r, UnitKind.Character);
        r.U8(); r.U8(); r.U8(); // abilities
        var x = r.S64();
        var y = r.S64();
        var z = r.F32();
        var appearance = ReadAppearance(r);
        r.S16();              // deadCount
        r.S64();              // deadTime
        r.U32(); r.U32();     // rezWait, specialRezWait
        r.S64();              // rezTime
        r.U32();              // rezPenaltyDuration
        r.S64();              // lastWorldLeaveTime
        r.S64(); r.S64();     // money, aa point
        r.S16(); r.S32(); r.S16(); // crimePoint, crimeRecord, crimeScore
        r.S64(); r.S64();     // delete/transfer requested
        r.S64();              // created
        r.S64();              // deleteDelay
        r.S64(); r.S64();     // bank money, bank aa point
        r.U8();               // autoUseAAPoint
        r.U32(); r.U32(); r.U32(); // prevPoint, point, gift
        r.S64();              // updated
        r.U8();               // forceNameChange
        r.Blob();             // guid
        r.U32(); r.U32(); r.U32(); // lp, localLp, consumed
        r.S64();              // lp updated
        r.S64();              // bmPoint
        r.U32();              // rechargedLp
        r.S64();              // rechargeResetTime
        return new LobbyCharacter
        {
            Id = (ulong)id, Name = name, Race = race, Gender = gender, Level = level, Hp = hp, Mp = mp, ZoneId = zone,
            FactionId = faction, FactionName = factionName, ExpeditionId = expedition,
            Position = new Vector3((x >> 32) / 4096f, (y >> 32) / 4096f, z),
            Equipment = equipment, Appearance = appearance, Raw = Slice(r, start),
        };
    }

    /// <summary>SCCharacterList (0x069): bool last, u8 count, lobby records.</summary>
    public static (bool Last, List<LobbyCharacter> Characters) ParseCharacterList(byte[] body)
    {
        var r = new WireReader(body);
        var last = r.Bool();
        var count = r.U8();
        var list = new List<LobbyCharacter>(count);
        for (var i = 0; i < count; i++)
            list.Add(ReadLobbyCharacter(r));
        return (last, list);
    }

    /// <summary>SCCharacterState (0x06C): instance id, guid, rwd, srwd, lobby record, angles (rest ignored).</summary>
    public static (uint InstanceId, LobbyCharacter Character, Vector3 Angles) ParseCharacterState(byte[] body)
    {
        var r = new WireReader(body);
        var iid = r.U32();
        r.Blob();
        r.U32(); r.U32();
        var character = ReadLobbyCharacter(r);
        var angles = new Vector3(r.F32(), r.F32(), r.F32());
        // SCCharacterState's in-world tail starts with absolute character experience.
        character = new LobbyCharacter
        {
            Id = character.Id, Name = character.Name, Race = character.Race, Gender = character.Gender,
            Level = character.Level, Hp = character.Hp, Mp = character.Mp, ZoneId = character.ZoneId,
            FactionId = character.FactionId, FactionName = character.FactionName,
            ExpeditionId = character.ExpeditionId, Position = character.Position,
            Equipment = character.Equipment, Appearance = character.Appearance, Raw = character.Raw,
            Experience = r.U32(),
        };
        return (iid, character, angles);
    }

    // ---------------------------------------------------------------- misc

    /// <summary>SCChatMessage (0x102): locale, chat word, sender bc, type, language, race, faction, name, message, ...</summary>
    public static ChatEvent ParseChat(byte[] body)
    {
        var r = new WireReader(body);
        r.U8();
        var chat = r.U64();
        var sender = r.Bc();
        r.S64();
        r.U8(); r.U8(); r.U32();
        var name = r.Str();
        var message = r.Str();
        return new ChatEvent(unchecked((short)(chat & 0xFFFF)), sender, name, message);
    }
}
