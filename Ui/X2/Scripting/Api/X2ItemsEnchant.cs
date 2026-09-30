#nullable enable
using System.Collections;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

/// <summary>A Lua-shaped result supplied by the network/state adapter. List values become 1-based Lua arrays.</summary>
public sealed record X2ItemsTable(IReadOnlyDictionary<string, object?> Fields);

/// <summary>The second return value of GetEvolvingRndAttrsInfo identifies the target random-attribute group.</summary>
public sealed record X2EvolvingAttributeInfo(X2ItemsTable? Attributes, double TargetGroupSetType = 0);

/// <summary>Live values exposed by X2ItemEnchant. Item tables retain the native field names consumed by x2ui.</summary>
public sealed record X2ItemEnchantState
{
    public X2ItemsTable? AwakenResultInfo { get; init; }
    public string ElementConsumeTax { get; init; } = "";
    public double EnchantConsumeLaborPower { get; init; }
    public X2ItemsTable? EnchantItemInfo { get; init; }
    public double EvolvingAddibleRandomAttributeCount { get; init; }
    public double EvolvingCanGradeUpCount { get; init; }
    public X2ItemsTable? EvolvingDifferentAttributes { get; init; }
    public double EvolvingEnableGradeUpCount { get; init; }
    public X2ItemsTable? EvolvingExperienceInfo { get; init; }
    public X2ItemsTable? ItemElementExperienceInfo { get; init; }
    public IReadOnlyList<X2ItemsTable> ItemElements { get; init; } = [];
    public double ItemSocketMax { get; init; }
    public double LeftBatchCount { get; init; }
    public IReadOnlyDictionary<int, X2ItemsTable> MaterialItemInfos { get; init; } = new Dictionary<int, X2ItemsTable>();
    public X2ItemsTable? RatioInfos { get; init; }
    public double SocketingExtraCost { get; init; }
    public X2ItemsTable? SupportItemInfo { get; init; }
    public X2ItemsTable? TargetItemInfo { get; init; }
    public bool EvolvingRerollSelected { get; init; }
    public bool Working { get; init; }
}

