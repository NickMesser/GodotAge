#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Ui.X2;

// Scripted live tests: with X2_SCENARIO=<file> set, once in the world the layer plays the file's steps as real input events
// (they go through Godot's input like a player's keys) and saves screenshots, then quits. One step per line:
//   wait <seconds>          key <Key>[+<Key>...] (e.g. key Tab, key 1, key Shift+2, key Enter)
//   type <text>             (key events with unicode characters, e.g. into the chat line)
//   click <x> <y> [right]   drag <x1> <y1> <x2> <y2>   (UI pixels)   lua <code>   (runs in the UI's Lua state)
//   shot <name>             (screenshot into the file's folder)   log <text>
//   clicktext <text>        (clicks the centre of the visible button/text widget showing that text)
//   chat <line>             (sends the line as the chat box would, e.g. chat /move 15600 15214 123)
//   hideui / showui         (hides the UI layer for a clean 3D shot)
// Lines starting with # are comments. The scenario starts X2_SCENARIO_DELAY seconds (default 40) after world entry, or with
// X2_SCENARIO_LOBBY=1 after the character list (stage 8) first appears.
public partial class X2UiLayer
{
    private readonly string? _scenarioFile = System.Environment.GetEnvironmentVariable("X2_SCENARIO");
    private List<string>? _scenario;
    private int _scenarioLine;
    private double _scenarioClock = -1, _scenarioWaitUntil;
    private readonly bool _scenarioLobby = System.Environment.GetEnvironmentVariable("X2_SCENARIO_LOBBY") == "1";
    private bool _scenarioLobbySeen, _scenarioHideUi;
    private double _waitLoadUntil, _unitsTraceUntil, _unitsTraceNext;

    private void TraceUnits()
    {
        var session = LiveSession;
        var visible = session?.VisibleUnits ?? [];
        var me = session?.Player?.CryPosition;
        string Near(IEnumerable<(uint Id, uint T, string N, float X, float Y)> xs) => string.Join("; ", xs
            .OrderBy(u => me == null ? 0 : (u.X - me.Value.X) * (u.X - me.Value.X) + (u.Y - me.Value.Y) * (u.Y - me.Value.Y)).Take(4)
            .Select(u => $"{u.Id} t{u.T} '{u.N}' {(me == null ? 0 : MathF.Sqrt((u.X - me.Value.X) * (u.X - me.Value.X) + (u.Y - me.Value.Y) * (u.Y - me.Value.Y))):F0}m"));
        var sessionNear = Near(visible.Select(v => (v.Snapshot.UnitId, v.Snapshot.TemplateId, v.Snapshot.Name ?? "", v.Position.X, v.Position.Y)));
        var bridgeUnits = Bridge?.All.ToArray() ?? [];
        var bridgeNear = Near(bridgeUnits.Select(u => (u.Id, u.TemplateId, u.Name ?? "", u.X, u.Y)));
        Emit($"[x2 unitstrace] t={_scenarioClock:F0} me={(me == null ? "?" : $"{me.Value.X:F0},{me.Value.Y:F0}")} session#{session?.GetHashCode()} visible={visible.Count} " +
             $"bridge#{Bridge?.GetHashCode()} units={bridgeUnits.Length} binding.session#{_combatBinding?.AttachedSession?.GetHashCode()} " +
             $"binding.bridge#{_combatBinding?.WorldBridge.GetHashCode()} provider={(Bridge?.SessionUnitsProvider != null)}");
        Emit($"[x2 unitstrace]   session nearest: {sessionNear}");
        Emit($"[x2 unitstrace]   bridge nearest:  {bridgeNear}");
    }

    private string _sweepLabel = "";
    private long _sweepMark;
    private HashSet<Widget>? _sweepBefore;

