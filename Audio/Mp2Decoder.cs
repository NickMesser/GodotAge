using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace AAEmu.GodotViewer;

/// <summary>
/// Decodes MPEG-1 and MPEG-2 low-sampling-frequency Layer II audio.
/// The decoder has no Godot or native-code dependencies. An instance retains
/// the synthesis filter history between frames and allocates no memory while
/// <see cref="DecodeFrame"/> is running.
/// </summary>
public sealed class Mp2Decoder
{
    public const int SamplesPerFrame = 1152;

    private static readonly int[] Mpeg1Layer2Bitrates =
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 };

    private static readonly int[] Mpeg2Layer2Bitrates =
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

    private static readonly int[] SampleRates = { 44100, 48000, 32000 };
    private static readonly int[] SubbandLimits = { 27, 30, 8, 12, 30 };
    private static readonly int[] QuantizerSteps =
        { 3, 5, 7, 9, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535 };
    private static readonly int[] QuantizerBits =
        { -5, -7, 3, -10, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

    // Each subband entry starts with nbal, followed by 2^nbal quantizer-class
    // values. Tables 0/1 and 2/3 share their allocation mapping and differ only
    // in sblimit. These are the compact form of ISO/IEC 11172-3 table 3-B.2.
    private static readonly byte[] AllocationTableA =
    {
        4,0,2,4,5,6,7,8,9,10,11,12,13,14,15,16,
        4,0,2,4,5,6,7,8,9,10,11,12,13,14,15,16,
        4,0,2,4,5,6,7,8,9,10,11,12,13,14,15,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,16,
        3,0,1,2,3,4,5,16, 3,0,1,2,3,4,5,16,
        3,0,1,2,3,4,5,16, 3,0,1,2,3,4,5,16,
        3,0,1,2,3,4,5,16, 3,0,1,2,3,4,5,16,
        3,0,1,2,3,4,5,16, 3,0,1,2,3,4,5,16,
        3,0,1,2,3,4,5,16, 3,0,1,2,3,4,5,16,
        3,0,1,2,3,4,5,16, 3,0,1,2,3,4,5,16,
        2,0,1,16, 2,0,1,16, 2,0,1,16, 2,0,1,16,
        2,0,1,16, 2,0,1,16, 2,0,1,16
    };

    private static readonly byte[] AllocationTableB =
    {
        4,0,1,3,4,5,6,7,8,9,10,11,12,13,14,15,
        4,0,1,3,4,5,6,7,8,9,10,11,12,13,14,15,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7
    };

    // ISO/IEC 13818-3 LSF allocation table (also table 3-B.2 variant 4 in
    // commonly used decoder nomenclature).
    private static readonly byte[] AllocationTableLsf =
    {
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,
        4,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7,
        3,0,1,3,4,5,6,7, 3,0,1,3,4,5,6,7,
        3,0,1,3,4,5,6,7,
        2,0,1,3, 2,0,1,3, 2,0,1,3, 2,0,1,3,
        2,0,1,3, 2,0,1,3, 2,0,1,3, 2,0,1,3,
        2,0,1,3, 2,0,1,3, 2,0,1,3, 2,0,1,3,
        2,0,1,3, 2,0,1,3, 2,0,1,3, 2,0,1,3,
        2,0,1,3, 2,0,1,3, 2,0,1,3
    };

    private static readonly float[] ScaleFactors = BuildScaleFactors();
    private static readonly float[] SynthesisMatrix = BuildSynthesisMatrix();
    private static readonly float[] SynthesisWindow = BuildSynthesisWindow();

    private readonly byte[] _allocation = new byte[2 * 32];
    private readonly byte[] _scaleFactorSelection = new byte[2 * 32];
    private readonly byte[] _scaleFactor = new byte[2 * 32 * 3];
    private readonly float[] _triplet = new float[2 * 3 * 32];
    private readonly float[] _synthesisHistory = new float[2 * 1024];
    private readonly int[] _synthesisPosition = new int[2];
    private int _sampleRate;
    private int _channels;

    /// <summary>Clears frame-format and synthesis-filter state.</summary>
    public void Reset()
    {
        Array.Clear(_allocation, 0, _allocation.Length);
        Array.Clear(_scaleFactorSelection, 0, _scaleFactorSelection.Length);
        Array.Clear(_scaleFactor, 0, _scaleFactor.Length);
        Array.Clear(_triplet, 0, _triplet.Length);
        Array.Clear(_synthesisHistory, 0, _synthesisHistory.Length);
        Array.Clear(_synthesisPosition, 0, _synthesisPosition.Length);
        _sampleRate = 0;
        _channels = 0;
    }

    /// <summary>
    /// Decodes one complete MPEG Layer II frame into interleaved signed PCM16.
    /// Exactly 1152 samples per channel are written. The returned value is the
    /// number of interleaved <see cref="short"/> values written.
    /// </summary>
    /// <exception cref="InvalidDataException">The header or Layer II data is invalid.</exception>
    /// <exception cref="ArgumentException">The source frame or destination is too short.</exception>
    public int DecodeFrame(
        ReadOnlySpan<byte> frame,
        Span<short> destination,
        out int bytesConsumed,
        out Mp2FrameInfo frameInfo)
    {
        if (!TryReadFrameHeader(frame, out frameInfo))
            throw new InvalidDataException("The input does not begin with a supported MPEG Layer II frame.");
        if (frame.Length < frameInfo.FrameLength)
            throw new ArgumentException("The MPEG frame is truncated.", nameof(frame));

        int outputSamples = checked(SamplesPerFrame * frameInfo.Channels);
        if (destination.Length < outputSamples)
            throw new ArgumentException($"The PCM destination needs at least {outputSamples} samples.", nameof(destination));

        if (_sampleRate != 0 && (_sampleRate != frameInfo.SampleRate || _channels != frameInfo.Channels))
            Reset();
        _sampleRate = frameInfo.SampleRate;
        _channels = frameInfo.Channels;

        int payloadOffset = frameInfo.HasCrc ? 6 : 4; // CRC is protected data; decoding only needs to skip it.
        var bits = new BitReader(frame.Slice(payloadOffset, frameInfo.FrameLength - payloadOffset));
        DecodeLayer2(ref bits, frameInfo, destination);
        bytesConsumed = frameInfo.FrameLength;
        return outputSamples;
    }

    /// <summary>
    /// Decodes every complete Layer II frame in a byte stream. Leading ID3 data
    /// or other bytes are skipped while locating the first frame.
    /// </summary>
    public static Mp2DecodedAudio Decode(ReadOnlySpan<byte> stream)
    {
        if (!TryGetStreamInfo(stream, out Mp2StreamInfo streamInfo))
            throw new InvalidDataException("No complete MPEG Layer II frame was found.");

        int byteCount = checked(streamInfo.SamplesPerChannel * streamInfo.Channels * sizeof(short));
        byte[] pcm = new byte[byteCount];
        Span<short> output = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        var decoder = new Mp2Decoder();
        int offset = 0;
        int outputOffset = 0;
        int frames = 0;

        while (TryFindNextFrame(stream, offset, streamInfo.SampleRate, streamInfo.Channels,
                   out int frameOffset, out Mp2FrameInfo header))
        {
            int written = decoder.DecodeFrame(stream.Slice(frameOffset, header.FrameLength),
                output.Slice(outputOffset), out _, out _);
            outputOffset += written;
            frames++;
            offset = frameOffset + header.FrameLength;
        }

        // PCM byte streams are little endian. Godot targets are little endian,
        // but retain correct wire representation on a big-endian CLR as well.
        if (!BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < pcm.Length; i += 2)
                (pcm[i], pcm[i + 1]) = (pcm[i + 1], pcm[i]);
        }

        return new Mp2DecodedAudio(pcm, streamInfo.SampleRate, streamInfo.Channels,
            frames, frames * SamplesPerFrame);
    }

    /// <summary>Reads and validates the MPEG header at the start of <paramref name="data"/>.</summary>
    public static bool TryReadFrameHeader(ReadOnlySpan<byte> data, out Mp2FrameInfo info)
    {
        info = default;
        if (data.Length < 4)
            return false;

        uint header = BinaryPrimitives.ReadUInt32BigEndian(data);
        if ((header & 0xffe00000u) != 0xffe00000u)
            return false;

        int versionBits = (int)((header >> 19) & 3);
        if (versionBits != 3 && versionBits != 2) // MPEG-2.5 and the reserved version are outside Layer II.
            return false;
        if (((header >> 17) & 3) != 2) // 10b is Layer II.
            return false;

        int bitrateIndex = (int)((header >> 12) & 15);
        int sampleRateIndex = (int)((header >> 10) & 3);
        if (bitrateIndex is 0 or 15 || sampleRateIndex == 3)
            return false; // free-format has no self-contained frame length.

        bool mpeg1 = versionBits == 3;
        int bitrateKbps = (mpeg1 ? Mpeg1Layer2Bitrates : Mpeg2Layer2Bitrates)[bitrateIndex];
        int sampleRate = SampleRates[sampleRateIndex] >> (mpeg1 ? 0 : 1);
        bool padding = ((header >> 9) & 1) != 0;
        int mode = (int)((header >> 6) & 3);
        int channels = mode == 3 ? 1 : 2;
        int frameLength = (144000 * bitrateKbps) / sampleRate + (padding ? 1 : 0);
        if (frameLength <= 4)
            return false;

        info = new Mp2FrameInfo(
            sampleRate,
            channels,
            bitrateKbps * 1000,
            frameLength,
            mpeg1 ? 1 : 2,
            (Mp2ChannelMode)mode,
            (int)((header >> 4) & 3),
            ((header >> 16) & 1) == 0,
            padding);
        return true;
    }

    /// <summary>Scans a stream and returns its decoded size and format.</summary>
    public static bool TryGetStreamInfo(ReadOnlySpan<byte> stream, out Mp2StreamInfo info)
    {
        info = default;
        int offset = 0;
        int frames = 0;
        int sampleRate = 0;
        int channels = 0;
        int firstOffset = -1;

        while (TryFindNextFrame(stream, offset, sampleRate, channels,
                   out int frameOffset, out Mp2FrameInfo header))
        {
            if (frames == 0)
            {
                sampleRate = header.SampleRate;
                channels = header.Channels;
                firstOffset = frameOffset;
            }
            frames++;
            offset = frameOffset + header.FrameLength;
        }

        if (frames == 0)
            return false;
        info = new Mp2StreamInfo(sampleRate, channels, frames,
            checked(frames * SamplesPerFrame), firstOffset);
        return true;
    }

    private static bool TryFindNextFrame(
        ReadOnlySpan<byte> stream,
        int start,
        int requiredSampleRate,
        int requiredChannels,
        out int offset,
        out Mp2FrameInfo info)
    {
        int last = stream.Length - 4;
        for (int i = Math.Max(start, 0); i <= last; i++)
        {
            if (stream[i] != 0xff || (stream[i + 1] & 0xe0) != 0xe0)
                continue;
            if (!TryReadFrameHeader(stream.Slice(i), out info))
                continue;
            if (i + info.FrameLength > stream.Length)
                continue;
            if (requiredSampleRate != 0 &&
                (info.SampleRate != requiredSampleRate || info.Channels != requiredChannels))
                continue;
            offset = i;
            return true;
        }

        offset = 0;
        info = default;
        return false;
    }

    private void DecodeLayer2(ref BitReader bits, Mp2FrameInfo header, Span<short> output)
    {
        int tableNumber = SelectAllocationTable(header);
        int subbandLimit = SubbandLimits[tableNumber];
        byte[] table = tableNumber <= 1 ? AllocationTableA :
            tableNumber <= 3 ? AllocationTableB : AllocationTableLsf;
        int bound = header.ChannelMode == Mp2ChannelMode.JointStereo
            ? Math.Min((header.ModeExtension + 1) * 4, subbandLimit)
            : subbandLimit;

        Span<int> tableOffsets = stackalloc int[32];
        Span<byte> allocationBits = stackalloc byte[32];
        int tableOffset = 0;
        for (int sb = 0; sb < subbandLimit; sb++)
        {
            tableOffsets[sb] = tableOffset;
            byte nbal = table[tableOffset];
            allocationBits[sb] = nbal;
            tableOffset += 1 << nbal;
        }

        Array.Clear(_allocation, 0, _allocation.Length);
        for (int sb = 0; sb < bound; sb++)
        {
            int nbal = allocationBits[sb];
            for (int ch = 0; ch < header.Channels; ch++)
                _allocation[ch * 32 + sb] = (byte)bits.Read(nbal);
        }
        for (int sb = bound; sb < subbandLimit; sb++)
        {
            byte allocation = (byte)bits.Read(allocationBits[sb]);
            _allocation[sb] = allocation;
            _allocation[32 + sb] = allocation;
        }

        for (int sb = 0; sb < subbandLimit; sb++)
        {
            for (int ch = 0; ch < header.Channels; ch++)
            {
                int index = ch * 32 + sb;
                if (_allocation[index] != 0)
                    _scaleFactorSelection[index] = (byte)bits.Read(2);
            }
        }

        for (int sb = 0; sb < subbandLimit; sb++)
        {
            for (int ch = 0; ch < header.Channels; ch++)
            {
                int index = ch * 32 + sb;
                if (_allocation[index] == 0)
                    continue;
                int sf = index * 3;
                switch (_scaleFactorSelection[index])
                {
                    case 0:
                        _scaleFactor[sf] = (byte)bits.Read(6);
                        _scaleFactor[sf + 1] = (byte)bits.Read(6);
                        _scaleFactor[sf + 2] = (byte)bits.Read(6);
                        break;
                    case 1:
                        _scaleFactor[sf] = (byte)bits.Read(6);
                        _scaleFactor[sf + 1] = _scaleFactor[sf];
                        _scaleFactor[sf + 2] = (byte)bits.Read(6);
                        break;
                    case 2:
                        _scaleFactor[sf] = (byte)bits.Read(6);
                        _scaleFactor[sf + 1] = _scaleFactor[sf];
                        _scaleFactor[sf + 2] = _scaleFactor[sf];
                        break;
                    default:
                        _scaleFactor[sf] = (byte)bits.Read(6);
                        _scaleFactor[sf + 2] = (byte)bits.Read(6);
                        _scaleFactor[sf + 1] = _scaleFactor[sf + 2];
                        break;
                }
            }
        }

        int pcmOffset = 0;
        for (int scaleBlock = 0; scaleBlock < 3; scaleBlock++)
        {
            for (int group = 0; group < 4; group++)
            {
                for (int sb = 0; sb < bound; sb++)
                {
                    int entry = tableOffsets[sb];
                    for (int ch = 0; ch < header.Channels; ch++)
                    {
                        int allocation = _allocation[ch * 32 + sb];
                        if (allocation == 0)
                        {
                            ClearTriplet(ch, sb);
                            continue;
                        }
                        int quantizer = table[entry + allocation];
                        ReadTriplet(ref bits, ch, sb, quantizer,
                            _scaleFactor[(ch * 32 + sb) * 3 + scaleBlock]);
                    }
                }

                for (int sb = bound; sb < subbandLimit; sb++)
                {
                    int allocation = _allocation[sb];
                    if (allocation == 0)
                    {
                        ClearTriplet(0, sb);
                        ClearTriplet(1, sb);
                        continue;
                    }

                    int quantizer = table[tableOffsets[sb] + allocation];
                    ReadSharedTriplet(ref bits, sb, quantizer,
                        _scaleFactor[sb * 3 + scaleBlock],
                        _scaleFactor[(32 + sb) * 3 + scaleBlock]);
                }

                for (int sample = 0; sample < 3; sample++)
                {
                    for (int ch = 0; ch < header.Channels; ch++)
                        Synthesize(ch, sample, subbandLimit, header.Channels, output, pcmOffset);
                    pcmOffset += 32 * header.Channels;
                }
            }
        }
    }

    private void ReadTriplet(ref BitReader bits, int channel, int subband, int quantizer, int scaleFactor)
    {
        int codedBits = QuantizerBits[quantizer];
        if (codedBits < 0)
        {
            int combined = bits.Read(-codedBits);
            int steps = QuantizerSteps[quantizer];
            for (int sample = 0; sample < 3; sample++)
            {
                int code = combined % steps;
                combined /= steps;
                _triplet[(channel * 3 + sample) * 32 + subband] =
                    Requantize(code, quantizer, scaleFactor);
            }
        }
        else
        {
            for (int sample = 0; sample < 3; sample++)
            {
                int code = bits.Read(codedBits);
                _triplet[(channel * 3 + sample) * 32 + subband] =
                    Requantize(code, quantizer, scaleFactor);
            }
        }
    }

    private void ReadSharedTriplet(
        ref BitReader bits,
        int subband,
        int quantizer,
        int leftScaleFactor,
        int rightScaleFactor)
    {
        int codedBits = QuantizerBits[quantizer];
        if (codedBits < 0)
        {
            int combined = bits.Read(-codedBits);
            int steps = QuantizerSteps[quantizer];
            for (int sample = 0; sample < 3; sample++)
            {
                int code = combined % steps;
                combined /= steps;
                _triplet[sample * 32 + subband] = Requantize(code, quantizer, leftScaleFactor);
                _triplet[(3 + sample) * 32 + subband] = Requantize(code, quantizer, rightScaleFactor);
            }
        }
        else
        {
            for (int sample = 0; sample < 3; sample++)
            {
                int code = bits.Read(codedBits);
                _triplet[sample * 32 + subband] = Requantize(code, quantizer, leftScaleFactor);
                _triplet[(3 + sample) * 32 + subband] = Requantize(code, quantizer, rightScaleFactor);
            }
        }
    }

    private void ClearTriplet(int channel, int subband)
    {
        _triplet[channel * 96 + subband] = 0;
        _triplet[channel * 96 + 32 + subband] = 0;
        _triplet[channel * 96 + 64 + subband] = 0;
    }

    private static float Requantize(int code, int quantizer, int scaleFactor)
    {
        int codedBits = QuantizerBits[quantizer];
        int sampleBits = codedBits < 0 ? -codedBits switch { 5 => 2, 7 => 3, _ => 4 } : codedBits;
        int half = 1 << (sampleBits - 1);
        float c = (1 << sampleBits) / (float)QuantizerSteps[quantizer];
        float d = codedBits < 0 ? 0.5f : 1f / half;
        float normalized = (code / (float)half - 1f + d) * c;
        return normalized * ScaleFactors[scaleFactor];
    }

    private void Synthesize(
        int channel,
        int tripletSample,
        int subbandLimit,
        int channels,
        Span<short> output,
        int outputOffset)
    {
        int position = (_synthesisPosition[channel] - 64) & 1023;
        _synthesisPosition[channel] = position;
        int historyBase = channel * 1024;
        int sampleBase = (channel * 3 + tripletSample) * 32;

        for (int i = 0; i < 64; i++)
        {
            float sum = 0;
            int matrix = i * 32;
            for (int sb = 0; sb < subbandLimit; sb++)
                sum += _triplet[sampleBase + sb] * SynthesisMatrix[matrix + sb];
            _synthesisHistory[historyBase + ((position + i) & 1023)] = sum;
        }

        for (int j = 0; j < 32; j++)
        {
            float sum = 0;
            for (int i = 0; i < 8; i++)
            {
                int v = position + i * 128 + j;
                int d = i * 64 + j;
                sum += _synthesisHistory[historyBase + (v & 1023)] * SynthesisWindow[d];
                sum += _synthesisHistory[historyBase + ((v + 96) & 1023)] * SynthesisWindow[d + 32];
            }

            float scaled = sum * 32768f;
            int pcm = scaled >= 0 ? (int)(scaled + 0.5f) : (int)(scaled - 0.5f);
            if (pcm > short.MaxValue) pcm = short.MaxValue;
            else if (pcm < short.MinValue) pcm = short.MinValue;
            output[outputOffset + j * channels + channel] = (short)pcm;
        }
    }

    private static int SelectAllocationTable(Mp2FrameInfo header)
    {
        if (header.MpegVersion == 2)
            return 4;
        int channelBitrate = header.Bitrate / 1000 / header.Channels;
        if ((header.SampleRate == 48000 && channelBitrate >= 56) ||
            channelBitrate is >= 56 and <= 80)
            return 0;
        if (header.SampleRate != 48000 && channelBitrate >= 96)
            return 1;
        if (header.SampleRate != 32000 && channelBitrate <= 48)
            return 2;
        return 3;
    }

    private static float[] BuildScaleFactors()
    {
        var factors = new float[64];
        for (int i = 0; i < 63; i++)
            factors[i] = (float)(2.0 * Math.Pow(2.0, -i / 3.0));
        factors[63] = 0;
        return factors;
    }

    private static float[] BuildSynthesisMatrix()
    {
        var matrix = new float[64 * 32];
        for (int i = 0; i < 64; i++)
        for (int k = 0; k < 32; k++)
            matrix[i * 32 + k] = (float)Math.Cos((16 + i) * (2 * k + 1) * Math.PI / 64.0);
        return matrix;
    }

    private static float[] BuildSynthesisWindow()
    {
        // ISO table 3-B.3 is exactly representable in units of 1/65536. Its
        // 257 stored values are traversed forward through coefficient 256 and
        // backward thereafter. The sign alternates independently for each
        // 64-coefficient block across the complete 512-entry window.
        int[] half =
        {
            0,-1,-1,-1,-1,-1,-1,-2,-2,-2,-2,-3,-3,-4,-4,-5,
            -5,-6,-7,-7,-8,-9,-10,-11,-13,-14,-16,-17,-19,-21,-24,-26,
            -29,-31,-35,-38,-41,-45,-49,-53,-58,-63,-68,-73,-79,-85,-91,-97,
            -104,-111,-117,-125,-132,-139,-147,-154,-161,-169,-176,-183,-190,-196,-202,-208,
            -213,-218,-222,-225,-227,-228,-228,-227,-224,-221,-215,-208,-200,-189,-177,-163,
            -146,-127,-106,-83,-57,-29,2,36,72,111,153,197,244,294,347,401,
            459,519,581,645,711,779,848,919,991,1064,1137,1210,1283,1356,1428,1498,
            1567,1634,1698,1759,1817,1870,1919,1962,2001,2032,2057,2075,2085,2087,2080,2063,
            2037,2000,1952,1893,1822,1739,1644,1535,1414,1280,1131,970,794,605,402,185,
            -45,-288,-545,-814,-1095,-1388,-1692,-2006,-2330,-2663,-3004,-3351,-3705,-4063,-4425,-4788,
            -5153,-5517,-5879,-6237,-6589,-6935,-7271,-7597,-7910,-8209,-8491,-8755,-8998,-9219,-9416,-9585,
            -9727,-9838,-9916,-9959,-9966,-9935,-9863,-9750,-9592,-9389,-9139,-8840,-8492,-8092,-7640,-7134,
            -6574,-5959,-5288,-4561,-3776,-2935,-2037,-1082,-70,998,2122,3300,4533,5818,7154,8540,
            9975,11455,12980,14548,16155,17799,19478,21189,22929,24694,26482,28289,30112,31947,33791,35640,
            37489,39336,41176,43006,44821,46617,48390,50137,51853,53534,55178,56778,58333,59838,61289,62684,
            64019,65290,66494,67629,68692,69679,70590,71420,72169,72835,73415,73908,74313,74630,74856,74992,
            75038
        };

        var window = new float[512];
        for (int i = 0; i < window.Length; i++)
        {
            int magnitudeIndex = i <= 256 ? i : 512 - i;
            int signed = ((i >> 6) & 1) == 0 ? half[magnitudeIndex] : -half[magnitudeIndex];
            window[i] = signed / 65536f;
        }
        return window;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public int Read(int count)
        {
            if ((uint)count > 24 || _position + count > _data.Length * 8)
                throw new InvalidDataException("The MPEG Layer II frame ended inside its audio data.");
            int value = 0;
            while (count > 0)
            {
                int byteIndex = _position >> 3;
                int bitIndex = _position & 7;
                int take = Math.Min(count, 8 - bitIndex);
                int shift = 8 - bitIndex - take;
                value = (value << take) | ((_data[byteIndex] >> shift) & ((1 << take) - 1));
                _position += take;
                count -= take;
            }
            return value;
        }
    }
}

