#nullable enable
using System.Globalization;
using AAEmu.GodotViewer.Lua;
using AAEmu.GodotViewer.Ui.X2.Scripting.World;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Ui.X2.Scripting.Api;

public partial interface IX2ItemsData
{
    /// <summary>All equipment instances currently visible on a unit. The optional Fields["equipSlot"] is the zero-based client slot.</summary>
    IReadOnlyList<X2InventoryItem> GetEquippedItems(string unit);
    X2InventoryItem? GetEquippedItem(string unit, int equipSlot);
    /// <summary>Static item-template data enriched with the tooltip fields known by the host.</summary>
    X2InventoryItem? GetItemInfo(uint itemType);
    void SetRepairMode(bool enabled);
    void RepairAllEquipment();
    bool UseEquippedItem(uint itemType);
    bool UnequipMateItem(string unit, int equipSlot);
    void PickupMateEquippedItem(string unit, int equipSlot);
    void SetEquipmentSecurityLock(bool locked);
}

public partial class NullItemsData
{
    private static string DefaultItemDatabasePath => ClientPaths.Database;
    private const int MaxStaticItemGrade = 12;
    private readonly string _itemDatabasePath;
    private readonly object _itemInfoGate = new();
    private readonly Dictionary<uint, X2InventoryItem?> _itemInfoCache = new();

    /// <summary>
    /// Creates the empty live-state provider. Static item templates are read lazily from the decrypted game database;
    /// pass a different read-only database path for tests or installations outside the standard client layout.
    /// </summary>
    public NullItemsData(string? itemDatabasePath = null)
        => _itemDatabasePath = string.IsNullOrWhiteSpace(itemDatabasePath) ? DefaultItemDatabasePath : itemDatabasePath;

    public virtual IReadOnlyList<X2InventoryItem> GetEquippedItems(string unit) => [];
    public virtual X2InventoryItem? GetEquippedItem(string unit, int equipSlot) => null;
    public virtual X2InventoryItem? GetItemInfo(uint itemType)
    {
        if (itemType == 0) return null;
        lock (_itemInfoGate)
        {
            if (_itemInfoCache.TryGetValue(itemType, out var cached)) return cached;
            X2InventoryItem? result = null;
            try
            {
                result = ReadItemInfo(itemType);
            }
            catch (SqliteException)
            {
                // Static content is optional in offline/test environments; an unavailable DB behaves like an unknown item.
            }
            catch (IOException)
            {
                // The source remains read-only and may be absent on machines without extracted client data.
            }
            _itemInfoCache[itemType] = result;
            return result;
        }
    }
    public virtual void SetRepairMode(bool enabled) { }
    public virtual void RepairAllEquipment() { }
    public virtual bool UseEquippedItem(uint itemType) => false;
    public virtual bool UnequipMateItem(string unit, int equipSlot) => false;
    public virtual void PickupMateEquippedItem(string unit, int equipSlot) { }
    public virtual void SetEquipmentSecurityLock(bool locked) { }

