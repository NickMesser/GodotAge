#nullable enable
using System.Text;
#if !KEYBINDINGS_PURE
using Godot;
#endif

namespace AAEmu.GodotViewer.Settings;

public enum BindingDevice { Keyboard, MouseButton, MouseWheel, MouseAxis }
public enum BindingSlot { Primary, Secondary }

public sealed record BindingChord(
    string Key,
    bool Shift = false,
    bool Ctrl = false,
    bool Alt = false,
    bool Meta = false,
    BindingDevice Device = BindingDevice.Keyboard)
{
    public override string ToString()
    {
        var prefix = string.Concat(Ctrl ? "Ctrl+" : "", Alt ? "Alt+" : "", Shift ? "Shift+" : "", Meta ? "Meta+" : "");
        return prefix + Key;
    }
}

public sealed record BindingInfo(
    string Action,
    string? Argument,
    BindingChord? Primary,
    BindingChord? Secondary,
    string Label)
{
    public string InputAction => "x2_" + Action + (string.IsNullOrEmpty(Argument) ? "" : "_" + Sanitize(Argument));
    public string? PrimaryKey => Primary?.ToString();
    public string? SecondaryKey => Secondary?.ToString();
    public string EnglishLabel => Label;

    private static string Sanitize(string value) => new(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());
}

/// <summary>
/// Parses the original client's <c>default_binding.g</c> and adapts it to Godot's InputMap.
///
/// Grammar (indentation is four spaces per level): the top-level slot is <c>primary</c>,
/// <c>second</c>, or <c>deactivate</c>; below it is an input mode; below that is
/// <c>builtin</c> or <c>builtin_multi</c>. A builtin row is
/// <c>[MODIFIER-]*key action</c>, or <c>[MODIFIER-]*key ( action, argument )</c>.
/// A builtin_multi block names the action on the next indentation level and contains
/// <c>[MODIFIER-]*key argument</c> rows below it. Recognized modifiers are CTRL, ALT,
/// SHIFT and META. Keys include keyboard/OEM/numpad/F-keys, mouse4, wheelup/wheeldown,
/// and the continuous mousedx/mousedy axes. Mode names scope when the original client
/// activates a binding, but are intentionally not part of the viewer action name.
/// Deactivate rows are mode-scoped action names without keys. Since the viewer does
/// not model Cry input modes, those directives do not create global InputMap events.
/// Mouse axes remain visible in All but cannot be represented as discrete Godot events.
/// </summary>
public sealed class KeyBindings
{
    public const string DefaultPath = "user://keybindings.cfg";
    public const string LocalizedPakPath = "game/en_us/default_binding.g";
    public const string FallbackPakPath = "game/default_binding.g";

    private List<BindingInfo> _all;
    public IReadOnlyList<BindingInfo> All => _all;

    /// <summary>A checked-in fallback generated verbatim from the en_us file (SHA-256 15D6F404...AAC783).</summary>
    public static IReadOnlyList<BindingInfo> FallbackDefaults { get; } = ParseDefaults(FallbackSource);

#if !KEYBINDINGS_PURE
    public string Path { get; }
    public event Action<string>? BindingChanged;

    public KeyBindings(string path = DefaultPath)
    {
        Path = path;
        _all = FallbackDefaults.ToList();
    }
#else
    private KeyBindings() => _all = FallbackDefaults.ToList();
#endif

