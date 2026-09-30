#nullable enable
namespace AAEmu.GodotViewer.Lua;

public delegate IReadOnlyList<object?> LuaFunction(LuaState state, IReadOnlyList<object?> arguments);
public delegate IReadOnlyList<object?> LuaObjectMethod<in T>(T instance, LuaState state, IReadOnlyList<object?> arguments) where T : class;

/// <summary>A managed table value. Keys may be strings, booleans, numbers, or other reference values.</summary>
public sealed class LuaTable : Dictionary<object, object?>
{
    public LuaTable() { }
    public LuaTable(IDictionary<object, object?> values) : base(values) { }
}

/// <summary>An exposed managed object as seen when a Lua userdata is read back.</summary>
public sealed record LuaUserData(object Target, string TypeName);

/// <summary>A registry-rooted Lua function. Dispose it on the owning state's thread.</summary>
public sealed class LuaFunctionReference : IDisposable
{
    private LuaState? _state;
    private int _reference;

    internal LuaFunctionReference(LuaState state, int reference)
    {
        _state = state;
        _reference = reference;
    }

    internal LuaState Owner => _state ?? throw new ObjectDisposedException(nameof(LuaFunctionReference));
    internal int Reference => _reference;

    public object?[] Invoke(params object?[] arguments)
    {
        var state = _state ?? throw new ObjectDisposedException(nameof(LuaFunctionReference));
        return state.CallReference(_reference, arguments);
    }

    public void Dispose()
    {
        var state = Interlocked.Exchange(ref _state, null);
        if (state is not null)
            state.ReleaseReference(Interlocked.Exchange(ref _reference, 0));
    }
}
