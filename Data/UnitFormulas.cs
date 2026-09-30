#nullable enable
using System.Globalization;
using Jace;
using Jace.Execution;

namespace AAEmu.GodotViewer.Data;

/// <summary><c>unit_formulas.owner_type_id</c>.</summary>
public enum FormulaOwnerType : byte
{
    Character = 0,
}

/// <summary><c>unit_formulas.kind_id</c> values the character sheet evaluates.</summary>
public enum UnitFormulaKind : byte
{
    MeleeCritical = 1,
    MeleeAntiMiss = 2,
    MeleeParry = 5,
    RangedCritical = 6,
    RangedAntiMiss = 7,
    SpellCritical = 10,
    SpellAntiMiss = 11,
    MaxHealth = 14,
    MaxMana = 15,
    HealthRegen = 16,
    ManaRegen = 17,
    Armor = 18,
    MagicResist = 19,
    Facet = 20,
    Str = 22,
    Dex = 23,
    Sta = 24,
    Int = 25,
    Spi = 26,
    PersistentHealthRegen = 31,
    PersistentManaRegen = 32,
    RangedParry = 38,
    HealCritical = 42,
    Block = 45,
    Dodge = 46,
}

/// <summary><c>enum_unit_attribute</c> ids the character sheet reads (any other id still round-trips as a number).</summary>
public enum UnitAttribute : uint
{
    Str = 0,
    Dex = 1,
    Sta = 2,
    Int = 3,
    Spi = 4,
    MaxHealth = 6,
    MaxMana = 7,
    Armor = 8,
    HealthRegen = 11,
    ManaRegen = 12,
    MeleeCritical = 16,
    MeleeAntiMiss = 18,
    MeleeParry = 22,
    RangedAntiMiss = 23,
    RangedCritical = 25,
    SpellAntiMiss = 28,
    SpellCritical = 30,
    MagicResist = 64,
    PersistentHealthRegen = 67,
    PersistentManaRegen = 68,
    RangedParry = 153,
    HealCritical = 174,
    Block = 177,
    Dodge = 178,
}

/// <summary><c>unit_modifiers.unit_modifier_type_id</c>.</summary>
public enum UnitModifierType : byte
{
    Value = 0,
    Percent = 1,
}

/// <summary>
/// A game-database formula (<c>unit_formulas</c>, <c>formulas</c>) compiled with Jace, with the helper functions the
/// formula text uses. A formula that fails to compile or evaluate yields 0.
/// </summary>
public sealed class Formula
{
    private static readonly CalculationEngine Engine = CreateEngine();
    private Func<Dictionary<string, double>, double>? _expression;
    private string _text = "";
    private bool _compiled;

    public uint Id { get; set; }

    public string TextFormula
    {
        get => _text;
        set { _text = value ?? ""; _compiled = false; _expression = null; }
    }

    /// <summary>Compiles the formula now; false when the text does not parse.</summary>
    public bool Prepare() => Compile() != null;

    public double Evaluate(Dictionary<string, double> parameters)
    {
        var expression = Compile();
        if (expression == null)
            return 0d;
        lock (expression)
        {
            try { return expression(parameters); }
            catch (Exception) { return 0d; }
        }
    }

    private Func<Dictionary<string, double>, double>? Compile()
    {
        if (_compiled)
            return _expression;
        _compiled = true;
        try { _expression = Engine.Build(_text); }
        catch (Exception) { _expression = null; }
        return _expression;
    }

    private static CalculationEngine CreateEngine()
    {
        var engine = new CalculationEngine(new JaceOptions
        {
            CacheEnabled = true,
            OptimizerEnabled = true,
            CaseSensitive = true,
            ExecutionMode = ExecutionMode.Compiled,
            CultureInfo = CultureInfo.InvariantCulture,
        });
        engine.AddFunction("clamp", (a, b, c) => a < b ? b : a > c ? c : a);
        engine.AddFunction("if_negative", (a, b, c) => a < 0 ? b : c);
        engine.AddFunction("if_positive", (a, b, c) => a > 0 ? b : c);
        engine.AddFunction("if_zero", (a, b, c) => a == 0 ? b : c);
        engine.AddFunction("log", a => Math.Log10(a));
        return engine;
    }
}
