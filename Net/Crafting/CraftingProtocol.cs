#nullable enable

namespace AAEmu.GodotViewer.Net;

public sealed record CraftFailedEvent(int RecipeId, IReadOnlyList<int> FailureTypes) : GameEvent;

/// <summary>Craft execution and result layouts for the 10.0.2.13 game protocol.</summary>
public static class CraftingProtocol
{
    public const ushort CSExecuteCraft = 0x145;
    public const ushort SCCraftFailed = 0x22D;

    /// <summary>AAEmu's current opaque station block is one 24-bit object id.</summary>
    public static byte[] WriteExecute(int recipeId, uint stationObjectId, uint count) =>
        new WireWriter().S32(recipeId).Bc(stationObjectId).U32(count).ToArray();

    /// <summary>Allows a future recovered station-selector block without changing the packet API.</summary>
    public static byte[] WriteExecute(int recipeId, ReadOnlySpan<byte> stationSelector, uint count) =>
        new WireWriter().S32(recipeId).Bytes(stationSelector).U32(count).ToArray();

    public static CraftFailedEvent ParseCraftFailed(byte[] body)
    {
        var r = new WireReader(body);
        var recipeId = r.S32();
        var count = r.U32();
        if (count > 20)
            throw new WireException($"invalid craft failure count {count}");
        var failures = new List<int>((int)count);
        for (var i = 0u; i < count; i++)
            failures.Add(r.S32());
        return new CraftFailedEvent(recipeId, failures);
    }
}
