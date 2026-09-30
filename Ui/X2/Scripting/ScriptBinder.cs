#nullable enable
using System.Collections;
using System.Globalization;
using System.Reflection;
using AAEmu.GodotViewer.Lua;

namespace AAEmu.GodotViewer.Ui.X2.Scripting;

/// <summary>
/// Maps obj:Method(...) calls from Lua onto public instance methods of the widget model by name (overloads are picked
/// by argument count), converting Lua values to the parameter types. Unknown methods become logged stubs that
/// return a type-appropriate default, so the original scripts keep running.
/// </summary>
internal sealed class ScriptBinder
{
    private readonly Dictionary<(Type, string), MethodInfo[]> _methods = new();
    private readonly Dictionary<string, int> _stubHits = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    public ScriptBinder(Action<string> log) => _log = log;

    /// <summary>Stubbed widget/UIParent methods that scripts called: "Type:Method" -> call count.</summary>
    public IReadOnlyDictionary<string, int> StubHits => _stubHits;

    public LuaObjectMethod<T> Resolve<T>(string name) where T : class
        => (target, state, args) => Invoke(target, name, args);

    public IReadOnlyList<object?> Invoke(object target, string name, IReadOnlyList<object?> args)
    {
        var type = target.GetType();
        if (!_methods.TryGetValue((type, name), out var candidates))
        {
            candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name && m.DeclaringType != typeof(object) && !m.IsSpecialName)
                .OrderBy(m => m.GetParameters().Length)
                .ToArray();
            _methods[(type, name)] = candidates;
        }
        if (candidates.Length == 0)
            return Stub(type, name);
        var method = Pick(candidates, args.Count);
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.GetCustomAttribute<ParamArrayAttribute>() != null)
            {
                var rest = new object?[Math.Max(0, args.Count - i)];
                for (var k = 0; k < rest.Length; k++) rest[k] = Unwrap(args[i + k]);
                values[i] = rest;
                break;
            }
            values[i] = i < args.Count ? Convert(args[i], p) : Missing(p);
        }
        object? result;
        try
        {
            result = method.Invoke(target, values);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw new InvalidOperationException($"{type.Name}:{name}: {e.InnerException.Message}", e.InnerException);
        }
        return ToLua(result, method.ReturnType);
    }

    private static MethodInfo Pick(MethodInfo[] candidates, int argCount)
    {
        foreach (var m in candidates)
        {
            var ps = m.GetParameters();
            var required = ps.Count(p => !p.IsOptional && p.GetCustomAttribute<ParamArrayAttribute>() == null);
            var hasParams = ps.Length > 0 && ps[^1].GetCustomAttribute<ParamArrayAttribute>() != null;
            if (argCount >= required && (argCount <= ps.Length || hasParams))
                return m;
        }
        return candidates[^1];
    }

    private IReadOnlyList<object?> Stub(Type type, string name)
    {
        var key = $"{type.Name}:{name}";
        _stubHits[key] = _stubHits.GetValueOrDefault(key) + 1;
        if (_stubHits[key] == 1) _log($"stub {key}");
        return DefaultFor(name);
    }

    /// <summary>A plausible return value for an unimplemented getter, by name.</summary>
    internal static IReadOnlyList<object?> DefaultFor(string name)
    {
        if (name.StartsWith("Is", StringComparison.Ordinal) || name.StartsWith("Has", StringComparison.Ordinal)
            || name.StartsWith("Can", StringComparison.Ordinal) || name.StartsWith("Use", StringComparison.Ordinal))
            return [false];
        if (name.StartsWith("Get", StringComparison.Ordinal))
        {
            if (name.Contains("Text") || name.Contains("Name") || name.EndsWith("Str", StringComparison.Ordinal)) return [""];
            if (name.Contains("Extent") || name.Contains("Offset")) return [0.0, 0.0];
            if (name.Contains("Info") || name.Contains("List") || name.Contains("Table") || name.Contains("Data")) return [null];
            return [0.0];
        }
        return [];
    }

    private static object? Missing(ParameterInfo p)
    {
        if (p.HasDefaultValue) return p.DefaultValue;
        var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    internal static object? Unwrap(object? value) => value is LuaUserData u ? u.Target : value;

    private static object? Convert(object? raw, ParameterInfo p)
    {
        var value = Unwrap(raw);
        var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        if (value == null)
        {
            if (type == typeof(bool)) return false;
            return Missing(p);
        }
        if (type == typeof(object)) return value;
        if (type.IsInstanceOfType(value)) return value;
        if (type == typeof(string)) return value switch
        {
            double d => FormatNumber(d),
            bool b => b ? "true" : "false",
            _ => value.ToString(),
        };
        if (type == typeof(bool)) return value is not false;
        if (type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long))
        {
            double d = value switch
            {
                double x => x,
                bool b => b ? 1 : 0,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) => x,
                _ => 0,
            };
            if (type == typeof(float)) return (float)d;
            if (type == typeof(int)) return (int)d;
            if (type == typeof(long)) return (long)d;
            return d;
        }
        return Missing(p);
    }

    internal static string FormatNumber(double d)
        => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    internal static IReadOnlyList<object?> ToLua(object? result, Type declared)
    {
        if (declared == typeof(void)) return [];
        return result switch
        {
            null => [null],
            LuaMulti m => m.Values.Select(ToLuaValue).ToArray(),
            _ => [ToLuaValue(result)],
        };
    }

    /// <summary>Numbers become doubles and dictionaries LuaTables (recursively); objects stay objects (userdata).</summary>
    internal static object? ToLuaValue(object? value) => value switch
    {
        null => null,
        LuaTable t => t,
        float f => (double)f,
        int i => (double)i,
        long l => (double)l,
        double or bool or string => value,
        IDictionary<object, object?> dict => ToTable(dict),
        IDictionary dict => ToTable(dict.Keys.Cast<object>().ToDictionary(k => k, k => dict[k])),
        _ => value,
    };

    private static LuaTable ToTable(IDictionary<object, object?> dict)
    {
        var t = new LuaTable();
        foreach (var (k, v) in dict) t[ToLuaValue(k)!] = ToLuaValue(v);
        return t;
    }
}
