#nullable enable

namespace AAEmu.GodotViewer.Net;

/// <summary>One decoder family for otherwise unhandled server-to-client game packets.</summary>
public interface IPacketParserFamily
{
    /// <summary>Returns true when this family owns the opcode, even when its valid result has no events.</summary>
    bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events);
}

/// <summary>Adapter for the strict combat packet decoders already used by the offline protocol checks.</summary>
public sealed class CombatPacketParserFamily : IPacketParserFamily
{
    public bool TryParse(ushort opcode, byte[] body, out IReadOnlyList<GameEvent> events)
    {
        events = CombatPacketParsers.Parse(opcode, body);
        return events.Count != 0;
    }
}
