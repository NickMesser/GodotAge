using Godot;

namespace AAEmu.GodotViewer.Audio;

/// <summary>UI, positional, ambient, voice, and cross-faded zone-music playback.</summary>
public partial class SoundPlayer : Node
{
    public const string MasterBus = "Master";
    public const string MusicBus = "Music";
    public const string EffectsBus = "Effects";
    public const string AmbientBus = "Ambient";
    public const string VoiceBus = "Voice";
    public const string UiBus = "UI";

    private AudioStreamPlayer _musicA = null!;
    private AudioStreamPlayer _musicB = null!;
    private int _activeMusic;
    private bool _musicFading;
    private double _fadeElapsed;
    private double _fadeDuration;
    private AudioStreamPlayer? _fadeIncoming;
    private AudioStreamPlayer? _fadeOutgoing;
    private float _incomingStartGain;
    private float _incomingTargetGain;
    private float _outgoingStartGain;

    public SoundLibrary? Library { get; set; }

    public override void _Ready()
    {
        EnsureBuses();
        _musicA = CreateMusicPlayer("MusicA");
        _musicB = CreateMusicPlayer("MusicB");
        AddChild(_musicA);
        AddChild(_musicB);
        SetProcess(false);
    }

    public override void _Process(double delta)
    {
        if (!_musicFading) return;
        _fadeElapsed += delta;
        var amount = _fadeDuration <= 0 ? 1f : Mathf.Clamp((float)(_fadeElapsed / _fadeDuration), 0f, 1f);
        var incoming = _fadeIncoming!;
        var outgoing = _fadeOutgoing!;
        incoming.VolumeDb = GainToDb(Mathf.Lerp(_incomingStartGain, _incomingTargetGain, amount));
        outgoing.VolumeDb = GainToDb(Mathf.Lerp(_outgoingStartGain, 0, amount));
        if (amount < 1f) return;
        outgoing.Stop();
        outgoing.Stream = null;
        incoming.VolumeDb = 0;
        _musicFading = false;
        SetProcess(false);
    }

    public AudioStreamPlayer PlayUi(AudioStream stream, float volumeDb = 0, float pitchScale = 1) =>
        PlayOneShot2D(stream, UiBus, volumeDb, pitchScale);

    public AudioStreamPlayer PlayEffect(AudioStream stream, float volumeDb = 0, float pitchScale = 1) =>
        PlayOneShot2D(stream, EffectsBus, volumeDb, pitchScale);

    public AudioStreamPlayer PlayVoice(AudioStream stream, float volumeDb = 0, float pitchScale = 1) =>
        PlayOneShot2D(stream, VoiceBus, volumeDb, pitchScale);

    public AudioStreamPlayer3D PlayAt(Node3D parent, AudioStream stream, Vector3 localPosition,
        string bus = EffectsBus, float volumeDb = 0, float maxDistance = 100, float pitchScale = 1)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(stream);
        var player = new AudioStreamPlayer3D
        {
            Stream = stream,
            Position = localPosition,
            Bus = bus,
            VolumeDb = volumeDb,
            MaxDistance = maxDistance,
            PitchScale = pitchScale,
        };
        player.Finished += player.QueueFree;
        parent.AddChild(player);
        player.Play();
        return player;
    }

    public bool PlayZoneMusic(uint zoneId, float crossfadeSeconds = 2f)
    {
        if (Library == null)
            throw new InvalidOperationException("Assign SoundPlayer.Library before playing zone music.");
        if (!Library.TryLoadZoneMusic(zoneId, out var stream))
            return false;
        CrossfadeMusic(stream!, crossfadeSeconds);
        return true;
    }

    public void CrossfadeMusic(AudioStream stream, float seconds = 2f)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_musicA == null || _musicB == null)
            throw new InvalidOperationException("SoundPlayer must be inside the scene tree before playback.");
        var current = _activeMusic == 0 ? _musicA : _musicB;
        if (current.Stream == stream && current.Playing) return;
        _activeMusic = 1 - _activeMusic;
        var incoming = _activeMusic == 0 ? _musicA : _musicB;
        incoming.Stream = stream;
        incoming.VolumeDb = -80;
        incoming.Play();
        BeginFade(incoming, current, 1, seconds);
    }

    public void StopMusic(float fadeSeconds = 1f)
    {
        if (_musicA == null || _musicB == null)
            throw new InvalidOperationException("SoundPlayer must be inside the scene tree before playback.");
        var current = _activeMusic == 0 ? _musicA : _musicB;
        if (!current.Playing) return;
        // Fade toward a silent player, then the normal completion path stops the old stream.
        _activeMusic = 1 - _activeMusic;
        var silent = _activeMusic == 0 ? _musicA : _musicB;
        silent.Stop();
        silent.Stream = null;
        silent.VolumeDb = -80;
        BeginFade(silent, current, 0, fadeSeconds);
    }

    public static void EnsureBuses()
    {
        EnsureBus(MusicBus);
        EnsureBus(EffectsBus);
        EnsureBus(AmbientBus);
        EnsureBus(VoiceBus);
        EnsureBus(UiBus);
    }

    private AudioStreamPlayer PlayOneShot2D(AudioStream stream, string bus, float volumeDb, float pitchScale)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var player = new AudioStreamPlayer
        {
            Stream = stream,
            Bus = bus,
            VolumeDb = volumeDb,
            PitchScale = pitchScale,
        };
        player.Finished += player.QueueFree;
        AddChild(player);
        player.Play();
        return player;
    }

    private static void EnsureBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0) return;
        AudioServer.AddBus();
        AudioServer.SetBusName(AudioServer.BusCount - 1, name);
    }

    private static AudioStreamPlayer CreateMusicPlayer(string name) => new()
    {
        Name = name,
        Bus = MusicBus,
        VolumeDb = -80,
    };

    private static float GainToDb(float gain) => gain <= 0.0001f ? -80f : Mathf.LinearToDb(gain);

    private void BeginFade(AudioStreamPlayer incoming, AudioStreamPlayer outgoing,
        float incomingTargetGain, float seconds)
    {
        _fadeIncoming = incoming;
        _fadeOutgoing = outgoing;
        _incomingStartGain = DbToGain(incoming.VolumeDb);
        _incomingTargetGain = incomingTargetGain;
        _outgoingStartGain = outgoing.Playing ? DbToGain(outgoing.VolumeDb) : 0;
        _fadeElapsed = 0;
        _fadeDuration = Math.Max(0, seconds);
        _musicFading = true;
        SetProcess(true);
    }

    private static float DbToGain(float decibels) => decibels <= -80 ? 0 : Mathf.DbToLinear(decibels);
}
