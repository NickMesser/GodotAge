using Godot;

namespace AAEmu.GodotViewer;

using Audio;

/// <summary>
/// Scene-level game audio facade.  Construct it after <see cref="PakFiles.Open"/>
/// and configure it with the decrypted game-content database.
/// </summary>
public partial class AudioDirector : Node
{
    public static AudioDirector? Shared { get; private set; }
    private AudioStreamPlayer _ambientA = null!;
    private AudioStreamPlayer _ambientB = null!;
    private int _activeAmbient;
    private double _ambientElapsed;
    private double _ambientDuration;
    private bool _ambientFading;
    private AudioStreamPlayer? _ambientIncoming;
    private AudioStreamPlayer? _ambientOutgoing;
    private float _ambientFrom;

    public SoundLibrary? Library { get; private set; }
    public AudioContentDatabase? Content { get; private set; }
    public SoundPlayer? Player { get; private set; }
    public float ZoneCrossfadeSeconds { get; set; } = 2f;

    /// <summary>Loads content mappings from a SQLite database opened with Mode=ReadOnly.</summary>
    public void Configure(string databasePath, Func<string, byte[]?> readPak, VorbisSetupHeaderRegistry? setupHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(readPak);
        if (IsInsideTree())
            throw new InvalidOperationException("Configure AudioDirector before adding it to the scene tree.");
        Library = new SoundLibrary(readPak, setupHeaders);
        Content = AudioContentDatabase.Load(databasePath, readPak);
    }

    public override void _Ready()
    {
        if (Library == null || Content == null)
            throw new InvalidOperationException("Call AudioDirector.Configure before AddChild.");
        SoundPlayer.EnsureBuses();
        Player = new SoundPlayer { Name = "SoundPlayer", Library = Library };
        AddChild(Player);
        _ambientA = CreateAmbientPlayer("AmbientA");
        _ambientB = CreateAmbientPlayer("AmbientB");
        AddChild(_ambientA);
        AddChild(_ambientB);
        SetProcess(false);
        Shared = this;
    }

    public override void _ExitTree()
    {
        if (Shared == this) Shared = null;
    }

    public override void _Process(double delta)
    {
        if (!_ambientFading) return;
        _ambientElapsed += delta;
        var amount = _ambientDuration <= 0 ? 1f : Mathf.Clamp((float)(_ambientElapsed / _ambientDuration), 0, 1);
        _ambientIncoming!.VolumeDb = GainToDb(amount);
        _ambientOutgoing!.VolumeDb = GainToDb(Mathf.Lerp(_ambientFrom, 0, amount));
        if (amount < 1) return;
        _ambientOutgoing.Stop();
        _ambientOutgoing.Stream = null;
        _ambientIncoming.VolumeDb = 0;
        _ambientFading = false;
        SetProcess(false);
    }

    /// <summary>Updates the default or sub-zone music and ambience. zoneKey is zones.zone_key.</summary>
    public bool OnZoneChanged(uint zoneKey, uint subZoneId)
    {
        EnsureReady();
        if (!Content!.TryResolveZone(zoneKey, subZoneId, out var zone))
        {
            Player!.StopMusic(ZoneCrossfadeSeconds);
            StopAmbience(ZoneCrossfadeSeconds);
            return false;
        }
        if (zone.Music != null) PlayAsMusic(zone.Music, ZoneCrossfadeSeconds);
        else Player!.StopMusic(ZoneCrossfadeSeconds);
        if (zone.Ambience != null) PlayAsAmbience(zone.Ambience, ZoneCrossfadeSeconds);
        else StopAmbience(ZoneCrossfadeSeconds);
        return zone.Music != null || zone.Ambience != null;
    }

    /// <summary>Convenience overload for callers which retain DB IDs as signed integers.</summary>
    public bool OnZoneChanged(int zoneKey, int subZoneId) =>
        zoneKey >= 0 && subZoneId >= 0 && OnZoneChanged((uint)zoneKey, (uint)subZoneId);

    /// <summary>Equivalent to X2Sound:PlayUISound(name).</summary>
    public AudioStreamPlayer? PlayUi(string name)
    {
        EnsureReady();
        return Content!.TryResolveUi(name, out var sound) ? Play2D(sound!, SoundPlayer.UiBus) : null;
    }

    /// <summary>Equivalent to X2Sound:PlayMusic(name), for named sound-pack music entries.</summary>
    public bool PlayMusic(string name)
    {
        EnsureReady();
        if (!Content!.TryResolveNamedSound(name, out var sound)) return false;
        return PlayAsMusic(sound!, ZoneCrossfadeSeconds);
    }

    public void StopMusic(float fadeSeconds = 1f)
    {
        EnsureReady();
        Player!.StopMusic(fadeSeconds);
    }

