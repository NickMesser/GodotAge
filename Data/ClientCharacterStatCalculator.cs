#nullable enable
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AAEmu.GodotViewer.Data;

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
    private readonly string _path;
    private readonly Dictionary<UnitFormulaKind, Formula> _formulas = [];
    private readonly Dictionary<uint, List<Modifier>> _itemModifiers = [];
    private readonly Dictionary<uint, List<Modifier>> _buffModifiers = [];
    private readonly Dictionary<uint, List<(int Pieces, uint BuffId)>> _itemSetBonuses = [];
    private readonly Dictionary<uint, uint> _itemSetByTemplate = [];
    private readonly Dictionary<uint, WearableTemplate> _wearableTemplates = [];
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
    {
        EnsureLoaded();
        EnsureEquipmentLoaded(equipmentTemplateIds);
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

        double Eval(UnitFormulaKind kind, Dictionary<string, double> args)
        {
            if (!_formulas.TryGetValue(kind, out var formula)) return 0d;
            return formula.Evaluate(args);
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
            if (_limits.TryGetValue(attribute, out var limit))
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
            if (kind == UnitFormulaKind.ManaRegen) raw += attributes[UnitAttribute.Spi] / 10d;
            var composed = Compose(attribute, raw);
            if (kind is UnitFormulaKind.PersistentHealthRegen or UnitFormulaKind.PersistentManaRegen) composed /= 5d;
            var result = Math.Truncate(composed);
            attributes[attribute] = result;
            values[key] = result;
        }
        var armorBase = 0d;
        var magicResistanceBase = 0d;
        foreach (var templateId in equipmentTemplateIds)
        {
            if (!_wearableTemplates.TryGetValue(templateId, out var template)) continue;
            var itemArgs = new Dictionary<string, double>
            {
                ["item_level"] = template.Level,
                ["item_grade"] = template.ArmorGrade,
            };
            if (_baseArmorFormula is not null)
                armorBase += (int)(_baseArmorFormula.Evaluate(itemArgs) * template.ArmorRatio * 0.01 * template.Coverage * 0.01);
            itemArgs["item_grade"] = template.MagicResistanceGrade;
            if (_baseMagicResistanceFormula is not null)
                magicResistanceBase += (int)(_baseMagicResistanceFormula.Evaluate(itemArgs) * template.MagicResistanceRatio * 0.01 * template.Coverage * 0.01);
        }
        values["armor"] = Math.Truncate(Compose(UnitAttribute.Armor, values.GetValueOrDefault("armor") + armorBase));
        values["magic_resist"] = Math.Truncate(Compose(UnitAttribute.MagicResist, values.GetValueOrDefault("magic_resist") + magicResistanceBase));
        // The server formulas use the same facet normalization and bindings as Character.cs.
        var facets = Eval(UnitFormulaKind.Facet, args);
        if (facets != 0)
        {
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
                // Character.BlockRate returns zero unless offhand is a shield; this calculator does not
                // yet carry the weapon's holdable slot type in the stats input, so the default is safer.
                if (kind == UnitFormulaKind.Block) continue;
                var formulaArgs = new Dictionary<string, double>(args, StringComparer.Ordinal);
                formulaArgs["heir_level"] = heirLevel;
                var raw = Compose(attr, Eval(kind, formulaArgs));
                values[key] = kind is UnitFormulaKind.MeleeAntiMiss or UnitFormulaKind.RangedAntiMiss or UnitFormulaKind.SpellAntiMiss
                    ? Math.Clamp((1d - (facets / 10d - raw) / facets) * 100d, 0d, 100d)
                    : raw / facets * 100d;
            }
        }
        // Values the formula set does not describe remain explicitly absent to the caller, which can
        // overlay SCCharacterSubStats when the server sent those fields.
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

    private void EnsureEquipmentLoaded(IReadOnlyList<uint> equipmentTemplateIds)
    {
        var missing = equipmentTemplateIds.Distinct().Where(id => !_wearableTemplates.ContainsKey(id)).ToArray();
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
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT i.level,g.var_wearable_armor,g.var_wearable_magic_resistance,
wk.armor_ratio,wk.magic_resistance_ratio,ws.coverage
FROM item_armors a JOIN items i ON i.id=a.item_id
JOIN item_grades g ON g.id=0
JOIN wearable_kinds wk ON wk.armor_type_id=a.type_id
JOIN wearable_slots ws ON ws.slot_type_id=a.slot_type_id
WHERE i.id=$id";
            command.Parameters.AddWithValue("$id", templateId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
                _wearableTemplates[templateId] = new WearableTemplate(reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2),
                    reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5));
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
