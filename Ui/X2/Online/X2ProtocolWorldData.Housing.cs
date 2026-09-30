#nullable enable
using System.Globalization;
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// The X2House family and the BUILDER_* / HOUSE_* events, projected from the session's housing state.
/// X2House's getters answer for the house whose window is open (the original returned defaults for a house that
/// was only targeted), the builder queries answer for the design being placed.
/// </summary>
public sealed partial class X2ProtocolWorldData
{
    /// <summary>Tax Certificate and its bound twin: what HOUSING_TAX_SEAL construction and upkeep consume.</summary>
    private const uint TaxCertificate = 31891;
    private const uint BoundTaxCertificate = 31892;
    /// <summary>CountTaxItemForTax("100000", 1) returned 10 in the real client: one certificate per 10000.</summary>
    private const double CopperPerTaxCertificate = 10000;

    private Func<uint, X2InventoryItem?>? _itemInfo;
    private HousingPlacementCatalog? _housingCatalog;

    /// <summary>Static item info (X2Item:GetItemInfoByType shape) for the housing item queries.</summary>
    public void SetItemInfoSource(Func<uint, X2InventoryItem?> itemInfo) => _itemInfo = itemInfo;

    private HousingPlacementCatalog? HousingCatalog =>
        _session?.HousingCatalog ?? (_housingCatalog ??= File.Exists(_gameDatabase) ? new HousingPlacementCatalog(_gameDatabase) : null);

    private void AttachHousing(OnlineSession session)
    {
        session.HousingBuilderStepChanged += OnBuilderStep;
        session.HousingBuilderEnded += OnBuilderEnd;
        session.HousingBuildInfoReceived += OnBuildInfo;
        session.HousingBuildInfoCleared += OnBuildInfoCleared;
        session.HousingInteractionStarted += OnHouseInteractionStart;
        session.HousingInteractionEnded += OnHouseInteractionEnd;
        session.HousingBuildProgressChanged += OnBuildProgress;
        session.HousingTaxInfoReceived += OnTaxInfo;
        session.HousingPermissionChanged += OnPermission;
        session.HousingStateReceived += OnHouseState;
        session.HousingRecoverToggled += OnRecoverToggled;
    }

    private void DetachHousing(OnlineSession session)
    {
        session.HousingBuilderStepChanged -= OnBuilderStep;
        session.HousingBuilderEnded -= OnBuilderEnd;
        session.HousingBuildInfoReceived -= OnBuildInfo;
        session.HousingBuildInfoCleared -= OnBuildInfoCleared;
        session.HousingInteractionStarted -= OnHouseInteractionStart;
        session.HousingInteractionEnded -= OnHouseInteractionEnd;
        session.HousingBuildProgressChanged -= OnBuildProgress;
        session.HousingTaxInfoReceived -= OnTaxInfo;
        session.HousingPermissionChanged -= OnPermission;
        session.HousingStateReceived -= OnHouseState;
        session.HousingRecoverToggled -= OnRecoverToggled;
    }

    private void OnBuilderStep(string step) => _events.Fire("BUILDER_STEP", step);

    private void OnBuilderEnd() => _events.Fire("BUILDER_END");

    // Captured: SCConstructHouseTax (267, heavy 0, normal 0, isHeavy 1, base 50000, deposit 100000, total 0,
    // weekly 0, hostile 0) -> HOUSE_BUILD_INFO(267, "50000", "0", 0, 0, true, 0, "100000", 1, false).
    private void OnBuildInfo(HouseConstructTaxEvent quote, HousingDesignData design) =>
        _events.Fire(X2WorldEvents.HOUSE_BUILD_INFO, (double)quote.DesignId, Money(quote.BaseTax), Money(quote.TotalTax),
            (double)quote.HeavyTaxHouseCount, (double)quote.NormalTaxHouseCount, quote.IsHeavyTaxHouse,
            (double)quote.HostileTaxRate, Money(quote.DepositTax), (double)OnlineSession.HousingTaxSeal,
            design.Completion);

    // Captured: the dialog's Cancel was followed by HOUSE_BUILD_INFO with no arguments.
    private void OnBuildInfoCleared() => _events.Fire(X2WorldEvents.HOUSE_BUILD_INFO);

    // x2game FUN_398861b0 fires HOUSE_INTERACTION_START(structureType, viewType, flag): "housing", the category's
    // view type ("farm", "house", "seafarm" or nil) and the design's always-public bit.
    private void OnHouseInteractionStart(ushort timelineId)
    {
        var state = _session?.HouseStates.GetValueOrDefault(timelineId);
        var design = state is null ? null : HousingCatalog?.DesignById(state.TemplateId);
        _events.Fire(X2WorldEvents.HOUSE_INTERACTION_START, "housing",
            design is null ? null : OnlineSession.HouseViewType(design.CategoryId), design?.AlwaysPublic ?? false);
    }

    private void OnHouseInteractionEnd() => _events.Fire(X2WorldEvents.HOUSE_INTERACTION_END);

