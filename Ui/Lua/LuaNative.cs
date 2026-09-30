#nullable enable
using System.Runtime.InteropServices;

namespace AAEmu.GodotViewer.Lua;

/// <summary>Lua 5.1 ABI declarations used by <see cref="LuaState"/>.</summary>
public static class LuaNative
{
    public const string LibraryName = "lua51.dll";
    public const int RegistryIndex = -10000;
    public const int EnvironmentIndex = -10001;
    public const int GlobalsIndex = -10002;
    public const int MultipleReturns = -1;

    public static readonly IReadOnlyList<string> RequiredExports = new[]
    {
        "luaL_newstate", "luaL_openlibs", "lua_close", "luaL_loadbuffer", "lua_load",
        "lua_pcall", "lua_error", "lua_gettop", "lua_settop", "lua_checkstack",
        "lua_type", "lua_typename", "lua_toboolean", "lua_tonumber", "lua_tointeger",
        "lua_isinteger", "lua_tolstring", "lua_touserdata", "lua_topointer",
        "lua_pushnil", "lua_pushboolean", "lua_pushnumber", "lua_pushinteger",
        "lua_pushlstring", "lua_pushvalue", "lua_pushcclosure", "lua_createtable",
        "lua_getfield", "lua_setfield", "lua_gettable", "lua_settable", "lua_rawgeti",
        "lua_next", "lua_insert", "lua_remove", "lua_newuserdata", "lua_getmetatable",
        "lua_setmetatable", "lua_getfenv", "lua_setfenv", "luaL_newmetatable",
        "luaL_ref", "luaL_unref"
    };

    private static int _resolverInstalled;

    /// <summary>
    /// Makes lua51.dll load from the assembly's own folder (or AAEMU_LUA51 / Ui/Lua/native), also when the host
    /// (Godot) loads the assembly into a plugin load context that only probes deps.json assets.
    /// </summary>
    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
            return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LuaNative).Assembly, (name, assembly, searchPath) =>
            {
                if (!name.Equals(LibraryName, StringComparison.OrdinalIgnoreCase) && !name.Equals("lua51", StringComparison.OrdinalIgnoreCase))
                    return 0;
                foreach (var candidate in Candidates(assembly))
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                        return handle;
                return 0;
            });
        }
        catch (InvalidOperationException)
        {
            // another resolver is already registered for this assembly; default probing applies
        }
    }

    private static IEnumerable<string> Candidates(System.Reflection.Assembly assembly)
    {
        var env = Environment.GetEnvironmentVariable("AAEMU_LUA51");
        if (!string.IsNullOrEmpty(env)) yield return env;
        if (!string.IsNullOrEmpty(assembly.Location)) yield return Path.Combine(Path.GetDirectoryName(assembly.Location)!, LibraryName);
        yield return Path.Combine(AppContext.BaseDirectory, LibraryName);
        var cwd = Environment.CurrentDirectory;
        yield return Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Debug", LibraryName);
        yield return Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Release", LibraryName);
        yield return Path.Combine(cwd, "Ui", "Lua", "native", LibraryName);
    }

    /// <summary>Loads a DLL and returns required C API exports that are absent.</summary>
    public static IReadOnlyList<string> FindMissingExports(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        var handle = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        try
        {
            return RequiredExports.Where(name => !NativeLibrary.TryGetExport(handle, name, out _)).ToArray();
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int CFunction(nint state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint Reader(nint state, nint data, out nuint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern nint luaL_newstate();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void luaL_openlibs(nint state);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_close(nint state);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int luaL_loadbuffer(nint state, byte[] buffer, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_load(nint state, Reader reader, nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string chunkName);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_pcall(nint state, int argumentCount, int resultCount, int errorFunction);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_error(nint state);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_gettop(nint state);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_settop(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_checkstack(nint state, int extra);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_type(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern nint lua_typename(nint state, int type);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_toboolean(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern double lua_tonumber(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern long lua_tointeger(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_isinteger(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern nint lua_tolstring(nint state, int index, out nuint length);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern nint lua_touserdata(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern nint lua_topointer(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_pushnil(nint state);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_pushboolean(nint state, int value);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_pushnumber(nint state, double value);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_pushinteger(nint state, long value);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern nint lua_pushlstring(nint state, byte[] value, nuint length);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_pushvalue(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_pushcclosure(nint state, CFunction callback, int upvalueCount);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_createtable(nint state, int arrayCount, int recordCount);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_getfield(nint state, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_setfield(nint state, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_gettable(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_settable(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_rawgeti(nint state, int index, int reference);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_next(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_insert(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_remove(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern nint lua_newuserdata(nint state, nuint size);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_getmetatable(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_setmetatable(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void lua_getfenv(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int lua_setfenv(nint state, int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int luaL_newmetatable(nint state, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern int luaL_ref(nint state, int tableIndex);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)] public static extern void luaL_unref(nint state, int tableIndex, int reference);
}