    /// <summary>
    /// sweepend: one "[x2 sweep]" line for the content opened since sweepbegin: the new top-level windows, their visible
    /// widgets and texts, and the lua errors and stubbed/missing APIs logged meanwhile (a stub is logged once per run,
    /// so it is charged to the first content that hits it).
    /// </summary>
    private void SweepEnd()
    {
        // everything that became visible since sweepbegin; "opened" are its roots (a window, or a page inside one)
        var shown = Root?.AllWidgets().Where(x => x.IsEffectivelyVisible() && _sweepBefore?.Contains(x) != true).ToHashSet() ?? [];
        var opened = shown.Where(x => x.Parent == null || !shown.Contains(x.Parent)).ToArray();
        var visible = shown.Count;
        var texts = shown.Count(w => !string.IsNullOrWhiteSpace(w.GetText()));
        var since = (int)Math.Min(_log.Count, _emittedTotal - _sweepMark);
        var lines = _log.Skip(_log.Count - since).Where(l => !l.StartsWith("[x2 scenario]")).ToArray();
        var errors = lines.Where(l => l.StartsWith("[lua error]")).ToArray();
        var stubs = lines.Where(l => l.StartsWith("stub ")).Select(l => l[5..]).Distinct().ToArray();
        var nils = errors.SelectMany(e => System.Text.RegularExpressions.Regex.Matches(e, @"attempt to (?:call|index) (?:method|field|global|upvalue|local) '([^']+)' \(a nil value\)")
            .Select(m => m.Groups[1].Value)).Distinct().ToArray();
        var rects = string.Join(" ", opened.Select(w => $"{UiRoot.Describe(w)}@{w.ScreenRect.Width:F0}x{w.ScreenRect.Height:F0}"));
        Emit($"[x2 sweep] {_sweepLabel}	opened={opened.Length}	visible={visible}	texts={texts}	errors={errors.Length}" +
             $"	nil={string.Join(",", nils)}	stubs={string.Join(",", stubs)}	windows={rects}");
        foreach (var e in errors.Distinct().Take(3))
            Emit($"[x2 sweep-err] {_sweepLabel}: {(e.Length > 400 ? e[..400] : e)}");
    }

