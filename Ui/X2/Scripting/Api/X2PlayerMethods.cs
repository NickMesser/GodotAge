#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Player values sent by the game session. Amounts are raw client units.</summary>
public sealed record X2PlayerState
{
    public int LaborPower { get; init; }
    public int LocalLaborPower { get; init; }
    public int MaxLaborPower { get; init; }
    public int MaxLocalLaborPower { get; init; }
    public int BreathSeconds { get; init; }
    public int JuryPoint { get; init; }
    public int JuryWaitingNumber { get; init; }
    public int HonorPoint { get; init; }
    public int LivingPoint { get; init; }
    public int LeadershipPoint { get; init; }
    public int VocationPoint { get; init; }
    public long LoyaltyPoints { get; init; }
    public double ExperiencePercent { get; init; }
    public long Experience { get; init; }
    public long ExperienceToNextLevel { get; init; }
    public bool InCombat { get; init; }
    public bool IsRunning { get; init; }
    public bool IsAiming { get; init; }
    public bool UsesAccountLaborPower { get; init; }
    public bool IsInSeamlessZone { get; init; } = true;
    public bool CanChat { get; init; } = true;
    public bool HeirLevelEnabled { get; init; }
    public int MaxHeirLevel { get; init; }
    public int MinHeirLevel { get; init; }
    /// <summary>Server configured level cap. The empty adapter uses the client's level cap of 55.</summary>
    public int LevelLimit { get; init; } = 55;
    /// <summary>Regional/server feature switches received at world entry; false when absent.</summary>
    public IReadOnlyDictionary<string, object?> Features { get; init; } = new Dictionary<string, object?>();
    /// <summary>Additional player UI records or one based lists keyed by the exact X2Player method name.</summary>
    public IReadOnlyDictionary<string, object?> Results { get; init; } = new Dictionary<string, object?>();
}

public partial interface IX2UnitData
{
    /// <summary>The current character state, updated only by server packets.</summary>
    X2PlayerState Player { get; }
    /// <summary>Send a player action to the session. The adapter checks support and server authority.</summary>
    void RequestPlayerAction(string method, params object?[] arguments);
}

public partial class NullUnitData
{
    public virtual X2PlayerState Player { get; } = new() { Features = DefaultFeatures() };

    /// <summary>
    /// X2Player:GetFeatureSet() until the server's feature set is known: the client features a live 10.0 server enables
    /// (the HUD buttons, esc menu entries and character rows gated by ui_content_info_feature_sets and the scripts).
    /// Restrictions (block*, forbid*, restrict*, not*, ...) stay off.
    /// </summary>
    private static Dictionary<string, object?> DefaultFeatures()
    {
        var on = """
            aaPoint account_attendance achievement arche_pass auctionPartialBuy backpackProfitShare bless_uthstin bm_mileage butler
            characterInfoLivingPoint chatRace chronicle_info combatResource equipSlotBundleEffect equipSlotEnchantment
            event_center_content_schedule event_center_event_info event_center_today_assignment expeditionImmigration expeditionLevel
            expeditionRecruit expeditionSummon hero heroBonus hud_mail_box_button hudBattleFieldButton indunDailyLimit indunPortal
            ingamecashshop itemEvolving itemGradeEnchant itemLookConvertInBag itemRepairInBag itemSmelting itemlookExtract lootGacha
            marketPrice mateAggressive mate_type_summon premium ranking rebuildHouse renameExpeditionByItem reportBadUser
            reportBadWordUser reportSpamMail reportSpammer secondpass shopOnUI show_instance_in_hud show_premium_hud siege
            socketChange socketExtract specialtyTradeGoods specialty_trade_info_ui squad survey_form todayAssignment ui_avi
            useCosplayLooksSlot useCraftOrder useForceAttack useHeirSkill use_palos_shop use_web_diary use_web_help
            use_web_messenger use_web_wiki
            """;
        return on.Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToDictionary(k => k, _ => (object?)true);
    }
    public virtual void RequestPlayerAction(string method, params object?[] arguments) { }
}

public static partial class X2UnitApi
{
    private static LuaTable PlayerTable(IReadOnlyDictionary<string, object?>? fields)
    {
        var table = new LuaTable();
        if (fields != null)
            foreach (var (key, value) in fields) table[key] = PlayerValue(value);
        return table;
    }

