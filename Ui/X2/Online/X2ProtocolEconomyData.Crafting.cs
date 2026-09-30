#nullable enable
using AAEmu.GodotViewer.Client;
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;

namespace AAEmu.GodotViewer.Ui.X2.Online;

public sealed partial class X2ProtocolEconomyData
{
    private static readonly Lazy<X2CraftCatalog> CraftBook = new(() => new X2CraftCatalog(X2DbLists.DefaultDatabase));
    private bool _craftWorking;
    private int _craftBatchRemaining;
    private int _pendingCraftSkillId;
    private ushort _craftTimeline;
    private Func<uint>? _craftTargetTemplate;
    private uint _craftInteractionId;
    private uint _craftInteractionTemplate;
    private int _craftInteractionPack;
    private readonly Dictionary<int, uint> _craftActability = new();
    private OnlineSession? _recipeSession;
    private long _recipeSignature = long.MinValue;
    private IReadOnlyList<X2CraftRecipe>? _recipeCache;

    public void SetCraftTargetTemplate(Func<uint> template) => _craftTargetTemplate = template;

    private Func<long>? _craftLabor;
    /// <summary>Total labor (global + local) as the HUD shows it: the live packet value, else the character record's.
    /// InventoryState.Labor stays 0 until the server sends a labor packet, which it does not do at world entry.</summary>
    public void SetCraftLabor(Func<long> labor) => _craftLabor = labor;

    internal void ApplyCraftActability(ActabilityEvent update)
    {
        foreach (var entry in update.Entries)
            _craftActability[checked((int)entry.Id)] = entry.Points;
        _recipeSignature = long.MinValue;
    }

    internal bool BeginCraftInteraction(uint objectId, uint templateId, uint phaseId)
    {
        var pack = CraftBook.Value.GetStationPack((int)templateId, (int)phaseId);
        if (objectId == 0 || pack == 0)
            return false;
        _craftInteractionId = objectId;
        _craftInteractionTemplate = templateId;
        _craftInteractionPack = pack;
        return true;
    }

    internal bool CraftSkillStarted(SkillStartedEvent skill, out int remaining)
    {
        remaining = _craftBatchRemaining;
        if (!_craftWorking || _pendingCraftSkillId <= 0 || skill.SkillId != (uint)_pendingCraftSkillId ||
            skill.Caster.UnitId != _session()?.Entered.UnitId) return false;
        _craftTimeline = skill.TimelineId;
        return true;
    }

    internal bool CraftSkillEnded(SkillEndedEvent skill, out int remaining)
    {
        remaining = _craftBatchRemaining;
        if (!_craftWorking || _craftTimeline == 0 || skill.TimelineId != _craftTimeline) return false;
        _craftTimeline = 0;
        remaining = _craftBatchRemaining = Math.Max(0, _craftBatchRemaining - 1);
        if (remaining == 0) ResetCraftProgress();
        return true;
    }

    internal bool CraftFailed(CraftFailedEvent failure)
    {
        if (!_craftWorking) return false;
        ResetCraftProgress();
        return true;
    }

    private void ResetCraftProgress()
    {
        _craftWorking = false;
        _craftBatchRemaining = 0;
        _pendingCraftSkillId = 0;
        _craftTimeline = 0;
    }