    /// <summary>Parses UTF-8 binding text without any Godot dependency.</summary>
    public static IReadOnlyList<BindingInfo> ParseDefaults(string text)
    {
        var rows = new List<MutableBinding>();
        var section = "";
        var kind = "";
        var multiAction = "";

        foreach (var original in text.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(original)) continue;
            var trimmed = original.Trim();
            if (trimmed.StartsWith('#')) continue;
            var spaces = original.TakeWhile(c => c == ' ').Count();
            var level = spaces / 4;

            if (level == 0) { section = trimmed; kind = multiAction = ""; continue; }
            if (level == 1) continue; // mode name
            if (level == 2) { kind = trimmed; multiAction = ""; continue; }
            if (section == "deactivate") continue;

            if (kind == "builtin" && level == 3)
            {
                var split = SplitOnce(trimmed);
                if (split is null) continue;
                var (action, argument) = ParseAction(split.Value.Tail);
                Add(rows, section, action, argument, ParseChord(split.Value.Head));
            }
            else if (kind == "builtin_multi" && level == 3)
            {
                multiAction = trimmed;
            }
            else if (kind == "builtin_multi" && level == 4 && multiAction.Length != 0)
            {
                var split = SplitOnce(trimmed);
                if (split is null) continue;
                Add(rows, section, multiAction, split.Value.Tail.Trim(), ParseChord(split.Value.Head));
            }
        }

        return rows.Select(r => new BindingInfo(r.Action, r.Argument, r.Primary, r.Secondary,
            EnglishLabel(r.Action, r.Argument))).ToArray();
    }

    public static IReadOnlyList<BindingInfo> ParseDefaults(byte[] utf8) => ParseDefaults(Encoding.UTF8.GetString(utf8));