    private static object? PlayerValue(object? value)
    {
        if (value is IReadOnlyDictionary<string, object?> map) return PlayerTable(map);
        if (value is string || value is null) return value;
        if (value is IEnumerable sequence)
        {
            var table = new LuaTable();
            double i = 1;
            foreach (var item in sequence) table[i++] = PlayerValue(item);
            return table;
        }
        return value;
    }

    private static object? PlayerCall(string method, LuaArgs a, X2GameContext context, IX2UnitData data)
    {
        var p = data.Player;
        if (X2AssignmentPlayerData.TryQuery(method, a.Values,
            (data as NullUnitData)?.SkillDatabasePath, p.Results, out var assignmentValue))
            return PlayerValue(assignmentValue);
        object? Table(string key) => p.Results.TryGetValue(key, out var value) ? PlayerValue(value) : null;
        void Command() => data.RequestPlayerAction(method, a.Values.Select((_, i) => a[i]).ToArray());
        switch (method)
        {
            case "GetFeatureSet":
            {
                var features = PlayerTable(p.Features);
                if (!features.ContainsKey("levelLimit")) features["levelLimit"] = (double)p.LevelLimit;
                return features;
            }
            case "GetUIScreenState": return 6d; // SCREEN_WORLD; this API is installed only for the entered-world UI host.
            case "GetExpInfo": return new LuaMulti((double)p.ExperiencePercent, p.Experience.ToString(System.Globalization.CultureInfo.InvariantCulture), p.ExperienceToNextLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case "GetGlobalLaborPower": return (double)p.LaborPower;
            case "GetBmPoint": return p.LoyaltyPoints.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "GetLocalLaborPower": return (double)p.LocalLaborPower;
            case "GetTotalLaborPower": return (double)(p.LaborPower + p.LocalLaborPower);
            case "GetMaxLaborPower": return (double)p.MaxLaborPower;
            case "GetMaxLocalLaborPower": return (double)p.MaxLocalLaborPower;
            case "GetBreathTime": return (double)p.BreathSeconds;
            case "GetJuryPoint": return (double)p.JuryPoint;
            case "GetJuryWaitingNumber": return (double)p.JuryWaitingNumber;
            case "GetGamePoints": return new LuaTable { ["honorPoint"] = (double)p.HonorPoint, ["honorPointStr"] = p.HonorPoint.ToString(), ["livingPoint"] = (double)p.LivingPoint, ["livingPointStr"] = p.LivingPoint.ToString(), ["leadershipPoint"] = (double)p.LeadershipPoint, ["periodLeadershipPointStr"] = p.LeadershipPoint.ToString(), ["vocationPoint"] = (double)p.VocationPoint };
            case "GetHeirExpInfo": return Table(method) ?? new LuaTable { ["percent"] = "0", ["exp"] = "0", ["totalExp"] = "0", ["level"] = 0d };
            case "GetRecoverableExp":
            {
                if (p.Results.TryGetValue(method, out var value) && value is IEnumerable values)
                    return new LuaMulti(values.Cast<object?>().ToArray());
                return new LuaMulti(0d, 0d);
            }
            case "GetZonePermissionCondition": return Table(method) ?? new LuaTable { ["permission"] = 0d, ["inZone"] = false, ["waitName"] = "" };
            case "PlayerInCombat": return p.InCombat || (context.Units.Get(context.Units.PlayerId)?.InCombat ?? false);
            case "IsRuning": return p.IsRunning;
            case "IsPlayerAimming": return p.IsAiming;
            case "IsUsingAccountLaborPower": return p.UsesAccountLaborPower;
            case "IsUsingLocalLaborPower": return !p.UsesAccountLaborPower;
            case "IsInSeamlessZone": return p.IsInSeamlessZone;
            case "IsEnableChat": return p.CanChat;
            case "IsEnabledHeirLevel": return p.HeirLevelEnabled;
            case "GetMaxHeirLevel": return (double)p.MaxHeirLevel;
            case "GetMinHeirLevel": return (double)p.MinHeirLevel;
            case "GetRechargedLaborPowerInfo": return Table(method) ?? new LuaTable();
            case "GetBackCarryingOrderKeys":
            case "GetScheduleItemList":
            case "GetULCList":
            case "GetUnitAppellationRouteList":
            case "GetAppellationStampInfos":
            case "GetAppellations": return Table(method) ?? new LuaTable();
            case "IsActiveReopenRandomBox": return Table(method) ?? new LuaTable { ["isActive"] = false, ["isBlocked"] = false }; // timer.lua reads .isActive/.isBlocked
            case "GetCrimeInfo": return Table(method) ?? new LuaTable { ["crimePoint"] = 0d, ["crimeRecord"] = 0d };
            case "GetPayLocation":
            case "GetPayMethod":
            case "GetPremiumItemReceiveCharacterName":
            case "GetReopenRandomBoxName": return "";
            case "GetAppellationCount": return Table(method) ?? 0d;
            case "GetAppellationsCount":
            case "GetAaCoin":
            case "GetForceAttackLimitLevel":
            case "GetInstantTime":
            case "GetOfflineLaborPowerChargeAmount":
            case "GetOnlineLaborPowerChargeAmount":
            case "GetPremiumServiceOfflineLaborPower":
            case "GetPremiumServiceOnlineLaborPower":
            case "GetProtectPvpLevel":
            case "GetReopenRandomBoxLifeTime":
            case "GetSensitiveOperationTime":
            case "GetServerOpenTime":
            case "GetWorldLevel": return 0d;
            case "GetUseULC": return false;
            case "HasAccountBuffUsingSpecialityConfig":
            case "HasActiveLimitedAuction":
            case "IsAccountRestrictState":
            case "IsCharTransform":
            case "IsForeigner":
            case "IsInIndunWithGraveyard":
            case "IsInvisibleCosplay":
            case "IsReturnAccount":
            // the 10.0.2.13 client recovers local labor on the tick (dump: IS_TICK_RECOVER_LOCAL_LABOR_POWER = true), so the
            // HUD reads "Labor <total> (<local> + <account>)"
            case "IsTickRecoverLocalLaborPower": return true;
            case "IsWorldLevelEnabled":
            case "UseSteam": return false;
            case "HasSlaveUnit":
            case "IsBoundSlave": return Table(method) is true;
            case "AnswerBotCheck":
            case "AskToggleForceAttack":
            case "BackCarryingOrders":
            case "CancelSensitiveOperationVerify":
            case "ChangeAppellation":
            case "ExchangeCashToAAPoint":
            case "OpenUrl":
            case "OpenZonePermissionOutWindow":
            case "OpenZonePermissionWaitWindow":
            case "OpendTutorialWindow":
            case "PlayCinema":
            case "PlayCinemaByQuestType":
            case "PlotAuctionExit":
            case "PlotAuctionPlaceBid":
            case "PlotAuctionQueryInfo":
            case "ReceiveReopenRandomBoxItem":
            case "RefreshBotCheckInfo":
            case "RefreshReopenRandomBox":
            case "RequestBuyAAPoint":
            case "RequestCashCharge":
            case "RequestHelp":
            case "RequestHome":
            case "RequestRefreshCash":
            case "RequestTicket":
            case "RequestULCBuy":
            case "Resurrect":
            case "SensitiveOperationVerify":
            case "SetAppellationStamp":
            case "SetGameSchedule":
            case "SetSensitiveOperationTime":
            case "SetSpecialty":
            case "ShowBackHoldable":
            case "ShowBackPackWithCosplay":
            case "ShowCosplay":
            case "TakeReturnAccountItem":
            case "TakeScheduleItem":
            case "ToggleForceAttack":
            case "UpdateRegistFavoriteList":
            case "UpdateZoneScoreContentState": Command(); return null;
            default: return Table(method); // Optional server feature with no active state returns nil.
        }
    }