    private IReadOnlyList<X2CraftRecipe> CraftRecipes(OnlineSession? session)
    {
        var signature = 17L;
        if (session != null)
        {
            foreach (var pair in session.InventoryState.Items)
                signature = unchecked(signature * 31 + pair.Key.Slot * 7L + pair.Value.ItemId.GetHashCode() +
                    pair.Value.TemplateId * 13L + pair.Value.Count);
            signature = unchecked(signature * 31 + session.InventoryState.Labor +
                session.InventoryState.LocalLabor * 7L + session.InventoryState.Money + (_craftLabor?.Invoke() ?? 0) * 17L);
            signature = unchecked(signature * 31 + (_craftInteractionId != 0 ? _craftInteractionId : _targetObjectId?.Invoke() ?? 0) +
                (_craftInteractionTemplate != 0 ? _craftInteractionTemplate : _craftTargetTemplate?.Invoke() ?? 0) * 7L +
                _craftInteractionPack * 13L);
        }
        if (ReferenceEquals(session, _recipeSession) && signature == _recipeSignature && _recipeCache != null)
            return _recipeCache;
        var owned = session?.InventoryState.Items
            .Where(pair => pair.Key.SlotType == InventorySlot)
            .GroupBy(pair => checked((int)pair.Value.TemplateId))
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value.Count))
            ?? new Dictionary<int, int>();
        var labor = _craftLabor?.Invoke() ?? (session?.InventoryState.Labor + session?.InventoryState.LocalLabor ?? 0);
        var result = CraftBook.Value.Entries.Select(entry =>
        {
            var count = entry.Materials.Count == 0 ? 0 : entry.Materials
                .Min(material => material.Amount <= 0 ? 0 : owned.GetValueOrDefault(material.ItemType) / material.Amount);
            if (session == null) count = 0;
            var stationReady = session != null && _craftInteractionId != 0 &&
                CraftBook.Value.IsInPack(_craftInteractionPack, entry.Type);
            var baseInfo = new Dictionary<string, object?>
            {
                ["actability_satisfied"] = entry.ActabilityLimit == 0 ||
                    _craftActability.GetValueOrDefault(entry.ActabilityGroup) >= entry.ActabilityLimit,
                ["laborpower_satisfied"] = stationReady && labor >= entry.LaborCost,
                ["cost_satisfied"] = session != null && session.InventoryState.Money >= entry.Cost,
                ["required_actability_type"] = entry.ActabilityGroup,
                ["required_actability_point"] = entry.ActabilityLimit,
                ["required_actability_name"] = entry.ActabilityName,
                ["use_only_actability"] = false,
                ["needed_lp"] = entry.LaborCost,
                ["consume_lp"] = entry.LaborCost,
                ["cost"] = entry.Cost,
                ["discount"] = 0,
                ["recommend_level"] = entry.RecommendLevel,
                ["orderable"] = entry.Orderable,
                ["doodad_name"] = entry.StationName,
            };
            IReadOnlyList<IReadOnlyDictionary<string, object?>> products = entry.Products.Select(product =>
                (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["itemType"] = product.ItemType, ["item_name"] = product.Name,
                    ["amount"] = product.Amount, ["useGrade"] = product.UseGrade,
                    ["productGrade"] = product.Grade,
                }).ToArray();
            IReadOnlyList<IReadOnlyDictionary<string, object?>> materials = entry.Materials.Select(material =>
                (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["itemType"] = material.ItemType, ["amount"] = material.Amount,
                    ["count"] = owned.GetValueOrDefault(material.ItemType), ["mainGrade"] = material.MainGrade,
                    ["item_info"] = new Dictionary<string, object?>
                    {
                        ["itemType"] = material.ItemType, ["name"] = material.Name,
                        ["itemGrade"] = 1, ["stack"] = owned.GetValueOrDefault(material.ItemType),
                    },
                }).ToArray();
            return new X2CraftRecipe(entry.Type, entry.Products[0].ItemType, entry.Name,
                Math.Max(0, count), BaseInfo: baseInfo, Products: products, Materials: materials);
        }).ToArray();
        _recipeSession = session;
        _recipeSignature = signature;
        return _recipeCache = result;
    }

    private bool TryQueryCraft(X2EconomyQuery query, out object? value)
    {
        value = null;
        if (query.Table != "X2Craft") return false;
        var catalog = CraftBook.Value;
        var session = _session();
        var recipes = CraftRecipes(session);
        var craftType = Int(query.Arguments, 0);
        var recipe = recipes.FirstOrDefault(x => x.CraftType == craftType);
        var entry = catalog.ByType.GetValueOrDefault(craftType);
        switch (query.Method)
        {
            case "GetACategories":
                value = catalog.GetACategoryRows();
                return true;
            case "GetBCategories":
                value = catalog.GetBCategoryRows(craftType);
                return true;
            case "IsVisibleACategory":
                value = catalog.A.FirstOrDefault(x => x.Type == craftType)?.Visible ?? false;
                return true;
            case "IsEquipmnetACategory":
                value = catalog.IsEquipmentCategory(craftType);
                return true;
            case "GetList":
            {
                var a = craftType;
                var b = Int(query.Arguments, 1);
                var craftable = Bool(query.Arguments, 2);
                var executable = recipes.ToDictionary(x => x.CraftType, x => x.ExecutableCount);
                var matches = catalog.Entries.Where(e => (a == 0 || e.CategoryA == a) &&
                    (b == 0 || e.CategoryB == b) && (!craftable || executable.GetValueOrDefault(e.Type) > 0) &&
                    (_craftInteractionId == 0 || catalog.IsInPack(_craftInteractionPack, e.Type)))
                    .ToArray();
                value = matches.Length == 0 ? null : CraftTree(matches, catalog);
                return true;
            }
            case "GetListBySearching":
            {
                var search = String(query.Arguments, 4);
                var categoryA = craftType;
                var craftable = Bool(query.Arguments, 3);
                var matches = catalog.Entries.Where(x => (categoryA == 0 || x.CategoryA == categoryA) &&
                    (!craftable || recipes.First(recipe => recipe.CraftType == x.Type).ExecutableCount > 0) &&
                    x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                    (_craftInteractionId == 0 || catalog.IsInPack(_craftInteractionPack, x.Type))).ToArray();
                value = matches.Length == 0 ? null : SearchTree(matches, catalog);
                return true;
            }
            case "GetCraftBaseInfo":
                if (recipe == null) return false;
                value = recipe.BaseInfo!.ToDictionary(pair => pair.Key, pair => pair.Value);
                var baseInfo = (Dictionary<string, object?>)value;
                baseInfo["craftType"] = recipe.CraftType;
                baseInfo["name"] = recipe.Name;
                baseInfo["productItemType"] = recipe.ProductItemType;
                return true;
            case "GetCraftProductInfo":
                value = recipe?.Products ?? [];
                return true;
            case "GetCraftMaterialInfo":
                value = recipe?.Materials ?? [];
                return true;
            case "GetExecutableCraftCount":
                value = recipe?.ExecutableCount ?? 0;
                return true;
            case "GetCraftStepInfo":
                value = null;
                return true;
            case "GetEquipmentBCateoryInfo":
                value = catalog.GetEquipmentBCategoryRows();
                return true;
            case "GetActabilityGroup":
                value = catalog.Entries.Where(x => x.ActabilityGroup > 0)
                    .GroupBy(x => x.ActabilityGroup)
                    .Select(group => (object?)new Dictionary<string, object?>
                    { ["type"] = group.Key, ["name"] = group.First().ActabilityName }).ToArray();
                return true;
            case "GetCraftingMaterialLimitCount":
                value = 10;
                return true;
            case "GetInteratcionTargetId":
                value = _craftInteractionId;
                return true;
            case "GetInteratcionTargetDoodadType":
                value = _craftInteractionTemplate;
                return true;
            case "IsWorkingCraft":
                value = _craftWorking;
                return true;
            case "GetLeftBatchCount":
                value = _craftBatchRemaining;
                return true;
            default:
                return false;
        }
    }

    private static LuaTable CraftTree(IReadOnlyList<X2CraftCatalog.Entry> entries, X2CraftCatalog catalog)
    {
        var tree = new LuaTable();
        var categoryNames = catalog.C.ToDictionary(x => x.Type, x => x.Name);
        var groupIndex = 1d;
        foreach (var group in entries.GroupBy(x => x.CategoryC))
        {
            var crafts = new LuaTable();
            var craftIndex = 1d;
            foreach (var entry in group)
                crafts[craftIndex++] = new LuaTable
                {
                    ["name"] = entry.Name, ["type"] = (double)entry.Type,
                    ["possibleFavorite"] = true, ["isFavorite"] = false,
                };
            tree[groupIndex++] = new LuaTable
            {
                ["CCategoryName"] = categoryNames.GetValueOrDefault(group.Key, "Other"),
                ["crafts"] = crafts,
            };
        }
        return tree;
    }

    private static LuaTable SearchTree(IReadOnlyList<X2CraftCatalog.Entry> entries, X2CraftCatalog catalog)
    {
        var roots = new LuaTable();
        var aNames = catalog.A.ToDictionary(category => category.Type, category => category.Name);
        var bNames = catalog.B.ToDictionary(category => category.Type, category => category.Name);
        var cNames = catalog.C.ToDictionary(category => category.Type, category => category.Name);
        var aIndex = 1d;
        foreach (var aGroup in entries.GroupBy(entry => entry.CategoryA))
        {
            var bRows = new LuaTable();
            var bIndex = 1d;
            foreach (var bGroup in aGroup.GroupBy(entry => entry.CategoryB))
            {
                var cRows = new LuaTable();
                var cIndex = 1d;
                foreach (var cGroup in bGroup.GroupBy(entry => entry.CategoryC))
                {
                    var crafts = new LuaTable();
                    var craftIndex = 1d;
                    foreach (var entry in cGroup)
                        crafts[craftIndex++] = new LuaTable
                        {
                            ["text"] = entry.Name, ["value"] = (double)entry.Type,
                            ["possibleFavorite"] = true, ["isFavorite"] = false,
                        };
                    cRows[cIndex++] = new LuaTable
                    {
                        ["text"] = cNames.GetValueOrDefault(cGroup.Key, "Other"), ["value"] = 0d,
                        ["child"] = crafts,
                    };
                }
                bRows[bIndex++] = new LuaTable
                {
                    ["text"] = bNames.GetValueOrDefault(bGroup.Key, "Other"), ["value"] = 0d,
                    ["child"] = cRows,
                };
            }
            roots[aIndex++] = new LuaTable
            {
                ["text"] = aNames.GetValueOrDefault(aGroup.Key, "Other"), ["value"] = 0d,
                ["child"] = bRows,
            };
        }
        return roots;
    }

    private bool ExecuteCraft(X2EconomyCommand command, OnlineSession? session, ClientActions? actions)
    {
        if (command.Table != "X2Craft") return false;
        switch (command.Method)
        {
            case "ExecuteBatchCraftByType" when session != null && actions != null:
            {
                var craft = Int(command.Arguments, 0);
                var target = Unsigned(command.Arguments, 1);
                var count = Math.Clamp(Unsigned(command.Arguments, 2), 1u, 1000u);
                if (!CraftBook.Value.ByType.ContainsKey(craft) || target == 0 ||
                    _craftInteractionId == 0 || target != _craftInteractionId ||
                    !CraftBook.Value.IsInPack(_craftInteractionPack, craft)) return false;
                actions.Craft(craft, target, count);
                _craftWorking = true;
                _craftBatchRemaining = checked((int)count);
                _pendingCraftSkillId = CraftBook.Value.ByType[craft].SkillId;
                _craftTimeline = 0;
                return true;
            }
            case "EndCraftingInteraction":
                _craftInteractionId = 0;
                _craftInteractionTemplate = 0;
                _craftInteractionPack = 0;
                ResetCraftProgress();
                return true;
            case "StopBatchCrafting":
                ResetCraftProgress();
                return true;
            default:
                return false;
        }
    }
}