    /// <summary>Plays a DB/FEV event, spatialized when <paramref name="at"/> is supplied.</summary>
    public Node? PlayEvent(string eventPath, Node3D? at = null)
    {
        EnsureReady();
        return Content!.TryResolveEvent(eventPath, out var sound)
            ? at == null ? Play2D(sound!, SoundPlayer.EffectsBus) : Play3D(sound!, at)
            : null;
    }

    /// <summary>Plays the FxSound attached to an fx_items row at the supplied effect node.</summary>
    public AudioStreamPlayer3D? PlaySkillFx(uint fxItemId, Node3D at)
    {
        ArgumentNullException.ThrowIfNull(at);
        EnsureReady();
        return Content!.TryResolveSkillFx(fxItemId, out var sound) ? Play3D(sound!, at) : null;
    }

    public AudioStreamPlayer3D? PlaySkillFx(int fxItemId, Node3D at) =>
        fxItemId < 0 ? null : PlaySkillFx((uint)fxItemId, at);

    /// <summary>Plays the Sound attribute for a named x2_sounds.xml particle at its effect node.</summary>
    public AudioStreamPlayer3D? PlayParticleFx(string particleName, Node3D at)
    {
        ArgumentNullException.ThrowIfNull(at);
        EnsureReady();
        return Content!.TryResolveParticleFx(particleName, out var sound) ? Play3D(sound!, at) : null;
    }

    private AudioStreamPlayer? Play2D(AudioEventDefinition definition, string bus)
    {
        foreach (var candidate in definition.Candidates)
            if (Library!.TryLoad(candidate, out var stream))
                return bus == SoundPlayer.UiBus ? Player!.PlayUi(stream!) : Player!.PlayEffect(stream!);
        GD.PushWarning($"No playable FSB sample for '{definition.EventPath}'.");
        return null;
    }

    private AudioStreamPlayer3D? Play3D(AudioEventDefinition definition, Node3D at)
    {
        foreach (var candidate in definition.Candidates)
            if (Library!.TryLoad(candidate, out var stream))
                return Player!.PlayAt(at, stream!, Vector3.Zero);
        GD.PushWarning($"No playable FSB sample for '{definition.EventPath}'.");
        return null;
    }

    private bool PlayAsMusic(AudioEventDefinition definition, float seconds)
    {
        foreach (var candidate in definition.Candidates)
            if (Library!.TryLoad(candidate, out var stream))
            {
                Player!.CrossfadeMusic(stream!, seconds);
                return true;
            }
        GD.PushWarning($"No playable music sample for '{definition.EventPath}'.");
        return false;
    }

    private bool PlayAsAmbience(AudioEventDefinition definition, float seconds)
    {
        foreach (var candidate in definition.Candidates)
            if (Library!.TryLoad(candidate, out var stream))
            {
                var current = _activeAmbient == 0 ? _ambientA : _ambientB;
                if (current.Stream == stream && current.Playing) return true;
                _activeAmbient = 1 - _activeAmbient;
                var incoming = _activeAmbient == 0 ? _ambientA : _ambientB;
                incoming.Stream = stream;
                incoming.VolumeDb = -80;
                incoming.Play();
                BeginAmbientFade(incoming, current, seconds);
                return true;
            }
        GD.PushWarning($"No playable ambience sample for '{definition.EventPath}'.");
        return false;
    }

    private void StopAmbience(float seconds)
    {
        var current = _activeAmbient == 0 ? _ambientA : _ambientB;
        if (!current.Playing) return;
        var spare = _activeAmbient == 0 ? _ambientB : _ambientA;
        spare.Stop();
        spare.Stream = null;
        spare.VolumeDb = -80;
        BeginAmbientFade(spare, current, seconds);
    }

    private void BeginAmbientFade(AudioStreamPlayer incoming, AudioStreamPlayer outgoing, float seconds)
    {
        _ambientIncoming = incoming;
        _ambientOutgoing = outgoing;
        _ambientFrom = outgoing.Playing ? DbToGain(outgoing.VolumeDb) : 0;
        _ambientElapsed = 0;
        _ambientDuration = Math.Max(0, seconds);
        _ambientFading = true;
        SetProcess(true);
    }

    private void EnsureReady()
    {
        if (Library == null || Content == null || Player == null)
            throw new InvalidOperationException("Add a configured AudioDirector to the scene tree before playback.");
    }

    private static AudioStreamPlayer CreateAmbientPlayer(string name) => new()
    {
        Name = name,
        Bus = SoundPlayer.AmbientBus,
        VolumeDb = -80,
    };

    private static float GainToDb(float gain) => gain <= 0.0001f ? -80 : Mathf.LinearToDb(gain);
    private static float DbToGain(float db) => db <= -80 ? 0 : Mathf.DbToLinear(db);
}