    private X2InventoryItem? ReadItemInfo(uint itemType)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _itemDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        AAEmu.GodotViewer.Data.ClientEnums.Ensure(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id,
                   COALESCE(NULLIF(n.en_us, ''), i.name, ''),
                   COALESCE(NULLIF(d.en_us, ''), i.description, ''),
                   COALESCE(ic.filename, ''), COALESCE(oi.filename, ''),
                   i.fixed_grade, i.max_stack_size,
                   CASE WHEN i.sellable IN ('t', 'true', '1', 1) THEN 1 ELSE 0 END,
                   i.use_skill_id, i.use_skill_lifetime, i.buff_id, i.impl_id,
                   COALESCE(i.bind_id, 0), i.level_requirement, i.expedition_level,
                   i.max_enchantable_grade,
                   CASE WHEN i.gradable IN ('t', 'true', '1', 1) THEN 1 ELSE 0 END,
                   CASE WHEN i.side_effect IN ('t', 'true', '1', 1) THEN 1 ELSE 0 END,
                   i.category_id, i.pickup_limit,
                   CASE WHEN EXISTS(SELECT 1 FROM item_armors a WHERE a.item_id = i.id)
                          OR EXISTS(SELECT 1 FROM item_weapons w WHERE w.item_id = i.id)
                          OR EXISTS(SELECT 1 FROM item_accessories x WHERE x.item_id = i.id)
                          OR EXISTS(SELECT 1 FROM item_backpacks b WHERE b.item_id = i.id)
                        THEN 1 ELSE 0 END,
                   COALESCE((SELECT b.backpack_type_id FROM item_backpacks b WHERE b.item_id = i.id LIMIT 1), 0),
                   COALESCE((SELECT a.slot_type_id FROM item_armors a WHERE a.item_id = i.id LIMIT 1),
                            (SELECT h.slot_type_id FROM item_weapons w
                               JOIN holdables h ON h.id = w.holdable_id
                              WHERE w.item_id = i.id LIMIT 1),
                            (SELECT x.slot_type_id FROM item_accessories x WHERE x.item_id = i.id LIMIT 1),
                            -1),
                   COALESCE((SELECT p.price FROM item_prices p
                              WHERE p.item_id = i.id AND p.currency_id = 0 LIMIT 1), 0),
                   COALESCE((SELECT p.refund FROM item_prices p
                              WHERE p.item_id = i.id AND p.currency_id = 0 LIMIT 1), 0),
                   COALESCE((SELECT e.default_equip_slot_id FROM enum_equip_slot_types e
                              WHERE e.id = COALESCE(
                                (SELECT a.slot_type_id FROM item_armors a WHERE a.item_id = i.id LIMIT 1),
                                (SELECT h.slot_type_id FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                                  WHERE w.item_id = i.id LIMIT 1),
                                (SELECT x.slot_type_id FROM item_accessories x WHERE x.item_id = i.id LIMIT 1))
                              LIMIT 1), -1),
                   COALESCE((SELECT e.aux_equip_slot_id FROM enum_equip_slot_types e
                              WHERE e.id = COALESCE(
                                (SELECT a.slot_type_id FROM item_armors a WHERE a.item_id = i.id LIMIT 1),
                                (SELECT h.slot_type_id FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                                  WHERE w.item_id = i.id LIMIT 1),
                                (SELECT x.slot_type_id FROM item_accessories x WHERE x.item_id = i.id LIMIT 1))
                              LIMIT 1), -1),
                   i.level, i.level_limit,
                   COALESCE((SELECT name FROM enum_item_impls WHERE id = i.impl_id), 'misc'),
                   COALESCE((SELECT en_us FROM localized_texts
                             WHERE tbl_name = 'item_categories' AND tbl_column_name = 'name'
                               AND idx = i.category_id LIMIT 1), ''),
                   COALESCE((SELECT name FROM enum_equip_slot_types WHERE id =
                       (SELECT h.slot_type_id FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                        WHERE w.item_id = i.id LIMIT 1)),
                       (SELECT name FROM enum_equip_slot_types WHERE id =
                        (SELECT a.slot_type_id FROM item_armors a WHERE a.item_id = i.id LIMIT 1)), ''),
                   (SELECT h.speed FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                    WHERE w.item_id = i.id LIMIT 1),
                   (SELECT w.durability_multiplier FROM item_weapons w WHERE w.item_id = i.id LIMIT 1),
                   (SELECT w.repairable FROM item_weapons w WHERE w.item_id = i.id LIMIT 1),
                   (SELECT name FROM enum_item_bind_types WHERE id = i.bind_id LIMIT 1),
                   (SELECT h.formula_dps FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                    WHERE w.item_id = i.id LIMIT 1),
                   (SELECT h.formula_mdps FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                    WHERE w.item_id = i.id LIMIT 1),
                   (SELECT h.formula_hdps FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                    WHERE w.item_id = i.id LIMIT 1),
                   (SELECT h.durability_ratio FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                    WHERE w.item_id = i.id LIMIT 1),
                   (SELECT holdable_durability_const FROM item_configs LIMIT 1),
                   (SELECT durability_const FROM item_configs LIMIT 1),
                   (SELECT h.damage_scale FROM item_weapons w JOIN holdables h ON h.id = w.holdable_id
                    WHERE w.item_id = i.id LIMIT 1),
                   (SELECT a.durability_multiplier FROM item_armors a WHERE a.item_id = i.id LIMIT 1),
                   (SELECT a.repairable FROM item_armors a WHERE a.item_id = i.id LIMIT 1),
                   (SELECT s.coverage FROM item_armors a JOIN wearable_slots s ON s.slot_type_id = a.slot_type_id
                    WHERE a.item_id = i.id LIMIT 1),
                   (SELECT k.durability_ratio FROM item_armors a JOIN wearable_kinds k ON k.armor_type_id = a.type_id
                    WHERE a.item_id = i.id LIMIT 1),
                   (SELECT k.armor_ratio FROM item_armors a JOIN wearable_kinds k ON k.armor_type_id = a.type_id
                    WHERE a.item_id = i.id LIMIT 1),
                   (SELECT k.magic_resistance_ratio FROM item_armors a JOIN wearable_kinds k ON k.armor_type_id = a.type_id
                    WHERE a.item_id = i.id LIMIT 1),
                   (SELECT wearable_durability_const FROM item_configs LIMIT 1),
                   (SELECT formula FROM wearable_formulas WHERE kind_id = 0 LIMIT 1),
                   (SELECT formula FROM wearable_formulas WHERE kind_id = 1 LIMIT 1),
                   i.name, i.description,
                   COALESCE((SELECT name FROM item_categories WHERE id = i.category_id LIMIT 1), '')
              FROM items i
              LEFT JOIN localized_texts n
                ON n.tbl_name = 'items' AND n.tbl_column_name = 'name' AND n.idx = i.id
              LEFT JOIN localized_texts d
                ON d.tbl_name = 'items' AND d.tbl_column_name = 'description' AND d.idx = i.id
              LEFT JOIN icons ic ON ic.id = i.icon_id
              LEFT JOIN icons oi ON oi.id = i.over_icon_id
             WHERE i.id = $id
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", (long)itemType);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var fixedGrade = reader.GetInt32(5);
        var maxStack = reader.GetInt32(6);
        var useSkill = reader.GetInt64(8);
        var equippable = reader.GetInt32(20) != 0;
        var equipSlotType = reader.GetInt32(22);
        var defaultEquipSlot = reader.GetInt32(25);
        var auxiliaryEquipSlot = reader.GetInt32(26);
        var fields = new Dictionary<string, object?>
        {
            ["description"] = UiTranslator.Shared.TranslateDatabaseText(reader.GetString(2), reader.GetString(53)),
            ["overIcon"] = IconPath(reader.GetString(4)),
            ["maxStack"] = (double)maxStack,
            ["stackable"] = maxStack > 1,
            ["sellable"] = reader.GetInt32(7) != 0,
            ["usable"] = useSkill != 0,
            ["consumable"] = useSkill != 0 && !equippable,
            ["combinedSkill"] = useSkill == 0 ? null : (double)useSkill,
            ["cooldown"] = (double)reader.GetInt64(9),
            ["combinedBuff"] = reader.GetInt64(10) == 0 ? null : (double)reader.GetInt64(10),
            ["implId"] = (double)reader.GetInt64(11),
            ["soulBoundable"] = reader.GetInt64(12) != 0,
            ["levelRequirement"] = (double)reader.GetInt32(13),
            ["expeditionLevelRequirement"] = (double)reader.GetInt32(14),
            ["maxGrade"] = (double)(reader.GetInt32(15) >= 0 ? reader.GetInt32(15) : MaxStaticItemGrade),
            ["gradeable"] = reader.GetInt32(16) != 0,
            ["sideEffect"] = reader.GetInt32(17) != 0,
            ["categoryId"] = (double)reader.GetInt64(18),
            ["pickupLimit"] = (double)reader.GetInt32(19),
            ["equippable"] = equippable,
            ["canEquip"] = equippable,
            ["backpackType"] = (double)reader.GetInt64(21),
            ["money_price"] = reader.GetInt64(23).ToString(CultureInfo.InvariantCulture),
            ["money_refund"] = reader.GetInt64(24).ToString(CultureInfo.InvariantCulture),
            ["level_requirement"] = (double)reader.GetInt32(13),
            ["level_limit"] = reader.IsDBNull(28) ? 0d : (double)reader.GetInt32(28),
            ["itemLevel"] = reader.IsDBNull(27) ? 0d : (double)reader.GetInt32(27),
            ["item_impl"] = reader.IsDBNull(29) ? "misc" : reader.GetString(29),
            ["category"] = UiTranslator.Shared.TranslateDatabaseText(reader.IsDBNull(30) ? "" : reader.GetString(30), reader.GetString(54)),
            ["isStackable"] = maxStack > 1,
            ["itemUsage"] = equippable ? "equip" : useSkill != 0 ? "use" : "material",
        };
        var slotType = reader.IsDBNull(31) ? "" : reader.GetString(31);
        if (slotType.Length > 0) fields["slotType"] = slotType;
        if (!reader.IsDBNull(32)) fields["attackDelay"] = reader.GetDouble(32) / 1000d;
        if (!reader.IsDBNull(33)) fields["durabilityMultiplier"] = reader.GetDouble(33);
        if (!reader.IsDBNull(34)) fields["repairable"] = reader.GetString(34) is "t" or "true" or "1";
        if (!reader.IsDBNull(35)) fields["soul_bind"] = reader.GetString(35);
        if (!reader.IsDBNull(36)) fields["formulaDps"] = reader.GetString(36);
        if (!reader.IsDBNull(37)) fields["formulaMagicDps"] = reader.GetString(37);
        if (!reader.IsDBNull(38)) fields["formulaHealDps"] = reader.GetString(38);
        if (!reader.IsDBNull(39)) fields["durabilityRatio"] = reader.GetDouble(39);
        if (!reader.IsDBNull(40)) fields["holdableDurabilityConst"] = reader.GetDouble(40);
        if (!reader.IsDBNull(41)) fields["durabilityConst"] = reader.GetDouble(41);
        if (!reader.IsDBNull(42)) fields["damageScale"] = reader.GetDouble(42);
        if (!reader.IsDBNull(43)) fields["durabilityMultiplier"] = reader.GetDouble(43);
        if (!reader.IsDBNull(44)) fields["repairable"] = reader.GetString(44) is "t" or "true" or "1";
        if (!reader.IsDBNull(45)) fields["armorCoverage"] = reader.GetDouble(45);
        if (!reader.IsDBNull(46)) fields["durabilityRatio"] = reader.GetDouble(46);
        if (!reader.IsDBNull(47)) fields["armorRatio"] = reader.GetDouble(47);
        if (!reader.IsDBNull(48)) fields["magicResistanceRatio"] = reader.GetDouble(48);
        if (!reader.IsDBNull(49)) fields["wearableDurabilityConst"] = reader.GetDouble(49);
        if (!reader.IsDBNull(50)) fields["formulaArmor"] = reader.GetString(50);
        if (!reader.IsDBNull(51)) fields["formulaMagicResistance"] = reader.GetString(51);
        if (equipSlotType >= 0)
        {
            fields["equipSlotType"] = (double)equipSlotType;
            fields["defaultEquipSlot"] = (double)defaultEquipSlot;
            fields["auxEquipSlot"] = (double)auxiliaryEquipSlot;
            fields["equipSlots"] = new[] { defaultEquipSlot, auxiliaryEquipSlot }
                .Where(slot => slot >= 0).Select(slot => slot + 1).ToArray();
        }
        return new X2InventoryItem
        {
            ItemType = checked((uint)reader.GetInt64(0)),
            Grade = fixedGrade >= 0 ? fixedGrade : 0,
            Count = 1,
            Name = UiTranslator.Shared.TranslateDatabaseText(reader.GetString(1), reader.GetString(52)),
            Icon = IconPath(reader.GetString(3)),
            Fields = fields,
        };
    }

