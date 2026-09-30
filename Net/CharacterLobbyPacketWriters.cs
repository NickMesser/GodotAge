#nullable enable

using AAEmu.GodotViewer.Ui;

namespace AAEmu.GodotViewer.Net;

/// <summary>A complete character-lobby request ready for the game channel.</summary>
public sealed record LobbyOutboundPacket(ushort Opcode, byte[] Body);

/// <summary>Decoded SCDeleteCharacterResponse (0x064).</summary>
public sealed record CharacterDeleteResponse(ulong CharacterId, byte Status, ulong DeleteRequestedTime, ulong DeleteDelay);

/// <summary>Decoded SCCancelCharacterDeleteResponse (0x067).</summary>
public sealed record CharacterDeleteCancelResponse(ulong CharacterId, byte Status);

/// <summary>Writers and response readers for the 10.0.2.13 character-lobby protocol.</summary>
public static class CharacterLobbyPacketWriters
{
    /// <summary>
    /// Builds CSCreateCharacter. The seven body-item ids are slots 19..25 (face through beard),
    /// followed by the cumulative face-tier UnitCustomModelParams block.
    /// </summary>
    public static LobbyOutboundPacket CreateCharacter(CharacterCreateRequest request, byte level = 1,
        uint introZoneId = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Appearance);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Character name must not be empty.", nameof(request));
        if (request.Abilities.Count < 3)
            throw new ArgumentException("Character creation requires three ability ids.", nameof(request));

        var race = RaceId(request.Race);
        var gender = request.Gender.Equals("female", StringComparison.OrdinalIgnoreCase) ? (byte)2 : (byte)1;
        var bodyItems = new uint[7];
        for (var i = 0; i < bodyItems.Length; i++)
            if (request.Equipment.TryGetValue(i + 19, out var item))
                bodyItems[i] = CheckedU32(item, $"equipment slot {i + 19}");

        var appearance = request.Appearance;
        SetIfPresent(bodyItems, 0, appearance.FaceItemId, "face item");
        SetIfPresent(bodyItems, 1, appearance.HairItemId, "hair item");
        SetIfPresent(bodyItems, 4, appearance.TailItemId, "tail item");
        SetIfPresent(bodyItems, 5, appearance.BodyItemId, "body item");
        SetIfPresent(bodyItems, 3, appearance.HornItemId, "horn item");

