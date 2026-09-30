#nullable enable
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.GodotViewer.Lua;

/// <summary>
/// Owns one native Lua 5.1 state. Every member, including Dispose and reference disposal,
/// must be called from the thread that constructed the instance.
/// </summary>
public sealed class LuaState : IDisposable
{
    private const int LuaTNone = -1;
    private const int LuaTNil = 0;
    private const int LuaTBoolean = 1;
    private const int LuaTLightUserData = 2;
    private const int LuaTNumber = 3;
    private const int LuaTString = 4;
    private const int LuaTTable = 5;
    private const int LuaTFunction = 6;
    private const int LuaTUserData = 7;
    private const int LuaTThread = 8;

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly List<LuaNative.CFunction> _callbacks = new();
    private readonly Dictionary<string, UserDataRegistration> _userDataByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, UserDataRegistration> _userDataByType = new();
    private readonly Dictionary<nint, nint> _userDataHandles = new();
    // One userdata per managed object, so identity (==, table keys) and per-object Lua fields survive round trips.
    private readonly Dictionary<object, int> _userDataRefs = new(ReferenceEqualityComparer.Instance);
    private nint _state;

    static LuaState() => LuaNative.EnsureResolver();

    public LuaState(bool openStandardLibraries = true)
    {
        _state = LuaNative.luaL_newstate();
        if (_state == 0)
            throw new InvalidOperationException("luaL_newstate returned null.");

        if (openStandardLibraries)
            LuaNative.luaL_openlibs(_state);
    }

    public bool IsDisposed => _state == 0;
    public int ActiveManagedUserDataCount { get { EnsureAccess(); return _userDataHandles.Count; } }

    public LuaTable CreateTable() => new();

    public void DoString(string code, string chunkName = "chunk")
    {
        ArgumentNullException.ThrowIfNull(code);
        DoBuffer(Encoding.UTF8.GetBytes(code), chunkName);
    }

