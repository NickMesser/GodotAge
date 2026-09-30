#nullable enable
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>
/// Live game state and requests needed by the unit, player, skill, ability, action, and hero Lua APIs.
/// The host implements this over the network session. All indices exposed to Lua are one based.
/// </summary>
public partial interface IX2UnitData
{
}

/// <summary>
/// Offline implementation of the unit API data source. It exposes an empty, fresh character state.
/// Static definitions are supplied by the binding methods without requiring a live session.
/// </summary>
public partial class NullUnitData : IX2UnitData
{
    public static readonly NullUnitData Instance = new();
}

/// <summary>Registers the full Unit family on the Lua host.</summary>
public static partial class X2UnitApi
{
    public static void Install(X2LuaHost host, X2GameContext context, IX2UnitData data)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);
        InstallUnit(host, context, data);
        InstallPlayer(host, context, data);
        InstallAbility(host, context, data);
        InstallSkill(host, context, data);
        InstallAction(host, context, data);
    }
}
