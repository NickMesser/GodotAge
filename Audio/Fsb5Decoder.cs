using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace AAEmu.GodotViewer.Audio;

/// <summary>Codecs used by an FSB5 bank. The numeric values are the FSB5 header values.</summary>
public enum Fsb5Codec : uint
{
    Pcm8 = 1,
    Pcm16 = 2,
    Pcm24 = 3,
    Pcm32 = 4,
    PcmFloat = 5,
    GcAdpcm = 6,
    ImaAdpcm = 7,
    Vag = 8,
    HeVag = 9,
    Xma = 10,
    Mpeg = 11,
    Celt = 12,
    Atrac9 = 13,
    Xwma = 14,
    Vorbis = 15,
}

public sealed record Fsb5Metadata(byte Type, byte[] Data);

public sealed record Fsb5Sample(
    int Index,
    string Name,
    int Channels,
    int SampleRate,
    long SampleCount,
    int DataOffset,
    int DataLength,
    uint? VorbisSetupCrc,
    long? LoopStart,
    long? LoopEnd,
    IReadOnlyList<Fsb5Metadata> Metadata);

/// <summary>Interleaved, signed, little-endian 16-bit PCM.</summary>
public sealed record Pcm16Audio(int Channels, int SampleRate, long FrameCount, byte[] Data)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)SampleRate);

    public byte[] ToWaveFile()
    {
        if (Data.LongLength > uint.MaxValue - 36)
            throw new InvalidOperationException("PCM data is too large for a RIFF/WAVE file.");

        var result = new byte[44 + Data.Length];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(36 + Data.Length));
        "WAVEfmt "u8.CopyTo(result.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22), checked((ushort)Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), checked((uint)SampleRate));
        var blockAlign = checked((ushort)(Channels * 2));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28), checked((uint)(SampleRate * blockAlign)));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(34), 16);
        "data"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), (uint)Data.Length);
        Data.CopyTo(result, 44);
        return result;
    }
}

/// <summary>
/// Strict, dependency-free FSB5 reader. PCM banks are converted to PCM16, MPEG banks are
/// reconstructed as MPEG audio streams, and Vorbis banks are reconstructed as Ogg through
/// <see cref="RebuildOggVorbis"/> once the setup packet identified by the bank's CRC is registered.
/// </summary>
public sealed class Fsb5Decoder
{
    private const int HeaderSize = 0x3c;
    private static readonly int[] Frequencies =
        [4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000];
    private static readonly int[] MpegSampleRates = [44100, 48000, 32000];

    private readonly byte[] _file;
    private readonly int _dataStart;
    private readonly int _dataLength;

    private Fsb5Decoder(byte[] file, Fsb5Codec codec, int dataStart, int dataLength, List<Fsb5Sample> samples)
    {
        _file = file;
        Codec = codec;
        _dataStart = dataStart;
        _dataLength = dataLength;
        Samples = new ReadOnlyCollection<Fsb5Sample>(samples);
    }

    public Fsb5Codec Codec { get; }
    public IReadOnlyList<Fsb5Sample> Samples { get; }

