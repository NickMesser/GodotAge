#nullable enable
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Data;

/// <summary>An equipped client item. Slot uses the server's zero-based EquipmentItemSlot values.</summary>
public sealed record ClientEquippedItem(uint TemplateId, byte Grade = 0, int Slot = -1,
    int ElementLevel = 0, int FilledGemCount = 0);

/// <summary>
/// Evaluates character unit formulas from the same read-only game database used by the viewer.
/// Formulas are compiled with Jace (see <see cref="Formula"/>). The calculator is deliberately
/// independent of Godot and never opens a server connection.
/// </summary>
public sealed class ClientCharacterStatCalculator : IDisposable
{
    private sealed record Modifier(UnitAttribute Attribute, UnitModifierType Type, double Value, int LinearLevelBonus);
    private sealed record WearableTemplate(int Level, double ArmorGrade, double MagicResistanceGrade,
        double ArmorRatio, double MagicResistanceRatio, double Coverage);
    private sealed record ItemGrade(double HoldableDps, double HoldableMagicDps,
        double WearableArmor, double WearableMagicResistance, double HoldableArmor = 1d);
    private sealed record EquipmentTemplate(int Level, int Kind, int SlotType, int Speed, int DamageScale,
        double GearScoreMultiplier, Formula? Dps, Formula? MagicDps, Formula? HealDps,
        Formula? Armor = null, Formula? MagicResistance = null);
    private readonly string _path;
    private readonly Dictionary<UnitFormulaKind, Formula> _formulas = [];
    private readonly Dictionary<uint, List<Modifier>> _itemModifiers = [];
    private readonly Dictionary<uint, List<Modifier>> _buffModifiers = [];
    private readonly Dictionary<uint, List<(int Pieces, uint BuffId)>> _itemSetBonuses = [];
    private readonly Dictionary<uint, uint> _itemSetByTemplate = [];
    private readonly Dictionary<uint, WearableTemplate> _wearableTemplates = [];
    private readonly Dictionary<uint, EquipmentTemplate> _equipmentTemplates = [];
    private readonly Dictionary<byte, ItemGrade> _grades = [];
    private readonly Dictionary<uint, Formula> _generalFormulas = [];
    private Formula? _baseArmorFormula;
    private Formula? _baseMagicResistanceFormula;
    private readonly Dictionary<UnitAttribute, (double Minimum, double Maximum)> _limits = [];
    private readonly object _gate = new();
    private bool _loaded;

    public ClientCharacterStatCalculator(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _path = Path.GetFullPath(databasePath);
    }

    /// <summary>Calculates the keys consumed by the character sheet from current client-known state.</summary>
    public IReadOnlyDictionary<string, double> Calculate(int level, int heirLevel, byte race, byte gender,
        IReadOnlyList<int> activeAbilities, IReadOnlyList<uint> equipmentTemplateIds,
        IReadOnlyList<(uint BuffId, uint Stacks)> activeBuffs)
        => Calculate(level, heirLevel, race, gender, activeAbilities,
            equipmentTemplateIds.Select(id => new ClientEquippedItem(id)).ToArray(), activeBuffs);