    internal static void InstallPlayer(X2LuaHost host, X2GameContext context, IX2UnitData data)
    {
        // Every binding is registered explicitly below. The closed switch above keeps unsupported optional services nil.
        host.Define("X2Player", "AnswerBotCheck", a => PlayerCall("AnswerBotCheck", a, context, data));
        host.Define("X2Player", "AskToggleForceAttack", a => PlayerCall("AskToggleForceAttack", a, context, data));
        host.Define("X2Player", "BackCarryingOrders", a => PlayerCall("BackCarryingOrders", a, context, data));
        host.Define("X2Player", "CancelSensitiveOperationVerify", a => PlayerCall("CancelSensitiveOperationVerify", a, context, data));
        host.Define("X2Player", "ChangeAppellation", a => PlayerCall("ChangeAppellation", a, context, data));
        host.Define("X2Player", "CheckEventScheduleState", a => PlayerCall("CheckEventScheduleState", a, context, data));
        host.Define("X2Player", "ExchangeCashToAAPoint", a => PlayerCall("ExchangeCashToAAPoint", a, context, data));
        host.Define("X2Player", "GetAaCoin", a => PlayerCall("GetAaCoin", a, context, data));
        host.Define("X2Player", "GetAccountRestrictInfo", a => PlayerCall("GetAccountRestrictInfo", a, context, data));
        host.Define("X2Player", "GetAppellationBuffInfoByLevels", a => PlayerCall("GetAppellationBuffInfoByLevels", a, context, data));
        host.Define("X2Player", "GetAppellationChangeItemInfo", a => PlayerCall("GetAppellationChangeItemInfo", a, context, data));
        host.Define("X2Player", "GetAppellationCount", a => PlayerCall("GetAppellationCount", a, context, data));
        host.Define("X2Player", "GetAppellationMyLevelInfo", a => PlayerCall("GetAppellationMyLevelInfo", a, context, data));
        host.Define("X2Player", "GetAppellationMyStamp", a => PlayerCall("GetAppellationMyStamp", a, context, data));
        host.Define("X2Player", "GetAppellationRouteInfo", a => PlayerCall("GetAppellationRouteInfo", a, context, data));
        host.Define("X2Player", "GetAppellationStampInfo", a => PlayerCall("GetAppellationStampInfo", a, context, data));
        host.Define("X2Player", "GetAppellationStampInfos", a => PlayerCall("GetAppellationStampInfos", a, context, data));
        host.Define("X2Player", "GetAppellations", a => PlayerCall("GetAppellations", a, context, data));
        host.Define("X2Player", "GetAppellationsCount", a => PlayerCall("GetAppellationsCount", a, context, data));
        host.Define("X2Player", "GetBackCarryingOrderKeys", a => PlayerCall("GetBackCarryingOrderKeys", a, context, data));
        host.Define("X2Player", "GetBmPoint", a => PlayerCall("GetBmPoint", a, context, data));
        host.Define("X2Player", "GetBreathTime", a => PlayerCall("GetBreathTime", a, context, data));
        host.Define("X2Player", "GetCharacterPrivacyStatus", a => PlayerCall("GetCharacterPrivacyStatus", a, context, data));
        host.Define("X2Player", "GetCrimeInfo", a => PlayerCall("GetCrimeInfo", a, context, data));
        host.Define("X2Player", "GetDefaultLaborPowerTic", a => PlayerCall("GetDefaultLaborPowerTic", a, context, data));
        host.Define("X2Player", "GetEffectAppellation", a => PlayerCall("GetEffectAppellation", a, context, data));
        host.Define("X2Player", "GetExchangeRatio", a => PlayerCall("GetExchangeRatio", a, context, data));
        host.Define("X2Player", "GetExpInfo", a => PlayerCall("GetExpInfo", a, context, data));
        host.Define("X2Player", "GetFeatureSet", a => PlayerCall("GetFeatureSet", a, context, data));
        host.Define("X2Player", "GetForceAttackLimitLevel", a => PlayerCall("GetForceAttackLimitLevel", a, context, data));
        host.Define("X2Player", "GetGamePoints", a => PlayerCall("GetGamePoints", a, context, data));
        host.Define("X2Player", "GetGlobalLaborPower", a => PlayerCall("GetGlobalLaborPower", a, context, data));
        host.Define("X2Player", "GetHeirExpInfo", a => PlayerCall("GetHeirExpInfo", a, context, data));
        host.Define("X2Player", "GetInstantTime", a => PlayerCall("GetInstantTime", a, context, data));
        host.Define("X2Player", "GetJuryPoint", a => PlayerCall("GetJuryPoint", a, context, data));
        host.Define("X2Player", "GetJuryWaitingNumber", a => PlayerCall("GetJuryWaitingNumber", a, context, data));
        host.Define("X2Player", "GetLocalLaborPower", a => PlayerCall("GetLocalLaborPower", a, context, data));
        host.Define("X2Player", "GetMaxHeirLevel", a => PlayerCall("GetMaxHeirLevel", a, context, data));
        host.Define("X2Player", "GetMaxLaborPower", a => PlayerCall("GetMaxLaborPower", a, context, data));
        host.Define("X2Player", "GetMaxLocalLaborPower", a => PlayerCall("GetMaxLocalLaborPower", a, context, data));
        host.Define("X2Player", "GetMinHeirLevel", a => PlayerCall("GetMinHeirLevel", a, context, data));
        host.Define("X2Player", "GetMyCash", a => PlayerCall("GetMyCash", a, context, data));
        host.Define("X2Player", "GetOfflineLaborPowerChargeAmount", a => PlayerCall("GetOfflineLaborPowerChargeAmount", a, context, data));
        host.Define("X2Player", "GetOnlineLaborPowerChargeAmount", a => PlayerCall("GetOnlineLaborPowerChargeAmount", a, context, data));
        host.Define("X2Player", "GetPayLocation", a => PlayerCall("GetPayLocation", a, context, data));
        host.Define("X2Player", "GetPayMethod", a => PlayerCall("GetPayMethod", a, context, data));
        host.Define("X2Player", "GetPcbangLaborPowerTic", a => PlayerCall("GetPcbangLaborPowerTic", a, context, data));
        host.Define("X2Player", "GetPlotAuctionBidData", a => PlayerCall("GetPlotAuctionBidData", a, context, data));
        host.Define("X2Player", "GetPlotAuctionConfigList", a => PlayerCall("GetPlotAuctionConfigList", a, context, data));
        host.Define("X2Player", "GetPlotAuctionInfoList", a => PlayerCall("GetPlotAuctionInfoList", a, context, data));
        host.Define("X2Player", "GetPlotAuctionPhase", a => PlayerCall("GetPlotAuctionPhase", a, context, data));
        host.Define("X2Player", "GetPremiumItemReceiveCharacterName", a => PlayerCall("GetPremiumItemReceiveCharacterName", a, context, data));
        host.Define("X2Player", "GetPremiumServiceOfflineLaborPower", a => PlayerCall("GetPremiumServiceOfflineLaborPower", a, context, data));
        host.Define("X2Player", "GetPremiumServiceOnlineLaborPower", a => PlayerCall("GetPremiumServiceOnlineLaborPower", a, context, data));
        host.Define("X2Player", "GetProtectPvpLevel", a => PlayerCall("GetProtectPvpLevel", a, context, data));
        host.Define("X2Player", "GetRechargedLaborPowerInfo", a => PlayerCall("GetRechargedLaborPowerInfo", a, context, data));
        host.Define("X2Player", "GetRecoverableExp", a => PlayerCall("GetRecoverableExp", a, context, data));
        host.Define("X2Player", "GetReopenRandomBoxFavoriteState", a => PlayerCall("GetReopenRandomBoxFavoriteState", a, context, data));
        host.Define("X2Player", "GetReopenRandomBoxGroupRateInfo", a => PlayerCall("GetReopenRandomBoxGroupRateInfo", a, context, data));
        host.Define("X2Player", "GetReopenRandomBoxInfo", a => PlayerCall("GetReopenRandomBoxInfo", a, context, data));
        host.Define("X2Player", "GetReopenRandomBoxLifeTime", a => PlayerCall("GetReopenRandomBoxLifeTime", a, context, data));
        host.Define("X2Player", "GetReopenRandomBoxName", a => PlayerCall("GetReopenRandomBoxName", a, context, data));
        host.Define("X2Player", "GetResurrectionInfo", a => PlayerCall("GetResurrectionInfo", a, context, data));
        host.Define("X2Player", "GetReturnAccountItemType", a => PlayerCall("GetReturnAccountItemType", a, context, data));
        host.Define("X2Player", "GetScheduleItemInfo", a => PlayerCall("GetScheduleItemInfo", a, context, data));
        host.Define("X2Player", "GetScheduleItemList", a => PlayerCall("GetScheduleItemList", a, context, data));
        host.Define("X2Player", "GetSensitiveOperationTime", a => PlayerCall("GetSensitiveOperationTime", a, context, data));
        host.Define("X2Player", "GetServerOpenTime", a => PlayerCall("GetServerOpenTime", a, context, data));
        host.Define("X2Player", "GetShowingAppellation", a => PlayerCall("GetShowingAppellation", a, context, data));
        host.Define("X2Player", "GetStampChangeItemInfo", a => PlayerCall("GetStampChangeItemInfo", a, context, data));
        host.Define("X2Player", "GetTotalLaborPower", a => PlayerCall("GetTotalLaborPower", a, context, data));
        host.Define("X2Player", "GetUIScreenState", a => PlayerCall("GetUIScreenState", a, context, data));
        host.Define("X2Player", "GetULCGuideInfo", a => PlayerCall("GetULCGuideInfo", a, context, data));
        host.Define("X2Player", "GetULCInfo", a => PlayerCall("GetULCInfo", a, context, data));
        host.Define("X2Player", "GetULCList", a => PlayerCall("GetULCList", a, context, data));
        host.Define("X2Player", "GetUnitAppellationRouteList", a => PlayerCall("GetUnitAppellationRouteList", a, context, data));
        host.Define("X2Player", "GetUseULC", a => PlayerCall("GetUseULC", a, context, data));
        host.Define("X2Player", "GetWorldLevel", a => PlayerCall("GetWorldLevel", a, context, data));
        host.Define("X2Player", "GetWorldLevelCanGetMainQuest", a => PlayerCall("GetWorldLevelCanGetMainQuest", a, context, data));
        host.Define("X2Player", "GetWorldLevelExpModifier", a => PlayerCall("GetWorldLevelExpModifier", a, context, data));
        host.Define("X2Player", "GetWorldLevelExpModifierRange", a => PlayerCall("GetWorldLevelExpModifierRange", a, context, data));
        host.Define("X2Player", "GetWorldLevelHardCapInfo", a => PlayerCall("GetWorldLevelHardCapInfo", a, context, data));
        host.Define("X2Player", "GetWorldLevelMainQuestAllowance", a => PlayerCall("GetWorldLevelMainQuestAllowance", a, context, data));
        host.Define("X2Player", "GetWorldLevelQuestRestrictionInfo", a => PlayerCall("GetWorldLevelQuestRestrictionInfo", a, context, data));
        host.Define("X2Player", "GetZonePermissionCondition", a => PlayerCall("GetZonePermissionCondition", a, context, data));
        host.Define("X2Player", "GetZoneScore", a => PlayerCall("GetZoneScore", a, context, data));
        host.Define("X2Player", "GetZoneScoreContents", a => PlayerCall("GetZoneScoreContents", a, context, data));
        host.Define("X2Player", "HasAccountBuffUsingSpecialityConfig", a => PlayerCall("HasAccountBuffUsingSpecialityConfig", a, context, data));
        host.Define("X2Player", "HasActiveLimitedAuction", a => PlayerCall("HasActiveLimitedAuction", a, context, data));
        host.Define("X2Player", "HasSlaveUnit", a => PlayerCall("HasSlaveUnit", a, context, data));
        host.Define("X2Player", "IsAccountRestrictState", a => PlayerCall("IsAccountRestrictState", a, context, data));
        host.Define("X2Player", "IsActiveReopenRandomBox", a => PlayerCall("IsActiveReopenRandomBox", a, context, data));
        host.Define("X2Player", "IsBoundSlave", a => PlayerCall("IsBoundSlave", a, context, data));
        host.Define("X2Player", "IsCharTransform", a => PlayerCall("IsCharTransform", a, context, data));
        host.Define("X2Player", "IsEnableChat", a => PlayerCall("IsEnableChat", a, context, data));
        host.Define("X2Player", "IsEnabledHeirLevel", a => PlayerCall("IsEnabledHeirLevel", a, context, data));
        host.Define("X2Player", "IsForeigner", a => PlayerCall("IsForeigner", a, context, data));
        host.Define("X2Player", "IsInIndunWithGraveyard", a => PlayerCall("IsInIndunWithGraveyard", a, context, data));
        host.Define("X2Player", "IsInSeamlessZone", a => PlayerCall("IsInSeamlessZone", a, context, data));
        host.Define("X2Player", "IsInvisibleCosplay", a => PlayerCall("IsInvisibleCosplay", a, context, data));
        host.Define("X2Player", "IsPlayerAimming", a => PlayerCall("IsPlayerAimming", a, context, data));
        host.Define("X2Player", "IsReturnAccount", a => PlayerCall("IsReturnAccount", a, context, data));
        host.Define("X2Player", "IsRuning", a => PlayerCall("IsRuning", a, context, data));
        host.Define("X2Player", "IsTickRecoverLocalLaborPower", a => PlayerCall("IsTickRecoverLocalLaborPower", a, context, data));
        host.Define("X2Player", "IsUsingAccountLaborPower", a => PlayerCall("IsUsingAccountLaborPower", a, context, data));
        host.Define("X2Player", "IsUsingLocalLaborPower", a => PlayerCall("IsUsingLocalLaborPower", a, context, data));
        host.Define("X2Player", "IsWorldLevelEnabled", a => PlayerCall("IsWorldLevelEnabled", a, context, data));
        host.Define("X2Player", "OpenUrl", a => PlayerCall("OpenUrl", a, context, data));
        host.Define("X2Player", "OpenZonePermissionOutWindow", a => PlayerCall("OpenZonePermissionOutWindow", a, context, data));
        host.Define("X2Player", "OpenZonePermissionWaitWindow", a => PlayerCall("OpenZonePermissionWaitWindow", a, context, data));
        host.Define("X2Player", "OpendTutorialWindow", a => PlayerCall("OpendTutorialWindow", a, context, data));
        host.Define("X2Player", "PlayCinema", a => PlayerCall("PlayCinema", a, context, data));
        host.Define("X2Player", "PlayCinemaByQuestType", a => PlayerCall("PlayCinemaByQuestType", a, context, data));
        host.Define("X2Player", "PlayerInCombat", a => PlayerCall("PlayerInCombat", a, context, data));
        host.Define("X2Player", "PlotAuctionExit", a => PlayerCall("PlotAuctionExit", a, context, data));
        host.Define("X2Player", "PlotAuctionPlaceBid", a => PlayerCall("PlotAuctionPlaceBid", a, context, data));
        host.Define("X2Player", "PlotAuctionQueryInfo", a => PlayerCall("PlotAuctionQueryInfo", a, context, data));
        host.Define("X2Player", "ReceiveReopenRandomBoxItem", a => PlayerCall("ReceiveReopenRandomBoxItem", a, context, data));
        host.Define("X2Player", "RefreshBotCheckInfo", a => PlayerCall("RefreshBotCheckInfo", a, context, data));
        host.Define("X2Player", "RefreshReopenRandomBox", a => PlayerCall("RefreshReopenRandomBox", a, context, data));
        host.Define("X2Player", "RequestBuyAAPoint", a => PlayerCall("RequestBuyAAPoint", a, context, data));
        host.Define("X2Player", "RequestCashCharge", a => PlayerCall("RequestCashCharge", a, context, data));
        host.Define("X2Player", "RequestHelp", a => PlayerCall("RequestHelp", a, context, data));
        host.Define("X2Player", "RequestHome", a => PlayerCall("RequestHome", a, context, data));
        host.Define("X2Player", "RequestRefreshCash", a => PlayerCall("RequestRefreshCash", a, context, data));
        host.Define("X2Player", "RequestTicket", a => PlayerCall("RequestTicket", a, context, data));
        host.Define("X2Player", "RequestULCBuy", a => PlayerCall("RequestULCBuy", a, context, data));
        host.Define("X2Player", "Resurrect", a => PlayerCall("Resurrect", a, context, data));
        host.Define("X2Player", "SensitiveOperationVerify", a => PlayerCall("SensitiveOperationVerify", a, context, data));
        host.Define("X2Player", "SetAppellationStamp", a => PlayerCall("SetAppellationStamp", a, context, data));
        host.Define("X2Player", "SetGameSchedule", a => PlayerCall("SetGameSchedule", a, context, data));
        host.Define("X2Player", "SetSensitiveOperationTime", a => PlayerCall("SetSensitiveOperationTime", a, context, data));
        host.Define("X2Player", "SetSpecialty", a => PlayerCall("SetSpecialty", a, context, data));
        host.Define("X2Player", "ShowBackHoldable", a => PlayerCall("ShowBackHoldable", a, context, data));
        host.Define("X2Player", "ShowBackPackWithCosplay", a => PlayerCall("ShowBackPackWithCosplay", a, context, data));
        host.Define("X2Player", "ShowCosplay", a => PlayerCall("ShowCosplay", a, context, data));
        host.Define("X2Player", "TakeReturnAccountItem", a => PlayerCall("TakeReturnAccountItem", a, context, data));
        host.Define("X2Player", "TakeScheduleItem", a => PlayerCall("TakeScheduleItem", a, context, data));
        host.Define("X2Player", "ToggleForceAttack", a => PlayerCall("ToggleForceAttack", a, context, data));
        host.Define("X2Player", "UpdateRegistFavoriteList", a => PlayerCall("UpdateRegistFavoriteList", a, context, data));
        host.Define("X2Player", "UpdateZoneScoreContentState", a => PlayerCall("UpdateZoneScoreContentState", a, context, data));
        host.Define("X2Player", "UseSteam", a => PlayerCall("UseSteam", a, context, data));
    }
}