    private static string IconPath(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "";
        var path = filename.Replace('\\', '/').TrimStart('/');
        return path.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ? path : $"ui/icon/{path}";
    }
}

public static partial class X2ItemsApi
{
    private static readonly string[] GradeNames =
    [
        "Basic", "Crude", "Grand", "Rare", "Arcane", "Heroic", "Unique",
        "Celestial", "Divine", "Epic", "Legendary", "Mythic", "Eternal",
    ];

    // item_grades.color_argb; color.alb and tooltip.lua require eight ARGB digits.
    private static readonly string[] GradeColors =
    [
        "FFBA976D", "FF949293", "FF77B064", "FF558FD7", "FFCB72D8", "FFD78B06", "FFE17853",
        "FFF95252", "FFCF7D5D", "FF8FA5CA", "FFBF7900", "FFC90B0B", "FFAE98FE",
    ];
    private static readonly double[] GradeDurability =
        [1, 0.5, 1.05, 1.095, 1.135, 1.17, 1.2, 1.225, 1.245, 1.26, 1.27, 1.275, 1.2795];
    private static readonly double[] GradeWeaponDps =
        [1, 0.8, 1.05, 1.1, 1.15, 1.2, 1.25, 1.35, 1.5, 1.7, 1.9, 2, 2.1];