        var body = new WireWriter().Str(request.Name.Trim()).U8(race).U8(gender);
        foreach (var item in bodyItems)
            body.U32(item);
        WriteAppearance(body, appearance, race, gender);
        body.U8(CheckedU8(request.Abilities[0], "ability 1"))
            .U8(CheckedU8(request.Abilities[1], "ability 2"))
            .U8(CheckedU8(request.Abilities[2], "ability 3"))
            .U8(level).U32(introZoneId);
        return new LobbyOutboundPacket(Opcodes.CSCreateCharacter, body.ToArray());
    }

    /// <summary>CSDeleteCharacter and CSCancelCharacterDelete share a single-u64 body layout.</summary>
    public static LobbyOutboundPacket DeleteCharacter(ulong characterId) =>
        Packet(Opcodes.CSDeleteCharacter, new WireWriter().U64(characterId));

    public static LobbyOutboundPacket CancelCharacterDelete(ulong characterId) =>
        Packet(Opcodes.CSCancelCharacterDelete, new WireWriter().U64(characterId));

    public static CharacterDeleteResponse ReadDeleteResponse(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body.ToArray());
        var value = new CharacterDeleteResponse(r.U64(), r.U8(), r.U64(), r.U64());
        RequireConsumed(r, nameof(ReadDeleteResponse));
        return value;
    }

    public static CharacterDeleteCancelResponse ReadCancelDeleteResponse(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body.ToArray());
        var value = new CharacterDeleteCancelResponse(r.U64(), r.U8());
        RequireConsumed(r, nameof(ReadCancelDeleteResponse));
        return value;
    }

    private static void WriteAppearance(WireWriter w, LoginCharacterAppearance a, byte race, byte gender)
    {
        // The creation UI always supplies the complete face tier (ext=3).
        w.U8(3) // UnitCustomModelType.Face
            .U8(race).U8(gender).S64(0).U8(0).U8(0)
            .U32(CheckedU32(a.HairColorId, "hair color"))
            .U32(CheckedU32(a.HornColorId, "horn color"))
            .U32(a.DefaultHairColor)
            .U32(a.TwoToneHairColor).F32(a.TwoToneFirstWidth).F32(a.TwoToneSecondWidth)
            .U32(CheckedU32(a.SkinColorId, "skin color"))
            .U32(0)
            .U32(CheckedU32(a.BodyNormalMapId, "body normal map"))
            .F32(a.BodyNormalMapWeight)
            .U32(CheckedU32(a.DecalIds.ElementAtOrDefault(0), "movable decal"))
            .F32(a.MovableDecalWeight)
            .F32(a.MovableDecalScale).F32(a.MovableDecalRotate)
            .S16(a.MovableDecalMoveX).S16(a.MovableDecalMoveY);

        WritePisc(w, Enumerable.Range(1, 6)
            .Select(i => CheckedU32(a.DecalIds.ElementAtOrDefault(i), $"fixed decal {i}")));
        WritePisc(w, [0u, CheckedU32(a.FaceNormalMapId, "face normal map"), 0u]);
        for (var i = 1; i <= 6; i++)
            w.F32(a.DecalWeights.ElementAtOrDefault(i));
        w.F32(a.FaceNormalMapWeight)
            .U32(a.LipColor).U32(a.LeftPupilColor).U32(a.RightPupilColor).U32(a.EyebrowColor).U32(a.DecoColor);

        if (a.Modifier.Length > 128)
            throw new ArgumentException("Face modifier cannot exceed 128 bytes.", nameof(a));
        w.U16((ushort)a.Modifier.Length).Bytes(a.Modifier);
    }

    // PacketStream.WritePisc: groups of four values, with 2-bit (encoded byte-count - 1) widths.
    private static void WritePisc(WireWriter w, IEnumerable<uint> source)
    {
        var values = source.ToArray();
        for (var offset = 0; offset < values.Length; offset += 4)
        {
            var count = Math.Min(4, values.Length - offset);
            byte lengths = 0;
            for (var i = 0; i < count; i++)
                lengths |= (byte)((Width(values[offset + i]) - 1) << (2 * i));
            w.U8(lengths);
            for (var i = 0; i < count; i++)
            {
                var value = values[offset + i];
                for (var b = 0; b < Width(value); b++)
                    w.U8((byte)(value >> (8 * b)));
            }
        }
    }

    private static int Width(uint value) => value <= byte.MaxValue ? 1 : value <= ushort.MaxValue ? 2 : value <= 0xFFFFFF ? 3 : 4;

    private static byte RaceId(string race) => race.Trim().ToLowerInvariant() switch
    {
        "nuian" => 1, "fairy" => 2, "dwarf" => 3, "elf" => 4, "hariharan" => 5,
        "ferre" => 6, "returned" => 7, "warborn" => 8, "daru" => 9,
        _ => throw new ArgumentException($"Unknown character race '{race}'.", nameof(race)),
    };

    private static byte CheckedU8(int value, string field) => value is >= 0 and <= byte.MaxValue
        ? (byte)value : throw new ArgumentOutOfRangeException(field, value, "Value must fit in one byte.");

    private static uint CheckedU32(long value, string field) => value is >= 0 and <= uint.MaxValue
        ? (uint)value : throw new ArgumentOutOfRangeException(field, value, "Value must fit in an unsigned 32-bit field.");

    private static void SetIfPresent(uint[] items, int index, long value, string field)
    {
        if (value != 0)
            items[index] = CheckedU32(value, field);
    }

    private static LobbyOutboundPacket Packet(ushort opcode, WireWriter body) => new(opcode, body.ToArray());

    private static void RequireConsumed(WireReader reader, string parser)
    {
        if (reader.Remaining != 0)
            throw new WireException($"{parser} left {reader.Remaining} trailing bytes");
    }
}
