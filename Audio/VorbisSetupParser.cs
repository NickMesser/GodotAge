namespace AAEmu.GodotViewer.Audio;

internal sealed record VorbisModeInfo(bool[] LongBlockModes)
{
    public int ModeBits => ILog(LongBlockModes.Length - 1);

    private static int ILog(int value)
    {
        var bits = 0;
        while (value > 0) { bits++; value >>= 1; }
        return bits;
    }
}

/// <summary>Minimal Vorbis setup parser: skips codebook/floor/residue bodies and retains mode block flags.</summary>
internal static class VorbisSetupParser
{
    public static VorbisModeInfo Parse(ReadOnlySpan<byte> packet, int channels)
    {
        if (packet.Length < 8 || packet[0] != 5 || !packet.Slice(1, 6).SequenceEqual("vorbis"u8))
            throw new InvalidDataException("Invalid Vorbis setup packet signature.");
        if (channels < 1) throw new InvalidDataException("Vorbis channel count must be positive.");
        var bits = new VorbisBits(packet[7..]);

        var codebooks = checked((int)bits.Read(8) + 1);
        for (var i = 0; i < codebooks; i++) SkipCodebook(ref bits);

        var times = checked((int)bits.Read(6) + 1);
        for (var i = 0; i < times; i++)
            if (bits.Read(16) != 0) throw new InvalidDataException("Unsupported Vorbis time transform.");

        var floors = checked((int)bits.Read(6) + 1);
        for (var i = 0; i < floors; i++) SkipFloor(ref bits);

        var residues = checked((int)bits.Read(6) + 1);
        for (var i = 0; i < residues; i++) SkipResidue(ref bits);

        var mappings = checked((int)bits.Read(6) + 1);
        for (var i = 0; i < mappings; i++) SkipMapping(ref bits, channels);

        var modeCount = checked((int)bits.Read(6) + 1);
        var modes = new bool[modeCount];
        for (var i = 0; i < modeCount; i++)
        {
            modes[i] = bits.Read(1) != 0;
            if (bits.Read(16) != 0 || bits.Read(16) != 0)
                throw new InvalidDataException("Unsupported Vorbis mode window or transform type.");
            bits.Read(8); // mapping
        }
        if (bits.Read(1) != 1) throw new InvalidDataException("Vorbis setup framing bit is absent.");
        return new VorbisModeInfo(modes);
    }

    public static long[] CalculateGranules(IReadOnlyList<byte[]> packets, VorbisModeInfo modes,
        byte shortExponent, byte longExponent, long finalFrameCount)
    {
        var result = new long[packets.Count];
        long position = 0;
        var previousBlock = 0;
        var finalContribution = 0;
        for (var i = 0; i < packets.Count; i++)
        {
            var packetBits = new VorbisBits(packets[i]);
            if (packetBits.Read(1) != 0)
                throw new InvalidDataException("An FSB Vorbis audio payload contains a non-audio packet.");
            var mode = modes.ModeBits == 0 ? 0 : checked((int)packetBits.Read(modes.ModeBits));
            if ((uint)mode >= modes.LongBlockModes.Length)
                throw new InvalidDataException("A Vorbis audio packet uses an out-of-range mode.");
            var block = 1 << (modes.LongBlockModes[mode] ? longExponent : shortExponent);
            finalContribution = previousBlock == 0 ? 0 : (previousBlock + block) / 4;
            position += finalContribution;
            result[i] = position;
            previousBlock = block;
        }
        if (result.Length > 0)
        {
            if (finalFrameCount > position || finalFrameCount < position - finalContribution)
                throw new InvalidDataException(
                    "FSB frame count is inconsistent with the registered Vorbis block sizes and packet modes.");
            result[^1] = finalFrameCount;
        }
        return result;
    }

    private static void SkipCodebook(ref VorbisBits bits)
    {
        if (bits.Read(24) != 0x564342) throw new InvalidDataException("Invalid Vorbis codebook sync.");
        var dimensions = checked((int)bits.Read(16));
        var entries = checked((int)bits.Read(24));
        if (bits.Read(1) != 0)
        {
            var codewordLength = checked((int)bits.Read(5) + 1);
            var current = 0;
            while (current < entries)
            {
                var number = checked((int)bits.Read(ILog(entries - current)));
                if (current > entries - number)
                    throw new InvalidDataException("Invalid ordered Vorbis codebook lengths.");
                current += number;
                codewordLength++;
                if (codewordLength > 32 && current < entries)
                    throw new InvalidDataException("Ordered Vorbis codebook length exceeds 32 bits.");
            }
        }
        else
        {
            var sparse = bits.Read(1) != 0;
            for (var j = 0; j < entries; j++)
                if (!sparse || bits.Read(1) != 0) bits.Read(5);
        }

        var lookupType = bits.Read(4);
        if (lookupType > 2) throw new InvalidDataException("Invalid Vorbis codebook lookup type.");
        if (lookupType == 0) return;
        bits.Read(32);
        bits.Read(32);
        var valueBits = checked((int)bits.Read(4) + 1);
        bits.Read(1);
        var lookupValues = lookupType == 1 ? Lookup1Values(entries, dimensions) : checked(entries * dimensions);
        bits.Skip(checked((long)lookupValues * valueBits));
    }