#if !KEYBINDINGS_PURE
    /// <summary>Loads the localized pak file, falling back first to the base pak file and then a minimal built-in key set.</summary>
    public void LoadDefaults(bool eraseExisting = true)
    {
        var bytes = global::AAEmu.GodotViewer.PakFiles.Read(LocalizedPakPath)
                    ?? global::AAEmu.GodotViewer.PakFiles.Read(FallbackPakPath);
        _all = (bytes is null ? FallbackDefaults : ParseDefaults(bytes)).Select(binding =>
            binding.Action == "toggle_achievement"
                ? binding with { Primary = new BindingChord("y") }
                : binding).ToList();
        foreach (var binding in _all) Apply(binding, eraseExisting);
    }

    public Error Load()
    {
        LoadDefaults();
        var config = new ConfigFile();
        var error = config.Load(Path);
        if (error == Error.FileNotFound) return Error.Ok;
        if (error != Error.Ok || !config.HasSection("keybindings")) return error;

        for (var i = 0; i < _all.Count; i++)
        {
            var binding = _all[i];
            var primary = Decode(config.GetValue("keybindings", binding.InputAction + ".primary", Encode(binding.Primary)).AsString());
            var secondary = Decode(config.GetValue("keybindings", binding.InputAction + ".secondary", Encode(binding.Secondary)).AsString());
            _all[i] = binding with { Primary = primary, Secondary = secondary };
            Apply(_all[i], eraseExisting: true);
        }
        return Error.Ok;
    }

    public void Rebind(string action, string? argument, BindingSlot slot, InputEvent? inputEvent, bool notify = true)
        => RebindChord(action, argument, slot, inputEvent is null ? null : FromEvent(inputEvent), notify);

    /// <summary>
    /// Compatibility entry point for existing viewer callers. The identifier may be a
    /// generated x2 action name or an unambiguous source action name. Legacy HA_* ids
    /// are rejected so this overload can never recreate the obsolete InputMap actions.
    /// With eraseExisting=false, the event occupies the secondary slot.
    /// </summary>
    public void Rebind(string originalActionId, InputEvent inputEvent, bool eraseExisting = true, bool notify = true)
    {
        if (originalActionId.StartsWith("HA_", StringComparison.Ordinal))
            throw new ArgumentException($"Legacy action '{originalActionId}' has no default_binding.g identity.", nameof(originalActionId));

        var matches = originalActionId.StartsWith("x2_", StringComparison.Ordinal)
            ? _all.Select((binding, index) => (binding, index)).Where(x => x.binding.InputAction == originalActionId).ToArray()
            : _all.Select((binding, index) => (binding, index)).Where(x => x.binding.Action == originalActionId).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException(matches.Length == 0
                ? $"Unknown binding '{originalActionId}'."
                : $"Binding '{originalActionId}' needs an argument-specific x2 action name.", nameof(originalActionId));

        var (binding, index) = matches[0];
        var chord = FromEvent(inputEvent);
        _all[index] = eraseExisting
            ? binding with { Primary = chord, Secondary = null }
            : binding with { Secondary = chord };
        Apply(_all[index], eraseExisting: true);
        if (notify) BindingChanged?.Invoke(_all[index].InputAction);
    }

    public void RebindChord(string action, string? argument, BindingSlot slot, BindingChord? chord, bool notify = true)
    {
        var index = _all.FindIndex(b => b.Action == action && b.Argument == argument);
        if (index < 0) throw new ArgumentException($"Unknown binding: {action} {argument}");
        _all[index] = slot == BindingSlot.Primary
            ? _all[index] with { Primary = chord }
            : _all[index] with { Secondary = chord };
        Apply(_all[index], eraseExisting: true);
        if (notify) BindingChanged?.Invoke(_all[index].InputAction);
    }

    public Error Save()
    {
        var config = new ConfigFile();
        if (config.Load(Path) is not (Error.Ok or Error.FileNotFound)) config = new ConfigFile();
        foreach (var binding in _all)
        {
            config.SetValue("keybindings", binding.InputAction + ".primary", Encode(binding.Primary));
            config.SetValue("keybindings", binding.InputAction + ".secondary", Encode(binding.Secondary));
        }
        return config.Save(Path);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<InputEvent>> Snapshot() => _all.ToDictionary(
        b => b.InputAction,
        b => (IReadOnlyList<InputEvent>)InputMap.ActionGetEvents(b.InputAction).ToArray(),
        StringComparer.Ordinal);

    private static void Apply(BindingInfo binding, bool eraseExisting)
    {
        var name = binding.InputAction;
        if (!InputMap.HasAction(name)) InputMap.AddAction(name);
        if (eraseExisting) InputMap.ActionEraseEvents(name);
        foreach (var chord in new[] { binding.Primary, binding.Secondary })
            if (chord is not null && ToEvent(chord) is { } inputEvent) InputMap.ActionAddEvent(name, inputEvent);
    }

    private static InputEvent? ToEvent(BindingChord chord)
    {
        if (chord.Device == BindingDevice.MouseAxis) return null;
        if (chord.Device is BindingDevice.MouseButton or BindingDevice.MouseWheel)
        {
            var button = chord.Key.ToLowerInvariant() switch
            {
                "mouse1" => MouseButton.Left,
                "mouse2" => MouseButton.Right,
                "mouse3" => MouseButton.Middle,
                "mouse4" => MouseButton.Xbutton1,
                "mouse5" => MouseButton.Xbutton2,
                "wheelup" => MouseButton.WheelUp,
                "wheeldown" => MouseButton.WheelDown,
                _ => MouseButton.None
            };
            return button == MouseButton.None ? null : new InputEventMouseButton
            {
                ButtonIndex = button, ShiftPressed = chord.Shift, CtrlPressed = chord.Ctrl,
                AltPressed = chord.Alt, MetaPressed = chord.Meta
            };
        }

        if (!Enum.TryParse<Key>(GodotKeyName(chord.Key), true, out var key)) return null;
        return new InputEventKey
        {
            PhysicalKeycode = key, ShiftPressed = chord.Shift, CtrlPressed = chord.Ctrl,
            AltPressed = chord.Alt, MetaPressed = chord.Meta
        };
    }

    private static BindingChord FromEvent(InputEvent inputEvent) => inputEvent switch
    {
        InputEventKey key => new BindingChord(key.PhysicalKeycode.ToString(), key.ShiftPressed, key.CtrlPressed, key.AltPressed, key.MetaPressed),
        InputEventMouseButton mouse => new BindingChord(MouseKeyName(mouse.ButtonIndex), mouse.ShiftPressed, mouse.CtrlPressed,
            mouse.AltPressed, mouse.MetaPressed, mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown ? BindingDevice.MouseWheel : BindingDevice.MouseButton),
        _ => throw new ArgumentException($"Unsupported input event: {inputEvent.GetType().Name}")
    };

    private static string GodotKeyName(string key) => key.ToLowerInvariant() switch
    {
        "." => "Period", "/" => "Slash", ";" => "Semicolon", "minus" => "Minus", "equals" => "Equal",
        "," or "comma" => "Comma", "period" => "Period", "apostrophe" => "Apostrophe",
        "grave" or "quoteleft" => "Quoteleft", "backslash" => "Backslash",
        "lbracket" or "bracketleft" => "Bracketleft", "rbracket" or "bracketright" => "Bracketright",
        "pageup" => "Pageup", "pagedown" => "Pagedown", "numlock" => "Numlock",
        "numpadperiod" or "np_period" => "KpPeriod", "numpadadd" or "np_add" => "KpAdd",
        "numpadsubtract" or "np_subtract" => "KpSubtract", "numpadmultiply" or "np_multiply" => "KpMultiply",
        "numpaddivide" or "np_divide" => "KpDivide", "numpadenter" or "np_enter" => "KpEnter",
        var value when value.StartsWith("numpad") && value.Length == 7 && char.IsDigit(value[6]) => "Kp" + value[6],
        var value when value.StartsWith("np_") && value.Length == 4 && char.IsDigit(value[3]) => "Kp" + value[3],
        var value when value.Length == 1 && char.IsDigit(value[0]) => "Key" + value,
        var value => char.ToUpperInvariant(value[0]) + value[1..]
    };

    private static string MouseKeyName(MouseButton button) => button switch
    {
        MouseButton.Left => "mouse1", MouseButton.Right => "mouse2", MouseButton.Middle => "mouse3",
        MouseButton.Xbutton1 => "mouse4", MouseButton.Xbutton2 => "mouse5",
        MouseButton.WheelUp => "wheelup", MouseButton.WheelDown => "wheeldown", _ => button.ToString()
    };
#endif

    private static BindingChord ParseChord(string token)
    {
        var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var key = parts[^1].ToLowerInvariant();
        var device = key switch
        {
            "mousedx" or "mousedy" => BindingDevice.MouseAxis,
            "wheelup" or "wheeldown" => BindingDevice.MouseWheel,
            var k when k.StartsWith("mouse", StringComparison.Ordinal) => BindingDevice.MouseButton,
            _ => BindingDevice.Keyboard
        };
        return new BindingChord(key, parts.Any(IsShift), parts.Any(IsCtrl), parts.Any(IsAlt), parts.Any(IsMeta), device);
    }

    private static bool IsShift(string value) => value.Equals("SHIFT", StringComparison.OrdinalIgnoreCase);
    private static bool IsCtrl(string value) => value.Equals("CTRL", StringComparison.OrdinalIgnoreCase);
    private static bool IsAlt(string value) => value.Equals("ALT", StringComparison.OrdinalIgnoreCase);
    private static bool IsMeta(string value) => value.Equals("META", StringComparison.OrdinalIgnoreCase);

    private static (string Head, string Tail)? SplitOnce(string value)
    {
        var index = value.IndexOfAny([' ', '\t']);
        return index < 0 ? null : (value[..index], value[(index + 1)..].Trim());
    }

    private static (string Action, string? Argument) ParseAction(string expression)
    {
        if (!expression.StartsWith('(')) return (expression.Trim(), null);
        var body = expression.Trim().TrimStart('(').TrimEnd(')').Trim();
        var comma = body.IndexOf(',');
        return comma < 0 ? (body, null) : (body[..comma].Trim(), body[(comma + 1)..].Trim());
    }

    private static void Add(List<MutableBinding> rows, string section, string action, string? argument, BindingChord chord)
    {
        var row = rows.FirstOrDefault(r => r.Action == action && r.Argument == argument);
        if (row is null) { row = new MutableBinding(action, argument); rows.Add(row); }
        if (section == "primary") row.Primary = chord;
        else if (section == "second") row.Secondary = chord;
    }

    // text.lua obtains most option labels from KEY_BINDING_TEXT builtin_* entries. The
    // English client strings are represented here directly; engine/debug-only actions
    // absent from the option window use the same readable title convention.
    private static string EnglishLabel(string action, string? argument)
    {
        if (action == "team_target") return $"Target Team Member {argument}";
        if (action == "over_head_marker") return $"Overhead Marker {argument}";
        if (action == "action_bar_button") return $"Action Bar Slot {argument}";
        if (action == "mode_action_bar_button") return $"Mode Skill {argument}";
        if (action == "quest_directing_interaction") return $"Quest Interaction {argument}";
        return action switch
        {
            "moveforward" => "Move Forward", "moveback" => "Move Backward", "moveleft" => "Strafe Left",
            "moveright" => "Strafe Right", "turnleft" => "Turn Left", "turnright" => "Turn Right",
            "round_target" => "Target Self", "cycle_hostile_forward" => "Next Hostile Target",
            "cycle_hostile_backward" => "Previous Hostile Target", "cycle_friendly_forward" => "Next Friendly Target",
            "cycle_friendly_backward" => "Previous Friendly Target", "toggle_bag" => "Inventory",
            "toggle_spellbook" => "Skills", "toggle_character" => "Character Info", "toggle_quest" => "Quest Log",
            "toggle_worldmap" => "World Map", "open_chat" => "Open Chat", "slash_open_chat" => "Open Slash Chat",
            "open_config" => "Game Menu", "activate_weapon" => "Draw or Sheathe Weapon",
            "toggle_walk" => "Toggle Walk", "autorun" => "Auto Run", "jump" => "Jump", "down" => "Descend",
            "zoom_in" => "Zoom In", "zoom_out" => "Zoom Out", "front_camera" => "Front Camera",
            "back_camera" => "Back Camera", "left_camera" => "Left Camera", "right_camera" => "Right Camera",
            "cycle_camera_clockwise" => "Rotate Camera Clockwise", "cycle_camera_counter_clockwise" => "Rotate Camera Counterclockwise",
            "do_interaction_1" => "Interaction 1", "do_interaction_2" => "Interaction 2",
            "do_interaction_3" => "Interaction 3", "do_interaction_4" => "Interaction 4",
            "action_bar_page_prev" => "Previous Action Bar Page", "action_bar_page_next" => "Next Action Bar Page",
            _ => Humanize(action)
        };
    }

    private static string Humanize(string value) => string.Join(' ', value.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private sealed class MutableBinding(string action, string? argument)
    {
        public string Action { get; } = action;
        public string? Argument { get; } = argument;
        public BindingChord? Primary { get; set; }
        public BindingChord? Secondary { get; set; }
    }

#if !KEYBINDINGS_PURE
    private static string Encode(BindingChord? chord) => chord is null ? "" :
        $"{chord.Device}|{chord.Key}|{(chord.Shift ? 1 : 0)}|{(chord.Ctrl ? 1 : 0)}|{(chord.Alt ? 1 : 0)}|{(chord.Meta ? 1 : 0)}";

    private static BindingChord? Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        var fields = encoded.Split('|');
        return fields.Length == 6 && Enum.TryParse<BindingDevice>(fields[0], out var device)
            ? new BindingChord(fields[1], fields[2] == "1", fields[3] == "1", fields[4] == "1", fields[5] == "1", device)
            : null;
    }
#endif

    /// <summary>
    /// Core movement and targeting keys used until the pak's <c>default_binding.g</c> is loaded (or if a pak lacks it).
    /// The full default table always comes from the client's own pak.
    /// </summary>
    private const string FallbackSource = """
primary
    normal_mode
        builtin
            w moveforward
            s moveback
            q moveleft
            e moveright
            a turnleft
            d turnright
            space jump
            x down
            mousedx rotateyaw
            mousedy rotatepitch
            wheelup zoom_in
            wheeldown zoom_out
            enter open_chat
            escape open_config
            numlock autorun
            tab cycle_hostile_forward
            SHIFT-tab cycle_hostile_backward
            i toggle_bag
            k toggle_spellbook
            c toggle_character
            m toggle_worldmap
            f do_interaction_1
            action_bar_button
                1 1
                2 2
                3 3
                4 4
                5 5
                6 6
                7 7
                8 8
                9 9
                0 10
""";
}
