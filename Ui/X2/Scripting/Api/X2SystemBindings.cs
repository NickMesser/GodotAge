#nullable enable
using System.Globalization;
using System.Numerics;
using System.Text;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2;
using AAEmu.GodotViewer.Ui.X2.Scripting;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>Installs the non-gameplay system portion of the native X2 Lua API.</summary>
public static class X2SystemApi
{
    // X2Engine owns these stage-sensitive bindings. Skipping them lets its installed delegates survive regardless of
    // whether this installer is called before or after the world bridge is attached.
    private static readonly HashSet<string> Preserved = new(StringComparer.Ordinal)
    {
        "X2:ConnectToServer", "X2:GetCurrentWorldId", "X2:ReturnToLoginStage", "X2:GetWebWidgetName", "X2:IsWebEnable",
        "X2Util:GetGameProvider", "X2Util:GetVersionInfo", "X2Util:GetRevisionStr", "X2Util:AND", "X2Util:OR",
        "X2Util:XOR", "X2Util:UTF8StringLength", "X2Util:NumberToString", "X2Util:Random", "X2Util:OpenWeb",
        "X2Util:RaiseLuaCallStack", "X2Util:GetFocusedWidgetId",
    };

    /// <summary>
    /// Defines every method in the requested X2ApiData families. The unavailable default pass is intentional: it makes
    /// feature-gated native facilities (GM tools, client-driven dungeons and web integrations) behave as disabled while
    /// typed definitions below replace every locally implementable or host-backed operation.
    /// </summary>
    public static void Install(X2LuaHost host, X2GameContext context, IX2SystemData data)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);
        InstallRegistryDefaults(host);
        InstallX2(host, context, data);
        InstallUtil(host, data);
        InstallOptions(host, data);
        InstallHotkeys(host, data);
        InstallSoundCursorInputCamera(host, data);
        InstallDebugConsoleSecurity(host, context, data);
        InstallMusicSurveyBookScoreboard(host, data);
        InstallGm(host, data);
        InstallTime(host, data);
    }

    private static void InstallRegistryDefaults(X2LuaHost host)
    {
        Defaults(host, "X2",
            "AdjustQuestChatBubbleAutoFireEnd CancelEscape ChatLog ClearCharacterUiData ConnectToServer ConvertUnitId DialogUIContent EnterSystemDungeon ExecuteDynamicAction FastBackwardQuestChat FastBackwardQuestChatByForceSkip FastForwardQuestChat FireAddon GetAimPos GetBodyItemCountByCategory GetCandidatePageStartIdx GetCandidateSelectedIdx GetCandidateSelectedIdxOnCurrentPage GetContent GetDynamicActionSkill GetEscMenuCategories GetFarmGroups GetFeatureset GetHpBarSplitColors GetHudRightIconMenus GetImageTextCoords GetQuestChatBubbleNextSpeech GetRaceCongestions GetRaces GetTargetCombatRelationship GetTargetFactionRelationship GetTipOfDays GetTotalRepairsForPetItems GetTotalRepairsForSlaveItems ImportAPI ImportObject NotifyQuestDirectingModeUpdate RegisterContentTriggerFunc RegisterContentWidget ReloadAddon RepairPetItems RepairSlaveItems RequestEndClientDrivenIndun RequestFindId RequestFindPassword RequestJoin RequestRuntimeCommonFarmDoodadInfo ReturnToLoginStage RollDice SaveAddonInfos SendNewsCastByChat SendOtpNumber SendPcCertNumber SendPlayDiaryByChat SendSecureCardNumber SetAddonEnable SetAutoToggleSlaveEquipmentUiData SetCandidateOnceRetrieveCount SetCharacterUiData SetClipboardText SetCombatResourceUiData SetPortalSortUiData SetQuestContextStateValuesUiData SetQuestNotifierListUiData SetRoadMapUiData SetRoadmapOptionUiData SetWorldmapDefaultExpansionLevel ShowContent StopChatBubble SystemDungeonStateClear ToggleContent UnBoardingTransfer WidgetClosedByEsc",
            "GetAimLevel GetBodyItemCategoryCount GetCandidateCount GetCandidateOnceRetrieveCount GetCurrentWorldId GetDoodadInfoById GetDynamicActionCount GetInstanceIndex GetRuntimeCommonFarmDoodadCount GetWorldmapDefaultExpansionLevel",
            "HasNextQuestChat IsDynamicActionSkillToggled IsEnableSkipClientDriven IsEnteredWorld IsExistFileInAFS IsFirstQuestChat IsInClientDrivenZone IsRepairmanNpc IsStablerNpc IsWebEnable",
            "GetBodyItemName GetSystemDungeonName GetWebWidgetName",
            "GetAddonInfos GetAggroTable GetAutoToggleSlaveEquipmentUiData GetCandidateList GetCharacterUiData GetCombatResourceUiData GetCommonFarmInfo GetFarmGorupInfo GetFarmGroupDoodadList GetPortalSortUiData GetQuestContextStateValuesUiData GetQuestNotifierListUiData GetRoadMapUiData GetRoadmapOptionUiData GetRuntimeCommonFarmDoodadsInfo GetSystemDungeonStateInfo");
        Defaults(host, "X2Util", "ApplyUIMacroString ConvertWorldToScreen DiffTimeTable GetSeamlessOrigin InsertComma MakeAAPointString MakeMoneyString OpenWeb RaiseLuaCallStack UTF8StringLimit", "AND GetCurrencyExchangeFee GetDdcmsTimeOffset GetGameProvider GetGroupMailExchangeFee GetHpPercent OR Random Random2 StrNumericComp UTF8StringLength XOR", "HasEnoughCurrency IsValidName", "CalcCurrencyExchangeFee CalcGroupMailExchangeFee ConvertFormatString DiffTimeStr DivideNumberString ErasePipeArgsFromText GetConstIconPath GetCurrentFileTimeStr GetFocusedWidgetId GetMyAAPointString GetMyBankAAPointString GetMyBankMoneyString GetMyMoneyString GetRevisionStr GetVersionInfo MakeFileTimeStr MakeMarkedCiphersStr MultiplyNumberString NumberToString StrNumericAdd StrNumericMul StrNumericSub", "GetNamePolicyInfo ParsePipeArgsFromText");
        Defaults(host, "X2Option", "CreateOptionItemFloat CreateOptionItemString GetMinxMaxOfMouseSensitivity GetResolution OptimizationEnable RemoveModifiedOption Reset Save SetItemDefaultFloatValue SetItemDefaultFloatValueByName SetItemDefaultStringValue SetItemDefaultStringValueByName SetItemFloatValue SetItemFloatValueByName SetItemFloatValueWithoutModify SetItemStringValue SetItemStringValueByName", "GetNextSysSpecFullValue GetOptionItemValue GetResolutionCount", "GetModifiedRestartOption HasOceanSimulateOption IsPixelSyncSupported", "GetOptionItemValueByName", "EnumAAFormats GetBasicCursorShape GetCursorSize GetHotkeyInfo GetOptionInfo GetSubOptionItemList");
        Defaults(host, "X2Hotkey", "BindingToOption EnableHotkey ExcuteActionHandler GetOptionBinding InitOptionHotKey OptionToBinding RemoveOptionBinding SaveHotKey SetBinding SetBindingButton SetBindingButtonWithIndex SetBindingItem SetBindingItemWithIndex SetBindingSpell SetBindingSpellWithIndex SetBindingWithIndex SetOptionBinding SetOptionBindingButton SetOptionBindingButtonWithIndex SetOptionBindingWithIndex SetTemporaryBindingButton", "", "IsOverridableAction IsValidActionName", "GetBinding GetBindingButton GetBindingSpell GetOptionBindingButton GetTemporaryBindingButton", "");
        Defaults(host, "X2Sound", "PlayMusic SetSiegePeriod StopMusic StopSound", "PlayUISound", "IsPlaying", "", "");
        Defaults(host, "X2Cursor", "ClearCursor GetCursorPickedItemIconInfo", "GetCursorPickedBagItemAmount GetCursorPickedBagItemIndex", "", "GetCursorInfo", "");
        Defaults(host, "X2Input", "GetMousePos SetInputLanguage", "", "IsAltKeyDown IsControlKeyDown IsShiftKeyDown", "GetInputLanguage", "");
        Defaults(host, "X2Camera", "SetUnitCameraAngles ShakeCamera", "", "IsScreenShotCameraMode", "", "");
        Defaults(host, "X2Debug", "GetPlayerUnit ReloadScreen", "GetPlayerId", "GetDevMode IsMaster", "", "GetSavedLocation");
        Defaults(host, "X2Console", "ExecuteString", "", "", "GetAttribute", "");
        Defaults(host, "X2Security", "CancelVaildation ChangeSecondPassword CheckSecondPassword ClearSecondPassword CreateSecondPassword RecommendUsingSecondPassword StartSecondPasswordChange StartSecondPasswordClear StartSecondPasswordCreation StartSecondPasswordWebClear", "GetSecondPasswordFailedCount", "IsSecondPasswordCreated IsSecondPasswordLocked IsSecondPasswordPassed", "", "GetSecondPasswordClearReserveTime GetSecondPasswordUnlockRemainTime GetSecondPasswordUnlockTime");
        Defaults(host, "X2UserMusic", "PlayMusicSheet PrepareToSaveMusicSheet StopMusicSheet TryToSaveMusicSheet", "", "", "", "GetCompositionLimitInfos");
        Defaults(host, "X2SurveyForm", "SendReply", "", "CanSurvey", "", "GetSurveyFormData GetSurveyFormList GetSurveyFormQuestionData");
        Defaults(host, "X2Helper", "", "BitwiseAnd BitwiseOr", "", "", "");
        Defaults(host, "X2Book", "", "", "", "", "GetBookInfo GetPageInfo");
        Defaults(host, "X2NameTag", "SetNameTag", "", "", "", "");
        Defaults(host, "X2MiniScoreboard", "", "", "", "", "GetInfo");
        Defaults(host, "X2Gm", "AddActionPoint AddCash AddExp AddLaborPower AddMoney ApplyInstantGameGmEvent Attach ChangeFaction ChangeMode CheckBotPlayer CheckZone ClearAttribute ClearBuff ClearBuffs DailyResetReputation DebugLooting DelayTaxDueDate DeleteDominion DemolishHouse Detach DumpChar DumpCharBag DumpCharBank DumpCharCompletedQuests DumpCharEquipment DumpCharParty DumpCharQuests DumpGameRule DumpIndun DumpInstantGame EnablePirates EndInstantGame EndInstantGameJoined ExecuteConsoleCommand Freeze GiveNewItem GmOneAndOneChat GoTo InfoInstantApplier InfoInstantField Kick MoveCharRezPoint Notice NoticeEx PlaySequence RecoverDoodad RecoverHouses RemoveAllItems RemoveMate RemoveSlave ResendHouseTaxMail ResetHouses ResetSkillCooldown Resurrect Return ReturnNpc ScheduleSiege SetAttribute SetBattleRecordRating SetBuff SetCongestion SetCrimeValue SetDoodadGrowth SetEmptyBag SetExpFactor SetHealth SetHousePermission SetInstantExclusive SetInstantGmEventMode SetInvisible SetMana SetTradeStatus ShowTradeStatus SpawnDoodad SpawnGimmick SpawnMate SpawnNpc SpawnSlave Summon TowerDefList TowerDefReload TowerDefStart TowerDefStop UnFreeze UpdateHeroScore UpdateLeadership UseSkill WorldGoTo", "", "", "", "ConsoleCommandList LoadBookmarks");
        Defaults(host, "X2Time", "DateToTimeString GetGameTime", "CompareTime GetUiMsec", "", "GetLocalTime", "GetLocalDate GetServerTime PeriodTimeToDate PeriodToDate TimeToDate");

        // Script callsites contain these bindings even though the generated 10.0.2.13 dump omits them.
        Define(host, "X2Cursor", "SetCursorImage", _ => null);
        Define(host, "X2Time", "GetLocalWeek", _ => 0d);
    }

    private static void Defaults(X2LuaHost host, string ns, string none, string number, string boolean, string text, string table)
    {
        DefineMany(host, ns, none, _ => null);
        DefineMany(host, ns, number, _ => 0d);
        DefineMany(host, ns, boolean, _ => false);
        DefineMany(host, ns, text, _ => "");
        DefineMany(host, ns, table, _ => new LuaTable());
    }

    private static void DefineMany(X2LuaHost host, string ns, string names, Func<LuaArgs, object?> function)
    {
        foreach (var name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Define(host, ns, name, function);
    }

    private static void Define(X2LuaHost host, string ns, string name, Func<LuaArgs, object?> function)
    {
        if (!Preserved.Contains(ns + ":" + name)) host.Define(ns, name, function);
    }

    private static void InstallX2(X2LuaHost host, X2GameContext context, IX2SystemData data)
    {
        Define(host, "X2", "IsEnteredWorld", _ => data.IsWorldEntered);
        Define(host, "X2", "GetInstanceIndex", _ => (double)data.InstanceIndex);
        Define(host, "X2", "GetCharacterUiData", a => data.GetCharacterUiData(a.Str(0) ?? ""));
        Define(host, "X2", "SetCharacterUiData", a => { data.SetCharacterUiData(a.Str(0) ?? "", a[1]); return null; });
        Define(host, "X2", "ClearCharacterUiData", a => { data.SetCharacterUiData(a.Str(0) ?? "", null); return null; });
        foreach (var (getter, setter, store) in new[]
        {
            ("GetCombatResourceUiData", "SetCombatResourceUiData", "combatResource"),
            ("GetQuestContextStateValuesUiData", "SetQuestContextStateValuesUiData", "questContext"),
            ("GetQuestNotifierListUiData", "SetQuestNotifierListUiData", "questNotifier"),
            ("GetRoadMapUiData", "SetRoadMapUiData", "roadMap"),
            ("GetRoadmapOptionUiData", "SetRoadmapOptionUiData", "roadMapOption"),
            ("GetAutoToggleSlaveEquipmentUiData", "SetAutoToggleSlaveEquipmentUiData", "slaveEquipment"),
        })
        {
            Define(host, "X2", getter, _ => data.GetUiData(store, "default") ?? (store == "roadMap" ? true : null));
            Define(host, "X2", setter, a => { data.SetUiData(store, "default", a[0]); return null; });
        }
        Define(host, "X2", "GetPortalSortUiData", a => data.GetUiData("portalSort", a.Str(0) ?? "default"));
        Define(host, "X2", "SetPortalSortUiData", a => { data.SetUiData("portalSort", a.Str(0) ?? "default", a[1]); return null; });
        Define(host, "X2", "GetAddonInfos", _ => Array(data.GetAddons().Select(AddonTable)));
        Define(host, "X2", "GetEscMenuCategories", _ => Array(data.EscMenuCategories.Select(EscMenuCategoryTable)));
        Define(host, "X2", "SetAddonEnable", a => { data.SetAddonEnabled(a.Str(0) ?? "", a.Bool(1)); return null; });
        Define(host, "X2", "GetFeatureset", a => data.GetFeatureSet(a.Str(0) ?? "") ?? "");
        Define(host, "X2", "ChatLog", a => { context.Log(a.Str(0) ?? ""); return null; });
        Define(host, "X2", "SetClipboardText", a => { data.ExecuteSystemCommand("SetClipboardText", a.Values); return null; });
        Define(host, "X2", "FireAddon", a => { context.Events.Fire(a.Str(0) ?? "", a.Values.Skip(1).ToArray()); return null; });
        Define(host, "X2", "ConvertUnitId", a => a[0]);
        Define(host, "X2", "GetCandidateOnceRetrieveCount", _ => data.GetUiData("candidate", "count") is { } value ? AsDouble(value) : 50d);
        Define(host, "X2", "SetCandidateOnceRetrieveCount", a => { data.SetUiData("candidate", "count", a.Num(0)); return null; });
        Define(host, "X2", "GetWorldmapDefaultExpansionLevel", _ => AsDouble(data.GetUiData("worldMap", "expansionLevel")));
        Define(host, "X2", "SetWorldmapDefaultExpansionLevel", a => { data.SetUiData("worldMap", "expansionLevel", a.Num(0)); return null; });

        var commands = "AdjustQuestChatBubbleAutoFireEnd CancelEscape DialogUIContent EnterSystemDungeon ExecuteDynamicAction FastBackwardQuestChat FastBackwardQuestChatByForceSkip FastForwardQuestChat NotifyQuestDirectingModeUpdate ReloadAddon RepairPetItems RepairSlaveItems RequestEndClientDrivenIndun RequestFindId RequestFindPassword RequestJoin RequestRuntimeCommonFarmDoodadInfo RollDice SaveAddonInfos SendNewsCastByChat SendOtpNumber SendPcCertNumber SendPlayDiaryByChat SendSecureCardNumber ShowContent StopChatBubble SystemDungeonStateClear ToggleContent UnBoardingTransfer WidgetClosedByEsc";
        foreach (var command in commands.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var captured = command;
            Define(host, "X2", captured, a => { data.ExecuteSystemCommand(captured, a.Values); return null; });
        }
    }

    // name_rules for en_us (locale 2), per enum_name_rule_targets id: local min/max, English min/max, space, mixed case, special letters
    private static readonly Dictionary<int, (int LocalMin, int LocalMax, int EngMin, int EngMax, bool Space, bool MixCase, bool Special)> NameRules = new()
    {
        [1] = (2, 26, 2, 26, false, false, false), [2] = (2, 26, 2, 26, false, false, false), [3] = (2, 32, 2, 32, true, true, false),
        [4] = (2, 26, 2, 26, false, true, false), [5] = (2, 10, 2, 10, true, true, false), [6] = (3, 32, 3, 32, true, true, false),
        [7] = (2, 26, 2, 26, true, true, false), [8] = (2, 26, 2, 26, false, false, false), [9] = (2, 32, 2, 32, true, true, false),
        [10] = (2, 42, 2, 42, true, true, true),
    };

    internal static LuaTable NamePolicy(int target)
    {
        var r = NameRules.TryGetValue(target, out var rule) ? rule : NameRules[1];
        return new LuaTable
        {
            ["min"] = (double)Math.Min(r.LocalMin, r.EngMin), ["max"] = (double)Math.Max(r.LocalMax, r.EngMax),
            ["local_min"] = (double)r.LocalMin, ["local_max"] = (double)r.LocalMax, ["eng_min"] = (double)r.EngMin, ["eng_max"] = (double)r.EngMax,
            ["is_allow_space"] = r.Space, ["is_allow_mix_case"] = r.MixCase, ["is_allow_special_letter"] = r.Special, ["limit_space_cnt"] = 0d,
        };
    }

    private static void InstallUtil(X2LuaHost host, IX2SystemData data)
    {
        Define(host, "X2Util", "Random2", a => (double)Random.Shared.Next(Math.Max(1, a.Int(0, int.MaxValue))));
        Define(host, "X2Util", "GetHpPercent", a => a.Num(1) <= 0 ? 0d : Math.Clamp(a.Num(0) * 100d / a.Num(1), 0d, 100d));
        Define(host, "X2Util", "InsertComma", a => Comma(a.Str(0) ?? "0"));
        Define(host, "X2Util", "ErasePipeArgsFromText", a => (a.Str(0) ?? "").Split('|')[0]);
        Define(host, "X2Util", "ParsePipeArgsFromText", a => Array((a.Str(0) ?? "").Split('|').Skip(1).Cast<object?>()));
        Define(host, "X2Util", "ApplyUIMacroString", a => X2AssignmentPlayerData.CleanFormatting(a.Str(0) ?? ""));
        Define(host, "X2Util", "ConvertFormatString", a => a.Str(0) ?? "");
        Define(host, "X2Util", "UTF8StringLimit", a => LimitUtf8(a.Str(0) ?? "", a.Int(1), a.Str(2) ?? ""));
        Define(host, "X2Util", "IsValidName", a => !string.IsNullOrWhiteSpace(a.Str(0)) && (a.Str(0)?.Length ?? 0) <= 32);
        Define(host, "X2Util", "GetNamePolicyInfo", a => NamePolicy(a.Int(0, 1)));
        Define(host, "X2Util", "GetCurrentFileTimeStr", _ => data.ServerTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Util", "MakeFileTimeStr", a => DateFromLua(a.Table(0)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Util", "DiffTimeStr", a => (ParseTime(a.Str(0)) - ParseTime(a.Str(1))).TotalSeconds.ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Util", "DiffTimeTable", a => DurationTable((ParseTime(a.Str(1)) - ParseTime(a.Str(2))).Duration()));
        Define(host, "X2Util", "MakeMoneyString", a => JoinCurrency(a));
        Define(host, "X2Util", "MakeAAPointString", a => JoinCurrency(a));
        Define(host, "X2Util", "GetMyMoneyString", _ => "0");
        Define(host, "X2Util", "GetMyBankMoneyString", _ => "0");
        Define(host, "X2Util", "GetMyAAPointString", _ => "0");
        Define(host, "X2Util", "GetMyBankAAPointString", _ => "0");
        Define(host, "X2Util", "GetCurrencyExchangeFee", _ => 0d);
        Define(host, "X2Util", "GetGroupMailExchangeFee", _ => 0d);
        Define(host, "X2Util", "CalcCurrencyExchangeFee", _ => "0");
        Define(host, "X2Util", "CalcGroupMailExchangeFee", _ => "0");
        Define(host, "X2Util", "HasEnoughCurrency", a => Big(a.Str(1)) <= 0);
        Define(host, "X2Util", "StrNumericAdd", a => (Big(a.Str(0)) + Big(a.Str(1))).ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Util", "StrNumericSub", a => (Big(a.Str(0)) - Big(a.Str(1))).ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Util", "StrNumericMul", a => (Big(a.Str(0)) * Big(a.Str(1))).ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Util", "MultiplyNumberString", a => (Big(a.Str(0)) * Big(a.Str(1))).ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Util", "DivideNumberString", a => Divide(a.Str(0), a.Str(1)));
        Define(host, "X2Util", "StrNumericComp", a => (double)Big(a.Str(0)).CompareTo(Big(a.Str(1))));
        Define(host, "X2Util", "GetConstIconPath", a => "ui/icon/" + (a.Str(0) ?? ""));
        Define(host, "X2Util", "MakeMarkedCiphersStr", a => Mask(a.Str(0) ?? "", a.Int(1)));
        Define(host, "X2Util", "GetDdcmsTimeOffset", _ => (double)TimeZoneInfo.Local.GetUtcOffset(data.ServerTime).TotalSeconds);
    }

    private static void InstallOptions(X2LuaHost host, IX2SystemData data)
    {
        Define(host, "X2Option", "CreateOptionItemFloat", a => { data.CreateOption(a.Str(0) ?? "", a.Num(1), a.Int(2)); return null; });
        Define(host, "X2Option", "CreateOptionItemString", a => { data.CreateOption(a.Str(0) ?? "", a.Str(1) ?? "", a.Int(2)); return null; });
        Define(host, "X2Option", "GetOptionItemValue", a => data.GetOption(a.Int(0)));
        Define(host, "X2Option", "GetOptionItemValueByName", a => data.GetOption(a.Str(0) ?? ""));
        Define(host, "X2Option", "GetOptionInfo", a => OptionTable(data.GetOptionInfo(a.Int(0))));
        Define(host, "X2Option", "GetHotkeyInfo", a => Array(data.GetHotkeyInfo(a.Int(0)).Select(HotkeyInfoTable)));
        Define(host, "X2Option", "GetSubOptionItemList", a => Array(data.GetSubOptionItemList(a.Int(0), a.Int(1)).Select(x => (object?)(double)x)));
        Define(host, "X2Option", "EnumAAFormats", _ => Array(data.AntiAliasingFormats.Select(x => new LuaTable { ["samples"] = (double)x.Samples, ["quality"] = (double)x.Quality, ["txaa"] = x.Txaa })));
        Define(host, "X2Option", "GetBasicCursorShape", _ => Array(data.CursorShapes.Select(x => (object?)(double)x)));
        Define(host, "X2Option", "GetCursorSize", _ => Array(data.CursorSizes.Select(x => (object?)(double)x)));
        Define(host, "X2Option", "GetResolutionCount", _ => (double)data.Resolutions.Count);
        Define(host, "X2Option", "GetResolution", a => Resolution(data, a.Int(0)));
        Define(host, "X2Option", "GetMinxMaxOfMouseSensitivity", _ => new LuaMulti(0d, 100d));
        Define(host, "X2Option", "GetModifiedRestartOption", _ => data.HasModifiedRestartOption);
        Define(host, "X2Option", "GetNextSysSpecFullValue", _ => 3d);
        Define(host, "X2Option", "HasOceanSimulateOption", _ => false);
        Define(host, "X2Option", "IsPixelSyncSupported", _ => false);
        Define(host, "X2Option", "OptimizationEnable", a => { data.SetOption("OptimizationEnable", a.Bool(0) ? 1d : 0d); return null; });
        Define(host, "X2Option", "SetItemFloatValue", a => { data.SetOption(a.Int(0), a.Num(1)); return null; });
        Define(host, "X2Option", "SetItemFloatValueByName", a => { data.SetOption(a.Str(0) ?? "", a.Num(1)); return null; });
        Define(host, "X2Option", "SetItemFloatValueWithoutModify", a => { data.SetOption(a.Int(0), a.Num(1), false); return null; });
        Define(host, "X2Option", "SetItemStringValue", a => { data.SetOption(a.Int(0), a.Str(1) ?? ""); return null; });
        Define(host, "X2Option", "SetItemStringValueByName", a => { data.SetOption(a.Str(0) ?? "", a.Str(1) ?? ""); return null; });
        Define(host, "X2Option", "SetItemDefaultFloatValue", a => { data.SetOptionDefault(a.Int(0), a.Num(1)); return null; });
        Define(host, "X2Option", "SetItemDefaultFloatValueByName", a => { data.SetOptionDefault(a.Str(0) ?? "", a.Num(1)); return null; });
        Define(host, "X2Option", "SetItemDefaultStringValue", a => { data.SetOptionDefault(a.Int(0), a.Str(1) ?? ""); return null; });
        Define(host, "X2Option", "SetItemDefaultStringValueByName", a => { data.SetOptionDefault(a.Str(0) ?? "", a.Str(1) ?? ""); return null; });
        Define(host, "X2Option", "Reset", _ => { data.ResetOptions(); return null; });
        Define(host, "X2Option", "RemoveModifiedOption", _ => { data.RevertOptions(); return null; });
        Define(host, "X2Option", "Save", _ => { data.SaveOptions(); return null; });
    }

    private static void InstallHotkeys(X2LuaHost host, IX2SystemData data)
    {
        Define(host, "X2Hotkey", "GetBinding", a => data.GetHotkey(X2HotkeyKind.Action, a.Str(0) ?? "", a.Int(1)) ?? "");
        Define(host, "X2Hotkey", "GetBindingButton", a => data.GetHotkey(X2HotkeyKind.Button, a.Str(0) ?? "", a.Int(1)));
        Define(host, "X2Hotkey", "GetBindingSpell", a => data.GetHotkey(X2HotkeyKind.Spell, a.Str(0) ?? "", a.Int(1)));
        Define(host, "X2Hotkey", "GetOptionBinding", a => data.GetHotkey(X2HotkeyKind.Action, a.Str(0) ?? "", a.Int(1), a.Int(3), true));
        Define(host, "X2Hotkey", "GetOptionBindingButton", a => data.GetHotkey(X2HotkeyKind.Button, a.Str(0) ?? "", a.Int(1), option: true));
        Define(host, "X2Hotkey", "GetTemporaryBindingButton", a => data.GetHotkey(X2HotkeyKind.Button, a.Str(0) ?? "", a.Int(1), temporary: true));
        Define(host, "X2Hotkey", "IsValidActionName", a => data.IsValidHotkeyAction(a.Str(0) ?? ""));
        Define(host, "X2Hotkey", "IsOverridableAction", a => data.IsOverridableHotkeyAction(a.Str(0) ?? ""));
        Define(host, "X2Hotkey", "EnableHotkey", a => { data.EnableHotkeys(a.Bool(0)); return null; });
        Define(host, "X2Hotkey", "ExcuteActionHandler", a => { data.ExecuteHotkeyAction(a.Str(0) ?? ""); return null; });
        Define(host, "X2Hotkey", "BindingToOption", _ => { data.CopyHotkeysToOptions(); return null; });
        Define(host, "X2Hotkey", "InitOptionHotKey", _ => { data.CopyHotkeysToOptions(); return null; });
        Define(host, "X2Hotkey", "OptionToBinding", _ => { data.CopyOptionsToHotkeys(); return null; });
        Define(host, "X2Hotkey", "SaveHotKey", _ => { data.SaveHotkeys(); return null; });
        Define(host, "X2Hotkey", "RemoveOptionBinding", a => { data.SetHotkey(X2HotkeyKind.Action, a.Str(0) ?? "", null, a.Int(1), a.Int(2), true); return null; });
        HotkeySetter(host, data, "SetBinding", X2HotkeyKind.Action);
        HotkeySetter(host, data, "SetBindingButton", X2HotkeyKind.Button);
        HotkeySetter(host, data, "SetBindingItem", X2HotkeyKind.Item);
        HotkeySetter(host, data, "SetBindingSpell", X2HotkeyKind.Spell);
        HotkeySetter(host, data, "SetBindingWithIndex", X2HotkeyKind.Action, indexed: true);
        HotkeySetter(host, data, "SetBindingButtonWithIndex", X2HotkeyKind.Button, indexed: true);
        HotkeySetter(host, data, "SetBindingItemWithIndex", X2HotkeyKind.Item, indexed: true);
        HotkeySetter(host, data, "SetBindingSpellWithIndex", X2HotkeyKind.Spell, indexed: true);
        HotkeySetter(host, data, "SetOptionBinding", X2HotkeyKind.Action, option: true);
        HotkeySetter(host, data, "SetOptionBindingButton", X2HotkeyKind.Button, option: true);
        // The dump omits keyType, but the shipped scripts pass it as the third argument.
        HotkeySetter(host, data, "SetOptionBindingButtonWithIndex", X2HotkeyKind.Button, indexed: true, option: true);
        Define(host, "X2Hotkey", "SetOptionBindingWithIndex", a => { data.SetHotkey(X2HotkeyKind.Action, a.Str(0) ?? "", a.Str(1), a.Int(2), a.Int(3), true); return null; });
        Define(host, "X2Hotkey", "SetTemporaryBindingButton", a => { data.SetHotkey(X2HotkeyKind.Button, a.Str(0) ?? "", a.Str(1), 1, temporary: true); return null; });
    }

    private static void HotkeySetter(X2LuaHost host, IX2SystemData data, string method, X2HotkeyKind kind, bool indexed = false, bool option = false)
        => Define(host, "X2Hotkey", method, a => { data.SetHotkey(kind, a.Str(0) ?? "", a.Str(1), indexed ? a.Int(2, 1) : 1, option: option); return null; });

    private static void InstallSoundCursorInputCamera(X2LuaHost host, IX2SystemData data)
    {
        Define(host, "X2Sound", "PlayUISound", a => (double)data.PlayUiSound(a.Str(0) ?? "", a.Bool(1)));
        Define(host, "X2Sound", "IsPlaying", a => data.IsSoundPlaying((long)a.Num(0)));
        Define(host, "X2Sound", "StopSound", a => { data.StopSound((long)a.Num(0), a.Int(1)); return null; });
        Define(host, "X2Sound", "PlayMusic", a => { data.PlayMusic(a.Str(0) ?? ""); return null; });
        Define(host, "X2Sound", "StopMusic", _ => { data.StopMusic(); return null; });
        Define(host, "X2Sound", "SetSiegePeriod", a => { data.SetSiegeMusic(a.Bool(0)); return null; });
        Define(host, "X2Cursor", "GetCursorInfo", _ => data.Cursor.Info);
        Define(host, "X2Cursor", "GetCursorPickedBagItemIndex", _ => (double)data.Cursor.BagSlot);
        Define(host, "X2Cursor", "GetCursorPickedBagItemAmount", _ => (double)data.Cursor.Amount);
        Define(host, "X2Cursor", "GetCursorPickedItemIconInfo", _ => string.IsNullOrEmpty(data.Cursor.Icon) ? null : data.Cursor.Icon);
        Define(host, "X2Cursor", "ClearCursor", _ => { data.ClearCursor(); return null; });
        Define(host, "X2Cursor", "SetCursorImage", a => { data.SetCursorImage(a.Str(0) ?? "", a.Int(1), a.Int(2)); return null; });
        Define(host, "X2Input", "GetMousePos", _ => new LuaMulti(data.Input.MouseX, data.Input.MouseY));
        Define(host, "X2Input", "IsAltKeyDown", _ => data.Input.Alt);
        Define(host, "X2Input", "IsControlKeyDown", _ => data.Input.Control);
        Define(host, "X2Input", "IsShiftKeyDown", _ => data.Input.Shift);
        Define(host, "X2Input", "GetInputLanguage", _ => data.Input.Language);
        Define(host, "X2Input", "SetInputLanguage", a => { data.SetInputLanguage(a.Str(0) ?? "English"); return null; });
        Define(host, "X2Camera", "IsScreenShotCameraMode", _ => data.IsScreenshotCameraMode);
        Define(host, "X2Camera", "SetUnitCameraAngles", a => { data.SetUnitCameraAngles(a[0]); return null; });
        Define(host, "X2Camera", "ShakeCamera", a => { data.ShakeCamera(a[0]); return null; });
    }

    private static void InstallDebugConsoleSecurity(X2LuaHost host, X2GameContext context, IX2SystemData data)
    {
        Define(host, "X2Debug", "GetDevMode", _ => data.IsDeveloperMode);
        Define(host, "X2Debug", "IsMaster", _ => data.IsGameMaster);
        Define(host, "X2Debug", "GetPlayerId", _ => (double)context.Units.PlayerId);
        Define(host, "X2Debug", "GetPlayerUnit", _ => context.Units.PlayerId == 0 ? null : context.Units.PlayerId.ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Debug", "ReloadScreen", _ => { data.ExecuteSystemCommand("ReloadScreen", []); return null; });
        Define(host, "X2Console", "GetAttribute", a => data.GetConsoleAttribute(a.Str(0) ?? "") ?? "");
        Define(host, "X2Console", "ExecuteString", a => { data.ExecuteConsole(a.Str(0) ?? ""); return null; });
        Define(host, "X2Security", "IsSecondPasswordCreated", _ => data.Security.Created);
        Define(host, "X2Security", "IsSecondPasswordLocked", _ => data.Security.Locked);
        Define(host, "X2Security", "IsSecondPasswordPassed", _ => data.Security.Passed);
        Define(host, "X2Security", "GetSecondPasswordFailedCount", _ => new LuaMulti((double)data.Security.FailedCount, (double)data.Security.MaximumFailedCount));
        Define(host, "X2Security", "GetSecondPasswordUnlockTime", _ => DateTable(data.Security.UnlockTime));
        Define(host, "X2Security", "GetSecondPasswordClearReserveTime", _ => DateTable(data.Security.ClearReservationTime));
        Define(host, "X2Security", "GetSecondPasswordUnlockRemainTime", _ => DurationTable(data.Security.UnlockTime is { } t ? t - data.ServerTime : TimeSpan.Zero));
        foreach (var method in "CancelVaildation ChangeSecondPassword CheckSecondPassword ClearSecondPassword CreateSecondPassword RecommendUsingSecondPassword StartSecondPasswordChange StartSecondPasswordClear StartSecondPasswordCreation StartSecondPasswordWebClear".Split(' '))
        {
            var captured = method;
            Define(host, "X2Security", captured, a => { data.RequestSecurityAction(captured, a.Values.Select(x => x?.ToString() ?? "").ToArray()); return null; });
        }
    }

    private static void InstallMusicSurveyBookScoreboard(X2LuaHost host, IX2SystemData data)
    {
        Define(host, "X2UserMusic", "GetCompositionLimitInfos", _ => Array(data.CompositionLimits.Select(x => new LuaTable { ["grade"] = (double)x.Grade, ["name"] = x.Name, ["compositionLimit"] = (double)x.CompositionLimit })));
        Define(host, "X2UserMusic", "PlayMusicSheet", a => { data.PlayMusicSheet(a.Str(0) ?? ""); return null; });
        Define(host, "X2UserMusic", "StopMusicSheet", _ => { data.StopMusicSheet(); return null; });
        Define(host, "X2UserMusic", "PrepareToSaveMusicSheet", a => data.PrepareMusicSheet(a.Str(0) ?? "", a.Str(1) ?? "", a.Str(2) ?? ""));
        Define(host, "X2UserMusic", "TryToSaveMusicSheet", _ => { data.SavePreparedMusicSheet(); return null; });
        Define(host, "X2SurveyForm", "CanSurvey", a => data.CanSurvey(a.Int(0)));
        Define(host, "X2SurveyForm", "GetSurveyFormList", _ => Array(data.Surveys.Select(x => new LuaTable { ["title"] = x.Title, ["type"] = (double)x.Type, ["progress"] = (double)x.Progress })));
        Define(host, "X2SurveyForm", "GetSurveyFormData", a => SurveyTable(data.GetSurvey(a.Int(0))));
        Define(host, "X2SurveyForm", "GetSurveyFormQuestionData", a => Array(data.GetSurveyQuestions(a.Int(0)).Select(QuestionTable)));
        Define(host, "X2SurveyForm", "SendReply", a => { data.SendSurveyReply(ParseSurveyReply(a.Table(0))); return null; });
        Define(host, "X2Book", "GetBookInfo", a => BookTable(data.GetBook(a.Int(0))));
        Define(host, "X2Book", "GetPageInfo", a => PageTable(data.GetBookPage(a.Int(0))));
        Define(host, "X2MiniScoreboard", "GetInfo", _ => data.MiniScoreboard.Count == 0 ? null : Array(data.MiniScoreboard.Select(ScoreboardTable)));
        Define(host, "X2NameTag", "SetNameTag", _ => { data.RefreshNameTags(); return null; });
        Define(host, "X2Helper", "BitwiseAnd", a => (double)((long)a.Num(0) & (long)a.Num(1)));
        Define(host, "X2Helper", "BitwiseOr", a => (double)((long)a.Num(0) | (long)a.Num(1)));
    }

    private static void InstallGm(X2LuaHost host, IX2SystemData data)
    {
        Define(host, "X2Gm", "ConsoleCommandList", _ => Array(data.GmConsoleCommands.Cast<object?>()));
        Define(host, "X2Gm", "LoadBookmarks", _ => Array(data.GmBookmarks.Cast<object?>()));
        // A normal account cannot use these. The bridge may forward them only after the server establishes GM authority.
        var commands = "AddActionPoint AddCash AddExp AddLaborPower AddMoney ApplyInstantGameGmEvent Attach ChangeFaction ChangeMode CheckBotPlayer CheckZone ClearAttribute ClearBuff ClearBuffs DailyResetReputation DebugLooting DelayTaxDueDate DeleteDominion DemolishHouse Detach DumpChar DumpCharBag DumpCharBank DumpCharCompletedQuests DumpCharEquipment DumpCharParty DumpCharQuests DumpGameRule DumpIndun DumpInstantGame EnablePirates EndInstantGame EndInstantGameJoined ExecuteConsoleCommand Freeze GiveNewItem GmOneAndOneChat GoTo InfoInstantApplier InfoInstantField Kick MoveCharRezPoint Notice NoticeEx PlaySequence RecoverDoodad RecoverHouses RemoveAllItems RemoveMate RemoveSlave ResendHouseTaxMail ResetHouses ResetSkillCooldown Resurrect Return ReturnNpc ScheduleSiege SetAttribute SetBattleRecordRating SetBuff SetCongestion SetCrimeValue SetDoodadGrowth SetEmptyBag SetExpFactor SetHealth SetHousePermission SetInstantExclusive SetInstantGmEventMode SetInvisible SetMana SetTradeStatus ShowTradeStatus SpawnDoodad SpawnGimmick SpawnMate SpawnNpc SpawnSlave Summon TowerDefList TowerDefReload TowerDefStart TowerDefStop UnFreeze UpdateHeroScore UpdateLeadership UseSkill WorldGoTo";
        foreach (var command in commands.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var captured = command;
            Define(host, "X2Gm", captured, a => { if (data.IsGameMaster) data.ExecuteGmCommand(captured, a.Values); return null; });
        }
    }

    private static void InstallTime(X2LuaHost host, IX2SystemData data)
    {
        Define(host, "X2Time", "GetUiMsec", _ => (double)data.UiMilliseconds);
        Define(host, "X2Time", "GetLocalTime", _ => DateTimeOffset.Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Time", "GetLocalDate", _ => DateTable(DateTimeOffset.Now));
        Define(host, "X2Time", "GetLocalWeek", _ => (double)DateTimeOffset.Now.DayOfWeek);
        Define(host, "X2Time", "GetServerTime", _ => DateTable(data.ServerTime));
        Define(host, "X2Time", "GetGameTime", _ => GameTime(data.GameTime));
        Define(host, "X2Time", "DateToTimeString", a => MakeDate(a).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Define(host, "X2Time", "TimeToDate", a => DateTable(ParseTime(a.Str(0))));
        Define(host, "X2Time", "PeriodToDate", a => DurationTable(TimeSpan.FromSeconds(Math.Max(0, a.Num(0)))));
        Define(host, "X2Time", "PeriodTimeToDate", a => DurationTable((ParseTime(a.Str(1)) - ParseTime(a.Str(0))).Duration()));
        Define(host, "X2Time", "CompareTime", a => (double)ParseTime(a.Str(0)).CompareTo(ParseTime(a.Str(1))));
    }

    private static LuaMulti Resolution(IX2SystemData data, int index)
    {
        var i = Math.Clamp(index - 1, 0, Math.Max(0, data.Resolutions.Count - 1));
        var r = data.Resolutions.Count == 0 ? new X2Resolution(1920, 1080) : data.Resolutions[i];
        return new LuaMulti((double)r.Width, (double)r.Height, (double)r.BitsPerPixel);
    }

    private static LuaTable? OptionTable(X2OptionInfo? value) => value == null ? null : new LuaTable
    {
        ["id"] = (double)value.Id, ["title"] = value.Title, ["tooltip"] = value.Tooltip, ["restart"] = value.Restart,
        ["featureSet"] = value.FeatureSet, ["featureSetCondition"] = value.FeatureSetCondition != 0, ["value"] = value.Value,
    };

    private static LuaTable HotkeyInfoTable(X2HotkeyInfo value) => new()
    {
        ["hotkeyActionName"] = value.HotkeyActionName, ["title"] = value.Title, ["tooltip"] = value.Tooltip,
        ["restart"] = value.Restart, ["featureSet"] = value.FeatureSet, ["featureSetCondition"] = value.FeatureSetCondition != 0,
    };

    private static LuaTable AddonTable(X2AddonInfo value)
    {
        var table = Map(value.Fields); table["name"] = value.Name; table["enabled"] = value.Enabled; return table;
    }

    private static LuaTable EscMenuCategoryTable(X2EscMenuCategory value) => new()
    {
        ["id"] = (double)value.Id, ["name"] = value.Name, ["visibleOrder"] = (double)value.VisibleOrder,
        ["iconKey"] = value.IconKey, ["menus"] = Array(value.Menus.Select(EscMenuItemTable)),
    };

    private static LuaTable EscMenuItemTable(X2EscMenuItem value) => new()
    {
        ["uiContentType"] = (double)value.UiContentType, ["visibleOrder"] = (double)value.VisibleOrder,
        ["iconKey"] = value.IconKey, ["badgeColorKey"] = value.BadgeColorKey,
        ["featureSet"] = Array(value.FeatureSet.Cast<object?>()),
    };

    private static LuaTable? SurveyTable(X2SurveyData? value)
    {
        if (value == null) return null;
        return new LuaTable
        {
            ["type"] = (double)value.Type, ["progress"] = (double)value.Progress, ["title"] = value.Title, ["description"] = value.Description,
            ["stYear"] = (double)value.StartsAt.Year, ["stMonth"] = (double)value.StartsAt.Month, ["stDay"] = (double)value.StartsAt.Day,
            ["stDisplayHour"] = (double)value.StartsAt.Hour, ["stDisplayMin"] = (double)value.StartsAt.Minute,
            ["edYear"] = (double)value.EndsAt.Year, ["edMonth"] = (double)value.EndsAt.Month, ["edDay"] = (double)value.EndsAt.Day,
            ["edDisplayHour"] = (double)value.EndsAt.Hour, ["edDisplayMin"] = (double)value.EndsAt.Minute,
            ["item"] = value.Item == null ? null : new LuaTable { ["itemType"] = (double)value.Item.ItemType, ["itemNum"] = (double)value.Item.ItemNum },
        };
    }

    private static LuaTable QuestionTable(X2SurveyQuestion value)
    {
        var table = Map(value.Fields); table["kind"] = (double)value.Kind; return table;
    }

    private static X2SurveyReply ParseSurveyReply(LuaTable? value)
    {
        if (value == null) return new(0, []);
        var type = (int)AsDouble(value.GetValueOrDefault("sType"));
        var answers = value.GetValueOrDefault("datas") is LuaTable t
            ? t.Where(x => x.Key is double).OrderBy(x => (double)x.Key).Select(x => x.Value).ToArray()
            : [];
        return new(type, answers);
    }

    private static LuaTable? BookTable(X2BookInfo? value)
    {
        if (value == null) return null;
        return new LuaTable
        {
            ["book"] = (double)value.Type, ["name"] = value.Name, ["useContent"] = value.UseContent,
            ["contents"] = Array(value.Contents.Select(chapter => new LuaTable
            {
                ["name"] = chapter.Name, ["title"] = chapter.Name, ["startPage"] = (double)chapter.StartPage,
                ["pages"] = Array(chapter.Pages.Select(x => (object?)(double)x)),
            })),
        };
    }

    private static LuaTable? PageTable(X2BookPage? value) => value == null ? null : new LuaTable
    {
        ["type"] = (double)value.Type, ["title"] = value.Title,
        ["contents"] = Array(value.Contents.Select(x => new LuaTable { ["text"] = x.Text, ["illust"] = x.Illust })),
    };

    private static LuaTable ScoreboardTable(X2MiniScoreboardSection value) => new()
    {
        ["name"] = value.Name, ["footer"] = value.Footer, ["footerGuide"] = value.FooterGuide,
        ["visibleOrder"] = (double)value.VisibleOrder, ["type"] = value.Type,
        ["rows"] = Array(value.Rows.Select(ScoreboardRowTable)),
    };

    private static LuaTable ScoreboardRowTable(X2MiniScoreboardRow value)
    {
        var table = Map(value.Extra);
        table["type"] = value.Type; table["visibleOrder"] = (double)value.VisibleOrder; table["moduleType"] = value.ModuleType;
        table["name"] = value.Name; table["curHp"] = (double)value.CurrentHp; table["maxHp"] = (double)value.MaximumHp;
        return table;
    }

    private static LuaTable Map(IReadOnlyDictionary<string, object?> values)
    {
        var result = new LuaTable(); foreach (var pair in values) result[pair.Key] = ToLua(pair.Value); return result;
    }

    private static object? ToLua(object? value) => value switch
    {
        null or string or bool or double or LuaTable => value,
        byte b => (double)b, short s => (double)s, int i => (double)i, long l => (double)l,
        float f => (double)f, decimal d => (double)d,
        IReadOnlyDictionary<string, object?> map => Map(map),
        IEnumerable<object?> list => Array(list),
        _ => value.ToString(),
    };

    private static LuaTable Array(IEnumerable<object?> values)
    {
        var result = new LuaTable(); var i = 1d; foreach (var value in values) result[i++] = ToLua(value); return result;
    }

    private static LuaTable? DateTable(DateTimeOffset? value)
    {
        if (value == null) return null;
        var v = value.Value;
        return new LuaTable
        {
            ["year"] = (double)v.Year, ["month"] = (double)v.Month, ["day"] = (double)v.Day,
            ["hour"] = (double)v.Hour, ["minute"] = (double)v.Minute, ["second"] = (double)v.Second,
            ["millisecond"] = (double)v.Millisecond, ["weekDay"] = (double)v.DayOfWeek,
        };
    }

    private static LuaTable DurationTable(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return new LuaTable
        {
            ["year"] = 0d, ["month"] = 0d, ["day"] = (double)(int)value.TotalDays,
            ["hour"] = (double)value.Hours, ["minute"] = (double)value.Minutes, ["second"] = (double)value.Seconds,
        };
    }

    private static LuaMulti GameTime(TimeSpan value)
    {
        var hour = ((int)value.TotalHours % 24 + 24) % 24;
        return new LuaMulti(hour < 12, (double)(hour % 12), value.Minutes + value.Seconds / 60d);
    }

    private static DateTimeOffset MakeDate(LuaArgs a)
    {
        try
        {
            var year = Math.Clamp(a.Int(0), 1, 9999);
            var month = Math.Clamp(a.Int(1), 1, 12);
            var day = Math.Clamp(a.Int(2), 1, DateTime.DaysInMonth(year, month));
            return new DateTimeOffset(year, month, day, Math.Clamp(a.Int(3), 0, 23), Math.Clamp(a.Int(4), 0, 59), Math.Clamp(a.Int(5), 0, 59), TimeSpan.Zero);
        }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    private static DateTimeOffset DateFromLua(LuaTable? table)
    {
        if (table == null) return DateTimeOffset.UnixEpoch;
        try { return new DateTimeOffset((int)AsDouble(table.GetValueOrDefault("year")), (int)AsDouble(table.GetValueOrDefault("month")), (int)AsDouble(table.GetValueOrDefault("day")), (int)AsDouble(table.GetValueOrDefault("hour")), (int)AsDouble(table.GetValueOrDefault("minute")), (int)AsDouble(table.GetValueOrDefault("second")), TimeSpan.Zero); }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    private static DateTimeOffset ParseTime(string? value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch { return DateTimeOffset.UnixEpoch; }
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static BigInteger Big(string? value) => BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : BigInteger.Zero;
    private static string Divide(string? left, string? right) => Big(right) is var divisor && divisor != 0 ? (Big(left) / divisor).ToString(CultureInfo.InvariantCulture) : "0";
    private static string JoinCurrency(LuaArgs value) => (Big(value.Str(0)) * 10_000 + Big(value.Str(1)) * 100 + Big(value.Str(2))).ToString(CultureInfo.InvariantCulture);
    private static double AsDouble(object? value) => value switch { double d => d, int i => i, long l => l, string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d, _ => 0d };
    private static string Comma(string value) => BigInteger.TryParse(value, out var number) ? number.ToString("N0", CultureInfo.InvariantCulture) : value;
    private static string Mask(string value, int visible) => visible <= 0 ? new string('*', value.Length) : new string('*', Math.Max(0, value.Length - visible)) + value[^Math.Min(value.Length, visible)..];

    private static string LimitUtf8(string value, int limit, string suffix)
    {
        if (limit <= 0) return "";
        var builder = new StringBuilder(); var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var count = rune.Utf8SequenceLength;
            if (bytes + count > limit) return builder.ToString() + suffix;
            builder.Append(rune); bytes += count;
        }
        return builder.ToString();
    }
}
