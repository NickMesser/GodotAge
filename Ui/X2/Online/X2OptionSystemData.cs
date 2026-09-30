#nullable enable
using System.Globalization;
using System.Text.RegularExpressions;
using AAEmu.GodotViewer.Settings;
using AAEmu.GodotViewer.Ui.X2.Scripting;
using AAEmu.GodotViewer.Ui.X2.Scripting.Api;

namespace AAEmu.GodotViewer.Ui.X2.Online;

/// <summary>
/// X2Option for the in-game UI backed by the viewer's <see cref="OptionStore"/>: the scripts address options by
/// numeric OIT_* ids, the store by their names. Options the store does not define live in memory, starting from the
/// client defaults the HUD depends on (e.g. which action bars are shown).
/// </summary>
public sealed class X2OptionSystemData : NullSystemData
{
    public Func<X2CursorState>? PickedCursorProvider { get; set; }
    public Action? ClearPickedCursorAction { get; set; }
    public override X2CursorState Cursor => PickedCursorProvider?.Invoke() ?? base.Cursor;
    public override void ClearCursor()
    {
        ClearPickedCursorAction?.Invoke();
        base.ClearCursor();
    }
    private static readonly Lazy<Dictionary<int, string>> OptionNames = new(() =>
    {
        var names = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(X2ApiData.Prelude, @"^(OIT_\w+) = (\d+)", RegexOptions.Multiline))
            names.TryAdd(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[1].Value);
        return names;
    });

    /// <summary>Client defaults for options the HUD scripts read at start (values the native engine supplies).</summary>
    private static readonly Dictionary<string, double> HudDefaults = new()
    {
        ["OIT_SHOWACTIONBAR_1"] = 1, ["OIT_SHOWACTIONBAR_2"] = 1, ["OIT_SHOWACTIONBAR_3"] = 0,
        ["OIT_SHOWACTIONBAR_4"] = 0, ["OIT_SHOWACTIONBAR_5"] = 0, ["OIT_SHOWACTIONBAR_6"] = 0,
    };

    private readonly OptionStore? _store;
    private readonly KeyBindings? _bindings;
    private readonly Dictionary<int, object?> _local = [];

    // The sound-option scripts use the Cry cvar names while the numeric API uses
    // OIT_S_* constants.  Keep this small and explicit: an unknown string still
    // belongs to NullSystemData's script-local option store.
    private static readonly IReadOnlyDictionary<string, string> StoreAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["s_MasterVolume"] = "OIT_S_GAMEMASTERVOLUME",
            ["s_MusicVolume"] = "OIT_S_MUSICVOLUME",
            ["s_SFXVolume"] = "OIT_S_SFXVOLUME",
            ["s_MidiVolume"] = "OIT_S_MIDIVOLUME",
            ["s_CinemaVolume"] = "OIT_S_CINEMAVOLUME",
            ["s_VehicleMusicVolume"] = "OIT_S_VEHCLEMUSICVOLUME",
        };

    public X2OptionSystemData(OptionStore? store = null, KeyBindings? bindings = null)
    {
        _store = store;
        _bindings = bindings;
    }

    /// <summary>X2Hotkey bindings: changes made in the UI first, then the key bindings (client defaults).</summary>
    public override string? GetHotkey(X2HotkeyKind kind, string action, int index, int argument = 0, bool option = false, bool temporary = false)
        => base.GetHotkey(kind, action, index, argument, option, temporary)
           ?? (_bindings == null ? null : X2KeyBindingText.Get(_bindings, action, argument > 0 ? argument.ToString() : null, index));

    // ------------------------------------------------------------------ sound (X2Sound) through the game's AudioDirector

    private readonly Dictionary<long, Godot.AudioStreamPlayer> _sounds = [];
    private long _nextSound;

    /// <summary>The shared AudioDirector once the viewer created it (read by reflection until AudioDirector.Shared exists everywhere).</summary>
    private static AudioDirector? Audio =>
        typeof(AudioDirector).GetProperty("Shared", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as AudioDirector;

    public override long PlayUiSound(string name, bool duplicable)
    {
        var id = ++_nextSound;
        try
        {
            if (Audio?.PlayUi(name) is { } player) _sounds[id] = player;
        }
        catch (Exception) { /* a missing or undecodable sound must not break the UI */ }
        return id;
    }

    public override bool IsSoundPlaying(long soundId)
        => _sounds.TryGetValue(soundId, out var p) && Godot.GodotObject.IsInstanceValid(p) && p.Playing;

    public override void StopSound(long soundId, int mode)
    {
        if (_sounds.Remove(soundId, out var p) && Godot.GodotObject.IsInstanceValid(p)) p.Stop();
    }

    public override void PlayMusic(string name)
    {
        try { Audio?.PlayMusic(name); } catch (Exception) { }
    }

    public override void StopMusic()
    {
        try { Audio?.StopMusic(); } catch (Exception) { }
    }

    public override object? GetOption(int id)
    {
        if (OptionNames.Value.TryGetValue(id, out var name))
        {
            if (TryGetStoreValue(name, out var value)) return value;
        }
        if (_local.TryGetValue(id, out var local)) return local;
        if (OptionNames.Value.TryGetValue(id, out name) && HudDefaults.TryGetValue(name, out var d)) return d;
        return base.GetOption(id);
    }

    public override object? GetOption(string name)
        => TryGetStoreValue(name, out var value) ? value : base.GetOption(name);

    public override void SetOption(int id, object? value, bool markModified = true)
    {
        _local[id] = value;
        if (OptionNames.Value.TryGetValue(id, out var name)) SetStoreValue(name, value);
    }

    public override void SetOption(string name, object? value, bool markModified = true)
    {
        if (SetStoreValue(name, value)) return;
        base.SetOption(name, value, markModified);
    }

    public override void SaveOptions()
    {
        base.SaveOptions();
        _store?.Save();
    }

    private bool TryGetStoreValue(string name, out object? value)
    {
        var storeName = ResolveStoreName(name);
        if (storeName != null && _store!.TryGet<object>(storeName, out var stored))
        {
            value = stored switch { bool b => b ? 1d : 0d, int i => (double)i, float f => (double)f, _ => stored };
            return true;
        }
        value = null;
        return false;
    }

    private bool SetStoreValue(string name, object? value)
    {
        var storeName = ResolveStoreName(name);
        if (storeName == null || !_store!.Definitions.TryGetValue(storeName, out var def)) return false;
        try
        {
            var n = value is double dv ? dv : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            switch (def.Kind)
            {
                case OptionValueKind.Boolean: _store.Set(storeName, n != 0); break;
                case OptionValueKind.Integer: _store.Set(storeName, (int)n); break;
                case OptionValueKind.Float: _store.Set(storeName, (float)n); break;
                default: _store.Set(storeName, value?.ToString() ?? ""); break;
            }
            return true;
        }
        catch (Exception)
        {
            // Preserve native behaviour for an invalid value: the script can keep its own local value.
            return false;
        }
    }

    private string? ResolveStoreName(string name)
    {
        if (_store == null) return null;
        if (_store.Definitions.ContainsKey(name)) return name;
        return StoreAliases.TryGetValue(name, out var mapped) && _store.Definitions.ContainsKey(mapped) ? mapped : null;
    }
}
