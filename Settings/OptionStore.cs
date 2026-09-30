#nullable enable
using Godot;

namespace AAEmu.GodotViewer.Settings;

public enum OptionValueKind { Boolean, Integer, Float, String }

public sealed record OptionDefinition(string Id, OptionValueKind Kind, object DefaultValue,
    double? Minimum = null, double? Maximum = null, string Evidence = "viewer mapping");

public sealed class OptionChangedEventArgs(string id, object oldValue, object newValue) : EventArgs
{
    public string Id { get; } = id;
    public object OldValue { get; } = oldValue;
    public object NewValue { get; } = newValue;
}

/// <summary>Typed option values keyed by the original X2Option/CryEngine option identifiers.</summary>
public sealed class OptionStore
{
    public const string DefaultPath = "user://options.cfg";
    private readonly Dictionary<string, OptionDefinition> _definitions;
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<OptionChangedEventArgs>? Changed;
    public string Path { get; }
    public IReadOnlyDictionary<string, OptionDefinition> Definitions => _definitions;

    public OptionStore(string path = DefaultPath, IEnumerable<OptionDefinition>? definitions = null)
    {
        Path = path;
        _definitions = (definitions ?? Defaults).ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        ResetToDefaults(notify: false);
    }

    public T Get<T>(string originalOptionId)
    {
        if (!_values.TryGetValue(originalOptionId, out var value))
            throw new KeyNotFoundException($"Unknown original option id '{originalOptionId}'.");
        if (value is T typed) return typed;
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool TryGet<T>(string originalOptionId, out T value)
    {
        try { value = Get<T>(originalOptionId); return true; }
        catch (Exception) { value = default!; return false; }
    }

    public void Set<T>(string originalOptionId, T value)
    {
        if (!_definitions.TryGetValue(originalOptionId, out var definition))
            throw new KeyNotFoundException($"Unknown original option id '{originalOptionId}'.");
        var normalized = Normalize(definition, value!);
        var old = _values[definition.Id];
        if (Equals(old, normalized)) return;
        _values[definition.Id] = normalized;
        Changed?.Invoke(this, new OptionChangedEventArgs(definition.Id, old, normalized));
    }

    public void ResetToDefaults(bool notify = true)
    {
        foreach (var definition in _definitions.Values)
        {
            if (notify) Set(definition.Id, definition.DefaultValue);
            else _values[definition.Id] = definition.DefaultValue;
        }
    }

    public Error Load()
    {
        ResetToDefaults(notify: false);
        var config = new ConfigFile();
        var error = config.Load(Path);
        if (error == Error.FileNotFound) return Error.Ok;
        if (error != Error.Ok) return error;
        foreach (var definition in _definitions.Values)
        {
            if (!config.HasSectionKey("options", definition.Id)) continue;
            try { Set(definition.Id, FromVariant(config.GetValue("options", definition.Id), definition.Kind)); }
            catch (Exception exception) { GD.PushWarning($"Ignoring invalid option {definition.Id}: {exception.Message}"); }
        }
        return Error.Ok;
    }

    public Error Save()
    {
        var config = new ConfigFile();
        if (config.Load(Path) is not (Error.Ok or Error.FileNotFound)) config = new ConfigFile();
        foreach (var definition in _definitions.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
            config.SetValue("options", definition.Id, ToVariant(_values[definition.Id]));
        return config.Save(Path);
    }

    private static object Normalize(OptionDefinition d, object value)
    {
        object converted = d.Kind switch
        {
            OptionValueKind.Boolean => Convert.ToBoolean(value),
            OptionValueKind.Integer => Convert.ToInt32(value),
            OptionValueKind.Float => Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture),
            OptionValueKind.String => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            _ => throw new ArgumentOutOfRangeException()
        };
        if (converted is int i) return (int)Math.Clamp(i, d.Minimum ?? i, d.Maximum ?? i);
        if (converted is float f) return (float)Math.Clamp(f, d.Minimum ?? f, d.Maximum ?? f);
        return converted;
    }

    private static object FromVariant(Variant v, OptionValueKind kind) => kind switch
    {
        OptionValueKind.Boolean => v.AsBool(), OptionValueKind.Integer => v.AsInt32(),
        OptionValueKind.Float => v.AsSingle(), OptionValueKind.String => v.AsString(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Variant ToVariant(object value) => value switch
    {
        bool b => Variant.From(b), int i => Variant.From(i), float f => Variant.From(f),
        string s => Variant.From(s), _ => throw new ArgumentException("Unsupported option value")
    };

    public static IReadOnlyList<OptionDefinition> Defaults { get; } =
    [
        new("OIT_R_DESIREWIDTH", OptionValueKind.Integer, 1600, 640, 16384),
        new("OIT_R_DESIREHEIGHT", OptionValueKind.Integer, 900, 480, 16384),
        new("OIT_R_FULLSCREEN", OptionValueKind.Integer, 0, 0, 2),
        new("OIT_R_VSYNC", OptionValueKind.Boolean, true),
        new("OIT_SYS_USE_LIMIT_FPS", OptionValueKind.Boolean, false),
        new("OIT_SYS_MAX_FPS", OptionValueKind.Integer, 120, 30, 150, "screen_option.lua clamps to 30..150"),
        // The original API spells "GRAHIC" without the second p. Values 1..4 select
        // a preset; 5 denotes the user-defined combination of the detailed selectors.
        new("OIT_MASTERGRAHICQUALITY", OptionValueKind.Integer, 4, 1, 5, "option reference: default quality 4; 5=user-defined"),
        new("VIEWER_RENDER_SCALE", OptionValueKind.Float, 1.0f, 0.5, 2.0, "Godot-only render scale; original client has no corresponding slider"),
        new("OIT_OPTION_ANTI_ALIASING", OptionValueKind.Integer, 1, 1, 13, "option_anti_aliasing.cfg default=1"),
        new("OIT_OPTION_USE_SHADOW", OptionValueKind.Boolean, true),
        new("OIT_OPTION_SHADOW_DIST", OptionValueKind.Integer, 4, 1, 4, "option_shadow_dist.cfg default=4"),
        new("OIT_OPTION_VIEW_DISTANCE", OptionValueKind.Integer, 4, 1, 4, "option_view_distance.cfg default=4"),
        new("VIEWER_STREAM_NEAR_RADIUS", OptionValueKind.Integer, 2, 1, 8, "Godot streamer control; original camera distance maps to metres separately"),
        new("VIEWER_STREAM_FAR_RADIUS", OptionValueKind.Integer, 5, 1, 16, "Godot streamer control; original camera distance maps to metres separately"),
        new("OIT_OPTION_TEXTURE_BG", OptionValueKind.Integer, 4, 1, 4, "option_texture_bg.cfg default=4"),
        new("OIT_OPTION_TEXTURE_CHARACTER", OptionValueKind.Integer, 4, 1, 4, "option_texture_character.cfg default=4"),
        new("OIT_OPTION_TERRAIN_DETAIL", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_TERRAIN_LOD", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_VIEW_DIST_RATIO", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_VIEW_DIST_RATIO_VEGETATION", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_CHARACTER_LOD", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_ANIMATION", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_SHADOW_VIEW_DIST_RATIO", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_SHADOW_VIEW_DIST_RATIO_CHARACTER", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_USE_CLOUD", OptionValueKind.Boolean, true),
        new("OIT_E_ZONEWEATHEREFFECT", OptionValueKind.Boolean, true),
        new("OIT_OPTION_SHADER_QUALITY", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_VOLUMETRIC_EFFECT", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_WEAPON_EFFECT", OptionValueKind.Boolean, true, Evidence: "option_weapon_effect.cfg default=1, [0] disables"),
        new("OIT_OPTION_EFFECT", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_USE_WATER_REFLECTION", OptionValueKind.Boolean, true),
        new("OIT_OPTION_WATER", OptionValueKind.Integer, 4, 1, 4),
        new("OIT_OPTION_USE_DOF", OptionValueKind.Boolean, true),
        new("OIT_OPTION_USE_HDR", OptionValueKind.Boolean, true),
        new("VIEWER_RENDER_SSAO", OptionValueKind.Boolean, false, Evidence: "Godot-only toggle; option_shader_quality.cfg default sets r_SSAO=0"),
        new("VIEWER_RENDER_FOG", OptionValueKind.Boolean, true, Evidence: "Godot-only toggle; enabled as a default; original fog follows view-distance cvars"),
        new("OIT_S_GAMEMASTERVOLUME", OptionValueKind.Float, 1.0f, 0, 1),
        new("OIT_S_MUSICVOLUME", OptionValueKind.Float, 0.8f, 0, 1),
        new("OIT_S_SFXVOLUME", OptionValueKind.Float, 1.0f, 0, 1),
        new("OIT_S_MIDIVOLUME", OptionValueKind.Float, 1.0f, 0, 1, "user music slider, used as Ambient bus source"),
        new("OIT_S_CINEMAVOLUME", OptionValueKind.Float, 1.0f, 0, 1),
        new("OIT_S_VEHCLEMUSICVOLUME", OptionValueKind.Float, 1.0f, 0, 1),
        new("VIEWER_AUDIO_UI_VOLUME", OptionValueKind.Float, 1.0f, 0, 1, "Godot-only bus level; no distinct original UI-volume option found"),
        new("VIEWER_AUDIO_MUTE", OptionValueKind.Boolean, false, Evidence: "Godot-only master mute toggle"),
    ];
}
