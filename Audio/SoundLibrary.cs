using Godot;
using AAEmu.GodotViewer;

namespace AAEmu.GodotViewer.Audio;

public sealed record SoundEventReference(string BankPath, string? SampleName = null, int SampleIndex = 0);

/// <summary>
/// Resolves named game events to samples, reads banks through the supplied pak callback, and caches
/// both parsed banks and Godot streams. Register event paths from FEV/DB research at application startup.
/// Construct and call this class on Godot's main thread because AudioStream resources are Godot objects.
/// </summary>
public sealed class SoundLibrary
{
    private readonly Func<string, byte[]?> _readPakFile;
    private readonly VorbisSetupHeaderRegistry _setupHeaders;
    private readonly Dictionary<string, SoundEventReference> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, SoundEventReference> _zoneMusic = [];
    private readonly Dictionary<string, Fsb5Decoder> _banks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Bank, int Index), AudioStream> _streams = [];

    public SoundLibrary(Func<string, byte[]?> readPakFile, VorbisSetupHeaderRegistry? setupHeaders = null)
    {
        _readPakFile = readPakFile ?? throw new ArgumentNullException(nameof(readPakFile));
        _setupHeaders = setupHeaders ?? new VorbisSetupHeaderRegistry();
    }

    public VorbisSetupHeaderRegistry SetupHeaders => _setupHeaders;

    public void RegisterEvent(string eventName, SoundEventReference sound) =>
        _events[NormalizeEvent(eventName)] = sound ?? throw new ArgumentNullException(nameof(sound));

    public void RegisterZoneMusic(uint zoneId, SoundEventReference music) =>
        _zoneMusic[zoneId] = music ?? throw new ArgumentNullException(nameof(music));

    public bool TryGetEventReference(string eventName, out SoundEventReference? sound) =>
        _events.TryGetValue(NormalizeEvent(eventName), out sound);

    public SoundEventReference GetEventReference(string eventName) =>
        TryGetEventReference(eventName, out var sound)
            ? sound!
            : throw new KeyNotFoundException($"Sound event '{eventName}' has no bank/sample mapping.");

    public bool TryGetZoneMusicReference(uint zoneId, out SoundEventReference? music) =>
        _zoneMusic.TryGetValue(zoneId, out music);

    public bool TryLoadEvent(string eventName, out AudioStream? stream)
    {
        stream = null;
        return _events.TryGetValue(NormalizeEvent(eventName), out var sound) && TryLoad(sound, out stream);
    }

    public AudioStream LoadEvent(string eventName) => Load(GetEventReference(eventName));

    public AudioStream Load(string bankPath, string sampleName) =>
        Load(new SoundEventReference(bankPath, sampleName));

    public AudioStream Load(string bankPath, int sampleIndex = 0) =>
        Load(new SoundEventReference(bankPath, null, sampleIndex));

    public bool TryLoadZoneMusic(uint zoneId, out AudioStream? stream)
    {
        stream = null;
        return _zoneMusic.TryGetValue(zoneId, out var music) && TryLoad(music, out stream);
    }

    public AudioStream Load(SoundEventReference sound)
    {
        var bankPath = NormalizePakPath(sound.BankPath);
        var bank = GetBank(bankPath);
        var sampleIndex = sound.SampleName is { Length: > 0 } name ? bank.FindSample(name) : sound.SampleIndex;
        if (sampleIndex < 0)
            throw new KeyNotFoundException($"Sample '{sound.SampleName}' is absent from '{bankPath}'.");
        if (_streams.TryGetValue((bankPath, sampleIndex), out var cached))
            return cached;

        AudioStream stream;
        if (bank.Codec is >= Fsb5Codec.Pcm8 and <= Fsb5Codec.PcmFloat)
        {
            var decoded = bank.DecodePcm16(sampleIndex);
            var sample = bank.Samples[sampleIndex];
            if (decoded.Channels is not (1 or 2))
                throw new NotSupportedException(
                    $"The AudioStreamWav adapter supports mono/stereo; sample has {decoded.Channels} channels.");
            var wav = new AudioStreamWav
            {
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = decoded.SampleRate,
                Stereo = decoded.Channels == 2,
                Data = decoded.Data,
            };
            if (sample.LoopStart is { } loopStart && sample.LoopEnd is { } loopEnd)
            {
                wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                wav.LoopBegin = checked((int)loopStart);
                wav.LoopEnd = checked((int)loopEnd);
            }
            stream = wav;
        }
        else if (bank.Codec == Fsb5Codec.Vorbis)
        {
            var sample = bank.Samples[sampleIndex];
            var ogg = bank.RebuildOggVorbis(sampleIndex, _setupHeaders);
            var vorbis = AudioStreamOggVorbis.LoadFromBuffer(ogg)
                ?? throw new InvalidDataException($"Godot rejected reconstructed Vorbis sample {sampleIndex} in '{bankPath}'.");
            if (sample.LoopStart is { } loopStart)
            {
                if (loopStart < 0 || loopStart > sample.SampleCount || sample.SampleRate <= 0)
                    throw new InvalidDataException($"Sample {sampleIndex} in '{bankPath}' has an invalid loop start.");
                vorbis.Loop = true;
                vorbis.LoopOffset = loopStart / (double)sample.SampleRate;
            }
            stream = vorbis;
        }
        else if (bank.Codec == Fsb5Codec.Mpeg)
        {
            // FSB5 codec 11 can contain Layer II or Layer III. Official Godot builds
            // compile minimp3 for Layer III only, so decode Layer II to PCM here.
            var mpeg = bank.RebuildMp3(sampleIndex);
            var layer = bank.GetMpegLayer(sampleIndex);
            if (layer == 2)
            {
                var sample = bank.Samples[sampleIndex];
                var decoded = Mp2Decoder.Decode(mpeg);
                if (decoded.SampleRate != sample.SampleRate || decoded.Channels != sample.Channels)
                    throw new InvalidDataException(
                        $"Decoded MPEG format {decoded.Channels}ch/{decoded.SampleRate}Hz does not match " +
                        $"FSB sample {sampleIndex} ({sample.Channels}ch/{sample.SampleRate}Hz) in '{bankPath}'.");
                if (sample.SampleCount <= 0 || sample.SampleCount > decoded.SamplesPerChannel)
                    throw new InvalidDataException(
                        $"Decoded MPEG sample {sampleIndex} has {decoded.SamplesPerChannel} frames, " +
                        $"but FSB declares {sample.SampleCount} in '{bankPath}'.");
                if (decoded.Channels is not (1 or 2))
                    throw new NotSupportedException(
                        $"AudioStreamWav supports mono/stereo; MPEG sample has {decoded.Channels} channels.");

                var pcmByteCount = checked((int)sample.SampleCount * decoded.Channels * sizeof(short));
                var pcm = pcmByteCount == decoded.Pcm16.Length
                    ? decoded.Pcm16
                    : decoded.Pcm16.AsSpan(0, pcmByteCount).ToArray();
                var wav = new AudioStreamWav
                {
                    Format = AudioStreamWav.FormatEnum.Format16Bits,
                    MixRate = decoded.SampleRate,
                    Stereo = decoded.Channels == 2,
                    Data = pcm,
                };
                if (sample.LoopStart is { } loopStart && sample.LoopEnd is { } loopEnd)
                {
                    if (loopStart < 0 || loopEnd < loopStart || loopEnd > sample.SampleCount)
                        throw new InvalidDataException($"Sample {sampleIndex} in '{bankPath}' has invalid loop points.");
                    wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                    wav.LoopBegin = checked((int)loopStart);
                    wav.LoopEnd = checked((int)loopEnd);
                }
                stream = wav;
            }
            else if (layer == 3)
            {
                stream = AudioStreamMP3.LoadFromBuffer(mpeg)
                    ?? throw new InvalidDataException($"Godot rejected reconstructed MPEG Layer III sample {sampleIndex} in '{bankPath}'.");
            }
            else
            {
                throw new NotSupportedException(
                    $"FSB5 MPEG sample {sampleIndex} is Layer {layer}; only Layer II and Layer III are supported.");
            }
        }
        else
        {
            throw new NotSupportedException($"FSB5 codec {bank.Codec} is not supported by SoundLibrary.");
        }

        _streams[(bankPath, sampleIndex)] = stream;
        return stream;
    }

    public bool TryLoad(SoundEventReference sound, out AudioStream? stream)
    {
        try
        {
            stream = Load(sound);
            return true;
        }
        catch (Exception error)
        {
            GD.PushWarning($"Could not load sound '{sound.BankPath}': {error.Message}");
            stream = null;
            return false;
        }
    }

    public void ClearStreamCache() => _streams.Clear();

    public void ClearAllCaches()
    {
        _streams.Clear();
        _banks.Clear();
    }

    private Fsb5Decoder GetBank(string path)
    {
        if (_banks.TryGetValue(path, out var bank))
            return bank;
        var bytes = _readPakFile(path) ?? throw new FileNotFoundException("Sound bank is absent from game_pak.", path);
        bank = Fsb5Decoder.Parse(bytes);
        _banks[path] = bank;
        return bank;
    }

    private static string NormalizePakPath(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    private static string NormalizeEvent(string name) => name.Replace('\\', '/').Trim().TrimStart('/');
}
