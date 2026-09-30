using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using AAEmu.GodotViewer.Audio;
using NVector3 = System.Numerics.Vector3;

namespace AAEmu.GodotViewer;

/// <summary>Movement and streamed-world audio. All methods are called on the Godot main thread.</summary>
public partial class WorldSoundService : Node3D
{
    public static WorldSoundService? Shared { get; private set; }

    private static readonly IReadOnlyDictionary<string, string> SurfaceEffectNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mat_metal"] = "metal_thick", ["mat_metal_thick"] = "metal_thick",
            ["mat_wood"] = "wood_solid", ["mat_wood_solid"] = "wood_solid",
            ["mat_wood_hollow"] = "wood_hollow", ["mat_wood_breakable"] = "wood_hollow",
            ["mat_wood_breakable2"] = "wood_hollow", ["mat_wood_breakable_thin"] = "wood_hollow",
            ["mat_wood_breakable_thick"] = "wood_hollow", ["mat_wood_breakable_medium"] = "wood_hollow",
            ["mat_wood_breakable_large"] = "wood_hollow", ["mat_tree"] = "wood_hollow",
            ["mat_snow"] = "snow", ["mat_ice"] = "ice", ["mat_ice_breakable"] = "ice",
            ["mat_water"] = "water", ["mat_water_fake"] = "water_fake", ["mat_water_deep"] = "water",
            ["mat_mud"] = "mud", ["mat_concrete"] = "rock", ["mat_gravel"] = "gravel",
            ["mat_dirt_veg"] = "dirt", ["mat_dirt"] = "dirt", ["mat_sand_dry"] = "sand",
            ["mat_rock"] = "rock", ["mat_rock_landslide"] = "rock", ["mat_rock_dusty"] = "rock",
            ["mat_soil"] = "dirt", ["mat_grass"] = "grass", ["mat_grass_tall"] = "grass",
            ["mat_vegetation"] = "vegetation", ["mat_canopy"] = "vegetation", ["mat_leaves"] = "leaves",
            ["mat_fabric"] = "dirt", ["mat_magma"] = "magma",
        };

    public bool FootstepsEnabled { get; set; } = true;
    public bool WaterMovementEnabled { get; set; } = true;
    public bool JumpLandEnabled { get; set; } = true;
    public bool PositionalAmbientEnabled { get; set; } = true;
    public int MaxVoices { get; set; } = 32;
    public int MaxAmbientEmitters { get; set; } = 128;
    public float MaxAudibleDistance { get; set; } = 100f;

    // The client data, rather than a guessed terrain label, supplies each FMOD event path.
    public IDictionary<string, string> SurfaceFootstepEvents { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IDictionary<string, string> SurfaceWalkFootstepEvents { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? GenericFootstepEvent { get; set; }
    public string? WaterEntryEvent { get; set; }
    public string? SwimStrokeEvent { get; set; }
    public string? JumpEvent { get; set; }
    public string? LandEvent { get; set; }

    private AudioDirector? _director;
    private WorldStreamer? _world;
    private readonly List<AudioStreamPlayer3D> _voices = [];
    private readonly Dictionary<CharacterNode, MovementState> _movement = [];
    private readonly Dictionary<(int X, int Y), List<Emitter>> _cells = [];
    private readonly Dictionary<string, AudioStream?> _streams = new(StringComparer.OrdinalIgnoreCase);
    private Vector3 _listener;
    private double _ambientClock;
    private int _ambientCount;

    private sealed class MovementState
    {
        public float Distance;
        public bool Swimming;
        public bool Grounded = true;
    }

    private sealed class Emitter
    {
        public required string Event;
        public Vector3 Position;
        public float Radius;
        public float InnerRadius;
        public bool Loop;
        public bool Random;
        public float MinWait;
        public float MaxWait;
        public double NextPlay;
        public AudioStreamPlayer3D? Voice;
    }

    public override void _EnterTree() => Shared = this;

    public override void _ExitTree()
    {
        if (ReferenceEquals(Shared, this)) Shared = null;
        _cells.Clear();
        _movement.Clear();
        _voices.Clear();
        _streams.Clear();
    }

    internal void Configure(WorldStreamer world, AudioDirector director)
    {
        _world = world;
        _director = director;
        foreach (var (surface, effect) in SurfaceEffectNames)
        {
            SurfaceFootstepEvents[surface] = $"sounds/x2physics:mid_fs:{effect}_run";
            SurfaceWalkFootstepEvents[surface] = $"sounds/x2physics:mid_fs:{effect}_walk";
        }
        GenericFootstepEvent ??= "sounds/x2physics:mid_fs:dirt_run";
    }

    /// <summary>CharacterNode hook; use the streamed terrain surface and the current presentation pose.</summary>
    public void TickCharacter(CharacterNode character, double delta)
    {
        if (_world == null) return;
        var cry = _world.ToCry(character.GlobalPosition);
        var swimming = character.MovementPose is MovementPose.SwimIdle or MovementPose.SwimMove or MovementPose.SwimDive;
        var grounded = character.MovementPose is not (MovementPose.JumpRise or MovementPose.Fall or MovementPose.Glide);
        TickCharacter(character, delta, _world.SurfaceNameAt(cry.X, cry.Y), swimming, grounded);
    }

    /// <summary>Advance one visible character using its actual horizontal speed and movement pose.</summary>
    public void TickCharacter(CharacterNode character, double delta, string? surfaceName, bool swimming, bool grounded)
    {
        if (!_movement.TryGetValue(character, out var state))
            _movement[character] = state = new MovementState { Swimming = swimming, Grounded = grounded };
        if (_director == null || delta <= 0 || delta > 0.5 || !GodotObject.IsInstanceValid(character)) return;

        var position = character.GlobalPosition;
        if (WaterMovementEnabled && swimming && !state.Swimming)
            PlayEvent(WaterEntryEvent, position, 35f, SoundPlayer.EffectsBus);
        if (JumpLandEnabled && !grounded && state.Grounded)
            PlayEvent(JumpEvent, position, 35f, SoundPlayer.EffectsBus);
        if (JumpLandEnabled && grounded && !state.Grounded && !swimming)
        {
            var landEvent = surfaceName is "mat_grass" or "mat_grass_tall" or "mat_vegetation" or "mat_canopy" or "mat_leaves"
                ? "sounds/x2physics:small_landing:grass" : LandEvent;
            PlayEvent(landEvent, position, 35f, SoundPlayer.EffectsBus);
        }
        state.Swimming = swimming;
        state.Grounded = grounded;

        var moving = character.Speed > 0.2f;
        if (!moving || (!swimming && !grounded))
        {
            state.Distance = 0;
            return;
        }

        // One plant per stride. Distance keeps cadence stable through frame-rate and clip changes.
        var stride = swimming ? 1.4f : character.Speed > 3.5f ? 1.65f : 1.05f;
        state.Distance += character.Speed * (float)delta;
        if (state.Distance < stride) return;
        state.Distance %= stride;
        if (swimming)
        {
            if (WaterMovementEnabled) PlayEvent(SwimStrokeEvent, position, 30f, SoundPlayer.EffectsBus);
        }
        else if (FootstepsEnabled)
        {
            var walk = character.MovementPose == MovementPose.Walk;
            var surfaceEvents = walk ? SurfaceWalkFootstepEvents : SurfaceFootstepEvents;
            var path = surfaceName != null && surfaceEvents.TryGetValue(surfaceName, out var mapped)
                ? mapped : GenericFootstepEvent;
            PlayEvent(path, position, 35f, SoundPlayer.EffectsBus);
        }
    }

    public void ForgetCharacter(CharacterNode character) => _movement.Remove(character);

    /// <summary>Replace sound emitters for one streamed cell; cell coordinates are Cry world coordinates.</summary>
    public void SetCellEntities((int X, int Y) cell, CellEntities entities, NVector3 cellOrigin)
    {
        RemoveCell(cell);
        if (!PositionalAmbientEnabled) return;
        var capacity = Math.Max(0, MaxAmbientEmitters - _ambientCount);
        if (capacity == 0) return;
        var emitters = new List<Emitter>();
        foreach (var entity in entities.Entities)
        {
            if (entity.HiddenInGame) continue;
            var kind = entity.EntityClass.ToLowerInvariant();
            if (kind is not ("ambientvolume" or "soundspot" or "randomsoundvolume")) continue;
            var props = entity.Properties;
            if (kind == "soundspot" && !Flag(props, "bPlay", true)) continue;
            var name = Property(props, kind == "soundspot" ? "sndSource" : "soundName");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var radius = Number(props, kind == "randomsoundvolume" ? "DiscRadius" : "OuterRadius", 30f);
            if (radius <= 0) continue;
            var p = entity.WorldTransform.Translation + cellOrigin;
            var godot = _world?.ToGodot(p.X, p.Y, p.Z) ?? new Vector3(p.X, p.Z, -p.Y);
            emitters.Add(new Emitter
            {
                Event = name,
                Position = godot,
                Radius = radius,
                InnerRadius = Number(props, "InnerRadius", 0f),
                Loop = kind != "randomsoundvolume" && Flag(props, "bLoop", kind == "ambientvolume"),
                Random = kind == "randomsoundvolume",
                MinWait = Number(props, "MinWaitTime", 5f),
                MaxWait = Number(props, "MaxWaitTime", 15f),
                NextPlay = _ambientClock,
            });
            if (emitters.Count >= capacity) break;
        }
        if (emitters.Count > 0)
        {
            _cells[cell] = emitters;
            _ambientCount += emitters.Count;
        }
    }

    public void RemoveCell((int X, int Y) cell)
    {
        if (!_cells.Remove(cell, out var emitters)) return;
        _ambientCount -= emitters.Count;
        foreach (var emitter in emitters)
            if (emitter.Voice is { } voice) voice.Stop();
    }

    public void TickAmbient(Vector3 listenerPosition, double delta)
    {
        _listener = listenerPosition;
        _ambientClock += Math.Max(0, delta);
        if (!PositionalAmbientEnabled || _director == null) return;
        var active = 0;
        foreach (var emitters in _cells.Values)
        foreach (var emitter in emitters)
        {
            if (active++ >= MaxAmbientEmitters) return;
            var distance = listenerPosition.DistanceTo(emitter.Position);
            if (distance > Math.Min(MaxAudibleDistance, emitter.Radius))
            {
                if (emitter.Voice is { } old) old.Stop();
                emitter.Voice = null;
                continue;
            }
            var expectedStream = _streams.TryGetValue(emitter.Event, out var cachedStream) ? cachedStream : null;
            if (emitter.Voice is { Playing: true } activeVoice && expectedStream != null && activeVoice.Stream == expectedStream)
                continue;
            if (emitter.Loop && emitter.Voice is { Stream: not null } loopVoice && loopVoice.Stream == expectedStream)
            {
                loopVoice.Play();
                continue;
            }
            if (emitter.Voice != null)
            {
                emitter.Voice = null;
                if (emitter.Loop) emitter.NextPlay = Math.Min(emitter.NextPlay, _ambientClock);
            }
            if (_ambientClock < emitter.NextPlay) continue;
            emitter.Voice = PlayEvent(emitter.Event, emitter.Position, emitter.Radius, SoundPlayer.AmbientBus);
            if (emitter.Voice != null && emitter.InnerRadius > 0)
                emitter.Voice.UnitSize = emitter.InnerRadius;
            var wait = emitter.Random ? (float)GD.RandRange(emitter.MinWait, Math.Max(emitter.MinWait, emitter.MaxWait)) : 1f;
            emitter.NextPlay = emitter.Random ? _ambientClock + wait
                : emitter.Voice == null ? _ambientClock + 1
                : emitter.Loop ? _ambientClock : double.PositiveInfinity;
        }
    }

    private AudioStreamPlayer3D? PlayEvent(string? eventPath, Vector3 position, float radius, string bus)
    {
        if (_director?.Content == null || _director.Library == null || string.IsNullOrWhiteSpace(eventPath)) return null;
        if (position.DistanceTo(_listener) > Math.Min(MaxAudibleDistance, radius)) return null;
        if (!_streams.TryGetValue(eventPath, out var stream))
        {
            stream = null;
            if ((_director.Content.TryResolveEvent(eventPath, out var definition) ||
                 _director.Content.TryResolveNamedSound(eventPath, out definition)) && definition != null)
                foreach (var candidate in definition.Candidates)
                    if (_director.Library.TryLoad(candidate, out stream) && stream != null) break;
            _streams[eventPath] = stream;
        }
        if (stream == null) return null;
        var voice = _voices.FirstOrDefault(v => !v.Playing);
        if (voice == null)
        {
            if (_voices.Count >= MaxVoices) return null;
            voice = new AudioStreamPlayer3D();
            AddChild(voice);
            _voices.Add(voice);
        }
        voice.Stop();
        voice.Stream = stream;
        voice.Bus = bus;
        voice.GlobalPosition = position;
        voice.MaxDistance = radius;
        voice.VolumeDb = 0;
        voice.Play();
        return voice;
    }

    private static string? Property(Dictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value :
        properties.FirstOrDefault(p => p.Key.EndsWith('.' + name, StringComparison.OrdinalIgnoreCase)).Value;

    private static float Number(Dictionary<string, string> properties, string name, float fallback) =>
        float.TryParse(Property(properties, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    private static bool Flag(Dictionary<string, string> properties, string name, bool fallback) =>
        Property(properties, name) is { } value ? value is "1" or "true" or "True" : fallback;
}