    // Captured: SCHouseBuildProgress raised HOUSE_STEP_INFO_UPDATED("housing") with no window open.
    private void OnBuildProgress(HouseBuildProgressEvent progress) =>
        _events.Fire(X2WorldEvents.HOUSE_STEP_INFO_UPDATED, "housing");

    // x2game FUN_39728650: HOUSE_TAX_INFO(dominionRate, hostileRate, second money (the tax), due date, due + 7 days,
    // weeksWithoutPay, weeksPrepay, isAlreadyPaid, isHeavy, first money (the deposit), taxType, tl). The window
    // compares the last argument with X2House:GetHouseId(), which is therefore the timeline id too.
    private void OnTaxInfo(HouseTaxInfoEvent tax)
    {
        var due = DateTimeOffset.FromUnixTimeSeconds(tax.DueUnixTime).ToLocalTime();
        _events.Fire(X2WorldEvents.HOUSE_TAX_INFO, (double)tax.DominionTaxRate, (double)tax.HostileTaxRate,
            Money(tax.SecondaryMoneyAmount), DateTable(due), DateTable(due.AddDays(7)),
            (double)tax.WeeksWithoutPay, (double)tax.WeeksPrepay, tax.IsAlreadyPaid, tax.IsHeavyTaxHouse,
            Money(tax.MoneyAmount), (double)tax.TaxType, (double)tax.TimelineId);
    }

    private void OnPermission(HousePermissionChangedEvent permission)
    {
        if (_session?.InteractingHouse == permission.TimelineId)
            _events.Fire(X2WorldEvents.HOUSE_PERMISSION_UPDATED);
    }

    private void OnHouseState(HouseStateEvent state)
    {
        if (_session?.InteractingHouse == state.TimelineId)
            _events.Fire(X2WorldEvents.HOUSE_INFO_UPDATED);
    }

    private void OnRecoverToggled(HouseRecoverToggledEvent recover)
    {
        if (_session?.InteractingHouse == recover.TimelineId)
            _events.Fire(X2WorldEvents.HOUSE_INFO_UPDATED);
    }

    public override X2HouseInfo? CurrentHouse
    {
        get
        {
            if (_session?.InteractingHouseState is not { } state)
                return null;
            return new X2HouseInfo(state.TimelineId, (int)state.TemplateId, state.HouseName, state.OwnerName,
                HouseZoneName(state), IsMine(state), UnderConstruction(state), false, state.Permission,
                state.AllSteps, state.AllSteps - Math.Min(state.CurrentStep, state.AllSteps), state.AllowRecover);
        }
    }

    public override X2ApiResult? QueryHouse(string method, IReadOnlyList<object?> args)
    {
        var state = _session?.InteractingHouseState;
        var design = state is null ? null : HousingCatalog?.DesignById(state.TemplateId);
        switch (method)
        {
            case "GetHousingModelName":
                return X2ApiResult.Scalar(HousingCatalog?.DesignById(UInt(args, 0))?.Name ?? "");
            case "GetHousingModelConstructionInfo":
                return X2ApiResult.Scalar(ConstructionInfo(UInt(args, 0)));
            case "GetHouseConstructionStepInfo":
                return X2ApiResult.Scalar(state is null ? null : ConstructionInfo(state.TemplateId));
            case "GetTaxItem":
                return X2ApiResult.Scalar(_itemInfo?.Invoke(TaxCertificate));
            case "CountTaxItemInBag":
                return X2ApiResult.Scalar((double)(BagCount(TaxCertificate) + BagCount(BoundTaxCertificate)));
            case "CountTaxItemForTax":
                // The real client returned nothing without a tax type and 10 for ("100000", 1).
                if (args.Count < 2 || args[1] is null)
                    return X2ApiResult.Multi();
                return X2ApiResult.Scalar(Math.Ceiling(ParseMoney(args[0]) / CopperPerTaxCertificate));
            case "GetHousePermission":
                return X2ApiResult.Multi((double)(state?.Permission ?? 0), design?.AlwaysPublic ?? false);
            case "GetHouseDecoCount":
                return state is null
                    ? X2ApiResult.Multi()
                    : X2ApiResult.Multi(0d, (double)(design?.DecoLimit ?? 0), 0d, (double)(design?.AbsoluteDecoLimit ?? 0));
            case "GetHouseSaleInfo":
            {
                var mine = state is not null && IsMine(state);
                var onSale = state is { SalePrice: > 0 };
                return X2ApiResult.Table(new X2ApiTable(new Dictionary<string, object?>
                {
                    ["onSale"] = onSale,
                    ["canSell"] = mine && !onSale && design?.IsSellable == true,
                    ["canBuy"] = !mine && onSale,
                    ["price"] = Money(state?.SalePrice ?? 0),
                    ["targetName"] = state is { SellToName.Length: > 0 } ? state.SellToName : null,
                }));
            }
            case "GetHouseZoneName":
                return X2ApiResult.Scalar(state is null ? "" : HouseZoneName(state));
            case "IsFreeDemolishHouse":
            case "CanPackageDemolish":
            case "IsCastle":
            case "IsDominionCastle":
            case "IsExpeditionHouse":
            case "IsSiegePeriod":
            case "IsWarmUpPeriod":
            case "IsHouseRotatable":
            case "CanUseHousingUcc":
                return X2ApiResult.Scalar(false);
            case "GetDemolishSealCount":
                return X2ApiResult.Scalar(0d);
            case "GetHousingRebuildingPackInfo":
            case "GetHousingUccInfo":
            case "GetPrepayRequireItemInfo":
                return X2ApiResult.Scalar(null);
            default:
                return null;
        }
    }