    private void ScenarioTick(double delta)
    {
        if (_scenarioFile == null || _session.Host is null) return;
        if (_scenarioLobby) _scenarioLobbySeen |= _session.Engine.Stage == Scripting.X2Engine.StageSelect;
        if (_scenarioLobby ? !_scenarioLobbySeen : !InWorld) return;
        if (_scenario == null)
        {
            _scenario = System.IO.File.ReadAllLines(_scenarioFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            _scenarioClock = 0;
            _scenarioWaitUntil = double.TryParse(System.Environment.GetEnvironmentVariable("X2_SCENARIO_DELAY"), out var d) ? d : 40;
        }
        _scenarioClock += delta;
        if (_unitsTraceUntil > _scenarioClock && _scenarioClock >= _unitsTraceNext)
        {
            _unitsTraceNext = _scenarioClock + 1;
            TraceUnits();
        }
        if (_waitLoadUntil > 0)
        {
            var loading = GetTree().Root.FindChild("LoadingScreen", true, false) is CanvasLayer { Visible: true };
            if (!loading || _scenarioClock >= _waitLoadUntil)
            {
                Emit($"[x2 scenario] waitload: {(loading ? "timed out" : "loaded")} at {_scenarioClock:F0} s");
                if (loading) Emit("[x2 scenario] WARNING: the loading screen is still up; the following screenshots show it, not the world");
                _waitLoadUntil = 0;
                _scenarioWaitUntil = _scenarioClock + 2;
            }
        }
        while (_scenarioClock >= _scenarioWaitUntil)
        {
            if (_scenarioLine >= _scenario.Count)
            {
                Emit("[x2] scenario done");
                GetTree().Quit();
                _scenarioWaitUntil = double.MaxValue;
                return;
            }
            var line = _scenario[_scenarioLine++];
            Emit($"[x2 scenario] {line}");
            try { RunScenarioStep(line); }
            catch (Exception e) { Emit($"[x2 scenario] step failed: {e.Message}"); }
        }
    }

    private void RunScenarioStep(string line)
    {
        var space = line.IndexOf(' ');
        var verb = space < 0 ? line : line[..space];
        var arg = space < 0 ? "" : line[(space + 1)..].Trim();
        switch (verb.ToLowerInvariant())
        {
            case "wait":
                _scenarioWaitUntil = _scenarioClock + double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "key":
                PressChord(arg);
                _scenarioWaitUntil = _scenarioClock + 0.15;
                break;
            case "hold":
            {
                // hold <Key> <seconds>: keeps a key down (movement), releasing it after the time
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var key = ParseKey(parts[0]);
                var seconds = parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
                Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
                GetTree().CreateTimer(seconds).Timeout += () =>
                    Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
                _scenarioWaitUntil = _scenarioClock + seconds + 0.2;
                break;
            }
            case "type":
                foreach (var ch in arg)
                {
                    Input.ParseInputEvent(new InputEventKey { Pressed = true, Unicode = ch, Keycode = CharKey(ch) });
                    Input.ParseInputEvent(new InputEventKey { Pressed = false, Unicode = ch, Keycode = CharKey(ch) });
                }
                _scenarioWaitUntil = _scenarioClock + 0.15;
                break;
            case "click":
            {
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var s = Math.Max(0.1f, UiScale);
                var pos = new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * s,
                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * s);
                var button = parts.Length > 2 && parts[2] == "right" ? MouseButton.Right : MouseButton.Left;
                Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = button, Pressed = true });
                Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = button, Pressed = false });
                _scenarioWaitUntil = _scenarioClock + 0.2;
                break;
            }
            case "hover":
            {
                // hover <x> <y>: moves the pointer there (tooltips, OnEnter)
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var s = Math.Max(0.1f, UiScale);
                var pos = new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * s,
                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * s);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                _scenarioWaitUntil = _scenarioClock + 0.3;
                break;
            }
            case "drag":
            {
                // drag x1 y1 x2 y2: press at the first point, move in steps, release at the second
                var v = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture) * Math.Max(0.1f, UiScale)).ToArray();
                var from = new Vector2(v[0], v[1]);
                var to = new Vector2(v[2], v[3]);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = from, GlobalPosition = from });
                Input.ParseInputEvent(new InputEventMouseButton { Position = from, GlobalPosition = from, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
                for (var i = 1; i <= 8; i++)
                {
                    var p = from.Lerp(to, i / 8f);
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = MouseButtonMask.Left });
                }
                Input.ParseInputEvent(new InputEventMouseButton { Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Left, Pressed = false });
                _scenarioWaitUntil = _scenarioClock + 0.3;
                break;
            }
            case "hoverbag":
            case "rclickbag":
            {
                // hoverbag <item template id>: moves the pointer onto the visible bag slot that holds that item (live InventoryState);
                // rclickbag: right-clicks it (use / sell / attach)
                var template = uint.Parse(arg.Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var entry = LiveSession?.InventoryState.Items.FirstOrDefault(p => p.Key.SlotType == (byte)Net.InventorySlotType.Inventory && p.Value.TemplateId == template);
                if (entry is not { } found || found.Value == null) { Emit($"[x2 scenario] no bag item {template}"); break; }
                var slotIndex = found.Key.Slot;
                var w = Root?.AllWidgets().OfType<SlotWidget>().FirstOrDefault(x => x.IsEffectivelyVisible() && x.SlotType == "2" && (int)x.SlotIndex == slotIndex);
                if (w == null) { Emit($"[x2 scenario] bag slot {slotIndex} (item {template}) not visible"); break; }
                Emit($"[x2 scenario] {verb} item {template} at wire slot {slotIndex}: {UiRoot.Describe(w)}");
                var r = w.ScreenRect;
                var sc = Math.Max(0.1f, UiScale);
                var pos = new Vector2((r.X + r.Width / 2) * sc, (r.Y + r.Height / 2) * sc);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                if (verb == "rclickbag")
                {
                    Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Right, Pressed = true });
                    Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Right, Pressed = false });
                }
                _scenarioWaitUntil = _scenarioClock + 0.3;
                break;
            }
            case "hoverid":
            {
                // hoverid <path substring>: moves the pointer onto the first visible widget whose path contains the text
                var w = Root?.AllWidgets().Where(x => x.IsEffectivelyVisible() && UiRoot.Describe(x).Contains(arg, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => UiRoot.Describe(x).Length).FirstOrDefault();
                if (w == null) { Emit($"[x2 scenario] no widget matching {arg}"); break; }
                var r = w.ScreenRect;
                var sc = Math.Max(0.1f, UiScale);
                var pos = new Vector2((r.X + r.Width / 2) * sc, (r.Y + r.Height / 2) * sc);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                _scenarioWaitUntil = _scenarioClock + 0.3;
                break;
            }
            case "clickid":
            case "rclickid":
            {
                // clickid <path substring>: clicks the centre of the first visible widget whose path (window:x/button:y) contains the text;
                // rclickid: same with the right button
                var w = Root?.AllWidgets().Where(x => x.IsEffectivelyVisible() && UiRoot.Describe(x).Contains(arg, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => UiRoot.Describe(x).Length).FirstOrDefault();
                if (w == null) { Emit($"[x2 scenario] no widget matching {arg}"); break; }
                Emit($"[x2 scenario] clicking {UiRoot.Describe(w)}");
                var r = w.ScreenRect;
                var sc = Math.Max(0.1f, UiScale);
                var pos = new Vector2((r.X + r.Width / 2) * sc, (r.Y + r.Height / 2) * sc);
                var btn = verb.StartsWith('r') ? MouseButton.Right : MouseButton.Left;
                Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = btn, Pressed = true });
                Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = btn, Pressed = false });
                _scenarioWaitUntil = _scenarioClock + 0.2;
                break;
            }
            case "windows":
            {
                // windows: logs every visible top-level window (id, rect) to find what is open
                foreach (var w in Root?.AllWidgets().Where(x => x.Parent == null && x.IsEffectivelyVisible()) ?? [])
                    Emit($"[x2 scenario] window {UiRoot.Describe(w)} rect={w.ScreenRect.X:F0},{w.ScreenRect.Y:F0} {w.ScreenRect.Width:F0}x{w.ScreenRect.Height:F0}");
                break;
            }
            case "clicktext":
            case "dclicktext":
            {
                // clicktext <text>: clicks the visible widget showing exactly that text (buttons first);
                // dclicktext: double-clicks it (list rows that open on double click, e.g. mail)
                var w = Root?.AllWidgets().Where(x => x.IsEffectivelyVisible() && x.GetText() == arg)
                    .OrderByDescending(x => x is ButtonWidget).FirstOrDefault();
                if (w == null) { Emit($"[x2 scenario] no widget with text {arg}"); break; }
                var r = w.ScreenRect;
                var s = Math.Max(0.1f, UiScale);
                var pos = new Vector2((r.X + r.Width / 2) * s, (r.Y + r.Height / 2) * s);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
                for (var n = verb.StartsWith('d') ? 2 : 1; n > 0; n--)
                {
                    Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = n == 1 && verb.StartsWith('d') });
                    Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = false });
                }
                _scenarioWaitUntil = _scenarioClock + 0.2;
                break;
            }
            case "waitload":
            {
                // waitload [seconds]: waits (up to the limit, default 480 s) until the world loading screen is gone, then 2 s more.
                // A cold start of zone 179 has taken up to 268 s for the initial 49-58 cells, so 180 s was too short.
                var limit = arg.Length > 0 ? double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 480;
                _waitLoadUntil = _scenarioClock + limit;
                _scenarioWaitUntil = double.MaxValue;
                break;
            }
            case "dump":
            {
                // dump <id substring>: logs every widget whose id contains the text (and its first 3 levels of children):
                // shown / effective visibility, alpha, rect, text
                var count = 0;
                var dumpParts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var needle = dumpParts.Length > 0 ? dumpParts[0] : "";
                var maxDepth = dumpParts.Length > 1 && int.TryParse(dumpParts[1], out var dd) ? dd : 3;
                foreach (var w in Root?.AllWidgets() ?? [])
                {
                    if (!w.Id.Contains(needle, StringComparison.OrdinalIgnoreCase) || !w.IsEffectivelyVisible() || count++ > 30) continue;
                    void Line(Widget x, int depth)
                    {
                        var r = x.ScreenRect;
                        Emit($"[x2 dump] {new string(' ', depth * 2)}{UiRoot.Describe(x)} shown={x.Visible} eff={x.IsEffectivelyVisible()} a={x.Alpha:F2} " +
                             $"rect={r.X:F0},{r.Y:F0} {r.Width:F0}x{r.Height:F0} enabled={x.Enabled} text='{x.GetText()}'");
                        if (depth < maxDepth) foreach (var c in x.Children) if (c.IsEffectivelyVisible()) Line(c, depth + 1);
                    }
                    Line(w, 0);
                }
                break;
            }
            case "chat":
                // chat <line>: sends a chat line as the chat box would (slash commands included, e.g. /move x y z)
                SendChatLine(0, arg);
                break;
            case "hideui":
                _scenarioHideUi = true;
                break;
            case "showui":
                _scenarioHideUi = false;
                break;
            case "stage":
                Emit($"[x2 scenario] stage {_session.Engine.Stage}");
                break;
            case "target":
            case "targetdead":
            {
                // target <name>: selects the nearest living unit whose name contains the text, as clicking it would;
                // targetdead <name>: the nearest corpse instead
                var me = Bridge?.Get(Bridge.PlayerId);
                var dead = verb.Equals("targetdead", StringComparison.OrdinalIgnoreCase);
                var unit = Bridge?.All.Where(u => u.Id != Bridge.PlayerId && u.IsDead == dead && u.Name.Contains(arg, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(u => me == null ? 0 : (u.X - me.X) * (u.X - me.X) + (u.Y - me.Y) * (u.Y - me.Y)).FirstOrDefault();
                if (unit == null) { Emit($"[x2 scenario] no unit named {arg}"); break; }
                Emit($"[x2 scenario] targeting {unit.Name} ({unit.Id})");
                Bridge!.RequestTarget(unit.Id);
                break;
            }
            case "unitstrace":
            {
                // unitstrace [seconds]: logs, once a second, the session's scene units vs the UI bridge (counts, instances, nearest)
                _unitsTraceUntil = _scenarioClock + (arg.Length > 0 ? double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 10);
                _unitsTraceNext = 0;
                break;
            }
            case "usedoodad":
            {
                // usedoodad <template id>: right-click interaction with the nearest doodad of that template (no target selection;
                // the server's CSChangeTarget only knows units, so doodads are used directly as in the original client)
                var me = Bridge?.Get(Bridge.PlayerId);
                var templateId = uint.Parse(arg.TrimStart('t', 'T'), System.Globalization.CultureInfo.InvariantCulture);
                var doodad = Bridge?.All.Where(u => u.Type == "doodad" && u.TemplateId == templateId)
                    .OrderBy(u => me == null ? 0 : (u.X - me.X) * (u.X - me.X) + (u.Y - me.Y) * (u.Y - me.Y)).FirstOrDefault();
                if (doodad == null) { Emit($"[x2 scenario] no doodad {templateId}"); break; }
                var ok = LiveSession?.InteractWith(doodad.Id) == true;
                Emit($"[x2 scenario] use doodad {doodad.Id} template {templateId} at {doodad.X:F1} {doodad.Y:F1}: {(ok ? "sent" : "refused")}");
                break;
            }
            case "targetdoodad":
            {
                // targetdoodad <name|template id>: selects the nearest matching live doodad.
                var me = Bridge?.Get(Bridge.PlayerId);
                var hasTemplate = uint.TryParse(arg.TrimStart('t', 'T'), out var templateId);
                var doodad = Bridge?.All.Where(u => u.Id != Bridge.PlayerId && u.Type == "doodad" &&
                        (hasTemplate ? u.TemplateId == templateId :
                            u.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(u => me == null ? 0 : (u.X - me.X) * (u.X - me.X) + (u.Y - me.Y) * (u.Y - me.Y))
                    .FirstOrDefault();
                if (doodad == null) { Emit($"[x2 scenario] no doodad matching {arg}"); break; }
                Emit($"[x2 scenario] targeting doodad {doodad.Name} ({doodad.Id}) template {doodad.TemplateId}");
                Bridge!.RequestTarget(doodad.Id);
                break;
            }
            case "units":
            {
                // units: logs the units in sight (id, template, name, distance), nearest first
                var me = Bridge?.Get(Bridge.PlayerId);
                if (me != null) Emit($"[x2 scenario] me at {me.X:F1} {me.Y:F1} {me.Z:F1}");
                foreach (var u in (Bridge?.All ?? []).Where(u => u.Id != Bridge!.PlayerId && u.Type != "doodad")
                             .OrderBy(u => me == null ? 0 : (u.X - me.X) * (u.X - me.X) + (u.Y - me.Y) * (u.Y - me.Y)).Take(40))
                    Emit($"[x2 scenario] unit {u.Id} t{u.TemplateId} {u.Type} '{u.Name}' lv{u.Level} " +
                         $"{(me == null ? 0 : MathF.Sqrt((u.X - me.X) * (u.X - me.X) + (u.Y - me.Y) * (u.Y - me.Y))):F0}m");
                break;
            }
            case "movetarget":
            {
                // movetarget [metres]: GM-moves next to the current target (server /move), <metres> short of it
                var target = Bridge?.Get(Bridge.TargetId);
                var me = Bridge?.Get(Bridge.PlayerId);
                if (target == null || me == null) { Emit("[x2 scenario] no target"); break; }
                var gap = arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 3f;
                var dx = me.X - target.X; var dy = me.Y - target.Y;
                var len = MathF.Max(0.01f, MathF.Sqrt(dx * dx + dy * dy));
                var x = target.X + dx / len * gap; var y = target.Y + dy / len * gap;
                SendChatLine(0, string.Format(System.Globalization.CultureInfo.InvariantCulture, "/move {0:F1} {1:F1} {2:F1}", x, y, target.Z + 1));
                break;
            }
            case "eval":
            {
                // eval <lua expression>: logs the value (tables as key=value lists)
                var host = _session.Host!;
                host.RunString("local function dump(v, d) if type(v) ~= 'table' or d > 2 then return tostring(v) end local t = {} " +
                               "for k, x in pairs(v) do t[#t + 1] = tostring(k) .. '=' .. dump(x, d + 1) end return '{' .. table.concat(t, ', ') .. '}' end " +
                               "SCENARIO_EVAL = dump((function() return " + arg + " end)(), 0)", "=scenario_eval");
                Emit($"[x2 scenario] eval {arg} => {host.Lua.GetGlobal("SCENARIO_EVAL")}");
                break;
            }
            case "lua":
                _session.Host?.RunString(arg, "=scenario");
                break;
            case "sweepbegin":
                // sweepbegin <label>: marks the log and the visible widgets before opening a content
                _sweepLabel = arg;
                _sweepMark = _emittedTotal;
                _sweepBefore = new HashSet<Widget>(Root?.AllWidgets().Where(x => x.IsEffectivelyVisible()) ?? []);
                break;
            case "sweepend":
                SweepEnd();
                break;
            case "sweepclose":
                // sweepclose: hides every top-level window opened since sweepbegin (after the script's own close)
                foreach (var w in Root?.TopLevel.Where(x => x.IsEffectivelyVisible() && _sweepBefore?.Contains(x) != true).ToArray() ?? [])
                    w.Show(false);
                // a page opened inside an already open window (e.g. a community tab) is left to the script's own close
                break;
            case "shot":
            {
                var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_scenarioFile!)) ?? ".";
                GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, arg + ".png"));
                break;
            }
            case "log":
                Emit("[x2 scenario] " + arg);
                break;
            case "bones":
            {
                // bones <substring>: logs the bones of the lobby character's skeleton whose name contains the text, with their global pose
                var skeleton = _loginModelView?.Character?.Skeleton;
                if (skeleton == null) { Emit("[x2 bones] no lobby character"); break; }
                Emit($"[x2 bones] {skeleton.GetBoneCount()} bones, skeleton global {skeleton.GlobalTransform.Origin}");
                for (var i = 0; i < skeleton.GetBoneCount(); i++)
                {
                    var name = skeleton.GetBoneName(i).ToString();
                    if (arg.Length > 0 && !name.Contains(arg, StringComparison.OrdinalIgnoreCase)) continue;
                    var pose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(i);
                    Emit($"[x2 bones] {i} '{name}' parent {skeleton.GetBoneParent(i)} origin {pose.Origin} fwd {-pose.Basis.Z} up {pose.Basis.Y}");
                }
                break;
            }
            case "project":
            {
                // project <bone> [<bone>...]: logs the pixel (of the lobby viewport) each named bone of the lobby character projects to
                var view = _loginModelView;
                var skeleton = view?.Character?.Skeleton;
                if (view == null || skeleton == null) { Emit("[x2 project] no lobby character"); break; }
                foreach (var name in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var index = skeleton.FindBone(name.Replace('_', ' '));
                    if (index < 0) { Emit($"[x2 project] no bone {name}"); continue; }
                    var world = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(index);
                    var pixel = view.Camera.UnprojectPosition(world.Origin);
                    Emit($"[x2 project] {name} {pixel.X:F1} {pixel.Y:F1} (world {world.Origin})");
                }
                Emit($"[x2 project] camera {view.Camera.GlobalTransform.Origin} fov {view.Camera.Fov} viewport {view.Viewport.Size} pivot {view.Pivot.GlobalTransform.Origin}");
                break;
            }
            case "tree":
            {
                // tree <node name> <depth>: logs the 3D node tree below the first node with that name (type, visibility, mesh/material summary)
                var treeParts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var treeRoot = GetTree().Root.FindChild(treeParts.Length > 0 ? treeParts[0] : "Login2Stage", true, false);
                var treeDepth = treeParts.Length > 1 && int.TryParse(treeParts[1], out var td) ? td : 2;
                void Walk(Node n, int depth)
                {
                    var text = $"{new string(' ', depth * 2)}{n.GetType().Name} '{n.Name}'";
                    if (n is Node3D n3) text += $" vis={n3.Visible} pos={n3.GlobalPosition}";
                    if (n is MeshInstance3D mi)
                    {
                        text += $" aabb={mi.GetAabb().Size}";
                        var m = mi.GetActiveMaterial(0);
                        text += m switch
                        {
                            BaseMaterial3D bm => $" mat=Std albedo={bm.AlbedoColor} tex={bm.AlbedoTexture != null} vcol={bm.VertexColorUseAsAlbedo} shade={bm.ShadingMode} tr={bm.Transparency}",
                            ShaderMaterial sm => $" mat=Shader",
                            null => " mat=null",
                            _ => $" mat={m.GetType().Name}",
                        };
                    }
                    if (n is GpuParticles3D gp)
                        text += $" amount={gp.Amount} life={gp.Lifetime} emitting={gp.Emitting} local={gp.LocalCoords} passes={gp.DrawPasses} " +
                                $"mesh={gp.DrawPass1?.GetType().Name} aabb={gp.DrawPass1?.GetAabb().Size} trail={gp.TrailEnabled} scale={gp.GlobalTransform.Basis.Scale}";
                    Emit($"[x2 tree] {text}");
                    if (depth < treeDepth) foreach (var c in n.GetChildren()) Walk(c, depth + 1);
                }
                if (treeRoot != null) Walk(treeRoot, 0); else Emit("[x2 tree] node not found");
                break;
            }
            default:
                Emit($"[x2 scenario] unknown step: {line}");
                break;
        }
    }

    private static void PressChord(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var key = ParseKey(parts[^1]);
        var shift = parts.Any(p => p.Equals("shift", StringComparison.OrdinalIgnoreCase));
        var ctrl = parts.Any(p => p.Equals("ctrl", StringComparison.OrdinalIgnoreCase));
        var alt = parts.Any(p => p.Equals("alt", StringComparison.OrdinalIgnoreCase));
        foreach (var pressed in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = key, PhysicalKeycode = key, Pressed = pressed, ShiftPressed = shift, CtrlPressed = ctrl, AltPressed = alt,
            });
    }

    private static Key ParseKey(string name)
    {
        if (name.Length == 1 && char.IsDigit(name[0])) return Key.Key0 + (name[0] - '0');
        if (name.Length == 1 && char.IsLetter(name[0])) return Key.A + (char.ToUpperInvariant(name[0]) - 'A');
        return Enum.TryParse<Key>(name, true, out var k) ? k : Key.None;
    }

    private static Key CharKey(char c) => char.IsLetterOrDigit(c) && c < 128 ? ParseKey(c.ToString()) : c == ' ' ? Key.Space : Key.None;
}
