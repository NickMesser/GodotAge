#nullable enable
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Live social state and commands supplied by the host's network session.</summary>
public partial interface IX2SocialData { }

/// <summary>Empty social state for a fresh or disconnected character.</summary>
public partial class NullSocialData : IX2SocialData { }

/// <summary>Registers the client's social Lua binding families.</summary>
public static partial class X2SocialApi
{
    public static void Install(X2LuaHost host, X2GameContext context, IX2SocialData data, string? gameDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);
        InstallChat(host, context, data);
        InstallTeam(host, context, data);
        InstallRelations(host, context, data, gameDatabase);
        InstallMail(host, context, data);
    }
}