    internal static void InstallCore(X2LuaHost host, X2GameContext context, IX2ItemsData data)
    {
        var pinMode = false;
        var repairMode = false;
        var securityLockMode = false;
        var securityUnlockMode = false;
        var slaveEquipChangeMode = false;
        var prelimBagInteraction = false;
        var prelimSlotLocks = new HashSet<int>();

        X2InventoryItem? Definition(LuaArgs a) => data.GetItemInfo(ItemType(a));
        X2InventoryItem? Equipped(LuaArgs a, int slotArg = 0, string unit = "player")
            => data.GetEquippedItem(unit, a.Int(slotArg));
        object? ItemInfo(X2InventoryItem? item) => item is null ? null : CompleteItemTable(item);
        object? ItemGuideInfo(LuaArgs a)
        {
            var definition = Definition(a);
            if (definition is null) return null;
            // The guide displays a static, pristine preview rather than a character-owned
            // instance. Apply its selected grade before deriving grade-dependent template
            // fields, and pair maxDurability with a full current durability for icon.lua.
            var preview = definition with { Grade = a.Int(1, definition.Grade) };
            var result = CompleteItemTable(preview);
            if (result.TryGetValue("maxDurability", out var maximum) && maximum is not null)
                result["durability"] = maximum;
            return result;
        }

        // X2Item: template queries and client-side interaction modes.
        host.Define("X2Item", "AllGradeTypes", _ => NumberArray(Enumerable.Range(0, GradeNames.Length)));
        host.Define("X2Item", "CanEquip", a => ItemFlag(Definition(a), "canEquip", "equippable"));
        host.Define("X2Item", "CheckSecondPassword", _ => true); // No second-password subsystem in this client.
        host.Define("X2Item", "CombinedBuff", a => ItemNumberOrNull(Definition(a), "combinedBuff"));
        host.Define("X2Item", "CombinedSkill", a => ItemNumberOrNull(Definition(a), "combinedSkill"));
        host.Define("X2Item", "Cooldown", a => Field(Definition(a), "cooldown"));
        host.Define("X2Item", "Count", a => (double)data.GetItemCount(ItemType(a)));
        host.Define("X2Item", "CountInBag", a => (double)data.GetItemCount(ItemType(a)));
        host.Define("X2Item", "CountInEquipment", a => (double)data.GetEquippedItems("player").Where(i => i.ItemType == ItemType(a)).Sum(i => i.Count));
        host.Define("X2Item", "Description", a => ItemString(Definition(a), "description"));
        host.Define("X2Item", "EnterPinMode", _ => { pinMode = true; return null; });
        host.Define("X2Item", "EnterRepairMode", _ => { repairMode = true; data.SetRepairMode(true); return null; });
        host.Define("X2Item", "EnterSecurityLockMode", _ => { securityLockMode = true; securityUnlockMode = false; return null; });
        host.Define("X2Item", "EnterSecurityUnlockMode", _ => { securityUnlockMode = true; securityLockMode = false; return null; });
        host.Define("X2Item", "EnterSlaveEquipChangeMode", _ => { slaveEquipChangeMode = true; return null; });
        // Native GetAllItems is the GM item catalogue; GM tooling is unavailable in the replacement client.
        host.Define("X2Item", "GetAllItems", _ => new LuaTable());
        host.Define("X2Item", "GetExpeditionLevelRequirement", a => ItemNumberOrNull(Definition(a), "expeditionLevelRequirement"));
        host.Define("X2Item", "GetGearScore", a => ItemNumber(Definition(a), "gearScore"));
        host.Define("X2Item", "GetItemGradeIconPath", a => GradeIcon(a.Int(0)));
        host.Define("X2Item", "GetItemIconSet", a => IconTable(data.GetItemInfo(ItemType(a)), a.Int(1)));
        host.Define("X2Item", "GetItemInfoByType", a => ItemInfo(Definition(a)));
        host.Define("X2Item", "GetItemInfoToItemGuide", ItemGuideInfo);
        host.Define("X2Item", "GetItemLinkedTextByItemType", a => LinkedText(Definition(a)));
        host.Define("X2Item", "GetItemSideEffect", a => ItemFlag(Definition(a), "sideEffect"));
        host.Define("X2Item", "GetLevelRequirement", a => ItemNumberOrNull(Definition(a), "levelRequirement"));
        host.Define("X2Item", "GetSecurityUnlockDelayTime", _ => 0.0);
        host.Define("X2Item", "GradeColor", a => GradeValue(GradeColors, a.Int(0), "FFFFFFFF"));
        host.Define("X2Item", "GradeName", a => GradeValue(GradeNames, a.Int(0), ""));
        host.Define("X2Item", "GroupDescription", _ => ""); // Groups are content-driven and absent until supplied by the host.
        host.Define("X2Item", "GroupName", _ => "");
        host.Define("X2Item", "GroupTypes", _ => new LuaTable());
        host.Define("X2Item", "Info", a => ItemInfo(Definition(a)));
        host.Define("X2Item", "InfoFromLink", a => ItemInfo(ParseLinkedItem(data, a.Str(0))));
        host.Define("X2Item", "IsBundle", a => ItemFlag(Definition(a), "bundle"));
        host.Define("X2Item", "IsConsumable", a => ItemFlag(Definition(a), "consumable"));
        host.Define("X2Item", "IsEquippable", a => ItemFlag(Definition(a), "equippable", "canEquip"));
        host.Define("X2Item", "IsEquipped", a => data.GetEquippedItems("player").Any(i => i.ItemType == ItemType(a)));
        host.Define("X2Item", "IsGradeable", a => ItemFlag(Definition(a), "gradeable"));
        host.Define("X2Item", "IsInPinMode", _ => pinMode);
        host.Define("X2Item", "IsInRange", _ => null); // Native binding is a command-style range check with no Lua result.
        host.Define("X2Item", "IsInRepairMode", _ => repairMode);
        host.Define("X2Item", "IsInSecurityLockMode", _ => securityLockMode);
        host.Define("X2Item", "IsInSecurityUnlockMode", _ => securityUnlockMode);
        host.Define("X2Item", "IsInSlaveEquipChangeMode", _ => slaveEquipChangeMode);
        host.Define("X2Item", "IsLimitGrade", a => a.Int(1) >= ItemInt(Definition(a), "maxGrade", GradeNames.Length - 1));
        host.Define("X2Item", "IsPetArmor", a => ItemFlag(Definition(a), "petArmor"));
        host.Define("X2Item", "IsSellable", a => ItemFlag(Definition(a), "sellable"));
        host.Define("X2Item", "IsShowEquipItemLockUI", _ => true);
        host.Define("X2Item", "IsSoulBoundable", a => ItemFlag(Definition(a), "soulBoundable"));
        host.Define("X2Item", "IsStackable", a => ItemFlag(Definition(a), "stackable") || ItemInt(Definition(a), "maxStack", 1) > 1);
        host.Define("X2Item", "IsUsable", a => ItemFlag(Definition(a), "usable", "consumable"));
        host.Define("X2Item", "LeavePinMode", _ => { pinMode = false; return null; });
        host.Define("X2Item", "LeaveRepairMode", _ => { repairMode = false; data.SetRepairMode(false); return null; });
        host.Define("X2Item", "LeaveSecurityLockMode", _ => { securityLockMode = false; return null; });
        host.Define("X2Item", "LeaveSecurityUnlockMode", _ => { securityUnlockMode = false; return null; });
        host.Define("X2Item", "LeaveSlaveEquipChangeMode", _ => { slaveEquipChangeMode = false; return null; });
        host.Define("X2Item", "Name", a => Definition(a)?.Name ?? "");
        host.Define("X2Item", "NoPoorGradeTypes", _ => NumberArray(Enumerable.Range(1, GradeNames.Length - 1)));
        host.Define("X2Item", "RepairAll", _ => { data.RepairAllEquipment(); return null; });
        host.Define("X2Item", "RepairAllCost", _ => data.GetEquippedItems("player").Sum(i => ItemLong(i, "repairCost", 0)).ToString(CultureInfo.InvariantCulture));
        host.Define("X2Item", "StatDelta", a => { data.SendItemsCommand(new("X2Item", "StatDelta", a.Values)); return null; });
        host.Define("X2Item", "Stats", a => Field(Definition(a), "stats") as LuaTable ?? new LuaTable());

        // X2Equipment: equipped-instance queries. Optional instance Fields are copied verbatim into tooltip tables.
        host.Define("X2Equipment", "DameagedItemCount", _ => (double)Damaged(data).Count);
        host.Define("X2Equipment", "DameagedItems", a => DamagedItems(data, a.Int(0, int.MaxValue)));
        host.Define("X2Equipment", "EndPrelimBagInteraction", _ => { prelimBagInteraction = false; return null; });
        host.Define("X2Equipment", "FindEquippedItemByType", a =>
        {
            var found = data.GetEquippedItems("player").FirstOrDefault(i => i.ItemType == ItemType(a));
            return found is null ? null : ItemInt(found, "equipSlot", 0);
        });
        host.Define("X2Equipment", "GetBackPackGoodsInfo", a => ItemInfo(data.GetEquippedItems(a.Str(0) ?? "player").FirstOrDefault(i => ItemInt(i, "backpackType", 0) != 0)));
        host.Define("X2Equipment", "GetEquipSlotByEquipSlotType", a => (double)FindSlotByType(data, a.Int(0)));
        host.Define("X2Equipment", "GetEquippedItemInfo", a => ItemInfo(data.GetEquippedItem(a.Str(0) ?? "player", a.Int(1))));
        host.Define("X2Equipment", "GetEquippedItemTooltipInfo", a => ItemInfo(data.GetEquippedItem("player", a.Int(0))));
        // equipped_item.alb branches on itemType == 0 to show the slot label for an empty slot.
        // A missing key instead enters ShowTooltip with an incomplete item table.
        host.Define("X2Equipment", "GetEquippedItemTooltipText", a => ItemInfo(data.GetEquippedItem(a.Str(0) ?? "player", a.Int(1))) ?? new LuaTable { ["itemType"] = 0d });
        host.Define("X2Equipment", "GetEquippedItemType", a => (double)(Equipped(a)?.ItemType ?? 0));
        host.Define("X2Equipment", "GetLinkText", a => LinkedText(Equipped(a)));
        host.Define("X2Equipment", "GetPrelimLinkText", a => LinkedText(Equipped(a)));
        // keyed by equip slot type: { isLocked, isDisabled } (preliminary_equipments.lua indexes every slot)
        host.Define("X2Equipment", "GetPrelimSlotLockInfo", _ =>
        {
            var t = new LuaTable();
            for (var slot = 0; slot <= 40; slot++)
                t[(double)slot] = new LuaTable { ["isLocked"] = prelimSlotLocks.Contains(slot), ["isDisabled"] = false };
            return t;
        });
        host.Define("X2Equipment", "GetPreliminaryItemTooltipText", a => ItemInfo(Equipped(a)));
        host.Define("X2Equipment", "HasAnyPrelimEquipments", _ => prelimBagInteraction);
        host.Define("X2Equipment", "IsEquippedItemByType", a => data.GetEquippedItems("player").Any(i => i.ItemType == ItemType(a)));
        host.Define("X2Equipment", "IsLocked", a => ItemFlag(Equipped(a), "locked"));
        host.Define("X2Equipment", "IsMateEquippableSlot", a => MateSlot(data, a.Str(0), a.Int(1)));
        host.Define("X2Equipment", "IsSoulBoundedItem", a => ItemFlag(Equipped(a), "soulBound", "soulBounded"));
        host.Define("X2Equipment", "ItemDurability", a => DurabilityTable(Equipped(a)));
        host.Define("X2Equipment", "ItemGemStats", a => Field(Equipped(a), "gemStats") as LuaTable ?? new LuaTable());
        host.Define("X2Equipment", "ItemIdentifier", a => ItemIdentifier(Equipped(a)));
        host.Define("X2Equipment", "ItemRepairCost", a => ItemLong(Equipped(a), "repairCost", 0).ToString(CultureInfo.InvariantCulture));
        host.Define("X2Equipment", "ItemRequireLevel", a => (double)ItemInt(Equipped(a), "levelRequirement", 0));
        host.Define("X2Equipment", "ItemStack", a => (double)(Equipped(a)?.Count ?? 0));
        host.Define("X2Equipment", "MateUnequipItem", a => data.UnequipMateItem(a.Str(0) ?? "", a.Int(1)));
        host.Define("X2Equipment", "PickupMateEquippedItem", a => { data.PickupMateEquippedItem(a.Str(0) ?? "", a.Int(1)); return null; });
        host.Define("X2Equipment", "SecurityLock", _ => { data.SetEquipmentSecurityLock(true); return null; });
        host.Define("X2Equipment", "SecurityUnlock", _ => { data.SetEquipmentSecurityLock(false); return null; });
        host.Define("X2Equipment", "SetPrelimSlotLock", a => { if (a.Bool(1)) prelimSlotLocks.Add(a.Int(0)); else prelimSlotLocks.Remove(a.Int(0)); return null; });
        host.Define("X2Equipment", "StartPrelimBagInteraction", _ => { prelimBagInteraction = true; return null; });
        host.Define("X2Equipment", "SwapPrelimEquipments", a => { data.SendItemsCommand(new("X2Equipment", "SwapPrelimEquipments", a.Values)); return null; });
        host.Define("X2Equipment", "UseEquippedItemByType", a => data.UseEquippedItem(ItemType(a)));
    }