    public static Fsb5Decoder Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes[..4].SequenceEqual("FSB5"u8))
            throw new InvalidDataException("The input is not an FSB5 bank.");

        var version = ReadU32(bytes, 4);
        if (version != 1)
            throw new NotSupportedException($"FSB5 version {version} is not supported.");

        var sampleCount = checked((int)ReadU32(bytes, 8));
        var sampleHeadersSize = checked((int)ReadU32(bytes, 12));
        var nameTableSize = checked((int)ReadU32(bytes, 16));
        var dataSize = checked((int)ReadU32(bytes, 20));
        var codec = (Fsb5Codec)ReadU32(bytes, 24);
        if (sampleCount < 0 || sampleHeadersSize < sampleCount * 8)
            throw new InvalidDataException("The FSB5 sample-header section is invalid.");

        var nameStart = checked(HeaderSize + sampleHeadersSize);
        var dataStart = checked(nameStart + nameTableSize);
        RequireRange(bytes.Length, HeaderSize, sampleHeadersSize, "sample headers");
        RequireRange(bytes.Length, nameStart, nameTableSize, "name table");
        RequireRange(bytes.Length, dataStart, dataSize, "sample data");

        var parsed = new List<MutableSample>(sampleCount);
        var cursor = HeaderSize;
        for (var index = 0; index < sampleCount; index++)
        {
            if (cursor + 8 > nameStart)
                throw new InvalidDataException($"Sample {index} header extends outside the header section.");
            var value = ReadU64(bytes, cursor);
            cursor += 8;
            var hasMetadata = (value & 1) != 0;
            var frequencyIndex = (int)((value >> 1) & 0xf);
            if ((uint)frequencyIndex >= Frequencies.Length)
                throw new InvalidDataException($"Sample {index} has unknown frequency index {frequencyIndex}.");

            var sample = new MutableSample
            {
                Index = index,
                Channels = (int)((value >> 5) & 1) + 1,
                SampleRate = Frequencies[frequencyIndex],
                DataOffset = checked((int)(((value >> 6) & 0x0fffffff) * 16)),
                SampleCount = checked((long)(value >> 34)),
            };

            while (hasMetadata)
            {
                if (cursor + 4 > nameStart)
                    throw new InvalidDataException($"Sample {index} metadata header is truncated.");
                var chunk = ReadU32(bytes, cursor);
                cursor += 4;
                hasMetadata = (chunk & 1) != 0;
                var chunkSize = checked((int)((chunk >> 1) & 0x00ffffff));
                var chunkType = checked((byte)(chunk >> 25));
                if (cursor + chunkSize > nameStart)
                    throw new InvalidDataException($"Sample {index} metadata chunk {chunkType} is truncated.");
                var payload = bytes.Slice(cursor, chunkSize).ToArray();
                cursor += chunkSize;
                sample.Metadata.Add(new Fsb5Metadata(chunkType, payload));
                ApplyMetadata(sample, chunkType, payload);
            }

            parsed.Add(sample);
        }

        var names = ReadNames(bytes.Slice(nameStart, nameTableSize), sampleCount);
        var samples = new List<Fsb5Sample>(sampleCount);
        for (var i = 0; i < parsed.Count; i++)
        {
            var current = parsed[i];
            if (current.DataOffset < 0 || current.DataOffset > dataSize)
                throw new InvalidDataException($"Sample {i} data offset is outside the declared data section.");
            var end = i + 1 < parsed.Count ? parsed[i + 1].DataOffset : dataSize;
            if (end < current.DataOffset || end > dataSize)
                throw new InvalidDataException($"Sample {i} has an invalid data extent.");
            samples.Add(new Fsb5Sample(i, names[i], current.Channels, current.SampleRate,
                current.SampleCount, current.DataOffset, end - current.DataOffset,
                current.VorbisSetupCrc, current.LoopStart, current.LoopEnd,
                new ReadOnlyCollection<Fsb5Metadata>(current.Metadata)));
        }

        return new Fsb5Decoder(bytes.ToArray(), codec, dataStart, dataSize, samples);
    }

    public ReadOnlyMemory<byte> GetEncodedData(int sampleIndex)
    {
        var sample = GetSample(sampleIndex);
        return _file.AsMemory(_dataStart + sample.DataOffset, sample.DataLength);
    }

    public Pcm16Audio DecodePcm16(int sampleIndex)
    {
        var sample = GetSample(sampleIndex);
        var source = GetEncodedData(sampleIndex).Span;
        var bytesPerInputSample = Codec switch
        {
            Fsb5Codec.Pcm8 => 1,
            Fsb5Codec.Pcm16 => 2,
            Fsb5Codec.Pcm24 => 3,
            Fsb5Codec.Pcm32 or Fsb5Codec.PcmFloat => 4,
            _ => throw new NotSupportedException($"{Codec} cannot be decoded as PCM by this decoder."),
        };
        var inputLength = checked((int)(sample.SampleCount * sample.Channels * bytesPerInputSample));
        if (inputLength > source.Length)
            throw new InvalidDataException($"Sample {sampleIndex} PCM payload is truncated.");
        source = source[..inputLength];
        var output = new byte[checked((int)(sample.SampleCount * sample.Channels * 2))];

        for (int input = 0, result = 0; input < source.Length; input += bytesPerInputSample, result += 2)
        {
            short value = Codec switch
            {
                Fsb5Codec.Pcm8 => (short)(unchecked((sbyte)source[input]) << 8),
                Fsb5Codec.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(source[input..]),
                Fsb5Codec.Pcm24 => (short)(ReadInt24(source[input..]) >> 8),
                Fsb5Codec.Pcm32 => (short)(BinaryPrimitives.ReadInt32LittleEndian(source[input..]) >> 16),
                Fsb5Codec.PcmFloat => FloatToInt16(BinaryPrimitives.ReadSingleLittleEndian(source[input..])),
                _ => 0,
            };
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(result), value);
        }
        return new Pcm16Audio(sample.Channels, sample.SampleRate, sample.SampleCount, output);
    }

    public byte[] RebuildOggVorbis(int sampleIndex, VorbisSetupHeaderRegistry setupHeaders)
    {
        if (Codec != Fsb5Codec.Vorbis)
            throw new NotSupportedException($"The bank codec is {Codec}, not Vorbis.");
        ArgumentNullException.ThrowIfNull(setupHeaders);
        var sample = GetSample(sampleIndex);
        if (sample.VorbisSetupCrc is not { } crc)
            throw new InvalidDataException($"Sample {sampleIndex} has no FSB Vorbis metadata chunk.");
        if (!setupHeaders.TryGet(crc, out var setupHeader))
            throw new KeyNotFoundException($"No Vorbis setup packet is registered for CRC 0x{crc:X8}.");
        return OggVorbisRebuilder.Build(sample, GetEncodedData(sampleIndex).Span, setupHeader!);
    }

    /// <summary>
    /// Returns the complete MPEG audio frames for one codec-11 sample. FMOD uses codec 11 for
    /// both Layer II and Layer III; the returned elementary stream retains any ID3v2/ID3v1,
    /// Xing, VBRI, LAME, encoder-delay, and padding data already present and removes only FSB
    /// alignment zeroes after the final complete frame. The historical API name reflects the
    /// extension used by the Godot adapter, while <see cref="GetMpegLayer"/> reports the actual layer.
    /// </summary>
    public byte[] RebuildMp3(int sampleIndex)
    {
        if (Codec != Fsb5Codec.Mpeg)
            throw new NotSupportedException($"The bank codec is {Codec}, not MPEG.");

        var sample = GetSample(sampleIndex);
        var source = GetEncodedData(sampleIndex).Span;
        var cursor = ReadId3v2Length(source);
        MpegFrameHeader? streamHeader = null;
        var frames = 0;
        var decodedCapacity = 0L;
        var outputEnd = -1;
        var id3v1Offset = -1;
        while (cursor < source.Length)
        {
            if (source.Length - cursor >= 128 && source.Slice(cursor, 3).SequenceEqual("TAG"u8))
            {
                id3v1Offset = cursor;
                cursor += 128;
                break;
            }
            if (!TryReadMpegFrame(source[cursor..], out var header)) break;
            streamHeader ??= header;
            if (header.Version != streamHeader.Value.Version || header.Layer != streamHeader.Value.Layer ||
                header.SampleRate != streamHeader.Value.SampleRate || header.Channels != streamHeader.Value.Channels)
                throw new InvalidDataException($"Sample {sampleIndex} changes MPEG version, layer, sample rate, or channels.");
            if (header.SampleRate != sample.SampleRate || header.Channels != sample.Channels)
                throw new InvalidDataException(
                    $"Sample {sampleIndex} MPEG header is {header.Channels}ch/{header.SampleRate}Hz, " +
                    $"but its FSB header is {sample.Channels}ch/{sample.SampleRate}Hz.");
            if (header.FrameLength > source.Length - cursor)
                throw new InvalidDataException($"Sample {sampleIndex} ends in a truncated MPEG frame.");
            cursor += header.FrameLength;
            frames++;
            decodedCapacity = checked(decodedCapacity + header.SamplesPerFrame);
            outputEnd = cursor;
        }
        if (frames == 0 || outputEnd < 0)
            throw new InvalidDataException($"Sample {sampleIndex} contains no MPEG audio frames.");
        if (decodedCapacity < sample.SampleCount)
            throw new InvalidDataException(
                $"Sample {sampleIndex} MPEG frames cover {decodedCapacity} samples, " +
                $"less than the FSB declaration {sample.SampleCount}.");
        if (!IsZeroPadding(source[cursor..]))
            throw new InvalidDataException($"Sample {sampleIndex} has nonzero data after its final MPEG frame.");

        // Complete leading/trailing frames can carry the codec delay, bit reservoir, or encoder
        // padding needed to reproduce SampleCount; only the FSB container's zero alignment is removed.
        if (id3v1Offset < 0) return source[..outputEnd].ToArray();
        var output = new byte[outputEnd + 128];
        source[..outputEnd].CopyTo(output);
        source.Slice(id3v1Offset, 128).CopyTo(output.AsSpan(outputEnd));
        return output;
    }

    /// <summary>Returns 2 for MPEG Layer II or 3 for MPEG Layer III.</summary>
    public int GetMpegLayer(int sampleIndex)
    {
        if (Codec != Fsb5Codec.Mpeg)
            throw new NotSupportedException($"The bank codec is {Codec}, not MPEG.");
        var source = GetEncodedData(sampleIndex).Span;
        var cursor = ReadId3v2Length(source);
        if (!TryReadMpegFrame(source[cursor..], out var header))
            throw new InvalidDataException($"Sample {sampleIndex} contains no MPEG audio frame header.");
        return header.Layer;
    }

    public int FindSample(string name)
    {
        for (var i = 0; i < Samples.Count; i++)
            if (string.Equals(Samples[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private Fsb5Sample GetSample(int index) =>
        (uint)index < Samples.Count ? Samples[index] : throw new ArgumentOutOfRangeException(nameof(index));

    private static void ApplyMetadata(MutableSample sample, byte type, byte[] data)
    {
        switch (type)
        {
            case 1 when data.Length >= 1:
                sample.Channels = data[0];
                break;
            case 2 when data.Length >= 4:
                sample.SampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
                break;
            case 3 when data.Length >= 8:
                sample.LoopStart = BinaryPrimitives.ReadUInt32LittleEndian(data);
                sample.LoopEnd = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                break;
            case 11 when data.Length >= 4:
                sample.VorbisSetupCrc = BinaryPrimitives.ReadUInt32LittleEndian(data);
                break;
        }
    }

    private static string[] ReadNames(ReadOnlySpan<byte> table, int count)
    {
        var names = new string[count];
        var offsetsSize = checked(count * 4);
        if (table.Length < offsetsSize)
            throw new InvalidDataException("The FSB5 name table is truncated.");
        for (var i = 0; i < count; i++)
        {
            var relative = checked((int)ReadU32(table, i * 4));
            // Stored offsets are relative to the start of the complete name table.
            var start = relative;
            if (start < offsetsSize || start >= table.Length)
                throw new InvalidDataException($"Sample {i} has an invalid name offset.");
            var tail = table[start..];
            var length = tail.IndexOf((byte)0);
            if (length < 0)
                throw new InvalidDataException($"Sample {i} name is not terminated.");
            names[i] = System.Text.Encoding.UTF8.GetString(tail[..length]);
        }
        return names;
    }

    private static int ReadInt24(ReadOnlySpan<byte> value)
    {
        var result = value[0] | (value[1] << 8) | (value[2] << 16);
        return (result & 0x800000) != 0 ? result | unchecked((int)0xff000000) : result;
    }

    private static short FloatToInt16(float value)
    {
        if (float.IsNaN(value)) return 0;
        var finite = Math.Clamp(value, -1f, 1f);
        return (short)MathF.Round(finite < 0 ? finite * 32768f : finite * 32767f);
    }

    private static int ReadId3v2Length(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || !data[..3].SequenceEqual("ID3"u8)) return 0;
        if (data.Length < 10 || (data[6] | data[7] | data[8] | data[9]) >= 0x80)
            throw new InvalidDataException("The MPEG ID3v2 header is truncated or has an invalid synchsafe size.");
        var payloadLength = (data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9];
        var totalLength = checked(10 + payloadLength + ((data[5] & 0x10) != 0 ? 10 : 0));
        if (totalLength > data.Length)
            throw new InvalidDataException("The MPEG ID3v2 tag extends outside the sample data.");
        return totalLength;
    }

    private static bool TryReadMpegFrame(ReadOnlySpan<byte> data, out MpegFrameHeader header)
    {
        header = default;
        if (data.Length < 4) return false;
        var bits = BinaryPrimitives.ReadUInt32BigEndian(data);
        if ((bits & 0xffe00000) != 0xffe00000) return false;
        var versionBits = (int)((bits >> 19) & 3);
        var layerBits = (int)((bits >> 17) & 3);
        var bitrateIndex = (int)((bits >> 12) & 15);
        var sampleRateIndex = (int)((bits >> 10) & 3);
        if (versionBits == 1 || layerBits is 0 or 3 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
            return false;

        var layer = 4 - layerBits;
        var version = versionBits switch { 3 => 1, 2 => 2, _ => 25 };
        var sampleRate = MpegSampleRates[sampleRateIndex] / (version == 1 ? 1 : version == 2 ? 2 : 4);
        var bitrate = GetMpegBitrate(version, layer, bitrateIndex) * 1000;
        var padding = (int)((bits >> 9) & 1);
        var frameLength = layer == 2 || version == 1
            ? checked(144 * bitrate / sampleRate + padding)
            : checked(72 * bitrate / sampleRate + padding);
        var channels = ((bits >> 6) & 3) == 3 ? 1 : 2;
        var samplesPerFrame = layer == 2 || version == 1 ? 1152 : 576;
        header = new MpegFrameHeader(version, layer, sampleRate, channels, frameLength, samplesPerFrame);
        return true;
    }

    private static int GetMpegBitrate(int version, int layer, int index)
    {
        ReadOnlySpan<ushort> table = version == 1
            ? layer == 2
                ? [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384]
                : [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
            : [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        return table[index];
    }

    private static bool IsZeroPadding(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
            if (value != 0) return false;
        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static ulong ReadU64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));

    private static void RequireRange(int total, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || offset > total - length)
            throw new InvalidDataException($"The declared FSB5 {label} section is outside the file.");
    }

    private sealed class MutableSample
    {
        public int Index;
        public int Channels;
        public int SampleRate;
        public int DataOffset;
        public long SampleCount;
        public uint? VorbisSetupCrc;
        public long? LoopStart;
        public long? LoopEnd;
        public List<Fsb5Metadata> Metadata { get; } = [];
    }

    private readonly record struct MpegFrameHeader(
        int Version, int Layer, int SampleRate, int Channels, int FrameLength, int SamplesPerFrame);
}
