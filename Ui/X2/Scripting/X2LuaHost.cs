#nullable enable
using System.Text;
using AAEmu.GodotViewer.Lua;

namespace AAEmu.GodotViewer.Ui.X2.Scripting;

/// <summary>Arguments of a C# X2 binding (the colon-call self is already stripped).</summary>
public readonly struct LuaArgs(IReadOnlyList<object?> values)
{
    public IReadOnlyList<object?> Values => values ?? [];
    public int Count => Values.Count;
    public object? this[int i] => i < Count ? ScriptBinder.Unwrap(Values[i]) : null;
    public string? Str(int i) => this[i] switch { null => null, string s => s, double d => ScriptBinder.FormatNumber(d), var o => o.ToString() };
    public double Num(int i, double fallback = 0) => this[i] switch { double d => d, string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d, bool b => b ? 1 : 0, _ => fallback };
    public int Int(int i, int fallback = 0) => (int)Num(i, fallback);
    public bool Bool(int i) => this[i] is not (null or false);
    public LuaFunctionReference? Func(int i) => this[i] as LuaFunctionReference;
    public LuaTable? Table(int i) => this[i] as LuaTable;
}

/// <summary>
/// Runs the client's x2ui Lua on the widget model: one native Lua 5.1 state, UIParent/widget/drawable/style userdata
/// bound by reflection, the engine constants and X2* namespaces (stubbed by default, overridden in C#), and TOC-ordered
/// package loading with the original chunk names (@game/scripts/...).
/// </summary>
public sealed class X2LuaHost : IDisposable
{
    private readonly IUiFileSource _files;
    private readonly Action<string> _log;
    private readonly HashSet<string> _apiStubHits = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];

    public X2LuaHost(UiRoot root, IUiFileSource files, Action<string> log)
    {
        Root = root;
        _files = files;
        _log = log;
        Lua = new LuaState();
        Binder = new ScriptBinder(m => _log(m));

        Lua.RegisterObjectType<UiRoot>("UIParent", new Dictionary<string, LuaObjectMethod<UiRoot>>(),
            name => name.Length > 0 && char.IsUpper(name[0]) ? Binder.Resolve<UiRoot>(name) : null);
        Lua.RegisterObjectType<Widget>("Widget", new Dictionary<string, LuaObjectMethod<Widget>>(), Fallback<Widget>);
        // drawables and styles carry no script-defined methods, so any capitalised name is treated as native
        Lua.RegisterObjectType<Drawable>("Drawable", new Dictionary<string, LuaObjectMethod<Drawable>>(),
            name => name.Length > 0 && char.IsUpper(name[0]) ? Binder.Resolve<Drawable>(name) : null);
        Lua.RegisterObjectType<WidgetStyle>("Style", new Dictionary<string, LuaObjectMethod<WidgetStyle>>(),
            name => name.Length > 0 && char.IsUpper(name[0]) ? Binder.Resolve<WidgetStyle>(name) : null);
        Lua.SetGlobalObject("UIParent", root);
        Lua.SetGlobalObject("UI", root);

        root.HandlerInvoker = InvokeHandler;
        root.FunctionInvoker = (f, args) => f is LuaFunctionReference fn ? First(Call(fn, args, "event handler")) : null;
        root.ScriptFieldSetter = (target, key, value) => Lua.SetObjectField(target, key, ScriptBinder.ToLuaValue(value));
        root.ScriptFieldReader = (target, key) => Lua.GetObjectField(target, key);
        root.WidgetCreated += OnWidgetCreated;
        root.WidgetReady += OnWidgetReady;
        root.NativeCallback = (name, args) => First(CallGlobal(name, args));
        root.DrawableCreated += d => { if (d is TextDrawable t) Lua.SetObjectField(t, "style", t.style); };
        root.WidgetDestroyed += w => { if (!Lua.IsDisposed) Lua.ReleaseObject(w); };
        root.LogSink = m => _log(m);

        Lua.RegisterFunction("__x2_stub_hit", (_, a) =>
        {
            var key = $"{a[0]}:{a[1]}";
            if (_apiStubHits.Add(key)) _log($"stub {key}");
            return [];
        });
        RunPrelude();
    }

    public LuaState Lua { get; }
    public UiRoot Root { get; }
    internal ScriptBinder Binder { get; }
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>X2 namespace functions and widget methods that were called but only stubbed.</summary>
    public IEnumerable<string> StubHits => _apiStubHits.Concat(Binder.StubHits.Keys).OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>
    /// Methods the model implements (on any subclass) or that the client scripts call natively are bound (stubbed when
    /// unimplemented); any other name reads as nil, so script checks like "if self.ShowProc ~= nil" behave.
    /// </summary>
    private LuaObjectMethod<T>? Fallback<T>(string name) where T : class
        => ModelMethodNames<T>().Contains(name) || WidgetApiNames.Native.Contains(name) ? Binder.Resolve<T>(name) : null;

    private static readonly Dictionary<Type, HashSet<string>> MethodNameCache = [];

    private static HashSet<string> ModelMethodNames<T>()
    {
        if (MethodNameCache.TryGetValue(typeof(T), out var names)) return names;
        names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in typeof(T).Assembly.GetTypes().Where(t => typeof(T).IsAssignableFrom(t)))
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                if (m.DeclaringType != typeof(object) && !m.IsSpecialName) names.Add(m.Name);
        MethodNameCache[typeof(T)] = names;
        return names;
    }

    private void OnWidgetCreated(Widget w)
    {
        // The client exposes every child widget as a field of its parent under its id (scroll.vs, vs.thumb, dlg.btnOk).
        // With an index > 0 the children form an array: parent.button[1], parent.button[2], ...
        if (w.Parent != null && w.PublishToParent)
        {
            if (w.ChildIndex > 0) CallGlobal("__x2_set_indexed", w.Parent, w.Id, (double)w.ChildIndex, w);
            else Lua.SetObjectField(w.Parent, w.Id, w);
        }
        Lua.SetObjectField(w, "style", w.style);
        if (w is EditBoxWidget edit) Lua.SetObjectField(w, "guideTextStyle", edit.guideTextStyle);
        if (w is WindowWidget window) Lua.SetObjectField(w, "titleStyle", window.titleStyle);
        foreach (var (name, value) in w.NativeFields) Lua.SetObjectField(w, name, value);
    }

    private void OnWidgetReady(Widget w)
    {
        // These callbacks are native engine hooks in the original client. They must run only after the native
        // controls exist because the Lua setup immediately fetches and styles them.
        if (w is ChatWindowWidget) CallGlobal("OnCreateChatWindow", w);
        else if (w is MessageWidget && w.Parent is ChatWindowWidget) CallGlobal("OnCreateChatMessage", w);
    }

    private const string StubBootstrap = """
        local stubHit = __x2_stub_hit
        local defaults = { n = 0, b = false, s = "" }
        for ns, methods in pairs(__X2_API) do
            local t = rawget(_G, ns)
            if t == nil then t = {} ; rawset(_G, ns, t) end
            setmetatable(t, { __index = function(tbl, key)
                local kind = methods[key]
                if kind == nil then return nil end
                local f
                f = function(...)
                    -- scripts may keep a stub they read before the world APIs were installed
                    -- (components/buffwindow.lua caches X2Unit.UnitBuffCount): forward to the real one
                    local current = rawget(tbl, key)
                    if current ~= nil and current ~= f then return current(...) end
                    stubHit(ns, key)
                    if kind == "t" then return {} end
                    return defaults[kind]
                end
                rawset(tbl, key, f)
                return f
            end })
        end
        FONT_PATH = { DEFAULT = "font_main", SUB = "font_sub", COMBAT = "font_combat" }
        function __x2_set_indexed(parent, id, index, child)
            local t = parent[id]
            if type(t) ~= "table" then t = {} ; parent[id] = t end
            t[index] = child
        end
        F_TEXT = F_TEXT or {}
        function F_TEXT.GetUIText(category, key, ...) return X2Locale:LocalizeUiText(category, key, ...) end
        function F_TEXT.GetCommonText(key, ...) return X2Locale:LocalizeUiText(COMMON_TEXT, key, ...) end
        -- F_TEXT helpers the client implements natively (not in any script)
        function F_TEXT.SetColonFormat(a, b) return tostring(a) .. ": " .. tostring(b) end
        function F_TEXT.SetSlashFormat(a, b) return tostring(a) .. "/" .. tostring(b) end
        function F_TEXT.SetCommaFormat(v)
            local s = tostring(v)
            local sign, int, frac = string.match(s, "^([-]?)(%d+)(.*)$")
            if int == nil then return s end
            int = string.reverse(string.gsub(string.reverse(int), "(%d%d%d)", "%1,"))
            if string.sub(int, 1, 1) == "," then int = string.sub(int, 2) end
            return sign .. int .. frac
        end
        function F_TEXT.SetSignFormat(showPlus, v)
            if v == nil then v = showPlus; showPlus = true end
            local n = tonumber(v) or 0
            if n > 0 and showPlus then return "+" .. tostring(v) end
            return tostring(v)
        end
        function F_TEXT.IStr(v) local n = tonumber(v); if n == nil then return tostring(v) end; return string.format("%d", n) end
        function F_TEXT.GetEllipsisStr() return "..." end
        function F_TEXT.Replace(s, a, b)
            local text, from, out = tostring(s), 1, ""
            while true do
                local i, j = string.find(text, a, from, true)
                if i == nil then return out .. string.sub(text, from) end
                out = out .. string.sub(text, from, i - 1) .. b
                from = j + 1
            end
        end
        function F_TEXT.RemoveNonDigit(s) return (string.gsub(tostring(s), "%D", "")) end
        function F_TEXT.HasLocalizeUiText(category, key) return X2Locale:HasLocalizeUiText(category, key) end
        function F_TEXT.GetEquipReinforceLevelString(level) return "+" .. tostring(level or 0) end
        function F_TEXT.CreateCountingText(id, parent, text)
            local label = parent:CreateChildWidget("label", id, 0, true)
            label:SetAutoResize(true)
            label:SetText(text or "")
            return label
        end
        -- The native build's string.format rejects non-integral numbers for %d/%x; the client passes values such as
        -- colour * 255 there, so integral conversions get the classic 5.1 behaviour (round when within 1e-6, else truncate).
        do
            local rawFormat, floor, abs, select, unpack = string.format, math.floor, math.abs, select, unpack
            local integral = { d = true, i = true, x = true, X = true, o = true, u = true, c = true }
            string.format = function(f, ...)
                local ok, result = pcall(rawFormat, f, ...)
                if ok then return result end
                local n = select("#", ...)
                local args = { ... }
                local index = 0
                for spec in string.gmatch(f, "%%[-+ #0]*%d*%.?%d*(.)") do
                    if spec ~= "%" then
                        index = index + 1
                        local v = args[index]
                        if integral[spec] and type(v) == "number" then
                            local r = floor(v + 0.5)
                            if abs(v - r) < 1e-6 then args[index] = r
                            elseif v >= 0 then args[index] = floor(v)
                            else args[index] = -floor(-v) end
                        end
                    end
                end
                local ok2, result2 = pcall(rawFormat, f, unpack(args, 1, n))
                if ok2 then return result2 end
                -- report at the caller (not this wrapper) with the format string, so a nil argument can be traced
                error(tostring(result2) .. " in format '" .. tostring(f) .. "'", 2)
            end
        end
        """;

    private void RunPrelude()
    {
        Lua.DoString(X2ApiData.Prelude, "=x2api");
        Lua.DoString(StubBootstrap, "=x2stubs");
        var sizes = new StringBuilder("FONT_SIZE = { ");
        foreach (var (name, value) in Root.Settings.FontSizes)
            sizes.Append($"{name.ToUpperInvariant()} = {ScriptBinder.FormatNumber(value)}, ");
        sizes.Append($"DEFAULT = {ScriptBinder.FormatNumber(Root.Settings.DefaultFontSize)} }}");
        Lua.DoString(sizes.ToString(), "=x2fontsize");
    }

    /// <summary>Defines Namespace:name(...) in C#; <paramref name="fn"/> returns the results (null for none).</summary>
    public void Define(string ns, string name, Func<LuaArgs, object?> fn, bool nullIsNil = false)
    {
        // Number/boolean APIs normally use a non-nil fallback so scripts can safely calculate with them.
        // Callers can preserve nil where the native API uses it to signal that data is absent.
        var kind = ReturnKind(ns, name);
        object? fallback = kind switch { "n" => 0.0, "b" => false, _ => null };
        Lua.RegisterMethod(ns, name, (_, args) =>
        {
            var result = fn(new LuaArgs(args));
            if (result == null && !nullIsNil) result = fallback;
            return result switch
            {
                null => [],
                LuaMulti m => m.Values.Select(ScriptBinder.ToLuaValue).ToArray(),
                _ => [ScriptBinder.ToLuaValue(result)],
            };
        });
    }

    private Dictionary<string, string>? _kinds;

    /// <summary>The return kind ("n", "b", "s", "t" or "") the generated API data gives Namespace:name.</summary>
    public string ReturnKind(string ns, string name)
    {
        if (_kinds == null)
        {
            _kinds = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Lua.GetGlobal("__X2_API") is LuaTable api)
                foreach (var (table, methods) in api)
                    if (methods is LuaTable m)
                        foreach (var (method, k) in m) _kinds[$"{table}.{method}"] = k as string ?? "";
        }
        return _kinds.GetValueOrDefault($"{ns}.{name}", "");
    }

    /// <summary>Defines a plain global function.</summary>
    public void DefineGlobal(string name, Func<LuaArgs, object?> fn)
        => Lua.RegisterFunction(name, (_, args) =>
        {
            var result = fn(new LuaArgs(args));
            return result switch
            {
                null => [],
                LuaMulti m => m.Values.Select(ScriptBinder.ToLuaValue).ToArray(),
                _ => [ScriptBinder.ToLuaValue(result)],
            };
        });

    // ------------------------------------------------------------------ packages

    /// <summary>
    /// Loads scripts/&lt;root&gt;/&lt;package&gt;/toc.g in order. A failing file is logged and skipped, as the client does.
    /// </summary>
    public int LoadPackage(string root, string package)
    {
        var dir = $"scripts/{root}/{package}";
        // the client runs the compiled scripts (game/scriptsbin64/**/*.alb, their own toc.g); the .lua sources are older
        var toc = UseCompiledScripts ? ReadText($"scriptsbin64/{root}/{package}/toc.g") : null;
        toc ??= ReadText($"{dir}/toc.g");
        if (toc == null)
        {
            _log($"package {root}/{package}: no toc.g");
            return 0;
        }
        var loaded = 0;
        foreach (var raw in toc.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var path = ResolveTocPath(root, dir, line);
            if (RunFile(path)) loaded++;
        }
        _log($"package {root}/{package}: {loaded} files");
        return loaded;
    }

    private static string ResolveTocPath(string root, string dir, string entry)
    {
        entry = entry.Replace('\\', '/');
        if (entry.StartsWith("SCRIPTS/", StringComparison.OrdinalIgnoreCase))
            return NormalizeDots($"scripts/x2ui/{entry[8..]}");
        return NormalizeDots($"{dir}/{entry}");
    }

    private static string NormalizeDots(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != "." && p.Length > 0) parts.Add(p);
        }
        return string.Join('/', parts).ToLowerInvariant();
    }

    /// <summary>
    /// Prefer the shipped bytecode (game/scriptsbin64/.../*.alb, what the client executes) over the .lua sources.
    /// Bytecode keeps its own chunk names (the build machine's scriptsworking paths).
    /// </summary>
    public bool UseCompiledScripts { get; set; } = true;

    /// <summary>Runs scripts/... with the chunk name @game/scripts/...; returns false (and logs) on error.</summary>
    public bool RunFile(string path)
    {
        if (UseCompiledScripts && path.StartsWith("scripts/", StringComparison.Ordinal) && path.EndsWith(".lua", StringComparison.Ordinal))
        {
            var compiled = _files.Read("scriptsbin64/" + path[8..^4] + ".alb");
            if (compiled != null) return RunChunk(compiled, "@game/" + path);
        }
        var bytes = _files.Read(path);
        if (bytes == null)
        {
            Fail($"missing script {path}");
            return false;
        }
        return RunChunk(bytes, "@game/" + path);
    }

    public bool RunChunk(byte[] code, string chunkName)
    {
        try
        {
            Lua.DoBuffer(code, chunkName);
            return true;
        }
        catch (LuaException e)
        {
            Fail(e.Traceback);
            return false;
        }
    }

    public bool RunString(string code, string chunkName) => RunChunk(Encoding.UTF8.GetBytes(code), chunkName);

    /// <summary>
    /// Bridges the legacy FONT_COLOR_HEX table used by older dialog scripts to the newer compiled F_COLOR API.
    /// The compiled commonui color package in this client build no longer defines FONT_COLOR_HEX, while extracted
    /// source-only dialog handlers still reference it.
    /// </summary>
    public void EnsureLegacyFontColorHex()
    {
        RunString("""
            if FONT_COLOR_HEX == nil and type(F_COLOR) == "table" and type(F_COLOR.GetColor) == "function" then
                local aliases = { PORTAL = "lemon", SET_ORANGE = "orange" }
                local colors = {
                    CONGESTION = {
                        F_COLOR.GetColor("congestion_low", true),
                        F_COLOR.GetColor("congestion_middle", true),
                        F_COLOR.GetColor("congestion_high", true)
                    }
                }
                setmetatable(colors, { __index = function(t, key)
                    if type(key) ~= "string" then return nil end
                    local name = aliases[key] or string.lower(key)
                    local value = F_COLOR.GetColor(name, true)
                    rawset(t, key, value)
                    return value
                end })
                FONT_COLOR_HEX = colors
            end
            """, "=@x2ui-compat/font-color-hex");
    }

    private string? ReadText(string path) => _files.Read(path) is { } b ? Encoding.UTF8.GetString(b) : null;

    private void Fail(string message)
    {
        _errors.Add(message);
        _log("[lua error] " + message);
    }

    // ------------------------------------------------------------------ calls into Lua

    private object? InvokeHandler(Widget widget, object handler, string name, object?[] args)
    {
        if (handler is not LuaFunctionReference fn) return null;
        var full = new object?[args.Length + 1];
        full[0] = widget;
        Array.Copy(args, 0, full, 1, args.Length);
        return First(Call(fn, full, $"{widget}.{name}"));
    }

    public object?[] Call(LuaFunctionReference fn, object?[] args, string what)
    {
        try
        {
            return fn.Invoke(args.Select(ScriptBinder.ToLuaValue).ToArray());
        }
        catch (LuaException e)
        {
            Fail($"{what}: {e.Traceback}");
            return [];
        }
    }

    /// <summary>Calls a global Lua function by name; errors are logged.</summary>
    public object?[] CallGlobal(string name, params object?[] args)
    {
        try
        {
            return Lua.Call(name, args.Select(ScriptBinder.ToLuaValue).ToArray());
        }
        catch (Exception e) when (e is LuaException or InvalidOperationException)
        {
            Fail($"{name}: {(e is LuaException le ? le.Traceback : e.Message)}");
            return [];
        }
    }

    private static object? First(object?[] results) => results.Length > 0 ? ScriptBinder.Unwrap(results[0]) : null;

    public void Dispose()
    {
        Root.HandlerInvoker = null;
        Root.FunctionInvoker = null;
        Root.ScriptFieldSetter = null;
        Lua.Dispose();
    }
}