    private static uint ItemType(LuaArgs a, int index = 0) => unchecked((uint)Math.Max(0, a.Num(index)));

    private static object? Field(X2InventoryItem? item, string key)
        => item is not null && item.Fields.TryGetValue(key, out var value) ? value : null;

    private static bool ItemFlag(X2InventoryItem? item, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Field(item, key);
            if (value is bool b) return b;
            if (value is double d) return d != 0;
            if (value is int i) return i != 0;
        }
        return false;
    }

    private static int ItemInt(X2InventoryItem? item, string key, int fallback)
        => Field(item, key) switch { double d => (int)d, int i => i, long l => (int)l, _ => fallback };

    private static long ItemLong(X2InventoryItem? item, string key, long fallback)
        => Field(item, key) switch { double d => (long)d, int i => i, long l => l, ulong u when u <= long.MaxValue => (long)u, _ => fallback };

    private static double ItemNumber(X2InventoryItem? item, string key, double fallback = 0)
        => Field(item, key) switch { double d => d, int i => i, long l => l, float f => f, _ => fallback };

    private static object? ItemNumberOrNull(X2InventoryItem? item, string key)
        => Field(item, key) is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;

    private static string ItemString(X2InventoryItem? item, string key)
        => Field(item, key)?.ToString() ?? "";

    internal static LuaTable CompleteItemTable(X2InventoryItem item)
    {
        var result = ItemTable(item);
        result.TryAdd("lookType", (double)item.ItemType);
        result.TryAdd("grade", GradeValue(GradeNames, item.Grade, ""));
        result.TryAdd("stackSize", (double)item.Count);
        result.TryAdd("iconPath", item.Icon);
        result.TryAdd("gradeColor", GradeValue(GradeColors, item.Grade, "FFFFFFFF"));
        result.TryAdd("gradeName", GradeValue(GradeNames, item.Grade, ""));
        result.TryAdd("gradeIcon", GradeIcon(item.Grade));
        result.TryAdd("overIcon", "");
        if (!result.ContainsKey("maxDurability") && MaxDurability(item) is { } maxDurability)
            result["maxDurability"] = maxDurability;
        if (ItemString(item, "item_impl") == "weapon" &&
            WeaponStat(item, "formulaDps") is { } dps && dps > 0)
        {
            result["DPS"] = dps;
            var meanDamage = dps * ItemNumber(item, "attackDelay");
            var spread = ItemNumber(item, "damageScale") * 0.01;
            result["minDamage"] = meanDamage * (1 - spread);
            result["maxDamage"] = meanDamage * (1 + spread);
        }
        if (ItemString(item, "item_impl") == "armor")
        {
            if (WeaponStat(item, "formulaArmor") is { } baseArmor && baseArmor > 0)
                result["armor"] = (double)(int)((int)(baseArmor * ItemNumber(item, "armorRatio") *
                    0.0099999998) * ItemNumber(item, "armorCoverage") * 0.0099999998);
            if (WeaponStat(item, "formulaMagicResistance") is { } baseResistance && baseResistance > 0)
                result["magicResistance"] = (double)(int)((int)(baseResistance *
                    ItemNumber(item, "magicResistanceRatio") * 0.0099999998) *
                    ItemNumber(item, "armorCoverage") * 0.0099999998);
        }
        return result;
    }

    internal static double? MaxDurability(X2InventoryItem? item)
    {
        if (item is null) return null;
        var impl = ItemString(item, "item_impl");
        if (impl is not ("weapon" or "armor")) return null;
        var grade = item.Grade;
        if (grade < 0 || grade >= GradeDurability.Length) return null;
        var ratio = ItemNumber(item, "durabilityRatio");
        var holdableConst = impl == "weapon" ? ItemNumber(item, "holdableDurabilityConst") :
            ItemNumber(item, "armorCoverage");
        var durabilityConst = ItemNumber(item, "durabilityConst");
        var multiplier = ItemNumber(item, "durabilityMultiplier");
        if (ratio <= 0 || holdableConst <= 0 || durabilityConst <= 0 || multiplier <= 0) return null;
        // EquipItem max durability uses the content's coverage or holdable constant, ratio and grade.
        var baseDurability = (int)((int)(holdableConst * 100 + 0.5) *
            (int)(ratio * 1000 + 0.5) * GradeDurability[grade] * 1000 * 0.00000001) * durabilityConst;
        return (byte)Math.Round(baseDurability * multiplier * 0.0099999998);
    }

    private static double? WeaponStat(X2InventoryItem item, string key)
    {
        var formulaText = ItemString(item, key);
        if (formulaText.Length == 0 || item.Grade < 0 || item.Grade >= GradeWeaponDps.Length) return null;
        return new WeaponFormulaParser(formulaText, ItemNumber(item, "itemLevel"),
            GradeWeaponDps[item.Grade]).Evaluate();
    }

    // Holdable formula_dps values use arithmetic over item_level and item_grade. Unknown
    // expressions stay absent rather than displaying an invented zero damage value.
    private sealed class WeaponFormulaParser(string source, double level, double grade)
    {
        private int _offset;
        private bool _valid = true;

        public double? Evaluate()
        {
            var value = Sum();
            Space();
            return _valid && _offset == source.Length && double.IsFinite(value) ? value : null;
        }

        private double Sum()
        {
            var value = Product();
            while (true)
            {
                Space();
                if (Take('+')) value += Product();
                else if (Take('-')) value -= Product();
                else return value;
            }
        }

        private double Product()
        {
            var value = Unary();
            while (true)
            {
                Space();
                if (Take('*')) value *= Unary();
                else if (Take('/')) value /= Unary();
                else return value;
            }
        }

        private double Unary()
        {
            Space();
            if (Take('+')) return Unary();
            if (Take('-')) return -Unary();
            return Power();
        }

        private double Power()
        {
            var value = Primary();
            Space();
            return Take('^') ? Math.Pow(value, Unary()) : value;
        }

        private double Primary()
        {
            Space();
            if (Take('('))
            {
                var value = Sum();
                Space();
                if (!Take(')')) _valid = false;
                return value;
            }
            var start = _offset;
            while (_offset < source.Length &&
                   (char.IsAsciiLetter(source[_offset]) || source[_offset] == '_')) _offset++;
            if (_offset > start)
            {
                var symbol = source[start.._offset];
                if (symbol is "floor" or "ceil")
                {
                    Space();
                    if (!Take('(')) return Invalid();
                    var argument = Sum();
                    Space();
                    if (!Take(')')) return Invalid();
                    return symbol == "floor" ? Math.Floor(argument) : Math.Ceiling(argument);
                }
                return source[start.._offset] switch
                {
                    "item_level" => level,
                    "item_grade" => grade,
                    _ => Invalid(),
                };
            }
            while (_offset < source.Length &&
                   (char.IsAsciiDigit(source[_offset]) || source[_offset] == '.')) _offset++;
            if (_offset > start && double.TryParse(source.AsSpan(start, _offset - start),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            return Invalid();
        }

        private double Invalid() { _valid = false; return 0; }
        private void Space() { while (_offset < source.Length && char.IsWhiteSpace(source[_offset])) _offset++; }
        private bool Take(char value)
        {
            if (_offset >= source.Length || source[_offset] != value) return false;
            _offset++;
            return true;
        }
    }

    private static LuaTable ItemArray(IEnumerable<X2InventoryItem> items)
    {
        var table = new LuaTable();
        var index = 1;
        foreach (var item in items) table[(double)index++] = CompleteItemTable(item);
        return table;
    }

    private static LuaTable NumberArray(IEnumerable<int> values)
    {
        var table = new LuaTable();
        var index = 1;
        foreach (var value in values) table[(double)index++] = (double)value;
        return table;
    }

    private static string GradeValue(string[] values, int grade, string fallback)
        => grade >= 0 && grade < values.Length ? values[grade] : fallback;

    private static readonly string[] GradeIcons =
    [
        "item_grade_1common.dds", "item_grade_0poor.dds", "item_grade_2uncommon.dds",
        "item_grade_3rare.dds", "item_grade_4ancient.dds", "item_grade_5heroic.dds",
        "item_grade_6unique.dds", "item_grade_7artifact.dds", "item_grade_8wonder.dds",
        "item_grade_9epic.dds", "item_grade_10legendary.dds", "item_grade_11mythic.dds",
        "item_grade_12arche.dds",
    ];
    private static string GradeIcon(int grade) => "ui/icon/" + GradeValue(GradeIcons, grade, GradeIcons[0]);

    private static object? IconTable(X2InventoryItem? item, int requestedGrade)
    {
        if (item is null) return null;
        var grade = requestedGrade >= 0 ? requestedGrade : item.Grade;
        return new LuaTable
        {
            ["icon"] = item.Icon,
            ["overIcon"] = Field(item, "overIcon")?.ToString() ?? "",
            ["gradeIcon"] = GradeIcon(grade),
        };
    }

    private static string LinkedText(X2InventoryItem? item)
        => item is null ? "" : $"|c{GradeValue(GradeColors, item.Grade, "FFFFFFFF")}[{item.Name}]|r|Hitem:{item.ItemType}:{item.Grade}|h";

    private static X2InventoryItem? ParseLinkedItem(IX2ItemsData data, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        const string marker = "|Hitem:";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = text.IndexOfAny([':', '|'], start);
        var token = end < 0 ? text[start..] : text[start..end];
        return uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var itemType)
            ? data.GetItemInfo(itemType) : null;
    }

    private static List<X2InventoryItem> Damaged(IX2ItemsData data) => data.GetEquippedItems("player")
        .Where(i => ItemInt(i, "maxDurability", 0) > 0 && ItemInt(i, "durability", 0) < ItemInt(i, "maxDurability", 0))
        .ToList();

    private static LuaMulti DamagedItems(IX2ItemsData data, int limit)
    {
        var items = Damaged(data).Take(Math.Max(0, limit)).ToArray();
        var types = new LuaTable();
        var grades = new LuaTable();
        var slots = new LuaTable();
        for (var i = 0; i < items.Length; i++)
        {
            var key = (double)(i + 1);
            types[key] = (double)items[i].ItemType;
            grades[key] = (double)items[i].Grade;
            slots[key] = (double)ItemInt(items[i], "equipSlot", i);
        }
        return new LuaMulti(types, grades, slots);
    }

    private static object? DurabilityTable(X2InventoryItem? item)
    {
        if (item is null || ItemInt(item, "maxDurability", 0) <= 0) return null;
        return new LuaTable
        {
            ["current"] = (double)ItemInt(item, "durability", 0),
            ["max"] = (double)ItemInt(item, "maxDurability", 0),
        };
    }

    private static object? ItemIdentifier(X2InventoryItem? item)
        => item is null ? null : new LuaMulti((double)item.ItemType, (double)item.Grade);

    private static int FindSlotByType(IX2ItemsData data, int equipSlotType)
    {
        var match = data.GetEquippedItems("player").FirstOrDefault(i => ItemInt(i, "equipSlotType", int.MinValue) == equipSlotType);
        return match is null ? equipSlotType : ItemInt(match, "equipSlot", equipSlotType);
    }

    private static bool MateSlot(IX2ItemsData data, string? unit, int slot)
    {
        if (string.IsNullOrEmpty(unit) || (!unit.StartsWith("playerpet", StringComparison.Ordinal) && unit != "playerpet")) return false;
        var item = data.GetEquippedItem(unit, slot);
        return item is null || ItemFlag(item, "mateEquippable");
    }
}