public enum Mp2ChannelMode
{
    Stereo = 0,
    JointStereo = 1,
    DualChannel = 2,
    Mono = 3
}

public readonly struct Mp2FrameInfo
{
    public int SampleRate { get; }
    public int Channels { get; }
    public int Bitrate { get; }
    public int FrameLength { get; }
    public int MpegVersion { get; }
    public Mp2ChannelMode ChannelMode { get; }
    public int ModeExtension { get; }
    public bool HasCrc { get; }
    public bool HasPadding { get; }

    internal Mp2FrameInfo(int sampleRate, int channels, int bitrate, int frameLength,
        int mpegVersion, Mp2ChannelMode channelMode, int modeExtension, bool hasCrc, bool hasPadding)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Bitrate = bitrate;
        FrameLength = frameLength;
        MpegVersion = mpegVersion;
        ChannelMode = channelMode;
        ModeExtension = modeExtension;
        HasCrc = hasCrc;
        HasPadding = hasPadding;
    }
}

public readonly struct Mp2StreamInfo
{
    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames { get; }
    public int SamplesPerChannel { get; }
    public int FirstFrameOffset { get; }

    internal Mp2StreamInfo(int sampleRate, int channels, int frames, int samplesPerChannel, int firstFrameOffset)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Frames = frames;
        SamplesPerChannel = samplesPerChannel;
        FirstFrameOffset = firstFrameOffset;
    }
}

public sealed class Mp2DecodedAudio
{
    public byte[] Pcm16 { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int FramesDecoded { get; }
    public int SamplesPerChannel { get; }

    internal Mp2DecodedAudio(byte[] pcm16, int sampleRate, int channels, int framesDecoded, int samplesPerChannel)
    {
        Pcm16 = pcm16;
        SampleRate = sampleRate;
        Channels = channels;
        FramesDecoded = framesDecoded;
        SamplesPerChannel = samplesPerChannel;
    }
}
