#nullable enable
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.World;

/// <summary>
/// The live data behind each X2 API family. Anything left null uses the family's Null*Data (a fresh, empty character).
/// </summary>
public sealed class X2FamilyData
{
    public Api.IX2UnitData? Unit { get; set; }
    public IX2SocialData? Social { get; set; }
    public IX2QuestData? Quest { get; set; }
    public IX2ItemsData? Items { get; set; }
    public IX2EconomyData? Economy { get; set; }
    public IX2WorldData? World { get; set; }
    public IX2SystemData? System { get; set; }
    public X2RankRuntime? Rank { get; set; }
    /// <summary>The client game database for static lists; null uses the default location.</summary>
    public string? GameDatabase { get; set; }
}

/// <summary>Installs the live X2 API families on an in-game Lua state.</summary>
public static class X2WorldApis
{
    public static void Install(X2LuaHost host, X2GameContext context, X2FamilyData? data = null)
    {
        data ??= new X2FamilyData();
        X2UnitApi.Install(host, context, data.Unit ?? Api.NullUnitData.Instance);
        X2SocialApi.Install(host, context, data.Social ?? new NullSocialData(), data.GameDatabase);
        X2QuestApi.Install(host, context, data.Quest ?? NullQuestData.Instance);
        X2ItemsApi.Install(host, context, data.Items ?? new NullItemsData());
        X2ItemGuideCatalog.Install(host, data.GameDatabase);
        X2EconomyApi.Install(host, context, data.Economy ?? NullEconomyData.Instance);
        X2InGameShopApi.Install(host, data.Economy ?? NullEconomyData.Instance, data.GameDatabase);
        X2WorldApi.Install(host, context, data.World ?? NullWorldData.Instance);
        X2SystemApi.Install(host, context, data.System ?? new NullSystemData());
        X2DbLists.Install(host, data.GameDatabase);
        X2RankApi.Install(host, data.GameDatabase, data.Rank);
    }
}
