#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>A server time-of-day update. Detailed updates also replace the continuous clock bounds and speed.</summary>
public sealed record TimeOfDayEvent(float Hour, float Speed, float Start, float End, bool Detailed) : GameEvent;
public sealed record SnowingEverywhereEvent(bool Enabled) : GameEvent;

/// <summary>
/// Strict receive-only decoders for the environment packets recovered for client 10.0.2.13.
/// <para>
/// Protocol evidence: AAEmu SCOffsets maps TimeOfDay/DetailedTimeOfDay to 0x130/0x131 and its packet writers emit
/// one float or four floats (hour, speed, start, end). X2Game.Protocol's generated bodies agree. Captured line 156
/// (sequence 375) in a recorded packet capture is opcode 304 with body <c>30010f95bb41</c>:
/// the capture includes the little-endian opcode <c>30 01</c>, followed by the hour float <c>0f 95 bb 41</c>.
/// </para>
/// </summary>
public sealed class EnvironmentPacketParserFamily : IPacketParserFamily
{
    public const ushort SCTimeOfDay = 0x130;
    public const ushort SCDetailedTimeOfDay = 0x131;
    public const ushort SCSnowingEverywhere = 0x0E9;

    // Used until a detailed update supplies the native clock speed. This is AAEmu's SCDetailedTimeOfDay default.
    public const float DefaultGameHourSpeed = 0.0016666f;

    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        if (opcode == SCSnowingEverywhere)
        {
            if (body.Length != 1)
                throw new WireException($"environment packet 0x{opcode:X3} has {body.Length} bytes; expected 1");
            events = ParserFamilyResult.One(new SnowingEverywhereEvent(new WireReader(body).Bool()));
            return true;
        }
        if (opcode is not (SCTimeOfDay or SCDetailedTimeOfDay))
        {
            events = [];
            return false;
        }

        var expected = opcode == SCTimeOfDay ? 4 : 16;
        if (body.Length != expected)
            throw new WireException($"environment packet 0x{opcode:X3} has {body.Length} bytes; expected {expected}");

        var reader = new WireReader(body);
        var hour = reader.F32();
        TimeOfDayEvent value;
        if (opcode == SCTimeOfDay)
            value = new TimeOfDayEvent(hour, DefaultGameHourSpeed, 0f, 24f, false);
        else
            value = new TimeOfDayEvent(hour, reader.F32(), reader.F32(), reader.F32(), true);

        if (!float.IsFinite(value.Hour) || !float.IsFinite(value.Speed) || !float.IsFinite(value.Start) ||
            !float.IsFinite(value.End))
            throw new WireException($"environment packet 0x{opcode:X3} contains a non-finite float");

        events = ParserFamilyResult.One(value);
        return true;
    }
}