    private X2WorldCommandResult ExecuteHouse(X2WorldCommand command)
    {
        var session = _session;
        if (session is null)
            return new();
        switch (command.Method)
        {
            case "NextStepFromBuildCheck":
                return new(session.NextStepFromBuildCheck());
            case "PrevStepFromBuildCheck":
                return new(session.PrevStepFromBuildCheck());
        }

        if (session.InteractingHouseState is not { } state || !IsMine(state) || _actions?.Invoke() is not { } actions)
            return new();
        switch (command.Method)
        {
            case "SetHousePermission" when command.Arguments.FirstOrDefault() is double permission:
                actions.ChangeHousePermission(state.TimelineId, checked((byte)permission));
                return new(true);
            case "SetHouseName" when command.Arguments.FirstOrDefault() is string { Length: > 0 } name:
                actions.ChangeHouseName(state.TimelineId, name);
                return new(true);
            case "PrepayHouseTax":
                actions.PrepayHouseTax(unchecked((short)state.TimelineId), session.InventoryState.AutoUseAaPoint != 0);
                return new(true);
            case "SetHouseAllowRecover" when command.Arguments.FirstOrDefault() is bool allow:
                // CSAllowHousingRecover toggles; send it only when the wanted value differs.
                if (allow != state.AllowRecover)
                    actions.ToggleHousingRecover(state.TimelineId);
                return new(true);
            case "Demolish":
                return new(session.DemolishInteractingHouse(command.Arguments.ElementAtOrDefault(0) is true,
                    command.Arguments.ElementAtOrDefault(1) is double seals && seals > 0 ? (uint)seals : 0));
            default:
                return new();
        }
    }

    private IReadOnlyList<object?> ConstructionInfo(uint designId)
    {
        var catalog = HousingCatalog;
        if (catalog is null || designId == 0)
            return [];
        var rows = new List<object?>();
        foreach (var step in catalog.BuildSteps(designId))
        {
            if (step.ConsumeItemId == 0 || _itemInfo?.Invoke(step.ConsumeItemId) is not { } item)
                continue;
            // The real client's rows are the material's item info plus step (0-based), needActions and
            // consumeItemStack (GetHousingModelConstructionInfo(267) -> Lumber, step 0, 1, 1).
            var fields = new Dictionary<string, object?>(item.Fields)
            {
                ["step"] = (double)step.Step,
                ["needActions"] = (double)step.NumActions,
                ["consumeItemStack"] = (double)step.ConsumeItemCount,
                ["isLaborPower"] = false,
            };
            rows.Add(item with { Fields = fields });
        }
        return rows;
    }

    private long BagCount(uint templateId) =>
        _session?.InventoryState.Items.Values.Where(item => item.TemplateId == templateId).Sum(item => (long)item.Count) ?? 0;

    private bool IsMine(HouseStateEvent state) => _session is { } session && state.OwnerId == session.Entered.CharacterId;

    private static bool UnderConstruction(HouseStateEvent state) => state.AllSteps > 0 && state.CurrentStep < state.AllSteps;

    private string HouseZoneName(HouseStateEvent state)
    {
        var position = state.Position.Value;
        var zoneKey = _session?.World?.ZoneAt(position.X, position.Y) ?? -1;
        if (zoneKey < 0 || _data?.GetZone(zoneKey) is not { } zone)
            return "";
        return string.IsNullOrWhiteSpace(zone.DisplayText) ? zone.Name : zone.DisplayText;
    }

    private static string Money(ulong copper) => copper.ToString(CultureInfo.InvariantCulture);

    private static double ParseMoney(object? value) => value switch
    {
        double number => number,
        string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };

    private static uint UInt(IReadOnlyList<object?> args, int index) =>
        index < args.Count && args[index] is double value && value > 0 ? (uint)value : 0u;

    private static AAEmu.GodotViewer.Lua.LuaTable DateTable(DateTimeOffset date) => new()
    {
        ["year"] = (double)date.Year, ["month"] = (double)date.Month, ["day"] = (double)date.Day,
        ["hour"] = (double)date.Hour, ["minute"] = (double)date.Minute, ["second"] = (double)date.Second,
    };
}