/// <summary>Live equipment-slot reinforcement state. Dictionaries are keyed by the native slot or attribute id.</summary>
public sealed record X2EquipSlotReinforceState
{
    public X2ItemsTable? AppliedAllBundleEffect { get; init; }
    public X2ItemsTable? AppliedAllLevelEffect { get; init; }
    public X2ItemsTable? AppliedAllSetEffect { get; init; }
    public IReadOnlyDictionary<int, double> AttributeTotalLevels { get; init; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<int, double> AttributeTypes { get; init; } = new Dictionary<int, double>();
    public X2ItemsTable? BundleEffectInfos { get; init; }
    public double BundleEffectTopLevel { get; init; }
    public IReadOnlyDictionary<int, X2ItemsTable> LevelEffectChangeUiInfos { get; init; } = new Dictionary<int, X2ItemsTable>();
    public IReadOnlyDictionary<int, X2ItemsTable> LevelEffectInfos { get; init; } = new Dictionary<int, X2ItemsTable>();
    public IReadOnlyDictionary<int, double> LevelEffectSteps { get; init; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<(int Slot, int Level), X2ItemsTable> MaterialInfos { get; init; } = new Dictionary<(int, int), X2ItemsTable>();
    public IReadOnlyDictionary<int, double> NextSetApplyLevels { get; init; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<int, X2ItemsTable> ReinforceInfos { get; init; } = new Dictionary<int, X2ItemsTable>();
    public IReadOnlySet<int> LevelUpEnabledSlots { get; init; } = new HashSet<int>();
    public IReadOnlyDictionary<int, double> SetEffectTopLevels { get; init; } = new Dictionary<int, double>();
    public IReadOnlyDictionary<int, X2ItemsTable> SetEffects { get; init; } = new Dictionary<int, X2ItemsTable>();
    public double TotalReinforceLevel { get; init; }
    public IReadOnlySet<int> AttributesWithNextSetEffect { get; init; } = new HashSet<int>();
    public IReadOnlyDictionary<int, double> FullExperienceBySlot { get; init; } = new Dictionary<int, double>();
    public bool WorkingAddExperience { get; init; }
    public double SuitableLevel { get; init; }
}

public sealed record X2ItemGachaState
{
    public X2ItemsTable? ConsumeItemInfo { get; init; }
    public X2ItemsTable? SourceItemInfo { get; init; }
    public double LeftBatchCount { get; init; }
    public double MaxLootCount { get; init; }
    public bool Entered { get; init; }
    public bool Working { get; init; }
}

public sealed record X2ItemLookConverterState
{
    public X2ItemsTable? ConvertBaseItemInfo { get; init; }
    public X2ItemsTable? ConvertLookItemInfo { get; init; }
    public X2ItemsTable? ConvertResultItemInfo { get; init; }
    public X2ItemsTable? ExtractBaseItemInfo { get; init; }
    public X2ItemsTable? ExtractConvertedItemInfo { get; init; }
    public X2ItemsTable? ExtractLookItemInfo { get; init; }
    public X2ItemsTable? ConvertInfo { get; init; }
    public X2ItemsTable? ExtractInfo { get; init; }
}

public sealed record X2DyeingState(X2ItemsTable? Info = null);

/// <summary>Slots filled from the client's currently picked bag/equipment item.</summary>
public enum X2ItemProcessSlot
{
    EnchantTarget,
    EnchantCatalyst,
    EnchantSupport,
    EnchantMaterial,
    GachaSource,
    GachaConsume,
    LookConvertBase,
    LookConvertAppearance,
    LookExtractConverted,
}

/// <summary>Commands sent by the enchanting, reinforcement, gacha, appearance and dyeing windows.</summary>
public enum X2ItemProcessCommand
{
    EnterEnchant, LeaveEnchant, ExecuteEnchant, StopEnchanting,
    ClearEnchantTarget, ClearEnchantCatalyst, ClearEnchantSupport, ClearEnchantMaterial,
    SetAutoEnchantMaterials, SetSmeltingTargetCount, SetSocketUpgradeSelection, UpdateSmelting,
    SwitchAwaken, SwitchElement, SwitchEvolving, SwitchEvolvingReroll, SwitchGem, SwitchGrade,
    SwitchSocketExtract, SwitchSocketInsert, SwitchSocketRemove, SwitchSocketUpgrade,
    SwitchRefurbishment, SwitchSmelting,
    ChangeReinforceLevelEffect, StartReinforceAddExperience, StartReinforceLevelUp, StopReinforceCasting,
    EnterGacha, LeaveGacha, ExecuteGacha, StopGacha, ClearGachaSource, ClearGachaConsume,
    EnterLookConversion, LeaveLookConversion, ConvertLook, ExtractLook,
    ClearLookConvertBase, ClearLookConvertAppearance, ClearLookExtractConverted,
    DyeSample, ExecuteDye, LeaveDye,
}

public partial interface IX2ItemsData
{
    X2ItemEnchantState ItemEnchant { get; }
    X2EquipSlotReinforceState EquipSlotReinforce { get; }
    X2ItemGachaState ItemGacha { get; }
    X2ItemLookConverterState ItemLookConverter { get; }
    X2DyeingState Dyeing { get; }

    double GetItemElementResist(string unitToken, int elementType);
    string? GetItemElementName(int elementType);
    string? GetWeaponElementName(int equipSlot);
    double GetWeaponElementValue(int equipSlot);
    X2EvolvingAttributeInfo GetEvolvingRandomAttributes(int index);
    X2ItemsTable? GetSmeltingEnchantRequirements(int targetItemType, int enchantItemType, int targetItemCount);
    X2ItemsTable? GetSmeltingResults(int itemType, int targetItemCount);
    X2ItemsTable? GetSmeltingTargetRequirements(int itemType, int targetItemCount);
    bool IsSmeltingSmeltable(int targetItemCount);
    bool IsInGameShopItemTag(int itemType);

    /// <summary>Attempts to use the item currently held by the UI cursor in a process slot.</summary>
    bool TryPlaceItemProcessSlot(X2ItemProcessSlot slot, int index = 0);
    void SendItemProcessCommand(X2ItemProcessCommand command, params object?[] arguments);

    bool IsBaseRenewEquipment(object? itemSlot);
    bool IsRenewableEquipment(object? itemSlot);
    bool IsRenewableItem(object? sourceItemSlot, object? targetItemSlot);
    void RenewItem(object? sourceItemSlot, object? targetItemSlot, int skillType);
}

public partial class NullItemsData
{
    // item_elements ids/names are static game data (localized_texts.en_us); every character sees these five rows.
    private static readonly IReadOnlyDictionary<int, string> ElementNames = new Dictionary<int, string>
    {
        [1] = "Piercing", [2] = "Slashing", [3] = "Blasting", [4] = "Crushing", [5] = "Bludgeoning",
    };
    private static readonly X2ItemEnchantState EmptyEnchant = new()
    {
        ItemElements = ElementNames.Select(pair => new X2ItemsTable(new Dictionary<string, object?>
        {
            ["elementType"] = pair.Key,
            ["name"] = pair.Value,
        })).ToArray(),
    };
    private static readonly X2EquipSlotReinforceState EmptyReinforce = new();
    private static readonly X2ItemGachaState EmptyGacha = new();
    private static readonly X2ItemLookConverterState EmptyLookConverter = new();
    private static readonly X2DyeingState EmptyDyeing = new();

    public virtual X2ItemEnchantState ItemEnchant => EmptyEnchant;
    public virtual X2EquipSlotReinforceState EquipSlotReinforce => EmptyReinforce;
    public virtual X2ItemGachaState ItemGacha => EmptyGacha;
    public virtual X2ItemLookConverterState ItemLookConverter => EmptyLookConverter;
    public virtual X2DyeingState Dyeing => EmptyDyeing;
    public virtual double GetItemElementResist(string unitToken, int elementType) => 0;
    public virtual string? GetItemElementName(int elementType) => ElementNames.GetValueOrDefault(elementType);
    public virtual string? GetWeaponElementName(int equipSlot) => null;
    public virtual double GetWeaponElementValue(int equipSlot) => 0;
    public virtual X2EvolvingAttributeInfo GetEvolvingRandomAttributes(int index) => new(null);
    public virtual X2ItemsTable? GetSmeltingEnchantRequirements(int targetItemType, int enchantItemType, int targetItemCount) => null;
    public virtual X2ItemsTable? GetSmeltingResults(int itemType, int targetItemCount) => null;
    public virtual X2ItemsTable? GetSmeltingTargetRequirements(int itemType, int targetItemCount) => null;
    public virtual bool IsSmeltingSmeltable(int targetItemCount) => false;
    public virtual bool IsInGameShopItemTag(int itemType) => false;
    public virtual bool TryPlaceItemProcessSlot(X2ItemProcessSlot slot, int index = 0) => false;
    public virtual void SendItemProcessCommand(X2ItemProcessCommand command, params object?[] arguments) { }
    public virtual bool IsBaseRenewEquipment(object? itemSlot) => false;
    public virtual bool IsRenewableEquipment(object? itemSlot) => false;
    public virtual bool IsRenewableItem(object? sourceItemSlot, object? targetItemSlot) => false;
    public virtual void RenewItem(object? sourceItemSlot, object? targetItemSlot, int skillType) { }
}

public static partial class X2ItemsApi
{
    internal static void InstallEnchant(X2LuaHost host, X2GameContext context, IX2ItemsData data)
    {
        InstallItemEnchant(host, data);
        InstallEquipSlotReinforce(host, data);
        InstallItemGacha(host, data);
        InstallItemLookConverter(host, data);
        InstallRenewItem(host, data);
        InstallDyeing(host, data);
    }

    private static void InstallItemEnchant(X2LuaHost host, IX2ItemsData data)
    {
        const string ns = "X2ItemEnchant";
        host.Define(ns, "ClearEnchantItemSlot", _ => Command(data, X2ItemProcessCommand.ClearEnchantCatalyst));
        host.Define(ns, "ClearMaterialItemSlot", a => Command(data, X2ItemProcessCommand.ClearEnchantMaterial, a.Int(0)));
        host.Define(ns, "ClearSupportItemSlot", _ => Command(data, X2ItemProcessCommand.ClearEnchantSupport));
        host.Define(ns, "ClearTargetItemSlot", _ => Command(data, X2ItemProcessCommand.ClearEnchantTarget));
        host.Define(ns, "EnterItemEnchantMode", _ => Command(data, X2ItemProcessCommand.EnterEnchant));
        host.Define(ns, "Execute", a => Command(data, X2ItemProcessCommand.ExecuteEnchant, Args(a)));
        host.Define(ns, "GetAwakenResultInfo", _ => Table(data.ItemEnchant.AwakenResultInfo));
        host.Define(ns, "GetElementComsumeTax", _ => data.ItemEnchant.ElementConsumeTax);
        host.Define(ns, "GetElementNameByType", a => data.GetItemElementName(a.Int(0)) ?? "");
        host.Define(ns, "GetEnchantConsumeLp", _ => data.ItemEnchant.EnchantConsumeLaborPower);
        host.Define(ns, "GetEnchantItemInfo", _ => Table(data.ItemEnchant.EnchantItemInfo));
        host.Define(ns, "GetEvolvingAddibleRndAttrCount", _ => data.ItemEnchant.EvolvingAddibleRandomAttributeCount);
        host.Define(ns, "GetEvolvingCanGradeupCount", _ => data.ItemEnchant.EvolvingCanGradeUpCount);
        host.Define(ns, "GetEvolvingDiffAttrs", _ => Table(data.ItemEnchant.EvolvingDifferentAttributes));
        host.Define(ns, "GetEvolvingEnableGradeupCount", _ => data.ItemEnchant.EvolvingEnableGradeUpCount);
        host.Define(ns, "GetEvolvingExpInfo", _ => Table(data.ItemEnchant.EvolvingExperienceInfo));
        host.Define(ns, "GetEvolvingRndAttrsInfo", a => EvolvingAttributes(data, a.Int(0)));
        host.Define(ns, "GetItemElementExpInfo", _ => Table(data.ItemEnchant.ItemElementExperienceInfo));
        host.Define(ns, "GetItemElementList", _ => Array(data.ItemEnchant.ItemElements));
        host.Define(ns, "GetItemElementResist", a => data.GetItemElementResist(a.Str(0) ?? "", a.Int(1)));
        host.Define(ns, "GetItemSocketMax", _ => data.ItemEnchant.ItemSocketMax);
        host.Define(ns, "GetLeftBatchCount", _ => data.ItemEnchant.LeftBatchCount);
        host.Define(ns, "GetMaterialItemInfo", a => data.ItemEnchant.MaterialItemInfos.TryGetValue(a.Int(0), out var v) ? Table(v) : null);
        host.Define(ns, "GetRatioInfos", _ => Table(data.ItemEnchant.RatioInfos));
        host.Define(ns, "GetSmeltingEnchantRequirementsInfo", a => Table(data.GetSmeltingEnchantRequirements(a.Int(0), a.Int(1), a.Int(2))));
        host.Define(ns, "GetSmeltingResultsInfo", a => Table(data.GetSmeltingResults(a.Int(0), a.Int(1))));
        host.Define(ns, "GetSmeltingTargetRequirementsInfo", a => Table(data.GetSmeltingTargetRequirements(a.Int(0), a.Int(1))));
        host.Define(ns, "GetSocketingExtraCost", _ => data.ItemEnchant.SocketingExtraCost);
        host.Define(ns, "GetSupportItemInfo", _ => Table(data.ItemEnchant.SupportItemInfo));
        host.Define(ns, "GetTargetItemInfo", _ => Table(data.ItemEnchant.TargetItemInfo));
        host.Define(ns, "GetWeaponElementName", a => data.GetWeaponElementName(a.Int(0)));
        host.Define(ns, "GetWeaponElementValue", a => data.GetWeaponElementValue(a.Int(0)));
        host.Define(ns, "IsEvolvingReRollSelect", _ => data.ItemEnchant.EvolvingRerollSelected);
        host.Define(ns, "IsSmeltingSmeltable", a => data.IsSmeltingSmeltable(a.Int(0)));
        host.Define(ns, "IsWorkingEnchant", _ => data.ItemEnchant.Working);
        host.Define(ns, "LeaveItemEnchantMode", _ => Command(data, X2ItemProcessCommand.LeaveEnchant));
        host.Define(ns, "SetAutoMaterials", a => Command(data, X2ItemProcessCommand.SetAutoEnchantMaterials, a.Bool(0)));
        host.Define(ns, "SetEnchantItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.EnchantCatalyst));
        host.Define(ns, "SetMaterialItemSlotFromPick", a => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.EnchantMaterial, a.Int(0)));
        host.Define(ns, "SetSmeltingTargetItemCount", a => Command(data, X2ItemProcessCommand.SetSmeltingTargetCount, a.Int(0)));
        host.Define(ns, "SetSocketUpgradeSelect", a => Command(data, X2ItemProcessCommand.SetSocketUpgradeSelection, a.Int(0), a.Bool(1), a.Int(2)));
        host.Define(ns, "SetSupportItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.EnchantSupport));
        host.Define(ns, "SetTargetItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.EnchantTarget));
        host.Define(ns, "StopEnchanting", _ => Command(data, X2ItemProcessCommand.StopEnchanting));
        DefineSwitch(host, ns, "SwitchItemAwakenMode", data, X2ItemProcessCommand.SwitchAwaken);
        DefineSwitch(host, ns, "SwitchItemEnchantElementMode", data, X2ItemProcessCommand.SwitchElement);
        DefineSwitch(host, ns, "SwitchItemEnchantEvolvingMode", data, X2ItemProcessCommand.SwitchEvolving);
        DefineSwitch(host, ns, "SwitchItemEnchantEvolvingReRollMode", data, X2ItemProcessCommand.SwitchEvolvingReroll);
        DefineSwitch(host, ns, "SwitchItemEnchantGemMode", data, X2ItemProcessCommand.SwitchGem);
        DefineSwitch(host, ns, "SwitchItemEnchantGradeMode", data, X2ItemProcessCommand.SwitchGrade);
        DefineSwitch(host, ns, "SwitchItemEnchantSocketExtractMode", data, X2ItemProcessCommand.SwitchSocketExtract);
        DefineSwitch(host, ns, "SwitchItemEnchantSocketInsertMode", data, X2ItemProcessCommand.SwitchSocketInsert);
        DefineSwitch(host, ns, "SwitchItemEnchantSocketRemoveMode", data, X2ItemProcessCommand.SwitchSocketRemove);
        DefineSwitch(host, ns, "SwitchItemEnchantSocketUpgradeMode", data, X2ItemProcessCommand.SwitchSocketUpgrade);
        DefineSwitch(host, ns, "SwitchItemRefurbishmentMode", data, X2ItemProcessCommand.SwitchRefurbishment);
        DefineSwitch(host, ns, "SwitchItemSmeltingMode", data, X2ItemProcessCommand.SwitchSmelting);
        host.Define(ns, "UpdateSmeltingEnchantMode", a => Command(data, X2ItemProcessCommand.UpdateSmelting, a.Int(0)));
    }

    private static void InstallEquipSlotReinforce(X2LuaHost host, IX2ItemsData data)
    {
        const string ns = "X2EquipSlotReinforce";
        host.Define(ns, "ChangeLevelEffect", a => Command(data, X2ItemProcessCommand.ChangeReinforceLevelEffect, a.Int(0), a.Int(1)));
        host.Define(ns, "EnableLevelUp", a => data.EquipSlotReinforce.LevelUpEnabledSlots.Contains(a.Int(0)));
        host.Define(ns, "GetAppliedAllBundleEffect", _ => Table(data.EquipSlotReinforce.AppliedAllBundleEffect) ?? new LuaTable());
        host.Define(ns, "GetAppliedAllLevelEffect", _ => Table(data.EquipSlotReinforce.AppliedAllLevelEffect) ?? new LuaTable());
        host.Define(ns, "GetAppliedAllSetEffect", _ => Table(data.EquipSlotReinforce.AppliedAllSetEffect) ?? new LuaTable());
        host.Define(ns, "GetAttributeTotalLevel", a => Get(data.EquipSlotReinforce.AttributeTotalLevels, a.Int(0)));
        host.Define(ns, "GetAttributeType", a => Get(data.EquipSlotReinforce.AttributeTypes, a.Int(0)));
        host.Define(ns, "GetBundleEffectInfos", _ => Table(data.EquipSlotReinforce.BundleEffectInfos) ?? new LuaTable());
        host.Define(ns, "GetBundleEffectTopLevel", _ => data.EquipSlotReinforce.BundleEffectTopLevel);
        host.Define(ns, "GetLevelEffectChangeUIInfo", a => GetTable(data.EquipSlotReinforce.LevelEffectChangeUiInfos, a.Int(0)));
        host.Define(ns, "GetLevelEffectInfoByEquipSlot", a => GetTable(data.EquipSlotReinforce.LevelEffectInfos, a.Int(0)));
        host.Define(ns, "GetLevelEffectStep", a => Get(data.EquipSlotReinforce.LevelEffectSteps, a.Int(0)));
        host.Define(ns, "GetMaterialInfo", a => data.EquipSlotReinforce.MaterialInfos.TryGetValue((a.Int(0), a.Int(1)), out var v) ? Table(v) : null);
        host.Define(ns, "GetNextSetApplyLevel", a => Get(data.EquipSlotReinforce.NextSetApplyLevels, a.Int(0)));
        host.Define(ns, "GetReinforceInfo", a => GetTable(data.EquipSlotReinforce.ReinforceInfos, a.Int(0)));
        host.Define(ns, "GetSetEffectTopLevel", a => Get(data.EquipSlotReinforce.SetEffectTopLevels, a.Int(0)));
        host.Define(ns, "GetSetEffects", a => GetTable(data.EquipSlotReinforce.SetEffects, a.Int(0)) ?? new LuaTable());
        host.Define(ns, "GetTotalReinforceLevel", _ => data.EquipSlotReinforce.TotalReinforceLevel);
        host.Define(ns, "HasNextSetEffect", a => data.EquipSlotReinforce.AttributesWithNextSetEffect.Contains(a.Int(0)));
        host.Define(ns, "IsFullExp", a => Get(data.EquipSlotReinforce.FullExperienceBySlot, a.Int(0)));
        host.Define(ns, "IsInGameShopItemTag", a => data.IsInGameShopItemTag(a.Int(0)));
        host.Define(ns, "IsWorkingAddExp", _ => data.EquipSlotReinforce.WorkingAddExperience);
        host.Define(ns, "StartReinforceAddExp", a => Command(data, X2ItemProcessCommand.StartReinforceAddExperience, a.Int(0), a.Int(1)));
        host.Define(ns, "StartReinforceLevelup", a => Command(data, X2ItemProcessCommand.StartReinforceLevelUp, a.Int(0)));
        host.Define(ns, "StopCasting", _ => Command(data, X2ItemProcessCommand.StopReinforceCasting));
        host.Define(ns, "SuitableLevelForEquipSlotReinforce", _ => data.EquipSlotReinforce.SuitableLevel);
    }

    private static void InstallItemGacha(X2LuaHost host, IX2ItemsData data)
    {
        const string ns = "X2ItemGacha";
        host.Define(ns, "ClearConsumeItemSlot", _ => Command(data, X2ItemProcessCommand.ClearGachaConsume));
        host.Define(ns, "ClearSourceItemSlot", _ => Command(data, X2ItemProcessCommand.ClearGachaSource));
        host.Define(ns, "EnterLootGachaMode", _ => Command(data, X2ItemProcessCommand.EnterGacha));
        host.Define(ns, "Execute", _ => Command(data, X2ItemProcessCommand.ExecuteGacha));
        host.Define(ns, "GetConsumeItemInfo", _ => Table(data.ItemGacha.ConsumeItemInfo));
        host.Define(ns, "GetLeftBatchCount", _ => data.ItemGacha.LeftBatchCount);
        host.Define(ns, "GetMaxLootCount", _ => data.ItemGacha.MaxLootCount);
        host.Define(ns, "GetSourceItemInfo", _ => Table(data.ItemGacha.SourceItemInfo));
        host.Define(ns, "IsEntered", _ => data.ItemGacha.Entered);
        host.Define(ns, "IsWorkingLoot", _ => data.ItemGacha.Working);
        host.Define(ns, "LeaveLootGachaMode", _ => Command(data, X2ItemProcessCommand.LeaveGacha));
        host.Define(ns, "SetConsumeItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.GachaConsume));
        host.Define(ns, "SetSourceItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.GachaSource));
        host.Define(ns, "StopLooting", _ => Command(data, X2ItemProcessCommand.StopGacha));
    }

    private static void InstallItemLookConverter(X2LuaHost host, IX2ItemsData data)
    {
        const string ns = "X2ItemLookConverter";
        host.Define(ns, "ClearConvertBaseItemSlot", _ => Command(data, X2ItemProcessCommand.ClearLookConvertBase));
        host.Define(ns, "ClearConvertLookItemSlot", _ => Command(data, X2ItemProcessCommand.ClearLookConvertAppearance));
        host.Define(ns, "ClearExtractConvertedItemSlot", _ => Command(data, X2ItemProcessCommand.ClearLookExtractConverted));
        host.Define(ns, "ConvertItemLook", _ => Command(data, X2ItemProcessCommand.ConvertLook));
        host.Define(ns, "EnterItemConvertMode", a => Command(data, X2ItemProcessCommand.EnterLookConversion, a.Bool(0), a.Bool(1)));
        host.Define(ns, "ExtractItemLook", _ => Command(data, X2ItemProcessCommand.ExtractLook));
        host.Define(ns, "GetConvertBaseItemInfo", _ => Table(data.ItemLookConverter.ConvertBaseItemInfo));
        host.Define(ns, "GetConvertLookItemInfo", _ => Table(data.ItemLookConverter.ConvertLookItemInfo));
        host.Define(ns, "GetConvertResultItemInfo", _ => Table(data.ItemLookConverter.ConvertResultItemInfo));
        host.Define(ns, "GetExtractBaseItemInfo", _ => Table(data.ItemLookConverter.ExtractBaseItemInfo));
        host.Define(ns, "GetExtractConvertedItemInfo", _ => Table(data.ItemLookConverter.ExtractConvertedItemInfo));
        host.Define(ns, "GetExtractLookItemInfo", _ => Table(data.ItemLookConverter.ExtractLookItemInfo));
        host.Define(ns, "GetItemLookConvertInfo", _ => Table(data.ItemLookConverter.ConvertInfo));
        host.Define(ns, "GetItemLookExtractInfo", _ => Table(data.ItemLookConverter.ExtractInfo));
        host.Define(ns, "LeaveItemConvertMode", _ => Command(data, X2ItemProcessCommand.LeaveLookConversion));
        host.Define(ns, "SetConvertBaseItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.LookConvertBase));
        host.Define(ns, "SetConvertLookItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.LookConvertAppearance));
        host.Define(ns, "SetExtractConvertedItemSlotFromPick", _ => data.TryPlaceItemProcessSlot(X2ItemProcessSlot.LookExtractConverted));
    }

    private static void InstallRenewItem(X2LuaHost host, IX2ItemsData data)
    {
        const string ns = "X2RenewItem";
        host.Define(ns, "IsBaseEquipment", a => data.IsBaseRenewEquipment(a[0]));
        host.Define(ns, "IsRenewableEquipment", a => data.IsRenewableEquipment(a[0]));
        host.Define(ns, "IsRenewableItem", a => data.IsRenewableItem(a[0], a[1]));
        host.Define(ns, "Renew", a => { data.RenewItem(a[0], a[1], a.Int(2)); return null; });
    }

    private static void InstallDyeing(X2LuaHost host, IX2ItemsData data)
    {
        const string ns = "X2Dyeing";
        host.Define(ns, "DyeSample", a => Command(data, X2ItemProcessCommand.DyeSample, a.Num(0), a.Num(1), a.Num(2)));
        host.Define(ns, "Execute", _ => Command(data, X2ItemProcessCommand.ExecuteDye));
        host.Define(ns, "GetInfo", _ => Table(data.Dyeing.Info));
        host.Define(ns, "Leave", _ => Command(data, X2ItemProcessCommand.LeaveDye));
    }

    private static object? Command(IX2ItemsData data, X2ItemProcessCommand command, params object?[] arguments)
    {
        data.SendItemProcessCommand(command, arguments);
        return null;
    }

    private static void DefineSwitch(X2LuaHost host, string ns, string name, IX2ItemsData data, X2ItemProcessCommand command)
        => host.Define(ns, name, _ => Command(data, command));

    private static object?[] Args(LuaArgs args)
    {
        var values = new object?[args.Count];
        for (var i = 0; i < values.Length; i++) values[i] = args[i];
        return values;
    }

    private static object EvolvingAttributes(IX2ItemsData data, int index)
    {
        var info = data.GetEvolvingRandomAttributes(index);
        return new LuaMulti(Table(info.Attributes), info.TargetGroupSetType);
    }

    private static double Get(IReadOnlyDictionary<int, double> values, int key)
        => values.TryGetValue(key, out var value) ? value : 0;

    private static LuaTable? GetTable(IReadOnlyDictionary<int, X2ItemsTable> values, int key)
        => values.TryGetValue(key, out var value) ? Table(value) : null;

    private static LuaTable Array(IReadOnlyList<X2ItemsTable> values)
    {
        var result = new LuaTable();
        for (var i = 0; i < values.Count; i++) result[(double)(i + 1)] = Table(values[i]);
        return result;
    }

    private static LuaTable? Table(X2ItemsTable? value)
    {
        if (value == null) return null;
        var result = new LuaTable();
        foreach (var (key, item) in value.Fields) result[key] = LuaValue(item);
        return result;
    }

    private static object? LuaValue(object? value)
    {
        if (value is null or string or bool or double) return value;
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or decimal)
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        if (value is X2ItemsTable table) return Table(table);
        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary) return Table(new X2ItemsTable(readOnlyDictionary));
        if (value is IDictionary dictionary)
        {
            var result = new LuaTable();
            foreach (DictionaryEntry entry in dictionary)
                result[entry.Key is string s ? s : Convert.ToDouble(entry.Key, System.Globalization.CultureInfo.InvariantCulture)] = LuaValue(entry.Value);
            return result;
        }
        if (value is IEnumerable sequence)
        {
            var result = new LuaTable();
            var index = 1d;
            foreach (var item in sequence) result[index++] = LuaValue(item);
            return result;
        }
        return value;
    }
}