    private static void SkipFloor(ref VorbisBits bits)
    {
        var type = bits.Read(16);
        if (type == 0)
        {
            bits.Skip(8 + 16 + 16 + 6 + 8);
            var books = checked((int)bits.Read(4) + 1);
            bits.Skip(books * 8L);
            return;
        }
        if (type != 1) throw new InvalidDataException($"Unsupported Vorbis floor type {type}.");
        var partitions = checked((int)bits.Read(5));
        var classes = new int[partitions];
        var maximumClass = -1;
        for (var i = 0; i < partitions; i++) maximumClass = Math.Max(maximumClass, classes[i] = checked((int)bits.Read(4)));
        var dimensions = new int[maximumClass + 1];
        for (var i = 0; i <= maximumClass; i++)
        {
            dimensions[i] = checked((int)bits.Read(3) + 1);
            var subclasses = checked((int)bits.Read(2));
            if (subclasses > 0) bits.Read(8);
            bits.Skip((1 << subclasses) * 8L);
        }
        bits.Read(2);
        var rangeBits = checked((int)bits.Read(4));
        for (var i = 0; i < partitions; i++) bits.Skip((long)dimensions[classes[i]] * rangeBits);
    }

    private static void SkipResidue(ref VorbisBits bits)
    {
        if (bits.Read(16) > 2) throw new InvalidDataException("Invalid Vorbis residue type.");
        bits.Skip(24 + 24 + 24);
        var classifications = checked((int)bits.Read(6) + 1);
        bits.Read(8);
        var cascades = new int[classifications];
        for (var i = 0; i < classifications; i++)
        {
            var low = checked((int)bits.Read(3));
            var high = bits.Read(1) != 0 ? checked((int)bits.Read(5)) : 0;
            cascades[i] = (high << 3) | low;
        }
        for (var i = 0; i < classifications; i++)
            for (var bit = 0; bit < 8; bit++)
                if ((cascades[i] & (1 << bit)) != 0) bits.Read(8);
    }

    private static void SkipMapping(ref VorbisBits bits, int channels)
    {
        if (bits.Read(16) != 0) throw new InvalidDataException("Unsupported Vorbis mapping type.");
        var submaps = bits.Read(1) != 0 ? checked((int)bits.Read(4) + 1) : 1;
        if (bits.Read(1) != 0)
        {
            var steps = checked((int)bits.Read(8) + 1);
            var channelBits = ILog(channels - 1);
            for (var i = 0; i < steps; i++) { bits.Read(channelBits); bits.Read(channelBits); }
        }
        if (bits.Read(2) != 0) throw new InvalidDataException("Vorbis mapping reserved bits are nonzero.");
        if (submaps > 1)
            for (var i = 0; i < channels; i++) bits.Read(4);
        bits.Skip(submaps * 24L);
    }

    private static int Lookup1Values(int entries, int dimensions)
    {
        if (dimensions <= 0) throw new InvalidDataException("Vorbis codebook has zero dimensions.");
        var value = (int)Math.Floor(Math.Pow(entries, 1.0 / dimensions));
        while (PowLimited(value + 1, dimensions, entries) <= entries) value++;
        while (PowLimited(value, dimensions, entries) > entries) value--;
        return value;
    }

    private static long PowLimited(int value, int exponent, int limit)
    {
        long result = 1;
        for (var i = 0; i < exponent && result <= limit; i++) result *= value;
        return result;
    }

    private static int ILog(int value)
    {
        var bits = 0;
        while (value > 0) { bits++; value >>= 1; }
        return bits;
    }
}

internal ref struct VorbisBits
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bit;

    public VorbisBits(ReadOnlySpan<byte> data) { _data = data; _bit = 0; }

    public uint Read(int count)
    {
        if ((uint)count > 32) throw new ArgumentOutOfRangeException(nameof(count));
        if ((long)_bit + count > (long)_data.Length * 8) throw new InvalidDataException("Vorbis setup packet is truncated.");
        uint value = 0;
        for (var i = 0; i < count; i++, _bit++)
            value |= (uint)((_data[_bit >> 3] >> (_bit & 7)) & 1) << i;
        return value;
    }

    public void Skip(long count)
    {
        if (count < 0 || count > int.MaxValue || (long)_bit + count > (long)_data.Length * 8)
            throw new InvalidDataException("Vorbis setup packet is truncated.");
        _bit += (int)count;
    }
}