    public void DoBuffer(ReadOnlySpan<byte> buffer, string chunkName, bool execute = true)
    {
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LoadChunk(buffer, chunkName);
            if (execute)
                ProtectedCall(0, 0);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    /// <summary>Compiles a chunk without running it and roots the resulting Lua function.</summary>
    public LuaFunctionReference LoadBuffer(ReadOnlySpan<byte> buffer, string chunkName)
    {
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LoadChunk(buffer, chunkName);
            return ReferenceFunction(-1);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    public void CompileBuffer(ReadOnlySpan<byte> buffer, string chunkName)
    {
        DoBuffer(buffer, chunkName, execute: false);
    }

    public object? GetGlobal(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LuaNative.lua_getfield(_state, LuaNative.GlobalsIndex, name);
            return ReadValue(-1, new Dictionary<nint, LuaTable>());
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    /// <summary>Returns the native numeric conversions for diagnostics, including the LNUM integer predicate.</summary>
    public LuaNumberInfo GetGlobalNumberInfo(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LuaNative.lua_getfield(_state, LuaNative.GlobalsIndex, name);
            if (LuaNative.lua_type(_state, -1) != LuaTNumber)
                throw new InvalidOperationException($"Global '{name}' is not a Lua number.");
            var integerBeforeConversion = LuaNative.lua_isinteger(_state, -1) != 0;
            var number = LuaNative.lua_tonumber(_state, -1);
            var integer = LuaNative.lua_tointeger(_state, -1);
            var integerAfterConversion = LuaNative.lua_isinteger(_state, -1) != 0;
            return new LuaNumberInfo(number, integer, integerBeforeConversion, integerAfterConversion);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    public void SetGlobal(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            PushValue(value, new HashSet<LuaTable>(ReferenceEqualityComparer.Instance));
            LuaNative.lua_setfield(_state, LuaNative.GlobalsIndex, name);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    public object?[] Call(string functionName, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LuaNative.lua_getfield(_state, LuaNative.GlobalsIndex, functionName);
            RequireType(-1, LuaTFunction, $"global '{functionName}'");
            foreach (var argument in arguments)
                PushValue(argument, new HashSet<LuaTable>(ReferenceEqualityComparer.Instance));
            ProtectedCall(arguments.Length, LuaNative.MultipleReturns);
            return ReadResults(top);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    public void RegisterFunction(string globalName, LuaFunction function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalName);
        ArgumentNullException.ThrowIfNull(function);
        EnsureAccess();
        var callback = RootCallback(state => InvokeFunctionCallback(state, function, 1));
        LuaNative.lua_pushcclosure(_state, callback, 0);
        LuaNative.lua_setfield(_state, LuaNative.GlobalsIndex, globalName);
    }

    public void RegisterFunction(string tableName, string fieldName, LuaFunction function)
        => RegisterTableFunction(tableName, fieldName, function, skipSelfTable: false);

    /// <summary>
    /// Registers Table:field(...) as a method: when the first argument is a table (the "self" of a colon call) it is
    /// skipped without being copied, so <paramref name="function"/> only sees the real arguments.
    /// </summary>
    public void RegisterMethod(string tableName, string fieldName, LuaFunction function)
        => RegisterTableFunction(tableName, fieldName, function, skipSelfTable: true);

    private void RegisterTableFunction(string tableName, string fieldName, LuaFunction function, bool skipSelfTable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(function);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LuaNative.lua_getfield(_state, LuaNative.GlobalsIndex, tableName);
            if (LuaNative.lua_type(_state, -1) == LuaTNil)
            {
                Pop(1);
                LuaNative.lua_createtable(_state, 0, 4);
                LuaNative.lua_pushvalue(_state, -1);
                LuaNative.lua_setfield(_state, LuaNative.GlobalsIndex, tableName);
            }
            RequireType(-1, LuaTTable, $"global '{tableName}'");
            var callback = RootCallback(state => InvokeFunctionCallback(state, function,
                skipSelfTable && LuaNative.lua_type(state, 1) == LuaTTable ? 2 : 1));
            LuaNative.lua_pushcclosure(_state, callback, 0);
            LuaNative.lua_setfield(_state, -2, fieldName);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    public void RegisterObjectType<T>(string typeName, IReadOnlyDictionary<string, LuaObjectMethod<T>> methods) where T : class
        => RegisterObjectType(typeName, methods, null);

    /// <summary>
    /// Registers a userdata type. <paramref name="fallback"/> is asked for methods the table does not have (once per
    /// name); returning null leaves the field nil.
    /// </summary>
    public void RegisterObjectType<T>(string typeName, IReadOnlyDictionary<string, LuaObjectMethod<T>> methods,
        Func<string, LuaObjectMethod<T>?>? fallback) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(methods);
        EnsureAccess();
        if (_userDataByName.ContainsKey(typeName) || _userDataByType.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"A userdata registration already exists for '{typeName}' or {typeof(T).FullName}.");

        var registration = new UserDataRegistration(typeName, typeof(T));
        foreach (var (name, method) in methods)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(method);
            registration.Methods.Add(name, (target, state, args) => method((T)target, state, args));
        }
        if (fallback is not null)
            registration.Fallback = name =>
            {
                var method = fallback(name);
                return method is null ? null : (target, state, args) => method((T)target, state, args);
            };
        _userDataByName.Add(typeName, registration);
        _userDataByType.Add(typeof(T), registration);
        CreateMetatable(registration);
    }

    public void SetGlobalObject<T>(string globalName, T instance, string? registeredTypeName = null) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalName);
        ArgumentNullException.ThrowIfNull(instance);
        EnsureAccess();
        var registration = FindRegistration(instance, registeredTypeName);
        PushUserData(instance, registration);
        LuaNative.lua_setfield(_state, LuaNative.GlobalsIndex, globalName);
    }

    public LuaTable ReadTable(LuaTable table) => table;

    internal object?[] CallReference(int reference, object?[] arguments)
    {
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LuaNative.lua_rawgeti(_state, LuaNative.RegistryIndex, reference);
            RequireType(-1, LuaTFunction, "registry reference");
            foreach (var argument in arguments)
                PushValue(argument, new HashSet<LuaTable>(ReferenceEqualityComparer.Instance));
            ProtectedCall(arguments.Length, LuaNative.MultipleReturns);
            return ReadResults(top);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    internal void ReleaseReference(int reference)
    {
        if (reference == 0 || IsDisposed)
            return;
        EnsureAccess();
        LuaNative.luaL_unref(_state, LuaNative.RegistryIndex, reference);
    }

    public void Dispose()
    {
        if (_state == 0)
            return;
        EnsureThread();
        var state = _state;
        LuaNative.lua_close(state);
        _state = 0;

        foreach (var handleValue in _userDataHandles.Values.ToArray())
        {
            var handle = GCHandle.FromIntPtr(handleValue);
            if (handle.IsAllocated)
                handle.Free();
        }
        _userDataHandles.Clear();
        _userDataRefs.Clear();
        _callbacks.Clear();
        _userDataByName.Clear();
        _userDataByType.Clear();
    }

    private void LoadChunk(ReadOnlySpan<byte> buffer, string chunkName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkName);
        var bytes = buffer.ToArray();
        using var context = new BufferLoadContext(bytes);
        var contextHandle = GCHandle.Alloc(context);
        int status;
        try
        {
            status = LuaNative.lua_load(
                _state,
                BufferReaderCallback,
                GCHandle.ToIntPtr(contextHandle),
                NormalizeChunkName(chunkName));
        }
        finally
        {
            contextHandle.Free();
        }
        if (status != 0)
            ThrowStackError(status);
    }

    private void ProtectedCall(int argumentCount, int resultCount)
    {
        var functionIndex = LuaNative.lua_gettop(_state) - argumentCount;
        var hasTraceback = PushTracebackFunction();
        if (hasTraceback)
            LuaNative.lua_insert(_state, functionIndex);

        var status = LuaNative.lua_pcall(_state, argumentCount, resultCount, hasTraceback ? functionIndex : 0);
        if (status != 0)
        {
            var traceback = ReadString(-1) ?? $"Lua error status {status}";
            if (hasTraceback)
                LuaNative.lua_remove(_state, functionIndex);
            throw BuildException(traceback, status);
        }
        if (hasTraceback)
            LuaNative.lua_remove(_state, functionIndex);
    }

    private bool PushTracebackFunction()
    {
        LuaNative.lua_getfield(_state, LuaNative.GlobalsIndex, "debug");
        if (LuaNative.lua_type(_state, -1) != LuaTTable)
        {
            Pop(1);
            return false;
        }
        LuaNative.lua_getfield(_state, -1, "traceback");
        LuaNative.lua_remove(_state, -2);
        if (LuaNative.lua_type(_state, -1) != LuaTFunction)
        {
            Pop(1);
            return false;
        }
        return true;
    }

    private void ThrowStackError(int status)
    {
        var message = ReadString(-1) ?? $"Lua error status {status}";
        Pop(1);
        throw BuildException(message, status);
    }

    private static LuaException BuildException(string traceback, int status)
    {
        var newline = traceback.IndexOf('\n');
        var message = newline < 0 ? traceback : traceback[..newline].TrimEnd('\r');
        return new LuaException(message, traceback, status);
    }

    private object?[] ReadResults(int originalTop)
    {
        var resultCount = LuaNative.lua_gettop(_state) - originalTop;
        var results = new object?[resultCount];
        var seen = new Dictionary<nint, LuaTable>();
        for (var i = 0; i < resultCount; i++)
            results[i] = ReadValue(originalTop + i + 1, seen);
        return results;
    }

    private object? ReadValue(int index, Dictionary<nint, LuaTable> seen)
    {
        index = AbsoluteIndex(index);
        return LuaNative.lua_type(_state, index) switch
        {
            LuaTNone => throw new ArgumentOutOfRangeException(nameof(index)),
            LuaTNil => null,
            LuaTBoolean => LuaNative.lua_toboolean(_state, index) != 0,
            LuaTLightUserData => new LuaLightUserData(LuaNative.lua_touserdata(_state, index)),
            LuaTNumber => LuaNative.lua_tonumber(_state, index),
            LuaTString => ReadString(index)!,
            LuaTTable => ReadTableAt(index, seen),
            LuaTFunction => ReferenceFunction(index),
            LuaTUserData => ReadUserData(index),
            LuaTThread => new LuaThreadValue(LuaNative.lua_topointer(_state, index)),
            var type => throw new NotSupportedException($"Unsupported Lua value type {type}.")
        };
    }

    private LuaTable ReadTableAt(int index, Dictionary<nint, LuaTable> seen)
    {
        index = AbsoluteIndex(index);
        var identity = LuaNative.lua_topointer(_state, index);
        if (seen.TryGetValue(identity, out var existing))
            return existing;

        var result = new LuaTable();
        seen.Add(identity, result);
        LuaNative.lua_pushnil(_state);
        while (LuaNative.lua_next(_state, index) != 0)
        {
            var key = ReadValue(-2, seen) ?? throw new InvalidOperationException("Lua table contained a nil key.");
            var value = ReadValue(-1, seen);
            result[key] = value;
            Pop(1);
        }
        return result;
    }

    private LuaUserData ReadUserData(int index)
    {
        var pointer = LuaNative.lua_touserdata(_state, index);
        if (!_userDataHandles.TryGetValue(pointer, out var handleValue))
            throw new NotSupportedException("The userdata was not created by this LuaState.");
        var entry = (UserDataHandle?)GCHandle.FromIntPtr(handleValue).Target
            ?? throw new ObjectDisposedException("Lua userdata");
        return new LuaUserData(entry.Target, entry.Registration.Name);
    }

    private LuaFunctionReference ReferenceFunction(int index)
    {
        index = AbsoluteIndex(index);
        LuaNative.lua_pushvalue(_state, index);
        var reference = LuaNative.luaL_ref(_state, LuaNative.RegistryIndex);
        return new LuaFunctionReference(this, reference);
    }

    private void PushValue(object? value, HashSet<LuaTable> tablePath)
    {
        switch (value)
        {
            case null:
                LuaNative.lua_pushnil(_state);
                return;
            case bool boolean:
                LuaNative.lua_pushboolean(_state, boolean ? 1 : 0);
                return;
            case byte or sbyte or short or ushort or int or uint or long:
                LuaNative.lua_pushinteger(_state, Convert.ToInt64(value));
                return;
            case ulong unsigned:
                if (unsigned <= long.MaxValue)
                    LuaNative.lua_pushinteger(_state, (long)unsigned);
                else
                    LuaNative.lua_pushnumber(_state, unsigned);
                return;
            case float or double or decimal:
                LuaNative.lua_pushnumber(_state, Convert.ToDouble(value));
                return;
            case string text:
                PushString(text);
                return;
            case LuaTable table:
                PushTable(table, tablePath);
                return;
            case LuaFunctionReference function:
                if (!ReferenceEquals(function.Owner, this))
                    throw new InvalidOperationException("A Lua function reference belongs to a different state.");
                LuaNative.lua_rawgeti(_state, LuaNative.RegistryIndex, function.Reference);
                return;
            case LuaUserData userData:
                PushUserData(userData.Target, FindRegistration(userData.Target, userData.TypeName));
                return;
            case LuaLightUserData light:
                throw new NotSupportedException($"Pushing light userdata ({light.Pointer}) is intentionally not exposed as a safe managed operation.");
            case IEnumerable sequence when TryPrimitiveSequence(sequence, out var sequenceTable):
                PushTable(sequenceTable, tablePath);
                return;
            default:
                PushUserData(value, FindRegistration(value, null));
                return;
        }
    }

    /// <summary>
    /// The UI data projections occasionally retain a CLR array in a field table (for example an item's category
    /// list). Lua has no CLR collection type, so pass scalar collections as ordinary one-based Lua tables instead of
    /// trying to expose their concrete array/list type as userdata.
    /// </summary>
    private static bool TryPrimitiveSequence(IEnumerable sequence, out LuaTable table)
    {
        table = new LuaTable();
        var index = 1;
        foreach (var value in sequence)
        {
            if (!IsLuaScalar(value))
            {
                table = null!;
                return false;
            }
            table[(double)index++] = value;
        }
        return true;
    }

    private static bool IsLuaScalar(object? value) => value is null or bool or byte or sbyte or short or ushort
        or int or uint or long or ulong or float or double or decimal or string;

    private void PushTable(LuaTable table, HashSet<LuaTable> tablePath)
    {
        if (!tablePath.Add(table))
            throw new NotSupportedException("Cyclic managed LuaTable values cannot be pushed.");
        try
        {
            LuaNative.lua_createtable(_state, 0, table.Count);
            foreach (var (key, value) in table)
            {
                PushValue(key, tablePath);
                PushValue(value, tablePath);
                LuaNative.lua_settable(_state, -3);
            }
        }
        finally
        {
            tablePath.Remove(table);
        }
    }

    private void CreateMetatable(UserDataRegistration registration)
    {
        var top = LuaNative.lua_gettop(_state);
        try
        {
            LuaNative.luaL_newmetatable(_state, registration.MetatableName);
            var index = RootCallback(state => UserDataIndex(state, registration));
            var newIndex = RootCallback(UserDataNewIndex);
            var gc = RootCallback(UserDataGarbageCollect);
            LuaNative.lua_pushcclosure(_state, index, 0);
            LuaNative.lua_setfield(_state, -2, "__index");
            LuaNative.lua_pushcclosure(_state, newIndex, 0);
            LuaNative.lua_setfield(_state, -2, "__newindex");
            LuaNative.lua_pushcclosure(_state, gc, 0);
            LuaNative.lua_setfield(_state, -2, "__gc");

            foreach (var (name, method) in registration.Methods)
                registration.Callbacks.Add(name, RootCallback(state => InvokeObjectMethod(state, registration, method)));
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    private void PushUserData(object target, UserDataRegistration registration)
    {
        if (_userDataRefs.TryGetValue(target, out var existing))
        {
            LuaNative.lua_rawgeti(_state, LuaNative.RegistryIndex, existing);
            return;
        }
        var pointer = LuaNative.lua_newuserdata(_state, (nuint)IntPtr.Size);
        var managedHandle = GCHandle.Alloc(new UserDataHandle(target, registration));
        var handleValue = GCHandle.ToIntPtr(managedHandle);
        Marshal.WriteIntPtr(pointer, handleValue);
        _userDataHandles.Add(pointer, handleValue);

        LuaNative.lua_createtable(_state, 0, 4);
        LuaNative.lua_setfenv(_state, -2);
        LuaNative.lua_getfield(_state, LuaNative.RegistryIndex, registration.MetatableName);
        LuaNative.lua_setmetatable(_state, -2);
        LuaNative.lua_pushvalue(_state, -1);
        _userDataRefs[target] = LuaNative.luaL_ref(_state, LuaNative.RegistryIndex);
    }

    /// <summary>Drops the state's strong reference to an object's userdata (it is collected once Lua drops it too).</summary>
    public void ReleaseObject(object target)
    {
        EnsureAccess();
        if (_userDataRefs.Remove(target, out var reference))
            LuaNative.luaL_unref(_state, LuaNative.RegistryIndex, reference);
    }

    /// <summary>Sets a Lua field on an object's userdata (stored in its environment table, as script assignments are).</summary>
    public void SetObjectField(object target, string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            PushUserData(target, FindRegistration(target, null));
            LuaNative.lua_getfenv(_state, -1);
            PushValue(value, new HashSet<LuaTable>(ReferenceEqualityComparer.Instance));
            LuaNative.lua_setfield(_state, -2, key);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    /// <summary>Reads a Lua field of an object's userdata (only fields set from Lua or <see cref="SetObjectField"/>).</summary>
    public object? GetObjectField(object target, string key)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            PushUserData(target, FindRegistration(target, null));
            LuaNative.lua_getfenv(_state, -1);
            LuaNative.lua_getfield(_state, -1, key);
            return ReadValue(-1, new Dictionary<nint, LuaTable>());
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    /// <summary>
    /// Calls a Lua function stored as a field of an object's userdata as a method (obj:key(args)).
    /// Returns null when the field is not a function.
    /// </summary>
    public object?[]? CallObjectMethod(object target, string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureAccess();
        var top = LuaNative.lua_gettop(_state);
        try
        {
            PushUserData(target, FindRegistration(target, null));
            LuaNative.lua_getfenv(_state, -1);
            LuaNative.lua_getfield(_state, -1, key);
            if (LuaNative.lua_type(_state, -1) != LuaTFunction)
                return null;
            LuaNative.lua_remove(_state, -2);
            LuaNative.lua_insert(_state, -2);
            foreach (var argument in arguments)
                PushValue(argument, new HashSet<LuaTable>(ReferenceEqualityComparer.Instance));
            ProtectedCall(arguments.Length + 1, LuaNative.MultipleReturns);
            return ReadResults(top);
        }
        finally
        {
            LuaNative.lua_settop(_state, top);
        }
    }

    private int UserDataIndex(nint state, UserDataRegistration registration)
    {
        try
        {
            LuaNative.lua_getfenv(state, 1);
            LuaNative.lua_pushvalue(state, 2);
            LuaNative.lua_gettable(state, -2);
            if (LuaNative.lua_type(state, -1) != LuaTNil)
            {
                LuaNative.lua_remove(state, -2);
                return 1;
            }
            LuaNative.lua_settop(state, -3);

            var name = LuaNative.lua_type(state, 2) == LuaTString ? ReadString(2) : null;
            if (name is not null && !registration.Callbacks.ContainsKey(name) && registration.Fallback is not null
                && !registration.Missing.Contains(name))
            {
                var method = registration.Fallback(name);
                if (method is null)
                    registration.Missing.Add(name);
                else
                    registration.Callbacks.Add(name, RootCallback(s => InvokeObjectMethod(s, registration, method)));
            }
            if (name is not null && registration.Callbacks.TryGetValue(name, out var callback))
                LuaNative.lua_pushcclosure(state, callback, 0);
            else
                LuaNative.lua_pushnil(state);
            return 1;
        }
        catch (Exception exception)
        {
            return CallbackError(state, exception);
        }
    }

    private int UserDataNewIndex(nint state)
    {
        try
        {
            LuaNative.lua_getfenv(state, 1);
            LuaNative.lua_pushvalue(state, 2);
            LuaNative.lua_pushvalue(state, 3);
            LuaNative.lua_settable(state, -3);
            return 0;
        }
        catch (Exception exception)
        {
            return CallbackError(state, exception);
        }
    }

    private int UserDataGarbageCollect(nint state)
    {
        try
        {
            var pointer = LuaNative.lua_touserdata(state, 1);
            if (pointer != 0 && _userDataHandles.Remove(pointer, out var handleValue))
            {
                Marshal.WriteIntPtr(pointer, 0);
                var handle = GCHandle.FromIntPtr(handleValue);
                if (handle.IsAllocated)
                    handle.Free();
            }
        }
        catch
        {
            // Finalizers run during lua_close; no managed exception may leave the callback.
        }
        return 0;
    }

    private int InvokeObjectMethod(nint state, UserDataRegistration registration, ManagedObjectMethod method)
    {
        try
        {
            var entry = GetUserDataHandle(1, registration);
            var arguments = ReadArguments(2);
            return PushResults(method(entry.Target, this, arguments));
        }
        catch (Exception exception)
        {
            return CallbackError(state, exception);
        }
    }

    private int InvokeFunctionCallback(nint state, LuaFunction function, int firstArgument)
    {
        try
        {
            return PushResults(function(this, ReadArguments(firstArgument)));
        }
        catch (Exception exception)
        {
            return CallbackError(state, exception);
        }
    }

    private IReadOnlyList<object?> ReadArguments(int first)
    {
        var count = LuaNative.lua_gettop(_state) - first + 1;
        if (count <= 0)
            return Array.Empty<object?>();
        var result = new object?[count];
        var seen = new Dictionary<nint, LuaTable>();
        for (var i = 0; i < count; i++)
            result[i] = ReadValue(first + i, seen);
        return result;
    }

    private int PushResults(IReadOnlyList<object?>? results)
    {
        if (results is null)
            return 0;
        foreach (var result in results)
            PushValue(result, new HashSet<LuaTable>(ReferenceEqualityComparer.Instance));
        return results.Count;
    }

    private int CallbackError(nint state, Exception exception)
    {
        try
        {
            PushString($"C# {exception.GetType().Name}: {exception.Message}");
        }
        catch
        {
            var bytes = Encoding.UTF8.GetBytes("C# callback failed");
            LuaNative.lua_pushlstring(state, bytes, (nuint)bytes.Length);
        }
        return LuaNative.lua_error(state);
    }

    private UserDataHandle GetUserDataHandle(int index, UserDataRegistration expected)
    {
        var pointer = LuaNative.lua_touserdata(_state, index);
        if (pointer == 0 || !_userDataHandles.TryGetValue(pointer, out var handleValue))
            throw new ArgumentException("Method receiver is not managed userdata.");
        var entry = (UserDataHandle?)GCHandle.FromIntPtr(handleValue).Target
            ?? throw new ObjectDisposedException("Lua userdata");
        if (!ReferenceEquals(entry.Registration, expected))
            throw new ArgumentException($"Method receiver is not userdata type '{expected.Name}'.");
        return entry;
    }

    private UserDataRegistration FindRegistration(object target, string? name)
    {
        if (name is not null)
        {
            if (!_userDataByName.TryGetValue(name, out var named) || !named.ManagedType.IsInstanceOfType(target))
                throw new InvalidOperationException($"No compatible userdata type named '{name}' is registered.");
            return named;
        }
        var type = target.GetType();
        if (_userDataByType.TryGetValue(type, out var exact))
            return exact;
        var compatible = _userDataByType.Values.FirstOrDefault(item => item.ManagedType.IsAssignableFrom(type));
        return compatible ?? throw new NotSupportedException($"No userdata type is registered for {type.FullName}.");
    }

    private LuaNative.CFunction RootCallback(LuaNative.CFunction callback)
    {
        _callbacks.Add(callback);
        return callback;
    }

    private string? ReadString(int index)
    {
        var pointer = LuaNative.lua_tolstring(_state, index, out var length);
        if (pointer == 0)
            return null;
        if (length > int.MaxValue)
            throw new NotSupportedException("Lua string exceeds the maximum managed string size.");
        return Marshal.PtrToStringUTF8(pointer, checked((int)length));
    }

    private void PushString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        LuaNative.lua_pushlstring(_state, bytes, (nuint)bytes.Length);
    }

    private void RequireType(int index, int expected, string description)
    {
        var actual = LuaNative.lua_type(_state, index);
        if (actual == expected)
            return;
        var typePointer = LuaNative.lua_typename(_state, actual);
        var actualName = typePointer == 0 ? actual.ToString() : Marshal.PtrToStringUTF8(typePointer);
        throw new InvalidOperationException($"Expected {description} to be a Lua function/table of type {expected}, got {actualName}.");
    }

    private int AbsoluteIndex(int index) => index > 0 || index <= LuaNative.RegistryIndex
        ? index
        : LuaNative.lua_gettop(_state) + index + 1;

    private void Pop(int count) => LuaNative.lua_settop(_state, -count - 1);

    private void EnsureAccess()
    {
        EnsureThread();
        ObjectDisposedException.ThrowIf(_state == 0, this);
    }

    private void EnsureThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException($"LuaState is thread-affine to managed thread {_ownerThreadId}.");
    }

    private static string NormalizeChunkName(string name) => name[0] is '@' or '=' ? name : "@" + name;

    private static readonly LuaNative.Reader BufferReaderCallback = BufferReader;

    private static nint BufferReader(nint state, nint data, out nuint size)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(data);
            var context = (BufferLoadContext?)handle.Target;
            if (context is null || context.Consumed || context.Bytes.Length == 0)
            {
                size = 0;
                return 0;
            }
            context.Consumed = true;
            size = (nuint)context.Bytes.Length;
            return context.Pointer;
        }
        catch
        {
            size = 0;
            return 0;
        }
    }

    private delegate IReadOnlyList<object?> ManagedObjectMethod(object target, LuaState state, IReadOnlyList<object?> arguments);

    private sealed class UserDataRegistration(string name, Type managedType)
    {
        public string Name { get; } = name;
        public Type ManagedType { get; } = managedType;
        public string MetatableName { get; } = $"AAEmu.GodotViewer.Lua:{name}";
        public Dictionary<string, ManagedObjectMethod> Methods { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, LuaNative.CFunction> Callbacks { get; } = new(StringComparer.Ordinal);
        public Func<string, ManagedObjectMethod?>? Fallback { get; set; }
        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);
    }

    private sealed record UserDataHandle(object Target, UserDataRegistration Registration);

    private sealed class BufferLoadContext : IDisposable
    {
        private GCHandle _pin;

        public BufferLoadContext(byte[] bytes)
        {
            Bytes = bytes;
            if (bytes.Length > 0)
                _pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        }

        public byte[] Bytes { get; }
        public bool Consumed { get; set; }
        public nint Pointer => _pin.IsAllocated ? _pin.AddrOfPinnedObject() : 0;

        public void Dispose()
        {
            if (_pin.IsAllocated)
                _pin.Free();
        }
    }
}

public sealed record LuaLightUserData(nint Pointer);
public sealed record LuaThreadValue(nint Pointer);
public readonly record struct LuaNumberInfo(
    double Number,
    long Integer,
    bool IsIntegerBeforeConversion,
    bool IsIntegerAfterConversion);