    /// <summary>Grade and slot aware character-sheet calculation.</summary>
    public IReadOnlyDictionary<string, double> Calculate(int level, int heirLevel, byte race, byte gender,
        IReadOnlyList<int> activeAbilities, IReadOnlyList<ClientEquippedItem> equipment,
        IReadOnlyList<(uint BuffId, uint Stacks)> activeBuffs)
    {
        EnsureLoaded();
        EnsureEquipmentLoaded(equipment);
        var equipmentTemplateIds = equipment.Select(item => item.TemplateId).ToArray();
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var attributes = new Dictionary<UnitAttribute, double>();
        var effectiveBuffs = activeBuffs.ToList();
        foreach (var set in equipmentTemplateIds
                     .Where(_itemSetByTemplate.ContainsKey)
                     .GroupBy(id => _itemSetByTemplate[id]))
        {
            if (!_itemSetBonuses.TryGetValue(set.Key, out var bonuses)) continue;
            foreach (var (pieces, buffId) in bonuses)
                if (set.Count() >= pieces && effectiveBuffs.All(buff => buff.BuffId != buffId))
                    effectiveBuffs.Add((buffId, 1));
        }
        // Unit.ApplyWeaponWieldBuff derives these buffs from the equipped holdables; they are not item
        // modifiers and need not be present in the network buff list when the local equipment is known.
        var mainItem = equipment.LastOrDefault(item => item.Slot == 15);
        var offItem = equipment.LastOrDefault(item => item.Slot == 16);
        var mainTemplate = mainItem is null ? null : _equipmentTemplates.GetValueOrDefault(mainItem.TemplateId);
        var offTemplate = offItem is null ? null : _equipmentTemplates.GetValueOrDefault(offItem.TemplateId);
        uint wieldBuff = mainTemplate?.SlotType == 16 ? 8227u
            : offTemplate?.SlotType == 20 ? 8226u
            : mainTemplate?.Kind == 1 && offTemplate?.Kind == 1 ? 4899u : 0u;
        if (wieldBuff != 0 && effectiveBuffs.All(buff => buff.BuffId != wieldBuff))
            effectiveBuffs.Add((wieldBuff, 1));

        double Eval(UnitFormulaKind kind, Dictionary<string, double> args)
        {
            if (!_formulas.TryGetValue(kind, out var formula)) return 0d;
            return formula.Evaluate(args);
        }
        double EvalGeneral(uint id, Dictionary<string, double> formulaArgs)
        {
            return _generalFormulas.TryGetValue(id, out var formula) ? formula.Evaluate(formulaArgs) : 0d;
        }
        double Compose(UnitAttribute attribute, double value)
        {
            var rows = new List<Modifier>();
            foreach (var id in equipmentTemplateIds)
                if (_itemModifiers.TryGetValue(id, out var equipmentRows)) rows.AddRange(equipmentRows);
            foreach (var (id, stacks) in effectiveBuffs)
                if (_buffModifiers.TryGetValue(id, out var buffRows))
                    foreach (var row in buffRows)
                        rows.Add(row with { Value = row.Value * Math.Max(1, stacks) });

            foreach (var row in rows.Where(row => row.Attribute == attribute && row.Type == UnitModifierType.Value))
                value += row.Value + row.LinearLevelBonus * Math.Max(0, level - 1);
            foreach (var row in rows.Where(row => row.Attribute == attribute && row.Type == UnitModifierType.Percent))
                value += value * (row.Value + row.LinearLevelBonus * Math.Max(0, level - 1)) / 100d;
            if (attribute is not (UnitAttribute.DropRateMul or UnitAttribute.ExpMul or UnitAttribute.LivingPointGainMul)
                && _limits.TryGetValue(attribute, out var limit))
                value = Math.Clamp(value, limit.Minimum, limit.Maximum);
            return value;
        }
        var primaryArgs = new Dictionary<string, double> { ["level"] = level };
        foreach (var (kind, attribute, key) in new[]
                 {
                     (UnitFormulaKind.Str, UnitAttribute.Str, "str"),
                     (UnitFormulaKind.Dex, UnitAttribute.Dex, "dex"),
                     (UnitFormulaKind.Sta, UnitAttribute.Sta, "sta"),
                     (UnitFormulaKind.Int, UnitAttribute.Int, "int"),
                     (UnitFormulaKind.Spi, UnitAttribute.Spi, "spi"),
                 })
        {
            var current = Math.Truncate(Compose(attribute, Eval(kind, primaryArgs)));
            attributes[attribute] = current;
            values[key] = current;
        }
        // Character.cs binds these exact names for the level-derived resource and recovery formulas.
        var args = new Dictionary<string, double>(primaryArgs, StringComparer.Ordinal)
        {
            ["heir_level"] = heirLevel,
            ["str"] = attributes[UnitAttribute.Str], ["dex"] = attributes[UnitAttribute.Dex],
            ["sta"] = attributes[UnitAttribute.Sta], ["int"] = attributes[UnitAttribute.Int],
            ["spi"] = attributes[UnitAttribute.Spi], ["fai"] = 0,
        };
        foreach (var (kind, attribute, key) in new[]
                 {
                     (UnitFormulaKind.MaxHealth, UnitAttribute.MaxHealth, "max_health"),
                     (UnitFormulaKind.MaxMana, UnitAttribute.MaxMana, "max_mana"),
                     (UnitFormulaKind.HealthRegen, UnitAttribute.HealthRegen, "health_regen"),
                     (UnitFormulaKind.ManaRegen, UnitAttribute.ManaRegen, "mana_regen"),
                     (UnitFormulaKind.PersistentHealthRegen, UnitAttribute.PersistentHealthRegen, "persistent_health_regen"),
                     (UnitFormulaKind.PersistentManaRegen, UnitAttribute.PersistentManaRegen, "persistent_mana_regen"),
                     (UnitFormulaKind.Armor, UnitAttribute.Armor, "armor"),
                     (UnitFormulaKind.MagicResist, UnitAttribute.MagicResist, "magic_resist"),
                 })
        {
            var raw = Eval(kind, args);
            // Armor/resist equipment is added below before their one and only modifier composition.
            var composed = kind is UnitFormulaKind.Armor or UnitFormulaKind.MagicResist
                ? raw : Compose(attribute, raw);
            if (kind is UnitFormulaKind.PersistentHealthRegen or UnitFormulaKind.PersistentManaRegen) composed /= 5d;
            // the client rounds health/mana regeneration up (the other derived values here truncate)
            var result = kind is UnitFormulaKind.HealthRegen or UnitFormulaKind.ManaRegen
                ? Math.Ceiling(composed) : Math.Truncate(composed);
            attributes[attribute] = result;
            values[key] = result;
        }
        var armorBase = 0d;
        var magicResistanceBase = 0d;
        foreach (var templateId in equipmentTemplateIds)
        {
            if (!_wearableTemplates.TryGetValue(templateId, out var template)) continue;
            var equipped = equipment.Last(item => item.TemplateId == templateId);
            var grade = _grades.GetValueOrDefault(equipped.Grade) ?? _grades.GetValueOrDefault((byte)0)
                ?? new ItemGrade(1, 1, 1, 1);
            var itemArgs = new Dictionary<string, double>
            {
                ["item_level"] = template.Level,
                ["item_grade"] = grade.WearableArmor,
            };
            if (_baseArmorFormula is not null)
                armorBase += (int)(_baseArmorFormula.Evaluate(itemArgs) * template.ArmorRatio * 0.01 * template.Coverage * 0.01);
            itemArgs["item_grade"] = grade.WearableMagicResistance;
            if (_baseMagicResistanceFormula is not null)
                magicResistanceBase += (int)(_baseMagicResistanceFormula.Evaluate(itemArgs) * template.MagicResistanceRatio * 0.01 * template.Coverage * 0.01);
        }
        // Shields (and any other holdable with a formula_armor) add their own armor, e.g. the level 1 shield:
        // floor(1 ^ 1.1 * 85 + 100) * grade * 0.27 = 49
        foreach (var held in equipment)
        {
            if (!_equipmentTemplates.TryGetValue(held.TemplateId, out var holdable) || holdable.Kind != 1) continue;
            var heldGrade = _grades.GetValueOrDefault(held.Grade) ?? _grades.GetValueOrDefault((byte)0)
                ?? new ItemGrade(1, 1, 1, 1);
            if (holdable.Armor is not null)
                armorBase += (int)holdable.Armor.Evaluate(new Dictionary<string, double>
                    { ["item_level"] = holdable.Level, ["item_grade"] = heldGrade.HoldableArmor });
            if (holdable.MagicResistance is not null)
                magicResistanceBase += (int)holdable.MagicResistance.Evaluate(new Dictionary<string, double>
                    { ["item_level"] = holdable.Level, ["item_grade"] = heldGrade.HoldableArmor });
        }
        var armor = Math.Truncate(Compose(UnitAttribute.Armor,
            values.GetValueOrDefault("armor") + armorBase));
        var magicResist = Math.Truncate(Compose(UnitAttribute.MagicResist,
            values.GetValueOrDefault("magic_resist") + magicResistanceBase));
        attributes[UnitAttribute.Armor] = values["armor"] = armor;
        attributes[UnitAttribute.MagicResist] = values["magic_resist"] = magicResist;
        // The server formulas use the same facet normalization and bindings as Character.cs.
        var facets = Eval(UnitFormulaKind.Facet, args);
        var shieldEquipped = offTemplate?.SlotType == 20;
        if (facets != 0)
        {
            var baseMissArgs = new Dictionary<string, double>(args, StringComparer.Ordinal) { ["level_diff"] = 0d };
            var baseMiss = Eval(UnitFormulaKind.BaseMissPercent, baseMissArgs);
            foreach (var (kind, attr, key) in new[]
                     {
                         (UnitFormulaKind.MeleeAntiMiss, UnitAttribute.MeleeAntiMiss, "melee_success_rate"),
                         (UnitFormulaKind.RangedAntiMiss, UnitAttribute.RangedAntiMiss, "ranged_success_rate"),
                         (UnitFormulaKind.SpellAntiMiss, UnitAttribute.SpellAntiMiss, "spell_success_rate"),
                         (UnitFormulaKind.MeleeCritical, UnitAttribute.MeleeCritical, "melee_critical_rate"),
                         (UnitFormulaKind.RangedCritical, UnitAttribute.RangedCritical, "ranged_critical_rate"),
                         (UnitFormulaKind.SpellCritical, UnitAttribute.SpellCritical, "spell_critical_rate"),
                         (UnitFormulaKind.HealCritical, UnitAttribute.HealCritical, "heal_critical_rate"),
                         (UnitFormulaKind.MeleeParry, UnitAttribute.MeleeParry, "melee_parry_rate"),
                         (UnitFormulaKind.RangedParry, UnitAttribute.RangedParry, "ranged_parry_rate"),
                         (UnitFormulaKind.Block, UnitAttribute.Block, "block_rate"),
                         (UnitFormulaKind.Dodge, UnitAttribute.Dodge, "dodge_rate"),
                     })
            {
                // Character.BlockRate returns zero unless the offhand is a shield (holdable slot type 20)
                if (kind == UnitFormulaKind.Block && !shieldEquipped) { values[key] = 0d; continue; }
                var formulaArgs = new Dictionary<string, double>(args, StringComparer.Ordinal);
                formulaArgs["heir_level"] = heirLevel;
                var raw = Compose(attr, Eval(kind, formulaArgs));
                values[key] = kind is UnitFormulaKind.MeleeAntiMiss or UnitFormulaKind.RangedAntiMiss or UnitFormulaKind.SpellAntiMiss
                    ? Math.Clamp((1d - (facets / 10d - raw) / facets) * 100d - (baseMiss - 10d), 0d, 100d)
                    : raw / facets * 100d;
            }
        }

        // Client-only character-sheet formulas. Newer databases carry these kinds even though the
        // server's Character getters still compose several of them from a zero base.
        var flexibility = Compose(UnitAttribute.Flexibility, Eval((UnitFormulaKind)60, args));
        var bullsEye = Compose(UnitAttribute.BullsEye, Eval((UnitFormulaKind)67, args));
        values["flexibility"] = Math.Truncate(flexibility);
        values["bulls_eye"] = Math.Truncate(bullsEye);
        // the ratios are computed from the displayed (truncated) ratings: Xyz bulls_eye 660 -> 6.43021
        flexibility = Math.Truncate(flexibility);
        bullsEye = Math.Truncate(bullsEye);
        values["flexibility_ratio"] = facets > 0
            ? EvalGeneral(25, new Dictionary<string, double> { ["flexibility"] = flexibility }) / facets * 100d : 0d;
        values["flexibility_bonus"] = EvalGeneral(26,
            new Dictionary<string, double> { ["flexibility"] = flexibility }) / 10d;
        values["bulls_eye_rate"] = facets > 0
            ? EvalGeneral(24, new Dictionary<string, double> { ["bulls_eye"] = bullsEye }) / facets * 100d : 0d;

        // The client's melee critical bonus starts 100 points above the ranged/spell/heal ones (formula 35 says 1500
        // like the others, yet the original shows 150 with no bonus buff and 156 with the dual wield buff, on every
        // weapon set tried), so the melee base is 2500.
        values["melee_critical_bonus"] = (Compose(UnitAttribute.MeleeCriticalBonus, 2500d) - 1000d) / 10d;
        values["ranged_critical_bonus"] = (Compose(UnitAttribute.RangedCriticalBonus, 1500d) - 1000d) / 10d;
        values["spell_critical_bonus"] = (Compose(UnitAttribute.SpellCriticalBonus, 1500d)
            + Compose(UnitAttribute.SpellDamageCriticalBonus, 0d) - 1000d) / 10d;
        values["heal_critical_bonus"] = (Compose(UnitAttribute.HealCriticalBonus, 1500d) - 1000d) / 10d;

        var castingRating = Math.Truncate(Compose(UnitAttribute.CastingTimeMul, Eval((UnitFormulaKind)68, args)));
        values["casting_time_mul"] = castingRating;
        values["casting_time"] = Math.Max(0d, (castingRating + 1000d) / 10d);
        var gcdRating = Compose(UnitAttribute.GlobalCooldownMul, 0d);
        values["global_cooldown_mul"] = Math.Truncate(100000d / (gcdRating + 1000d));
        var animationRating = Compose(UnitAttribute.AttackAnimSpeedMul, 0d);
        if (animationRating == 0d) animationRating = Compose(UnitAttribute.AttackSpeedMul, 0d);
        if (animationRating == 0d) animationRating = Compose(UnitAttribute.MeleeSpeedMul, 0d);
        values["attack_anim_speed"] = animationRating;
        values["attack_anim_speed_mul"] = 100000d / (Math.Clamp(animationRating, -666d, 2000d) + 1000d);
        var moveFactor = Compose(UnitAttribute.MoveSpeedMul, 1000d) / 1000d;
        values["move_speed_rate"] = moveFactor * 100d;
        // Client presentation conventions recovered from the captured 10.0.2.13 UnitInfo sample.
        // AAEmu has no player BaseMoveSpeed override or character-sheet percentage helpers for these.
        values["move_speed"] = 5.4d * moveFactor;

        var detectRating = Compose(UnitAttribute.DetectStealthRangeMul, 0d);
        values["detect_stealth_range_mul"] = 100d + detectRating / 10d;
        var detectArgs = new Dictionary<string, double>(args, StringComparer.Ordinal)
        {
            ["detect_stealth_range_mul"] = values["detect_stealth_range_mul"],
            // The client evaluates formula 4 against a fixed reference stealth level of 53 (equal levels): 6.61017 at
            // level 17 and at level 55 alike.
            ["source_level"] = level, ["target_level"] = level, ["stealth_level"] = 53d,
        };
        values["detect_stealth_range"] = EvalGeneral(4, detectArgs) * values["detect_stealth_range_mul"] / 100d;

        values["exp_mul"] = Compose(UnitAttribute.ExpMul, 0d);
        values["drop_rate_mul"] = Compose(UnitAttribute.DropRateMul, 0d);
        values["loot_gold_mul"] = Compose(UnitAttribute.LootGoldMul, 0d);
        values["ignore_shield_bonus"] = Compose(UnitAttribute.IgnoreShieldBonus, 0d);
        values["ignore_shield_bonus_mul"] = Compose(UnitAttribute.IgnoreShieldBonusMul,
            Eval((UnitFormulaKind)63, args)) / 10d;
        values["ignore_shield_chance"] = Compose(UnitAttribute.IgnoreShieldChance, 0d);
        values["armor_percentage"] = values["armor"] / (values["armor"] + 7900d) * 100d;
        values["magic_resist_percentage"] = values["magic_resist"] / (values["magic_resist"] + 7900d) * 100d;
        // a quarter of the magic resistance percentage (Xyz 3.52912 -> 0.882281, Spirall 4.91093 -> 1.22773)
        values["magic_effect_resist_percentage"] = values["magic_resist_percentage"] / 4d;
        // Character.IncomingMeleeDamageAdd / RangedDamageAdd / SpellDamageAdd (unit formula kinds 39-41), shown truncated
        double IncomingDamageValue(UnitFormulaKind kind, UnitAttribute attribute, double rating)
            => Math.Truncate(Compose(attribute, Eval(kind, new Dictionary<string, double> { ["armor"] = rating, ["magic_resist"] = rating }))) + 0d; // + 0 drops a negative zero
        values["incoming_melee_damage_val"] = IncomingDamageValue((UnitFormulaKind)39, (UnitAttribute)142, values["armor"]);
        values["incoming_ranged_damage_val"] = IncomingDamageValue((UnitFormulaKind)40, (UnitAttribute)144, values["armor"]);
        values["incoming_spell_damage_val"] = IncomingDamageValue((UnitFormulaKind)41, (UnitAttribute)146, values["magic_resist"]);

        ClientEquippedItem? InSlot(int slot) => equipment.LastOrDefault(item => item.Slot == slot);
        EquipmentTemplate? Template(ClientEquippedItem? item) => item is not null
            ? _equipmentTemplates.GetValueOrDefault(item.TemplateId) : null;
        ItemGrade Grade(ClientEquippedItem? item) => item is not null && _grades.TryGetValue(item.Grade, out var grade)
            ? grade : _grades.GetValueOrDefault((byte)0) ?? new ItemGrade(1, 1, 1, 1);
        double WeaponDps(ClientEquippedItem? item, int channel)
        {
            var template = Template(item);
            if (template is null || item is null) return 0d;
            var grade = Grade(item);
            var formula = channel switch { 0 => template.Dps, 1 => template.MagicDps, _ => template.HealDps };
            var itemGrade = channel == 0 ? grade.HoldableDps : grade.HoldableMagicDps;
            return formula?.Evaluate(new Dictionary<string, double>
                { ["item_level"] = template.Level, ["item_grade"] = itemGrade }) ?? 0d;
        }
        var mainhand = InSlot(15) ?? equipment.FirstOrDefault(item =>
            Template(item) is { Kind: 1, SlotType: 14 or 16 or 17 });
        var offhand = InSlot(16);
        var ranged = InSlot(17) ?? equipment.FirstOrDefault(item => Template(item) is { Kind: 1, SlotType: 18 });
        var mainWeaponDps = WeaponDps(mainhand, 0);
        var rangedWeaponDps = WeaponDps(ranged, 0);
        values["melee_dps"] = Compose(UnitAttribute.MainhandDps, mainWeaponDps * 1000d) / 1000d
            + Compose(UnitAttribute.MeleeDpsInc, Eval(UnitFormulaKind.MeleeDpsInc, args)) / 1000d;
        values["ranged_dps"] = Compose(UnitAttribute.RangedDps, rangedWeaponDps * 1000d) / 1000d
            + Compose(UnitAttribute.RangedDpsInc, Eval(UnitFormulaKind.RangedDpsInc, args)) / 1000d;
        values["spell_dps"] = Compose(UnitAttribute.SpellDps, WeaponDps(mainhand, 1) * 1000d) / 1000d
            + Compose(UnitAttribute.SpellDpsInc, Eval(UnitFormulaKind.SpellDpsInc, args)) / 1000d;
        values["heal_dps"] = Compose(UnitAttribute.HealDps, WeaponDps(mainhand, 2) * 1000d) / 1000d
            + Compose(UnitAttribute.HealDpsInc, Eval(UnitFormulaKind.HealDpsInc, args)) / 1000d;
        double AttackSeconds(ClientEquippedItem? item, double fallback, double multiplier = 1d)
        {
            var speed = (Template(item)?.Speed ?? (int)fallback) * multiplier;
            var rating = Compose(UnitAttribute.AttackSpeedMul, 0d);
            if (rating == 0d)
                rating = Compose(item?.Slot == 17 ? UnitAttribute.RangedSpeedMul : UnitAttribute.MeleeSpeedMul, 0d);
            if (rating == 0d) rating = gcdRating;
            var factor = 100000d / (Math.Clamp(rating, -666d, 2000d) + 1000d) / 100d;
            return Math.Truncate(Math.Clamp(speed * factor, 400d, 5000d)) / 1000d;
        }
        values["mainhand_melee_speed"] = AttackSeconds(mainhand, 1500d);
        // the offhand swings at twice the weapon's interval (dagger 0.8 s: 1.359 with the dual wield speed buff, shield 1.0 s:
        // 2); with nothing in the offhand the client shows 3
        values["offhand_melee_speed"] = Template(offhand) is null ? 3d : AttackSeconds(offhand, 1500d, 2d);
        values["ranged_speed"] = AttackSeconds(ranged, 1800d);
        // Character-sheet damage ranges. The native formula is not in the database; these are fitted to the original
        // client's samples (level 17 and 55 characters with dagger pair, sword, sword and dagger, sword and shield,
        // greatsword, shotgun and bow) and land within about 1 to 2 percent of them.
        // Ranged is exact for every sample: dps times the weapon's base interval in seconds, spread by its damage scale,
        // both ends truncated.
        var rangedTemplate = Template(ranged);
        if (rangedTemplate is not null && rangedTemplate.Speed > 0)
        {
            var rangedDamage = values["ranged_dps"] * rangedTemplate.Speed / 1000d;
            var rangedSpread = rangedTemplate.DamageScale / 100d;
            values["ranged_min_dps"] = Math.Floor(rangedDamage * (1d - rangedSpread));
            values["ranged_max_dps"] = Math.Floor(rangedDamage * (1d + rangedSpread));
        }
        else values["ranged_min_dps"] = values["ranged_max_dps"] = 0d;
        var mainTemplate2 = Template(mainhand);
        var offTemplate2 = Template(offhand);
        var meleeDps = values["melee_dps"];
        if (mainTemplate2 is null || mainTemplate2.Speed <= 0)
            values["melee_min_dps"] = values["melee_max_dps"] = 0d;
        else if (offTemplate2 is { Kind: 1 } && offTemplate2.SlotType != 20)
        {
            // dual wield: both hands' swings add up, with no visible spread
            values["melee_min_dps"] = values["melee_max_dps"] = Math.Floor(meleeDps
                * (mainTemplate2.Speed + offTemplate2.Speed) / 1000d * 0.99d);
        }
        else if (offTemplate2 is { SlotType: 20 })
        {
            // main hand plus a shield bash that carries the shield's own (wide) damage scale
            var bash = meleeDps * 0.686d;
            var spread = offTemplate2.DamageScale / 100d + 0.015d;
            var main = meleeDps * mainTemplate2.Speed / 1000d;
            values["melee_min_dps"] = Math.Floor(main + bash * (1d - spread));
            values["melee_max_dps"] = Math.Floor(main + bash * (1d + spread));
        }
        else
        {
            // one weapon and nothing (or a non weapon) in the other hand: about twice the dps, a little less for two-handers
            var damage = meleeDps * (mainTemplate2.SlotType == 16 ? 2.011d : 2.03d);
            var spread = mainTemplate2.DamageScale / 100d;
            values["melee_min_dps"] = Math.Floor(damage * (1d - spread));
            values["melee_max_dps"] = Math.Floor(damage * (1d + spread));
        }

        double gearScore = 0d;
        foreach (var item in equipment)
        {
            var template = Template(item);
            if (template is null) continue;
            var formulaId = template.Kind switch { 1 => 30u, 2 => 56u, 3 => 57u, _ => 0u };
            if (formulaId == 0) continue;
            // each item's score is rounded before the sum (Spirall/Xyz dagger pair 128, greatsword 116, sword and bow 105)
            var itemScore = EvalGeneral(formulaId, new Dictionary<string, double>
            {
                ["item_level"] = template.Level, ["item_grade"] = Grade(item).HoldableDps,
                ["scaling_multiplier"] = 1d, ["element_level"] = item.ElementLevel,
                ["gear_score_multiplier"] = template.GearScoreMultiplier,
            });
            gearScore += Math.Round(itemScore, MidpointRounding.AwayFromZero);
            if (item.FilledGemCount > 0)
            {
                var socketArgs = new Dictionary<string, double> { ["item_level"] = template.Level };
                gearScore += item.FilledGemCount * (EvalGeneral(31, socketArgs) + EvalGeneral(32, socketArgs));
            }
        }
        values["gear_score"] = Math.Round(gearScore);

        _ = race; _ = gender; _ = activeAbilities;
        return values;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            if (!File.Exists(_path)) throw new FileNotFoundException("Character formula database was not found.", _path);
            var cs = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, formula, kind_id, owner_type_id FROM unit_formulas WHERE owner_type_id=$owner";
                command.Parameters.AddWithValue("$owner", (byte)FormulaOwnerType.Character);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var kind = (UnitFormulaKind)reader.GetByte(2);
                    var formula = new Formula { Id = checked((uint)reader.GetInt64(0)), TextFormula = reader.GetString(1) };
                    if (formula.Prepare()) _formulas[kind] = formula;
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, formula FROM formulas";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var formula = new Formula { Id = checked((uint)reader.GetInt64(0)), TextFormula = reader.GetString(1) };
                    if (formula.Prepare()) _generalFormulas[formula.Id] = formula;
                }
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT id,var_holdable_dps,var_holdable_magic_dps,
var_wearable_armor,var_wearable_magic_resistance,var_holdable_armor FROM item_grades";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    _grades[checked((byte)reader.GetInt64(0))] = new ItemGrade(reader.GetDouble(1),
                        reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5));
            }
            LoadModifiers(connection, "Item", _itemModifiers);
            LoadModifiers(connection, "Buff", _buffModifiers);
            LoadModifiers(connection, "Buffs", _buffModifiers);
            LoadEquipmentSets(connection);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT unit_attribute_id, minimum, maximum FROM unit_attribute_limits";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    _limits[(UnitAttribute)reader.GetInt64(0)] = (reader.GetDouble(1), reader.GetDouble(2));
            }
            _loaded = true;
        }
    }

    private void LoadEquipmentSets(SqliteConnection connection)
    {
        foreach (var table in new[] { "item_armors", "item_weapons", "item_accessories" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT item_id, eiset_id FROM {table} WHERE eiset_id IS NOT NULL AND eiset_id > 0";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                _itemSetByTemplate[checked((uint)reader.GetInt64(0))] = checked((uint)reader.GetInt64(1));
        }

        using var bonusCommand = connection.CreateCommand();
        bonusCommand.CommandText = "SELECT equip_item_set_id, num_pieces, buff_id FROM equip_item_set_bonuses WHERE buff_id IS NOT NULL AND buff_id > 0";
        using var bonusReader = bonusCommand.ExecuteReader();
        while (bonusReader.Read())
        {
            var setId = checked((uint)bonusReader.GetInt64(0));
            if (!_itemSetBonuses.TryGetValue(setId, out var bonuses)) _itemSetBonuses[setId] = bonuses = [];
            bonuses.Add((bonusReader.GetInt32(1), checked((uint)bonusReader.GetInt64(2))));
        }
    }

    private void EnsureEquipmentLoaded(IReadOnlyList<ClientEquippedItem> equipment)
    {
        var missing = equipment.Select(item => item.TemplateId).Distinct()
            .Where(id => !_equipmentTemplates.ContainsKey(id)).ToArray();
        if (missing.Length == 0 && _baseArmorFormula is not null && _baseMagicResistanceFormula is not null) return;
        var cs = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        if (_baseArmorFormula is null || _baseMagicResistanceFormula is null)
        {
            using var formulaCommand = connection.CreateCommand();
            formulaCommand.CommandText = "SELECT kind_id, formula FROM wearable_formulas WHERE kind_id IN (0,1)";
            using var formulaReader = formulaCommand.ExecuteReader();
            while (formulaReader.Read())
            {
                var formula = new Formula { TextFormula = formulaReader.GetString(1) };
                if (!formula.Prepare()) continue;
                if (formulaReader.GetInt32(0) == 0) _baseArmorFormula = formula;
                else _baseMagicResistanceFormula = formula;
            }
        }
        foreach (var templateId in missing)
        {
            using (var equipmentCommand = connection.CreateCommand())
            {
                equipmentCommand.CommandText = @"SELECT i.level,
CASE WHEN w.item_id IS NOT NULL THEN 1 WHEN a.item_id IS NOT NULL THEN 2 WHEN ac.item_id IS NOT NULL THEN 3 ELSE 0 END,
COALESCE(h.slot_type_id,a.slot_type_id,ac.slot_type_id,-1),COALESCE(h.speed,0),COALESCE(h.damage_scale,0),
COALESCE(h.gear_score_multiplier,ws.gear_score_multiplier,0),h.formula_dps,h.formula_mdps,h.formula_hdps,
h.formula_armor,h.formula_magic_resist
FROM items i LEFT JOIN item_weapons w ON w.item_id=i.id LEFT JOIN holdables h ON h.id=w.holdable_id
LEFT JOIN item_armors a ON a.item_id=i.id LEFT JOIN item_accessories ac ON ac.item_id=i.id
LEFT JOIN wearable_slots ws ON ws.slot_type_id=COALESCE(a.slot_type_id,ac.slot_type_id)
WHERE i.id=$id";
                equipmentCommand.Parameters.AddWithValue("$id", templateId);
                using var equipmentReader = equipmentCommand.ExecuteReader();
                if (equipmentReader.Read())
                {
                    Formula? ReadFormula(int ordinal)
                    {
                        if (equipmentReader.IsDBNull(ordinal)) return null;
                        var formula = new Formula { TextFormula = equipmentReader.GetString(ordinal) };
                        return formula.Prepare() ? formula : null;
                    }
                    _equipmentTemplates[templateId] = new EquipmentTemplate(equipmentReader.GetInt32(0),
                        equipmentReader.GetInt32(1), equipmentReader.GetInt32(2), equipmentReader.GetInt32(3),
                        equipmentReader.GetInt32(4), equipmentReader.GetDouble(5) / 100d,
                        ReadFormula(6), ReadFormula(7), ReadFormula(8), ReadFormula(9), ReadFormula(10));
                }
                else
                    _equipmentTemplates[templateId] = new EquipmentTemplate(0, 0, -1, 0, 0, 0, null, null, null);
            }
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT i.level,
wk.armor_ratio,wk.magic_resistance_ratio,ws.coverage
FROM item_armors a JOIN items i ON i.id=a.item_id
JOIN wearable_kinds wk ON wk.armor_type_id=a.type_id
JOIN wearable_slots ws ON ws.slot_type_id=a.slot_type_id
WHERE i.id=$id";
            command.Parameters.AddWithValue("$id", templateId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
                _wearableTemplates[templateId] = new WearableTemplate(reader.GetInt32(0), 1, 1,
                    reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
            else
                _wearableTemplates[templateId] = new WearableTemplate(0, 1, 1, 0, 0, 0);
        }
    }

    private static void LoadModifiers(SqliteConnection connection, string owner,
        Dictionary<uint, List<Modifier>> destination)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner_id, unit_attribute_id, unit_modifier_type_id, value, linear_level_bonus FROM unit_modifiers WHERE owner_type=$owner";
        command.Parameters.AddWithValue("$owner", owner);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = checked((uint)reader.GetInt64(0));
            if (!destination.TryGetValue(id, out var rows)) destination[id] = rows = [];
            rows.Add(new Modifier((UnitAttribute)reader.GetInt64(1), (UnitModifierType)reader.GetByte(2),
                reader.GetDouble(3), reader.GetInt32(4)));
        }
    }

    public void Dispose() { }
}
